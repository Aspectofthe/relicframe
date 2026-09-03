"""
item_catalog_cache.py
Local disk cache for the Warframe Market item catalog (/v2/items).

Why this exists: the catalog is a big payload (~50k+ items) that barely
changes - new items only show up alongside game content updates, roughly
monthly at most - yet the old code re-downloaded it on every single
"Fetch Prices & Analyze" click AND on every auto-refresh tick, which is
pure waste against a rate-limited API and adds several seconds to every
refresh for data that's still identical to five minutes ago.

Pure and network-free by design: the caller supplies the actual fetch
function, so this module (and its tests) never touch the network directly.
That's also what makes it testable without mocking requests.
"""

import json
import os
import time

DEFAULT_MAX_AGE_SECONDS = 24 * 60 * 60  # 24h - catalog changes rarely; a day-old copy is fine


def _read_cache_file(cache_path: str) -> dict | None:
    """Returns the raw cache dict, or None if missing/unreadable/malformed."""
    if not os.path.exists(cache_path):
        return None
    try:
        with open(cache_path, "r", encoding="utf-8") as f:
            data = json.load(f)
    except (OSError, json.JSONDecodeError):
        return None
    if not isinstance(data, dict) or "fetched_at" not in data or "items" not in data:
        return None
    return data


def _write_cache_file(cache_path: str, items: list[dict]) -> None:
    os.makedirs(os.path.dirname(cache_path) or ".", exist_ok=True)
    tmp_path = cache_path + ".tmp"
    with open(tmp_path, "w", encoding="utf-8") as f:
        json.dump({"fetched_at": time.time(), "items": items}, f)
    os.replace(tmp_path, cache_path)  # atomic-ish swap - never leaves a half-written cache file


def _read_generic_cache_file(cache_path: str) -> dict | None:
    """Same shape/behavior as _read_cache_file, but keyed 'payload' instead
    of 'items' - used by load_or_fetch_json for cache payloads that aren't
    the item catalog (e.g. the ducat map)."""
    if not os.path.exists(cache_path):
        return None
    try:
        with open(cache_path, "r", encoding="utf-8") as f:
            data = json.load(f)
    except (OSError, json.JSONDecodeError):
        return None
    if not isinstance(data, dict) or "fetched_at" not in data or "payload" not in data:
        return None
    return data


def _write_generic_cache_file(cache_path: str, payload) -> None:
    os.makedirs(os.path.dirname(cache_path) or ".", exist_ok=True)
    tmp_path = cache_path + ".tmp"
    with open(tmp_path, "w", encoding="utf-8") as f:
        json.dump({"fetched_at": time.time(), "payload": payload}, f)
    os.replace(tmp_path, cache_path)


def load_or_fetch_json(
    cache_path: str,
    fetch_fn,
    max_age_seconds: float = DEFAULT_MAX_AGE_SECONDS,
    force_refresh: bool = False,
) -> dict:
    """
    Same caching behavior as load_or_fetch (fresh cache reused with zero
    network calls; expired/missing/corrupt triggers one fetch and rewrites
    the cache; a fetch failure falls back to a stale cache rather than
    failing outright), but for any JSON-serializable payload - a dict, a
    list, whatever fetch_fn returns - not specifically the item catalog's
    list-of-items shape. Used for the ducat map, which is a dict, not a
    list, and needs its own cache file separate from the item catalog's.

    Returns {"data": <payload>, "source": "cache"|"live"|"stale_fallback",
    "age_seconds": float | None}.
    """
    if not force_refresh:
        cached = _read_generic_cache_file(cache_path)
        if cached is not None:
            age = time.time() - cached["fetched_at"]
            if age <= max_age_seconds:
                return {"data": cached["payload"], "source": "cache", "age_seconds": age}

    try:
        payload = fetch_fn()
    except Exception:
        cached = _read_generic_cache_file(cache_path)
        if cached is not None:
            age = time.time() - cached["fetched_at"]
            return {"data": cached["payload"], "source": "stale_fallback", "age_seconds": age}
        raise

    _write_generic_cache_file(cache_path, payload)
    return {"data": payload, "source": "live", "age_seconds": 0.0}


def cache_age_seconds(cache_path: str) -> float | None:
    """Age of the cached copy in seconds, or None if there's no usable cache."""
    data = _read_cache_file(cache_path)
    if data is None:
        return None
    return max(0.0, time.time() - data["fetched_at"])


def load_or_fetch(
    cache_path: str,
    fetch_fn,
    max_age_seconds: float = DEFAULT_MAX_AGE_SECONDS,
    force_refresh: bool = False,
) -> dict:
    """
    Returns {"items": [...], "source": "cache" | "live" | "stale_fallback",
    "age_seconds": float | None}.

    - force_refresh=True always hits fetch_fn, ignoring any cache.
    - Otherwise: a cache newer than max_age_seconds is used as-is (source
      "cache", zero network calls).
    - A missing/expired/corrupt cache calls fetch_fn and writes a fresh
      cache file (source "live").
    - If fetch_fn raises (e.g. offline) AND an expired-but-present cache
      exists, that stale copy is used rather than failing outright (source
      "stale_fallback") - a day-old item catalog is still far better than
      no catalog at all when the network is briefly unavailable. The
      caller is expected to surface the source/age so this is never
      silently mistaken for a fresh fetch.
    """
    if not force_refresh:
        cached = _read_cache_file(cache_path)
        if cached is not None:
            age = time.time() - cached["fetched_at"]
            if age <= max_age_seconds:
                return {"items": cached["items"], "source": "cache", "age_seconds": age}

    try:
        items = fetch_fn()
    except Exception:
        cached = _read_cache_file(cache_path)
        if cached is not None:
            age = time.time() - cached["fetched_at"]
            return {"items": cached["items"], "source": "stale_fallback", "age_seconds": age}
        raise

    _write_cache_file(cache_path, items)
    return {"items": items, "source": "live", "age_seconds": 0.0}

