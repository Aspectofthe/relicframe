"""
state.py
Process-wide bot state: the loaded relic list and the current price
Snapshot. Fetching prices for ~768 relics takes minutes (rate-limited to
5 req/sec against Warframe Market), so - same as the desktop app - the
bot fetches once and slash commands read the cached Snapshot instantly,
rather than re-fetching on every command. Use /relics refresh to update it
on demand, or run the bot with --auto-refresh-minutes for a background loop.
"""

from __future__ import annotations

import asyncio
from workload import heavy_operation
import os
import time
from dataclasses import dataclass

from relic_data import load_relics_from_csv, Relic
import analysis
from analysis import Snapshot
from live_market import LiveMarket
from seller_blacklist import SellerBlacklist

RELIC_CSV_ENV = "RELIC_CSV_PATH"
_MODULE_DIR = os.path.dirname(os.path.abspath(__file__))
_LOCAL_RELIC_CSV = os.path.join(_MODULE_DIR, "relics_from_official_data.csv")
_PROJECT_RELIC_CSV = os.path.join(_MODULE_DIR, "data", "relics_from_official_data.csv")
_WORKSPACE_RELIC_CSV = os.path.normpath(
    os.path.join(_MODULE_DIR, "..", "All Txt Files", "relics_from_official_data.csv")
)
DEFAULT_RELIC_CSV = (
    _LOCAL_RELIC_CSV
    if os.path.isfile(_LOCAL_RELIC_CSV)
    else _PROJECT_RELIC_CSV
    if os.path.isfile(_PROJECT_RELIC_CSV)
    else _WORKSPACE_RELIC_CSV
)


@dataclass
class AutoRefreshConfig:
    """
    Runtime config for the recurring background refresh, set via
    `/relics refresh auto:Start` (or the --auto-refresh-minutes /
    --auto-refresh-channel-id startup flags). Deliberately holds no
    discord.py objects (no Task, no Channel) - this module stays
    discord-agnostic like the rest of state.py; bot.py owns the actual
    asyncio.Task and resolves channel_id to a real channel each cycle.
    """
    enabled: bool = False
    channel_id: int | None = None
    guild_id: int | None = None
    interval_minutes: float | None = None
    refinement: str | None = None
    force_catalog_refresh: bool = False
    last_run: float | None = None
    last_status: str | None = None  # "ok" | "error" | None (never run yet)
    last_error: str | None = None


class BotState:
    def __init__(self):
        self.relics: dict[str, Relic] = {}
        self.snapshot: Snapshot | None = None
        self.default_refinement = "radiant"
        self.default_trace_rate = 0.0
        self.auto_refresh = AutoRefreshConfig()
        self._fetch_lock = asyncio.Lock()
        self._last_fetch_started: float | None = None
        self._last_error: str | None = None

        # The live-market layer (async, full order books, bounded
        # concurrency, WebSocket + continuous reconciliation - see
        # live_market.py's module docstring for why this replaced the old
        # ~700s-average serialized fetch pipeline). Started once via
        # ensure_live_market_started(); after that, snapshot() below is a
        # synchronous, instant, in-memory read - no network call, no
        # multi-minute wait, on every single command.
        self.live_market: LiveMarket | None = None
        self._live_market_lock = asyncio.Lock()
        # Owned here, not by LiveMarket, so it's manageable via Discord
        # commands even before the live market has ever started, and so
        # it survives a LiveMarket restart intact (LiveMarket accepts it
        # as an injected dependency - see live_market.py).
        self.seller_blacklist = SellerBlacklist()

    def load_relics(self, path: str | None = None) -> int:
        path = path or os.environ.get(RELIC_CSV_ENV) or DEFAULT_RELIC_CSV
        if not os.path.isfile(path):
            raise FileNotFoundError(
                f"Relic CSV not found: {path}. Set {RELIC_CSV_ENV} to the full CSV path."
            )
        self.relics = load_relics_from_csv(path)
        return len(self.relics)

    async def ensure_live_market_started(
        self,
        force_catalog_refresh: bool = False,
        progress_cb=None,
        max_concurrent: int = 4,
        max_per_second: float = 5.0,
        sweep_interval_seconds: float = 300.0,
        enable_websocket: bool = False,
    ) -> LiveMarket:
        """
        Starts the live-market layer exactly once (bootstraps every
        required slug concurrently, then leaves the WebSocket +
        reconciliation loop running in the background). A concurrent
        caller waits for the SAME bootstrap rather than starting a second
        one - same "don't stack fetches" rule the old refresh() used.
        Safe to call repeatedly; a second call after the first succeeded
        is a no-op that just returns the already-running instance.
        """
        if not self.relics:
            self.load_relics()
        async with self._live_market_lock:
            if self.live_market is not None:
                return self.live_market
            self._last_fetch_started = time.time()
            self._last_error = None
            market = LiveMarket(
                max_concurrent=max_concurrent, max_per_second=max_per_second,
                sweep_interval_seconds=sweep_interval_seconds, enable_websocket=enable_websocket,
                seller_blacklist=self.seller_blacklist,
            )
            try:
                async with heavy_operation("relic bootstrap"):
                    await market.start(self.relics, force_catalog_refresh=force_catalog_refresh, progress_cb=progress_cb)
            except asyncio.CancelledError:
                # A refresh kill must also close a partially bootstrapped client.
                await market.stop()
                raise
            except Exception as e:  # noqa: BLE001
                self._last_error = str(e)
                await market.stop()
                raise
            self.live_market = market
            return market

    async def close(self) -> None:
        """Stop background pricing tasks and release network sessions cleanly."""
        async with self._live_market_lock:
            market = self.live_market
            self.live_market = None
            if market is not None:
                await market.stop()

    def build_snapshot(self, refinement: str | None = None) -> Snapshot:
        """
        Builds a Snapshot from the live market's CURRENT in-memory state -
        synchronous, no network call, effectively instant. This is the
        actual payoff of the live-market architecture: the old refresh()
        did a fresh multi-minute fetch on every call; this reads whatever
        the background bootstrap/reconciliation/WebSocket loop has already
        assembled, which is correct-as-of-right-now by construction.

        Named build_snapshot (not snapshot) deliberately - self.snapshot
        is the STORED Snapshot attribute bot.py already reads directly in
        several places; a method of the same name would shadow that
        attribute the moment it's assigned to, breaking every later read.
        """
        if self.live_market is None:
            raise RuntimeError(
                "Live market isn't running yet - an admin needs to run "
                "`/relics refresh` first to start it (one-time bootstrap)."
            )
        refinement = refinement or self.default_refinement
        snap = self.live_market.build_snapshot(self.relics, refinement)
        self.snapshot = snap
        return snap

    async def refresh(self, refinement: str | None = None, force_catalog_refresh: bool = False,
                       progress_cb=None) -> Snapshot:
        """
        Ensures the live market is running (starting it - the one-time
        bootstrap - if this is the first call), then returns an instant
        Snapshot built from its current state. After the first call, this
        is effectively free; the "refresh" a person asks for is already
        continuously happening in the background via the reconciliation
        loop and the WebSocket feed, not something each call has to redo
        from scratch.
        """
        refinement = refinement or self.default_refinement
        await self.ensure_live_market_started(force_catalog_refresh=force_catalog_refresh, progress_cb=progress_cb)
        # Lossless book decoding is CPU work; do not delay Discord heartbeats.
        return await asyncio.to_thread(self.build_snapshot, refinement)

    def require_snapshot(self) -> Snapshot:
        if self.snapshot is None:
            raise RuntimeError("No price data yet - an admin needs to run `/relics refresh` first.")
        return self.snapshot


state = BotState()
