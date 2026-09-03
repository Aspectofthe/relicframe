"""
test_analysis.py
Tests for analysis.py - mostly confirming its thin delegation wrappers
around relic_row.py/ranking.py/filtering.py are wired correctly (right
arguments, right order), since the actual engine logic is already
thoroughly tested in those modules directly. Also covers Snapshot
construction and estimate_fetch_seconds.

Run with:
    python -m unittest test_analysis.py -v
"""

import unittest

from relic_data import Relic, RelicReward
import analysis
from analysis import Snapshot, RANK_MODES


def _relic():
    return Relic("Lith A1", rewards=[
        RelicReward("Common A", "common"), RelicReward("Common B", "common"),
        RelicReward("Common C", "common"), RelicReward("Uncommon A", "uncommon"),
        RelicReward("Uncommon B", "uncommon"), RelicReward("Rare A", "rare"),
    ])


def _snapshot(online_cost=5.0, offline_cost=None, rare_price=60.0):
    relic = _relic()
    return Snapshot(
        relics={"Lith A1": relic},
        refinement="intact",
        prices={"common a": 1.0, "common b": 1.0, "common c": 1.0,
                 "uncommon a": 5.0, "uncommon b": 5.0, "rare a": rare_price},
        relic_prices={"Lith A1": {
            "online": online_cost, "online_quantity": 5,
            "offline_included": offline_cost, "offline_quantity": 5 if offline_cost else None,
        }},
        ducats={},
        name_to_slug={},
        catalog_source="test",
        catalog_age_seconds=0.0,
    )


class TestComputeRow(unittest.TestCase):
    def test_produces_expected_row_shape(self):
        snap = _snapshot()
        row = analysis.compute_row(snap.relics["Lith A1"], snap, 0.0, "Online + Offline")
        self.assertEqual(row["online_cost"], 5.0)
        self.assertIn(row["overall_category"], {"green", "yellow", "red", "white"})

    def test_compute_all_rows_covers_every_relic(self):
        snap = _snapshot()
        rows = analysis.compute_all_rows(snap, 0.0, "Online + Offline")
        self.assertEqual(len(rows), 1)


class TestPassesFilters(unittest.TestCase):
    def test_min_roi_filters_out_bad_relic(self):
        snap = _snapshot(online_cost=500.0, rare_price=1.0)
        row = analysis.compute_row(snap.relics["Lith A1"], snap, 0.0, "Online + Offline")
        self.assertFalse(analysis.passes_filters(row, "Online + Offline", min_roi=1000.0))

    def test_no_filters_passes(self):
        snap = _snapshot()
        row = analysis.compute_row(snap.relics["Lith A1"], snap, 0.0, "Online + Offline")
        self.assertTrue(analysis.passes_filters(row, "Online + Offline"))


class TestRankRows(unittest.TestCase):
    def test_rank_modes_matches_shared_ranking_module(self):
        import ranking
        self.assertEqual(RANK_MODES, ranking.RANK_MODES)

    def test_cheapest_mode_sorts_cheaper_first(self):
        cheap_snap = _snapshot(online_cost=2.0)
        pricey_snap = _snapshot(online_cost=20.0)
        cheap_row = analysis.compute_row(cheap_snap.relics["Lith A1"], cheap_snap, 0.0, "Online + Offline")
        cheap_row["relic"] = Relic("Cheap", rewards=cheap_row["relic"].rewards)
        pricey_row = analysis.compute_row(pricey_snap.relics["Lith A1"], pricey_snap, 0.0, "Online + Offline")
        pricey_row["relic"] = Relic("Pricey", rewards=pricey_row["relic"].rewards)
        ranked = analysis.rank_rows([pricey_row, cheap_row], "Cheapest", "Online + Offline")
        self.assertEqual(ranked[0]["relic"].relic_name, "Cheap")


class TestEstimateFetchSeconds(unittest.TestCase):
    def test_best_case_less_than_worst_case(self):
        best, worst = analysis.estimate_fetch_seconds(100, 50)
        self.assertLess(best, worst)

    def test_scales_with_relic_count(self):
        best_small, _ = analysis.estimate_fetch_seconds(10, 5)
        best_large, _ = analysis.estimate_fetch_seconds(1000, 500)
        self.assertLess(best_small, best_large)


if __name__ == "__main__":
    unittest.main()

