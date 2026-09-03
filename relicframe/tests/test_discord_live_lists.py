"""
test_discord_live_lists.py
Tests for discord_live_lists.py - specifically the fix for a real
production incident (see relic_row.py / CHANGELOG context): the live-list
updater was recomputing the full relic set's profitability math once per
list channel (7x redundant per refresh) and, at the time, that math
included a per-relic Monte Carlo simulation, which together blocked the
Discord gateway heartbeat for 10-30+ seconds and crashed the bot.

These tests cover the two parts of the fix:
1. base_online_rows()/filter_and_rank() split - the expensive part
   (per-relic profitability math) is separable from the cheap part
   (per-channel filter/rank), so it can be computed once and reused.
2. update_guild() groups channels by refinement and computes the
   expensive part exactly once per DISTINCT refinement actually
   configured, not once per channel.

Uses stub aiohttp/discord packages (see the project's other test files
for the same pattern) since this module imports both but these tests
don't need real network or Discord connections.

Run with:
    python -m unittest test_discord_live_lists.py -v
"""

import unittest
from types import SimpleNamespace
from unittest.mock import patch

try:
    import discord_live_lists as dll
    from relic_data import Relic, RelicReward
    from analysis import Snapshot
    _DEPS_AVAILABLE = True
except ImportError:
    _DEPS_AVAILABLE = False


def _relics():
    return {
        "Lith A1": Relic("Lith A1", rewards=[
            RelicReward("Common A", "common"), RelicReward("Common B", "common"),
            RelicReward("Common C", "common"), RelicReward("Uncommon A", "uncommon"),
            RelicReward("Uncommon B", "uncommon"), RelicReward("Rare A", "rare"),
        ], vaulted=True),
        "Meso B2": Relic("Meso B2", rewards=[
            RelicReward("Common A", "common"), RelicReward("Common B", "common"),
            RelicReward("Common C", "common"), RelicReward("Uncommon A", "uncommon"),
            RelicReward("Uncommon B", "uncommon"), RelicReward("Rare B", "rare"),
        ], vaulted=True),
        "Neo C3": Relic("Neo C3", rewards=[
            RelicReward("Common A", "common"), RelicReward("Common B", "common"),
            RelicReward("Common C", "common"), RelicReward("Uncommon A", "uncommon"),
            RelicReward("Uncommon B", "uncommon"), RelicReward("Rare A", "rare"),
        ], vaulted=False),
        "Axi D4": Relic("Axi D4", rewards=[
            RelicReward("Common A", "common"), RelicReward("Common B", "common"),
            RelicReward("Common C", "common"), RelicReward("Uncommon A", "uncommon"),
            RelicReward("Uncommon B", "uncommon"), RelicReward("Rare A", "rare"),
        ], vaulted=None),
    }


class FakeLiveMarket:
    def best_online_relic_order(self, relic_name, refinement):
        return None, True


class FakeState:
    def __init__(self):
        self.relics = _relics()
        self.live_market = FakeLiveMarket()

    def build_snapshot(self, refinement):
        prices = {"common a": 1.0, "common b": 1.0, "common c": 1.0,
                   "uncommon a": 5.0, "uncommon b": 5.0, "rare a": 60.0, "rare b": 60.0}
        relic_prices = {
            "Lith A1": {"online": 5.0, "online_quantity": 3, "online_subtype_matched": True, "online_is_outlier": False,
                        "offline_included": None, "offline_quantity": None},
            "Meso B2": {"online": None, "online_quantity": None,  # no online listing - must be excluded
                        "offline_included": 10.0, "offline_quantity": 5},
            "Neo C3": {"online": 4.0, "online_quantity": 2, "online_subtype_matched": True,
                       "online_is_outlier": False, "offline_included": None, "offline_quantity": None},
            "Axi D4": {"online": 3.0, "online_quantity": 1, "online_subtype_matched": True,
                       "online_is_outlier": False, "offline_included": None, "offline_quantity": None},
        }
        return Snapshot(
            relics=self.relics, refinement=refinement, prices=prices, relic_prices=relic_prices,
            ducats={}, name_to_slug={}, catalog_source="test", catalog_age_seconds=0.0,
        )


@unittest.skipUnless(_DEPS_AVAILABLE, "discord/aiohttp stubs not available in this environment")
class TestBaseOnlineRows(unittest.TestCase):
    def setUp(self):
        self.manager = dll.LiveListManager(bot=None, state=FakeState())
        self.manager._save = lambda: None  # never touch a real state file in tests

    def test_only_vaulted_relics_with_online_listing_included(self):
        rows = self.manager.base_online_rows("radiant")
        names = {r["relic"].relic_name for r in rows}
        self.assertIn("Lith A1", names)
        self.assertNotIn("Meso B2", names, "relic with no online listing must be excluded from the live list")
        self.assertNotIn("Neo C3", names, "unvaulted relic must be excluded from every live list")
        self.assertNotIn("Axi D4", names, "unknown vault status must not be presented as vaulted")


@unittest.skipUnless(_DEPS_AVAILABLE, "discord/aiohttp stubs not available in this environment")
class TestRelicEmojis(unittest.TestCase):
    class FakeEmoji:
        def __init__(self, name, rendered):
            self.name = name
            self.rendered = rendered

        def __str__(self):
            return self.rendered

    class FakeGuild:
        def __init__(self, emojis):
            self.emojis = emojis

    def test_custom_emoji_name_matches_server_convention(self):
        self.assertEqual(dll._relic_emoji_name("Lith A1", "radiant"), "LithRelicRadiant")
        self.assertEqual(dll._relic_emoji_name("axi d4", "intact"), "AxiRelicIntact")

    def test_matching_custom_guild_emoji_is_used(self):
        rendered = "<:MesoRelicFlawless:123456>"
        guild = self.FakeGuild([self.FakeEmoji("MesoRelicFlawless", rendered)])
        self.assertEqual(dll._relic_emoji(guild, "Meso B2", "flawless"), rendered)

    def test_missing_custom_emoji_uses_era_fallback(self):
        self.assertEqual(dll._relic_emoji(self.FakeGuild([]), "Neo C3", "exceptional"), "⚪")

    def test_void_tear_is_used_for_relic_list_headings(self):
        rendered = "<:VoidTear:999>"
        guild = self.FakeGuild([self.FakeEmoji("VoidTear", rendered)])
        self.assertEqual(dll._void_tear_emoji(guild), rendered)


@unittest.skipUnless(_DEPS_AVAILABLE, "discord/aiohttp stubs not available in this environment")
class TestFilterAndRank(unittest.TestCase):
    def setUp(self):
        self.manager = dll.LiveListManager(bot=None, state=FakeState())
        self.manager._save = lambda: None

    def test_max_cost_filter_applied_per_channel(self):
        base_rows = self.manager.base_online_rows("radiant")
        cfg = self.manager._config(111, "cheapest")
        cfg["max_cost"] = 1.0  # Lith A1 costs 5.0p, should be filtered out
        rows = self.manager.filter_and_rank(base_rows, 111, "cheapest")
        self.assertEqual(rows, [])

    def test_no_filters_keeps_all_base_rows(self):
        base_rows = self.manager.base_online_rows("radiant")
        rows = self.manager.filter_and_rank(base_rows, 111, "cheapest")
        self.assertEqual(len(rows), len(base_rows))


@unittest.skipUnless(_DEPS_AVAILABLE, "discord/aiohttp stubs not available in this environment")
class TestUpdateGuildBatchesByRefinement(unittest.IsolatedAsyncioTestCase):
    """
    THE regression test for the production incident: update_guild() must
    compute the expensive part (base_online_rows_async) exactly ONCE per
    DISTINCT refinement actually configured across the 7 list channels,
    not once per channel - and every channel sharing a refinement must
    receive the SAME computed rows object, proving genuine reuse rather
    than each channel independently recomputing.
    """

    async def test_single_shared_refinement_computed_exactly_once(self):
        manager = dll.LiveListManager(bot=None, state=FakeState())
        manager._save = lambda: None

        call_count = {"n": 0}
        received_base_rows = []

        async def fake_base_online_rows_async(refinement):
            call_count["n"] += 1
            return [{"marker": "shared-computation", "refinement": refinement}]

        async def fake_update_channel(guild_id, channel_key, page=None, base_rows=None):
            received_base_rows.append(base_rows)

        manager.base_online_rows_async = fake_base_online_rows_async
        manager.update_channel = fake_update_channel

        await manager.update_guild(111)

        # All 7 channels default to "radiant" - the expensive computation
        # must run exactly once, not once per channel.
        self.assertEqual(call_count["n"], 1)
        self.assertEqual(len(received_base_rows), len(dll.LIST_CHANNELS))
        # Every channel must have received the SAME (reused) rows object,
        # not independently recomputed copies.
        for rows in received_base_rows:
            self.assertIs(rows, received_base_rows[0])

    async def test_channels_with_different_refinements_each_get_their_own_computation(self):
        manager = dll.LiveListManager(bot=None, state=FakeState())
        manager._save = lambda: None
        # Configure one channel to a different refinement than the rest.
        manager._config(111, "cheapest")["refinement"] = "intact"

        call_refinements = []

        async def fake_base_online_rows_async(refinement):
            call_refinements.append(refinement)
            return [{"refinement": refinement}]

        async def fake_update_channel(guild_id, channel_key, page=None, base_rows=None):
            pass

        manager.base_online_rows_async = fake_base_online_rows_async
        manager.update_channel = fake_update_channel

        await manager.update_guild(111)

        # 6 channels at the default "radiant" (1 shared computation) + 1
        # channel at "intact" (its own computation) = 2 total, not 7.
        self.assertEqual(sorted(call_refinements), ["intact", "radiant"])

    async def test_one_channel_failing_does_not_block_the_rest(self):
        manager = dll.LiveListManager(bot=None, state=FakeState())
        manager._save = lambda: None

        updated = []

        async def fake_base_online_rows_async(refinement):
            return ["shared"]

        async def fake_update_channel(guild_id, channel_key, page=None, base_rows=None):
            if channel_key == "cheapest":
                raise RuntimeError("simulated failure")
            updated.append(channel_key)

        manager.base_online_rows_async = fake_base_online_rows_async
        manager.update_channel = fake_update_channel

        await manager.update_guild(111)

        self.assertNotIn("cheapest", updated)
        self.assertEqual(len(updated), len(dll.LIST_CHANNELS) - 1)


@unittest.skipUnless(_DEPS_AVAILABLE, "discord/aiohttp stubs not available in this environment")
class TestPreRefreshSetup(unittest.IsolatedAsyncioTestCase):
    async def test_setup_cleanup_prunes_only_servers_the_bot_has_left(self):
        manager = dll.LiveListManager(bot=SimpleNamespace(get_guild=lambda guild_id: object() if guild_id == 222 else None), state=FakeState())
        manager.data = {"guilds": {"111": {}, "222": {}, "bad": {}}}
        manager._unavailable_guilds_logged = {111}

        removed = manager._prune_unavailable_guilds(222)

        self.assertEqual(removed, [111])
        self.assertEqual(set(manager.data["guilds"]), {"222"})

    async def test_update_before_market_start_posts_waiting_board(self):
        class FakeMessage:
            def __init__(self, message_id):
                self.id = message_id
                self.edits = []

            async def edit(self, **kwargs):
                self.edits.append(kwargs)

        class FakeChannel:
            def __init__(self):
                self.sent = []

            async def fetch_message(self, _message_id):
                return None

            async def send(self, **kwargs):
                message = FakeMessage(123)
                message.payload = kwargs
                self.sent.append(message)
                return message

        channel = FakeChannel()
        bot = SimpleNamespace(
            get_channel=lambda _channel_id: channel,
            fetch_channel=None,
        )
        fake_state = SimpleNamespace(live_market=None)
        manager = dll.LiveListManager(bot=bot, state=fake_state)
        manager.data = {}
        manager._save = lambda: None
        manager._config(1, "cheapest")["channel_id"] = 10

        with patch.object(dll.discord, "TextChannel", FakeChannel):
            await manager.update_channel(1, "cheapest")

        self.assertEqual(len(channel.sent), 1)
        embed = channel.sent[0].payload["embed"]
        self.assertIn("/relics refresh", embed.description)
        self.assertEqual(manager._config(1, "cheapest")["message_id"], 123)

    async def test_shared_computation_failure_is_not_repeated_for_every_channel(self):
        manager = dll.LiveListManager(bot=None, state=FakeState())
        manager._save = lambda: None
        calls = {"count": 0}

        async def fail_once(_refinement):
            calls["count"] += 1
            raise RuntimeError("calculation failed")

        manager.base_online_rows_async = fail_once
        errors = await manager.update_guild(1)

        self.assertEqual(calls["count"], 1)
        self.assertEqual(len(errors), 1)
        self.assertIn("calculation failed", errors[0])


if __name__ == "__main__":
    unittest.main()
