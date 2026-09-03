"""
ws_client.py
Connects once to Warframe Market's official realtime WebSocket API and
applies live "new order" events into the shared OrderBookStore, so a
fresh, cheaper listing shows up between reconciliation passes instead of
waiting for the next scheduled REST refetch.

READ THIS BEFORE ASSUMING THE WS FEED REPLACES REST FETCHING:
Per WFM's own public docs (https://docs.warframe.market/docs/websockets/
overview and .../subscriptions, API version v0.13.0 at the time this was
written), the ONLY market-data subscription documented is:

    @wfm|cmd/subscribe/newOrders

...and it is a GLOBAL firehose of every newly-posted VISIBLE order from
ONLINE users across the ENTIRE marketplace, filterable only by
platform/crossplay - there is no documented per-item/per-slug
subscription, and no documented event for an order being edited, closed,
or deleted (those events exist only for your OWN account's orders, via
the separate authenticated "account events" channel, not for the market
as a whole). That means:

- This feed can tell us about a BETTER (new, possibly cheaper) sell order
  appearing for a slug we're tracking, as soon as it's posted.
- This feed CANNOT tell us when an existing order we already know about
  gets sold, cancelled, or edited elsewhere. An order_book.py OrderBook
  fed only by this stream will accumulate orders that may no longer be
  real, with no way to detect that from the WS feed alone.
- Therefore this is a LATENCY IMPROVEMENT on top of periodic reconciliation
  (reconciler.py), never a replacement for it. reconciler.py's full REST
  refetch remains the actual source of truth; this module just narrows the
  gap between reconciliation passes for whichever slug got a new listing.

The supplied Warframe Market API documentation confirms the production
socket URL, the required ``wfm`` WebSocket subprotocol, and that order
payloads identify their item with ``itemId``. The client therefore receives
the item-id-to-slug catalog mapping from LiveMarket and never guesses that a
nested item slug will be present in an order event.
"""

from __future__ import annotations

import asyncio
import json
import logging
import time

import aiohttp

from order_book import OrderBookStore

logger = logging.getLogger("live_market.ws_client")

WS_URL = "wss://ws.warframe.market/socket"
WS_PROTOCOL = "wfm"

RECONNECT_BACKOFF_SECONDS = [1, 2, 5, 10, 30, 60]  # caps out at 60s between retries


class WfmWebSocketClient:
    """
    async with WfmWebSocketClient(store, tracked_slugs) as ws:
        await ws.run_forever()   # or: asyncio.create_task(ws.run_forever())

    tracked_slugs is a live-updatable set (the same object the caller
    holds) - since the WS feed is global and unfiltered server-side,
    every incoming order is checked against this set client-side and
    silently ignored if it's for a slug we're not tracking. OrderBookStore
    itself also no-ops writes for untracked slugs (see its docstring), so
    this is a belt-and-suspenders filter, not the only one.
    """

    def __init__(
        self,
        store: OrderBookStore,
        tracked_slugs: set[str],
        id_to_slug: dict[str, str],
        ws_url: str = WS_URL,
    ):
        self.store = store
        self.tracked_slugs = tracked_slugs
        self.id_to_slug = id_to_slug
        self.ws_url = ws_url
        self._session: aiohttp.ClientSession | None = None
        self._ws: aiohttp.ClientWebSocketResponse | None = None
        self._stopped = False
        self.connected = False
        self.last_event_at: float | None = None

    async def __aenter__(self) -> "WfmWebSocketClient":
        self._session = aiohttp.ClientSession()
        return self

    async def __aexit__(self, exc_type, exc, tb):
        self._stopped = True
        if self._ws is not None and not self._ws.closed:
            await self._ws.close()
        if self._session is not None:
            await self._session.close()
        return False

    async def _subscribe_new_orders(self) -> None:
        await self._ws.send_json({
            "route": "@wfm|cmd/subscribe/newOrders",
            "id": "relicframe-subscribe-new-orders",
            "payload": {"platform": "pc", "crossplay": True},
        })

    def _handle_message(self, raw: str) -> None:
        """
        Parses one WS text frame and, if it's a new-order event for a
        slug we're tracking, applies it into the OrderBookStore. Unknown
        routes (reports, acks, anything not yet handled) are silently
        ignored rather than erroring - this is a pre-1.0, evolving API per
        WFM's own docs, and an unrecognized message shape here should
        never take the whole connection down.
        """
        try:
            msg = json.loads(raw)
        except (json.JSONDecodeError, TypeError):
            return
        route = msg.get("route", "")
        payload = msg.get("payload") or {}

        if "neworder" not in route.lower() and "order" not in route.lower():
            return
        order = payload.get("order", payload)
        item_id = order.get("itemId")
        slug = self.id_to_slug.get(str(item_id)) if item_id is not None else None
        if not slug or slug not in self.tracked_slugs:
            return

        self.store.apply_ws_order_created(slug, order)
        self.last_event_at = time.monotonic()

    async def run_forever(self) -> None:
        """
        Connects, subscribes, and processes events until stopped.
        Reconnects with exponential backoff on any disconnect/error -
        this is a best-effort speed layer (see module docstring), so a
        connection drop is logged and retried, never raised up to crash
        the bot process.
        """
        attempt = 0
        while not self._stopped:
            try:
                async with self._session.ws_connect(
                    self.ws_url,
                    heartbeat=30,
                    protocols=(WS_PROTOCOL,),
                ) as ws:
                    self._ws = ws
                    await self._subscribe_new_orders()
                    self.connected = True
                    attempt = 0
                    logger.info("WFM WebSocket connected and subscribed to newOrders.")
                    async for msg in ws:
                        if msg.type == aiohttp.WSMsgType.TEXT:
                            self._handle_message(msg.data)
                        elif msg.type in (aiohttp.WSMsgType.CLOSED, aiohttp.WSMsgType.ERROR):
                            break
            except Exception as e:  # noqa: BLE001 - never let a WS hiccup kill the bot
                logger.warning(
                    "WFM WebSocket connection failed or dropped (%s) - the bot continues on "
                    "REST-reconciliation-only mode; retrying in the background.", e,
                )
            self.connected = False
            if self._stopped:
                break
            wait = RECONNECT_BACKOFF_SECONDS[min(attempt, len(RECONNECT_BACKOFF_SECONDS) - 1)]
            attempt += 1
            await asyncio.sleep(wait)

