"""
http_client.py
Async, persistent-connection HTTP client for Warframe Market's REST API -
replaces wfm_api.py's synchronous `requests.Session()` + `time.sleep()`
throttle for the live-market layer.

Three things this fixes vs. the old pipeline:
- ONE aiohttp.ClientSession, connection-pooled and reused for every
  request (not a new connection per call).
- Requests go out with CONTROLLED CONCURRENCY (via rate_limiter.py's
  semaphore) instead of one at a time - many in flight together, still
  capped, instead of the whole pipeline serializing behind a single
  0.20s-per-request gate.
- fetch_many_order_books() calls back with each slug's result AS IT
  ARRIVES (via asyncio.as_completed), not after the whole batch finishes -
  so a caller building order books can start using early results
  immediately instead of waiting for the single slowest request in a
  batch of hundreds.

Every response here is the FULL, unmodified `data` list from
`/orders/item/{slug}` - every listing, both buy and sell, every subtype,
every user. No truncation to a "top N", no averaging, nothing dropped.
"""

from __future__ import annotations

import asyncio
import logging
from typing import Awaitable, Callable

import aiohttp

from rate_limiter import AsyncRateLimiter

logger = logging.getLogger("live_market.http_client")

BASE_URL = "https://api.warframe.market/v2"
HEADERS = {
    "User-Agent": "RelicFrame-Bot/1.0 (live-market)",
    "Accept": "application/json",
    "Platform": "pc",
    "Language": "en",
}
REQUEST_TIMEOUT_SECONDS = 15
MAX_RETRIES = 4
DEFAULT_429_BACKOFF_SECONDS = 2.0


class WfmHttpClient:
    def __init__(
        self,
        max_concurrent: int = 15,
        max_per_second: float = 5.0,
        session: aiohttp.ClientSession | None = None,
    ):
        """
        max_concurrent / max_per_second: tune these to how much load is
        acceptable against WFM. Higher max_concurrent with the SAME
        max_per_second still respects the rate ceiling (concurrency just
        controls how many requests can be queued/in-flight waiting for
        their turn, not how fast the ceiling itself allows them through) -
        so raising max_concurrent alone doesn't risk violating the rate
        limit, it just reduces how much time is spent with idle capacity
        waiting for a slot rather than waiting on the clock.
        """
        self._limiter = AsyncRateLimiter(max_concurrent=max_concurrent, max_per_second=max_per_second)
        self._session = session
        self._owns_session = session is None

    async def __aenter__(self) -> "WfmHttpClient":
        if self._session is None:
            connector = aiohttp.TCPConnector(limit=0)  # pool sizing is handled by our own limiter, not aiohttp's
            self._session = aiohttp.ClientSession(
                headers=HEADERS,
                connector=connector,
                timeout=aiohttp.ClientTimeout(total=REQUEST_TIMEOUT_SECONDS),
            )
        return self

    async def __aexit__(self, exc_type, exc, tb):
        if self._owns_session and self._session is not None:
            await self._session.close()
        return False

    async def _get(self, path: str, params: dict | None = None) -> dict:
        """GET with connection pooling plus server-aware 429 retry.

        The important part is that a 429 pauses the *shared* limiter before
        retrying. Without a shared pause, dozens of already-queued tasks can
        all continue generating 429s and turn a theoretically fast bootstrap
        into a much longer retry storm.
        """
        last_error = None
        for attempt in range(MAX_RETRIES + 1):
            try:
                async with self._limiter:
                    async with self._session.get(f"{BASE_URL}{path}", params=params) as resp:
                        if resp.status == 429:
                            retry_after = resp.headers.get("Retry-After")
                            try:
                                delay = float(retry_after) if retry_after else DEFAULT_429_BACKOFF_SECONDS * (2 ** attempt)
                            except ValueError:
                                delay = DEFAULT_429_BACKOFF_SECONDS * (2 ** attempt)
                            delay = min(max(delay, 1.0), 30.0)
                            await self._limiter.pause(delay)
                            last_error = RuntimeError(f"WFM rate limited (429); retrying in {delay:.1f}s")
                            continue
                        resp.raise_for_status()
                        return await resp.json()
            except (aiohttp.ClientConnectionError, asyncio.TimeoutError) as exc:
                last_error = exc
                if attempt >= MAX_RETRIES:
                    raise
                await asyncio.sleep(min(0.5 * (2 ** attempt), 5.0))
        raise last_error or RuntimeError("WFM request failed")

    async def get_all_items(self) -> list[dict]:
        """Item catalog - metadata (names/slugs/ids), not price data. Rarely
        changes; still fine to cache on disk (item_catalog_cache.py) since
        the 'no caching' requirement is specifically about PRICE data, not
        about which items exist."""
        data = await self._get("/items")
        return data.get("data", [])

    async def get_full_order_book(self, slug: str) -> list[dict]:
        """
        The FULL, unmodified order list for one slug - every listing
        exactly as the API returns it, buy and sell, every subtype, every
        user. This is what "no top-10, no bulk-average replacement" means
        in practice: every derived number (cheapest match, the full
        cheapest-combination sweep) is computed by order_book.py /
        order_math.py from this complete list, never a pre-truncated or
        pre-averaged subset.
        """
        data = await self._get(f"/orders/item/{slug}")
        return data.get("data", [])

    async def fetch_many_order_books(
        self,
        slugs: list[str],
        on_result: Callable[[str, list[dict] | None, Exception | None], Awaitable[None] | None],
    ) -> None:
        """
        Dispatches a full-order-book fetch for every slug in `slugs`,
        bounded by this client's concurrency/rate limiter (so this is safe
        to call with hundreds or thousands of slugs at once - it doesn't
        blast them all onto the wire simultaneously, the limiter still
        governs actual concurrency and rate).

        Calls `on_result(slug, orders, error)` as EACH request completes,
        via asyncio.as_completed - not after the whole batch finishes.
        Exactly one of `orders` / `error` is None. `on_result` may be a
        plain function or an async function; both are supported so a
        caller storing results into an OrderBookStore (a synchronous
        operation) doesn't need to wrap every call in a no-op coroutine.

        A single slug's failure never aborts the batch - it's reported via
        `error` and every other slug keeps going, since one bad/renamed
        slug shouldn't stall pricing for everything else.
        """
        async def _one(slug: str):
            try:
                orders = await self.get_full_order_book(slug)
                return slug, orders, None
            except Exception as e:  # noqa: BLE001 - reported per-slug, not raised
                return slug, None, e

        tasks = [asyncio.ensure_future(_one(slug)) for slug in slugs]
        for coro in asyncio.as_completed(tasks):
            slug, orders, error = await coro
            result = on_result(slug, orders, error)
            if asyncio.iscoroutine(result):
                await result
