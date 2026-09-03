import asyncio
import gc
import json
from pathlib import Path
import random
import tempfile
import unittest
from unittest.mock import AsyncMock, patch
import weakref

from auction_pool import AuctionPool
from companion_appraisal import CompanionAppraiser
from http_client import WfmHttpClient
from order_book import OrderBookStore
from riven_market import RivenMarketService
from riven_trade_chat import RivenTradeChatLog
from workload import heavy_operation


class TestCompressedBooks(unittest.TestCase):
    def test_all_fields_quantities_and_price_calculations_are_preserved(self):
        rng = random.Random(42)
        rows = [{"id": str(i), "type": rng.choice(["buy", "sell"]),
                 "platinum": rng.randint(0, 1000), "quantity": rng.randint(1, 40),
                 "perTrade": rng.choice([None, 1, 2, 5]), "subtype": rng.choice([None, "radiant", "intact"]),
                 "user": {"ingameName": f"Seller{i}", "status": rng.choice(["offline", "online", "ingame"])},
                 "unused_future_api_field": {"unicode": "原始数据", "numbers": [1.234567890123, None, True]}}
                for i in range(500)]
        raw, compact = OrderBookStore(), OrderBookStore(compressed=True)
        for store in (raw, compact):
            store.apply_full_fetch("test", rows)
        a, b = raw.get("test"), compact.get("test")
        self.assertEqual(a.orders, b.orders)
        for subtype in (None, "radiant", "intact", "missing"):
            for method in ("best_matching_entry", "best_matching_entry_online", "matching_entries", "matching_entries_online"):
                self.assertEqual(getattr(a, method)(subtype), getattr(b, method)(subtype), method)
        update = dict(rows[0], id="new")
        for store in (raw, compact):
            store.apply_ws_order_created("test", update)
            store.apply_ws_order_created("test", update)
        self.assertEqual(a.orders, b.orders)
        self.assertEqual(len(b.orders), 501)

    def test_compressed_blacklist_purge_and_reconciliation(self):
        class Blacklist:
            blocked = False
            def is_blacklisted(self, name):
                return self.blocked and name == "bad"
        blacklist = Blacklist()
        store = OrderBookStore(blacklist, compressed=True)
        rows = [{"id": "1", "user": {"ingameName": "bad"}}, {"id": "2", "user": {"ingameName": "good"}}]
        store.apply_full_fetch("a", rows)
        blacklist.blocked = True
        self.assertEqual(store.purge_seller("bad"), 1)
        self.assertEqual(store.get("a").orders, [rows[1]])
        store.apply_full_fetch("a", [])
        self.assertEqual(store.get("a").orders, [])


class TestWorkerMemory(unittest.IsolatedAsyncioTestCase):
    async def test_completed_responses_are_released_during_bootstrap(self):
        class Payload(list):
            pass
        refs, live_counts = [], []
        client = WfmHttpClient(max_concurrent=3)
        async def fetch(slug):
            await asyncio.sleep(0)
            payload = Payload([{"id": slug}])
            refs.append(weakref.ref(payload))
            return payload
        def callback(slug, rows, error):
            live_counts.append(sum(ref() is not None for ref in refs))
        with patch.object(client, "get_full_order_book", side_effect=fetch):
            await client.fetch_many_order_books([str(i) for i in range(100)], callback)
        gc.collect()
        self.assertEqual(len(refs), 100)
        self.assertLessEqual(max(live_counts), 3)
        self.assertFalse(any(ref() is not None for ref in refs))

    async def test_heavy_phases_do_not_overlap(self):
        active, peak = 0, 0
        async def run():
            nonlocal active, peak
            async with heavy_operation("test"):
                active += 1
                peak = max(peak, active)
                await asyncio.sleep(0)
                active -= 1
        await asyncio.gather(run(), run(), run())
        self.assertEqual(peak, 1)

    async def test_auction_cache_is_bounded(self):
        service = RivenMarketService.__new__(RivenMarketService)
        service._auction_cache = {}
        service._get_json = AsyncMock(return_value={"payload": {"auctions": [{"id": "1"}]}})
        for index in range(12):
            await service.auctions_for(str(index), session=object())
        self.assertEqual(len(service._auction_cache), 4)
        self.assertEqual(list(service._auction_cache), ["8", "9", "10", "11"])

    async def test_failed_auction_scan_cleans_up_spool(self):
        service = RivenMarketService.__new__(RivenMarketService)
        service._get_json = AsyncMock(side_effect=RuntimeError("offline"))
        pool = AuctionPool()
        directory = Path(pool._temp.name)
        with patch("riven_market.AuctionPool", return_value=pool):
            with self.assertRaisesRegex(RuntimeError, "Every cross-weapon"):
                await service.auctions_across_market(object())
        self.assertFalse(directory.exists())

    async def test_history_quota_preserves_old_archive_and_updates_latest(self):
        with tempfile.TemporaryDirectory() as temp:
            service = RivenMarketService(temp)
            service.history_dir.mkdir(parents=True)
            old = service.history_dir / "old.json"
            old.write_text('{"preserve": true}', encoding="utf-8")
            service._get_json = AsyncMock(side_effect=[[], {"data": [{"slug": "torid"}]}])
            with patch("riven_market.HISTORY_BUDGET_BYTES", 1):
                await service.refresh(force=True)
            self.assertEqual(old.read_text(encoding="utf-8"), '{"preserve": true}')
            self.assertEqual(list(service.history_dir.glob("*.json")), [old])
            self.assertTrue((Path(temp) / "latest.json").exists())


class TestAuctionSpool(unittest.TestCase):
    def test_lossless_rows_dedup_and_family_migration(self):
        pool = AuctionPool()
        directory = Path(pool._temp.name)
        try:
            a = {"id": "a", "item": {"type": "riven", "weapon_url_name": "Torid"}, "rolls": [125.123456, "🔥"]}
            b = {"id": "b", "item": {"weapon_url_name": "torid"}, "buyout_price": 2}
            pool.add([a, b])
            self.assertEqual(pool["torid"], [a, b])
            replacement = dict(a, item={"weapon_url_name": "braton"})
            pool.add([replacement])
            self.assertEqual(pool["torid"], [b])
            self.assertEqual(pool["braton"], [replacement])
            self.assertEqual(pool.get("missing", []), [])
            self.assertEqual(len(pool), 2)
            pool.add([dict(b, item={"type": "other", "weapon_url_name": "torid"})])
            self.assertNotIn("torid", pool)
            self.assertEqual(pool.db.execute("PRAGMA max_page_count").fetchone()[0], 16384)
        finally:
            pool.close()
        self.assertFalse(directory.exists())


class TestEvidenceMemory(unittest.TestCase):
    def test_companion_compaction_preserves_dedup_price_traits_and_asset_presence(self):
        row = {"text": "A sale " * 1000, "message_id": "1", "timestamp": "2026-01-01",
               "traits": {"pattern": ["lotus"]}, "amounts": [{"low": 100, "high": 200}],
               "classification": "listing", "attachments": [{"url": "https://example.com/p.png", "filename": "p.png"}]}
        newer = dict(row, timestamp="2026-02-01", amounts=[{"low": 200, "high": 300}])
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "evidence.jsonl"
            source = "\n".join(json.dumps(x) for x in (row, newer))
            path.write_text(source, encoding="utf-8")
            appraiser = CompanionAppraiser(path)
            self.assertEqual(len(appraiser.records), 1)
            record = appraiser.records[0]
            self.assertNotIn("text", record)
            self.assertTrue(record["attachments"])
            self.assertEqual(record["amounts"], newer["amounts"])
            self.assertEqual(appraiser.appraise(pattern="lotus").estimate, 250)
            self.assertEqual(path.read_text(encoding="utf-8"), source)

    def test_chat_budget_rejects_import_without_modifying_existing_data(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "chat.jsonl"
            log = RivenTradeChatLog(path, ["Torid"])
            log.import_text("Alice: WTS [Torid Riven] 100p")
            original = path.read_bytes()
            with patch("riven_trade_chat.TRADE_CHAT_BUDGET_BYTES", len(original)):
                with self.assertRaisesRegex(RuntimeError, "budget"):
                    log.import_text("Bob: WTS [Torid Riven] 200p")
            self.assertEqual(path.read_bytes(), original)
            self.assertEqual(len(log.recent()), 1)


if __name__ == "__main__":
    unittest.main()
