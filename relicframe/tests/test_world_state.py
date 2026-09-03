"""Tests for the live Warframe world-state feed and Discord rendering."""
import os
import tempfile
import time
import unittest
from datetime import datetime
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch
from zoneinfo import ZoneInfo

import discord_world_state as dws
from world_state import (
    BOUNTY_URL,
    OFFICIAL_WORLD_STATE_URL,
    STATIC_RETRY_SECONDS,
    WORLD_STATE_URL,
    WorldStateClient,
    current_arbitration,
    current_sp_incursions,
    load_arbitration_schedule,
    official_world_state_is_fresh,
    parse_official_alerts,
    parse_official_daily_deals,
    parse_official_events,
    parse_official_invasions,
    parse_official_news,
    parse_official_sortie,
    parse_official_void_trader,
    parse_official_fissures,
    parse_sp_incursions,
)


class TestArbitrationSchedule(unittest.TestCase):
    def test_full_human_schedule_shape_is_parsed(self):
        text = (
            "Mon, December 31\n"
            "2300 • Survival - Infestation @ Assur, Uranus (F tier, 25% resource bonus)\n"
            "Tue, January 1\n"
            "0000 • Void Cascade - Grineer @ Tuvul Commons, Zariman (A tier)\n"
        )
        with tempfile.NamedTemporaryFile("w", encoding="utf-8", delete=False) as handle:
            handle.write(text)
            path = handle.name
        try:
            entries = load_arbitration_schedule(path, start_year=2026)
        finally:
            os.unlink(path)

        self.assertEqual(len(entries), 2)
        self.assertEqual(entries[0].mission_type, "Survival")
        self.assertEqual(entries[0].enemy, "Infestation")
        self.assertEqual(entries[0].tier, "F")
        self.assertEqual(entries[0].resource_bonus, "25% resource bonus")
        self.assertEqual(entries[1].mission_type, "Void Cascade")
        self.assertEqual(datetime.fromtimestamp(entries[1].activation, ZoneInfo("America/New_York")).year, 2027)

    def test_current_entry_lookup(self):
        text = "Mon, August 24, 2026\n0400 • Defense - Grineer @ Hydron, Sedna (B tier)\n"
        with tempfile.NamedTemporaryFile("w", encoding="utf-8", delete=False) as handle:
            handle.write(text)
            path = handle.name
        try:
            entries = load_arbitration_schedule(path, start_year=2026)
        finally:
            os.unlink(path)
        self.assertIs(current_arbitration(entries, entries[0].activation + 30), entries[0])
        self.assertIsNone(current_arbitration(entries, entries[0].expiry + 1))


class TestSteelPathSchedule(unittest.TestCase):
    def test_schedule_parser_and_current_day(self):
        schedule = parse_sp_incursions("100;SolNode1,SolNode2\nbad\n200;SolNode3\n")
        self.assertEqual(schedule[100], ["SolNode1", "SolNode2"])
        # The current helper rounds down to a UTC day boundary.
        day = 20 * 86400
        result = current_sp_incursions({day: ["SolNode9"]}, now=day + 10)
        self.assertEqual(result, (day, ["SolNode9"]))


def _mongo_date(milliseconds):
    return {"$date": {"$numberLong": str(milliseconds)}}


class TestOfficialMissionFallback(unittest.IsolatedAsyncioTestCase):
    async def test_failed_static_bootstrap_retries_instead_of_caching_failure_for_a_day(self):
        class FakeClient(WorldStateClient):
            fail = True

            async def _json(self, url):
                if self.fail:
                    raise RuntimeError("temporary static failure")
                return {"loaded": url}

            async def _text(self, _url):
                if self.fail:
                    raise RuntimeError("temporary static failure")
                return "100;SolNode1\n"

        client = FakeClient()
        await client._load_static()
        self.assertFalse(client.static_complete)
        self.assertEqual(client.static_loaded_at, 0.0)

        client.fail = False
        client.static_attempted_at -= STATIC_RETRY_SECONDS + 1
        await client._load_static()
        self.assertTrue(client.static_complete)
        self.assertTrue(client.regions)
        self.assertIn(100, client.sp_incursions)

    async def test_invalid_general_world_payload_has_a_clear_error(self):
        class FakeClient(WorldStateClient):
            async def _load_static(self, force=False):
                return None

            async def _json(self, url):
                if url == WORLD_STATE_URL:
                    return []
                if url == BOUNTY_URL:
                    return {}
                raise AssertionError(url)

        with self.assertRaisesRegex(RuntimeError, "invalid world-state payload"):
            await FakeClient().fetch()

    def test_raw_fissures_and_storms_are_normalized(self):
        now_ms = int(time.time() * 1000)
        raw = {
            "ActiveMissions": [{
                "_id": {"$oid": "f1"}, "Activation": _mongo_date(now_ms - 1000),
                "Expiry": _mongo_date(now_ms + 600_000), "Node": "SolNode1",
                "MissionType": "MT_VOID_CASCADE", "Modifier": "VoidT6", "Hard": True,
            }],
            "VoidStorms": [{
                "_id": {"$oid": "s1"}, "Activation": _mongo_date(now_ms - 1000),
                "Expiry": _mongo_date(now_ms + 600_000), "Node": "CrewNode1",
                "ActiveMissionTier": "VoidT3",
            }],
        }
        nodes = {
            "SolNode1": {"value": "Tuvul Commons (Zariman)", "type": "Void Cascade"},
            "CrewNode1": {"value": "Nu-gua Mines (Neptune)", "type": "Extermination"},
        }
        fissures = parse_official_fissures(raw, nodes)
        self.assertEqual(fissures[0]["tier"], "Omnia")
        self.assertEqual(fissures[0]["missionType"], "Void Cascade")
        self.assertTrue(fissures[0]["isHard"])
        self.assertEqual(fissures[1]["tier"], "Neo")
        self.assertTrue(fissures[1]["isStorm"])
        self.assertEqual(fissures[1]["node"], "Nu-gua Mines (Neptune)")

    def test_official_timestamp_must_be_recent(self):
        now = 2_000_000_000
        self.assertTrue(official_world_state_is_fresh({"Time": now - 30}, now))
        self.assertFalse(official_world_state_is_fresh({"Time": now - 3600}, now))

    def test_official_vendor_sortie_archon_invasion_and_baro_are_normalized(self):
        now = 2_000_000_000
        activation = _mongo_date((now - 60) * 1000)
        expiry = _mongo_date((now + 3600) * 1000)
        raw = {
            "DailyDeals": [{
                "StoreItem": "/Lotus/StoreItems/Upgrades/Focus/WardLensGreater",
                "Activation": activation, "Expiry": expiry, "Discount": 10,
                "OriginalPrice": 40, "SalePrice": 36, "AmountTotal": 150, "AmountSold": 26,
            }],
            "Sorties": [{
                "_id": {"$oid": "sortie"}, "Activation": activation, "Expiry": expiry,
                "Boss": "SORTIE_BOSS_VOR", "Variants": [{
                    "missionType": "MT_SABOTAGE", "modifierType": "SORTIE_MODIFIER_SNIPER_ONLY",
                    "node": "SolNode1",
                }],
            }],
            "LiteSorties": [{
                "_id": {"$oid": "archon"}, "Activation": activation, "Expiry": expiry,
                "Boss": "SORTIE_BOSS_NIRA", "Missions": [{"missionType": "MT_RESCUE", "node": "SolNode1"}],
            }],
            "Invasions": [{
                "_id": {"$oid": "inv"}, "Activation": activation, "Node": "SolNode1",
                "Count": -50, "Goal": 100, "Completed": False,
                "DefenderMissionInfo": {"faction": "FC_CORPUS"},
                "AttackerMissionInfo": {"faction": "FC_GRINEER"},
                "AttackerReward": {"countedItems": [{"ItemType": "/Lotus/Types/Items/Research/ChemComponent", "ItemCount": 3}]},
                "DefenderReward": {},
            }],
            "VoidTraders": [{
                "_id": {"$oid": "baro"}, "Activation": activation, "Expiry": expiry,
                "Character": "Baro'Ki Teel", "Node": "EarthHUB", "Manifest": [],
            }],
        }
        nodes = {"SolNode1": {"value": "Hydron (Sedna)"}, "EarthHUB": {"value": "Strata Relay (Earth)"}}

        deals = parse_official_daily_deals(raw)
        self.assertEqual(deals[0]["item"], "Greater Vazarin Lens")
        sortie = parse_official_sortie(raw, nodes, now=now)
        self.assertEqual(sortie["boss"], "Captain Vor")
        self.assertEqual(sortie["variants"][0]["modifier"], "Sniper Only")
        archon = parse_official_sortie(raw, nodes, archon=True, now=now)
        self.assertEqual(archon["boss"], "Nira")
        invasions = parse_official_invasions(raw, nodes)
        self.assertEqual(invasions[0]["attacker"]["reward"]["countedItems"][0]["type"], "Detonite Injector")
        self.assertEqual(invasions[0]["node"], "Hydron (Sedna)")
        baro = parse_official_void_trader(raw, nodes, now=now)
        self.assertEqual(baro["character"], "Baro Ki'Teer")
        self.assertEqual(baro["location"], "Strata Relay (Earth)")

    def test_official_news_alerts_and_events_are_normalized(self):
        now_ms = int(time.time() * 1000)
        raw = {
            "Events": [{
                "_id": {"$oid": "news"}, "Date": _mongo_date(now_ms - 1_000),
                "Messages": [{"LanguageCode": "en", "Message": "/News/Welcome"}],
                "Prop": "https://example.com/news", "Priority": True,
            }, {
                "_id": {"$oid": "foreign-news"}, "Date": _mongo_date(now_ms - 1_000),
                "Messages": [{"LanguageCode": "fr", "Message": "/News/Bonjour"}],
                "Prop": "https://example.com/fr",
            }],
            "Alerts": [{
                "_id": {"$oid": "alert"}, "Activation": _mongo_date(now_ms - 1_000),
                "Expiry": _mongo_date(now_ms + 600_000), "MissionInfo": {
                    "missionType": "MT_EXTERMINATION", "faction": "FC_GRINEER",
                    "location": "SolNode1", "minEnemyLevel": 10, "maxEnemyLevel": 15,
                    "missionReward": {"credits": 20_000},
                },
            }],
            "Goals": [{
                "_id": {"$oid": "event"}, "Activation": _mongo_date(now_ms - 1_000),
                "Expiry": _mongo_date(now_ms + 600_000), "Tag": "WaterFight",
                "Goal": 100, "Count": 25,
            }],
        }
        dictionary = {"/News/Welcome": "Welcome, Tenno"}
        nodes = {"SolNode1": {"value": "E Prime (Earth)"}}

        news = parse_official_news(raw, dictionary)
        self.assertEqual(len(news), 1)
        self.assertEqual(news[0]["message"], "Welcome, Tenno")
        self.assertTrue(news[0]["priority"])
        alerts = parse_official_alerts(raw, nodes)
        self.assertEqual(alerts[0]["mission"]["type"], "Extermination")
        self.assertEqual(alerts[0]["mission"]["faction"], "Grineer")
        self.assertEqual(alerts[0]["mission"]["node"], "E Prime (Earth)")
        self.assertEqual(alerts[0]["mission"]["reward"]["credits"], 20_000)
        events = parse_official_events(raw, dictionary)
        self.assertEqual(events[0]["description"], "Tactical Alert: Dog Days")
        self.assertEqual(events[0]["currentScore"], 25)

    async def test_stale_parsed_snapshot_uses_official_fissures(self):
        now = int(time.time())
        now_ms = now * 1000

        class FakeClient(WorldStateClient):
            async def _load_static(self, force=False):
                self.sol_nodes = {"SolNode1": {"value": "Hydron (Sedna)", "type": "Defense"}}

            async def _json(self, url):
                if url == WORLD_STATE_URL:
                    return {"timestamp": "2020-01-01T00:00:00Z", "fissures": []}
                if url == BOUNTY_URL:
                    return {}
                if url == OFFICIAL_WORLD_STATE_URL:
                    return {"Time": now, "ActiveMissions": [{
                        "_id": {"$oid": "fresh"}, "Activation": _mongo_date(now_ms - 1000),
                        "Expiry": _mongo_date(now_ms + 600_000), "Node": "SolNode1",
                        "MissionType": "MT_DEFENSE", "Modifier": "VoidT3",
                    }], "VoidStorms": []}
                raise AssertionError(url)

        data = await FakeClient().fetch()
        self.assertEqual(data["mission_source"], "official")
        self.assertFalse(data["mission_data_stale"])
        self.assertEqual(data["world"]["fissures"][0]["id"], "fresh")


def _sample_data():
    future = "2030-01-01T00:00:00Z"
    past = "2020-01-01T00:00:00Z"
    return {
        "world": {
            "cetusCycle": {"state": "day", "isDay": True, "expiry": future},
            "vallisCycle": {"state": "cold", "isWarm": False, "expiry": future},
            "cambionCycle": {"state": "fass", "expiry": future},
            "duviriCycle": {"state": "joy", "expiry": future, "choices": [
                {"category": "normal", "choices": ["Nidus"]},
                {"category": "hard", "choices": ["Vectis"]},
            ]},
            "zarimanCycle": {"state": "corpus", "expiry": future},
            "news": [{"id": "news1", "message": "News", "date": past, "link": "https://example.com"}],
            "kinepage": {"message": "Radio message", "timestamp": past},
            "alerts": [], "events": [],
            "sortie": {"id": "s1", "boss": "Boss", "expiry": future, "variants": []},
            "archonHunt": {"id": "a1", "boss": "Nira", "expiry": future, "missions": []},
            "steelPath": {"currentReward": {"name": "Endo", "cost": 150}, "expiry": future},
            "archimedeas": [],
            "dailyDeals": [{"item": "Lens", "activation": past, "expiry": future, "total": 75,
                            "sold": 1, "salePrice": 24, "discount": 40}],
            "voidTrader": {"activation": future, "expiry": future, "location": "Strata Relay", "inventory": []},
            "fissures": [{"id": "f1", "activation": past, "expiry": future, "tier": "Omnia", "tierNum": 6,
                          "missionType": "Void Cascade", "node": "Tuvul Commons (Zariman)",
                          "isHard": True, "isStorm": False}],
            "invasions": [],
            "arbitration": {"expired": True, "type": "Unknown"},
        },
        "bounty": None,
        "regions": {}, "challenges": {}, "dictionary": {}, "sol_nodes": {},
        "sp_incursions": {}, "fetched_at": 0,
    }


class TestDiscordWorldRendering(unittest.TestCase):
    def test_custom_server_emojis_are_used_for_requested_world_features(self):
        class FakeEmoji:
            def __init__(self, name, emoji_id):
                self.name = name
                self.id = emoji_id

            def __str__(self):
                return f"<:{self.name}:{self.id}>"

        names = ("VoidTear", "ThraxPlasm", "Survival", "VitusEssence", "OrokinDucats")
        guild = SimpleNamespace(emojis=[FakeEmoji(name, index + 1) for index, name in enumerate(names)])
        icons = dws.feature_emoji_map(guild)
        data = _sample_data()
        survival = [{
            "tier": "Neo", "tierNum": 3, "missionType": "Survival",
            "node": "Mot (Void)", "expiry": "2030-01-01T00:00:00Z",
        }]

        self.assertIn("<:VoidTear:1>", dws.fissure_embed(data, icons=icons).title)
        self.assertIn("<:ThraxPlasm:2>", dws.cascade_embed([], data, icons=icons).title)
        self.assertIn("<:Survival:3>", dws._fissure_lines(survival, icons=icons)[0])
        self.assertIn("<:VitusEssence:4>", dws.arbitration_embed([], data, icons=icons).title)
        self.assertIn("<:OrokinDucats:5>", dws.vendor_embeds(data, icons=icons)[-1].title)

    def test_every_feed_channel_builds_valid_embeds(self):
        data = _sample_data()
        for key in dws.CHANNELS:
            if key == "world-pings":
                continue
            embeds = dws.build_channel_embeds(key, data, [])
            self.assertTrue(embeds, key)
            self.assertLessEqual(len(embeds), 10, key)
            self.assertLessEqual(sum(len(embed) for embed in embeds), 6000, key)
            for embed in embeds:
                self.assertLessEqual(len(embed.description or ""), 4096, key)
                self.assertLessEqual(len(embed.fields), 25, key)

    def test_cascade_notification_includes_fissure(self):
        signatures = dws.notification_signatures(_sample_data(), [], now=1_800_000_000)
        self.assertEqual(signatures["cascade"], ["f1"])

    def test_fissures_are_split_into_general_and_tier_roles(self):
        signatures = dws.notification_signatures(_sample_data(), [], now=1_800_000_000)
        self.assertEqual(signatures["fissures"], [])
        self.assertEqual(signatures["steel_fissures"], ["f1"])
        self.assertEqual(signatures["steel_fissure_omnia"], ["f1"])
        self.assertEqual(signatures["fissure_omnia"], [])
        self.assertEqual(signatures["void_storms"], [])

    def test_fissure_rows_include_matching_arbitration_tier(self):
        entry = SimpleNamespace(location="Hydron, Sedna", mission_type="Defense", tier="B")
        item = {
            "tier": "Neo", "tierNum": 3, "missionType": "Defense",
            "node": "Hydron (Sedna)", "expiry": "2030-01-01T00:00:00Z",
        }
        line = dws._fissure_lines([item], [entry])[0]
        self.assertIn("Lvl B tier", line)
        self.assertNotIn("Arbitration B tier", line)

    def test_stale_empty_fissure_feed_reports_delay_not_no_missions(self):
        data = _sample_data()
        data["world"]["fissures"] = []
        data["mission_data_stale"] = True
        embed = dws.fissure_embed(data)
        self.assertIn("source is delayed", embed.description)
        self.assertNotIn("Nothing active", embed.description)

    def test_inactive_darvo_deal_does_not_render_fake_price_or_stock(self):
        data = _sample_data()
        data["world"]["dailyDeals"] = []
        darvo = dws.vendor_embeds(data)[0]
        self.assertEqual(darvo.description, "No active Darvo deal right now.")
        self.assertNotIn("? Platinum", darvo.description)
        self.assertNotIn("0/0", darvo.description)
        self.assertNotIn("time unavailable", darvo.description)

    def test_empty_world_boards_do_not_render_fake_unknown_values(self):
        data = {
            "world": {}, "bounty": None, "regions": {}, "challenges": {},
            "dictionary": {}, "sol_nodes": {}, "sp_incursions": {},
            "world_state_stale": True,
        }
        rendered = []
        for key in dws.CHANNELS:
            if key == "world-pings":
                continue
            rendered.extend(dws.build_channel_embeds(key, data, []))
        body = "\n".join(
            (embed.description or "") + "\n" + "\n".join(field.value for field in embed.fields)
            for embed in rendered
        )
        self.assertNotIn("time unavailable", body.casefold())
        self.assertNotIn("? platinum", body.casefold())
        self.assertNotIn("0/0 in stock", body.casefold())
        self.assertNotIn("unknown archon", body.casefold())

    def test_role_menus_cover_every_role_with_valid_option_counts(self):
        menu_roles = [key for _menu, _placeholder, keys in dws.ROLE_MENUS for key in keys]
        self.assertEqual(set(menu_roles), set(dws.ROLES))
        self.assertEqual(len(menu_roles), len(set(menu_roles)))
        self.assertLessEqual(len(dws.ROLE_MENUS), 5)
        self.assertTrue(all(1 <= len(keys) <= 25 for _menu, _placeholder, keys in dws.ROLE_MENUS))

    def test_setup_defines_opt_in_role_for_each_notification(self):
        self.assertEqual(set(dws.ROLES), set(dws.PING_TEXT))
        self.assertEqual(set(dws.ROLES), set(dws.ROLE_CHANNEL))

    def test_hex_bounty_game_markup_is_removed(self):
        key = "/Challenge/Hex"
        data = {
            "dictionary": {
                "/Name": "Back Breaker",
                "/Description": "|OPEN_COLOR||ALLY| Bounty|CLOSE_COLOR|\r\nDestroy |COUNT| backpacks",
            },
            "challenges": {
                key: {"name": "/Name", "description": "/Description", "requiredCount": 9},
            },
        }
        rendered = dws._challenge_text(data, key)
        self.assertEqual(rendered, "Back Breaker: Destroy 9 backpacks")
        self.assertNotIn("|", rendered)

    def test_bounty_rows_split_only_between_complete_rows(self):
        embed = dws._embed("Bounties", "Current")
        rows = [f"**Mission {index}**\n" + ("objective " * 40) for index in range(7)]
        dws._add_section_fields(embed, "The Hex", rows, limit=500)
        self.assertGreater(len(embed.fields), 1)
        for field in embed.fields:
            self.assertLessEqual(len(field.value), 500)
            self.assertEqual(field.value.count("**") % 2, 0)


class _FakeMessage:
    def __init__(self, message_id, author_id, content):
        self.id = message_id
        self.author = SimpleNamespace(id=author_id)
        self.content = content
        self.deleted = False

    async def delete(self):
        self.deleted = True

    async def edit(self, **kwargs):
        self.edited = kwargs


class _FakeChannel:
    def __init__(self, history_messages):
        self.messages = {message.id: message for message in history_messages}
        self.history_messages = history_messages
        self.sent = []

    async def fetch_message(self, message_id):
        if message_id not in self.messages:
            raise TypeError("message missing")
        return self.messages[message_id]

    async def send(self, content=None, **_kwargs):
        message = _FakeMessage(1000 + len(self.sent), 99, content)
        self.messages[message.id] = message
        self.sent.append(message)
        return message

    def history(self, limit=100):
        async def iterator():
            for message in self.history_messages[:limit]:
                yield message
        return iterator()


class TestRollingChannelPing(unittest.IsolatedAsyncioTestCase):
    async def test_one_broken_board_does_not_block_other_world_channels(self):
        cfg = {"channels": {"world-pings": {}, "broken": {}, "healthy": {}}}
        manager = object.__new__(dws.WorldStateManager)
        manager._guild = lambda _guild_id: cfg
        manager._ping_embed = lambda *_args: dws._embed("Pings", "roles")
        manager._save = lambda: None

        updated = []

        async def update_message(_guild_id, key, _embeds, _view=None):
            updated.append(key)
            if key == "broken":
                raise RuntimeError("channel unavailable")

        manager._update_message = update_message
        manager._send_new_pings = AsyncMock(return_value=None)
        manager.arbitration_entries = []

        with patch.object(dws, "CHANNELS", {"world-pings": "", "broken": "", "healthy": ""}), \
             patch.object(dws, "build_channel_embeds", return_value=[dws._embed("Board", "data")]), \
             patch.object(dws, "PingRoleView", return_value=None):
            with self.assertRaisesRegex(RuntimeError, "1 world-feed update"):
                await manager.update_guild(1, {})

        self.assertEqual(updated, ["world-pings", "broken", "healthy"])
        manager._send_new_pings.assert_awaited_once()

    async def test_refresh_ignores_corrupt_saved_server_ids(self):
        manager = object.__new__(dws.WorldStateManager)
        manager._refresh_lock = dws.asyncio.Lock()
        manager.client = SimpleNamespace(fetch=AsyncMock(return_value={"timestamp": 1}))
        manager.data = {"guilds": {"bad-id": {}, "123": {}}}
        manager.update_guild = AsyncMock(return_value=None)
        manager.last_error = "old"

        await manager.refresh_all()

        manager.update_guild.assert_awaited_once_with(123, {"timestamp": 1})
        self.assertIsNone(manager.last_error)

    async def test_uninstalled_saved_server_is_skipped_without_channel_updates(self):
        manager = object.__new__(dws.WorldStateManager)
        manager._refresh_lock = dws.asyncio.Lock()
        manager.client = SimpleNamespace(fetch=AsyncMock(return_value={"timestamp": 1}))
        manager.data = {"guilds": {"123": {}}}
        manager.bot = SimpleNamespace(get_guild=lambda _guild_id: None)
        manager.update_guild = AsyncMock(return_value=None)
        manager.last_error = None
        manager._unavailable_guilds_logged = set()

        await manager.refresh_all()
        await manager.refresh_all()

        manager.update_guild.assert_not_awaited()
        self.assertEqual(manager._unavailable_guilds_logged, {123})
        self.assertIn("not installed", manager.last_error)

    def test_setup_prunes_only_servers_the_bot_has_left(self):
        manager = object.__new__(dws.WorldStateManager)
        manager.data = {"guilds": {"111": {}, "222": {}, "bad": {}}}
        manager.bot = SimpleNamespace(get_guild=lambda guild_id: object() if guild_id == 222 else None)
        manager._unavailable_guilds_logged = {111}

        removed = manager._prune_unavailable_guilds(222)

        self.assertEqual(removed, [111])
        self.assertEqual(set(manager.data["guilds"]), {"222"})

    async def test_deleted_unchanged_board_is_recreated_after_verification_interval(self):
        channel = _FakeChannel([])
        cfg = {
            "channels": {"world-alerts": {
                "channel_id": 10, "message_id": 20,
                "render_hash": "will-be-replaced", "verified_at": 0,
            }},
        }
        manager = object.__new__(dws.WorldStateManager)
        manager._guild = lambda _guild_id: cfg

        async def get_channel(_channel_id):
            return channel

        manager._channel = get_channel
        embed = dws._embed("Alerts", "Nothing active right now.")
        payload = dws.json.dumps([embed.to_dict()], sort_keys=True, ensure_ascii=False)
        cfg["channels"]["world-alerts"]["render_hash"] = dws.hashlib.sha256(payload.encode("utf-8")).hexdigest()

        with patch.object(dws.discord, "TextChannel", _FakeChannel):
            await manager._update_message(1, "world-alerts", [embed])

        self.assertEqual(len(channel.sent), 1)
        self.assertEqual(cfg["channels"]["world-alerts"]["message_id"], channel.sent[0].id)
        self.assertGreater(cfg["channels"]["world-alerts"]["verified_at"], 0)

    async def test_failed_ping_is_not_marked_delivered_and_retries(self):
        channel = _FakeChannel([])
        cfg = {
            "signatures": {"alerts": ["old"]},
            "roles": {"alerts": 5},
            "channels": {"world-alerts": {"channel_id": 7}},
        }
        role = SimpleNamespace(mention="<@&5>")
        guild = SimpleNamespace(get_role=lambda _role_id: role)
        manager = object.__new__(dws.WorldStateManager)
        manager._guild = lambda _guild_id: cfg
        manager.arbitration_entries = []
        manager.bot = SimpleNamespace(get_guild=lambda _guild_id: guild)

        async def get_channel(_channel_id):
            return channel

        manager._channel = get_channel
        manager._replace_channel_ping = AsyncMock(side_effect=RuntimeError("Discord unavailable"))
        with patch.object(dws, "notification_signatures", return_value={"alerts": ["old", "new"]}), \
             patch.object(dws.discord, "TextChannel", _FakeChannel):
            with self.assertRaisesRegex(RuntimeError, "Could not send"):
                await manager._send_new_pings(1, {})
        self.assertEqual(cfg["signatures"]["alerts"], ["old"])

        manager._replace_channel_ping = AsyncMock(return_value=None)
        with patch.object(dws, "notification_signatures", return_value={"alerts": ["old", "new"]}), \
             patch.object(dws.discord, "TextChannel", _FakeChannel):
            await manager._send_new_pings(1, {})
        self.assertEqual(cfg["signatures"]["alerts"], ["old", "new"])

    async def test_grouped_role_picker_builds_with_five_select_menus(self):
        view = dws.PingRoleView(SimpleNamespace(), 123)
        try:
            self.assertEqual(len(view.children), 5)
            self.assertTrue(all(len(menu.options) <= 25 for menu in view.children))
        finally:
            view.stop()

    async def test_old_bot_pings_are_deleted_before_new_ping(self):
        content = "<@&5>\n• New Void Fissures are available."
        old_one = _FakeMessage(1, 99, content)
        old_two = _FakeMessage(2, 99, content)
        user_post = _FakeMessage(3, 42, content)
        channel = _FakeChannel([old_one, user_post, old_two])
        manager = object.__new__(dws.WorldStateManager)
        manager.bot = SimpleNamespace(user=SimpleNamespace(id=99))
        cfg = {}

        await manager._replace_channel_ping(cfg, "world-fissures", channel, content)

        self.assertTrue(old_one.deleted)
        self.assertTrue(old_two.deleted)
        self.assertFalse(user_post.deleted)
        self.assertEqual(len(channel.sent), 1)
        self.assertEqual(cfg["ping_messages"]["world-fissures"], channel.sent[0].id)

    async def test_cleanup_and_replacement_apply_to_non_fissure_channels(self):
        content = "<@&7>\n• A new Warframe alert is active."
        old_ping = _FakeMessage(10, 99, content)
        user_post = _FakeMessage(11, 42, content)
        channel = _FakeChannel([old_ping, user_post])
        manager = object.__new__(dws.WorldStateManager)
        manager.bot = SimpleNamespace(user=SimpleNamespace(id=99))
        cfg = {}

        await manager._replace_channel_ping(cfg, "world-alerts", channel, content)

        self.assertTrue(old_ping.deleted)
        self.assertFalse(user_post.deleted)
        self.assertEqual(cfg["ping_messages"]["world-alerts"], channel.sent[0].id)


if __name__ == "__main__":
    unittest.main()
