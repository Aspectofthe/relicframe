"""
live_market.py
The orchestrator: ties http_client + slug_registry + reconciler +
ws_client + OrderBookStore into one service, and builds an
analysis.Snapshot straight from the live in-memory state - synchronously,
with ZERO network calls, since by the time any command asks for it the
data is already there.

This is the actual fix for "700s average": the old pipeline did a fresh,
serialized, multi-minute fetch on every /relics refresh. This one does ONE
concurrent bootstrap fetch (see reconciler.Reconciler.bootstrap, dispatched
through http_client.WfmHttpClient with bounded concurrency instead of
one-request-at-a-time), then serves already-current data instantly from
then on, kept correct by a continuous background reconciliation sweep and
kept fresh between sweeps by the WebSocket feed.

Nothing here uses /top, /orders/recent, or any average/median-based price
substitution - every price comes from a full /orders/item/{slug} response,
scanned directly by order_book.py / order_math.py's pure matching logic.
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
import os
import time

import item_catalog_cache
import wfm_api
import order_math
from relic_data import Relic

from http_client import WfmHttpClient
from order_book import OrderBookStore
from reconciler import Reconciler
from seller_blacklist import SellerBlacklist
from slug_registry import build_required_slugs
from ws_client import WfmWebSocketClient

from analysis import Snapshot

logger = logging.getLogger("live_market.service")

CACHE_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), ".cache")
ITEM_CATALOG_CACHE_PATH = os.path.join(CACHE_DIR, "item_catalog.json")
DUCAT_MAP_CACHE_PATH = os.path.join(CACHE_DIR, "ducat_map.json")


class LiveMarket:
    def __init__(
        self,
        max_concurrent: int = 15,
        max_per_second: float = 5.0,
        sweep_interval_seconds: float = 300.0,
        enable_websocket: bool = False,
        http_client: WfmHttpClient | None = None,
        seller_blacklist: SellerBlacklist | None = None,
    ):
        self.http_client = http_client if http_client is not None else WfmHttpClient(
            max_concurrent=max_concurrent, max_per_second=max_per_second,
        )
        # Global (not per-guild) - see seller_blacklist.py's module
        # docstring for why: this filters orders at ingestion, before
        # they ever reach any guild's list. Injectable (same pattern as
        # http_client above) so BotState can own ONE instance that
        # survives across LiveMarket restarts instead of this reloading
        # a fresh one from disk every time the live market (re)starts.
        self.seller_blacklist = seller_blacklist if seller_blacklist is not None else SellerBlacklist()
        self.store = OrderBookStore(blacklist=self.seller_blacklist)
        self.reconciler: Reconciler | None = None
        self.ws_client: WfmWebSocketClient | None = None
        self.sweep_interval_seconds = sweep_interval_seconds
        self.enable_websocket = enable_websocket

        self.name_to_slug: dict[str, str] = {}
        self.id_to_slug: dict[str, str] = {}
        self.ducats: dict[str, int] = {}
        self.required_slugs: dict[str, str] = {}

        self._reconcile_task: asyncio.Task | None = None
        self._ws_task: asyncio.Task | None = None
        self.started_at: float | None = None
        self.bootstrap_seconds: float | None = None
        self.bootstrap_slug_count: int = 0

    async def start(self, relics: dict[str, Relic], force_catalog_refresh: bool = False, progress_cb=None) -> None:
        """
        One-time startup: resolve slugs, bootstrap every one concurrently,
        then start the background WebSocket + reconciliation loop tasks.
        Returns once the initial bootstrap completes - build_snapshot()
        is safe to call immediately after this returns.
        """
        os.makedirs(CACHE_DIR, exist_ok=True)
        await self.http_client.__aenter__()

        # Item catalog (id/slug/name metadata) and the ducat map are NOT
        # price data - they change rarely (new game content) and are still
        # fine to cache on disk. The "no caching" requirement in this
        # module is specifically about PRICE data, which is why every
        # price below comes from the live OrderBookStore instead.
        #
        # Both still go through wfm_api.py's SYNCHRONOUS, blocking
        # requests-based client (they're small, infrequent, cache-backed
        # calls - not worth a second async implementation) - but that
        # means they must be run via run_in_executor here, or they'd
        # block this event loop for their duration, defeating the entire
        # point of the async rewrite for what's usually a sub-second call
        # anyway, but wouldn't be on a cold cache / slow connection.
        loop = asyncio.get_running_loop()
        catalog = await loop.run_in_executor(
            None,
            lambda: item_catalog_cache.load_or_fetch(
                ITEM_CATALOG_CACHE_PATH, wfm_api.get_all_items, force_refresh=force_catalog_refresh,
            ),
        )
        items = catalog["items"]
        self.name_to_slug = wfm_api.build_name_to_slug_map(items)
        self.id_to_slug = wfm_api.build_id_to_slug_map(items)
        try:
            ducat_result = await loop.run_in_executor(
                None, lambda: item_catalog_cache.load_or_fetch_json(DUCAT_MAP_CACHE_PATH, wfm_api.get_ducat_map),
            )
            self.ducats = ducat_result["data"]
        except Exception:  # noqa: BLE001 - ducats are supplementary, never fatal
            pass

        self.required_slugs = build_required_slugs(relics, self.name_to_slug)
        self.bootstrap_slug_count = len(self.required_slugs)
        for slug in self.required_slugs:
            self.store.get_or_create(slug)

        self.reconciler = Reconciler(self.http_client, self.store, self.required_slugs)

        t0 = time.time()
        await self.reconciler.bootstrap(progress_cb=progress_cb)
        self.bootstrap_seconds = time.time() - t0
        self.started_at = time.time()
        logger.info(
            "Live market bootstrap complete: %d slugs in %.1fs (%d failed).",
            self.bootstrap_slug_count, self.bootstrap_seconds, len(self.reconciler.bootstrap_failures),
        )

        self._reconcile_task = asyncio.create_task(self.reconciler.run_forever(self.sweep_interval_seconds))

        if self.enable_websocket:
            self.ws_client = WfmWebSocketClient(
                self.store,
                set(self.required_slugs.keys()),
                self.id_to_slug,
            )
            await self.ws_client.__aenter__()
            self._ws_task = asyncio.create_task(self.ws_client.run_forever())

    async def stop(self) -> None:
        if self.reconciler:
            self.reconciler.stop()
        if self._reconcile_task:
            self._reconcile_task.cancel()
        if self._ws_task:
            self._ws_task.cancel()
        tasks = [task for task in (self._reconcile_task, self._ws_task) if task is not None]
        if self.ws_client:
            await self.ws_client.__aexit__(None, None, None)
        for task in tasks:
            with contextlib.suppress(asyncio.CancelledError):
                await task
        self._reconcile_task = None
        self._ws_task = None
        await self.http_client.__aexit__(None, None, None)

    def _relic_price_entry(self, relic_name: str, refinement: str) -> dict:
        """
        Builds the SAME shape analysis.Snapshot.relic_prices has always
        used (online/offline cost, quantity, subtype-match, outlier flag)
        so nothing downstream (relic_row.py, ranking.py, filtering.py, the
        old analysis.compute_row) needs to change - just the SOURCE of
        this data changes, from a fresh network fetch to the already-live
        OrderBookStore.
        """
        slug = wfm_api.find_relic_slug(self.name_to_slug, relic_name)
        empty = {
            "online": None, "online_quantity": None, "online_subtype_matched": True, "online_is_outlier": False,
            "offline_included": None, "offline_quantity": None, "offline_subtype_matched": True, "offline_is_outlier": False,
        }
        if not slug:
            return empty
        book = self.store.get(slug)
        if book is None or not book.is_bootstrapped:
            return empty  # never bootstrapped yet - report as no-price, never guess

        online_entry, online_matched = book.best_matching_entry_online(refinement)
        offline_entry, offline_matched = book.best_matching_entry(refinement)
        return {
            "online": online_entry["price"] if online_entry else None,
            "online_quantity": online_entry["quantity"] if online_entry else None,
            "online_subtype_matched": online_matched,
            "online_is_outlier": online_entry["is_outlier"] if online_entry else False,
            "offline_included": offline_entry["price"] if offline_entry else None,
            "offline_quantity": offline_entry["quantity"] if offline_entry else None,
            "offline_subtype_matched": offline_matched,
            "offline_is_outlier": offline_entry["is_outlier"] if offline_entry else False,
        }

    def _reward_price(self, reward_name: str) -> float | None:
        """
        Reward prices (used in expected-value math) have never had an
        online/offline split in this project - they use whichever price is
        available, same as the old get_price_batch. Here: the cheapest
        matching entry from the full order book, any subtype (rewards
        don't have refinement tiers the way relics do).
        """
        slug = self.name_to_slug.get(reward_name.strip().lower())
        if not slug:
            return None
        book = self.store.get(slug)
        if book is None or not book.is_bootstrapped:
            return None
        entry, _matched = book.best_matching_entry(None)
        return entry["price"] if entry else None

    def relic_matching_entries(self, relic_name: str, refinement: str, include_offline: bool) -> list[dict] | None:
        """
        The FULL, normalized sell-entry list for one relic at the given
        refinement - exactly what cheapest_combination_cost() needs
        ({"price", "quantity", "is_outlier"} entries, subtype-matched with
        fallback, straight from the live order book). include_offline=False
        for currently-online sellers only, True for every listed seller.
        Returns None if the relic has no resolvable slug or was never
        bootstrapped; an empty list if it's bootstrapped but genuinely has
        no live sell orders.

        This is what /relics buyn uses instead of a fresh network call -
        the data's already live in memory, and unlike the old /top-based
        path (wfm_api.get_relic_order_book's online branch) this is never
        capped to "the 5 best" - see that function's own docstring for the
        limitation this method does not share.
        """
        slug = wfm_api.find_relic_slug(self.name_to_slug, relic_name)
        if not slug:
            return None
        book = self.store.get(slug)
        if book is None or not book.is_bootstrapped:
            return None
        entries, _matched = (
            book.matching_entries_online(refinement) if not include_offline
            else book.matching_entries(refinement)
        )
        return entries

    def best_online_relic_order(self, relic_name: str, refinement: str) -> tuple[dict | None, bool]:
        """Return the raw cheapest currently-online sell order for a relic.

        The raw order is preserved so Discord can show the actual seller,
        quantity and WFM user slug for a purchase helper. Matching uses the
        same exact-refinement-first, any-subtype fallback rule as pricing.
        This is an in-memory read; it never makes a network request.
        """
        slug = wfm_api.find_relic_slug(self.name_to_slug, relic_name)
        if not slug:
            return None, False
        book = self.store.get(slug)
        if book is None or not book.is_bootstrapped:
            return None, False
        orders = [
            o for o in book.online_sell_orders()
            if order_math.order_unit_price(o) is not None and o.get("quantity") != 0
        ]
        if refinement:
            matched = [o for o in orders if (o.get("subtype") or "").lower() == refinement.lower()]
            if matched:
                return min(matched, key=lambda o: order_math.order_unit_price(o)), True
        if not orders:
            return None, False
        return min(orders, key=lambda o: order_math.order_unit_price(o)), not bool(refinement)

    def online_relic_has_listing(self, relic_name: str, refinement: str) -> bool:
        """True when the relic currently has a buyable online listing."""
        order, _matched = self.best_online_relic_order(relic_name, refinement)
        return order is not None

    def build_snapshot(self, relics: dict[str, Relic], refinement: str) -> Snapshot:
        """
        SYNCHRONOUS - builds a Snapshot straight from whatever the live
        OrderBookStore currently holds. No network call happens here; this
        is the whole point of the live-market architecture. Safe to call
        as often as you like (e.g. once per slash command) since it's just
        reading already-in-memory state.
        """
        from relic_data import all_reward_names

        reward_names = all_reward_names(relics)
        prices = {name.strip().lower(): self._reward_price(name) for name in reward_names}
        relic_prices = {name: self._relic_price_entry(name, refinement) for name in relics}

        unmatched = [n for n in reward_names if prices.get(n.strip().lower()) is None]
        no_relic_price = [n for n in relics if (relic_prices.get(n) or {}).get("online") is None]

        bootstrapped_count = sum(1 for slug in self.required_slugs if (self.store.get(slug) and self.store.get(slug).is_bootstrapped))
        total_count = max(1, len(self.required_slugs))
        catalog_source = "live" if bootstrapped_count == total_count else f"live ({bootstrapped_count}/{total_count} slugs ready)"

        return Snapshot(
            relics=relics,
            refinement=refinement,
            prices=prices,
            relic_prices=relic_prices,
            ducats=self.ducats,
            name_to_slug=self.name_to_slug,
            catalog_source=catalog_source,
            catalog_age_seconds=0.0,  # always current by construction - see module docstring
            unmatched_rewards=unmatched,
            no_relic_price=no_relic_price,
        )
