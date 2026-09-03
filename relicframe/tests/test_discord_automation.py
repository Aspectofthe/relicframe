import asyncio
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, Mock, patch

import discord
import discord_automation as da
import discord_world_state as dws
from bot_guide import guide_embeds
from http_client import WfmHttpClient
from state import AutoRefreshConfig, BotState


class TestRefreshController(unittest.IsolatedAsyncioTestCase):
    def make_controller(self, runner):
        state = SimpleNamespace(auto_refresh=AutoRefreshConfig(), live_market=None, close=AsyncMock())
        controller = da.RefreshController(state, runner)
        self.addAsyncCleanup(controller.stop)
        return controller

    async def test_default_is_radiant_one_minute_force_and_starts_immediately(self):
        called = asyncio.Event()
        async def runner(*args):
            called.set()
            return True, "ok"
        mock = AsyncMock(side_effect=runner)
        controller = self.make_controller(mock)
        channel = SimpleNamespace(id=12)
        await controller.start(channel, 8, default=True)
        await asyncio.wait_for(called.wait(), 1)
        mock.assert_awaited_once_with(channel, "radiant", True)
        self.assertEqual(controller.state.auto_refresh.interval_minutes, 1)
        task = controller.task
        self.assertFalse(await controller.start(channel, 8, default=True))
        self.assertIs(controller.task, task)

    async def test_stop_cancels_bootstrap_closes_market_and_blocks_default_restart(self):
        started, cleaned = asyncio.Event(), asyncio.Event()
        async def runner(*args):
            started.set()
            try:
                await asyncio.Event().wait()
            finally:
                cleaned.set()
        controller = self.make_controller(runner)
        await controller.start(SimpleNamespace(id=1), 1)
        await asyncio.wait_for(started.wait(), 1)
        self.assertTrue(await controller.stop())
        self.assertTrue(cleaned.is_set())
        self.assertIsNone(controller.task)
        self.assertFalse(controller.state.auto_refresh.enabled)
        controller.state.close.assert_awaited()
        self.assertFalse(await controller.start(None, None, default=True))
        await controller.start(SimpleNamespace(id=2), 2, 5, "intact", False)
        self.assertEqual(controller.state.auto_refresh.refinement, "intact")
        self.assertEqual(controller.state.auto_refresh.interval_minutes, 5)
        self.assertFalse(controller.state.auto_refresh.force_catalog_refresh)

    async def test_kill_cancels_a_manual_refresh_too(self):
        started = asyncio.Event()
        async def runner(*args):
            started.set()
            await asyncio.Event().wait()
        controller = self.make_controller(runner)
        task = asyncio.create_task(controller.run_once(None, "radiant", True))
        await asyncio.wait_for(started.wait(), 1)
        await controller.stop()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assertFalse(controller.manual)

    async def test_start_replaces_and_awaits_old_worker(self):
        events = []
        ready = asyncio.Event()
        async def runner(channel, *args):
            events.append(f"start:{channel.id}")
            ready.set()
            try:
                await asyncio.Event().wait()
            finally:
                events.append(f"stop:{channel.id}")
        controller = self.make_controller(runner)
        await controller.start(SimpleNamespace(id=1), 1)
        await asyncio.wait_for(ready.wait(), 1)
        ready.clear()
        await controller.start(SimpleNamespace(id=2), 1)
        await asyncio.wait_for(ready.wait(), 1)
        self.assertEqual(events[:3], ["start:1", "stop:1", "start:2"])


class FakeChannel:
    def __init__(self, channel_id, name, category=None):
        self.id, self.name, self.category = channel_id, name, category
        self.edit_calls = []
    async def edit(self, **kwargs):
        self.edit_calls.append(kwargs)
        self.name = kwargs.get("name", self.name)


class FakeCategory(FakeChannel):
    def __init__(self, channel_id, name):
        super().__init__(channel_id, name)
        self.text_channels = []


class FakeGuild:
    def __init__(self, guild_id):
        self.id, self.unavailable = guild_id, False
        self.categories, self.roles = [], []
        self.channels = {}
        self.created = 0
    def get_channel(self, channel_id):
        return self.channels.get(channel_id)
    def get_role(self, role_id):
        return next((role for role in self.roles if role.id == role_id), None)
    async def create_category(self, name, **kwargs):
        channel = FakeCategory(len(self.channels) + 1, name)
        self.channels[channel.id] = channel
        self.categories.append(channel)
        return channel
    async def create_text_channel(self, name, category=None, **kwargs):
        channel = FakeChannel(len(self.channels) + 1, name, category)
        self.channels[channel.id] = channel
        if category:
            category.text_channels.append(channel)
        self.created += 1
        return channel
    async def create_role(self, name, **kwargs):
        role = FakeChannel(1000 + len(self.roles), name)
        self.roles.append(role)
        return role


class TestProvisioning(unittest.IsolatedAsyncioTestCase):
    async def test_world_migration_renames_in_place_and_preserves_role_membership_id(self):
        guild = FakeGuild(7)
        category = await guild.create_category(dws.WORLD_CATEGORY_NAME)
        old = await guild.create_text_channel("world-news", category)
        role = await guild.create_role("Void Cascade Ping")
        bot = SimpleNamespace(get_guild=lambda _: guild)
        with patch.object(dws, "_load_state", return_value={}), \
             patch.object(dws, "load_arbitration_schedule", return_value=[]):
            manager = dws.WorldStateManager(bot)
        manager._save = Mock()
        manager.refresh_all = AsyncMock()
        manager._prune_unavailable_guilds = Mock()
        cfg = manager._guild(guild.id)
        cfg["channels"] = {"world-news": {"channel_id": old.id, "message_id": 99}}
        cfg["roles"] = {"cascade": role.id}
        with patch.object(discord, "TextChannel", FakeChannel), patch.object(discord, "CategoryChannel", FakeCategory):
            await manager.setup_guild(guild, automatic=True)
            count = guild.created
            await manager.setup_guild(guild, automatic=True)
        self.assertEqual(old.name, "warframe-news")
        self.assertEqual(cfg["channels"]["world-news"]["message_id"], 99)
        self.assertEqual(cfg["roles"]["cascade"], role.id)
        self.assertEqual(role.name, "Normal Cascade Fissure Ping")
        self.assertIn("steel_cascade", cfg["roles"])
        self.assertEqual(guild.created, count)
        self.assertEqual(set(c.name for c in category.text_channels), set(dws.CHANNEL_NAMES.values()))
        manager._prune_unavailable_guilds.assert_not_called()

    async def test_startup_join_reconnect_use_one_shared_default_worker(self):
        guild1, guild2 = FakeGuild(1), FakeGuild(2)
        cfgs = {}
        world = SimpleNamespace(_guild=lambda gid: cfgs.setdefault(gid, {}),
                                _save=Mock(), setup_guild=AsyncMock())
        lists = SimpleNamespace(setup_guild=AsyncMock())
        state = SimpleNamespace(auto_refresh=AutoRefreshConfig(), live_market=None, close=AsyncMock())
        refresh = da.RefreshController(state, AsyncMock(return_value=(True, "ok")))
        self.addAsyncCleanup(refresh.stop)
        auto = da.GuildAutomation(SimpleNamespace(guilds=[guild1]), world, lists, refresh)
        with patch.object(discord, "TextChannel", FakeChannel):
            await auto.startup()
            task = refresh.task
            await auto.startup()
            await auto.startup([guild2])
            self.assertIs(refresh.task, task)
            self.assertEqual(world.setup_guild.await_count, 2)
            self.assertEqual(lists.setup_guild.await_count, 2)
            self.assertEqual(set(auto.outputs), {1, 2})
            self.assertEqual(guild1.created, 1)
            await refresh.stop()
            await auto.startup()
            self.assertIsNone(refresh.task)

    async def test_failed_world_setup_does_not_block_lists_or_other_guilds(self):
        cfgs = {}
        world = SimpleNamespace(_guild=lambda gid: cfgs.setdefault(gid, {}), _save=Mock(),
                                setup_guild=AsyncMock(side_effect=[RuntimeError("no roles"), None]))
        lists = SimpleNamespace(setup_guild=AsyncMock())
        refresh = SimpleNamespace(configured=True)
        auto = da.GuildAutomation(SimpleNamespace(guilds=[FakeGuild(1), FakeGuild(2)]), world, lists, refresh)
        with patch.object(discord, "TextChannel", FakeChannel):
            await auto.startup()
        self.assertEqual(lists.setup_guild.await_count, 2)
        self.assertEqual(auto.prepared, {2})

    async def test_refresh_output_reuses_saved_message(self):
        message = SimpleNamespace(id=77, edit=AsyncMock())
        channel = SimpleNamespace(fetch_message=AsyncMock(return_value=message), send=AsyncMock(return_value=message))
        cfg = {}
        world = SimpleNamespace(_guild=lambda gid: cfg, _save=Mock())
        output = da.RefreshBroadcast(SimpleNamespace(outputs={1: channel}, world=world))
        await output.send(content="first")
        await output.send(content="second")
        channel.send.assert_awaited_once()
        message.edit.assert_awaited_once()
        self.assertEqual(cfg["refresh"]["message_id"], 77)


class TestCancellation(unittest.IsolatedAsyncioTestCase):
    async def test_cancel_bootstrap_closes_partial_market(self):
        ready = asyncio.Event()
        async def start(*args, **kwargs):
            ready.set()
            await asyncio.Event().wait()
        market = SimpleNamespace(start=AsyncMock(side_effect=start), stop=AsyncMock())
        state = BotState()
        state.relics = {"fake": object()}
        with patch("state.LiveMarket", return_value=market):
            task = asyncio.create_task(state.ensure_live_market_started())
            await asyncio.wait_for(ready.wait(), 1)
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
        market.stop.assert_awaited_once()
        self.assertIsNone(state.live_market)

    async def test_cancel_batch_drains_every_child_request(self):
        ready = asyncio.Event()
        active, cancelled = set(), set()
        client = WfmHttpClient()
        async def fetch(slug):
            active.add(slug)
            if len(active) == 3:
                ready.set()
            try:
                await asyncio.Event().wait()
            finally:
                cancelled.add(slug)
        with patch.object(client, "get_full_order_book", side_effect=fetch):
            task = asyncio.create_task(client.fetch_many_order_books(["a", "b", "c"], Mock()))
            await asyncio.wait_for(ready.wait(), 1)
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
        self.assertEqual(cancelled, {"a", "b", "c"})


class TestGuideAndCascade(unittest.TestCase):
    def test_guide_fits_discord_and_covers_feature_groups(self):
        embeds = guide_embeds()
        self.assertLessEqual(sum(map(len, embeds)), 6000)
        text = "\n".join(embed.description for embed in embeds)
        for group in ("/world", "/relics", "/riven", "/companion"):
            self.assertIn(group, text)
        self.assertIn("/relics stop", text)

    def test_only_active_fissures_trigger_separate_cascade_pings(self):
        base = {"missionType": "Void Cascade", "activation": "2020-01-01T00:00:00Z", "expiry": "2030-01-01T00:00:00Z"}
        data = {"world": {"fissures": [dict(base, id="normal"), dict(base, id="steel", isHard=True),
                                       dict(base, id="storm", isStorm=True), dict(base, id="expired", expiry="2020-01-01T00:00:00Z")]}}
        arb = SimpleNamespace(activation=1_800_000_000, expiry=1_800_003_600, mission_type="Void Cascade", tier="A", location="test")
        signatures = dws.notification_signatures(data, [arb], now=1_800_000_100)
        self.assertEqual(signatures["cascade"], ["normal"])
        self.assertEqual(signatures["steel_cascade"], ["steel"])
        embed = dws.cascade_embed([], data, now=1_800_000_100)
        self.assertNotIn("Upcoming", embed.description)
        self.assertNotIn("Arbitrations", embed.description)
        self.assertIn("Normal", embed.description)
        self.assertIn("Steel Path", embed.description)

    def test_visible_channel_names(self):
        self.assertEqual(dws.CHANNEL_NAMES["world-cycles"], "world-cycles")
        self.assertEqual(dws.CHANNEL_NAMES["world-news"], "warframe-news")
        self.assertEqual(dws.CHANNEL_NAMES["world-pings"], "role-pings")
        self.assertTrue(all(not name.startswith("world-") for name in dws.CHANNEL_NAMES.values()
                            if name != "world-cycles"))


class TestDiscordRefreshCommands(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        # Never load a developer's local credentials while importing commands.
        with patch("local_env.load_local_env", return_value=0):
            import bot
        self.module = bot

    async def test_kill_switch_requires_manage_server(self):
        interaction = SimpleNamespace(guild=object(), user=SimpleNamespace(
            guild_permissions=SimpleNamespace(manage_guild=False)),
            response=SimpleNamespace(send_message=AsyncMock(), defer=AsyncMock()),
            followup=SimpleNamespace(send=AsyncMock()))
        with patch.object(self.module.refresh_controller, "stop", new_callable=AsyncMock) as stop:
            await self.module._kill_refresh(interaction)
        stop.assert_not_awaited()
        self.assertIn("Manage Server", interaction.response.send_message.call_args.args[0])

    async def test_admin_stop_clears_cooldown_for_immediate_restart(self):
        interaction = SimpleNamespace(guild=object(), user=SimpleNamespace(
            guild_permissions=SimpleNamespace(manage_guild=True)),
            response=SimpleNamespace(send_message=AsyncMock(), defer=AsyncMock()),
            followup=SimpleNamespace(send=AsyncMock()))
        with patch.object(self.module, "_refresh_cooldowns", {1: 123}), \
             patch.object(self.module.refresh_controller, "stop", new_callable=AsyncMock) as stop:
            await self.module._kill_refresh(interaction)
            self.assertFalse(self.module._refresh_cooldowns)
        stop.assert_awaited_once()
        interaction.response.defer.assert_awaited_once_with(ephemeral=True)

    async def test_default_commands_and_persistent_button_register(self):
        params = {param.name: param for param in self.module.relics_refresh.parameters}
        self.assertEqual(params["interval_minutes"].default, 1.0)
        self.assertTrue(params["force_catalog_refresh"].default)
        self.assertIsNotNone(self.module.relics_group.get_command("stop"))
        self.assertTrue(self.module.RefreshStopView().is_persistent())
        text = "\n".join(embed.description for embed in guide_embeds())
        for group in self.module.bot.tree.get_commands():
            for command in group.commands:
                self.assertIn(command.name, text, command.qualified_name)


if __name__ == "__main__":
    unittest.main()
