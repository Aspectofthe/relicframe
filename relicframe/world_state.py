"""Live Warframe world-state data used by the Discord status channels.

Market prices still come from warframe.market.  This module deliberately
uses the separate Warframe world-state services for cycles, missions,
fissures, vendors, and alerts, so it consumes none of the market API's
five-requests-per-second allowance.

WarframeStat.us supplies the translated general world state.  browse.wf's
documented Oracle supplies the richer bounty rotation plus the public node,
challenge, language, and Steel Path schedule data needed to reproduce its
live tracker.  The latter is cached in memory and refreshed only daily.
"""
from __future__ import annotations

import asyncio
import json
import os
import re
import time
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from zoneinfo import ZoneInfo

import aiohttp
from workload import heavy_operation

WORLD_STATE_URL = "https://api.warframestat.us/pc"
OFFICIAL_WORLD_STATE_URL = "https://api.warframe.com/cdn/worldState.php"
BOUNTY_URL = "https://oracle.browse.wf/bounty-cycle"
SOL_NODES_URL = "https://api.warframestat.us/solNodes"
BROWSE_REGIONS_URL = "https://browse.wf/warframe-public-export-plus/ExportRegions.json"
BROWSE_CHALLENGES_URL = "https://browse.wf/warframe-public-export-plus/ExportChallenges.json"
BROWSE_DICTIONARY_URL = "https://browse.wf/warframe-public-export-plus/dict.en.json"
SP_INCURSIONS_URL = "https://browse.wf/sp-incursions.txt"
USER_AGENT = "RelicFrame/1.0 (Discord world-state feed)"
WORLD_STATE_STALE_SECONDS = 10 * 60
STATIC_REFRESH_SECONDS = 24 * 60 * 60
STATIC_RETRY_SECONDS = 5 * 60

FISSURE_TIERS = {
    "VoidT1": ("Lith", 1), "VoidT2": ("Meso", 2), "VoidT3": ("Neo", 3),
    "VoidT4": ("Axi", 4), "VoidT5": ("Requiem", 5), "VoidT6": ("Omnia", 6),
}
MISSION_TYPES = {
    "MT_EXTERMINATION": "Extermination", "MT_SURVIVAL": "Survival",
    "MT_RESCUE": "Rescue", "MT_MOBILE_DEFENSE": "Mobile Defense",
    "MT_DEFENSE": "Defense", "MT_INTEL": "Spy", "MT_TERRITORY": "Interception",
    "MT_SABOTAGE": "Sabotage", "MT_VOID_CASCADE": "Void Cascade",
    "MT_ALCHEMY": "Alchemy", "MT_ASSASSINATION": "Assassination",
    "MT_DISRUPTION": "Disruption", "MT_CAPTURE": "Capture",
    "MT_EXCAVATE": "Excavation", "MT_HIVE": "Hive", "MT_PURSUIT": "Pursuit",
    "MT_RACE": "Rush", "MT_SECTOR": "Skirmish", "MT_VOLATILE": "Volatile",
}

FACTIONS = {
    "FC_GRINEER": "Grineer", "FC_CORPUS": "Corpus", "FC_INFESTATION": "Infestation",
    "FC_INFESTED": "Infestation", "FC_OROKIN": "Orokin", "FC_SENTIENT": "Sentient",
    "FC_MITW": "The Murmur", "FC_NARMER": "Narmer", "FC_TENNO": "Tenno",
}
SORTIE_BOSSES = {
    "SORTIE_BOSS_ALAD": "Alad V", "SORTIE_BOSS_AMBULAS": "Ambulas",
    "SORTIE_BOSS_CORRUPTED_VOR": "Corrupted Vor", "SORTIE_BOSS_HEK": "Vay Hek",
    "SORTIE_BOSS_HYENA": "Hyena Pack", "SORTIE_BOSS_INFALAD": "Mutalist Alad V",
    "SORTIE_BOSS_JACKAL": "Jackal", "SORTIE_BOSS_KELA": "Kela De Thaym",
    "SORTIE_BOSS_KRIL": "Lech Kril", "SORTIE_BOSS_LEPHANTIS": "Lephantis",
    "SORTIE_BOSS_NEF": "The Sergeant", "SORTIE_BOSS_PHORID": "Phorid",
    "SORTIE_BOSS_RAPTOR": "Raptor", "SORTIE_BOSS_RUK": "Sargas Ruk",
    "SORTIE_BOSS_TYL": "Tyl Regor", "SORTIE_BOSS_VOR": "Captain Vor",
    "SORTIE_BOSS_AMAR": "Amar", "SORTIE_BOSS_BOREAL": "Boreal", "SORTIE_BOSS_NIRA": "Nira",
}
SORTIE_MODIFIERS = {
    "SORTIE_MODIFIER_ARMOR": "Augmented Enemy Armor",
    "SORTIE_MODIFIER_BOW_ONLY": "Bow Only",
    "SORTIE_MODIFIER_CORROSIVE": "Enemy Elemental Enhancement (Corrosive)",
    "SORTIE_MODIFIER_DENSE_FOG": "Dense Fog",
    "SORTIE_MODIFIER_ELECTRICITY": "Enemy Elemental Enhancement (Electricity)",
    "SORTIE_MODIFIER_ENERGY_REDUCTION": "Energy Reduction",
    "SORTIE_MODIFIER_EXIMUS": "Eximus Stronghold",
    "SORTIE_MODIFIER_EXPLOSION": "Enemy Elemental Enhancement (Blast)",
    "SORTIE_MODIFIER_FIRE": "Enemy Elemental Enhancement (Heat)",
    "SORTIE_MODIFIER_FREEZE": "Enemy Elemental Enhancement (Cold)",
    "SORTIE_MODIFIER_HAZARD": "Environmental Hazard",
    "SORTIE_MODIFIER_IMPACT": "Enemy Physical Enhancement (Impact)",
    "SORTIE_MODIFIER_LOW_ENERGY": "Energy Reduction",
    "SORTIE_MODIFIER_MAGNETIC": "Enemy Elemental Enhancement (Magnetic)",
    "SORTIE_MODIFIER_MELEE_ONLY": "Melee Only",
    "SORTIE_MODIFIER_PISTOL_ONLY": "Secondary Only",
    "SORTIE_MODIFIER_POISON": "Enemy Elemental Enhancement (Toxin)",
    "SORTIE_MODIFIER_PUNCTURE": "Enemy Physical Enhancement (Puncture)",
    "SORTIE_MODIFIER_RADIATION": "Enemy Elemental Enhancement (Radiation)",
    "SORTIE_MODIFIER_SHIELDS": "Enhanced Enemy Shields",
    "SORTIE_MODIFIER_SHOTGUN_ONLY": "Shotgun Only",
    "SORTIE_MODIFIER_SLASH": "Enemy Physical Enhancement (Slash)",
    "SORTIE_MODIFIER_SNIPER_ONLY": "Sniper Only",
    "SORTIE_MODIFIER_TOXIN": "Enemy Elemental Enhancement (Toxin)",
    "SORTIE_MODIFIER_VIRAL": "Enemy Elemental Enhancement (Viral)",
}
ITEM_NAMES = {
    "BioComponent": "Mutagen Mass", "ChemComponent": "Detonite Injector",
    "EnergyComponent": "Fieldron", "MutagenSample": "Mutagen Sample",
    "FieldronSample": "Fieldron Sample", "DetoniteAmpule": "Detonite Ampule",
    "WaterFightBucks": "Nakak Pearls",
}
FOCUS_LENS_NAMES = {
    "Attack": "Madurai", "Ward": "Vazarin", "Tactical": "Naramon",
    "Defense": "Unairu", "Power": "Zenurik",
}

MODULE_DIR = os.path.dirname(os.path.abspath(__file__))
DEFAULT_ARBITRATION_PATH = os.path.normpath(os.path.join(MODULE_DIR, "..", "Untitled.txt"))


def parse_iso(value: str | None) -> datetime | None:
    if not value or not isinstance(value, str):
        return None
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    except (ValueError, OverflowError):
        return None


def unix_time(value: str | None) -> int | None:
    parsed = parse_iso(value)
    return int(parsed.timestamp()) if parsed is not None else None


def is_active(item: dict, now: float | None = None) -> bool:
    now = time.time() if now is None else now
    activation = unix_time(item.get("activation"))
    expiry = unix_time(item.get("expiry"))
    return (activation is None or activation <= now) and (expiry is None or now < expiry)


def active_items(items, now: float | None = None) -> list[dict]:
    return [item for item in (items or []) if isinstance(item, dict) and is_active(item, now)]


def world_state_age_seconds(world: dict, now: float | None = None) -> float | None:
    parsed = parse_iso(world.get("timestamp"))
    if parsed is None:
        return None
    now = time.time() if now is None else now
    return max(0.0, now - parsed.timestamp())


def _mongo_date(value) -> str | None:
    try:
        milliseconds = int(value["$date"]["$numberLong"])
    except (KeyError, TypeError, ValueError):
        return None
    return datetime.fromtimestamp(milliseconds / 1000, timezone.utc).isoformat().replace("+00:00", "Z")


def _mongo_id(value) -> str:
    if isinstance(value, dict):
        return str(value.get("$oid") or "")
    return str(value or "")


def _mongo_seconds(value) -> float | None:
    iso = _mongo_date(value)
    parsed = parse_iso(iso)
    return parsed.timestamp() if parsed is not None else None


def _humanize(value: str | None) -> str:
    """Turn raw DE identifiers and content paths into safe readable fallbacks."""
    token = str(value or "").rsplit("/", 1)[-1]
    token = re.sub(r"^(?:MT|DT|CT|CD|FC)_", "", token)
    token = re.sub(r"^(?:SORTIE_BOSS_|SORTIE_MODIFIER_)", "", token)
    token = token.replace("_", " ")
    token = re.sub(r"(?<=[a-z0-9])(?=[A-Z])", " ", token)
    token = re.sub(r"(?<=[A-Z])(?=[A-Z][a-z])", " ", token)
    return " ".join(token.split()).title() or "Unknown"


def _item_name(value: str | None) -> str:
    token = str(value or "").rsplit("/", 1)[-1]
    if token in ITEM_NAMES:
        return ITEM_NAMES[token]
    lens = re.fullmatch(r"(Attack|Ward|Tactical|Defense|Power)Lens(Greater)?", token)
    if lens:
        school = FOCUS_LENS_NAMES[lens.group(1)]
        return f"{'Greater ' if lens.group(2) else ''}{school} Lens"
    return _humanize(token)


def _faction(value: str | None) -> str:
    return FACTIONS.get(str(value or ""), _humanize(value))


def _node_name(sol_nodes: dict, key: str | None) -> str:
    key = str(key or "")
    return (sol_nodes.get(key) or {}).get("value") or _humanize(key)


def _official_active(item: dict, now: float | None = None) -> bool:
    now = time.time() if now is None else now
    activation = _mongo_seconds(item.get("Activation"))
    expiry = _mongo_seconds(item.get("Expiry"))
    return (activation is None or activation <= now) and (expiry is None or now < expiry)


def _official_reward(raw: dict | None) -> dict:
    raw = raw or {}
    return {
        "credits": int(raw.get("credits", raw.get("Credits", 0)) or 0),
        "items": [_item_name(item) for item in raw.get("items", raw.get("Items", [])) or []],
        "countedItems": [
            {
                "count": int(item.get("ItemCount", item.get("count", 1)) or 1),
                "type": _item_name(item.get("ItemType") or item.get("type")),
            }
            for item in raw.get("countedItems", raw.get("CountedItems", [])) or []
            if isinstance(item, dict)
        ],
    }


def parse_official_daily_deals(raw: dict) -> list[dict]:
    return [
        {
            "id": f"{deal.get('StoreItem', '')}:{_mongo_date(deal.get('Activation')) or ''}",
            "activation": _mongo_date(deal.get("Activation")),
            "expiry": _mongo_date(deal.get("Expiry")),
            "item": _item_name(deal.get("StoreItem")),
            "originalPrice": deal.get("OriginalPrice"), "salePrice": deal.get("SalePrice"),
            "total": deal.get("AmountTotal", 0), "sold": deal.get("AmountSold", 0),
            "discount": deal.get("Discount", 0),
        }
        for deal in raw.get("DailyDeals") or [] if isinstance(deal, dict)
    ]


def parse_official_news(raw: dict, dictionary: dict) -> list[dict]:
    news = []
    for item in raw.get("Events") or []:
        if not isinstance(item, dict) or item.get("MobileOnly"):
            continue
        message_entry = next(
            (entry for entry in item.get("Messages") or [] if entry.get("LanguageCode") == "en"),
            None,
        ) or {}
        message_key = message_entry.get("Message")
        if not message_key:
            continue
        message = dictionary.get(message_key) if message_key else None
        news.append({
            "id": _mongo_id(item.get("_id")),
            "message": message or _humanize(message_key) or "Warframe news",
            "link": item.get("Prop"), "date": _mongo_date(item.get("Date")),
            "priority": bool(item.get("Priority")),
        })
    return news


def parse_official_alerts(raw: dict, sol_nodes: dict) -> list[dict]:
    alerts = []
    for item in raw.get("Alerts") or []:
        if not isinstance(item, dict):
            continue
        mission = item.get("MissionInfo") or {}
        mission_type = mission.get("missionType") or mission.get("MissionType")
        faction = mission.get("faction") or mission.get("Faction")
        node = mission.get("location") or mission.get("Location") or mission.get("node") or mission.get("Node")
        reward = mission.get("missionReward") or mission.get("MissionReward") or mission.get("reward")
        alerts.append({
            "id": _mongo_id(item.get("_id")), "activation": _mongo_date(item.get("Activation")),
            "expiry": _mongo_date(item.get("Expiry")),
            "mission": {
                "type": MISSION_TYPES.get(str(mission_type or ""), _humanize(mission_type)),
                "faction": _faction(faction), "node": _node_name(sol_nodes, node),
                "minEnemyLevel": mission.get("minEnemyLevel", mission.get("MinEnemyLevel", "?")),
                "maxEnemyLevel": mission.get("maxEnemyLevel", mission.get("MaxEnemyLevel", "?")),
                "reward": _official_reward(reward),
            },
        })
    return alerts


def parse_official_events(raw: dict, dictionary: dict) -> list[dict]:
    labels = {"WaterFight": "Tactical Alert: Dog Days", "HeatFissure": "Thermia Fractures"}
    events = []
    for item in raw.get("Goals") or []:
        if not isinstance(item, dict):
            continue
        desc_key = item.get("Desc")
        tag = str(item.get("Tag") or "")
        description = dictionary.get(desc_key) if desc_key else None
        events.append({
            "id": _mongo_id(item.get("_id")), "activation": _mongo_date(item.get("Activation")),
            "expiry": _mongo_date(item.get("Expiry")), "tag": tag,
            "description": description or labels.get(tag) or _humanize(tag),
            "maximumScore": item.get("Goal", 0),
            "currentScore": item.get("Count", item.get("Success", 0)),
        })
    return events


def _current_official(items, now: float | None = None) -> dict | None:
    candidates = [item for item in items or [] if isinstance(item, dict) and _official_active(item, now)]
    return candidates[0] if candidates else None


def parse_official_sortie(raw: dict, sol_nodes: dict, *, archon: bool = False, now: float | None = None) -> dict:
    source = raw.get("LiteSorties" if archon else "Sorties") or []
    sortie = _current_official(source, now)
    if not sortie:
        return {}
    boss_key = str(sortie.get("Boss") or "")
    result = {
        "id": _mongo_id(sortie.get("_id")), "activation": _mongo_date(sortie.get("Activation")),
        "expiry": _mongo_date(sortie.get("Expiry")),
        "boss": SORTIE_BOSSES.get(boss_key, _humanize(boss_key)),
    }
    if archon:
        result["missions"] = [
            {
                "type": MISSION_TYPES.get(str(item.get("missionType") or ""), _humanize(item.get("missionType"))),
                "missionType": MISSION_TYPES.get(str(item.get("missionType") or ""), _humanize(item.get("missionType"))),
                "node": _node_name(sol_nodes, item.get("node")),
            }
            for item in sortie.get("Missions") or [] if isinstance(item, dict)
        ]
    else:
        result["variants"] = [
            {
                "missionType": MISSION_TYPES.get(str(item.get("missionType") or ""), _humanize(item.get("missionType"))),
                "modifier": SORTIE_MODIFIERS.get(str(item.get("modifierType") or ""), _humanize(item.get("modifierType"))),
                "node": _node_name(sol_nodes, item.get("node")),
            }
            for item in sortie.get("Variants") or [] if isinstance(item, dict)
        ]
    return result


def parse_official_invasions(raw: dict, sol_nodes: dict) -> list[dict]:
    parsed = []
    for item in raw.get("Invasions") or []:
        if not isinstance(item, dict):
            continue
        goal = float(item.get("Goal", 0) or 0)
        count = float(item.get("Count", 0) or 0)
        attacker_info = item.get("DefenderMissionInfo") or {}
        defender_info = item.get("AttackerMissionInfo") or {}
        attacker_faction = _faction(attacker_info.get("faction") or item.get("Faction"))
        defender_faction = _faction(defender_info.get("faction") or item.get("DefenderFaction"))
        vs_infestation = "infest" in str(attacker_info.get("faction") or "").casefold()
        completion = (1 + count / goal) * (100 if vs_infestation else 50) if goal else 0
        parsed.append({
            "id": _mongo_id(item.get("_id")), "activation": _mongo_date(item.get("Activation")),
            "node": _node_name(sol_nodes, item.get("Node")),
            "desc": f"{attacker_faction} vs {defender_faction}",
            "completion": max(0.0, min(100.0, completion)), "completed": bool(item.get("Completed")),
            "attacker": {"faction": attacker_faction, "reward": _official_reward(item.get("AttackerReward"))},
            "defender": {"faction": defender_faction, "reward": _official_reward(item.get("DefenderReward"))},
        })
    return parsed


def parse_official_void_trader(raw: dict, sol_nodes: dict, now: float | None = None) -> dict:
    now = time.time() if now is None else now
    candidates = [item for item in raw.get("VoidTraders") or [] if isinstance(item, dict)]
    candidates = [item for item in candidates if (_mongo_seconds(item.get("Expiry")) or 0) > now]
    if not candidates:
        return {}
    item = min(candidates, key=lambda entry: _mongo_seconds(entry.get("Activation")) or float("inf"))
    return {
        "id": _mongo_id(item.get("_id")), "activation": _mongo_date(item.get("Activation")),
        "expiry": _mongo_date(item.get("Expiry")),
        "character": str(item.get("Character") or "Baro Ki'Teer").replace("Baro'Ki Teel", "Baro Ki'Teer"),
        "location": _node_name(sol_nodes, item.get("Node")),
        "inventory": [
            {
                "item": _item_name(entry.get("ItemType")), "ducats": entry.get("PrimePrice", entry.get("Ducats", "?")),
                "credits": entry.get("RegularPrice", entry.get("Credits", "?")),
            }
            for entry in item.get("Manifest") or [] if isinstance(entry, dict)
        ],
    }


def parse_official_duviri(raw: dict) -> dict:
    schedule = _current_official(raw.get("EndlessXpSchedule") or [])
    if not schedule:
        return {}
    categories = {"EXC_NORMAL": "normal", "EXC_HARD": "hard"}
    return {
        "activation": _mongo_date(schedule.get("Activation")), "expiry": _mongo_date(schedule.get("Expiry")),
        "choices": [
            {"category": categories.get(choice.get("Category"), str(choice.get("Category") or "").casefold()),
             "choices": [_humanize(name) for name in choice.get("Choices") or []]}
            for choice in schedule.get("CategoryChoices") or [] if isinstance(choice, dict)
        ],
    }


def parse_official_archimedeas(raw: dict) -> list[dict]:
    entries = []
    for conquest in raw.get("Conquests") or []:
        if not isinstance(conquest, dict) or not _official_active(conquest):
            continue
        missions = []
        for mission in conquest.get("Missions") or []:
            difficulties = mission.get("difficulties") or []
            difficulty = difficulties[-1] if difficulties else {}
            missions.append({
                "missionType": MISSION_TYPES.get(str(mission.get("missionType") or ""), _humanize(mission.get("missionType"))),
                "deviation": {"name": _humanize(difficulty.get("deviation"))},
                "risks": [{"name": _humanize(risk)} for risk in difficulty.get("risks") or []],
            })
        entries.append({
            "id": f"{conquest.get('Type', '')}:{_mongo_date(conquest.get('Activation')) or ''}",
            "typeKey": str(conquest.get("Type") or ""),
            "activation": _mongo_date(conquest.get("Activation")), "expiry": _mongo_date(conquest.get("Expiry")),
            "missions": missions,
            "personalModifiers": [{"name": _humanize(value)} for value in conquest.get("Variables") or []],
        })
    return entries


def parse_official_cycles(raw: dict, now: float | None = None) -> dict[str, dict]:
    now = time.time() if now is None else now
    cetus = next((item for item in raw.get("SyndicateMissions") or [] if item.get("Tag") == "CetusSyndicate"), None)
    result: dict[str, dict] = {}
    bounty_end = _mongo_seconds((cetus or {}).get("Expiry"))
    if bounty_end and now < bounty_end:
        seconds_to_night_end = bounty_end - now
        is_day = seconds_to_night_end > 3000
        expiry = bounty_end - 3000 if is_day else bounty_end
        expiry_iso = datetime.fromtimestamp(expiry, timezone.utc).isoformat().replace("+00:00", "Z")
        result["cetusCycle"] = {"state": "day" if is_day else "night", "isDay": is_day, "expiry": expiry_iso}
        result["cambionCycle"] = {"state": "fass" if is_day else "vome", "expiry": expiry_iso}

    anchor = datetime.fromisoformat("2026-02-04T19:46:48+00:00").timestamp()
    loop_seconds, warm_seconds = 1600, 400
    elapsed = (now - anchor) % loop_seconds
    remaining_full = loop_seconds - elapsed
    is_warm = remaining_full > loop_seconds - warm_seconds
    remaining = remaining_full - (loop_seconds - warm_seconds) if is_warm else remaining_full
    vallis_expiry = datetime.fromtimestamp(now + remaining, timezone.utc).isoformat().replace("+00:00", "Z")
    result["vallisCycle"] = {"state": "warm" if is_warm else "cold", "isWarm": is_warm, "expiry": vallis_expiry}
    return result


def parse_official_kinepage(raw: dict) -> dict:
    try:
        payload = json.loads(raw.get("Tmp") or "{}")
        message = payload.get("pgr") or {}
        stamp = int(message.get("ts"))
    except (TypeError, ValueError, json.JSONDecodeError):
        return {}
    return {"message": message.get("en") or "No new messages.", "timestamp": stamp}


def parse_official_fissures(raw: dict, sol_nodes: dict) -> list[dict]:
    """Convert DE's fresh raw world state into the shape used by the Discord feed."""
    fissures: list[dict] = []
    for item in raw.get("ActiveMissions") or []:
        modifier = item.get("Modifier")
        tier, tier_num = FISSURE_TIERS.get(modifier, (modifier or "?", 99))
        node_key = str(item.get("Node") or "")
        node = (sol_nodes.get(node_key) or {}).get("value") or node_key or "Unknown node"
        mission_key = str(item.get("MissionType") or "")
        mission = MISSION_TYPES.get(mission_key) or (sol_nodes.get(node_key) or {}).get("type") or mission_key or "Unknown"
        fissures.append({
            "id": _mongo_id(item.get("_id")), "activation": _mongo_date(item.get("Activation")),
            "expiry": _mongo_date(item.get("Expiry")), "tier": tier, "tierNum": tier_num,
            "missionType": mission, "node": node, "isHard": bool(item.get("Hard")), "isStorm": False,
        })
    for item in raw.get("VoidStorms") or []:
        modifier = item.get("ActiveMissionTier")
        tier, tier_num = FISSURE_TIERS.get(modifier, (modifier or "?", 99))
        node_key = str(item.get("Node") or "")
        resolved = sol_nodes.get(node_key) or {}
        fissures.append({
            "id": _mongo_id(item.get("_id")), "activation": _mongo_date(item.get("Activation")),
            "expiry": _mongo_date(item.get("Expiry")), "tier": tier, "tierNum": tier_num,
            "missionType": resolved.get("type") or "Skirmish",
            "node": resolved.get("value") or node_key or "Unknown node",
            "isHard": False, "isStorm": True,
        })
    return fissures


def official_world_state_is_fresh(raw: dict, now: float | None = None) -> bool:
    try:
        stamp = float(raw.get("Time"))
    except (TypeError, ValueError):
        return False
    now = time.time() if now is None else now
    return 0 <= now - stamp <= WORLD_STATE_STALE_SECONDS


def translate(dictionary: dict, key: str | None, fallback: str | None = None) -> str:
    if not key:
        return fallback or "Unknown"
    value = dictionary.get(key)
    if isinstance(value, str) and value.strip():
        return value.strip()
    return fallback or key.rsplit("/", 1)[-1]


@dataclass(frozen=True)
class ArbitrationEntry:
    activation: int
    expiry: int
    text: str
    mission_type: str
    enemy: str
    location: str
    tier: str
    resource_bonus: str | None = None


_DATE_RE = re.compile(r"^(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun),\s+(.+)$")
_ENTRY_RE = re.compile(r"^(\d{2})(\d{2})\s+•\s+(.+)$")
_ARBITRATION_RE = re.compile(
    r"^(?P<mission>.+?)\s+-\s+(?P<enemy>.+?)\s+@\s+(?P<location>.+?)"
    r"(?:\s+\((?P<details>.+)\))?$"
)


def load_arbitration_schedule(
    path: str = DEFAULT_ARBITRATION_PATH,
    *,
    timezone_name: str = "America/New_York",
    start_year: int | None = None,
) -> list[ArbitrationEntry]:
    """Parse the human-readable hourly schedule supplied in Untitled.txt.

    The first date heading intentionally omits its year.  It is the current
    year unless ``start_year`` is supplied; later explicit years override the
    inference and a December-to-January transition advances it naturally.
    """
    if not os.path.isfile(path):
        return []
    tz = ZoneInfo(timezone_name)
    inferred_year = start_year or datetime.now(tz).year
    previous_month: int | None = None
    current_date = None
    entries: list[ArbitrationEntry] = []

    with open(path, "r", encoding="utf-8-sig") as source:
        for raw_line in source:
            line = raw_line.strip()
            if not line:
                continue
            date_match = _DATE_RE.match(line)
            if date_match:
                date_text = date_match.group(1)
                parsed = None
                has_year = bool(re.search(r"\b\d{4}$", date_text))
                candidates = ((date_text, "%B %d, %Y"),) if has_year else ((f"{date_text}, 2000", "%B %d, %Y"),)
                for candidate, pattern in candidates:
                    try:
                        parsed = datetime.strptime(candidate, pattern)
                        break
                    except ValueError:
                        continue
                if parsed is None:
                    current_date = None
                    continue
                if not has_year:
                    if previous_month == 12 and parsed.month == 1:
                        inferred_year += 1
                    parsed = parsed.replace(year=inferred_year)
                else:
                    inferred_year = parsed.year
                previous_month = parsed.month
                current_date = parsed.date()
                continue

            entry_match = _ENTRY_RE.match(line)
            if current_date is None or not entry_match:
                continue
            hour, minute = int(entry_match.group(1)), int(entry_match.group(2))
            text = entry_match.group(3).strip()
            details = _ARBITRATION_RE.match(text)
            if details:
                extra = details.group("details") or ""
                tier_match = re.search(r"\b([A-Z])\s+tier\b", extra, re.IGNORECASE)
                bonus_match = re.search(r"(\d+%\s+resource bonus)", extra, re.IGNORECASE)
                mission_type = details.group("mission")
                enemy = details.group("enemy")
                location = details.group("location")
                tier = tier_match.group(1).upper() if tier_match else "F"
                resource_bonus = bonus_match.group(1) if bonus_match else None
            else:
                mission_type, enemy, location, tier, resource_bonus = text, "Unknown", "Unknown", "F", None
            starts = datetime.combine(current_date, datetime.min.time(), tzinfo=tz).replace(hour=hour, minute=minute)
            ends = starts + timedelta(hours=1)
            entries.append(ArbitrationEntry(
                activation=int(starts.timestamp()),
                expiry=int(ends.timestamp()),
                text=text,
                mission_type=mission_type,
                enemy=enemy,
                location=location,
                tier=tier,
                resource_bonus=resource_bonus,
            ))
    return entries


def current_arbitration(entries: list[ArbitrationEntry], now: float | None = None) -> ArbitrationEntry | None:
    now = time.time() if now is None else now
    # The file is ordered, but reverse traversal makes a current lookup cheap.
    for entry in reversed(entries):
        if entry.activation <= now < entry.expiry:
            return entry
        if entry.expiry <= now:
            break
    return None


def parse_sp_incursions(text: str) -> dict[int, list[str]]:
    schedule: dict[int, list[str]] = {}
    for raw_line in text.splitlines():
        line = raw_line.strip()
        if not line or ";" not in line:
            continue
        epoch_text, nodes_text = line.split(";", 1)
        try:
            epoch = int(epoch_text)
        except ValueError:
            continue
        nodes = [node.strip() for node in nodes_text.split(",") if node.strip()]
        if nodes:
            schedule[epoch] = nodes
    return schedule


def current_sp_incursions(schedule: dict[int, list[str]], now: float | None = None) -> tuple[int, list[str]] | None:
    now = time.time() if now is None else now
    day = int(now // 86400) * 86400
    nodes = schedule.get(day)
    return (day, nodes) if nodes else None


class WorldStateClient:
    """Small, respectful polling client: two normal live calls per refresh.

    Static translation/schedule files are fetched once per day, not on every
    minute tick.  A failed optional browse.wf request retains its last good
    result while the general world state remains available. When the parsed
    WarframeStat snapshot is stale, one additional official DE request repairs
    the time-sensitive boards that are available in the raw world state.
    """

    def __init__(self, timeout_seconds: float = 25.0):
        self.timeout_seconds = timeout_seconds
        self.session: aiohttp.ClientSession | None = None
        self.static_loaded_at = 0.0
        self.static_attempted_at = 0.0
        self.static_complete = False
        self.regions: dict = {}
        self.challenges: dict = {}
        self.dictionary: dict = {}
        self.sol_nodes: dict = {}
        self.sp_incursions: dict[int, list[str]] = {}
        self.last_bounty: dict | None = None

    async def _ensure_session(self) -> aiohttp.ClientSession:
        if self.session is None or self.session.closed:
            timeout = aiohttp.ClientTimeout(total=self.timeout_seconds)
            self.session = aiohttp.ClientSession(timeout=timeout, headers={"User-Agent": USER_AGENT})
        return self.session

    async def _json(self, url: str):
        session = await self._ensure_session()
        async with session.get(url) as response:
            response.raise_for_status()
            return await response.json(content_type=None)

    async def _text(self, url: str) -> str:
        session = await self._ensure_session()
        async with session.get(url) as response:
            response.raise_for_status()
            return await response.text()

    async def _load_static(self, force: bool = False) -> None:
        now = time.time()
        retry_after = STATIC_REFRESH_SECONDS if self.static_complete else STATIC_RETRY_SECONDS
        if not force and self.static_attempted_at and now - self.static_attempted_at < retry_after:
            return
        async with heavy_operation("world static"):
            await self._load_static_data(now)

    async def _load_static_data(self, now):
        self.static_attempted_at = now
        results = []
        # Decode one export at a time, not five simultaneous response buffers.
        for url, text in ((BROWSE_REGIONS_URL, False), (BROWSE_CHALLENGES_URL, False),
                          (BROWSE_DICTIONARY_URL, False), (SOL_NODES_URL, False), (SP_INCURSIONS_URL, True)):
            try:
                results.append(await (self._text(url) if text else self._json(url)))
            except Exception as exc:
                results.append(exc)
        if isinstance(results[0], dict):
            self.regions = results[0]
        if isinstance(results[1], dict):
            self.challenges = results[1]
        if isinstance(results[2], dict):
            self.dictionary = results[2]
        if isinstance(results[3], dict):
            self.sol_nodes = results[3]
        if isinstance(results[4], str):
            self.sp_incursions = parse_sp_incursions(results[4])
        successful = (
            isinstance(results[0], dict), isinstance(results[1], dict),
            isinstance(results[2], dict), isinstance(results[3], dict), isinstance(results[4], str),
        )
        self.static_complete = all(successful)
        if any(successful):
            self.static_loaded_at = time.time()

    async def fetch(self) -> dict:
        await self._load_static()
        world_result, bounty_result = await asyncio.gather(
            self._json(WORLD_STATE_URL), self._json(BOUNTY_URL), return_exceptions=True,
        )
        if isinstance(world_result, Exception):
            raise world_result
        if not isinstance(world_result, dict):
            raise RuntimeError("WarframeStat.us returned an invalid world-state payload.")
        if isinstance(bounty_result, dict):
            self.last_bounty = bounty_result
        age = world_state_age_seconds(world_result)
        parsed_stale = age is None or age > WORLD_STATE_STALE_SECONDS
        mission_stale = parsed_stale
        mission_source = "warframestat"
        repaired_sections: list[str] = []
        if parsed_stale:
            try:
                official = await self._json(OFFICIAL_WORLD_STATE_URL)
            except Exception:  # noqa: BLE001 - retain general data when the fallback is unavailable
                official = None
            if isinstance(official, dict) and official_world_state_is_fresh(official):
                world_result["fissures"] = parse_official_fissures(official, self.sol_nodes)
                world_result["news"] = parse_official_news(official, self.dictionary)
                world_result["alerts"] = parse_official_alerts(official, self.sol_nodes)
                world_result["events"] = parse_official_events(official, self.dictionary)
                world_result["dailyDeals"] = parse_official_daily_deals(official)
                world_result["sortie"] = parse_official_sortie(official, self.sol_nodes)
                world_result["archonHunt"] = parse_official_sortie(official, self.sol_nodes, archon=True)
                world_result["invasions"] = parse_official_invasions(official, self.sol_nodes)
                world_result["voidTrader"] = parse_official_void_trader(official, self.sol_nodes)
                duviri = parse_official_duviri(official)
                if duviri:
                    world_result["duviriCycle"] = duviri
                archimedeas = parse_official_archimedeas(official)
                if archimedeas:
                    world_result["archimedeas"] = archimedeas
                world_result.update(parse_official_cycles(official))
                kinepage = parse_official_kinepage(official)
                if kinepage:
                    world_result["kinepage"] = kinepage
                mission_source = "official"
                mission_stale = False
                repaired_sections = [
                    "fissures", "news", "alerts", "events", "dailyDeals", "sortie", "archonHunt", "invasions",
                    "voidTrader", "duviriCycle", "archimedeas", "cetusCycle",
                    "cambionCycle", "vallisCycle", "kinepage",
                ]
        return {
            "world": world_result,
            "bounty": self.last_bounty,
            "regions": self.regions,
            "challenges": self.challenges,
            "dictionary": self.dictionary,
            "sol_nodes": self.sol_nodes,
            "sp_incursions": self.sp_incursions,
            "fetched_at": time.time(),
            "mission_source": mission_source,
            "mission_data_stale": mission_stale,
            "world_state_stale": parsed_stale,
            "official_fallback_sections": repaired_sections,
            "warframestat_age_seconds": age,
        }

    async def close(self) -> None:
        if self.session is not None and not self.session.closed:
            await self.session.close()
