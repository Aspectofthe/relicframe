"""
test_order_book.py
Tests for order_book.py.

Run with:
    python -m unittest test_order_book.py -v
"""

import time
import unittest

from order_book import OrderBook, OrderBookStore


class TestOrderBook(unittest.TestCase):
    def test_not_bootstrapped_until_full_fetch(self):
        book = OrderBook(slug="test_item")
        self.assertFalse(book.is_bootstrapped)
        book.replace_from_full_fetch([{"platinum": 10, "quantity": 1, "type": "sell"}])
        self.assertTrue(book.is_bootstrapped)

    def test_full_fetch_replaces_wholesale(self):
        book = OrderBook(slug="test_item")
        book.replace_from_full_fetch([{"platinum": 10, "quantity": 1, "type": "sell", "id": "a"}])
        book.replace_from_full_fetch([{"platinum": 20, "quantity": 1, "type": "sell", "id": "b"}])
        self.assertEqual(len(book.orders), 1)
        self.assertEqual(book.orders[0]["id"], "b")

    def test_ws_event_appends_new_order(self):
        book = OrderBook(slug="test_item")
        book.replace_from_full_fetch([{"platinum": 10, "quantity": 1, "type": "sell", "id": "a"}])
        book.apply_ws_order_created({"platinum": 5, "quantity": 1, "type": "sell", "id": "b"})
        self.assertEqual(len(book.orders), 2)

    def test_ws_event_does_not_duplicate_known_id(self):
        book = OrderBook(slug="test_item")
        book.replace_from_full_fetch([{"platinum": 10, "quantity": 1, "type": "sell", "id": "a"}])
        book.apply_ws_order_created({"platinum": 10, "quantity": 1, "type": "sell", "id": "a"})
        self.assertEqual(len(book.orders), 1)

    def test_ws_event_can_only_add_never_remove(self):
        # Confirms the documented limitation directly: nothing in this
        # class's public API can shrink .orders except a full refetch.
        book = OrderBook(slug="test_item")
        book.replace_from_full_fetch([
            {"platinum": 10, "quantity": 1, "type": "sell", "id": "a"},
            {"platinum": 20, "quantity": 1, "type": "sell", "id": "b"},
        ])
        book.apply_ws_order_created({"platinum": 30, "quantity": 1, "type": "sell", "id": "c"})
        self.assertEqual(len(book.orders), 3)

    def test_sell_orders_filters_out_buy_orders(self):
        book = OrderBook(slug="test_item")
        book.replace_from_full_fetch([
            {"platinum": 10, "quantity": 1, "type": "sell"},
            {"platinum": 5, "quantity": 1, "type": "buy"},
        ])
        self.assertEqual(len(book.sell_orders()), 1)

    def test_best_matching_entry_delegates_to_order_math(self):
        book = OrderBook(slug="test_item")
        book.replace_from_full_fetch([
            {"platinum": 20, "quantity": 2, "type": "sell", "subtype": "intact"},
            {"platinum": 50, "quantity": 1, "type": "sell", "subtype": "radiant"},
        ])
        entry, matched = book.best_matching_entry("radiant")
        self.assertTrue(matched)
        self.assertEqual(entry["price"], 50)

    def test_matching_entries_returns_full_sweep_list(self):
        book = OrderBook(slug="test_item")
        book.replace_from_full_fetch([
            {"platinum": 20, "quantity": 2, "type": "sell", "subtype": "radiant"},
            {"platinum": 15, "quantity": 1, "type": "sell", "subtype": "radiant"},
        ])
        entries, matched = book.matching_entries("radiant")
        self.assertTrue(matched)
        self.assertEqual(sorted(e["price"] for e in entries), [15, 20])

    def test_online_sell_orders_filters_by_user_status(self):
        book = OrderBook(slug="test_item")
        book.replace_from_full_fetch([
            {"platinum": 20, "quantity": 1, "type": "sell", "user": {"status": "online"}},
            {"platinum": 5, "quantity": 1, "type": "sell", "user": {"status": "offline"}},
        ])
        online = book.online_sell_orders()
        self.assertEqual(len(online), 1)
        self.assertEqual(online[0]["platinum"], 20)

    def test_best_matching_entry_online_excludes_offline_sellers(self):
        book = OrderBook(slug="test_item")
        book.replace_from_full_fetch([
            {"platinum": 5, "quantity": 1, "type": "sell", "subtype": "radiant", "user": {"status": "offline"}},
            {"platinum": 20, "quantity": 1, "type": "sell", "subtype": "radiant", "user": {"status": "online"}},
        ])
        # "Offline" (all sellers) channel picks the cheaper 5p listing.
        offline_entry, _ = book.best_matching_entry("radiant")
        self.assertEqual(offline_entry["price"], 5)
        # "Online" (currently-online only) channel must NOT pick the
        # offline seller's cheaper price - this is exactly the distinction
        # this project has always maintained between the two channels.
        online_entry, _ = book.best_matching_entry_online("radiant")
        self.assertEqual(online_entry["price"], 20)

    def test_matching_entries_online_excludes_offline_sellers(self):
        book = OrderBook(slug="test_item")
        book.replace_from_full_fetch([
            {"platinum": 5, "quantity": 1, "type": "sell", "user": {"status": "offline"}},
            {"platinum": 20, "quantity": 1, "type": "sell", "user": {"status": "ingame"}},
        ])
        entries, _ = book.matching_entries_online(None)
        self.assertEqual(len(entries), 1)
        self.assertEqual(entries[0]["price"], 20)


class TestOrderBookStore(unittest.TestCase):
    def test_get_or_create_returns_same_instance(self):
        store = OrderBookStore()
        book1 = store.get_or_create("slug_a")
        book2 = store.get_or_create("slug_a")
        self.assertIs(book1, book2)

    def test_get_returns_none_for_untracked_slug(self):
        store = OrderBookStore()
        self.assertIsNone(store.get("nonexistent"))

    def test_ws_event_ignored_for_untracked_slug(self):
        # The WS feed is global and mentions items we don't care about -
        # applying an event for a slug we never registered must be a
        # silent no-op, not create a new book out of nowhere.
        store = OrderBookStore()
        store.apply_ws_order_created("untracked_slug", {"platinum": 5, "quantity": 1, "type": "sell"})
        self.assertIsNone(store.get("untracked_slug"))

    def test_ws_event_applied_for_tracked_slug(self):
        store = OrderBookStore()
        store.get_or_create("slug_a")  # registers it as tracked
        store.apply_full_fetch("slug_a", [{"platinum": 10, "quantity": 1, "type": "sell", "id": "a"}])
        store.apply_ws_order_created("slug_a", {"platinum": 5, "quantity": 1, "type": "sell", "id": "b"})
        self.assertEqual(len(store.get("slug_a").orders), 2)

    def test_oldest_reconciled_slug_prefers_never_attempted(self):
        store = OrderBookStore()
        store.apply_full_fetch("fetched", [{"platinum": 1, "quantity": 1, "type": "sell"}])
        store.get_or_create("never_fetched")
        self.assertEqual(store.oldest_reconciled_slug(), "never_fetched")

    def test_oldest_reconciled_slug_picks_stalest_among_attempted(self):
        store = OrderBookStore()
        store.apply_full_fetch("newer", [{"platinum": 1, "quantity": 1, "type": "sell"}])
        time.sleep(0.01)
        store.apply_full_fetch("older_but_actually_fetched_first", [])
        # Manually backdate "older" to simulate genuine staleness ordering.
        store.get("newer").last_attempted = time.time()
        store.get("older_but_actually_fetched_first").last_attempted = time.time() - 1000
        self.assertEqual(store.oldest_reconciled_slug(), "older_but_actually_fetched_first")

    def test_repeatedly_failing_slug_does_not_starve_others(self):
        # This is the regression case: a slug that keeps FAILING (never
        # gets apply_full_fetch called) must not look permanently most-
        # overdue forever once it's been attempted at least once via
        # mark_attempted - otherwise it would hog oldest_reconciled_slug()
        # on every single call, starving every other tracked slug.
        store = OrderBookStore()
        store.get_or_create("always_fails")
        store.get_or_create("never_tried_yet")
        store.mark_attempted("always_fails")
        # "always_fails" was just attempted (recently), "never_tried_yet"
        # was never attempted at all - the never-attempted one is more
        # overdue and must be picked next.
        self.assertEqual(store.oldest_reconciled_slug(), "never_tried_yet")

    def test_empty_store_returns_none(self):
        store = OrderBookStore()
        self.assertIsNone(store.oldest_reconciled_slug())


class _FakeBlacklist:
    """Minimal stand-in for seller_blacklist.SellerBlacklist - avoids
    hitting disk in these tests, only needs is_blacklisted()."""

    def __init__(self, names):
        self._names = {n.lower() for n in names}

    def is_blacklisted(self, ingame_name):
        return bool(ingame_name) and ingame_name.lower() in self._names


class TestOrderBookStoreSellerBlacklist(unittest.TestCase):
    def test_no_blacklist_means_no_filtering(self):
        # Default (None) must behave exactly like before this feature
        # existed - every pre-existing caller/test constructs
        # OrderBookStore() with no blacklist.
        store = OrderBookStore()
        store.apply_full_fetch("slug_a", [
            {"platinum": 10, "quantity": 1, "type": "sell", "user": {"ingameName": "scammer1"}},
        ])
        self.assertEqual(len(store.get("slug_a").orders), 1)

    def test_full_fetch_drops_blacklisted_sellers_orders(self):
        store = OrderBookStore(blacklist=_FakeBlacklist(["scammer1"]))
        store.apply_full_fetch("slug_a", [
            {"platinum": 10, "quantity": 1, "type": "sell", "user": {"ingameName": "scammer1"}},
            {"platinum": 20, "quantity": 1, "type": "sell", "user": {"ingameName": "legit_trader"}},
        ])
        orders = store.get("slug_a").orders
        self.assertEqual(len(orders), 1)
        self.assertEqual(orders[0]["user"]["ingameName"], "legit_trader")

    def test_blacklist_check_is_case_insensitive(self):
        store = OrderBookStore(blacklist=_FakeBlacklist(["Scammer1"]))
        store.apply_full_fetch("slug_a", [
            {"platinum": 10, "quantity": 1, "type": "sell", "user": {"ingameName": "SCAMMER1"}},
        ])
        self.assertEqual(len(store.get("slug_a").orders), 0)

    def test_ws_event_from_blacklisted_seller_is_dropped(self):
        store = OrderBookStore(blacklist=_FakeBlacklist(["scammer1"]))
        store.get_or_create("slug_a")
        store.apply_ws_order_created("slug_a", {
            "platinum": 5, "quantity": 1, "type": "sell", "id": "x", "user": {"ingameName": "scammer1"},
        })
        self.assertEqual(len(store.get("slug_a").orders), 0)

    def test_ws_event_from_clean_seller_still_applies(self):
        store = OrderBookStore(blacklist=_FakeBlacklist(["scammer1"]))
        store.get_or_create("slug_a")
        store.apply_ws_order_created("slug_a", {
            "platinum": 5, "quantity": 1, "type": "sell", "id": "x", "user": {"ingameName": "legit_trader"},
        })
        self.assertEqual(len(store.get("slug_a").orders), 1)

    def test_purge_seller_removes_already_cached_orders_across_all_slugs(self):
        store = OrderBookStore(blacklist=_FakeBlacklist(["scammer1"]))
        # Populate BEFORE scammer1 was blacklisted (simulating: they were
        # already cached, then got blacklisted afterward) by fetching
        # with a store that has no blacklist yet, then swapping it in -
        # more simply, bypass filtering by writing directly to test the
        # purge path in isolation.
        store.get_or_create("slug_a").orders = [
            {"platinum": 10, "quantity": 1, "type": "sell", "user": {"ingameName": "scammer1"}},
            {"platinum": 20, "quantity": 1, "type": "sell", "user": {"ingameName": "legit_trader"}},
        ]
        store.get_or_create("slug_b").orders = [
            {"platinum": 15, "quantity": 1, "type": "sell", "user": {"ingameName": "scammer1"}},
        ]
        removed = store.purge_seller("scammer1")
        self.assertEqual(removed, 2)
        self.assertEqual(len(store.get("slug_a").orders), 1)
        self.assertEqual(len(store.get("slug_b").orders), 0)

    def test_purge_seller_without_a_blacklist_is_a_safe_no_op(self):
        store = OrderBookStore()
        store.get_or_create("slug_a").orders = [
            {"platinum": 10, "quantity": 1, "type": "sell", "user": {"ingameName": "anyone"}},
        ]
        self.assertEqual(store.purge_seller("anyone"), 0)
        self.assertEqual(len(store.get("slug_a").orders), 1)


if __name__ == "__main__":
    unittest.main()

