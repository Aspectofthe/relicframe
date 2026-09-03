"""Persistent Discord channels, opt-in roles, and notifications for world state."""
from __future__ import annotations

import asyncio
import contextlib
import hashlib
import json
import os
import re
import time
from datetime import datetime, timezone

import discord
from bot_guide import guide_embeds

from world_state import (
    DEFAULT_ARBITRATION_PATH,
    WorldStateClient,
    active_items,
    is_active,
    current_arbitration,
    current_sp_incursions,
    load_arbitration_schedule,
    translate,
    unix_time,
)

WORLD_CATEGORY_NAME = "WARFRAME LIVE"
POLL_SECONDS = 60
MESSAGE_VERIFY_SECONDS = 10 * 60
STATE_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "discord_world_state.json")

CHANNELS = {
    "bot-guide": "How to use RelicFrame and every feature",
    "world-pings": "Choose your notification roles",
    "world-cycles": "Environments",
    "world-news": "News and KinePage",
    "world-alerts": "Alerts and Events",
    "world-sortie": "Daily Sortie",
    "world-archon": "Weekly Archon Hunt",
    "world-steel-path": "Daily Steel Path Incursions",
    "world-weekly": "Weekly missions and Circuit choices",
    "world-archimedea": "Deep and Temporal Archimedea",
    "world-vendors": "Darvo, Baro, Steel Path Honors, Iron Wake",
    "world-bounties": "Zariman, Cavia, and Hex bounties",
    "world-fissures": "Normal Void Fissures",
    "world-steel-fissures": "Steel Path Void Fissures",
    "world-void-storms": "Railjack Void Storms",
    "world-invasions": "Active invasions",
    "world-arbitration": "Current and upcoming Arbitrations",
    "world-cascade": "Normal and Steel Path Void Cascade fissures",
}

# Keep persisted keys/IDs stable while migrating visible channel names in place.
CHANNEL_NAMES = {
    key: {"world-cycles": "world-cycles", "world-news": "warframe-news",
          "world-pings": "role-pings"}.get(key, key.removeprefix("world-"))
    for key in CHANNELS
}

BASE_ROLES = {
    "cascade": ("Normal Cascade Fissure Ping", "🌊"),
    "steel_cascade": ("Steel Path Cascade Fissure Ping", "🌊"),
    "arbitration": ("Arbitration Ping", "⚖️"),
    "alerts": ("Alerts Ping", "🚨"),
    "events": ("Events Ping", "🎉"),
    "sortie": ("Sortie Ping", "🎯"),
    "archon": ("Archon Hunt Ping", "🐺"),
    "steel_path": ("Steel Path Ping", "💀"),
    "bounties": ("Bounties Ping", "📋"),
    "fissures": ("Fissures Ping", "🌀"),
    "steel_fissures": ("Steel Fissures Ping", "☠️"),
    "void_storms": ("Void Storms Ping", "🚀"),
    "invasions": ("Invasions Ping", "⚔️"),
    "baro": ("Baro Ki'Teer Ping", "🛒"),
    "news": ("Warframe News Ping", "📰"),
    "archimedea": ("Archimedea Ping", "🧬"),
}

ARBITRATION_TIERS = ("S", "A", "B", "C", "D", "F")
FISSURE_TIERS = ("Lith", "Meso", "Neo", "Axi", "Requiem", "Omnia")
ARBITRATION_TIER_EMOJI = {
    "S": "💎", "A": "🟥", "B": "🟧", "C": "🟨", "D": "🟩", "F": "⬜",
}
FISSURE_TIER_EMOJI = {
    "Lith": "🟤", "Meso": "🟡", "Neo": "⚪", "Axi": "🟠", "Requiem": "🔴", "Omnia": "🟣",
}

# Exact custom emoji names used in the user's Discord server. Every feature
# keeps a Unicode fallback so the bot remains portable to another server.
FEATURE_EMOJI = {
    "fissure": ("VoidTear", "🌀"),
    "cascade": ("ThraxPlasm", "🌊"),
    "survival": ("Survival", "🛡️"),
    "arbitration": ("VitusEssence", "⚖️"),
    "baro": ("OrokinDucats", "🛒"),
}

ROLE_FEATURE_EMOJI = {
    "steel_cascade": "cascade",
    "cascade": "cascade",
    "arbitration": "arbitration",
    "fissures": "fissure",
    "steel_fissures": "fissure",
    "baro": "baro",
}


def _feature_emoji_object(guild, feature: str):
    """Return a matching guild Emoji object, or the portable fallback string."""
    expected_name, fallback = FEATURE_EMOJI[feature]
    for emoji in getattr(guild, "emojis", ()) if guild is not None else ():
        if str(getattr(emoji, "name", "")).casefold() == expected_name.casefold():
            return emoji
    return fallback


def feature_emoji_map(guild) -> dict[str, str]:
    return {feature: str(_feature_emoji_object(guild, feature)) for feature in FEATURE_EMOJI}


def _icon(icons: dict[str, str] | None, feature: str) -> str:
    return (icons or {}).get(feature) or FEATURE_EMOJI[feature][1]


def _mission_label(mission: str | None, icons: dict[str, str] | None = None) -> str:
    label = str(mission or "Unknown")
    return f"{_icon(icons, 'survival')} {label}" if "survival" in label.casefold() else label


def _tier_role_key(prefix: str, tier: str) -> str:
    return f"{prefix}_{tier.casefold()}"


TIER_ROLES = {
    **{
        _tier_role_key("arbitration", tier): (f"Arbitration Tier {tier} Ping", emoji)
        for tier, emoji in ARBITRATION_TIER_EMOJI.items()
    },
    **{
        _tier_role_key("fissure", tier): (f"Normal Fissure {tier} Ping", FISSURE_TIER_EMOJI[tier])
        for tier in FISSURE_TIERS
    },
    **{
        _tier_role_key("steel_fissure", tier): (f"Steel Fissure {tier} Ping", FISSURE_TIER_EMOJI[tier])
        for tier in FISSURE_TIERS
    },
    **{
        _tier_role_key("void_storm", tier): (f"Void Storm {tier} Ping", FISSURE_TIER_EMOJI[tier])
        for tier in FISSURE_TIERS
    },
}

ROLES = {**BASE_ROLES, **TIER_ROLES}

ROLE_CHANNEL = {
    "steel_cascade": "world-cascade",
    "cascade": "world-cascade",
    "arbitration": "world-arbitration",
    "alerts": "world-alerts",
    "events": "world-alerts",
    "sortie": "world-sortie",
    "archon": "world-archon",
    "steel_path": "world-steel-path",
    "bounties": "world-bounties",
    "fissures": "world-fissures",
    "steel_fissures": "world-steel-fissures",
    "void_storms": "world-void-storms",
    "invasions": "world-invasions",
    "baro": "world-vendors",
    "news": "world-news",
    "archimedea": "world-archimedea",
    **{_tier_role_key("arbitration", tier): "world-arbitration" for tier in ARBITRATION_TIERS},
    **{_tier_role_key("fissure", tier): "world-fissures" for tier in FISSURE_TIERS},
    **{_tier_role_key("steel_fissure", tier): "world-steel-fissures" for tier in FISSURE_TIERS},
    **{_tier_role_key("void_storm", tier): "world-void-storms" for tier in FISSURE_TIERS},
}

ALLY_NAMES = {
    "AmirAllyAgent": "Amir", "AoiAllyAgent": "Aoi", "ArthurAllyAgent": "Arthur",
    "EleanorAllyAgent": "Eleanor", "LettieAllyAgent": "Lettie", "QuincyAllyAgent": "Quincy",
}

BOUNTY_LEVELS = {
    "ZarimanSyndicate": (["50–55", "60–65", "70–75", "90–95", "110–115"],
                         ["1/2 VQ", "2/3 VQ", "3/5 VQ", "4/6 VQ", "5/8 VQ"]),
    "EntratiLabSyndicate": (["55–60", "65–70", "75–80", "95–100", "115–120"],
                            ["1000/1500", "2000/3000", "3000/4500", "4000/6000", "5000/7500"]),
    "HexSyndicate": (["65–70", "75–80", "85–90", "95–100", "105–110", "115–120", "125–130"],
                      ["1000/1500", "2000/3000", "3000/4500", "4000/6000", "5000/7500", "6000/9000", "7500/11250"]),
}

BOUNTY_NAMES = {
    "ZarimanSyndicate": "The Holdfasts",
    "EntratiLabSyndicate": "Cavia",
    "HexSyndicate": "The Hex",
}


def _load_state() -> dict:
    try:
        with open(STATE_PATH, "r", encoding="utf-8") as source:
            data = json.load(source)
        return data if isinstance(data, dict) else {}
    except (FileNotFoundError, json.JSONDecodeError, OSError):
        return {}


def _save_state(data: dict) -> None:
    temp = STATE_PATH + ".tmp"
    with open(temp, "w", encoding="utf-8") as target:
        json.dump(data, target, indent=2)
    os.replace(temp, STATE_PATH)


def _ts(value: str | int | float | None, style: str = "R") -> str:
    stamp = int(value) if isinstance(value, (int, float)) else unix_time(value)
    return f"<t:{stamp}:{style}>" if stamp is not None else "time unavailable"


def _trim(text: str, limit: int = 4000) -> str:
    text = text.strip()
    if len(text) <= limit:
        return text
    return text[: limit - 16].rstrip() + "\n…more omitted"


def _lines(lines: list[str], empty: str = "Nothing active right now.") -> str:
    return _trim("\n".join(line for line in lines if line) or empty)


def _embed(title: str, description: str, color: int = 0x5A67D8) -> discord.Embed:
    embed = discord.Embed(title=title, description=_trim(description), color=color)
    embed.set_footer(text="Live world state • auto-updates • WarframeStat.us + browse.wf")
    return embed


def _reward_text(reward: dict | None) -> str:
    if not reward:
        return "No reward listed"
    parts = []
    credits = reward.get("credits")
    if credits:
        parts.append(f"{int(credits):,} Credits")
    parts.extend(str(item) for item in reward.get("items") or [])
    for item in reward.get("countedItems") or []:
        count = item.get("count", 1)
        parts.append(f"{count}× {item.get('type') or item.get('key') or 'Item'}")
    return ", ".join(parts) or "No reward listed"


def _node_details(data: dict, node_key: str) -> tuple[str, str, str, int | None, int | None]:
    dictionary = data.get("dictionary") or {}
    region = (data.get("regions") or {}).get(node_key) or {}
    resolved = (data.get("sol_nodes") or {}).get(node_key) or {}
    node = translate(dictionary, region.get("name"), resolved.get("value") or node_key)
    system = translate(dictionary, region.get("systemName"), "")
    if system and system not in node:
        node = f"{node} ({system})"
    mission = translate(dictionary, region.get("missionName"), resolved.get("type") or "Unknown")
    enemy = resolved.get("enemy") or translate(dictionary, region.get("faction"), region.get("faction") or "Unknown")
    return node, mission, enemy, region.get("minEnemyLevel"), region.get("maxEnemyLevel")


def _challenge_text(data: dict, key: str) -> str:
    dictionary = data.get("dictionary") or {}
    challenge = (data.get("challenges") or {}).get(key) or {}
    title = translate(dictionary, challenge.get("name"), key.rsplit("/", 1)[-1])
    desc = translate(dictionary, challenge.get("description"), "")
    required = str(challenge.get("requiredCount", ""))
    desc = desc.replace("|COUNT|", required)
    # The game uses these tokens to tint labels such as "Quincy Bounty".
    # Discord reads the adjacent pipes as spoiler markup, so drop the colored
    # label; the relevant ally is already displayed beside the bounty row.
    desc = re.sub(r"\|OPEN_COLOR\|.*?\|CLOSE_COLOR\|", "", desc, flags=re.DOTALL)
    desc = re.sub(r"\|[A-Z_]+\|", "", desc)
    desc = re.sub(r"\s+", " ", desc.replace("\r", " ").replace("\n", " ")).strip()
    return f"{title}: {desc}" if desc and desc.lower() != title.lower() else title


def _add_section_fields(embed: discord.Embed, name: str, rows: list[str], limit: int = 1024) -> None:
    """Add complete rows without cutting Discord markdown at a field limit."""
    if not rows:
        embed.add_field(name=name, value="Unavailable", inline=False)
        return
    chunks: list[str] = []
    current = ""
    for row in rows:
        candidate = f"{current}\n\n{row}" if current else row
        if current and len(candidate) > limit:
            chunks.append(current)
            current = row
        else:
            current = candidate
    if current:
        chunks.append(current)
    for index, chunk in enumerate(chunks):
        label = name if index == 0 else f"{name} (continued)"
        embed.add_field(name=label, value=_trim(chunk, limit), inline=False)


def cycles_embed(data: dict) -> discord.Embed:
    world = data["world"]
    specs = [
        ("Plains of Eidolon / Earth", "cetusCycle", "☀️", "🌑"),
        ("Orb Vallis", "vallisCycle", "☀️", "❄️"),
        ("Cambion Drift", "cambionCycle", "☀️", "🌑"),
        ("Duviri", "duviriCycle", "🎭", "🎭"),
        ("Zariman", "zarimanCycle", "🚀", "🚀"),
    ]
    lines = []
    for label, key, first_emoji, second_emoji in specs:
        cycle = world.get(key) or {}
        if not cycle.get("state") or not cycle.get("expiry"):
            lines.append(f"**{label}** · Data temporarily unavailable.")
            continue
        state = str(cycle.get("state")).title()
        positive = cycle.get("isDay", cycle.get("isWarm", True))
        emoji = first_emoji if positive else second_emoji
        lines.append(f"**{label}** · {emoji} {state} · {_ts(cycle.get('expiry'))}")
    return _embed("🌍 Environments", _lines(lines), 0x2D9CDB)


def news_embeds(data: dict) -> list[discord.Embed]:
    world = data["world"]
    news_lines = []
    news = sorted(world.get("news") or [], key=lambda n: n.get("date") or "", reverse=True)
    for item in news[:12]:
        message = item.get("message") or "Warframe news"
        date = _ts(item.get("date"))
        link = item.get("link")
        news_lines.append(f"{date} · [{message}]({link})" if link else f"{date} · {message}")
    kine = world.get("kinepage") or {}
    kine_text = kine.get("message") or "No new messages. Scanning…"
    kine_posted = f"\n\nPosted {_ts(kine.get('timestamp'))}" if kine.get("timestamp") else ""
    return [
        _embed("📰 News", _lines(news_lines, "No current news."), 0x3498DB),
        _embed("📻 KinePage", f"{kine_text}{kine_posted}", 0x9B59B6),
    ]


def alerts_embeds(data: dict, icons: dict[str, str] | None = None) -> list[discord.Embed]:
    world = data["world"]
    alert_lines = []
    for alert in active_items(world.get("alerts")):
        mission = alert.get("mission") or {}
        alert_lines.append(
            f"**{_mission_label(mission.get('type'), icons)} — {mission.get('faction', 'Unknown')}** "
            f"({mission.get('minEnemyLevel', '?')}–{mission.get('maxEnemyLevel', '?')}) · "
            f"{mission.get('node', 'Unknown node')} · {_ts(alert.get('expiry'))}\n"
            f"↳ {_reward_text(mission.get('reward'))}"
        )
    event_lines = []
    for event in active_items(world.get("events")):
        label = event.get("description") or event.get("tooltip") or event.get("tag") or "Event"
        score = ""
        if event.get("maximumScore"):
            score = f" · progress {event.get('currentScore', 0)}/{event['maximumScore']}"
        event_lines.append(f"**{label}**{score} · {_ts(event.get('expiry'))}")
    return [
        _embed("🚨 Alerts", _lines(alert_lines), 0xE74C3C),
        _embed("🎉 Events", _lines(event_lines), 0xF39C12),
    ]


def _sortie_embed(world: dict, icons: dict[str, str] | None = None) -> discord.Embed:
    sortie = world.get("sortie") or {}
    if not sortie or not is_active(sortie):
        return _embed("🎯 Sortie", "Current Sortie data is temporarily unavailable.", 0xE67E22)
    lines = []
    for mission in sortie.get("variants") or []:
        lines.append(f"**{_mission_label(mission.get('missionType'), icons)}** · {mission.get('modifier', 'No modifier')} · {mission.get('node', '')}")
    boss = sortie.get("boss")
    desc = (f"**{boss}** · resets {_ts(sortie.get('expiry'))}\n" if boss else "") + _lines(lines)
    return _embed("🎯 Sortie", desc, 0xE67E22)


def _archon_embed(world: dict, icons: dict[str, str] | None = None) -> discord.Embed:
    hunt = world.get("archonHunt") or {}
    if not hunt or not is_active(hunt):
        return _embed("🐺 Archon Hunt", "Current Archon Hunt data is temporarily unavailable.", 0xC0392B)
    missions = [_mission_label(m.get("type") or m.get("missionType"), icons) for m in hunt.get("missions") or []]
    boss = hunt.get("boss") or "Unknown Archon"
    return _embed("🐺 Archon Hunt", f"**{boss}** · {', '.join(missions) or 'No missions listed'}\nResets {_ts(hunt.get('expiry'))}", 0xC0392B)


def _steel_path_embed(data: dict, icons: dict[str, str] | None = None) -> discord.Embed:
    current = current_sp_incursions(data.get("sp_incursions") or {})
    lines = []
    expiry = None
    if current:
        day, nodes = current
        expiry = day + 86400
        for key in nodes:
            node, mission, enemy, low, high = _node_details(data, key)
            levels = f" ({100 + low}–{100 + high})" if isinstance(low, int) and isinstance(high, int) else ""
            lines.append(f"**{_mission_label(mission, icons)} — {enemy}**{levels} · {node}")
    if not current:
        return _embed("💀 Steel Path Incursions", "Current Incursion schedule is temporarily unavailable.", 0x2C3E50)
    return _embed("💀 Steel Path Incursions", f"Resets {_ts(expiry)}\n" + _lines(lines), 0x2C3E50)


def _weekly_embed(world: dict) -> discord.Embed:
    duviri = world.get("duviriCycle") or {}
    normal, hard = [], []
    for choices in duviri.get("choices") or []:
        if choices.get("category") == "normal":
            normal = choices.get("choices") or []
        elif choices.get("category") == "hard":
            hard = choices.get("choices") or []
    reset = (world.get("archonHunt") or {}).get("expiry")
    lines = [
        "• Help Clem", "• Ayatan Treasure Hunt",
        f"• The Circuit (Normal): **{', '.join(normal) or 'Unavailable'}**",
        f"• The Circuit (Steel Path): **{', '.join(hard) or 'Unavailable'}**",
        "• Netracells", "• Break Narmer", "• Descendia",
    ]
    reset_text = f"Resets {_ts(reset)}\n" if reset else ""
    return _embed("📅 Weekly Missions", reset_text + "\n".join(lines), 0x16A085)


def _archimedea_embed(entry: dict, title: str, icons: dict[str, str] | None = None) -> discord.Embed:
    reset_text = f"Resets {_ts(entry.get('expiry'))}" if entry.get("expiry") else "Reset time unavailable."
    embed = _embed(title, reset_text, 0x8E44AD)
    for mission in entry.get("missions") or []:
        deviation = (mission.get("deviation") or {}).get("name") or "No deviation"
        risks = [risk.get("name", "Unknown risk") for risk in mission.get("risks") or []]
        value = f"Deviation: **{deviation}**\nRisks: {', '.join(risks) or 'None'}"
        embed.add_field(name=_mission_label(mission.get("missionType") or "Mission", icons), value=_trim(value, 1024), inline=False)
    modifiers = [m.get("name", "Unknown") for m in entry.get("personalModifiers") or []]
    if modifiers:
        embed.add_field(name="Personal modifiers", value=", ".join(modifiers), inline=False)
    return embed


def mission_embeds(data: dict, icons: dict[str, str] | None = None) -> list[discord.Embed]:
    world = data["world"]
    embeds = [_sortie_embed(world, icons), _archon_embed(world, icons), _steel_path_embed(data, icons), _weekly_embed(world)]
    for entry in world.get("archimedeas") or []:
        key = str(entry.get("typeKey") or entry.get("type") or "")
        title = "🧬 Temporal Archimedea" if "HEX" in key.replace(" ", "") else "🧬 Deep Archimedea"
        embeds.append(_archimedea_embed(entry, title, icons))
    return embeds[:10]


def archimedea_embeds(data: dict, icons: dict[str, str] | None = None) -> list[discord.Embed]:
    embeds = []
    for entry in data["world"].get("archimedeas") or []:
        key = str(entry.get("typeKey") or entry.get("type") or "")
        title = "🧬 Temporal Archimedea" if "HEX" in key.replace(" ", "") else "🧬 Deep Archimedea"
        embeds.append(_archimedea_embed(entry, title, icons))
    return embeds or [_embed("🧬 Archimedea", "Current modifiers are unavailable.", 0x8E44AD)]


def vendor_embeds(data: dict, icons: dict[str, str] | None = None) -> list[discord.Embed]:
    world = data["world"]
    deal = next(iter(active_items(world.get("dailyDeals"))), None)
    if deal:
        total = max(0, int(deal.get("total", 0) or 0))
        sold = max(0, int(deal.get("sold", 0) or 0))
        stock = max(0, total - sold)
        darvo_text = (
            f"**{deal.get('item') or 'Unknown item'}**\n"
            f"{deal.get('salePrice', '?')} Platinum · {deal.get('discount', '?')}% off · "
            f"{stock}/{total} in stock · ends {_ts(deal.get('expiry'))}"
        )
    else:
        darvo_text = "No active Darvo deal right now."
    darvo = _embed(
        "🛍️ Darvo's Deal",
        darvo_text,
        0x3498DB,
    )
    steel = world.get("steelPath") or {}
    reward = (steel.get("currentReward") or {}) if steel and is_active(steel) else {}
    if reward:
        steel_text = f"{reward.get('name') or 'Unknown offering'} · {reward.get('cost', '?')} Steel Essence"
    else:
        steel_text = "Temporarily unavailable"
    reset_text = f"\nResets {_ts(steel.get('expiry'))}" if steel.get("expiry") else ""
    vendors = _embed(
        "🏪 Vendors",
        f"**Steel Path Honors:** {steel_text}\n"
        f"**Iron Wake:** weekly offerings available{reset_text}",
        0x95A5A6,
    )
    baro = world.get("voidTrader") or {}
    now = time.time()
    activation, expiry = unix_time(baro.get("activation")), unix_time(baro.get("expiry"))
    if activation and activation <= now and (not expiry or now < expiry):
        inventory = baro.get("inventory") or []
        items = [f"• {i.get('item', 'Unknown')} — {_icon(icons, 'baro')} {i.get('ducats', '?')} Ducats + {i.get('credits', '?')} Credits" for i in inventory[:20]]
        baro_text = f"At **{baro.get('location', 'Unknown relay')}** · leaves {_ts(expiry)}\n" + _lines(items)
    elif activation:
        baro_text = f"Next visit: **{baro.get('location', 'Unknown relay')}** · arrives {_ts(activation)}"
    else:
        baro_text = "Baro's next visit is temporarily unavailable."
    return [darvo, vendors, _embed(f"{_icon(icons, 'baro')} Baro Ki'Teer", baro_text, 0xF1C40F)]


def bounties_embed(data: dict, icons: dict[str, str] | None = None) -> discord.Embed:
    bounty = data.get("bounty") or {}
    if not bounty:
        return _embed("📋 Bounties", "Current bounty rotations are temporarily unavailable.", 0x27AE60)
    rot = bounty.get("rot", "?")
    vault_rot = bounty.get("vaultRot", "?")
    bounty_expiry = (bounty.get("expiry") / 1000) if bounty.get("expiry") else None
    embed = _embed("📋 Bounties", f"Rotation **{rot}** · Vault Rotation **{vault_rot}** · refreshes {_ts(bounty_expiry)}", 0x27AE60)
    for key in ("ZarimanSyndicate", "EntratiLabSyndicate", "HexSyndicate"):
        rows = []
        levels, standing = BOUNTY_LEVELS[key]
        for index, job in enumerate((bounty.get("bounties") or {}).get(key) or []):
            node, mission, _enemy, _low, _high = _node_details(data, job.get("node", ""))
            objective = _challenge_text(data, job.get("challenge", ""))
            ally_key = str(job.get("ally") or "").rsplit("/", 1)[-1]
            ally = ALLY_NAMES.get(ally_key)
            suffix = f" · {levels[index]} · {standing[index]}" if index < len(levels) else ""
            if ally:
                suffix += f" · {ally}"
            rows.append(f"**{_mission_label(mission, icons)} · {node}**\n{objective}{suffix}")
        _add_section_fields(embed, BOUNTY_NAMES[key], rows)
    return embed


def _normalized_node(value: str) -> str:
    """Match API nodes like `Hydron (Sedna)` to schedule locations like `Hydron, Sedna`."""
    value = re.sub(r"\s*\([^)]*\)\s*$", "", str(value or ""))
    return value.split(",", 1)[0].strip().casefold()


def _arbitration_tier_maps(entries) -> tuple[dict[tuple[str, str], str], dict[str, str]]:
    exact: dict[tuple[str, str], str] = {}
    node_values: dict[str, set[str]] = {}
    for entry in entries or []:
        node = _normalized_node(entry.location)
        mission = str(entry.mission_type or "").strip().casefold()
        tier = str(entry.tier or "").strip().upper()
        if not node or tier not in ARBITRATION_TIERS:
            continue
        exact[(node, mission)] = tier
        node_values.setdefault(node, set()).add(tier)
    unambiguous = {node: next(iter(tiers)) for node, tiers in node_values.items() if len(tiers) == 1}
    return exact, unambiguous


def fissure_arbitration_tier(item: dict, entries) -> str | None:
    exact, by_node = _arbitration_tier_maps(entries)
    node = _normalized_node(item.get("node", ""))
    mission = str(item.get("missionType") or "").strip().casefold()
    return exact.get((node, mission)) or by_node.get(node)


def _fissure_lines(items: list[dict], arbitration_entries=(), icons: dict[str, str] | None = None) -> list[str]:
    items = sorted(items, key=lambda f: (f.get("tierNum", 99), f.get("expiry") or ""))
    exact, by_node = _arbitration_tier_maps(arbitration_entries)
    lines = []
    for item in items:
        node = _normalized_node(item.get("node", ""))
        mission = str(item.get("missionType") or "").strip().casefold()
        mission_tier = exact.get((node, mission)) or by_node.get(node)
        tier_text = f" · **Lvl {mission_tier} tier**" if mission_tier else ""
        lines.append(
            f"**{item.get('tier') or '?'} · {_mission_label(item.get('missionType'), icons)}**{tier_text} · "
            f"{item.get('node') or 'Unknown node'} · {_ts(item.get('expiry'))}"
        )
    return lines


def fissure_embed(
    data: dict, *, hard: bool = False, storm: bool = False, arbitration_entries=(),
    icons: dict[str, str] | None = None,
) -> discord.Embed:
    fissures = [
        f for f in active_items(data["world"].get("fissures"))
        if bool(f.get("isStorm")) is storm and (storm or bool(f.get("isHard")) is hard)
    ]
    if storm:
        title, color = "🚀 Void Storms (Railjack)", 0x2980B9
    elif hard:
        title, color = f"{_icon(icons, 'fissure')} Void Fissures (Steel Path)", 0x34495E
    else:
        title, color = f"{_icon(icons, 'fissure')} Void Fissures (Normal)", 0x8E44AD
    empty = "Live mission source is delayed; retrying automatically." if data.get("mission_data_stale") else "Nothing active right now."
    embed = _embed(title, _lines(_fissure_lines(fissures, arbitration_entries, icons), empty), color)
    if data.get("mission_source") == "official":
        embed.set_footer(text="Live world state • official Warframe fallback active • other data: WarframeStat.us + browse.wf")
    return embed


def invasions_embed(data: dict) -> discord.Embed:
    lines = []
    for invasion in data["world"].get("invasions") or []:
        if invasion.get("completed"):
            continue
        attacker = invasion.get("attacker") or {}
        defender = invasion.get("defender") or {}
        try:
            completion = float(invasion.get("completion", 0) or 0)
        except (TypeError, ValueError):
            completion = 0.0
        lines.append(
            f"**{invasion.get('node', 'Unknown node')}** · {invasion.get('desc', 'Invasion')} · "
            f"{completion:.1f}%\n"
            f"↳ {attacker.get('faction', '?')}: {_reward_text(attacker.get('reward'))} | "
            f"{defender.get('faction', '?')}: {_reward_text(defender.get('reward'))}"
        )
    return _embed("⚔️ Invasions", _lines(lines), 0xD35400)


def arbitration_embed(
    entries, data: dict, now: float | None = None, icons: dict[str, str] | None = None,
) -> discord.Embed:
    now = time.time() if now is None else now
    current = current_arbitration(entries, now)
    if current:
        head = (
            f"**{_mission_label(current.mission_type, icons)} — {current.enemy}**\n{current.location} · **{current.tier} tier**"
            f"{' · ' + current.resource_bonus if current.resource_bonus else ''} · ends {_ts(current.expiry)}"
        )
    else:
        live = data["world"].get("arbitration") or {}
        if not live.get("expired") and live.get("type") not in (None, "Unknown"):
            head = f"**{_mission_label(live.get('type'), icons)} — {live.get('enemy')}**\n{live.get('node')} · ends {_ts(live.get('expiry'))}"
        else:
            head = "Current Arbitration unavailable."
    upcoming = [entry for entry in entries if entry.activation > now][:6]
    next_lines = [f"{_ts(entry.activation, 'f')} · **{_mission_label(entry.mission_type, icons)}** · {entry.location} · {entry.tier} tier" for entry in upcoming]
    return _embed(f"{_icon(icons, 'arbitration')} Arbitration", head + "\n\n**Coming next**\n" + _lines(next_lines, "No schedule available."), 0xF39C12)


def cascade_embed(
    entries, data: dict, now: float | None = None, icons: dict[str, str] | None = None,
) -> discord.Embed:
    now = time.time() if now is None else now
    cascades = [f for f in active_items(data["world"].get("fissures"), now)
                if str(f.get("missionType", "")).casefold() == "void cascade" and not f.get("isStorm")]
    empty = "Live mission source is delayed; retrying automatically." if data.get("mission_data_stale") else "None active."
    desc = "**Normal Void Cascade fissures**\n" + _lines(
        _fissure_lines([f for f in cascades if not f.get("isHard")], entries, icons), empty)
    desc += "\n\n**Steel Path Void Cascade fissures**\n" + _lines(
        _fissure_lines([f for f in cascades if f.get("isHard")], entries, icons), empty)
    desc += "\n\nChoose normal and/or Steel Path Cascade notifications in **#role-pings**."
    return _embed(f"{_icon(icons, 'cascade')} Void Cascade Watch", desc, 0x00A8CC)


def build_channel_embeds(
    channel_key: str, data: dict, arbitration_entries, icons: dict[str, str] | None = None,
) -> list[discord.Embed]:
    if channel_key == "bot-guide":
        return guide_embeds()
    if channel_key == "world-cycles":
        embeds = [cycles_embed(data)]
    elif channel_key == "world-news":
        embeds = news_embeds(data)
    elif channel_key == "world-alerts":
        embeds = alerts_embeds(data, icons)
    elif channel_key == "world-sortie":
        embeds = [_sortie_embed(data["world"], icons)]
    elif channel_key == "world-archon":
        embeds = [_archon_embed(data["world"], icons)]
    elif channel_key == "world-steel-path":
        embeds = [_steel_path_embed(data, icons)]
    elif channel_key == "world-weekly":
        embeds = [_weekly_embed(data["world"])]
    elif channel_key == "world-archimedea":
        embeds = archimedea_embeds(data, icons)
    elif channel_key == "world-vendors":
        embeds = vendor_embeds(data, icons)
    elif channel_key == "world-bounties":
        embeds = [bounties_embed(data, icons)]
    elif channel_key == "world-fissures":
        embeds = [fissure_embed(data, arbitration_entries=arbitration_entries, icons=icons)]
    elif channel_key == "world-steel-fissures":
        embeds = [fissure_embed(data, hard=True, arbitration_entries=arbitration_entries, icons=icons)]
    elif channel_key == "world-void-storms":
        embeds = [fissure_embed(data, storm=True, arbitration_entries=arbitration_entries, icons=icons)]
    elif channel_key == "world-invasions":
        embeds = [invasions_embed(data)]
    elif channel_key == "world-arbitration":
        embeds = [arbitration_embed(arbitration_entries, data, icons=icons)]
    elif channel_key == "world-cascade":
        embeds = [cascade_embed(arbitration_entries, data, icons=icons)]
    else:
        embeds = []

    if data.get("official_fallback_sections"):
        footer = "Live world state • official DE fallback active • WarframeStat.us + browse.wf where needed"
        for embed in embeds:
            embed.set_footer(text=footer)
    elif data.get("world_state_stale"):
        footer = "⚠️ Live world-state source delayed • retrying automatically"
        for embed in embeds:
            embed.set_footer(text=footer)
    return embeds


def notification_signatures(data: dict, arbitration_entries, now: float | None = None) -> dict[str, list[str]]:
    now = time.time() if now is None else now
    world = data["world"]
    current_arb = current_arbitration(arbitration_entries, now)
    fissures = active_items(world.get("fissures"), now)
    normal_fissures = [f for f in fissures if not f.get("isStorm") and not f.get("isHard")]
    steel_fissures = [f for f in fissures if not f.get("isStorm") and bool(f.get("isHard"))]
    void_storms = [f for f in fissures if bool(f.get("isStorm"))]
    cascade_ids = [str(f.get("id")) for f in normal_fissures
                   if str(f.get("missionType", "")).casefold() == "void cascade"]
    steel_cascade_ids = [str(f.get("id")) for f in steel_fissures
                         if str(f.get("missionType", "")).casefold() == "void cascade"]
    baro = world.get("voidTrader") or {}
    archimedeas = world.get("archimedeas") or []
    steel = current_sp_incursions(data.get("sp_incursions") or {}, now)
    bounty = data.get("bounty") or {}
    signatures = {
        "cascade": sorted(cascade_ids),
        "steel_cascade": sorted(steel_cascade_ids),
        "arbitration": [str(current_arb.activation)] if current_arb else [],
        "alerts": sorted(str(x.get("id")) for x in active_items(world.get("alerts"), now)),
        "events": sorted(str(x.get("id")) for x in active_items(world.get("events"), now)),
        "sortie": [str((world.get("sortie") or {}).get("id"))] if (world.get("sortie") or {}).get("id") else [],
        "archon": [str((world.get("archonHunt") or {}).get("id"))] if (world.get("archonHunt") or {}).get("id") else [],
        "steel_path": [str(steel[0])] if steel else [],
        "bounties": [str(bounty.get("expiry"))] if bounty.get("expiry") else [],
        "fissures": sorted(str(x.get("id")) for x in normal_fissures),
        "steel_fissures": sorted(str(x.get("id")) for x in steel_fissures),
        "void_storms": sorted(str(x.get("id")) for x in void_storms),
        "invasions": sorted(str(x.get("id")) for x in world.get("invasions") or [] if not x.get("completed")),
        "baro": ([f"active:{baro.get('activation')}"] if (
            baro.get("activation") and (unix_time(baro.get("activation")) or float("inf")) <= now
            and (unix_time(baro.get("expiry")) is None or now < unix_time(baro.get("expiry")))
        ) else ([f"scheduled:{baro.get('activation')}"] if baro.get("activation") else [])),
        "news": sorted(str(x.get("id")) for x in (world.get("news") or [])[:20]),
        "archimedea": sorted(str(x.get("id")) for x in archimedeas),
    }
    for tier in ARBITRATION_TIERS:
        key = _tier_role_key("arbitration", tier)
        signatures[key] = (
            [str(current_arb.activation)] if current_arb and current_arb.tier.upper() == tier else []
        )
    for tier in FISSURE_TIERS:
        tier_folded = tier.casefold()
        signatures[_tier_role_key("fissure", tier)] = sorted(
            str(item.get("id")) for item in normal_fissures
            if str(item.get("tier") or "").casefold() == tier_folded
        )
        signatures[_tier_role_key("steel_fissure", tier)] = sorted(
            str(item.get("id")) for item in steel_fissures
            if str(item.get("tier") or "").casefold() == tier_folded
        )
        signatures[_tier_role_key("void_storm", tier)] = sorted(
            str(item.get("id")) for item in void_storms
            if str(item.get("tier") or "").casefold() == tier_folded
        )
    return signatures


PING_TEXT = {
    "cascade": "A normal Void Cascade fissure is active now.",
    "steel_cascade": "A Steel Path Void Cascade fissure is active now.",
    "arbitration": "A new Arbitration rotation is active.",
    "alerts": "A new Warframe alert is active.",
    "events": "A new Warframe event is active.",
    "sortie": "A new Sortie is available.",
    "archon": "A new Archon Hunt is available.",
    "steel_path": "The Steel Path Incursions have rotated.",
    "bounties": "The Zariman, Cavia, and Hex bounties have rotated.",
    "fissures": "New Void Fissures are available.",
    "steel_fissures": "New Steel Path Void Fissures are available.",
    "void_storms": "New Railjack Void Storms are available.",
    "invasions": "A new Invasion is active.",
    "baro": "Baro Ki'Teer's schedule has changed.",
    "news": "New Warframe news was posted.",
    "archimedea": "The Archimedea missions have rotated.",
    **{
        _tier_role_key("arbitration", tier): f"A Tier {tier} Arbitration is active."
        for tier in ARBITRATION_TIERS
    },
    **{
        _tier_role_key("fissure", tier): f"A new normal {tier} Fissure is available."
        for tier in FISSURE_TIERS
    },
    **{
        _tier_role_key("steel_fissure", tier): f"A new Steel Path {tier} Fissure is available."
        for tier in FISSURE_TIERS
    },
    **{
        _tier_role_key("void_storm", tier): f"A new {tier} Void Storm is available."
        for tier in FISSURE_TIERS
    },
}

ROLE_MENUS = (
    ("general", "Choose a general notification", tuple(BASE_ROLES)),
    ("arbitration", "Choose an Arbitration tier", tuple(_tier_role_key("arbitration", tier) for tier in ARBITRATION_TIERS)),
    ("fissure", "Choose a normal Fissure tier", tuple(_tier_role_key("fissure", tier) for tier in FISSURE_TIERS)),
    ("steel", "Choose a Steel Fissure tier", tuple(_tier_role_key("steel_fissure", tier) for tier in FISSURE_TIERS)),
    ("storm", "Choose a Void Storm tier", tuple(_tier_role_key("void_storm", tier) for tier in FISSURE_TIERS)),
)


class PingRoleView(discord.ui.View):
    def __init__(self, manager: "WorldStateManager", guild_id: int):
        super().__init__(timeout=None)
        self.manager = manager
        self.guild_id = guild_id
        for row, (menu_key, placeholder, role_keys) in enumerate(ROLE_MENUS):
            select = discord.ui.Select(
                placeholder=placeholder,
                min_values=1,
                max_values=1,
                row=row,
                custom_id=f"relicframe:world-role-menu:{guild_id}:{menu_key}",
                options=[
                    discord.SelectOption(
                        label=ROLES[key][0].replace(" Ping", ""), value=key,
                        emoji=(manager.role_emoji(guild_id, key) if hasattr(manager, "role_emoji") else ROLES[key][1]),
                    )
                    for key in role_keys
                ],
            )

            async def callback(interaction: discord.Interaction, menu=select):
                await self.manager.toggle_role(interaction, menu.values[0])

            select.callback = callback
            self.add_item(select)


class WorldStateManager:
    def __init__(self, bot):
        self.bot = bot
        self.client = WorldStateClient()
        timezone_name = os.environ.get("WORLD_STATE_TIMEZONE", "America/New_York")
        schedule_path = os.environ.get("ARBITRATION_SCHEDULE_PATH", DEFAULT_ARBITRATION_PATH)
        self.arbitration_entries = load_arbitration_schedule(schedule_path, timezone_name=timezone_name)
        self.data = _load_state()
        self.latest: dict | None = None
        self.last_error: str | None = None
        self.last_success: float | None = None
        self._task: asyncio.Task | None = None
        self._refresh_lock = asyncio.Lock()
        self._setup_locks = {}
        self._setting_up_guilds = set()
        self._unavailable_guilds_logged: set[int] = set()

    def _save(self):
        _save_state(self.data)

    def _guild(self, guild_id: int) -> dict:
        return self.data.setdefault("guilds", {}).setdefault(str(guild_id), {})

    def _prune_unavailable_guilds(self, keep_guild_id: int) -> list[int]:
        """Drop stale local IDs only when an admin sets up an accessible server."""
        removed = []
        guilds = self.data.setdefault("guilds", {})
        get_guild = getattr(self.bot, "get_guild", None)
        if not callable(get_guild):
            return removed
        for saved_id in list(guilds):
            try:
                parsed_id = int(saved_id)
            except (TypeError, ValueError):
                parsed_id = None
            if parsed_id != keep_guild_id and (parsed_id is None or get_guild(parsed_id) is None):
                guilds.pop(saved_id, None)
                if parsed_id is not None:
                    removed.append(parsed_id)
                getattr(self, "_unavailable_guilds_logged", set()).discard(parsed_id)
        return removed

    def emoji_map(self, guild_id: int | None) -> dict[str, str]:
        bot = getattr(self, "bot", None)
        guild = bot.get_guild(guild_id) if bot is not None and guild_id is not None else None
        return feature_emoji_map(guild)

    def role_emoji(self, guild_id: int, role_key: str):
        feature = ROLE_FEATURE_EMOJI.get(role_key)
        if feature:
            bot = getattr(self, "bot", None)
            guild = bot.get_guild(guild_id) if bot is not None else None
            return _feature_emoji_object(guild, feature)
        return ROLES[role_key][1]

    def start(self) -> None:
        if self._task is not None and not self._task.done():
            return
        for guild_id in self.data.get("guilds", {}):
            try:
                parsed_guild_id = int(guild_id)
            except (TypeError, ValueError):
                continue
            self.bot.add_view(PingRoleView(self, parsed_guild_id))
        self._task = asyncio.create_task(self._poll_forever())

    async def close(self) -> None:
        if self._task:
            self._task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._task
            self._task = None
        await self.client.close()

    async def _poll_forever(self) -> None:
        await self.bot.wait_until_ready()
        while not self.bot.is_closed():
            started = time.monotonic()
            try:
                if self.data.get("guilds"):
                    await self.refresh_all()
            except asyncio.CancelledError:
                raise
            except Exception as exc:  # noqa: BLE001 - keep the live feed alive
                self.last_error = str(exc) or type(exc).__name__
                print(f"[world-state] refresh failed: {self.last_error}")
            elapsed = time.monotonic() - started
            await asyncio.sleep(max(5.0, POLL_SECONDS - elapsed))

    async def refresh_all(self, guild_id: int | None = None) -> dict:
        async with self._refresh_lock:
            self.latest = await self.client.fetch()
            self.last_success = time.time()
            self.last_error = None
            if guild_id is not None:
                targets = [guild_id]
            else:
                targets = []
                for saved_guild_id in self.data.get("guilds", {}):
                    try:
                        targets.append(int(saved_guild_id))
                    except (TypeError, ValueError):
                        print(f"[world-state] ignoring invalid saved server id: {saved_guild_id!r}")
            guild_errors = []
            for target in targets:
                if guild_id is None and target in getattr(self, "_setting_up_guilds", set()):
                    continue  # do not render partially provisioned channel/role state
                get_guild = getattr(getattr(self, "bot", None), "get_guild", None)
                if callable(get_guild) and get_guild(target) is None:
                    logged = getattr(self, "_unavailable_guilds_logged", set())
                    if target not in logged:
                        print(
                            f"[world-state] saved server {target} is not available to this bot; "
                            "install the bot to that server, then run /world setup."
                        )
                        logged.add(target)
                        self._unavailable_guilds_logged = logged
                    guild_errors.append(f"{target}: bot is not installed in or cannot access this server")
                    continue
                getattr(self, "_unavailable_guilds_logged", set()).discard(target)
                try:
                    await self.update_guild(target, self.latest)
                except Exception as exc:  # noqa: BLE001 - one server cannot block the others
                    reason = str(exc) or type(exc).__name__
                    print(f"[world-state] guild {target} update failed: {reason}")
                    guild_errors.append(f"{target}: {reason}")
            if guild_errors:
                self.last_error = f"{len(guild_errors)} server update(s) failed: {guild_errors[0][:350]}"
                if guild_id is not None:
                    raise RuntimeError(self.last_error)
            return self.latest

    async def setup_guild(self, guild: discord.Guild, *, automatic=False) -> str:
        async with self._setup_locks.setdefault(guild.id, asyncio.Lock()):
            self._setting_up_guilds.add(guild.id)
            try:
                return await self._setup_guild(guild, automatic=automatic)
            finally:
                self._setting_up_guilds.discard(guild.id)

    async def _setup_guild(self, guild: discord.Guild, *, automatic=False) -> str:
        removed_guilds = [] if automatic else self._prune_unavailable_guilds(guild.id)
        cfg = self._guild(guild.id)
        category = guild.get_channel(cfg.get("category_id") or 0)
        if not isinstance(category, discord.CategoryChannel):
            category = discord.utils.get(guild.categories, name=WORLD_CATEGORY_NAME)
        if category is None:
            category = await guild.create_category(WORLD_CATEGORY_NAME, reason="RelicFrame live Warframe world state")
        cfg["category_id"] = category.id

        created_channels = []
        channel_cfg = cfg.setdefault("channels", {})
        for key, topic in CHANNELS.items():
            name = CHANNEL_NAMES[key]
            saved = channel_cfg.setdefault(key, {"channel_id": None, "message_id": None})
            channel = guild.get_channel(saved.get("channel_id") or 0)
            if not isinstance(channel, discord.TextChannel):
                channel = (discord.utils.get(category.text_channels, name=name)
                           or discord.utils.get(category.text_channels, name=key))
            if channel is None:
                channel = await guild.create_text_channel(name, category=category, topic=topic, reason="RelicFrame world-state feed")
                created_channels.append(name)
            elif channel.name != name:
                await channel.edit(name=name, topic=topic, reason="RelicFrame channel name migration")
            saved["channel_id"] = channel.id

        role_cfg = cfg.setdefault("roles", {})
        for key, (name, _emoji) in ROLES.items():
            role = guild.get_role(role_cfg.get(key) or 0)
            if role is None:
                role = discord.utils.get(guild.roles, name=name)
            if role is None and key == "cascade":
                role = discord.utils.get(guild.roles, name="Void Cascade Ping")
            if role is None:
                role = await guild.create_role(name=name, mentionable=True, reason="RelicFrame opt-in world-state ping")
            elif key == "cascade" and role.name == "Void Cascade Ping":
                await role.edit(name=name, reason="Normal Cascade fissure notification role")
            role_cfg[key] = role.id

        self._save()
        await self.refresh_all(guild.id)
        return (
            f"Configured **{len(CHANNELS)}** live world-state channels and **{len(ROLES)}** opt-in ping roles "
            f"in **{category.name}**."
            + (f" Created channels: {', '.join(created_channels)}." if created_channels else "")
            + (f" Removed {len(removed_guilds)} stale server configuration(s)." if removed_guilds else "")
        )

    async def toggle_role(self, interaction: discord.Interaction, role_key: str) -> None:
        if interaction.guild is None or role_key not in ROLES:
            await interaction.response.send_message("That role is unavailable here.", ephemeral=True)
            return
        role_id = (self._guild(interaction.guild.id).get("roles") or {}).get(role_key)
        role = interaction.guild.get_role(role_id or 0)
        if role is None:
            await interaction.response.send_message("That role is missing. Ask an admin to run `/world setup`.", ephemeral=True)
            return
        await interaction.response.defer(ephemeral=True)
        member = interaction.user
        try:
            if role in member.roles:
                await member.remove_roles(role, reason="RelicFrame opt-out")
                result = f"Removed **{role.name}**."
            else:
                await member.add_roles(role, reason="RelicFrame opt-in")
                result = f"Added **{role.name}**."
            await interaction.followup.send(result, ephemeral=True)
        except discord.Forbidden:
            await interaction.followup.send(
                "I cannot manage that role. Move my bot role above the ping roles and give me **Manage Roles**.",
                ephemeral=True,
            )

    def _ping_embed(self, guild_id: int | None = None) -> discord.Embed:
        lines = [
            f"{self.role_emoji(guild_id, key) if guild_id is not None else emoji} **{name}**"
            for key, (name, emoji) in ROLES.items()
        ]
        return _embed(
            "🔔 Live-feed notification roles",
            "Use the grouped menus below to add or remove your own ping roles. You are only pinged when new data appears—not on every one-minute check.\n\n"
            + "\n".join(lines),
            0x5865F2,
        )

    async def _channel(self, channel_id: int | None):
        channel = self.bot.get_channel(channel_id or 0)
        if channel is None and channel_id:
            try:
                channel = await self.bot.fetch_channel(channel_id)
            except discord.HTTPException:
                return None
        return channel

    async def _update_message(self, guild_id: int, key: str, embeds: list[discord.Embed], view=None) -> None:
        cfg = self._guild(guild_id)["channels"][key]
        channel = await self._channel(cfg.get("channel_id"))
        if not isinstance(channel, discord.TextChannel):
            raise RuntimeError(f"Saved channel for {key!r} is missing or inaccessible; run /world setup to repair it.")
        payload = json.dumps([embed.to_dict() for embed in embeds], sort_keys=True, ensure_ascii=False)
        render_hash = hashlib.sha256(payload.encode("utf-8")).hexdigest()
        unchanged = cfg.get("render_hash") == render_hash and cfg.get("message_id")
        recently_verified = time.time() - float(cfg.get("verified_at") or 0) < MESSAGE_VERIFY_SECONDS
        if unchanged and recently_verified:
            return
        try:
            message = await channel.fetch_message(cfg.get("message_id")) if cfg.get("message_id") else None
        except (discord.HTTPException, TypeError):
            message = None
        if message is None:
            message = await channel.send(embeds=embeds, view=view)
            cfg["message_id"] = message.id
        elif not unchanged:
            await message.edit(embeds=embeds, view=view)
        cfg["render_hash"] = render_hash
        cfg["verified_at"] = time.time()

    async def update_guild(self, guild_id: int, data: dict) -> None:
        cfg = self._guild(guild_id)
        if not cfg.get("channels"):
            return
        failures = []
        try:
            await self._update_message(guild_id, "world-pings", [self._ping_embed(guild_id)], PingRoleView(self, guild_id))
        except Exception as exc:  # noqa: BLE001 - continue repairing the remaining boards
            failures.append(f"world-pings: {exc}")
        for key in CHANNELS:
            if key == "world-pings":
                continue
            embeds = build_channel_embeds(key, data, self.arbitration_entries, self.emoji_map(guild_id))
            if embeds:
                try:
                    await self._update_message(guild_id, key, embeds)
                except Exception as exc:  # noqa: BLE001 - one broken channel must not block the rest
                    failures.append(f"{key}: {exc}")
        try:
            await self._send_new_pings(guild_id, data)
        except Exception as exc:  # noqa: BLE001 - preserve board updates even when notifications fail
            failures.append(f"notifications: {exc}")
        self._save()
        if failures:
            raise RuntimeError(f"{len(failures)} world-feed update(s) failed: {failures[0][:350]}")

    async def _send_new_pings(self, guild_id: int, data: dict) -> None:
        cfg = self._guild(guild_id)
        old = cfg.get("signatures")
        new = notification_signatures(data, self.arbitration_entries)
        if old is None:
            cfg["signatures"] = new
            return  # setup/restart baseline: never blast every role at once
        guild = self.bot.get_guild(guild_id)
        if guild is None:
            return
        committed = {key: list(value or []) for key, value in old.items()}
        failures = []
        by_channel: dict[str, list[tuple[str, discord.Role]]] = {}
        for role_key, new_ids in new.items():
            if role_key not in old:
                # New role/signature introduced by an update: establish its
                # baseline now instead of pinging for every already-active item.
                committed[role_key] = list(new_ids)
                continue
            previous = set(old.get(role_key) or [])
            additions = set(new_ids) - previous
            if not additions:
                committed[role_key] = list(new_ids)
                continue
            role_id = (cfg.get("roles") or {}).get(role_key)
            role = guild.get_role(role_id or 0)
            channel_key = ROLE_CHANNEL[role_key]
            if role is None:
                failures.append(f"{role_key}: role is missing; run /world setup")
                continue
            by_channel.setdefault(channel_key, []).append((role_key, role))

        for channel_key, triggered in by_channel.items():
            channel_cfg = (cfg.get("channels") or {}).get(channel_key) or {}
            channel = await self._channel(channel_cfg.get("channel_id"))
            if not isinstance(channel, discord.TextChannel):
                failures.append(f"{channel_key}: channel is missing; run /world setup")
                continue
            mentions = " ".join(dict.fromkeys(role.mention for _key, role in triggered))
            notices = "\n".join(dict.fromkeys(
                f"• {self.role_emoji(guild_id, key)} {PING_TEXT[key]}" for key, _role in triggered
            ))
            try:
                await self._replace_channel_ping(cfg, channel_key, channel, f"{mentions}\n{notices}")
            except Exception as exc:  # noqa: BLE001 - other channel pings should still be attempted
                failures.append(f"{channel_key}: {exc}")
                continue
            for role_key, _role in triggered:
                committed[role_key] = list(new.get(role_key) or [])
        cfg["signatures"] = committed
        if failures:
            raise RuntimeError(f"Could not send {len(failures)} notification channel(s): {failures[0][:300]}")

    async def _replace_channel_ping(
        self, cfg: dict, channel_key: str, channel: discord.TextChannel, content: str,
    ) -> None:
        """Keep one current bot notification in each feed channel and resend fresh mentions."""
        ping_messages = cfg.setdefault("ping_messages", {})
        previous = None
        previous_id = ping_messages.get(channel_key)
        if previous_id is None and channel_key == "world-fissures":
            previous_id = ping_messages.pop("fissures", None)  # migrate the original one-channel fix
        if previous_id:
            try:
                previous = await channel.fetch_message(previous_id)
            except (discord.HTTPException, TypeError):
                previous = None

        if previous is not None:
            try:
                await previous.delete()
            except discord.HTTPException:
                pass

        # Also remove duplicate pings created before per-channel message IDs
        # existed. Persistent board messages have no mention content and are
        # never touched; neither are user posts.
        try:
            async for message in channel.history(limit=100):
                if (
                    self.bot.user is not None
                    and message.author.id == self.bot.user.id
                    and "<@&" in (message.content or "")
                    and any(text in message.content for text in PING_TEXT.values())
                    and (previous is None or message.id != previous.id)
                ):
                    try:
                        await message.delete()
                    except discord.HTTPException:
                        pass
        except discord.HTTPException:
            pass

        sent = await channel.send(
            content,
            allowed_mentions=discord.AllowedMentions(everyone=False, users=False, roles=True),
        )
        ping_messages[channel_key] = sent.id

    async def setup_command(self, interaction: discord.Interaction) -> None:
        if interaction.guild is None:
            await interaction.response.send_message("Use this command inside a server.", ephemeral=True)
            return
        perms = interaction.user.guild_permissions
        if not perms.manage_channels or not perms.manage_roles:
            await interaction.response.send_message(
                "You need **Manage Channels** and **Manage Roles** to set up the live feed.", ephemeral=True,
            )
            return
        await interaction.response.defer(ephemeral=True)
        try:
            result = await self.setup_guild(interaction.guild)
            await interaction.followup.send(result, ephemeral=True)
        except discord.Forbidden:
            await interaction.followup.send(
                "I need **Manage Channels**, **Manage Roles**, **Send Messages**, and **Embed Links**. "
                "My bot role must also be above the roles I manage.", ephemeral=True,
            )

    async def refresh_command(self, interaction: discord.Interaction) -> None:
        if interaction.guild is None or str(interaction.guild.id) not in self.data.get("guilds", {}):
            await interaction.response.send_message("An admin needs to run `/world setup` first.", ephemeral=True)
            return
        await interaction.response.defer(ephemeral=True)
        try:
            await self.refresh_all(interaction.guild.id)
            await interaction.followup.send("✅ Live world-state channels refreshed.", ephemeral=True)
        except Exception as exc:  # noqa: BLE001
            await interaction.followup.send(f"❌ World-state refresh failed: {exc}", ephemeral=True)

    def status_embed(self) -> discord.Embed:
        if self.last_success is None:
            desc = "No successful world-state update yet."
        else:
            desc = f"Last successful update: {_ts(self.last_success)}\nPolling interval: **{POLL_SECONDS} seconds**"
        if self.last_error:
            desc += f"\nLast error: `{self.last_error[:500]}`"
        if self.latest:
            source = "Official Warframe fallback" if self.latest.get("mission_source") == "official" else "WarframeStat.us"
            desc += f"\nMission source: **{source}**"
            age = self.latest.get("warframestat_age_seconds")
            if isinstance(age, (int, float)) and age > 600:
                desc += f" · parsed snapshot delayed by {int(age // 60)}m"
            if self.latest.get("mission_data_stale"):
                desc += "\n⚠️ Both mission sources are delayed; retrying automatically."
        desc += f"\nArbitration schedule: **{len(self.arbitration_entries):,} hourly entries**"
        return _embed("🌐 Live world-state status", desc, 0x2ECC71 if not self.last_error else 0xE74C3C)
