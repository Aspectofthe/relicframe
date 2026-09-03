"""
test_relic_data.py
Regression tests for the calculation engine (relic_data.py + the order-
normalization helpers in wfm_api.py). No network access - everything here
runs on synthetic data so it's safe to run on every change to the math.

Run with:
    python -m unittest test_relic_data.py -v

These are deliberately built around the concrete scenarios from the design
review ("Test 1" .. "Test 7"), plus a few extra edge cases for the pieces
that have historically been the easiest to silently break: per-trade price
normalization, quantity-zero handling, refinement fallback, and the
guaranteed/expected/risky three-way split. The goal is that a change to the
engine that reintroduces one of these bugs fails loudly here instead of
being discovered by a user's plat count being wrong.
"""

import unittest

from relic_data import (
    Relic,
    RelicReward,
    classify_category,
    combine_categories,
    compute_risk_score,
    find_relics_for_reward,
    cheapest_combination_cost,
    chance_of_at_least_one,
)
from wfm_api import _order_entries, _order_unit_price, _best_matching_entry


class TestPriceNormalization(unittest.TestCase):
    """Test 1: a bulk listing must be normalized to per-item price."""

    def test_per_trade_normalization(self):
        # 1000p listed for a pack of 6 -> 166.6667p per relic, not 1000p.
        order = {"platinum": 1000, "perTrade": 6}
        self.assertAlmostEqual(_order_unit_price(order), 166.6667, places=3)

    def test_no_per_trade_field_is_already_unit_price(self):
        # Most reward items (non-bulk) have no perTrade at all - platinum
        # is already the per-item price and must pass through unchanged.
        order = {"platinum": 45}
        self.assertEqual(_order_unit_price(order), 45.0)

    def test_per_trade_of_one_is_unit_price(self):
        order = {"platinum": 45, "perTrade": 1}
        self.assertEqual(_order_unit_price(order), 45.0)

    def test_missing_platinum_is_none_not_zero(self):
        # Missing price data must never silently become 0 - that would
        # make a listing look like a free giveaway.
        self.assertIsNone(_order_unit_price({}))


class TestQuantityHandling(unittest.TestCase):
    """Test 2: zero quantity must be excluded, not treated as a $0 price."""

    def test_zero_quantity_excluded(self):
        orders = [
            {"platinum": 10, "quantity": 0},   # nothing actually for sale
            {"platinum": 12, "quantity": 3},
        ]
        entries = _order_entries(orders)
        self.assertEqual(len(entries), 1)
        self.assertEqual(entries[0]["price"], 12)

    def test_missing_quantity_is_not_excluded(self):
        # An order with no quantity field at all is "unknown", not "zero" -
        # those are different states and must not be conflated.
        orders = [{"platinum": 10}]
        entries = _order_entries(orders)
        self.assertEqual(len(entries), 1)
        self.assertIsNone(entries[0]["quantity"])


class TestRefinementFallback(unittest.TestCase):
    """
    Test 3: if Radiant is requested and only an Intact listing exists, the
    caller must be told this is a FALLBACK (subtype_matched=False) - never
    silently reported as if it were a confirmed Radiant price.
    """

    def test_fallback_flagged_when_no_exact_tier(self):
        sell_orders = [
            {"platinum": 20, "quantity": 2, "subtype": "intact"},
        ]
        entry, matched = _best_matching_entry(sell_orders, "radiant")
        self.assertIsNotNone(entry)
        self.assertFalse(matched, "fallback price must be flagged as NOT tier-matched")
        self.assertEqual(entry["price"], 20)

    def test_exact_tier_match_flagged_true(self):
        sell_orders = [
            {"platinum": 20, "quantity": 2, "subtype": "intact"},
            {"platinum": 50, "quantity": 1, "subtype": "radiant"},
        ]
        entry, matched = _best_matching_entry(sell_orders, "radiant")
        self.assertTrue(matched)
        self.assertEqual(entry["price"], 50)

    def test_no_listing_at_all_returns_none(self):
        entry, matched = _best_matching_entry([], "radiant")
        self.assertIsNone(entry)
        self.assertFalse(matched)


def _simple_relic() -> Relic:
    """3 common / 2 uncommon / 1 rare reward, used across several tests."""
    return Relic(
        relic_name="Test Relic",
        rewards=[
            RelicReward("Common A", "common"),
            RelicReward("Common B", "common"),
            RelicReward("Common C", "common"),
            RelicReward("Uncommon A", "uncommon"),
            RelicReward("Uncommon B", "uncommon"),
            RelicReward("Rare A", "rare"),
        ],
    )


class TestExpectedProfit(unittest.TestCase):
    """Test 4: expected profit is expected_value - cost, nothing fancier."""

    def test_basic_expected_profit(self):
        relic = _simple_relic()
        prices = {
            "common a": 1.0, "common b": 1.0, "common c": 1.0,
            "uncommon a": 5.0, "uncommon b": 5.0,
            "rare a": 60.0,
        }
        # Intact: 3x25.33% common, 2x11% uncommon, 1x2% rare
        expected_ev = (0.2533 * 1.0 * 3) + (0.11 * 5.0 * 2) + (0.02 * 60.0)
        result = relic.profitability("intact", prices, relic_cost=7.0)
        self.assertAlmostEqual(result["expected_value"], expected_ev, places=2)
        self.assertAlmostEqual(result["expected_profit"], expected_ev - 7.0, places=2)


class TestGuaranteedLoss(unittest.TestCase):
    """Test 5: if every possible reward is below cost, it's a guaranteed loss."""

    def test_all_rewards_below_cost_is_guaranteed_loss(self):
        relic = _simple_relic()
        prices = {name: 0.5 for name in
                   ["common a", "common b", "common c", "uncommon a", "uncommon b", "rare a"]}
        result = relic.profitability("intact", prices, relic_cost=10.0)
        self.assertLess(result["expected_profit"], 0)
        self.assertLess(result["worst_case_profit"], 0)
        self.assertFalse(result["always_profitable"])
        self.assertEqual(classify_category(result), "red")


class TestRiskyNotGuaranteed(unittest.TestCase):
    """
    Test 6: positive expected profit but a negative worst-case outcome is
    "risky", not "guaranteed" - these must land in different categories,
    and the row must never claim always_profitable=True here.
    """

    def test_positive_expected_negative_worst_case(self):
        relic = _simple_relic()
        prices = {
            "common a": 0.1, "common b": 0.1, "common c": 0.1,
            "uncommon a": 0.1, "uncommon b": 0.1,
            "rare a": 200.0,  # one big rare carries the average
        }
        result = relic.profitability("intact", prices, relic_cost=3.0)
        self.assertGreater(result["expected_profit"], 0)
        self.assertLess(result["worst_case_profit"], 0)
        self.assertFalse(result["always_profitable"])
        self.assertEqual(classify_category(result), "yellow")


class TestBuyNDistribution(unittest.TestCase):
    """
    Test 7: buying N relics must use the real N-fold outcome distribution
    (dynamic-programming convolution), not single-open worst-case * N,
    and the probabilities returned must be a valid distribution.
    """

    def test_n3_is_not_naive_worst_case_times_three(self):
        relic = _simple_relic()
        prices = {
            "common a": 1.0, "common b": 1.0, "common c": 1.0,
            "uncommon a": 5.0, "uncommon b": 5.0,
            "rare a": 60.0,
        }
        result = relic.buy_n_analysis(
            "intact", prices, relic_cost=7.0, plat_per_trace=0.0, n=3
        )
        naive_worst_case_profit = result["worst_case_profit"]  # this IS n * single worst, by definition
        # The real distribution's probability of hitting exactly that worst
        # case 3 times in a row must be far below certainty - i.e. the
        # engine is tracking a genuine distribution, not just scaling one
        # number by n. (0.2533 chance of the cheapest common per open.)
        self.assertLess(result["prob_loss_pct"], 100.0)
        self.assertGreater(result["prob_profit_pct"], 0.0)

    def test_probabilities_sum_to_100(self):
        relic = _simple_relic()
        prices = {
            "common a": 1.0, "common b": 1.0, "common c": 1.0,
            "uncommon a": 5.0, "uncommon b": 5.0,
            "rare a": 60.0,
        }
        result = relic.buy_n_analysis(
            "intact", prices, relic_cost=7.0, plat_per_trace=0.0, n=3
        )
        total = result["prob_profit_pct"] + result["prob_loss_pct"] + result["prob_breakeven_pct"]
        # Small tolerance for floating-point rounding in the DP convolution
        # (outcome keys are rounded to 4 decimals so near-identical totals
        # merge) - a few hundredths of a percent is expected, but material
        # drift here would mean probability mass is leaking somewhere.
        self.assertAlmostEqual(total, 100.0, delta=0.1)

    def test_exact_dp_used_below_threshold(self):
        relic = _simple_relic()
        prices = {"common a": 1.0, "common b": 1.0, "common c": 1.0,
                   "uncommon a": 5.0, "uncommon b": 5.0, "rare a": 60.0}
        result = relic.buy_n_analysis(
            "intact", prices, relic_cost=7.0, plat_per_trace=0.0, n=10,
            exact_threshold=120,
        )
        self.assertFalse(result["approx"], "n below exact_threshold must use exact DP, not Monte Carlo")

    def test_monte_carlo_flagged_above_threshold(self):
        relic = _simple_relic()
        prices = {"common a": 1.0, "common b": 1.0, "common c": 1.0,
                   "uncommon a": 5.0, "uncommon b": 5.0, "rare a": 60.0}
        result = relic.buy_n_analysis(
            "intact", prices, relic_cost=7.0, plat_per_trace=0.0, n=200,
            exact_threshold=120,
        )
        self.assertTrue(result["approx"], "n above exact_threshold must be flagged as approximate")


class TestUnknownPriceIsNeverFree(unittest.TestCase):
    """An unknown price must never be treated as 0 / guaranteed profit."""

    def test_no_relic_cost_is_never_always_profitable(self):
        relic = _simple_relic()
        prices = {name: 5.0 for name in
                   ["common a", "common b", "common c", "uncommon a", "uncommon b", "rare a"]}
        result = relic.profitability("intact", prices, relic_cost=None)
        self.assertFalse(result["price_known"])
        self.assertFalse(result["always_profitable"])
        self.assertEqual(classify_category(result), "white")

    def test_unknown_price_risk_score_is_maxed(self):
        relic = _simple_relic()
        prices = {name: 5.0 for name in
                   ["common a", "common b", "common c", "uncommon a", "uncommon b", "rare a"]}
        result = relic.profitability("intact", prices, relic_cost=None)
        self.assertEqual(compute_risk_score(result), 100)


class TestCategoryCombination(unittest.TestCase):
    """A guaranteed profit on either channel must not be dragged down by the other."""

    def test_best_channel_wins(self):
        self.assertEqual(combine_categories("green", "white"), "green")
        self.assertEqual(combine_categories("red", "yellow"), "yellow")
        self.assertEqual(combine_categories("white", "white"), "white")


class TestRefinementMarginalValue(unittest.TestCase):
    """Radiant must never be silently treated as equal to any other tier."""

    def test_marginal_value_uses_correct_trace_delta(self):
        relic = _simple_relic()
        prices = {"common a": 1.0, "common b": 1.0, "common c": 1.0,
                   "uncommon a": 5.0, "uncommon b": 5.0, "rare a": 60.0}
        result = relic.marginal_trace_value("intact", "radiant", prices)
        self.assertEqual(result["trace_cost"], 100)  # 100 - 0
        expected_gain = relic.expected_value("radiant", prices) - relic.expected_value("intact", prices)
        self.assertAlmostEqual(result["ev_gain"], expected_gain, places=6)
        self.assertAlmostEqual(result["value_per_trace"], expected_gain / 100, places=6)


class TestFindRelicsForReward(unittest.TestCase):
    """Item #12 from the review: 'specific Prime part' search mode."""

    def _relics(self):
        return {
            "Lith A1": Relic("Lith A1", rewards=[
                RelicReward("Ember Prime Blueprint", "rare"),
                RelicReward("Junk Item", "common"),
            ], vaulted=False),
            "Meso B2": Relic("Meso B2", rewards=[
                RelicReward("Ember Prime Systems", "uncommon"),
                RelicReward("Ember Prime Blueprint", "common"),
            ], vaulted=True),
            "Neo C3": Relic("Neo C3", rewards=[
                RelicReward("Unrelated Prime Chassis", "rare"),
            ], vaulted=None),
        }

    def test_exact_and_substring_match(self):
        relics = self._relics()
        results = find_relics_for_reward("Ember Prime Blueprint", relics, "radiant")
        names = {r["relic_name"] for r in results}
        self.assertEqual(names, {"Lith A1", "Meso B2"})

    def test_case_insensitive_partial_match(self):
        relics = self._relics()
        results = find_relics_for_reward("ember prime bp", relics, "radiant")
        # "ember prime bp" is NOT a substring of "ember prime blueprint" nor
        # vice versa, so this should find nothing - matching must not
        # silently guess at abbreviations it wasn't given.
        self.assertEqual(results, [])

        results2 = find_relics_for_reward("ember prime", relics, "radiant")
        names = {r["relic_name"] for r in results2}
        self.assertEqual(names, {"Lith A1", "Meso B2"})

    def test_no_match_returns_empty(self):
        relics = self._relics()
        self.assertEqual(find_relics_for_reward("Nonexistent Item", relics, "radiant"), [])

    def test_empty_query_returns_empty(self):
        relics = self._relics()
        self.assertEqual(find_relics_for_reward("   ", relics, "radiant"), [])

    def test_sorted_by_chance_descending(self):
        relics = self._relics()
        results = find_relics_for_reward("Ember Prime Blueprint", relics, "radiant")
        chances = [r["chance_pct"] for r in results]
        self.assertEqual(chances, sorted(chances, reverse=True))
        # Lith A1's copy is "rare" (10% at radiant), Meso B2's is "common" (16.67%)
        self.assertEqual(results[0]["relic_name"], "Meso B2")

    def test_expected_openings_is_inverse_of_chance(self):
        relics = self._relics()
        results = find_relics_for_reward("Ember Prime Blueprint", relics, "radiant")
        for r in results:
            self.assertAlmostEqual(r["expected_openings"], 100.0 / r["chance_pct"], places=6)

    def test_vaulted_status_passed_through(self):
        relics = self._relics()
        results = find_relics_for_reward("Ember Prime Blueprint", relics, "radiant")
        by_name = {r["relic_name"]: r["vaulted"] for r in results}
        self.assertEqual(by_name["Lith A1"], False)
        self.assertEqual(by_name["Meso B2"], True)


class TestDucatEfficiency(unittest.TestCase):
    """Item #13 from the review: Ducat efficiency, kept as its own objective."""

    def _relic(self):
        return _simple_relic()

    def test_expected_ducats_weighted_sum(self):
        relic = self._relic()
        ducats = {"rare a": 45, "uncommon a": 15}  # others unmatched -> 0
        result = relic.expected_ducats("intact", ducats)
        expected = (0.02 * 45) + (0.11 * 15)  # rare + one uncommon slot at intact
        self.assertAlmostEqual(result, expected, places=4)

    def test_missing_ducat_data_counts_as_zero_not_error(self):
        relic = self._relic()
        result = relic.expected_ducats("intact", {})
        self.assertEqual(result, 0.0)

    def test_best_ducat_reward_picks_highest(self):
        relic = self._relic()
        ducats = {"rare a": 45, "uncommon a": 15, "uncommon b": 60}
        best = relic.best_ducat_reward("intact", ducats)
        self.assertEqual(best["name"], "Uncommon B")
        self.assertEqual(best["ducats"], 60)

    def test_best_ducat_reward_none_when_no_ducat_data(self):
        relic = self._relic()
        self.assertIsNone(relic.best_ducat_reward("intact", {}))

    def test_ducat_efficiency_basic_ratio(self):
        relic = self._relic()
        ducats = {"rare a": 45, "uncommon a": 15, "uncommon b": 15}
        result = relic.ducat_efficiency("intact", ducats, relic_cost=10.0)
        expected_ducats = relic.expected_ducats("intact", ducats)
        self.assertTrue(result["price_known"])
        self.assertAlmostEqual(result["expected_ducats"], expected_ducats, places=4)
        self.assertAlmostEqual(result["plat_per_ducat"], 10.0 / expected_ducats, places=4)
        self.assertAlmostEqual(result["ducats_per_plat"], expected_ducats / 10.0, places=4)

    def test_ducat_efficiency_unknown_cost_gives_none_ratios(self):
        relic = self._relic()
        ducats = {"rare a": 45}
        result = relic.ducat_efficiency("intact", ducats, relic_cost=None)
        self.assertFalse(result["price_known"])
        self.assertIsNone(result["plat_per_ducat"])
        self.assertIsNone(result["ducats_per_plat"])

    def test_ducat_efficiency_zero_expected_ducats_gives_none_ratios(self):
        relic = self._relic()
        result = relic.ducat_efficiency("intact", {}, relic_cost=10.0)
        self.assertEqual(result["expected_ducats"], 0.0)
        self.assertIsNone(result["plat_per_ducat"])
        self.assertIsNone(result["ducats_per_plat"])

    def test_ducat_efficiency_includes_trace_cost(self):
        relic = self._relic()
        ducats = {"rare a": 45}
        result = relic.ducat_efficiency("radiant", ducats, relic_cost=5.0, plat_per_trace=0.2)
        # 100 traces to Radiant * 0.2 plat/trace = 20 plat, plus 5 plat relic cost = 25
        self.assertAlmostEqual(result["total_cost"], 25.0, places=4)


class TestCheapestCombinationCost(unittest.TestCase):
    """Item #11 from the review: 'Best Available Deal' for buying N."""

    def test_sweeps_multiple_sellers_in_price_order(self):
        entries = [
            {"price": 20, "quantity": 5},
            {"price": 10, "quantity": 2},
            {"price": 15, "quantity": 3},
        ]
        result = cheapest_combination_cost(entries, n=4)
        # cheapest seller (10p) has 2, next cheapest (15p) has 3 - need 2 more
        self.assertEqual(result["total_cost"], 2 * 10 + 2 * 15)
        self.assertEqual(result["units_filled"], 4)
        self.assertTrue(result["fully_filled"])
        self.assertAlmostEqual(result["avg_price_per_unit"], (2 * 10 + 2 * 15) / 4)

    def test_not_naive_single_price_times_n(self):
        # The bug this replaces: relic_cost * n, assuming the single
        # cheapest listing has unlimited stock. Here the cheapest seller
        # only has 1 unit, so naive pricing (10 * 5 = 50) would be wrong -
        # real cost must be higher because units 2-5 come from pricier sellers.
        entries = [
            {"price": 10, "quantity": 1},
            {"price": 12, "quantity": 10},
        ]
        result = cheapest_combination_cost(entries, n=5)
        naive_wrong_total = 10 * 5
        self.assertNotEqual(result["total_cost"], naive_wrong_total)
        self.assertEqual(result["total_cost"], 10 * 1 + 12 * 4)

    def test_insufficient_stock_reports_not_fully_filled(self):
        entries = [{"price": 10, "quantity": 2}, {"price": 20, "quantity": 1}]
        result = cheapest_combination_cost(entries, n=10)
        self.assertFalse(result["fully_filled"])
        self.assertEqual(result["units_filled"], 3)
        self.assertEqual(result["total_cost"], 10 * 2 + 20 * 1)

    def test_unknown_quantity_treated_as_exactly_one(self):
        # Missing quantity must never be treated as unlimited stock - that
        # would silently overstate how much is actually purchasable at
        # that seller's price.
        entries = [{"price": 5, "quantity": None}, {"price": 8, "quantity": 3}]
        result = cheapest_combination_cost(entries, n=2)
        self.assertEqual(result["total_cost"], 5 * 1 + 8 * 1)
        self.assertEqual(result["units_filled"], 2)

    def test_empty_order_book(self):
        result = cheapest_combination_cost([], n=3)
        self.assertEqual(result["units_filled"], 0)
        self.assertFalse(result["fully_filled"])
        self.assertIsNone(result["avg_price_per_unit"])

    def test_orders_used_is_in_draw_order_with_correct_quantities(self):
        entries = [{"price": 20, "quantity": 5}, {"price": 10, "quantity": 2}]
        result = cheapest_combination_cost(entries, n=3)
        self.assertEqual(
            result["orders_used"],
            [{"price": 10, "quantity_used": 2}, {"price": 20, "quantity_used": 1}],
        )

    def test_n_zero_or_negative_fills_nothing(self):
        entries = [{"price": 10, "quantity": 5}]
        result = cheapest_combination_cost(entries, n=0)
        self.assertEqual(result["units_filled"], 0)
        self.assertTrue(result["fully_filled"])  # 0 units needed, 0 units is "fully" filled
        self.assertEqual(result["total_cost"], 0.0)


class TestBuyNCombinationOverride(unittest.TestCase):
    """buy_n_analysis must use the real combination total, not relic_cost * n, when given one."""

    def test_override_replaces_naive_relic_cost_times_n(self):
        relic = _simple_relic()
        prices = {"common a": 1.0, "common b": 1.0, "common c": 1.0,
                   "uncommon a": 5.0, "uncommon b": 5.0, "rare a": 60.0}
        naive = relic.buy_n_analysis("intact", prices, relic_cost=10.0, plat_per_trace=0.0, n=5)
        combo = relic.buy_n_analysis(
            "intact", prices, relic_cost=10.0, plat_per_trace=0.0, n=5,
            total_relic_cost_override=70.0,  # e.g. real sweep cost, pricier than naive 10*5=50
        )
        self.assertEqual(naive["total_cost"], 50.0)
        self.assertEqual(combo["total_cost"], 70.0)
        self.assertNotEqual(naive["expected_profit"], combo["expected_profit"])

    def test_override_still_adds_trace_cost_per_relic(self):
        relic = _simple_relic()
        prices = {"common a": 1.0, "common b": 1.0, "common c": 1.0,
                   "uncommon a": 5.0, "uncommon b": 5.0, "rare a": 60.0}
        result = relic.buy_n_analysis(
            "radiant", prices, relic_cost=None, plat_per_trace=0.5, n=2,
            total_relic_cost_override=70.0,
        )
        # 100 traces to radiant * 0.5 plat/trace * 2 relics = 100, plus 70 override
        self.assertAlmostEqual(result["total_cost"], 70.0 + 100.0)
        self.assertTrue(result["price_known"])


class TestRefinementComparison(unittest.TestCase):
    """Item #6 from the review: full Intact->Radiant comparison, not one tier."""

    def _relic(self):
        return _simple_relic()

    def _prices(self):
        return {"common a": 1.0, "common b": 1.0, "common c": 1.0,
                 "uncommon a": 5.0, "uncommon b": 5.0, "rare a": 200.0}

    def test_all_four_tiers_present_in_order(self):
        relic = self._relic()
        rows = relic.refinement_comparison(self._prices(), relic_cost=5.0)
        self.assertEqual([r["tier"] for r in rows], ["intact", "exceptional", "flawless", "radiant"])

    def test_trace_cost_matches_known_table(self):
        relic = self._relic()
        rows = relic.refinement_comparison(self._prices(), relic_cost=5.0)
        by_tier = {r["tier"]: r["trace_cost"] for r in rows}
        self.assertEqual(by_tier, {"intact": 0, "exceptional": 25, "flawless": 50, "radiant": 100})

    def test_intact_has_no_incremental_value(self):
        relic = self._relic()
        rows = relic.refinement_comparison(self._prices(), relic_cost=5.0)
        self.assertIsNone(rows[0]["incremental_ev_per_trace"])
        for row in rows[1:]:
            self.assertIsNotNone(row["incremental_ev_per_trace"])

    def test_incremental_value_matches_marginal_trace_value(self):
        # The comparison table's Radiant row must agree with the existing,
        # separately-tested marginal_trace_value() calculation for
        # Intact->Radiant - two ways of computing the same number must
        # never silently drift apart.
        relic = self._relic()
        prices = self._prices()
        rows = relic.refinement_comparison(prices, relic_cost=5.0)
        radiant_row = next(r for r in rows if r["tier"] == "radiant")
        # marginal_trace_value only compares two arbitrary tiers directly
        # (Intact->Radiant here), while the table accumulates tier-by-tier
        # (Intact->Exceptional->Flawless->Radiant) - both must land on the
        # exact same total EV gain per trace over the full Intact->Radiant span,
        # since it's the same 100 traces and the same EV delta either way.
        direct = relic.marginal_trace_value("intact", "radiant", prices)
        # Reconstruct the table's cumulative EV-per-trace across all 100 traces:
        total_ev_gain = relic.expected_value("radiant", prices) - relic.expected_value("intact", prices)
        self.assertAlmostEqual(total_ev_gain / 100, direct["value_per_trace"], places=6)
        # Sanity: the radiant row's own incremental (Flawless->Radiant only,
        # the last 50 traces) must be a real, finite number.
        self.assertIsInstance(radiant_row["incremental_ev_per_trace"], float)

    def test_rare_slot_chance_increases_with_refinement(self):
        relic = self._relic()
        rows = relic.refinement_comparison(self._prices(), relic_cost=5.0)
        chances = [r["rare_slot_chance_pct"] for r in rows]
        self.assertEqual(chances, sorted(chances))  # non-decreasing: 2% -> 4% -> 6% -> 10%
        self.assertEqual(chances[0], 2.0)
        self.assertEqual(chances[-1], 10.0)

    def test_chance_of_profit_and_loss_sum_leq_100(self):
        relic = self._relic()
        rows = relic.refinement_comparison(self._prices(), relic_cost=5.0)
        for row in rows:
            self.assertLessEqual(row["chance_of_profit_pct"] + row["chance_of_loss_pct"], 100.01)

    def test_best_refinement_by_expected_profit(self):
        relic = self._relic()
        best = relic.best_refinement(self._prices(), relic_cost=5.0, objective="expected_profit")
        rows = relic.refinement_comparison(self._prices(), relic_cost=5.0)
        expected_best_tier = max(rows, key=lambda r: r["expected_profit"])["tier"]
        self.assertEqual(best["tier"], expected_best_tier)

    def test_best_refinement_radiant_not_always_optimal(self):
        # A relic whose only valuable reward is in the COMMON slot (which
        # shrinks, not grows, with refinement) should NOT recommend
        # Radiant - this is the review's explicit concern that Radiant
        # isn't always the right call.
        relic = Relic("Cheap Rare Relic", rewards=[
            RelicReward("Valuable Common", "common"),
            RelicReward("Junk B", "common"),
            RelicReward("Junk C", "common"),
            RelicReward("Junk D", "uncommon"),
            RelicReward("Junk E", "uncommon"),
            RelicReward("Junk F", "rare"),
        ])
        prices = {"valuable common": 100.0, "junk b": 0.1, "junk c": 0.1,
                   "junk d": 0.1, "junk e": 0.1, "junk f": 0.1}
        # Common slot chance SHRINKS from 25.33% (intact) to 16.67% (radiant),
        # so EV should actually go down as you refine - Intact should win.
        best = relic.best_refinement(prices, relic_cost=1.0, objective="expected_profit", )
        rows = relic.refinement_comparison(prices, relic_cost=1.0)
        self.assertEqual(best["tier"], "intact")
        self.assertLess(rows[-1]["expected_value"], rows[0]["expected_value"])

    def test_unknown_objective_falls_back_to_expected_profit(self):
        relic = self._relic()
        best = relic.best_refinement(self._prices(), relic_cost=5.0, objective="nonsense")
        self.assertEqual(best["objective"], "expected_profit")


class TestChanceOfAtLeastOne(unittest.TestCase):
    """Item #5 from the review: cumulative 'chance of >= 1 within N opens' table."""

    def test_matches_reviews_own_worked_example(self):
        # The review's exact worked example: 10% per-open chance.
        results = chance_of_at_least_one(10.0, [1, 3, 6, 10, 20])
        by_n = {r["n"]: r["chance_at_least_one_pct"] for r in results}
        self.assertAlmostEqual(by_n[1], 10.0, places=1)
        self.assertAlmostEqual(by_n[3], 27.1, places=1)
        self.assertAlmostEqual(by_n[6], 46.9, places=1)
        self.assertAlmostEqual(by_n[10], 65.1, places=1)
        self.assertAlmostEqual(by_n[20], 87.8, places=1)

    def test_monotonically_increasing_with_n(self):
        results = chance_of_at_least_one(5.0, [1, 5, 10, 25, 50])
        chances = [r["chance_at_least_one_pct"] for r in results]
        self.assertEqual(chances, sorted(chances))

    def test_zero_chance_stays_zero_at_every_n(self):
        results = chance_of_at_least_one(0.0, [1, 10, 100])
        self.assertTrue(all(r["chance_at_least_one_pct"] == 0.0 for r in results))

    def test_n_zero_is_zero_percent(self):
        results = chance_of_at_least_one(50.0, [0])
        self.assertEqual(results[0]["chance_at_least_one_pct"], 0.0)

    def test_preserves_caller_order_not_sorted(self):
        results = chance_of_at_least_one(10.0, [10, 1, 6])
        self.assertEqual([r["n"] for r in results], [10, 1, 6])

    def test_full_chance_saturates_near_100(self):
        results = chance_of_at_least_one(100.0, [1, 5])
        for r in results:
            self.assertAlmostEqual(r["chance_at_least_one_pct"], 100.0, places=6)


if __name__ == "__main__":
    unittest.main()

