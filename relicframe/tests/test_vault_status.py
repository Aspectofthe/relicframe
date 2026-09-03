"""
test_vault_status.py
Tests for vault_status.py.

Run with:
    python -m unittest test_vault_status.py -v
"""

import os
import shutil
import tempfile
import unittest

from relic_data import Relic
from vault_status import parse_vault_status, apply_vault_status


SAMPLE_DOC = """
Some intro text that should be ignored.

Unvaulted/Available Relics
Lith A1
Meso B2
Neo C3

Vaulted/Unavailable Relics
Axi D4
Neo E5

Some trailing footer text.
"""


class TestParseVaultStatus(unittest.TestCase):
    def setUp(self):
        self.tmpdir = tempfile.mkdtemp()
        self.path = os.path.join(self.tmpdir, "vault_doc.txt")
        with open(self.path, "w", encoding="utf-8") as f:
            f.write(SAMPLE_DOC)

    def tearDown(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_unvaulted_section_parsed_as_false(self):
        vaulted = parse_vault_status(self.path)
        self.assertEqual(vaulted["Lith A1"], False)
        self.assertEqual(vaulted["Meso B2"], False)
        self.assertEqual(vaulted["Neo C3"], False)

    def test_vaulted_section_parsed_as_true(self):
        vaulted = parse_vault_status(self.path)
        self.assertEqual(vaulted["Axi D4"], True)
        self.assertEqual(vaulted["Neo E5"], True)

    def test_lines_before_any_section_header_are_ignored(self):
        vaulted = parse_vault_status(self.path)
        self.assertNotIn("Some", vaulted)  # from "Some intro text..."

    def test_relic_not_in_doc_at_all_is_absent_not_defaulted(self):
        # A relic simply not mentioned anywhere in the doc must not appear
        # in the result at all - callers are expected to treat a missing
        # key as "unknown," never guess False.
        vaulted = parse_vault_status(self.path)
        self.assertNotIn("Lith Z9", vaulted)

    def test_malformed_relic_like_lines_are_not_matched(self):
        # RELIC_NAME_RE requires "<Era> <Code>" with a real era prefix -
        # random lines that happen to be in a section shouldn't be picked
        # up as relic names.
        doc = (
            "Unvaulted/Available Relics\n"
            "Lith A1\n"
            "Not A Real Relic Line\n"
            "SomeOtherText\n"
        )
        path = os.path.join(self.tmpdir, "doc2.txt")
        with open(path, "w", encoding="utf-8") as f:
            f.write(doc)
        vaulted = parse_vault_status(path)
        self.assertIn("Lith A1", vaulted)
        self.assertNotIn("Not A Real Relic Line", vaulted)
        self.assertNotIn("SomeOtherText", vaulted)


class TestApplyVaultStatus(unittest.TestCase):
    def test_sets_vaulted_on_matching_relics_and_returns_match_count(self):
        relics = {
            "Lith A1": Relic("Lith A1"),
            "Axi D4": Relic("Axi D4"),
            "Meso Z9": Relic("Meso Z9"),  # not in the vaulted map at all
        }
        vaulted = {"Lith A1": False, "Axi D4": True}
        matched = apply_vault_status(relics, vaulted)
        self.assertEqual(matched, 2)
        self.assertIs(relics["Lith A1"].vaulted, False)
        self.assertIs(relics["Axi D4"].vaulted, True)

    def test_relic_not_in_vaulted_map_keeps_unknown(self):
        relics = {"Meso Z9": Relic("Meso Z9")}
        apply_vault_status(relics, {})
        self.assertIsNone(relics["Meso Z9"].vaulted)


if __name__ == "__main__":
    unittest.main()

