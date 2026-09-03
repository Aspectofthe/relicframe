"""
test_http_client.py
Tests for http_client.py's fetch_many_order_books - the streaming,
error-isolated, concurrency-bounded batch fetch. Network calls themselves
are faked (subclassing to override get_full_order_book) since these tests
are about the CONCURRENCY/STREAMING CONTRACT, not real network behavior.

Run with:
    python -m unittest test_http_client.py -v
"""

import asyncio
import unittest

try:
    from http_client import WfmHttpClient
    _AIOHTTP_AVAILABLE = True
except ImportError:
    # aiohttp isn't installed in every environment these tests might run
    # in - skip gracefully rather than hard-failing the whole test module
    # to load. It IS a required dependency for actually running the bot
    # (see requirements.txt); this only affects running this test file in
    # an environment without it installed.
    _AIOHTTP_AVAILABLE = False
    WfmHttpClient = object


class FakeHttpClient(WfmHttpClient):
    """Overrides the actual network call with a controllable fake, so
    fetch_many_order_books's dispatch/streaming/error-isolation logic can
    be tested without touching the network."""

    def __init__(self, delays: dict[str, float], errors: set[str] = frozenset()):
        super().__init__(max_concurrent=50, max_per_second=1000.0)
        self.delays = delays
        self.errors = errors
        self.calls: list[str] = []

    async def get_full_order_book(self, slug: str) -> list[dict]:
        self.calls.append(slug)
        await asyncio.sleep(self.delays.get(slug, 0.0))
        if slug in self.errors:
            raise RuntimeError(f"simulated failure for {slug}")
        return [{"platinum": 10, "quantity": 1, "type": "sell", "slug": slug}]


@unittest.skipUnless(_AIOHTTP_AVAILABLE, "aiohttp not installed in this environment")
class TestFetchManyOrderBooks(unittest.IsolatedAsyncioTestCase):
    async def test_every_slug_gets_exactly_one_callback(self):
        client = FakeHttpClient(delays={})
        seen = []

        async def on_result(slug, orders, error):
            seen.append(slug)

        await client.fetch_many_order_books(["a", "b", "c"], on_result)
        self.assertEqual(sorted(seen), ["a", "b", "c"])

    async def test_results_stream_as_they_complete_not_in_dispatch_order(self):
        # "b" is dispatched second but finishes first (shorter delay) -
        # on_result must be called for "b" before "a" if this is genuinely
        # streaming (asyncio.as_completed) rather than waiting for the
        # whole batch and processing in original order.
        client = FakeHttpClient(delays={"a": 0.05, "b": 0.01})
        completion_order = []

        async def on_result(slug, orders, error):
            completion_order.append(slug)

        await client.fetch_many_order_books(["a", "b"], on_result)
        self.assertEqual(completion_order, ["b", "a"])

    async def test_one_slug_failing_does_not_abort_the_batch(self):
        client = FakeHttpClient(delays={}, errors={"bad_slug"})
        results = {}

        async def on_result(slug, orders, error):
            results[slug] = (orders, error)

        await client.fetch_many_order_books(["good1", "bad_slug", "good2"], on_result)
        self.assertEqual(len(results), 3)
        self.assertIsNone(results["bad_slug"][0])
        self.assertIsInstance(results["bad_slug"][1], RuntimeError)
        self.assertIsNotNone(results["good1"][0])
        self.assertIsNone(results["good1"][1])

    async def test_supports_plain_synchronous_callback(self):
        client = FakeHttpClient(delays={})
        seen = []

        def on_result(slug, orders, error):  # not async
            seen.append(slug)

        await client.fetch_many_order_books(["a", "b"], on_result)
        self.assertEqual(sorted(seen), ["a", "b"])

    async def test_full_order_list_preserved_not_truncated(self):
        client = FakeHttpClient(delays={})
        captured = {}

        async def on_result(slug, orders, error):
            captured[slug] = orders

        await client.fetch_many_order_books(["a"], on_result)
        self.assertEqual(captured["a"], [{"platinum": 10, "quantity": 1, "type": "sell", "slug": "a"}])

    async def test_empty_slug_list_completes_cleanly(self):
        client = FakeHttpClient(delays={})
        called = []

        async def on_result(slug, orders, error):
            called.append(slug)

        await client.fetch_many_order_books([], on_result)
        self.assertEqual(called, [])


if __name__ == "__main__":
    unittest.main()

