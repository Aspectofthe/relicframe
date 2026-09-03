"""
test_seller_blacklist.py
Tests for seller_blacklist.py. Each test runs against a temp-directory
copy of STATE_PATH so these never touch (or get polluted by) a real
seller_blacklist.json sitting in the project root.

Run with:
    python -m unittest tests.test_seller_blacklist -v
"""

import os
import shutil
import tempfile
import unittest

import seller_blacklist


class TestSellerBlacklist(unittest.TestCase):
    def setUp(self):
        self._tmpdir = tempfile.mkdtemp()
        self._orig_path = seller_blacklist.STATE_PATH
        seller_blacklist.STATE_PATH = os.path.join(self._tmpdir, "seller_blacklist.json")

    def tearDown(self):
        seller_blacklist.STATE_PATH = self._orig_path
        shutil.rmtree(self._tmpdir, ignore_errors=True)

    def test_starts_empty_when_no_file_exists(self):
        bl = seller_blacklist.SellerBlacklist()
        self.assertEqual(bl.list_names(), [])
        self.assertFalse(bl.is_blacklisted("anyone"))

    def test_add_then_is_blacklisted(self):
        bl = seller_blacklist.SellerBlacklist()
        self.assertTrue(bl.add("Scammer1"))
        self.assertTrue(bl.is_blacklisted("scammer1"))
        self.assertTrue(bl.is_blacklisted("SCAMMER1"))
        self.assertTrue(bl.is_blacklisted("  Scammer1  "))

    def test_add_is_idempotent(self):
        bl = seller_blacklist.SellerBlacklist()
        self.assertTrue(bl.add("scammer1"))
        self.assertFalse(bl.add("scammer1"))
        self.assertFalse(bl.add("SCAMMER1"))  # same seller, different case
        self.assertEqual(bl.list_names(), ["scammer1"])

    def test_remove(self):
        bl = seller_blacklist.SellerBlacklist()
        bl.add("scammer1")
        self.assertTrue(bl.remove("Scammer1"))
        self.assertFalse(bl.is_blacklisted("scammer1"))

    def test_remove_unknown_seller_returns_false(self):
        bl = seller_blacklist.SellerBlacklist()
        self.assertFalse(bl.remove("nobody"))

    def test_is_blacklisted_handles_none_and_empty(self):
        bl = seller_blacklist.SellerBlacklist()
        self.assertFalse(bl.is_blacklisted(None))
        self.assertFalse(bl.is_blacklisted(""))

    def test_persists_across_instances(self):
        bl1 = seller_blacklist.SellerBlacklist()
        bl1.add("scammer1")
        bl2 = seller_blacklist.SellerBlacklist()  # fresh load from disk
        self.assertTrue(bl2.is_blacklisted("scammer1"))

    def test_list_names_sorted(self):
        bl = seller_blacklist.SellerBlacklist()
        bl.add("zeta")
        bl.add("alpha")
        self.assertEqual(bl.list_names(), ["alpha", "zeta"])


if __name__ == "__main__":
    unittest.main()
