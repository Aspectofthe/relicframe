"""
test_reconciler.py
Tests for reconciler.py - bootstrap() and run_forever()'s stalest-first
continuous sweep. Network is faked (subclassing WfmHttpClient), so these
test the reconciliation LOGIC (dedup targeting, failure isolation,
progress reporting), not real HTTP.

Run with:
    python -m unittest test_reconciler.py -v
"""

import asyncio
import unittest

try:
    from http_client import WfmHttpClient
    from order_book import OrderBookStore
    from reconciler import Reconciler
    _AIOHTTP_AVAILABLE = True
except ImportError:
    _AIOHTTP_AVAILABLE = False
    WfmHttpClient = object


class FakeHttpClient(WfmHttpClient):
    def __init__(self, errors: set[str] = frozenset()):
        super().__init__(max_concurrent=50, max_per_second=1000.0)
        self.errors = errors
        self.fetch_counts: dict[str, int] = {}

    async def get_full_order_book(self, slug: str) -> list[dict]:
        self.fetch_counts[slug] = self.fetch_counts.get(slug, 0) + 1
        if slug in self.errors:
            raise RuntimeError(f"simulated failure for {slug}")
        return [{"platinum": 10, "quantity": 1, "type": "sell", "slug": slug}]

    async def fetch_many_order_books(self, slugs, on_result):
        # Same streaming/error-isolation contract as the real client, just
        # sequential (no real concurrency needed for these tests) and
        # without touching the network.
        for slug in slugs:
            try:
                orders = await self.get_full_order_book(slug)
                result = on_result(slug, orders, None)
            except Exception as e:  # noqa: BLE001
                result = on_result(slug, None, e)
            if asyncio.iscoroutine(result):
                await result


@unittest.skipUnless(_AIOHTTP_AVAILABLE, "aiohttp not installed in this environment")
class TestBootstrap(unittest.IsolatedAsyncioTestCase):
    async def test_populates_store_for_every_required_slug(self):
        client = FakeHttpClient()
        store = OrderBookStore()
        required = {"slug_a": "Relic A", "slug_b": "Relic B"}
        recon = Reconciler(client, store, required)
        await recon.bootstrap()
        self.assertTrue(store.get("slug_a").is_bootstrapped)
        self.assertTrue(store.get("slug_b").is_bootstrapped)

    async def test_progress_callback_called_for_every_slug(self):
        client = FakeHttpClient()
        store = OrderBookStore()
        required = {"a": "A", "b": "B", "c": "C"}
        recon = Reconciler(client, store, required)
        calls = []
        await recon.bootstrap(progress_cb=lambda done, total: calls.append((done, total)))
        self.assertEqual(len(calls), 3)
        self.assertEqual(calls[-1], (3, 3))

    async def test_one_failing_slug_does_not_abort_bootstrap(self):
        client = FakeHttpClient(errors={"bad"})
        store = OrderBookStore()
        required = {"good": "Good", "bad": "Bad"}
        recon = Reconciler(client, store, required)
        await recon.bootstrap()
        self.assertTrue(store.get("good").is_bootstrapped)
        self.assertIn("bad", recon.bootstrap_failures)
        # A failed slug's book is never marked bootstrapped from bad data -
        # it stays untrusted until a later successful reconciliation.
        book = store.get("bad")
        self.assertTrue(book is None or not book.is_bootstrapped)

    async def test_sets_last_bootstrap_completed_timestamp(self):
        client = FakeHttpClient()
        store = OrderBookStore()
        recon = Reconciler(client, store, {"a": "A"})
        self.assertIsNone(recon.last_bootstrap_completed_at)
        await recon.bootstrap()
        self.assertIsNotNone(recon.last_bootstrap_completed_at)


@unittest.skipUnless(_AIOHTTP_AVAILABLE, "aiohttp not installed in this environment")
class TestRunForever(unittest.IsolatedAsyncioTestCase):
    async def test_sweeps_every_slug_at_least_once_given_enough_time(self):
        client = FakeHttpClient()
        store = OrderBookStore()
        required = {"a": "A", "b": "B", "c": "C"}
        recon = Reconciler(client, store, required)
        for slug in required:
            store.get_or_create(slug)  # register as tracked before sweeping

        task = asyncio.ensure_future(recon.run_forever(sweep_interval_seconds=0.03))
        await asyncio.sleep(0.2)
        recon.stop()
        try:
            await asyncio.wait_for(task, timeout=1.0)
        except asyncio.TimeoutError:
            task.cancel()

        for slug in required:
            self.assertGreaterEqual(client.fetch_counts.get(slug, 0), 1, f"{slug} was never reconciled")

    async def test_failing_slug_during_sweep_does_not_stop_the_loop(self):
        client = FakeHttpClient(errors={"bad"})
        store = OrderBookStore()
        required = {"bad": "Bad", "good": "Good"}
        recon = Reconciler(client, store, required)
        for slug in required:
            store.get_or_create(slug)

        task = asyncio.ensure_future(recon.run_forever(sweep_interval_seconds=0.02))
        await asyncio.sleep(0.15)
        recon.stop()
        try:
            await asyncio.wait_for(task, timeout=1.0)
        except asyncio.TimeoutError:
            task.cancel()

        # "good" must still have been reconciled despite "bad" repeatedly failing.
        self.assertGreaterEqual(client.fetch_counts.get("good", 0), 1)

    async def test_stop_actually_halts_the_loop(self):
        client = FakeHttpClient()
        store = OrderBookStore()
        store.get_or_create("a")
        recon = Reconciler(client, store, {"a": "A"})

        task = asyncio.ensure_future(recon.run_forever(sweep_interval_seconds=0.01))
        await asyncio.sleep(0.05)
        recon.stop()
        await asyncio.wait_for(task, timeout=1.0)
        self.assertTrue(task.done())


if __name__ == "__main__":
    unittest.main()

