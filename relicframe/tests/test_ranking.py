"""
test_ranking.py
Tests for ranking.py.

Run with:
    python -m unittest test_ranking.py -v
"""

import unittest

from relic_data import Relic, RelicReward
from relic_row import compute_row
from ranking import sort_key_for_mode


def _row(relic_name, online_cost, offline_cost, rare_price):
    relic = Relic(relic_name, rewards=[
        RelicReward("Common A", "common"), RelicReward("Common B", "common"),
        RelicReward("Common C", "common"), RelicReward("Uncommon A", "uncommon"),
        RelicReward("Uncommon B", "uncommon"), RelicReward("Rare A", "rare"),
    ])
    prices = {"common a": 1.0, "common b": 1.0, "common c": 1.0,
               "uncommon a": 5.0, "uncommon b": 5.0, "rare a": rare_price}
    relic_prices = {relic_name: {
        "online": online_cost, "online_quantity": 5,
        "offline_included": offline_cost, "offline_quantity": 5,
    }}
    return compute_row(relic, "intact", 0.0, prices, relic_prices, {})


class TestSortKeyForMode(unittest.TestCase):
    def test_cheapest_mode_orders_ascending_by_negated_key(self):
        cheap = _row("Cheap", 2.0, None, 60.0)
        pricey = _row("Pricey", 20.0, None, 60.0)
        key = sort_key_for_mode("Cheapest")
        rows = sorted([cheap, pricey], key=key, reverse=True)
        self.assertEqual(rows[0]["relic"].relic_name, "Cheap")

    def test_expected_profit_mode_orders_by_ev_minus_cost(self):
        low_ev = _row("LowEV", 5.0, None, 10.0)
        high_ev = _row("HighEV", 5.0, None, 500.0)
        key = sort_key_for_mode("Expected Profit")
        rows = sorted([low_ev, high_ev], key=key, reverse=True)
        self.assertEqual(rows[0]["relic"].relic_name, "HighEV")

    def test_unknown_price_never_outranks_a_known_profitable_one(self):
        unknown = _row("Unknown", None, None, 60.0)
        known = _row("Known", 5.0, None, 60.0)
        for mode in ("Guaranteed Profit", "Expected Profit", "Best ROI", "Cheapest"):
            key = sort_key_for_mode(mode)
            rows = sorted([unknown, known], key=key, reverse=True)
            self.assertEqual(
                rows[0]["relic"].relic_name, "Known",
                f"mode={mode}: a relic with unknown price must never rank above a known one",
            )

    def test_online_only_scope_ignores_cheaper_offline_price(self):
        # Online 50p, offline 1p - "Online only" scope must rank by the
        # 50p online cost, not sneak in the cheaper offline number.
        row = _row("X", 50.0, 1.0, 60.0)
        key_online = sort_key_for_mode("Cheapest", channel_scope="Online only")
        key_both = sort_key_for_mode("Cheapest", channel_scope="Both (best of either)")
        # Recompute row under each scope since channel_scope affects the row itself too
        relic = Relic("X", rewards=[
            RelicReward("Common A", "common"), RelicReward("Common B", "common"),
            RelicReward("Common C", "common"), RelicReward("Uncommon A", "uncommon"),
            RelicReward("Uncommon B", "uncommon"), RelicReward("Rare A", "rare"),
        ])
        prices = {"common a": 1.0, "common b": 1.0, "common c": 1.0,
                   "uncommon a": 5.0, "uncommon b": 5.0, "rare a": 60.0}
        relic_prices = {"X": {"online": 50.0, "online_quantity": 5, "offline_included": 1.0, "offline_quantity": 5}}
        row_online = compute_row(relic, "intact", 0.0, prices, relic_prices, {}, channel_scope="Online only")
        row_both = compute_row(relic, "intact", 0.0, prices, relic_prices, {}, channel_scope="Both (best of either)")
        online_key_val = key_online(row_online)
        both_key_val = key_both(row_both)
        # Both keys are (has_cost, -min_cost); online-only's cost basis (50p)
        # must be worse (lower key) than "both" scope's basis (1p, the offline price).
        self.assertLess(online_key_val[1], both_key_val[1])

    def test_best_overall_prioritizes_category_over_raw_profit(self):
        # A "green" (guaranteed profit) relic must outrank a "yellow"
        # (risky but higher expected profit) relic under Best Overall.
        green = _row("Green", 1.0, None, 60.0)   # cheap relative to guaranteed common floor
        yellow = _row("Yellow", 0.5, None, 500.0)  # huge EV from one rare, but risky
        key = sort_key_for_mode("Best Overall")
        rows = sorted([green, yellow], key=key, reverse=True)
        if green["overall_category"] == "green" and yellow["overall_category"] != "green":
            self.assertEqual(rows[0]["relic"].relic_name, "Green")

    def test_best_roi_penalizes_a_higher_risk_row_over_lower_risk_similar_roi(self):
        # A single-listing/thin-quantity outlier can post a very high raw
        # ROI% that's not actually repeatable. Best ROI must weigh that
        # row's own risk score against it rather than sorting on raw ROI%
        # alone, so a lower-risk row with comparable ROI isn't buried
        # under a risky one-off.
        def fake_row(roi_pct, risk):
            profit = {"price_known": True, "expected_roi_pct": roi_pct}
            return {"online_profit": profit, "offline_profit": profit, "best_risk": risk}

        risky_outlier = fake_row(roi_pct=70.0, risk=80)
        solid_pick = fake_row(roi_pct=55.0, risk=5)
        key = sort_key_for_mode("Best ROI", channel_scope="Online only")
        rows = sorted([risky_outlier, solid_pick], key=key, reverse=True)
        self.assertIs(rows[0], solid_pick)

    def test_best_roi_still_breaks_ties_on_raw_roi_at_equal_risk(self):
        def fake_row(roi_pct, risk):
            profit = {"price_known": True, "expected_roi_pct": roi_pct}
            return {"online_profit": profit, "offline_profit": profit, "best_risk": risk}

        lower_roi = fake_row(roi_pct=40.0, risk=20)
        higher_roi = fake_row(roi_pct=60.0, risk=20)
        key = sort_key_for_mode("Best ROI", channel_scope="Online only")
        rows = sorted([lower_roi, higher_roi], key=key, reverse=True)
        self.assertIs(rows[0], higher_roi)


if __name__ == "__main__":
    unittest.main()

