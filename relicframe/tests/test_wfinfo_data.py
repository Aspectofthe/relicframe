"""
test_wfinfo_data.py
Tests for wfinfo_data.py - the WFInfo community-feed parser.

Run with:
    python -m unittest test_wfinfo_data.py -v
"""

import csv
import os
import shutil
import tempfile
import unittest

from relic_data import Relic, RelicReward, load_relics_from_csv
from wfinfo_data import build_relics_from_filtered, build_ducat_map, write_relics_csv


class TestBuildRelicsFromFiltered(unittest.TestCase):
    def _sample(self):
        return {
            "relics": {
                "Lith": {
                    "A1": {
                        "vaulted": True,
                        "rare1": "Ember Prime Blueprint",
                        "uncommon1": "Frost Prime Chassis",
                        "uncommon2": "Junk B",
                        "common1": "Junk C",
                        "common2": "Junk D",
                        "common3": "Junk E",
                    },
                    "B2": {
                        "vaulted": False,
                        "rare1": "Other Reward",
                        "uncommon1": "Junk F",
                        "uncommon2": "Junk G",
                        "common1": "Junk H",
                        "common2": "Junk I",
                        "common3": "Junk J",
                    },
                },
            },
        }

    def test_relic_own_vaulted_field_is_set_not_left_unknown(self):
        # This is the regression case: the Relic objects returned must
        # carry their own correct .vaulted value, not just the separate
        # summary dict - a caller that reads relic.vaulted directly (as
        # the vault filter and the table's Vault column both do) must see
        # the real status, not the dataclass default of None ("unknown").
        relics, vaulted = build_relics_from_filtered(self._sample())
        self.assertIs(relics["Lith A1"].vaulted, True)
        self.assertIs(relics["Lith B2"].vaulted, False)

    def test_returned_summary_dict_still_matches_relic_fields(self):
        relics, vaulted = build_relics_from_filtered(self._sample())
        for name, relic in relics.items():
            self.assertEqual(vaulted[name], relic.vaulted)

    def test_relic_with_no_vaulted_key_stays_unknown(self):
        data = {"relics": {"Meso": {"C3": {
            "rare1": "Something", "uncommon1": "A", "uncommon2": "B",
            "common1": "C", "common2": "D", "common3": "E",
        }}}}
        relics, vaulted = build_relics_from_filtered(data)
        self.assertIsNone(relics["Meso C3"].vaulted)
        self.assertNotIn("Meso C3", vaulted)

    def test_rewards_parsed_with_correct_rarity(self):
        relics, _vaulted = build_relics_from_filtered(self._sample())
        rarities = {r.reward_name: r.rarity for r in relics["Lith A1"].rewards}
        self.assertEqual(rarities["Ember Prime Blueprint"], "rare")
        self.assertEqual(rarities["Frost Prime Chassis"], "uncommon")
        self.assertEqual(rarities["Junk C"], "common")

    def test_relic_name_combines_era_and_code(self):
        relics, _vaulted = build_relics_from_filtered(self._sample())
        self.assertIn("Lith A1", relics)
        self.assertIn("Lith B2", relics)


class TestBuildDucatMap(unittest.TestCase):
    def test_ducat_map_keyed_lowercase(self):
        filtered = {"eqmt": {"Ember Prime": {"parts": {
            "Ember Prime Blueprint": {"ducats": 45},
            "Ember Prime Systems": {"ducats": 15},
        }}}}
        ducats = build_ducat_map(filtered)
        self.assertEqual(ducats["ember prime blueprint"], 45)
        self.assertEqual(ducats["ember prime systems"], 15)

    def test_missing_ducats_field_is_skipped(self):
        filtered = {"eqmt": {"Ember Prime": {"parts": {
            "Ember Prime Blueprint": {},
        }}}}
        ducats = build_ducat_map(filtered)
        self.assertEqual(ducats, {})


class TestWriteRelicsCsv(unittest.TestCase):
    """write_relics_csv must never write False for a relic whose vaulted
    status is actually unknown - that's a confident, wrong claim from data
    that doesn't support one. Mirrors parse_official_drops.py's explicit
    'blank = unknown, not assumed False' convention for the same column."""

    def setUp(self):
        self.tmpdir = tempfile.mkdtemp()
        self.path = os.path.join(self.tmpdir, "out.csv")

    def tearDown(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def _sample_relics(self):
        return {
            "Lith A1": Relic("Lith A1", rewards=[RelicReward("X", "common")], vaulted=True),
            "Lith B2": Relic("Lith B2", rewards=[RelicReward("Y", "common")], vaulted=False),
            "Lith C3": Relic("Lith C3", rewards=[RelicReward("Z", "common")], vaulted=None),
        }

    def test_unknown_vaulted_writes_blank_not_false(self):
        relics = self._sample_relics()
        write_relics_csv(relics, {}, self.path)
        with open(self.path, newline="", encoding="utf-8") as f:
            rows = {row["relic_name"]: row["vaulted"] for row in csv.DictReader(f)}
        self.assertEqual(rows["Lith A1"], "True")
        self.assertEqual(rows["Lith B2"], "False")
        self.assertEqual(rows["Lith C3"], "", "unknown vaulted status must write blank, not 'False'")

    def test_round_trip_through_load_relics_from_csv_preserves_unknown(self):
        relics = self._sample_relics()
        write_relics_csv(relics, {}, self.path)
        reloaded = load_relics_from_csv(self.path)
        self.assertIs(reloaded["Lith A1"].vaulted, True)
        self.assertIs(reloaded["Lith B2"].vaulted, False)
        self.assertIsNone(
            reloaded["Lith C3"].vaulted,
            "an unknown status must round-trip as unknown (None), not silently become False",
        )

    def test_stale_summary_dict_does_not_override_relics_own_field(self):
        # Even if the caller passes a `vaulted` summary dict that disagrees
        # with (or omits) what's on the Relic objects, the writer must
        # trust each Relic's own .vaulted field, not the separate dict -
        # that dict is a summary-count convenience, not a second source of
        # truth that can silently override the real per-relic data.
        relics = self._sample_relics()
        misleading_dict = {"Lith C3": True}  # relic.vaulted is actually None
        write_relics_csv(relics, misleading_dict, self.path)
        with open(self.path, newline="", encoding="utf-8") as f:
            rows = {row["relic_name"]: row["vaulted"] for row in csv.DictReader(f)}
        self.assertEqual(rows["Lith C3"], "")


if __name__ == "__main__":
    unittest.main()

