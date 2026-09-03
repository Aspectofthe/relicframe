"""
test_state.py
Tests for state.py's live-market wiring. Bypasses real network by
injecting a LiveMarket whose http_client is faked and whose store is
pre-populated directly (same approach as test_live_market.py).

Run with:
    python -m unittest test_state.py -v
"""

import unittest
from unittest.mock import patch

try:
    from live_market import LiveMarket
    from http_client import WfmHttpClient
    _AIOHTTP_AVAILABLE = True
except ImportError:
    _AIOHTTP_AVAILABLE = False
    WfmHttpClient = object

import state as state_module
from relic_data import Relic, RelicReward


def _relics():
    return {
        "Lith A1": Relic("Lith A1", rewards=[
            RelicReward("Common A", "common"), RelicReward("Common B", "common"),
            RelicReward("Common C", "common"), RelicReward("Uncommon A", "uncommon"),
            RelicReward("Uncommon B", "uncommon"), RelicReward("Rare A", "rare"),
        ]),
    }


@unittest.skipUnless(_AIOHTTP_AVAILABLE, "aiohttp not installed in this environment")
class TestBuildSnapshotNamingCollision(unittest.TestCase):
    """
    Direct regression test: build_snapshot() must be a distinct method
    from the self.snapshot ATTRIBUTE bot.py reads directly - a method
    named the same as the attribute it assigns to would shadow itself on
    the very first call, breaking every subsequent read of state.snapshot
    as an attribute (and every subsequent call as a method).
    """

    def test_snapshot_stays_a_readable_attribute_after_build_snapshot_runs(self):
        bs = state_module.BotState()
        bs.relics = _relics()
        bs.live_market = LiveMarket(http_client=WfmHttpClient(max_concurrent=1, max_per_second=1.0))
        bs.live_market.name_to_slug = {"lith a1 relic": "lith_a1_relic"}
        bs.live_market.required_slugs = {"lith_a1_relic": "Lith A1"}

        # Before any build: attribute exists and is None (never a callable).
        self.assertIsNone(bs.snapshot)

        snap1 = bs.build_snapshot("intact")
        self.assertIs(bs.snapshot, snap1)  # attribute now holds the Snapshot object

        # THE regression case: calling build_snapshot() a second time must
        # still work - if `snapshot` were both the attribute name and a
        # method name, the first assignment would have overwritten the
        # method on the instance, making this second call raise
        # "'Snapshot' object is not callable".
        snap2 = bs.build_snapshot("radiant")
        self.assertIs(bs.snapshot, snap2)
        self.assertNotEqual(snap1.refinement, snap2.refinement)

    def test_require_snapshot_reads_the_attribute_not_the_method(self):
        bs = state_module.BotState()
        bs.relics = _relics()
        bs.live_market = LiveMarket(http_client=WfmHttpClient(max_concurrent=1, max_per_second=1.0))
        bs.live_market.name_to_slug = {}
        bs.live_market.required_slugs = {}
        bs.build_snapshot("intact")
        result = bs.require_snapshot()
        self.assertIs(result, bs.snapshot)


@unittest.skipUnless(_AIOHTTP_AVAILABLE, "aiohttp not installed in this environment")
class TestBuildSnapshotWithoutLiveMarket(unittest.TestCase):
    def test_raises_clear_error_before_live_market_started(self):
        bs = state_module.BotState()
        bs.relics = _relics()
        with self.assertRaises(RuntimeError):
            bs.build_snapshot("intact")


@unittest.skipUnless(_AIOHTTP_AVAILABLE, "aiohttp not installed in this environment")
class TestStateLifecycle(unittest.IsolatedAsyncioTestCase):
    async def test_failed_market_start_is_cleaned_up(self):
        created = []

        class FakeMarket:
            def __init__(self, **_kwargs):
                self.stopped = False
                created.append(self)

            async def start(self, *_args, **_kwargs):
                raise RuntimeError("bootstrap failed")

            async def stop(self):
                self.stopped = True

        bs = state_module.BotState()
        bs.relics = _relics()
        with patch.object(state_module, "LiveMarket", FakeMarket):
            with self.assertRaisesRegex(RuntimeError, "bootstrap failed"):
                await bs.ensure_live_market_started()
        self.assertTrue(created[0].stopped)
        self.assertIsNone(bs.live_market)

    async def test_close_stops_market_and_clears_reference(self):
        class FakeMarket:
            stopped = False

            async def stop(self):
                self.stopped = True

        bs = state_module.BotState()
        market = FakeMarket()
        bs.live_market = market
        await bs.close()
        self.assertTrue(market.stopped)
        self.assertIsNone(bs.live_market)


if __name__ == "__main__":
    unittest.main()
