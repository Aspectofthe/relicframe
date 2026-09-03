"""
seller_blacklist.py
A persisted, case-insensitive set of WFM seller ingame names to exclude
from every relic price computation - not a display-layer filter, an
ingestion-layer one. Orders from a blacklisted seller are dropped in
OrderBookStore.apply_full_fetch/apply_ws_order_created (see order_book.py)
before they're ever stored, so a blacklisted seller's listing can never
become "the cheapest price" for a relic, never feeds ROI/EV/risk, and
never shows up in any list - not just hidden after the fact.

Global, not per-guild: the underlying LiveMarket/order book data is one
shared, in-process dataset serving every guild the bot is in (see
live_market.py), so a seller blacklisted here is blacklisted everywhere
this bot runs, the same way REQUIRED_SLUGS and the rest of the live
market state are global rather than per-guild.

Deliberately scoped to SELLER IDENTITY only - this does not touch which
relics/items are considered, filter by vault status, or change any ROI/
risk/expected-value calculation. A seller's OTHER listings for OTHER
relics are excluded too (this blocks the seller, not one listing), which
is the intended behavior for e.g. a seller repeatedly posting misleading
listings.
"""

from __future__ import annotations

import json
import os
import time

STATE_PATH = "seller_blacklist.json"


def _normalize(name: str) -> str:
    return name.strip().lower()


def _load_state() -> dict:
    try:
        with open(STATE_PATH, "r", encoding="utf-8") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else {}
    except (FileNotFoundError, json.JSONDecodeError, OSError):
        return {}


def _save_state(data: dict) -> None:
    tmp = STATE_PATH + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)
    # Same transient-WinError-5-on-Windows retry as discord_live_lists.py's
    # _save_state - antivirus/indexing can briefly hold the destination
    # file right after it's written.
    last_error: OSError | None = None
    for attempt in range(5):
        try:
            os.replace(tmp, STATE_PATH)
            return
        except OSError as exc:
            last_error = exc
            time.sleep(0.05 * (attempt + 1))
    raise last_error


class SellerBlacklist:
    """
    In-memory set backed by a JSON file on disk, loaded once at
    construction. Names are stored and compared lowercased/stripped so
    "Backstap86", "backstap86 ", and "BACKSTAP86" are all the same entry -
    WFM ingame names are unique regardless of case, matching how the
    site itself treats them.
    """

    def __init__(self):
        data = _load_state()
        raw = data.get("blacklisted_sellers")
        self._names: set[str] = {_normalize(n) for n in raw} if isinstance(raw, list) else set()

    def is_blacklisted(self, ingame_name: str | None) -> bool:
        if not ingame_name:
            return False
        return _normalize(ingame_name) in self._names

    def add(self, ingame_name: str) -> bool:
        """Returns True if this was a new entry, False if already present."""
        key = _normalize(ingame_name)
        if not key or key in self._names:
            return False
        self._names.add(key)
        self._save()
        return True

    def remove(self, ingame_name: str) -> bool:
        """Returns True if an entry was actually removed."""
        key = _normalize(ingame_name)
        if key not in self._names:
            return False
        self._names.discard(key)
        self._save()
        return True

    def list_names(self) -> list[str]:
        return sorted(self._names)

    def _save(self) -> None:
        _save_state({"blacklisted_sellers": sorted(self._names)})
