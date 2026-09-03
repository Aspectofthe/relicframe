"""
test_relic_row.py
Tests for relic_row.py - the shared row-computation engine extracted so
both the Tkinter GUI and the Discord bot compute identical numbers from
one source, rather than two independently-maintained copies.

Run with:
    python -m unittest test_relic_row.py -v
"""

import unittest

from relic_data import Relic, RelicReward
from relic_row import compute_channel, compute_row


def _relic():
    return Relic("Lith A1", rewards=[
        RelicReward("Common A", "common"),
        RelicReward("Common B", "common"),
        RelicReward("Common C", "common"),
        RelicReward("Uncommon A", "uncommon"),
        RelicReward("Uncommon B", "uncommon"),
        RelicReward("Rare A", "rare"),
    ])


def _prices():
    return {
        "common a": 1.0, "common b": 1.0, "common c": 1.0,
        "uncommon a": 5.0, "uncommon b": 5.0, "rare a": 60.0,
    }


class TestComputeChannel(unittest.TestCase):
    def test_zero_quantity_excludes_cost(self):
        relic = _relic()
        profit, category, zero = compute_channel(relic, 10.0, 0, "intact", 0.0, _prices())
        self.assertTrue(zero)
        self.assertFalse(profit["price_known"])
        self.assertEqual(category, "white")

    def test_missing_quantity_does_not_exclude_cost(self):
        relic = _relic()
        profit, category, zero = compute_channel(relic, 10.0, None, "intact", 0.0, _prices())
        self.assertFalse(zero)
        self.assertTrue(profit["price_known"])

    def test_no_cost_at_all_is_unknown_not_zero(self):
        relic = _relic()
        profit, category, zero = compute_channel(relic, None, None, "intact", 0.0, _prices())
        self.assertFalse(zero)
        self.assertFalse(profit["price_known"])


class TestComputeRow(unittest.TestCase):
    def test_basic_row_shape(self):
        relic = _relic()
        relic_prices = {"Lith A1": {
            "online": 5.0, "online_quantity": 3, "online_subtype_matched": True, "online_is_outlier": False,
            "offline_included": 4.0, "offline_quantity": 10, "offline_subtype_matched": True, "offline_is_outlier": False,
        }}
        row = compute_row(relic, "intact", 0.0, _prices(), relic_prices, {})
        self.assertEqual(row["online_cost"], 5.0)
        self.assertEqual(row["offline_cost"], 4.0)
        self.assertIn(row["overall_category"], {"green", "yellow", "red", "white"})

    def test_relic_with_no_price_data_at_all(self):
        relic = _relic()
        row = compute_row(relic, "intact", 0.0, _prices(), {}, {})
        self.assertIsNone(row["online_cost"])
        self.assertIsNone(row["offline_cost"])
        self.assertEqual(row["overall_category"], "white")

    def test_channel_scope_online_only_uses_only_online_category(self):
        relic = _relic()
        # Online cheap & profitable, offline deliberately awful - if scope
        # correctly restricts to online-only, overall_category must NOT be
        # dragged down by the offline number.
        relic_prices = {"Lith A1": {
            "online": 1.0, "online_quantity": 3, "online_subtype_matched": True, "online_is_outlier": False,
            "offline_included": 500.0, "offline_quantity": 1, "offline_subtype_matched": True, "offline_is_outlier": False,
        }}
        row_online_only = compute_row(
            relic, "intact", 0.0, _prices(), relic_prices, {}, channel_scope="Online only"
        )
        row_both = compute_row(
            relic, "intact", 0.0, _prices(), relic_prices, {}, channel_scope="Both (best of either)"
        )
        self.assertEqual(row_online_only["overall_category"], row_online_only["online_cat"])
        # combine_categories(online=good, offline=bad) should pick the
        # better one anyway (see combine_categories's own tests), so both
        # should actually agree here - but best_risk must differ, since
        # "Online only" must never surface the offline risk number.
        self.assertEqual(row_online_only["best_risk"], row_online_only["online_risk"])

    def test_best_risk_respects_offline_only_scope(self):
        relic = _relic()
        relic_prices = {"Lith A1": {
            "online": 1.0, "online_quantity": 3, "online_subtype_matched": True, "online_is_outlier": False,
            "offline_included": 4.0, "offline_quantity": 10, "offline_subtype_matched": True, "offline_is_outlier": False,
        }}
        row = compute_row(relic, "intact", 0.0, _prices(), relic_prices, {}, channel_scope="Offline only")
        self.assertEqual(row["best_risk"], row["offline_risk"])
        self.assertEqual(row["overall_category"], row["offline_cat"])

    def test_ducat_info_present_and_uses_cheapest_actionable_cost(self):
        relic = _relic()
        relic_prices = {"Lith A1": {
            "online": None, "online_quantity": None,
            "offline_included": 8.0, "offline_quantity": 2,
        }}
        ducats = {"rare a": 45}
        row = compute_row(relic, "intact", 0.0, _prices(), relic_prices, ducats)
        self.assertTrue(row["ducat_info"]["price_known"])
        self.assertEqual(row["ducat_info"]["total_cost"], 8.0)

    def test_single_open_probability_weighs_every_reward_slot(self):
        # 3 common @ 1p, 2 uncommon @ 5p, 1 rare @ 60p (see _prices()), at
        # Intact odds (common 25.33% each, uncommon 11% each, rare 2%).
        # At a cost of 3p, only the uncommon/rare slots clear the cost
        # line - true win chance is (11*2 + 2)% = 24%, i.e. dominated by
        # how common the common slots really are, NOT an even split
        # across "4 of 6" slots and NOT just whether the rare slot alone
        # is profitable. This is exactly what "accounts for everything"
        # has to mean: the real weighted odds, not a fixed fraction.
        relic = _relic()
        relic_prices = {"Lith A1": {"online": 3.0, "online_quantity": 5}}
        row = compute_row(relic, "intact", 0.0, _prices(), relic_prices, {}, channel_scope="Online only")
        self.assertIsNotNone(row["online_prob_profit_pct"])
        self.assertIsNotNone(row["online_prob_loss_pct"])
        self.assertAlmostEqual(row["online_prob_profit_pct"], 24.0, delta=0.1)
        # win% + loss% (+ breakeven, ~0 here with distinct prices) must be
        # a real probability split, not independently-computed numbers
        # that happen to both be printed.
        self.assertAlmostEqual(
            row["online_prob_profit_pct"] + row["online_prob_loss_pct"], 100.0, delta=0.5
        )

    def test_single_open_probability_is_none_when_price_unknown(self):
        relic = _relic()
        row = compute_row(relic, "intact", 0.0, _prices(), {}, {}, channel_scope="Online only")
        self.assertIsNone(row["online_prob_profit_pct"])
        self.assertIsNone(row["online_prob_loss_pct"])

    def test_high_loss_chance_increases_risk_score_over_a_safer_relic(self):
        # Same relic/prices, only the cost differs: 3p (cheap - most slots
        # clear it) vs 60p (only the rare slot clears it). The risk score
        # must reflect that this is now a real longshot, not just look at
        # expected value / worst case severity alone.
        relic = _relic()
        safe_prices = {"Lith A1": {"online": 3.0, "online_quantity": 3}}
        risky_prices = {"Lith A1": {"online": 60.0, "online_quantity": 3}}
        safe_row = compute_row(relic, "intact", 0.0, _prices(), safe_prices, {}, channel_scope="Online only")
        risky_row = compute_row(relic, "intact", 0.0, _prices(), risky_prices, {}, channel_scope="Online only")
        self.assertLess(safe_row["online_prob_loss_pct"], risky_row["online_prob_loss_pct"])
        self.assertLess(safe_row["online_risk"], risky_row["online_risk"])


if __name__ == "__main__":
    unittest.main()

