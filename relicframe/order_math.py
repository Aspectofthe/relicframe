"""
order_math.py
Pure, network-free logic for turning raw Warframe Market order dicts into
normalized {"price", "quantity"} entries, flagging outliers, and finding
the best subtype-matching entry. Extracted out of wfm_api.py so this exact
logic - the same per-trade price normalization, the same "unknown quantity
is never zero" rule, the same outlier flagging, the same subtype-fallback
behavior - can be reused by BOTH the synchronous wfm_api.py (used by the
desktop app and the bot's REST fallback) and the new async live-market
layer (discord_bot/) without a second, independently-drifting copy.

Nothing in here touches the network, threads, or asyncio - every function
takes an already-fetched list of order dicts and returns a plain value.
"""

import statistics


def order_unit_price(order: dict) -> float | None:
    """
    'platinum' on an order is the TOTAL price, not necessarily per item.
    Bulk-tradable items (relics, and many resource-type parts like Forma
    Blueprint) trade in batches via 'perTrade' - up to 6 items per
    transaction, matching the 6 slots in Warframe's own trade window. A
    listing of platinum=1000, perTrade=6 is 166.67 plat PER ITEM, not 1000.
    Always go through this helper instead of reading o['platinum'] directly.
    """
    plat = order.get("platinum")
    if plat is None:
        return None
    per_trade = order.get("perTrade")
    if per_trade and per_trade > 0:
        return plat / per_trade
    return float(plat)


def order_entries(orders: list[dict]) -> list[dict]:
    """
    Normalizes raw order dicts into {"price", "quantity"} entries.
    Quantity of exactly 0 is excluded here - the one case we always
    exclude, since there's nothing actually purchasable at that listing.
    An unknown/missing quantity is NOT treated as zero.
    """
    entries = []
    for o in orders:
        price = order_unit_price(o)
        if price is None:
            continue
        qty = o.get("quantity")
        if qty == 0:
            continue
        entries.append({"price": price, "quantity": qty})
    return entries


def mark_outliers(entries: list[dict]) -> list[dict]:
    """
    Never deletes a listing - a cheap-but-real liquidation sale or a
    genuinely expensive vaulted-Radiant price are both legitimate data,
    not noise to be silently discarded. Instead, tags each entry with
    is_outlier so the caller can show "cheapest listing" honestly while
    still warning the person to double-check before buying.

    - A lone listing with nothing to compare against is always flagged
      (we simply can't tell if it's reasonable).
    - With 2+ listings, anything more than 3x the median is flagged.
    """
    if not entries:
        return []
    if len(entries) == 1:
        return [{**entries[0], "is_outlier": True}]
    median = statistics.median(e["price"] for e in entries)
    return [
        {**e, "is_outlier": (e["price"] > median * 3 or e["price"] < median / 3)}
        for e in entries
    ]


def best_matching_entry(
    sell_orders: list[dict], subtype: str | None
) -> tuple[dict | None, bool]:
    """
    Tries to find the CHEAPEST sell order matching the exact refinement
    subtype first (Intact/Exceptional/Flawless/Radiant) - "cheapest"
    always means the actual cheapest, never an average or median, and
    never with expensive-but-real listings silently thrown away.

    If NOTHING matches that exact tier - common, since plenty of relics
    only have a couple of active sell orders total and not every relic has
    stock at every refinement - falls back to the cheapest order of ANY
    subtype rather than reporting "no listing" when a price is actually
    available, just not confirmed for this specific tier.

    Returns (entry_or_None, subtype_matched). Each entry carries
    is_outlier (see mark_outliers) so callers can flag rather than hide
    unusual prices. subtype_matched is False when the entry came from the
    fallback (any-subtype) pass.
    """
    if subtype:
        matched = [o for o in sell_orders if (o.get("subtype") or "").lower() == subtype.lower()]
        entries = mark_outliers(order_entries(matched))
        if entries:
            return min(entries, key=lambda e: e["price"]), True

    entries = mark_outliers(order_entries(sell_orders))
    if entries:
        return min(entries, key=lambda e: e["price"]), not bool(subtype)
    return None, False


def is_order_from_online_user(order: dict) -> bool:
    """
    Whether an order's seller is currently online/in-game (vs fully
    offline) - used to derive the "online" subset from a FULL order book
    (/orders/item/{slug}, which returns every listing regardless of
    seller status) now that the live-market layer fetches complete order
    books instead of using the old /top endpoint (which only ever
    returned online sellers to begin with, making this check unnecessary
    there).

    CONFIRMATION NOTE: third-party WFM client libraries (checked during
    this project's live-market build) document a per-order `user.status`
    field with values "online" / "ingame" / "offline" for the v2 API, and
    this function is written against that shape. It was NOT independently
    confirmed against WFM's own raw JSON response in this session - sanity
    -check one real /orders/item/{slug} response's `data[].user.status`
    field before depending on this in production, and adjust here if the
    real field name/values differ.
    """
    status = (order.get("user") or {}).get("status", "")
    return status in ("online", "ingame")


def matching_entries(sell_orders: list[dict], subtype: str | None) -> tuple[list[dict], bool]:
    """
    Same subtype-matching logic as best_matching_entry, but returns ALL
    matching entries (not just the cheapest) - the building block for
    "cheapest combination to buy N", which needs the whole order book, not
    just its minimum. Returns (entries, subtype_matched); entries are
    outlier-flagged via mark_outliers but not necessarily sorted by price.
    """
    if subtype:
        matched = [o for o in sell_orders if (o.get("subtype") or "").lower() == subtype.lower()]
        entries = mark_outliers(order_entries(matched))
        if entries:
            return entries, True
    entries = mark_outliers(order_entries(sell_orders))
    return entries, (not bool(subtype)) if entries else False

