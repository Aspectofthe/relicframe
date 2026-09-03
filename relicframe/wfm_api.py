"""
wfm_api.py
Thin client around the official Warframe Market v2 public API.
Docs: https://warframe.market/api_docs  (v2 base: https://api.warframe.market/v2)

No API key is needed for reading public item/order data. Every call is
throttled through one process-wide five-request-per-second gate.
"""

import threading
import time

import requests

import order_math

BASE_URL = "https://api.warframe.market/v2"
HEADERS = {
    "User-Agent": "RelicFrame/1.0 (personal use)",
    "Accept": "application/json",
    "Platform": "pc",
    "Language": "en",
}

_session = requests.Session()
_session.headers.update(HEADERS)

_MIN_INTERVAL = 0.20  # 5 request starts per second
_last_call = 0.0
# Review item #24: "one global rate limiter... rather than every component
# independently firing requests." A single background thread was the only
# caller when _throttle() was first written, but this module now has two
# (the main analysis fetch, and the N-relic "Best Available Deal"
# calculator's own order-book fetch) that can legitimately run at the same
# time. Without a lock, two threads can both read _last_call as "long
# enough ago", both pass the throttle check, and both fire immediately -
# silently exceeding the API's rate limit exactly when two features are
# used together. The lock makes the whole check-sleep-update sequence one
# atomic step shared by every caller, so the limiter is real regardless of
# how many threads end up calling into this module.
_rate_limit_lock = threading.Lock()


def _throttle():
    global _last_call
    with _rate_limit_lock:
        elapsed = time.time() - _last_call
        if elapsed < _MIN_INTERVAL:
            time.sleep(_MIN_INTERVAL - elapsed)
        _last_call = time.time()


def _get(path: str, params: dict | None = None) -> dict:
    _throttle()
    resp = _session.get(f"{BASE_URL}{path}", params=params, timeout=15)
    resp.raise_for_status()
    return resp.json()


# Pure order-normalization/matching logic now lives in order_math.py, so it
# can be shared with the async live-market layer (discord_bot/) without a
# second, independently-drifting copy. Re-exported here under the same
# underscore-prefixed names this module has always used, so every existing
# caller and test (e.g. test_wfm_api.py's wfm_api._order_unit_price(...))
# keeps working unchanged.
_order_unit_price = order_math.order_unit_price
_order_entries = order_math.order_entries
_mark_outliers = order_math.mark_outliers
_best_matching_entry = order_math.best_matching_entry
_matching_entries = order_math.matching_entries


def get_all_items() -> list[dict]:
    """Returns the full item catalog. Cache this locally - big payload, rarely changes."""
    data = _get("/items")
    return data.get("data", [])


def build_name_to_slug_map(items: list[dict]) -> dict[str, str]:
    """
    Warframe Market slugs are lowercase/underscored (e.g. 'nova_prime_systems').
    Relic data uses display names ('Nova Prime Systems'), so build a lookup
    table once and reuse it.
    """
    mapping = {}
    for item in items:
        i18n = item.get("i18n", {}).get("en", {})
        name = i18n.get("name")
        slug = item.get("slug")
        if name and slug:
            mapping[name.strip().lower()] = slug
    return mapping


def build_id_to_slug_map(items: list[dict]) -> dict[str, str]:
    """
    /orders/recent returns each order's itemId, not its slug - need the
    reverse of the name->slug lookup to translate the bulk snapshot back
    to the slugs the rest of this module keys everything by.
    """
    mapping = {}
    for item in items:
        item_id = item.get("id")
        slug = item.get("slug")
        if item_id and slug:
            mapping[item_id] = slug
    return mapping


def get_recent_orders() -> list[dict]:
    """
    /v2/orders/recent - up to 500 of the most recently created visible
    orders, online users only, refreshed every ~1 min server-side. One
    request can cover price data for a large chunk of a relic's rewards
    and the relics themselves at once, instead of one request per item -
    this is the bulk snapshot the rest of this module matches against
    before ever falling back to a per-item call.
    """
    data = _get("/orders/recent")
    return data.get("data", [])


def build_recent_sell_index(recent_orders: list[dict], id_to_slug: dict[str, str]) -> dict[str, list[dict]]:
    """
    Groups the /orders/recent snapshot into {slug: [raw sell order dict, ...]},
    sell orders only. Each order dict keeps the same shape /top's 'sell'
    list uses (platinum, perTrade, quantity, subtype), so it can be fed
    straight into the existing _best_matching_entry / _order_entries /
    _mark_outliers helpers - no separate matching logic needed for the
    bulk path vs. the per-item fallback path.
    """
    index: dict[str, list[dict]] = {}
    for o in recent_orders:
        if o.get("type") != "sell":
            continue
        slug = id_to_slug.get(o.get("itemId"))
        if not slug:
            continue
        index.setdefault(slug, []).append(o)
    return index


def get_top_orders(slug: str) -> dict:
    """
    /orders/item/{slug}/top - up to 5 sell + 5 buy orders from currently
    online users, pre-ranked by the server. Lighter than pulling every order.
    """
    return _get(f"/orders/item/{slug}/top").get("data", {"sell": [], "buy": []})


def get_lowest_sell_from_top(slug: str) -> float | None:
    top = get_top_orders(slug)
    entries = _mark_outliers(_order_entries(top.get("sell", [])))
    return min(entries, key=lambda e: e["price"])["price"] if entries else None


def get_lowest_sell_from_recent_or_top(slug: str, recent_index: dict[str, list[dict]] | None) -> float | None:
    """
    Bulk-first lookup: if the /orders/recent snapshot already has sell
    orders for this slug, price it from that (zero extra requests). Only
    falls back to the per-item /top request when the slug isn't
    represented in the snapshot at all - most of a relic's ~150-200
    unique reward slugs typically ARE covered by one 500-order snapshot,
    which is what turns "one /top call per reward" into "one shared
    request plus a handful of fallbacks."
    """
    if recent_index:
        orders = recent_index.get(slug)
        if orders:
            entries = _mark_outliers(_order_entries(orders))
            if entries:
                return min(entries, key=lambda e: e["price"])["price"]
    return get_lowest_sell_from_top(slug)


def find_relic_slug(name_to_slug: dict[str, str], relic_name: str) -> str | None:
    """
    Relics are tradable items on Warframe Market too (e.g. 'Axi N1 Relic'),
    so their own buy price can be looked up the same way as any reward item.
    """
    candidates = [
        f"{relic_name.strip().lower()} relic",
        relic_name.strip().lower(),
    ]
    for candidate in candidates:
        slug = name_to_slug.get(candidate)
        if slug:
            return slug
    return None


# Relics almost never legitimately trade above this many plat - but per the
# "cheapest means cheapest, don't silently delete" rule, this is no longer
# used to drop listings. Kept only as a documented reference point for what
# "unusually high" means when eyeballing an is_outlier flag.
RELIC_PRICE_SANITY_CAP = 400


def get_relic_price_detail(
    name_to_slug: dict[str, str],
    relic_name: str,
    refinement: str | None = None,
    recent_index: dict[str, list[dict]] | None = None,
) -> dict | None:
    """
    CHEAPEST current sell price for the relic itself, from online sellers
    only - what you could actually buy right now. Also returns the
    seller's available quantity, whether the price is confirmed for the
    exact refinement tier or a fallback across any tier, and whether this
    price looks like a market outlier (still returned, never hidden - see
    _mark_outliers). Returns None if nothing matched at all.

    Checks the /orders/recent bulk snapshot (recent_index) first - it's
    online-users-only, same as /top, so it's a like-for-like substitute
    when the slug is covered. Falls back to a per-relic /top request only
    when the snapshot has nothing for this slug.
    """
    slug = find_relic_slug(name_to_slug, relic_name)
    if not slug:
        return None
    sell_orders = (recent_index or {}).get(slug)
    if sell_orders:
        entry, matched = _best_matching_entry(sell_orders, refinement)
        if entry is not None:
            return {
                "price": entry["price"], "quantity": entry["quantity"],
                "subtype_matched": matched, "is_outlier": entry["is_outlier"],
            }
    top = get_top_orders(slug)
    entry, matched = _best_matching_entry(top.get("sell", []), refinement)
    if entry is None:
        return None
    return {
        "price": entry["price"], "quantity": entry["quantity"],
        "subtype_matched": matched, "is_outlier": entry["is_outlier"],
    }


def get_lowest_sell_all_detail(slug: str, subtype: str | None = None) -> dict | None:
    """
    Like get_relic_price_detail, but includes offline sellers too (needs
    the full order list, not /top, which is online-only). Often cheaper,
    but you'd have to wait for that seller to come online or message them.
    """
    data = _get(f"/orders/item/{slug}")
    orders = data.get("data", [])
    sell_orders = [o for o in orders if o.get("type") == "sell"]
    entry, matched = _best_matching_entry(sell_orders, subtype)
    if entry is None:
        return None
    return {
        "price": entry["price"], "quantity": entry["quantity"],
        "subtype_matched": matched, "is_outlier": entry["is_outlier"],
    }


def get_relic_order_book(
    name_to_slug: dict[str, str],
    relic_name: str,
    refinement: str | None = None,
    include_offline: bool = False,
) -> list[dict] | None:
    """
    The sell-order book for a relic (not just the single cheapest listing)
    - what "Best Available Deal" / cheapest-combination-for-N pricing
    needs, since a single seller often doesn't have N units in stock and
    buying N really means sweeping the book from cheapest seller upward.

    include_offline=False mirrors get_relic_price_detail (online-only, via
    /top). IMPORTANT CAVEAT: /top returns at most the 5 best sell orders
    the server has pre-ranked - it is NOT the complete online order book,
    just the API's idea of the best of it. For a relic with more than 5
    online sellers, cheapest_combination_cost() run on this list can only
    ever plan against those 5, so a "not fully filled" result here means
    "not fillable from the top 5", which is usually - but not always -
    the same as "not fillable at all." include_offline=True instead hits
    /orders/item/{slug} directly, which genuinely IS every listed seller
    (online or not) with no such cap - real prices, but you may have to
    wait for an offline seller to come online.

    Returns None if the relic has no slug match at all; an empty list if
    it has a slug but zero live sell orders.
    """
    slug = find_relic_slug(name_to_slug, relic_name)
    if not slug:
        return None
    if include_offline:
        data = _get(f"/orders/item/{slug}")
        orders = data.get("data", [])
        sell_orders = [o for o in orders if o.get("type") == "sell"]
    else:
        top = get_top_orders(slug)
        sell_orders = top.get("sell", [])
    entries, _matched = _matching_entries(sell_orders, refinement)
    return entries


def get_relic_prices(
    name_to_slug: dict[str, str],
    relic_name: str,
    refinement: str | None = None,
    recent_index: dict[str, list[dict]] | None = None,
) -> dict:
    """
    Both the online-only price and the offline-inclusive price for a
    single relic at the given refinement, plus quantity, subtype-match
    status, and outlier flag for each.

    Only the online side can be served from the /orders/recent bulk
    snapshot (recent_index) - that endpoint only includes online users by
    definition, so it's no help for offline listings. Offline still needs
    its own /orders/item/{slug} request per relic; there's no bulk
    endpoint that covers offline sellers.

    Returns {
        "online": float|None, "online_quantity": int|None,
        "online_subtype_matched": bool, "online_is_outlier": bool,
        "offline_included": float|None, "offline_quantity": int|None,
        "offline_subtype_matched": bool, "offline_is_outlier": bool,
    }
    """
    slug = find_relic_slug(name_to_slug, relic_name)
    if not slug:
        return {
            "online": None, "online_quantity": None,
            "online_subtype_matched": False, "online_is_outlier": False,
            "offline_included": None, "offline_quantity": None,
            "offline_subtype_matched": False, "offline_is_outlier": False,
        }
    online_detail = get_relic_price_detail(name_to_slug, relic_name, refinement, recent_index)
    offline_detail = get_lowest_sell_all_detail(slug, subtype=refinement)
    return {
        "online": online_detail["price"] if online_detail else None,
        "online_quantity": online_detail["quantity"] if online_detail else None,
        "online_subtype_matched": online_detail["subtype_matched"] if online_detail else False,
        "online_is_outlier": online_detail["is_outlier"] if online_detail else False,
        "offline_included": offline_detail["price"] if offline_detail else None,
        "offline_quantity": offline_detail["quantity"] if offline_detail else None,
        "offline_subtype_matched": offline_detail["subtype_matched"] if offline_detail else False,
        "offline_is_outlier": offline_detail["is_outlier"] if offline_detail else False,
    }


def get_price_batch(
    reward_names: list[str],
    name_to_slug: dict[str, str],
    progress_callback=None,
    recent_index: dict[str, list[dict]] | None = None,
) -> dict:
    """
    Fetches lowest online sell price for a list of reward item names.
    Anything covered by the /orders/recent bulk snapshot (recent_index)
    is priced from that with zero extra requests; only names missing from
    the snapshot fall back to an individual, rate-limited /top request.
    Returns {name.lower(): price}.
    """
    results = {}
    total = len(reward_names)
    for i, name in enumerate(reward_names, start=1):
        slug = name_to_slug.get(name.lower())
        try:
            price = get_lowest_sell_from_recent_or_top(slug, recent_index) if slug else None
        except requests.RequestException:
            price = None
        results[name.lower()] = price
        if progress_callback:
            progress_callback(i, total)
    return results


def get_relic_price_batch(
    name_to_slug: dict[str, str],
    relic_names: list[str],
    refinement: str | None = None,
    progress_callback=None,
    recent_index: dict[str, list[dict]] | None = None,
) -> dict[str, dict]:
    """
    Fetches both online and offline prices for every relic in the list.
    The online side is served from the /orders/recent bulk snapshot
    (recent_index) whenever it covers that relic's slug, falling back to
    a per-relic /top request only when it doesn't. Offline still needs
    its own /orders/item/{slug} request per relic every time - that
    endpoint has no bulk equivalent, so this is the one part of a full
    run that can't be collapsed into a shared snapshot.
    Returns {relic_name: {"online":..., "online_quantity":...,
    "online_subtype_matched":..., "offline_included":..., "offline_quantity":...,
    "offline_subtype_matched":...}}.
    """
    results = {}
    total = len(relic_names)
    for i, name in enumerate(relic_names, start=1):
        try:
            results[name] = get_relic_prices(name_to_slug, name, refinement, recent_index)
        except Exception:  # noqa: BLE001
            results[name] = {
                "online": None, "online_quantity": None,
                "online_subtype_matched": False, "online_is_outlier": False,
                "offline_included": None, "offline_quantity": None,
                "offline_subtype_matched": False, "offline_is_outlier": False,
            }
        if progress_callback:
            progress_callback(i, total)
    return results


def get_bulk_price_sheet() -> dict[str, float]:
    """
    A pre-aggregated pricing sheet covering every tradable item in one
    request (the community feed WFInfo publishes), instead of one request
    per item. A smoothed "custom average" price, not a live lowest-ask -
    good for a fast first pass; get_price_batch gives the live snapshot.
    Returns {item name (lowercase): plat price}.
    """
    resp = _session.get("https://api.warframestat.us/wfinfo/prices", timeout=20)
    resp.raise_for_status()
    sheet = resp.json()
    prices = {}
    for entry in sheet:
        name = entry.get("name")
        plat = entry.get("custom_avg")
        if name is not None and plat is not None:
            prices[name.strip().lower()] = float(plat)
            if name.endswith(" Blueprint"):
                alias = name[: -len(" Blueprint")].strip().lower()
                prices.setdefault(alias, float(plat))
    return prices


def get_filtered_items() -> dict:
    """
    WFInfo's full relic drop tables + ducat values + vaulted status, one
    request. See wfinfo_data.py for parsing. Only used by the auto-fetch
    fallback data source - prefer parse_official_drops.py against DE's own
    export when you have it.
    """
    resp = _session.get("https://api.warframestat.us/wfinfo/filtered_items", timeout=20)
    resp.raise_for_status()
    return resp.json()


def get_ducat_map() -> dict[str, int]:
    """
    Ducat values for every Prime part, keyed by lowercase item name.
    Ducats are only meaningful independent of which relic CSV was loaded
    (official-export CSVs don't carry ducat data at all), so this is
    always fetched from WFInfo's feed regardless of the relic data source
    - it's the ducat/vault side of that feed, not the drop-table side, so
    using it here doesn't conflict with preferring the official export for
    drop chances themselves.
    """
    import wfinfo_data  # local import: avoids a hard dependency for callers who never touch ducats
    filtered = get_filtered_items()
    return wfinfo_data.build_ducat_map(filtered)
