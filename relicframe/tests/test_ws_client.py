"""Network-free tests for Warframe Market WebSocket event routing."""

import json
import unittest

try:
    from ws_client import WS_PROTOCOL, WS_URL, WfmWebSocketClient
    _AIOHTTP_AVAILABLE = True
except ImportError:
    _AIOHTTP_AVAILABLE = False


class FakeStore:
    def __init__(self):
        self.applied = []

    def apply_ws_order_created(self, slug, order):
        self.applied.append((slug, order))


@unittest.skipUnless(_AIOHTTP_AVAILABLE, "aiohttp not installed in this environment")
class TestWebSocketOrderRouting(unittest.TestCase):
    def setUp(self):
        self.store = FakeStore()
        self.client = WfmWebSocketClient(
            self.store,
            {"lith_a1_relic"},
            {"item-123": "lith_a1_relic", "item-999": "other_item"},
        )

    def test_confirmed_connection_settings(self):
        self.assertEqual(WS_URL, "wss://ws.warframe.market/socket")
        self.assertEqual(WS_PROTOCOL, "wfm")

    def test_item_id_routes_order_to_tracked_slug(self):
        order = {"id": "order-1", "itemId": "item-123", "type": "sell", "platinum": 5}
        self.client._handle_message(json.dumps({
            "route": "@wfm|event/subscriptions/newOrder",
            "payload": {"order": order},
        }))
        self.assertEqual(self.store.applied, [("lith_a1_relic", order)])

    def test_untracked_item_id_is_ignored(self):
        self.client._handle_message(json.dumps({
            "route": "@wfm|event/subscriptions/newOrder",
            "payload": {"order": {"id": "order-2", "itemId": "item-999"}},
        }))
        self.assertEqual(self.store.applied, [])

    def test_missing_item_id_is_ignored_instead_of_guessing_slug(self):
        self.client._handle_message(json.dumps({
            "route": "@wfm|event/subscriptions/newOrder",
            "payload": {"order": {"id": "order-3", "slug": "lith_a1_relic"}},
        }))
        self.assertEqual(self.store.applied, [])

    def test_non_order_route_is_ignored(self):
        self.client._handle_message(json.dumps({
            "route": "@wfm|event/heartbeat",
            "payload": {"itemId": "item-123"},
        }))
        self.assertEqual(self.store.applied, [])


if __name__ == "__main__":
    unittest.main()

