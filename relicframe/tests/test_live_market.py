"""
test_live_market.py
Tests for live_market.py's build_snapshot() and its private helpers -
bypasses start()'s real network calls (item catalog / ducat map / actual
bootstrap) by constructing a LiveMarket and populating its store directly,
so these tests exercise the actual Snapshot-building logic without
touching the network.

Run with:
    python -m unittest test_live_market.py -v
"""

import asyncio
import unittest
from types import SimpleNamespace

try:
    from live_market import LiveMarket
    from http_client import WfmHttpClient
    _AIOHTTP_AVAILABLE = True
except ImportError:
    _AIOHTTP_AVAILABLE = False
    WfmHttpClient = object

from relic_data import Relic, RelicReward


def _relics():
    return {
        "Lith A1": Relic("Lith A1", rewards=[
            RelicReward("Common A", "common"), RelicReward("Common B", "common"),
            RelicReward("Common C", "common"), RelicReward("Uncommon A", "uncommon"),
            RelicReward("Uncommon B", "uncommon"), RelicReward("Rare A", "rare"),
        ]),
    }


@unittest.skipUnless(_AIOHTTP_AVAILABLE, "aiohttp not installed in this environment")
class TestBuildSnapshot(unittest.TestCase):
    def setUp(self):
        self.market = LiveMarket(http_client=WfmHttpClient(max_concurrent=1, max_per_second=1.0))
        self.market.name_to_slug = {
            "lith a1 relic": "lith_a1_relic",
            "common a": "common_a", "common b": "common_b", "common c": "common_c",
            "uncommon a": "uncommon_a", "uncommon b": "uncommon_b", "rare a": "rare_a",
        }
        self.market.required_slugs = {
            "lith_a1_relic": "Lith A1", "common_a": "Common A", "rare_a": "Rare A",
        }

    def test_never_bootstrapped_relic_reports_no_price(self):
        # No store entries populated at all - must report as unknown, never guess.
        snap = self.market.build_snapshot(_relics(), "radiant")
        self.assertIsNone(snap.relic_prices["Lith A1"]["online"])
        self.assertIn("Lith A1", snap.no_relic_price)

    def test_bootstrapped_relic_reports_online_and_offline_split(self):
        self.market.store.apply_full_fetch("lith_a1_relic", [
            {"platinum": 50, "quantity": 1, "type": "sell", "subtype": "radiant", "user": {"status": "online"}},
            {"platinum": 10, "quantity": 1, "type": "sell", "subtype": "radiant", "user": {"status": "offline"}},
        ])
        snap = self.market.build_snapshot(_relics(), "radiant")
        entry = snap.relic_prices["Lith A1"]
        # "offline" (all sellers) picks the cheaper 10p listing.
        self.assertEqual(entry["offline_included"], 10)
        # "online" (currently-online only) must NOT see the cheaper offline price.
        self.assertEqual(entry["online"], 50)

    def test_reward_price_uses_any_subtype_cheapest(self):
        self.market.store.apply_full_fetch("rare_a", [
            {"platinum": 60, "quantity": 1, "type": "sell", "user": {"status": "online"}},
        ])
        snap = self.market.build_snapshot(_relics(), "radiant")
        self.assertEqual(snap.prices["rare a"], 60.0)

    def test_reward_never_bootstrapped_is_none_not_zero(self):
        snap = self.market.build_snapshot(_relics(), "radiant")
        self.assertIsNone(snap.prices["common a"])
        self.assertIn("Common A", snap.unmatched_rewards)

    def test_catalog_age_seconds_is_zero_always_current(self):
        # Live data is current by construction (no cache TTL) - see module docstring.
        snap = self.market.build_snapshot(_relics(), "radiant")
        self.assertEqual(snap.catalog_age_seconds, 0.0)

    def test_relic_with_no_resolvable_slug_reports_no_price(self):
        self.market.name_to_slug = {}  # nothing resolves
        snap = self.market.build_snapshot(_relics(), "radiant")
        self.assertIsNone(snap.relic_prices["Lith A1"]["online"])


@unittest.skipUnless(_AIOHTTP_AVAILABLE, "aiohttp not installed in this environment")
class TestRelicMatchingEntries(unittest.TestCase):
    def setUp(self):
        self.market = LiveMarket(http_client=WfmHttpClient(max_concurrent=1, max_per_second=1.0))
        self.market.name_to_slug = {"lith a1 relic": "lith_a1_relic"}

    def test_none_for_unresolvable_relic(self):
        self.assertIsNone(self.market.relic_matching_entries("Nonexistent", "radiant", True))

    def test_none_for_never_bootstrapped_relic(self):
        self.assertIsNone(self.market.relic_matching_entries("Lith A1", "radiant", True))

    def test_offline_true_includes_all_sellers(self):
        self.market.store.apply_full_fetch("lith_a1_relic", [
            {"platinum": 50, "quantity": 1, "type": "sell", "subtype": "radiant", "user": {"status": "online"}},
            {"platinum": 10, "quantity": 1, "type": "sell", "subtype": "radiant", "user": {"status": "offline"}},
        ])
        entries = self.market.relic_matching_entries("Lith A1", "radiant", True)
        self.assertEqual(sorted(e["price"] for e in entries), [10, 50])

    def test_offline_false_excludes_offline_sellers(self):
        self.market.store.apply_full_fetch("lith_a1_relic", [
            {"platinum": 50, "quantity": 1, "type": "sell", "subtype": "radiant", "user": {"status": "online"}},
            {"platinum": 10, "quantity": 1, "type": "sell", "subtype": "radiant", "user": {"status": "offline"}},
        ])
        entries = self.market.relic_matching_entries("Lith A1", "radiant", False)
        self.assertEqual([e["price"] for e in entries], [50])

    def test_never_capped_to_five_unlike_old_top_endpoint(self):
        orders = [
            {"platinum": float(i), "quantity": 1, "type": "sell", "subtype": "radiant", "user": {"status": "online"}}
            for i in range(1, 11)
        ]
        self.market.store.apply_full_fetch("lith_a1_relic", orders)
        entries = self.market.relic_matching_entries("Lith A1", "radiant", False)
        self.assertEqual(len(entries), 10)

    def test_best_online_relic_order_preserves_seller_and_quantity(self):
        self.market.store.apply_full_fetch("lith_a1_relic", [
            {"platinum": 60, "quantity": 4, "type": "sell", "subtype": "radiant",
             "user": {"status": "online", "ingameName": "SellerA", "slug": "sellera"}},
            {"platinum": 40, "quantity": 2, "type": "sell", "subtype": "intact",
             "user": {"status": "online", "ingameName": "SellerB", "slug": "sellerb"}},
        ])
        order, matched = self.market.best_online_relic_order("Lith A1", "radiant")
        self.assertTrue(matched)
        self.assertEqual(order["user"]["ingameName"], "SellerA")
        self.assertEqual(order["quantity"], 4)


@unittest.skipUnless(_AIOHTTP_AVAILABLE, "aiohttp not installed in this environment")
class TestLiveMarketShutdown(unittest.IsolatedAsyncioTestCase):
    async def test_stop_cancels_and_awaits_background_tasks(self):
        class FakeHttpClient:
            closed = False

            async def __aexit__(self, *_args):
                self.closed = True

        class FakeWsClient:
            closed = False

            async def __aexit__(self, *_args):
                self.closed = True

        stopped = {"value": False}

        async def sleeper():
            await asyncio.Event().wait()

        http = FakeHttpClient()
        ws = FakeWsClient()
        market = LiveMarket(http_client=http)
        market.reconciler = SimpleNamespace(stop=lambda: stopped.__setitem__("value", True))
        market.ws_client = ws
        reconcile_task = asyncio.create_task(sleeper())
        ws_task = asyncio.create_task(sleeper())
        market._reconcile_task = reconcile_task
        market._ws_task = ws_task

        await market.stop()

        self.assertTrue(stopped["value"])
        self.assertTrue(reconcile_task.done())
        self.assertTrue(ws_task.done())
        self.assertTrue(http.closed)
        self.assertTrue(ws.closed)
        self.assertIsNone(market._reconcile_task)
        self.assertIsNone(market._ws_task)



if __name__ == "__main__":
    unittest.main()
