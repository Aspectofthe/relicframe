"""
reconciler.py
Two jobs, both built on the SAME http_client + OrderBookStore:

1. bootstrap() - the initial full population of every tracked slug's order
   book. This is what replaces the old ~700s serialized pipeline: every
   unique slug (see slug_registry.py) is fetched with bounded concurrency
   via http_client.fetch_many_order_books, each one applied into the store
   the moment it arrives (not after the whole batch finishes).

2. run_forever() - an ongoing background sweep that continuously refetches
   the SINGLE STALEST tracked slug, then waits just long enough that the
   whole tracked set gets swept roughly once every `sweep_interval_seconds`
   on average. This is the "periodically reconcile the complete books"
   requirement - the WS feed (ws_client.py) can only ever ADD orders it
   sees go live; it has no way to know about an order that got sold,
   cancelled, or edited elsewhere, so without this loop every OrderBook
   would silently accumulate stale listings forever. Spreading the sweep
   continuously (one slug at a time, evenly paced) instead of doing the
   whole set in one periodic burst keeps load smooth and means recently-
   bootstrapped slugs don't all go stale in lockstep.
"""

from __future__ import annotations

import asyncio
import logging

from http_client import WfmHttpClient
from order_book import OrderBookStore

logger = logging.getLogger("live_market.reconciler")


class Reconciler:
    def __init__(self, http_client: WfmHttpClient, store: OrderBookStore, required_slugs: dict[str, str]):
        """required_slugs: {slug: label}, as returned by
        slug_registry.build_required_slugs() - the label is used only for
        logging, never for lookups."""
        self.http_client = http_client
        self.store = store
        self.required_slugs = required_slugs
        self._stopped = False
        self.last_bootstrap_completed_at: float | None = None
        self.bootstrap_failures: dict[str, str] = {}  # slug -> error message, for the caller to surface

    async def bootstrap(self, progress_cb=None) -> None:
        """
        Fetches every tracked slug's full order book once, concurrently
        (bounded by http_client's own limiter), applying each result into
        the store AS IT ARRIVES. progress_cb(done, total) is called after
        each completion if provided; safe to pass None.

        A slug that fails to fetch is recorded in self.bootstrap_failures
        (and left un-bootstrapped in the store - see OrderBook.is_bootstrapped)
        rather than aborting the whole run; run_forever() will keep
        retrying it on its normal rotation afterward.
        """
        import time
        self.bootstrap_failures = {}
        total = len(self.required_slugs)
        done = 0

        async def on_result(slug: str, orders: list[dict] | None, error: Exception | None):
            nonlocal done
            if error is not None:
                self.bootstrap_failures[slug] = str(error)
                logger.warning("Bootstrap fetch failed for slug=%s (%s): %s", slug, self.required_slugs.get(slug), error)
            else:
                self.store.apply_full_fetch(slug, orders)
            done += 1
            if progress_cb:
                try:
                    progress_cb(done, total)
                except Exception:  # noqa: BLE001 - progress reporting must never abort a bootstrap
                    pass

        await self.http_client.fetch_many_order_books(list(self.required_slugs.keys()), on_result)
        self.last_bootstrap_completed_at = time.time()

    async def run_forever(self, sweep_interval_seconds: float = 300.0) -> None:
        """
        Continuously refetches the single stalest tracked slug (via
        OrderBookStore.oldest_reconciled_slug - never-bootstrapped slugs
        are always most overdue), then sleeps long enough that the full
        set gets swept roughly once every sweep_interval_seconds. Runs
        until stop() is called.

        A never-tracked / empty registry just idles (sleeps and rechecks)
        rather than erroring - lets this be started before bootstrap() has
        necessarily populated anything, or safely alongside an empty
        relic list during startup.
        """
        while not self._stopped:
            slug = self.store.oldest_reconciled_slug()
            if slug is None or not self.required_slugs:
                await asyncio.sleep(min(sweep_interval_seconds, 30.0))
                continue

            try:
                orders = await self.http_client.get_full_order_book(slug)
                self.store.apply_full_fetch(slug, orders)
                self.bootstrap_failures.pop(slug, None)
            except Exception as e:  # noqa: BLE001 - one bad slug must never stop the sweep
                self.bootstrap_failures[slug] = str(e)
                self.store.mark_attempted(slug)  # still counts as "just tried" - see mark_attempted's docstring
                logger.warning("Reconciliation fetch failed for slug=%s: %s", slug, e)

            per_slug_wait = sweep_interval_seconds / max(1, len(self.required_slugs))
            await asyncio.sleep(per_slug_wait)

    def stop(self) -> None:
        self._stopped = True

