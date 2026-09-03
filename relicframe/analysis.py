"""
analysis.py
Headless version of RelicFrame's main.py pipeline (fetch -> compute rows ->
filter -> sort), with no tkinter dependency, so it can run inside a Discord
bot's event loop (via a thread executor) instead of a desktop app.

Mirrors main.py's logic 1:1 (same functions from relic_data.py / wfm_api.py,
same "one shared calculation path for both channels" rule, same filter/sort
semantics) so the bot's numbers never disagree with what the desktop app
would show for the same inputs.
"""

from __future__ import annotations

import os
import time
from dataclasses import dataclass, field

import wfm_api
import item_catalog_cache
from relic_data import Relic, all_reward_names
import relic_row
import ranking
import filtering

CACHE_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), ".cache")
ITEM_CATALOG_CACHE_PATH = os.path.join(CACHE_DIR, "item_catalog.json")
DUCAT_MAP_CACHE_PATH = os.path.join(CACHE_DIR, "ducat_map.json")

RANK_MODES = ranking.RANK_MODES


@dataclass
class Snapshot:
    """Everything a fetch cycle produces, frozen together so a query never
    mixes prices from two different fetch cycles."""
    relics: dict[str, Relic]
    refinement: str
    prices: dict[str, float | None]
    relic_prices: dict[str, dict]
    ducats: dict[str, int]
    name_to_slug: dict[str, str]
    catalog_source: str
    catalog_age_seconds: float | None
    fetched_at: float = field(default_factory=time.time)
    unmatched_rewards: list[str] = field(default_factory=list)
    no_relic_price: list[str] = field(default_factory=list)


def fetch_snapshot(
    relics: dict[str, Relic],
    refinement: str,
    force_catalog_refresh: bool = False,
    progress_cb=None,
) -> Snapshot:
    """
    Blocking, synchronous fetch of everything needed to price every relic
    at the given refinement - same steps as main.py's _analyze_worker_body:
    item catalog (cached), ducat map (cached, best-effort), one shared
    /orders/recent snapshot, then reward prices and relic prices (online +
    offline) batched through that snapshot with per-item fallback.

    progress_cb(stage: str, done: int, total: int) is called periodically;
    safe to pass None. Call this from a thread executor, never directly on
    an asyncio event loop - it uses blocking sleeps for rate limiting.
    """
    os.makedirs(CACHE_DIR, exist_ok=True)

    def _prog(stage, done, total):
        if progress_cb:
            try:
                progress_cb(stage, done, total)
            except Exception:  # noqa: BLE001 - progress reporting must never abort a fetch
                pass

    _prog("catalog", 0, 1)
    catalog = item_catalog_cache.load_or_fetch(
        ITEM_CATALOG_CACHE_PATH, wfm_api.get_all_items, force_refresh=force_catalog_refresh,
    )
    items = catalog["items"]
    name_to_slug = wfm_api.build_name_to_slug_map(items)
    id_to_slug = wfm_api.build_id_to_slug_map(items)

    ducats: dict[str, int] = {}
    try:
        ducat_result = item_catalog_cache.load_or_fetch_json(DUCAT_MAP_CACHE_PATH, wfm_api.get_ducat_map)
        ducats = ducat_result["data"]
    except Exception:  # noqa: BLE001 - ducats are supplementary, never fatal
        pass

    _prog("recent_orders", 0, 1)
    try:
        recent_orders = wfm_api.get_recent_orders()
        recent_index = wfm_api.build_recent_sell_index(recent_orders, id_to_slug)
    except Exception:  # noqa: BLE001 - speed optimization only, not required for correctness
        recent_index = {}

    reward_names = all_reward_names(relics)
    relic_names = list(relics.keys())

    def reward_progress(done, total):
        _prog("reward_prices", done, total)

    prices = wfm_api.get_price_batch(reward_names, name_to_slug, reward_progress, recent_index)

    def relic_progress(done, total):
        _prog("relic_prices", done, total)

    relic_prices = wfm_api.get_relic_price_batch(
        name_to_slug, relic_names, refinement, relic_progress, recent_index,
    )

    unmatched = [n for n in reward_names if prices.get(n.lower()) is None]
    no_relic_price = [n for n in relic_names if (relic_prices.get(n) or {}).get("online") is None]

    return Snapshot(
        relics=relics,
        refinement=refinement,
        prices=prices,
        relic_prices=relic_prices,
        ducats=ducats,
        name_to_slug=name_to_slug,
        catalog_source=catalog["source"],
        catalog_age_seconds=catalog["age_seconds"],
        unmatched_rewards=unmatched,
        no_relic_price=no_relic_price,
    )


# ---------- Per-relic row computation ----------
# Delegates to relic_row.py - the SAME shared engine main.py's desktop GUI
# uses (see that module's docstring), so this is no longer a third,
# independently-drifting copy of the online/offline calculation logic.

def compute_row(relic: Relic, snap: Snapshot, trace_rate: float, channel_scope: str) -> dict:
    """
    channel_scope: "Online only" and "Offline only" are special-cased by
    the shared engine below; any other value (including this bot's
    existing "Online + Offline" choice) is treated as "both channels,
    take whichever is better" - so bot.py's existing channel_scope
    strings keep working unchanged against this shared engine.
    """
    return relic_row.compute_row(
        relic, snap.refinement, trace_rate, snap.prices, snap.relic_prices, snap.ducats,
        channel_scope=channel_scope,
    )


def compute_all_rows(snap: Snapshot, trace_rate: float, channel_scope: str) -> list[dict]:
    return [compute_row(relic, snap, trace_rate, channel_scope) for relic in snap.relics.values()]


# ---------- Filtering ----------
# Delegates to filtering.py - see relic_row.py's note above; same reasoning.

def passes_filters(
    row: dict,
    channel_scope: str,
    vault_filter: str | None = None,
    min_roi: float | None = None,
    max_cost: float | None = None,
    min_reward: float | None = None,
    guaranteed_only: bool = False,
) -> bool:
    return filtering.passes_filters(
        row,
        vault_filter=vault_filter or "All",
        channel_scope=channel_scope,
        min_roi=min_roi, max_cost=max_cost, min_reward=min_reward,
        green_only=guaranteed_only,
    )


# ---------- Sorting ----------
# Delegates to ranking.py - see relic_row.py's note above; same reasoning.

def sort_key_for_mode(mode: str, channel_scope: str):
    return ranking.sort_key_for_mode(mode, channel_scope=channel_scope)


def rank_rows(rows: list[dict], mode: str, channel_scope: str) -> list[dict]:
    key = sort_key_for_mode(mode, channel_scope)
    return sorted(rows, key=key, reverse=True)


def estimate_fetch_seconds(n_relics: int, n_rewards: int, per_request_seconds: float = 0.20) -> tuple[float, float]:
    """
    Rough best-case/worst-case wall-clock estimate for fetch_snapshot, given
    the 5 req/sec rate limit (see wfm_api._MIN_INTERVAL). Best case assumes
    the shared /orders/recent snapshot covers every online price, leaving
    only the once-per-relic offline request (no bulk endpoint covers that).
    Worst case assumes nothing was covered by that snapshot, so every
    reward and every relic also needs its own online request on top of the
    always-required offline one. Actual time usually lands toward the
    best-case end once the snapshot has warmed up.
    """
    best = n_relics * per_request_seconds
    worst = (n_relics * 2 + n_rewards) * per_request_seconds
    return best, worst
