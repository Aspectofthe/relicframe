"""
test_parse_official_drops.py
Tests for parse_official_drops.py - explicitly the PRIMARY, preferred data
source per its own docstring ("DE's own numbers, not a third-party copy of
them"), so it's worth having real coverage on, not just trusting the regex
by eye.

Run with:
    python -m unittest test_parse_official_drops.py -v
"""

import csv
import os
import shutil
import tempfile
import unittest

from parse_official_drops import (
    parse_file, classify_rarity, build_standard_relics, write_csv,
)


# A standard 6-slot relic block, formatted the way DE's actual export uses:
# tab-separated "Item Name\tRarityWord (XX.XX%)", one Intact block.
STANDARD_RELIC_BLOCK = (
    "Lith A1 Relic (Intact)\n"
    "Ember Prime Blueprint\tRare (2.00%)\n"
    "Frost Prime Chassis\tUncommon (11.00%)\n"
    "Frost Prime Systems\tUncommon (11.00%)\n"
    "Junk Item C\tUncommon (25.33%)\n"
    "Junk Item D\tUncommon (25.33%)\n"
    "Junk Item E\tUncommon (25.33%)\n"
    "\n"
)


class TestParseFile(unittest.TestCase):
    def setUp(self):
        self.tmpdir = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def _write(self, content: str) -> str:
        path = os.path.join(self.tmpdir, "drops.txt")
        with open(path, "w", encoding="utf-8") as f:
            f.write(content)
        return path

    def test_parses_header_and_rewards(self):
        path = self._write(STANDARD_RELIC_BLOCK)
        parsed = parse_file(path)
        self.assertIn("Lith A1", parsed)
        self.assertIn("intact", parsed["Lith A1"])
        self.assertEqual(len(parsed["Lith A1"]["intact"]), 6)

    def test_reward_percentages_parsed_correctly(self):
        path = self._write(STANDARD_RELIC_BLOCK)
        parsed = parse_file(path)
        by_name = dict(parsed["Lith A1"]["intact"])
        self.assertEqual(by_name["Ember Prime Blueprint"], 2.00)
        self.assertEqual(by_name["Frost Prime Chassis"], 11.00)
        self.assertEqual(by_name["Junk Item C"], 25.33)

    def test_relic_without_a_code_still_parses(self):
        # HEADER_RE's code group is optional - e.g. a hypothetical
        # "Requiem Relic (Intact)" with no trailing code.
        text = (
            "Requiem Relic (Intact)\n"
            "Some Reward\tRare (2.00%)\n"
        )
        path = self._write(text)
        parsed = parse_file(path)
        self.assertIn("Requiem", parsed)

    def test_multiple_refinement_tiers_for_same_relic(self):
        text = (
            "Lith A1 Relic (Intact)\n"
            "Ember Prime Blueprint\tRare (2.00%)\n"
            "\n"
            "Lith A1 Relic (Radiant)\n"
            "Ember Prime Blueprint\tRare (10.00%)\n"
        )
        path = self._write(text)
        parsed = parse_file(path)
        self.assertIn("intact", parsed["Lith A1"])
        self.assertIn("radiant", parsed["Lith A1"])
        self.assertEqual(parsed["Lith A1"]["radiant"][0][1], 10.00)

    def test_unrecognized_line_ends_current_block(self):
        # A line matching neither the header nor reward pattern (e.g. a
        # blank separator or stray text) must reset current_key, so
        # trailing garbage doesn't get misattributed to the last-seen relic.
        text = (
            "Lith A1 Relic (Intact)\n"
            "Ember Prime Blueprint\tRare (2.00%)\n"
            "Some unrelated footer text\n"
            "This Should Not Be Attached\tUncommon (11.00%)\n"
        )
        path = self._write(text)
        parsed = parse_file(path)
        names = [n for n, _p in parsed["Lith A1"]["intact"]]
        self.assertNotIn("This Should Not Be Attached", names)

    def test_lines_before_any_header_are_ignored(self):
        text = (
            "Some preamble text not part of any relic\n"
            "Lith A1 Relic (Intact)\n"
            "Ember Prime Blueprint\tRare (2.00%)\n"
        )
        path = self._write(text)
        parsed = parse_file(path)
        self.assertEqual(len(parsed), 1)


class TestClassifyRarity(unittest.TestCase):
    def test_exact_matches(self):
        self.assertEqual(classify_rarity(25.33, "intact"), "common")
        self.assertEqual(classify_rarity(11.00, "intact"), "uncommon")
        self.assertEqual(classify_rarity(2.00, "intact"), "rare")

    def test_within_tolerance_matches(self):
        # DE rounds to 2 decimals; TOLERANCE absorbs float noise, not real
        # discrepancies - 0.03 off should still match.
        self.assertEqual(classify_rarity(25.30, "intact"), "common")

    def test_outside_tolerance_returns_none(self):
        self.assertIsNone(classify_rarity(50.0, "intact"))

    def test_radiant_tier_uses_radiant_chances(self):
        self.assertEqual(classify_rarity(16.67, "radiant"), "common")
        self.assertEqual(classify_rarity(10.00, "radiant"), "rare")
        # Intact's rare chance (2.00%) must NOT match under the radiant
        # table - refinement-specific chances must not bleed together.
        self.assertIsNone(classify_rarity(2.00, "radiant"))


class TestBuildStandardRelics(unittest.TestCase):
    def test_six_slot_relic_with_valid_percentages_is_standard(self):
        parsed = {"Lith A1": {"intact": [
            ("Ember Prime Blueprint", 2.00),
            ("Frost Prime Chassis", 11.00),
            ("Frost Prime Systems", 11.00),
            ("Junk C", 25.33), ("Junk D", 25.33), ("Junk E", 25.33),
        ]}}
        standard, non_standard = build_standard_relics(parsed)
        self.assertIn("Lith A1", standard)
        self.assertEqual(non_standard, [])
        rarities = dict(standard["Lith A1"])
        self.assertEqual(rarities["Ember Prime Blueprint"], "rare")

    def test_wrong_slot_count_is_non_standard(self):
        parsed = {"Requiem Eterna": {"intact": [
            ("A", 2.00), ("B", 11.00), ("C", 11.00), ("D", 25.33),
            ("E", 25.33), ("F", 25.33), ("G", 25.33), ("H", 25.33),
        ]}}  # 8 slots, not the standard 6
        standard, non_standard = build_standard_relics(parsed)
        self.assertNotIn("Requiem Eterna", standard)
        self.assertIn("Requiem Eterna", non_standard)

    def test_unrecognized_percentage_is_non_standard(self):
        parsed = {"Weird Relic": {"intact": [
            ("A", 2.00), ("B", 11.00), ("C", 11.00),
            ("D", 25.33), ("E", 25.33), ("F", 99.99),  # doesn't match any known slot chance
        ]}}
        standard, non_standard = build_standard_relics(parsed)
        self.assertNotIn("Weird Relic", standard)
        self.assertIn("Weird Relic", non_standard)

    def test_missing_intact_tier_is_non_standard(self):
        parsed = {"Radiant Only Relic": {"radiant": [
            ("A", 10.0), ("B", 20.0), ("C", 20.0), ("D", 16.67), ("E", 16.67), ("F", 16.67),
        ]}}
        standard, non_standard = build_standard_relics(parsed)
        self.assertIn("Radiant Only Relic", non_standard)


class TestWriteCsv(unittest.TestCase):
    def setUp(self):
        self.tmpdir = tempfile.mkdtemp()
        self.path = os.path.join(self.tmpdir, "out.csv")

    def tearDown(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_writes_without_vaulted_column_by_default(self):
        standard = {"Lith A1": [("Ember Prime Blueprint", "rare")]}
        write_csv(standard, self.path)
        with open(self.path, newline="", encoding="utf-8") as f:
            header = next(csv.reader(f))
        self.assertEqual(header, ["relic_name", "reward_name", "rarity"])

    def test_unknown_vaulted_status_writes_blank_not_false(self):
        standard = {"Lith A1": [("Ember Prime Blueprint", "rare")]}
        write_csv(standard, self.path, vaulted={})  # relic not present in vaulted dict at all
        with open(self.path, newline="", encoding="utf-8") as f:
            row = next(csv.DictReader(f))
        self.assertEqual(row["vaulted"], "")

    def test_known_vaulted_status_written_correctly(self):
        standard = {"Lith A1": [("Ember Prime Blueprint", "rare")]}
        write_csv(standard, self.path, vaulted={"Lith A1": True})
        with open(self.path, newline="", encoding="utf-8") as f:
            row = next(csv.DictReader(f))
        self.assertEqual(row["vaulted"], "True")


if __name__ == "__main__":
    unittest.main()

