"""
order_book.py
The full, live order book for one WFM item slug - not a "top 5" or a
bulk-average snapshot, but every order the API returned for that slug,
kept in memory and updated two ways:

1. A full REST refetch (`/orders/item/{slug}`) - authoritative, replaces
   the book wholesale. This is both the initial bootstrap AND the periodic
   reconciliation pass (see reconciler.py) that corrects for anything the
   WebSocket feed can't tell us about (see the module docstring in
   ws_client.py for exactly what that is and isn't).
2. A live "order created" event from the WebSocket feed - incremental,
   appends/updates a single order between reconciliation passes so a
   fresh, cheaper listing shows up immediately rather than waiting for the
   next scheduled refetch.

Every derived number (cheapest matching entry, the full matching entry
list for cheapest-combination pricing) goes through order_math.py's pure
functions - the SAME subtype-matching, per-trade normalization, and
outlier-flagging rules the desktop app and the old sync bot pipeline use,
so numbers never disagree just because the data arrived via a different
path.
"""

from __future__ import annotations

import time
from dataclasses import dataclass, field

import order_math


@dataclass
class OrderBook:
    slug: str
    orders: list[dict] = field(default_factory=list)
    last_full_fetch: float | None = None  # None = never SUCCESSFULLY reconciled - see is_bootstrapped
    last_attempted: float | None = None  # None = never attempted at all (success OR failure)
    last_ws_update: float | None = None

    @property
    def is_bootstrapped(self) -> bool:
        """
        False until at least one full REST reconciliation has completed
        SUCCESSFULLY. Callers must not trust a book's prices before this
        is True - a book that has only ever received WS "order created"
        events (no full fetch yet) is missing every order that existed
        before this process started listening, which is not a usable
        state to price from.
        """
        return self.last_full_fetch is not None

    def replace_from_full_fetch(self, raw_orders: list[dict]) -> None:
        """Authoritative reconciliation: replaces the ENTIRE book with a
        fresh /orders/item/{slug} response - the source of truth. Corrects
        for anything the WS feed alone can't (order deletions/edits, and
        anything that existed before this process connected)."""
        self.orders = list(raw_orders)
        self.last_full_fetch = time.time()
        self.last_attempted = self.last_full_fetch

    def mark_attempted(self) -> None:
        """
        Records that a reconciliation was ATTEMPTED, whether it succeeded
        or failed. Deliberately separate from last_full_fetch: a slug that
        keeps failing (delisted item, transient API error, typo'd slug)
        must still count as "just attempted" for staleness-ordering
        purposes, or it would look permanently most-overdue and get
        retried on literally every single sweep iteration ahead of every
        other tracked slug - starving the rest of the tracked set of any
        reconciliation at all. Call this on both the success AND failure
        path (replace_from_full_fetch already calls it via last_attempted
        above; call this directly for a failed attempt).
        """
        self.last_attempted = time.time()

    def apply_ws_order_created(self, order: dict) -> None:
        """
        Incremental update from a live "new order" WebSocket event.
        Appends if this looks like a genuinely new order (by id, if
        present); otherwise this is a best-effort dedup and a rare
        duplicate entry only mildly skews outlier flagging, never
        correctness of "is there a real listing under N plat" - the next
        reconciliation pass corrects any such drift regardless.

        This can ONLY ever ADD orders - the WS feed as documented has no
        delete/close/update event for orders on items you don't own, so an
        order that got bought or cancelled will keep showing here until
        the next full reconciliation replaces the book. That staleness
        window is bounded by the reconciliation interval, not unbounded -
        see reconciler.py.
        """
        order_id = order.get("id")
        if order_id is not None and any(o.get("id") == order_id for o in self.orders):
            return
        self.orders.append(order)
        self.last_ws_update = time.time()

    def sell_orders(self) -> list[dict]:
        return [o for o in self.orders if o.get("type") == "sell"]

    def online_sell_orders(self) -> list[dict]:
        """
        The subset of sell_orders() whose seller is currently online/
        in-game - see order_math.is_order_from_online_user's docstring
        for the exact field this checks and its confirmation status.
        This is what makes "online" a meaningful concept again now that
        the live-market layer fetches the FULL order book (every seller,
        online or not) instead of the old /top endpoint, which only ever
        returned online sellers in the first place.
        """
        return [o for o in self.sell_orders() if order_math.is_order_from_online_user(o)]

    def best_matching_entry(self, subtype: str | None) -> tuple[dict | None, bool]:
        """Cheapest sell entry matching `subtype` among ALL listed sellers
        (online or not) - the "offline" definition throughout this
        project. Falls back to any subtype if nothing matches exactly.
        See order_math.best_matching_entry."""
        return order_math.best_matching_entry(self.sell_orders(), subtype)

    def best_matching_entry_online(self, subtype: str | None) -> tuple[dict | None, bool]:
        """Same as best_matching_entry, but restricted to currently-online
        sellers only - the "online" (buyable right now) definition."""
        return order_math.best_matching_entry(self.online_sell_orders(), subtype)

    def matching_entries(self, subtype: str | None) -> tuple[list[dict], bool]:
        """ALL matching sell entries among ALL listed sellers (not just
        the cheapest) - what cheapest-combination-for-N pricing sweeps
        across sellers with, for the "offline" (all sellers) channel."""
        return order_math.matching_entries(self.sell_orders(), subtype)

    def matching_entries_online(self, subtype: str | None) -> tuple[list[dict], bool]:
        """Same as matching_entries, but restricted to currently-online
        sellers only - for the "online" channel's cheapest-combination
        sweep."""
        return order_math.matching_entries(self.online_sell_orders(), subtype)


class OrderBookStore:
    """Every tracked slug's OrderBook, keyed by slug."""

    def __init__(self, blacklist=None):
        self._books: dict[str, OrderBook] = {}
        # SellerBlacklist | None - see seller_blacklist.py. None (the
        # default, and what every pre-existing caller/test still gets)
        # means no filtering, preserving prior behavior exactly.
        self._blacklist = blacklist

    def _drop_blacklisted(self, orders: list[dict]) -> list[dict]:
        if self._blacklist is None:
            return orders
        return [
            o for o in orders
            if not self._blacklist.is_blacklisted((o.get("user") or {}).get("ingameName"))
        ]

    def get_or_create(self, slug: str) -> OrderBook:
        book = self._books.get(slug)
        if book is None:
            book = OrderBook(slug=slug)
            self._books[slug] = book
        return book

    def get(self, slug: str) -> OrderBook | None:
        return self._books.get(slug)

    def slugs(self) -> list[str]:
        return list(self._books.keys())

    def apply_full_fetch(self, slug: str, raw_orders: list[dict]) -> None:
        self.get_or_create(slug).replace_from_full_fetch(self._drop_blacklisted(raw_orders))

    def mark_attempted(self, slug: str) -> None:
        """Records a reconciliation attempt (typically a failed one - a
        successful fetch already records this via apply_full_fetch) so a
        repeatedly-failing slug doesn't starve every other tracked slug's
        turn in oldest_reconciled_slug()'s ordering - see OrderBook.mark_attempted."""
        book = self._books.get(slug)
        if book is not None:
            book.mark_attempted()

    def apply_ws_order_created(self, slug: str, order: dict) -> None:
        """No-op for a slug we aren't tracking - the WS feed is global and
        will mention plenty of items outside our relic/reward set; only
        slugs registered via get_or_create (i.e. ones the bootstrap/
        reconciler already knows about) are worth storing."""
        book = self._books.get(slug)
        if book is not None and self._drop_blacklisted([order]):
            book.apply_ws_order_created(order)

    def purge_seller(self, ingame_name: str) -> int:
        """
        Immediately removes a seller's orders from every currently-tracked
        book, rather than waiting for that slug's next reconciliation pass
        to naturally drop them via apply_full_fetch's filtering. Meant to
        be called right after SellerBlacklist.add() so blocking a seller
        takes effect on the very next list refresh, not "eventually".
        Returns the number of orders removed.
        """
        if self._blacklist is None:
            return 0
        removed = 0
        for book in self._books.values():
            kept = self._drop_blacklisted(book.orders)
            removed += len(book.orders) - len(kept)
            book.orders = kept
        return removed

    def oldest_reconciled_slug(self) -> str | None:
        """
        The tracked slug most overdue for a reconciliation ATTEMPT - never-
        attempted slugs (last_attempted is None) sort first (infinitely
        overdue), then ascending by last_attempted. Deliberately keyed on
        "last attempted" rather than "last successful fetch": a slug that
        keeps failing must still count as recently touched once it's been
        tried, or it would look permanently most-overdue and get retried
        every single sweep iteration ahead of the rest of the tracked
        set - starving everything else of reconciliation entirely. Used
        by the reconciler to always work on whatever's genuinely stalest,
        spreading REST load evenly across the whole tracked set over time.
        """
        if not self._books:
            return None
        return min(
            self._books.keys(),
            key=lambda s: (self._books[s].last_attempted is not None, self._books[s].last_attempted or 0.0),
        )

