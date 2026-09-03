import asyncio
import json
import tempfile
import unittest
from unittest.mock import patch
from pathlib import Path
from unittest.mock import AsyncMock

from riven_market import (
    GUN_CSV_HEADER,
    MELEE_CSV_HEADER,
    RivenMarketService,
    auction_roll_quality,
    calculate_riven_stat_ranges,
    disposition_band,
    find_riven_deals,
    format_roll_stat,
    parse_public_payload,
    parse_weapon_variants,
    parse_weekly,
    top_weekly_weapons,
)
from riven_roll_rules import (
    evaluate_curated_roll,
    parse_negative_expression,
    parse_rule_expression,
)


def weekly(weapon="Torid", *, rerolled=True, median=450, average=600, popularity=100):
    return {
        "itemType": "Rifle Riven Mod",
        "compatibility": weapon,
        "rerolled": rerolled,
        "avg": average,
        "median": median,
        "min": 10,
        "max": 5000,
        "stddev": 300,
        "pop": popularity,
    }


def auction(price, positives=("critical_chance", "critical_damage"), negative="zoom", *, status="online", auction_id="one"):
    attributes = [{"url_name": value, "positive": True, "value": 100} for value in positives]
    if negative:
        attributes.append({"url_name": negative, "positive": False, "value": -30})
    return {
        "id": auction_id,
        "visible": True,
        "closed": False,
        "is_direct_sell": True,
        "buyout_price": price,
        "owner": {"ingame_name": "Seller", "status": status},
        "item": {"attributes": attributes},
    }


class TestRivenMarket(unittest.TestCase):
    def test_workbook_expression_parser_keeps_required_stats_and_choice_pool(self):
        parsed = parse_rule_expression("CC MS FR/CD/DMG/HEAT")
        self.assertEqual(parsed[0]["mandatory"], [["critical_chance"], ["multishot"]])
        self.assertEqual(
            set(parsed[0]["pool"]),
            {"fire_rate_/_attack_speed", "critical_damage", "base_damage_/_melee_damage", "heat_damage"},
        )

    def test_curated_roll_requires_two_wanted_stats_harmless_negative_and_no_dead_third(self):
        rule = {
            "alternatives": list(parse_rule_expression("CC MS FR/CD/DMG/HEAT")),
            "harmless_negatives": list(parse_negative_expression("ZOOM/REC")),
        }
        good = evaluate_curated_roll(
            {"critical_chance", "multishot", "critical_damage"}, {"zoom"}, rule
        )
        dead_third = evaluate_curated_roll(
            {"critical_chance", "multishot", "puncture_damage"}, {"zoom"}, rule
        )
        no_negative = evaluate_curated_roll(
            {"critical_chance", "multishot"}, set(), rule
        )
        self.assertTrue(good.qualifies)
        self.assertFalse(dead_third.qualifies)
        self.assertFalse(no_negative.qualifies)

    def test_element_token_matches_any_basic_element(self):
        rule = {
            "alternatives": list(parse_rule_expression("DMG MS ELEMENT")),
            "harmless_negatives": list(parse_negative_expression("DTI")),
        }
        result = evaluate_curated_roll(
            {"base_damage_/_melee_damage", "multishot", "toxin_damage"},
            {"damage_vs_infested"},
            rule,
        )
        self.assertTrue(result.qualifies)

    def test_current_de_javascript_style_feed_is_parsed_without_eval(self):
        payload = parse_public_payload("[{itemType: 'Rifle Riven Mod', compatibility: 'Torid', rerolled: true, avg: 12.5, note: null}]")
        self.assertEqual(payload[0]["compatibility"], "Torid")
        self.assertTrue(payload[0]["rerolled"])
        self.assertIsNone(payload[0]["note"])

    def test_embedded_weapon_csv_extracts_variants_and_ignores_prose(self):
        gun_header = (
            "Name,Trigger,AttackName,Impact,Puncture,Slash,Cold,Electricity,Heat,Toxin,Blast,Corrosive,Gas,Magnetic,"
            "Radiation,Viral,Void,BaseDamage,BaseDps,TotalDamage,CritChance,CritMultiplier,AvgShotDmg,BurstDps,SustainedDps,"
            "LifetimeDmg,StatusChance,ForcedProcs,AvgProcCount,AvgProcPerSec,Multishot,FireRate,BurstCount,BurstDelay,"
            "BurstReloadDelay,ChargeTime,Disposition,Mastery,Magazine,AmmoPickup,AmmoMax,AmmoCost,Reload,ShotType,ShotSpeed,"
            "PunchThrough,Accuracy,Introduced,IntroducedDate,Slot,Class,AmmoType,Range,InternalName,Family,FalloffStart,"
            "FalloffEnd,FalloffReduction,AvgSpread,MinSpread,MaxSpread,IsSilent"
        )
        gun = ["0"] * len(gun_header.split(","))
        gun[0] = "Braton Prime"
        gun[36] = "1.1"
        gun[37] = "8"
        gun[49] = "Primary"
        gun[50] = "Rifle"
        gun[53] = "/Lotus/Weapons/Tenno/LongGuns/Braton/PrimeBraton"
        gun[54] = "Braton"
        melee_header = (
            "Name,AttackName,Impact,Puncture,Slash,Cold,Electricity,Heat,Toxin,Blast,Corrosive,Gas,Magnetic,Radiation,Viral,"
            "Void,BaseDamage,TotalDamage,CritChance,CritMultiplier,AvgShotDmg,StatusChance,ForcedProcs,AvgProcCount,FireRate,"
            "Disposition,Mastery,Introduced,IntroducedDate,Slot,Class,MeleeRange,SweepRadius,InternalName,Family"
        )
        melee = ["0"] * len(melee_header.split(","))
        melee[0] = "Coda Pathocyst"
        melee[25] = "0.6"
        melee[26] = "17"
        melee[29] = "Melee"
        melee[30] = "Glaive"
        melee[33] = "/Lotus/Weapons/Infested/CodaPathocyst"
        melee[34] = "Pathocyst"
        text = "prose that is not data\n" + gun_header + "\n" + ",".join(gun) + "\n" + melee_header + "\n" + ",".join(melee)
        variants = parse_weapon_variants(text)
        self.assertEqual([item["name"] for item in variants], ["Braton Prime", "Coda Pathocyst"])
        self.assertEqual(variants[0]["family"], "Braton")
        self.assertEqual(variants[1]["disposition"], 0.6)

    def test_official_weekly_rows_keep_rolled_and_unrolled_separate(self):
        rows = [weekly(rerolled=False, median=300), weekly(rerolled=True, median=700)]
        self.assertEqual(parse_weekly(rows, "torid", False).median, 300)
        self.assertEqual(parse_weekly(rows, "TORID", True).median, 700)

    def test_top_weekly_uses_official_popularity_score(self):
        rows = [weekly("Torid", popularity=100), weekly("Boar", popularity=42)]
        result = top_weekly_weapons(rows, "popularity", 2)
        self.assertEqual([item.weapon for item in result], ["Torid", "Boar"])

    def test_supplied_formula_calculates_two_positive_one_negative_ranges(self):
        ranges = calculate_riven_stat_ranges(
            slugs=("critical_chance", "critical_damage"),
            negative="zoom",
            stat_class="rifle",
            disposition=1.0,
        )
        self.assertAlmostEqual(ranges[0].minimum, 149.99 * 1.2375 * 0.9, places=3)
        self.assertAlmostEqual(ranges[0].maximum, 149.99 * 1.2375 * 1.1, places=3)
        self.assertAlmostEqual(ranges[-1].minimum, -(59.99 * 0.495 * 1.1), places=3)
        self.assertAlmostEqual(ranges[-1].maximum, -(59.99 * 0.495 * 0.9), places=3)

    def test_formula_rejects_positive_only_negative_and_wrong_weapon_class(self):
        with self.assertRaisesRegex(ValueError, "cannot roll as a negative"):
            calculate_riven_stat_ranges(
                slugs=("critical_chance", "critical_damage"),
                negative="toxin_damage",
                stat_class="rifle",
                disposition=1.0,
            )
        with self.assertRaisesRegex(ValueError, "cannot roll on a Melee"):
            calculate_riven_stat_ranges(
                slugs=("multishot", "critical_damage"),
                negative=None,
                stat_class="melee",
                disposition=1.0,
            )

    def test_disposition_bands_match_five_dot_categories(self):
        self.assertTrue(disposition_band(1.31).startswith("●●●●●"))
        self.assertTrue(disposition_band(0.5).startswith("●○○○○"))

    def test_deal_finder_requires_peer_support_and_online_seller(self):
        auctions = [
            auction(100, auction_id="cheap"),
            auction(200, auction_id="peer1"),
            auction(220, auction_id="peer2"),
            auction(240, auction_id="peer3"),
            auction(50, status="offline", auction_id="offline"),
        ]
        deals = find_riven_deals(auctions, weapon_slug="torid", minimum_discount_pct=20, online_only=True)
        self.assertEqual(deals[0].auction_id, "cheap")
        self.assertGreaterEqual(deals[0].comparable_count, 3)
        self.assertNotIn("offline", {item.auction_id for item in deals})

    def test_roll_formula_is_used_as_flip_comparison_quality_not_appraisal(self):
        listing = auction(100)
        listing["item"]["mod_rank"] = 8
        listing["item"]["attributes"][0]["value"] = 199.0
        listing["item"]["attributes"][1]["value"] = 159.0
        listing["item"]["attributes"][2]["value"] = -32.0
        quality = auction_roll_quality(listing, stat_class="rifle", disposition=1.0)
        self.assertIsNotNone(quality)
        self.assertGreater(quality, 70.0)

    def test_flip_target_leaves_room_to_undercut_peer_median(self):
        auctions = [
            auction(100, auction_id="cheap"),
            auction(200, auction_id="peer1"),
            auction(220, auction_id="peer2"),
            auction(240, auction_id="peer3"),
        ]
        deal = find_riven_deals(auctions, weapon_slug="torid", minimum_discount_pct=20)[0]
        self.assertEqual(deal.projected_resale, round(deal.peer_value * 0.9))
        self.assertEqual(deal.potential_margin, deal.projected_resale - deal.price)
        self.assertGreater(deal.roi_pct, 0)
        self.assertEqual(deal.positive_rolls[0], ("critical_chance", 100.0))
        self.assertEqual(deal.negative_rolls, (("zoom", -30.0),))
        self.assertIn("/w Seller", deal.ingame_whisper)
        self.assertEqual(deal.seller_profile_url, "https://warframe.market/profile/Seller")

    def test_exact_live_roll_values_are_formatted_for_discord(self):
        self.assertEqual(format_roll_stat("critical_chance", 187.4, positive=True), "+187.4% Critical Chance")
        self.assertEqual(format_roll_stat("zoom", -31.5, positive=False), "−31.5% Zoom")
        self.assertEqual(format_roll_stat("range", 2.7, positive=True), "+2.7m Range")

    def test_deal_finder_can_limit_results_to_curated_weapon_profile(self):
        rule = {
            "positive_expression": "CC CD",
            "alternatives": list(parse_rule_expression("CC CD")),
            "harmless_negatives": list(parse_negative_expression("ZOOM")),
        }
        auctions = [
            auction(100, auction_id="good"),
            auction(200, auction_id="peer1"),
            auction(220, auction_id="peer2"),
            auction(240, auction_id="peer3"),
            auction(50, positives=("critical_chance", "puncture_damage"), auction_id="bad"),
        ]
        deals = find_riven_deals(
            auctions,
            weapon_slug="torid",
            minimum_discount_pct=20,
            roll_rule=rule,
            curated_only=True,
        )
        self.assertEqual([deal.auction_id for deal in deals], ["good"])
        self.assertTrue(deals[0].curated_match)

    def test_service_loads_cache_and_resolves_human_names(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            root.mkdir(exist_ok=True)
            (root / "latest.json").write_text(json.dumps({"fetched_at": 1, "rows": [weekly()]}), encoding="utf-8")
            (root / "weapons.json").write_text(json.dumps({"rows": [{"slug": "torid", "i18n": {"en": {"name": "Torid"}}}]}), encoding="utf-8")
            service = RivenMarketService(root)
        self.assertEqual(service.find_weapon("TORID")["slug"], "torid")

    def test_weapon_autocomplete_combines_variants_and_live_families(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "weapons.json").write_text(json.dumps({"rows": [
                {"slug": "new_weapon", "i18n": {"en": {"name": "New Weapon"}}},
            ]}), encoding="utf-8")
            (root / "weapon_variants.json").write_text(json.dumps({"rows": [
                {"name": "Braton Prime", "family": "Braton"},
            ]}), encoding="utf-8")
            service = RivenMarketService(root)
        self.assertIn("Braton Prime", service.weapon_suggestions(""))
        self.assertIn("New Weapon", service.weapon_suggestions(""))

    def test_named_variant_routes_to_market_family_and_keeps_its_disposition(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "data"
            root.mkdir()
            source = Path(temp) / "ALL weapons.txt"
            header = (
                "Name,Trigger,AttackName,Impact,Puncture,Slash,Cold,Electricity,Heat,Toxin,Blast,Corrosive,Gas,Magnetic,"
                "Radiation,Viral,Void,BaseDamage,BaseDps,TotalDamage,CritChance,CritMultiplier,AvgShotDmg,BurstDps,SustainedDps,"
                "LifetimeDmg,StatusChance,ForcedProcs,AvgProcCount,AvgProcPerSec,Multishot,FireRate,BurstCount,BurstDelay,"
                "BurstReloadDelay,ChargeTime,Disposition,Mastery,Magazine,AmmoPickup,AmmoMax,AmmoCost,Reload,ShotType,ShotSpeed,"
                "PunchThrough,Accuracy,Introduced,IntroducedDate,Slot,Class,AmmoType,Range,InternalName,Family,FalloffStart,"
                "FalloffEnd,FalloffReduction,AvgSpread,MinSpread,MaxSpread,IsSilent"
            )
            row = ["0"] * len(header.split(","))
            row[0], row[36], row[37] = "Braton Prime", "1.1", "8"
            row[49], row[50] = "Primary", "Rifle"
            row[53], row[54] = "/Lotus/Weapons/PrimeBraton", "Braton"
            source.write_text(header + "\n" + ",".join(row), encoding="utf-8")
            (root / "weapons.json").write_text(json.dumps({"rows": [{"slug": "braton", "i18n": {"en": {"name": "Braton"}}}]}), encoding="utf-8")
            service = RivenMarketService(root, weapon_source=source)
            result = service.require_riven_weapon("Braton Prime")
        self.assertEqual(result["slug"], "braton")
        self.assertEqual(result["riven_family"], "Braton")
        self.assertEqual(result["variant_disposition"], 1.1)

    def test_discord_riven_group_registers_all_public_commands(self):
        with patch("local_env.load_local_env", return_value=0):
            import bot

        names = {command.name for command in bot.riven_group.commands}
        self.assertEqual(names, {"price", "top", "deals", "flips", "chatlog", "chatstats", "refresh", "guide"})

    def test_placeholder_auction_prices_cannot_create_a_fake_deal(self):
        completed = parse_weekly(
            [weekly("Praedos", median=50, average=70, popularity=5)],
            "Praedos",
            True,
        )
        auctions = [
            auction(1_000, auction_id="candidate"),
            auction(888_888, auction_id="placeholder-one"),
            auction(888_888, auction_id="placeholder-two"),
            auction(888_888, auction_id="placeholder-three"),
        ]
        deals = find_riven_deals(
            auctions,
            weapon_slug="praedos",
            weapon_name="Praedos",
            weekly=completed,
            minimum_discount_pct=10,
            online_only=False,
        )
        self.assertEqual(deals, [])


class TestWholeMarketFlipScan(unittest.IsolatedAsyncioTestCase):
    async def test_full_scope_checks_every_catalog_family_and_returns_all_deals_without_top_cap(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            families = [
                {
                    "slug": f"weapon_{index}",
                    "group": "primary",
                    "rivenType": "rifle",
                    "disposition": 1.0,
                    "i18n": {"en": {"name": f"Weapon {index}"}},
                }
                for index in range(12)
            ]
            (root / "latest.json").write_text(
                json.dumps({"fetched_at": 1, "rows": [weekly("Weapon 0")]}), encoding="utf-8"
            )
            (root / "weapons.json").write_text(json.dumps({"rows": families}), encoding="utf-8")
            service = RivenMarketService(root)
            service.ensure_fresh = AsyncMock()

            auction_map = {
                family["slug"]: [
                    auction(100, auction_id=f"{slug}-cheap"),
                    auction(200, auction_id=f"{slug}-one"),
                    auction(220, auction_id=f"{slug}-two"),
                    auction(240, auction_id=f"{slug}-three"),
                ]
                for family in families
                for slug in [family["slug"]]
            }
            service.auctions_across_market = AsyncMock(return_value=(auction_map, 0))
            service.auctions_for = AsyncMock()

            deals, scanned = await service.scan_flips(
                weapon_limit=None,
                result_limit=None,
                minimum_discount_pct=20,
                online_only=False,
                curated_only=False,
            )
        self.assertEqual(scanned, 12)
        self.assertEqual(service.auctions_across_market.await_count, 1)
        self.assertEqual(service.auctions_for.await_count, 0)
        self.assertEqual(len(deals), 12)
        self.assertEqual({deal.weapon_name for deal in deals}, {f"Weapon {index}" for index in range(12)})

    async def test_cross_weapon_stat_search_deduplicates_and_groups_auctions(self):
        with tempfile.TemporaryDirectory() as temp:
            service = RivenMarketService(Path(temp))
            one = auction(100, auction_id="shared")
            one["item"]["type"] = "riven"
            one["item"]["weapon_url_name"] = "torid"
            service._get_json = AsyncMock(return_value={"payload": {"auctions": [one]}})
            grouped, failures = await service.auctions_across_market(AsyncMock())
        self.assertEqual(failures, 0)
        self.assertEqual([item["id"] for item in grouped["torid"]], ["shared"])
        self.assertGreater(service._get_json.await_count, 40)

    async def test_complete_flip_index_is_persisted_and_loaded_after_restart(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            deals = find_riven_deals(
                [
                    auction(100, auction_id="cheap"),
                    auction(200, auction_id="one"),
                    auction(220, auction_id="two"),
                    auction(240, auction_id="three"),
                ],
                weapon_slug="torid",
                weapon_name="Torid",
                minimum_discount_pct=10,
            )
            service = RivenMarketService(root)
            service.scan_flips = AsyncMock(return_value=(deals, 418))
            indexed, scanned, indexed_at = await service.refresh_flip_index(force=True)
            restarted = RivenMarketService(root)
        self.assertEqual(scanned, 418)
        self.assertGreater(indexed_at, 0)
        self.assertEqual([deal.auction_id for deal in indexed], ["cheap"])
        self.assertEqual([deal.auction_id for deal in restarted.flip_deals], ["cheap"])
        self.assertEqual(restarted.flip_index_scanned, 418)

    async def test_background_index_starts_immediately_and_stops_cleanly(self):
        with tempfile.TemporaryDirectory() as temp:
            service = RivenMarketService(Path(temp))
            service.refresh_flip_index = AsyncMock(return_value=([], 0, 0.0))
            service.start_background_index(interval_seconds=60)
            await asyncio.sleep(0)
            await service.close()
        self.assertGreaterEqual(service.refresh_flip_index.await_count, 1)


if __name__ == "__main__":
    unittest.main()
