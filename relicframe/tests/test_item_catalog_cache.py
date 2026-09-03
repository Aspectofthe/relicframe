"""
test_item_catalog_cache.py
Tests for item_catalog_cache.py. Everything here uses a fake fetch_fn and a
temp directory - no real network access, so this is safe and fast to run
on every change.

Run with:
    python -m unittest test_item_catalog_cache.py -v
"""

import os
import shutil
import tempfile
import time
import unittest

import item_catalog_cache as icc


class TestItemCatalogCache(unittest.TestCase):
    def setUp(self):
        self.tmpdir = tempfile.mkdtemp()
        self.cache_path = os.path.join(self.tmpdir, "sub", "item_catalog.json")

    def tearDown(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_no_cache_hits_fetch_and_writes_cache(self):
        calls = {"n": 0}

        def fetch():
            calls["n"] += 1
            return [{"id": "a", "slug": "a_item"}]

        result = icc.load_or_fetch(self.cache_path, fetch)
        self.assertEqual(result["source"], "live")
        self.assertEqual(calls["n"], 1)
        self.assertTrue(os.path.exists(self.cache_path))

    def test_fresh_cache_is_used_without_calling_fetch(self):
        calls = {"n": 0}

        def fetch():
            calls["n"] += 1
            return [{"id": "a"}]

        icc.load_or_fetch(self.cache_path, fetch)  # writes cache
        result = icc.load_or_fetch(self.cache_path, fetch)  # should hit cache

        self.assertEqual(result["source"], "cache")
        self.assertEqual(calls["n"], 1, "fetch_fn must not be called again for a fresh cache")

    def test_expired_cache_triggers_refetch(self):
        calls = {"n": 0}

        def fetch():
            calls["n"] += 1
            return [{"id": "a", "call": calls["n"]}]

        icc.load_or_fetch(self.cache_path, fetch)
        # Simulate an old cache by writing one with a backdated timestamp.
        import json
        with open(self.cache_path) as f:
            data = json.load(f)
        data["fetched_at"] = time.time() - 999999
        with open(self.cache_path, "w") as f:
            json.dump(data, f)

        result = icc.load_or_fetch(self.cache_path, fetch, max_age_seconds=3600)
        self.assertEqual(result["source"], "live")
        self.assertEqual(calls["n"], 2)

    def test_force_refresh_ignores_fresh_cache(self):
        calls = {"n": 0}

        def fetch():
            calls["n"] += 1
            return [{"id": "a"}]

        icc.load_or_fetch(self.cache_path, fetch)
        result = icc.load_or_fetch(self.cache_path, fetch, force_refresh=True)
        self.assertEqual(result["source"], "live")
        self.assertEqual(calls["n"], 2)

    def test_network_failure_falls_back_to_stale_cache(self):
        def good_fetch():
            return [{"id": "a"}]

        def failing_fetch():
            raise ConnectionError("offline")

        icc.load_or_fetch(self.cache_path, good_fetch)
        import json
        with open(self.cache_path) as f:
            data = json.load(f)
        data["fetched_at"] = time.time() - 999999  # force expiry
        with open(self.cache_path, "w") as f:
            json.dump(data, f)

        result = icc.load_or_fetch(self.cache_path, failing_fetch, max_age_seconds=3600)
        self.assertEqual(result["source"], "stale_fallback")
        self.assertEqual(result["items"], [{"id": "a"}])

    def test_network_failure_with_no_cache_at_all_raises(self):
        def failing_fetch():
            raise ConnectionError("offline")

        with self.assertRaises(ConnectionError):
            icc.load_or_fetch(self.cache_path, failing_fetch)

    def test_corrupt_cache_file_is_treated_as_missing(self):
        os.makedirs(os.path.dirname(self.cache_path), exist_ok=True)
        with open(self.cache_path, "w") as f:
            f.write("{ not valid json ]")

        calls = {"n": 0}

        def fetch():
            calls["n"] += 1
            return [{"id": "a"}]

        result = icc.load_or_fetch(self.cache_path, fetch)
        self.assertEqual(result["source"], "live")
        self.assertEqual(calls["n"], 1)

    def test_cache_age_seconds_none_when_missing(self):
        self.assertIsNone(icc.cache_age_seconds(self.cache_path))


class TestLoadOrFetchJson(unittest.TestCase):
    """Generic (non-item-catalog) cache path, used for the ducat map."""

    def setUp(self):
        self.tmpdir = tempfile.mkdtemp()
        self.cache_path = os.path.join(self.tmpdir, "sub", "ducat_map.json")

    def tearDown(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_dict_payload_round_trips_through_cache(self):
        calls = {"n": 0}

        def fetch():
            calls["n"] += 1
            return {"nova prime systems": 45, "trinity prime chassis": 15}

        first = icc.load_or_fetch_json(self.cache_path, fetch)
        self.assertEqual(first["source"], "live")
        self.assertEqual(first["data"], {"nova prime systems": 45, "trinity prime chassis": 15})

        second = icc.load_or_fetch_json(self.cache_path, fetch)
        self.assertEqual(second["source"], "cache")
        self.assertEqual(second["data"], first["data"])
        self.assertEqual(calls["n"], 1, "fetch_fn must not be called again for a fresh cache")

    def test_force_refresh_and_stale_fallback_behave_like_item_cache(self):
        def good_fetch():
            return {"a": 1}

        def failing_fetch():
            raise ConnectionError("offline")

        icc.load_or_fetch_json(self.cache_path, good_fetch)
        result = icc.load_or_fetch_json(self.cache_path, good_fetch, force_refresh=True)
        self.assertEqual(result["source"], "live")

        import json
        with open(self.cache_path) as f:
            data = json.load(f)
        data["fetched_at"] = time.time() - 999999
        with open(self.cache_path, "w") as f:
            json.dump(data, f)

        fallback = icc.load_or_fetch_json(self.cache_path, failing_fetch, max_age_seconds=3600)
        self.assertEqual(fallback["source"], "stale_fallback")
        self.assertEqual(fallback["data"], {"a": 1})

    def test_item_cache_and_json_cache_files_are_independent(self):
        """The two cache 'shapes' (items vs payload) must not cross-read
        each other's files - a ducat-map cache path must never be mistaken
        for a valid item-catalog cache, or vice versa."""
        icc.load_or_fetch(self.cache_path, lambda: [{"id": "x"}])
        # A load_or_fetch_json read of the SAME file (wrong schema key) must
        # not silently succeed with garbage - it should look like a corrupt/
        # missing cache and just re-fetch.
        calls = {"n": 0}

        def fetch():
            calls["n"] += 1
            return {"a": 1}

        result = icc.load_or_fetch_json(self.cache_path, fetch)
        self.assertEqual(result["source"], "live")
        self.assertEqual(calls["n"], 1)


if __name__ == "__main__":
    unittest.main()

