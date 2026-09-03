"""
test_wfm_api.py
Tests for the non-network-call parts of wfm_api.py: price/quantity
normalization (also covered from the relic_data.py side in
test_relic_data.py, but here we test the underscore-prefixed helpers
directly), refinement fallback matching, and - the main thing this file
exists for - the rate limiter's thread safety.

Run with:
    python -m unittest test_wfm_api.py -v
"""

import threading
import time
import unittest

import wfm_api


class TestOrderUnitPrice(unittest.TestCase):
    def test_bulk_listing_normalized_per_item(self):
        self.assertAlmostEqual(
            wfm_api._order_unit_price({"platinum": 1000, "perTrade": 6}), 166.6667, places=3
        )

    def test_no_per_trade_passthrough(self):
        self.assertEqual(wfm_api._order_unit_price({"platinum": 45}), 45.0)

    def test_missing_platinum_is_none(self):
        self.assertIsNone(wfm_api._order_unit_price({}))


class TestOrderEntries(unittest.TestCase):
    def test_zero_quantity_excluded(self):
        entries = wfm_api._order_entries([
            {"platinum": 10, "quantity": 0},
            {"platinum": 12, "quantity": 3},
        ])
        self.assertEqual(len(entries), 1)
        self.assertEqual(entries[0]["price"], 12)

    def test_missing_quantity_not_excluded(self):
        entries = wfm_api._order_entries([{"platinum": 10}])
        self.assertEqual(len(entries), 1)
        self.assertIsNone(entries[0]["quantity"])


class TestMatchingEntries(unittest.TestCase):
    """_matching_entries backs get_relic_order_book - the full sell book,
    not just the single cheapest listing, for cheapest-combination pricing."""

    def test_returns_all_matching_not_just_cheapest(self):
        sell_orders = [
            {"platinum": 20, "quantity": 2, "subtype": "radiant"},
            {"platinum": 15, "quantity": 1, "subtype": "radiant"},
            {"platinum": 999, "quantity": 1, "subtype": "intact"},
        ]
        entries, matched = wfm_api._matching_entries(sell_orders, "radiant")
        self.assertTrue(matched)
        prices = sorted(e["price"] for e in entries)
        self.assertEqual(prices, [15, 20])  # intact listing correctly excluded

    def test_falls_back_to_any_subtype_when_no_exact_match(self):
        sell_orders = [{"platinum": 20, "quantity": 2, "subtype": "intact"}]
        entries, matched = wfm_api._matching_entries(sell_orders, "radiant")
        self.assertFalse(matched)
        self.assertEqual(len(entries), 1)


class TestRateLimiterThreadSafety(unittest.TestCase):
    """
    Review item #24: "one global rate limiter... rather than every
    component independently firing requests." This is the actual
    regression case: without a lock, two threads calling _throttle() at
    nearly the same moment can both read '_last_call' as stale, both pass
    the check, and both proceed immediately - silently violating the rate
    limit. This test drives real concurrent threads (not just a code
    read) through _throttle() and verifies no two calls ever land closer
    together than the configured minimum interval.
    """

    def test_concurrent_throttle_calls_stay_spaced_out(self):
        original_interval = wfm_api._MIN_INTERVAL
        # Shrink the interval for a fast test - the mechanism under test
        # (the lock) doesn't care about the actual magnitude.
        wfm_api._MIN_INTERVAL = 0.05
        wfm_api._last_call = 0.0
        try:
            timestamps = []
            timestamps_lock = threading.Lock()

            def call_throttle():
                wfm_api._throttle()
                with timestamps_lock:
                    timestamps.append(time.time())

            threads = [threading.Thread(target=call_throttle) for _ in range(8)]
            for t in threads:
                t.start()
            for t in threads:
                t.join(timeout=5)

            self.assertEqual(len(timestamps), 8, "all threads must complete")
            timestamps.sort()
            for earlier, later in zip(timestamps, timestamps[1:]):
                gap = later - earlier
                # Small tolerance for scheduler jitter - the important thing
                # is gaps cluster around MIN_INTERVAL, not near-zero (which
                # is what the race condition without a lock produces).
                self.assertGreaterEqual(
                    gap, wfm_api._MIN_INTERVAL - 0.02,
                    f"two calls landed {gap:.4f}s apart, below the "
                    f"{wfm_api._MIN_INTERVAL}s minimum - rate limit was not enforced",
                )
        finally:
            wfm_api._MIN_INTERVAL = original_interval
            wfm_api._last_call = 0.0


if __name__ == "__main__":
    unittest.main()

