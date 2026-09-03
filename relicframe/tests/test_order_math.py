"""
test_order_math.py
Tests for order_math.py - the pure order-normalization/matching logic
shared by wfm_api.py (synchronous) and the new async live-market layer.

Run with:
    python -m unittest test_order_math.py -v
"""

import unittest

from order_math import order_unit_price, order_entries, mark_outliers, best_matching_entry, matching_entries


class TestOrderUnitPrice(unittest.TestCase):
    def test_bulk_listing_normalized_per_item(self):
        self.assertAlmostEqual(order_unit_price({"platinum": 1000, "perTrade": 6}), 166.6667, places=3)

    def test_no_per_trade_passthrough(self):
        self.assertEqual(order_unit_price({"platinum": 45}), 45.0)

    def test_missing_platinum_is_none(self):
        self.assertIsNone(order_unit_price({}))


class TestOrderEntries(unittest.TestCase):
    def test_zero_quantity_excluded(self):
        entries = order_entries([{"platinum": 10, "quantity": 0}, {"platinum": 12, "quantity": 3}])
        self.assertEqual(len(entries), 1)
        self.assertEqual(entries[0]["price"], 12)

    def test_missing_quantity_not_excluded(self):
        entries = order_entries([{"platinum": 10}])
        self.assertEqual(len(entries), 1)
        self.assertIsNone(entries[0]["quantity"])


class TestMarkOutliers(unittest.TestCase):
    def test_lone_entry_always_flagged(self):
        result = mark_outliers([{"price": 10, "quantity": 1}])
        self.assertTrue(result[0]["is_outlier"])

    def test_far_above_median_flagged(self):
        entries = [{"price": 10, "quantity": 1}, {"price": 10, "quantity": 1}, {"price": 100, "quantity": 1}]
        result = mark_outliers(entries)
        self.assertFalse(result[0]["is_outlier"])
        self.assertTrue(result[2]["is_outlier"])

    def test_empty_list(self):
        self.assertEqual(mark_outliers([]), [])


class TestBestMatchingEntry(unittest.TestCase):
    def test_exact_subtype_preferred(self):
        orders = [
            {"platinum": 20, "quantity": 2, "subtype": "intact"},
            {"platinum": 50, "quantity": 1, "subtype": "radiant"},
        ]
        entry, matched = best_matching_entry(orders, "radiant")
        self.assertTrue(matched)
        self.assertEqual(entry["price"], 50)

    def test_falls_back_when_no_exact_match(self):
        orders = [{"platinum": 20, "quantity": 2, "subtype": "intact"}]
        entry, matched = best_matching_entry(orders, "radiant")
        self.assertFalse(matched)
        self.assertEqual(entry["price"], 20)

    def test_no_orders_returns_none(self):
        entry, matched = best_matching_entry([], "radiant")
        self.assertIsNone(entry)
        self.assertFalse(matched)


class TestMatchingEntries(unittest.TestCase):
    def test_returns_all_not_just_cheapest(self):
        orders = [
            {"platinum": 20, "quantity": 2, "subtype": "radiant"},
            {"platinum": 15, "quantity": 1, "subtype": "radiant"},
            {"platinum": 999, "quantity": 1, "subtype": "intact"},
        ]
        entries, matched = matching_entries(orders, "radiant")
        self.assertTrue(matched)
        self.assertEqual(sorted(e["price"] for e in entries), [15, 20])


class TestIsOrderFromOnlineUser(unittest.TestCase):
    def test_online_and_ingame_are_online(self):
        from order_math import is_order_from_online_user
        self.assertTrue(is_order_from_online_user({"user": {"status": "online"}}))
        self.assertTrue(is_order_from_online_user({"user": {"status": "ingame"}}))

    def test_offline_is_not_online(self):
        from order_math import is_order_from_online_user
        self.assertFalse(is_order_from_online_user({"user": {"status": "offline"}}))

    def test_missing_user_or_status_is_not_online(self):
        from order_math import is_order_from_online_user
        self.assertFalse(is_order_from_online_user({}))
        self.assertFalse(is_order_from_online_user({"user": {}}))


if __name__ == "__main__":
    unittest.main()

