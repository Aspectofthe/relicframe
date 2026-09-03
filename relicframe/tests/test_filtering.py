"""
test_filtering.py
Tests for filtering.py.

Run with:
    python -m unittest test_filtering.py -v
"""

import unittest

from relic_data import Relic, RelicReward
from relic_row import compute_row
from filtering import passes_filters


def _make_row(vaulted=None, online_cost=5.0, offline_cost=None, rare_price=60.0, online_qty=5):
    relic = Relic("X", rewards=[
        RelicReward("Common A", "common"), RelicReward("Common B", "common"),
        RelicReward("Common C", "common"), RelicReward("Uncommon A", "uncommon"),
        RelicReward("Uncommon B", "uncommon"), RelicReward("Rare A", "rare"),
    ], vaulted=vaulted)
    prices = {"common a": 1.0, "common b": 1.0, "common c": 1.0,
               "uncommon a": 5.0, "uncommon b": 5.0, "rare a": rare_price}
    relic_prices = {"X": {
        "online": online_cost, "online_quantity": online_qty,
        "offline_included": offline_cost, "offline_quantity": 5 if offline_cost else None,
    }}
    return compute_row(relic, "intact", 0.0, prices, relic_prices, {})


class TestVaultFilter(unittest.TestCase):
    def test_all_passes_everything(self):
        for v in (True, False, None):
            self.assertTrue(passes_filters(_make_row(vaulted=v), vault_filter="All"))

    def test_unvaulted_only_passes_false(self):
        self.assertTrue(passes_filters(_make_row(vaulted=False), vault_filter="Unvaulted"))
        self.assertFalse(passes_filters(_make_row(vaulted=True), vault_filter="Unvaulted"))
        self.assertFalse(passes_filters(_make_row(vaulted=None), vault_filter="Unvaulted"))

    def test_vaulted_only_passes_true(self):
        self.assertTrue(passes_filters(_make_row(vaulted=True), vault_filter="Vaulted"))
        self.assertFalse(passes_filters(_make_row(vaulted=False), vault_filter="Vaulted"))

    def test_unknown_only_passes_none(self):
        self.assertTrue(passes_filters(_make_row(vaulted=None), vault_filter="Unknown"))
        self.assertFalse(passes_filters(_make_row(vaulted=True), vault_filter="Unknown"))


class TestMinRoiFilter(unittest.TestCase):
    def test_relic_below_min_roi_excluded(self):
        row = _make_row(online_cost=100.0, rare_price=1.0)  # terrible ROI
        self.assertFalse(passes_filters(row, min_roi=50.0))

    def test_relic_with_unknown_price_excluded_not_passed_by_default(self):
        row = _make_row(online_cost=None, offline_cost=None)
        self.assertFalse(passes_filters(row, min_roi=10.0))

    def test_no_min_roi_means_no_filtering(self):
        row = _make_row(online_cost=None, offline_cost=None)
        self.assertTrue(passes_filters(row, min_roi=None))


class TestMaxCostFilter(unittest.TestCase):
    def test_relic_above_max_cost_excluded(self):
        row = _make_row(online_cost=500.0, offline_cost=None)
        self.assertFalse(passes_filters(row, max_cost=10.0))

    def test_relic_at_or_below_max_cost_passes(self):
        row = _make_row(online_cost=5.0, offline_cost=None)
        self.assertTrue(passes_filters(row, max_cost=10.0))


class TestChannelScopeFilter(unittest.TestCase):
    def test_online_only_ignores_offline_meeting_max_cost(self):
        row = _make_row(online_cost=500.0, offline_cost=1.0)
        # Under "Both", the cheap 1p offline price should satisfy max_cost=10.
        self.assertTrue(passes_filters(row, channel_scope="Both (best of either)", max_cost=10.0))
        # Under "Online only", only the 500p online price counts - must fail.
        self.assertFalse(passes_filters(row, channel_scope="Online only", max_cost=10.0))


class TestGreenOnlyFilter(unittest.TestCase):
    def test_non_green_excluded_when_green_only_set(self):
        row = _make_row(online_cost=500.0, rare_price=1.0)  # very likely red/white
        if row["overall_category"] != "green":
            self.assertFalse(passes_filters(row, green_only=True))

    def test_green_only_false_does_not_filter(self):
        row = _make_row(online_cost=500.0, rare_price=1.0)
        self.assertTrue(passes_filters(row, green_only=False))


if __name__ == "__main__":
    unittest.main()

