"""Free, local-first Riven pricing and resale analysis.

The service deliberately keeps two very different signals separate:

* Digital Extremes' weekly feed contains aggregate prices from completed
  in-game Riven trades.  It is the best available source for a weapon's real
  baseline, but it does not include the stats on the sold Rivens.
* Warframe.market auctions contain exact rolls and current asking prices.
  They are useful comparables, but an asking price is not proof of a sale.

Official weekly snapshots are archived within a 32 MiB disk budget. DE only exposes the
latest week at the public URL, so the bot never claims that one fresh download
is years of history; history accumulates honestly as snapshots are collected
or imported later.
"""

from __future__ import annotations

import asyncio
import ast
import json
import math
import re
import statistics
import time
from urllib.parse import quote
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Iterable, Sequence

import aiohttp

from rate_limiter import AsyncRateLimiter
from riven_roll_rules import evaluate_curated_roll
from auction_pool import AuctionPool
from workload import heavy_operation


OFFICIAL_WEEKLY_URLS = {
    "pc": "https://www-static.warframe.com/repos/weeklyRivensPC.json",
    "ps4": "https://www-static.warframe.com/repos/weeklyRivensPS4.json",
    "xbox": "https://www-static.warframe.com/repos/weeklyRivensXB1.json",
    "switch": "https://www-static.warframe.com/repos/weeklyRivensSWI.json",
}
WFM_V2 = "https://api.warframe.market/v2"
WFM_V1 = "https://api.warframe.market/v1"
HEADERS = {
    "User-Agent": "RelicFrame-Bot/1.0 (riven-market; public-data)",
    "Accept": "application/json",
    "Platform": "pc",
    "Crossplay": "true",
    "Language": "en",
}
CACHE_MAX_AGE_SECONDS = 6 * 60 * 60
AUCTION_CACHE_SECONDS = 10 * 60
HISTORY_BUDGET_BYTES = 32 * 1024 * 1024
FLIP_INDEX_INTERVAL_SECONDS = 15 * 60
FLIP_INDEX_MAX_AGE_SECONDS = 30 * 60
FLIP_INDEX_VERSION = 2
GUN_CSV_HEADER = "Name,Trigger,AttackName,Impact,Puncture,Slash,"
MELEE_CSV_HEADER = "Name,AttackName,Impact,Puncture,Slash,"
WEAPON_SLOTS = {
    "Primary", "Secondary", "Robotic", "Archgun", "Archgun (Atmosphere)",
    "Amp", "Railjack Turret", "Railjack Ordnance", "Melee",
}

# Max-rank base values from the supplied Riven reference.  A displayed stat is
# base * the selected weapon variant's disposition * its roll-layout weight *
# a random 0.90-1.10 quality factor.  Keeping this table local makes the
# calculation deterministic and free; live APIs are still used only for price
# evidence.
RIVEN_STAT_BASES: dict[str, dict[str, float]] = {
    "chance_to_gain_extra_combo_count": {"melee": 58.77},
    "chance_to_gain_combo_count": {"melee": 58.77},
    "ammo_maximum": {"rifle": 49.95, "shotgun": 90.0, "pistol": 90.0, "archgun": 99.9},
    "damage_vs_corpus": {kind: 45.0 for kind in ("rifle", "shotgun", "pistol", "archgun", "melee")},
    "damage_vs_grineer": {kind: 45.0 for kind in ("rifle", "shotgun", "pistol", "archgun", "melee")},
    "damage_vs_infested": {kind: 45.0 for kind in ("rifle", "shotgun", "pistol", "archgun", "melee")},
    "cold_damage": {"rifle": 90.0, "shotgun": 90.0, "pistol": 90.0, "archgun": 119.7, "melee": 90.0},
    "combo_duration": {"melee": 8.1},
    "critical_chance": {"rifle": 149.99, "shotgun": 90.0, "pistol": 149.99, "archgun": 99.9, "melee": 180.0},
    "critical_chance_on_slide_attack": {"melee": 120.0},
    "critical_damage": {"rifle": 120.0, "shotgun": 90.0, "pistol": 90.0, "archgun": 80.1, "melee": 90.0},
    "base_damage_/_melee_damage": {"rifle": 165.0, "shotgun": 164.7, "pistol": 219.6, "archgun": 99.9, "melee": 164.7},
    "electric_damage": {"rifle": 90.0, "shotgun": 90.0, "pistol": 90.0, "archgun": 119.7, "melee": 90.0},
    "heat_damage": {"rifle": 90.0, "shotgun": 90.0, "pistol": 90.0, "archgun": 119.7, "melee": 90.0},
    "finisher_damage": {"melee": 119.7},
    "fire_rate_/_attack_speed": {"rifle": 60.03, "shotgun": 90.0, "pistol": 74.7, "archgun": 60.03, "melee": 54.9},
    "projectile_speed": {"rifle": 90.0, "shotgun": 90.0, "pistol": 90.0},
    "channeling_damage": {"melee": 24.5},
    "impact_damage": {"rifle": 119.97, "shotgun": 119.97, "pistol": 119.97, "archgun": 90.0, "melee": 119.7},
    "magazine_capacity": {"rifle": 50.0, "shotgun": 50.0, "pistol": 50.0, "archgun": 60.3},
    "channeling_efficiency": {"melee": 73.44},
    "multishot": {"rifle": 90.0, "shotgun": 119.7, "pistol": 119.7, "archgun": 60.3},
    "toxin_damage": {"rifle": 90.0, "shotgun": 90.0, "pistol": 90.0, "archgun": 119.7, "melee": 90.0},
    "punch_through": {"rifle": 2.7, "shotgun": 2.7, "pistol": 2.7, "archgun": 2.7},
    "puncture_damage": {"rifle": 119.97, "shotgun": 119.97, "pistol": 119.97, "archgun": 90.0, "melee": 119.7},
    "reload_speed": {"rifle": 50.0, "shotgun": 50.0, "pistol": 50.0, "archgun": 99.9},
    "range": {"melee": 1.94},
    "slash_damage": {"rifle": 119.97, "shotgun": 119.97, "pistol": 119.97, "archgun": 90.0, "melee": 119.7},
    "status_chance": {"rifle": 90.0, "shotgun": 90.0, "pistol": 90.0, "archgun": 60.3, "melee": 90.0},
    "status_duration": {kind: 99.99 for kind in ("rifle", "shotgun", "pistol", "archgun", "melee")},
    "recoil": {"rifle": 90.0, "shotgun": 90.0, "pistol": 90.0, "archgun": 90.0},
    "zoom": {"rifle": 59.99, "pistol": 80.1, "archgun": 59.99},
}
RIVEN_POSITIVE_ONLY = {"cold_damage", "electric_damage", "heat_damage", "toxin_damage", "punch_through", "chance_to_gain_extra_combo_count"}
RIVEN_NEGATIVE_ONLY = {"chance_to_gain_combo_count"}
RIVEN_STAT_UNITS = {
    "combo_duration": "seconds",
    "channeling_damage": "flat",
    "punch_through": "meters",
    "range": "meters",
}
RIVEN_LAYOUT_WEIGHTS = {
    (2, False): (0.99, 0.0),
    (2, True): (1.2375, 0.495),
    (3, False): (0.75, 0.0),
    (3, True): (0.9375, 0.75),
}


def _key(value: object) -> str:
    return re.sub(r"[^a-z0-9]+", "", str(value or "").casefold())


def parse_public_payload(text: str) -> dict | list:
    """Parse normal JSON or DE's current JavaScript object-literal feed.

    The weekly endpoint is named ``.json`` but currently uses unquoted keys
    and single-quoted strings.  ``ast.literal_eval`` is safe for literal data
    (it cannot run calls or attribute access), unlike eval.  A tiny scanner
    translates JavaScript's three primitive names only when outside strings.
    """
    try:
        value = json.loads(text)
    except json.JSONDecodeError:
        quoted_keys = re.sub(
            r"([\{,]\s*)([A-Za-z_$][A-Za-z0-9_$]*)(\s*:)",
            r"\1'\2'\3",
            text,
        )
        output: list[str] = []
        index = 0
        quote: str | None = None
        escaped = False
        translations = {"null": "None", "true": "True", "false": "False"}
        while index < len(quoted_keys):
            char = quoted_keys[index]
            if quote:
                output.append(char)
                if escaped:
                    escaped = False
                elif char == "\\":
                    escaped = True
                elif char == quote:
                    quote = None
                index += 1
                continue
            if char in {"'", '"'}:
                quote = char
                output.append(char)
                index += 1
                continue
            matched = False
            for original, replacement in translations.items():
                if quoted_keys.startswith(original, index):
                    before = quoted_keys[index - 1] if index else " "
                    after_index = index + len(original)
                    after = quoted_keys[after_index] if after_index < len(quoted_keys) else " "
                    if not (before.isalnum() or before in "_$") and not (after.isalnum() or after in "_$"):
                        output.append(replacement)
                        index = after_index
                        matched = True
                        break
            if not matched:
                output.append(char)
                index += 1
        try:
            value = ast.literal_eval("".join(output))
        except (SyntaxError, ValueError) as exc:
            raise ValueError(f"Public Riven feed was not valid JSON or a safe object literal: {exc}") from exc
    if not isinstance(value, (dict, list)):
        raise ValueError("Public Riven payload must be an object or array")
    return value


def parse_weapon_variants(text: str) -> list[dict]:
    """Extract the real CSV weapon rows embedded in ``ALL weapons.txt``.

    The export includes prose, example JavaScript, repeated attack modes, and
    unquoted HTML containing commas.  The columns we need are at the stable
    right-hand edge, so parsing from the tail avoids confusing commas inside
    the ForcedProcs HTML.  Strict slot/internal-name checks prevent prose or
    sample scripts from becoming fake weapons.
    """
    mode: str | None = None
    variants: dict[str, dict] = {}
    for raw_line in text.splitlines():
        line = raw_line.strip().lstrip("\ufeff")
        if line.startswith(GUN_CSV_HEADER):
            mode = "gun"
            continue
        if line.startswith(MELEE_CSV_HEADER) and not line.startswith(GUN_CSV_HEADER):
            mode = "melee"
            continue
        if not mode or not line or "," not in line:
            continue
        columns = line.split(",")
        try:
            if mode == "gun":
                if len(columns) < 62:
                    continue
                disposition_text = columns[-26].strip()
                mastery_text = columns[-25].strip()
                slot = columns[-13].strip()
                weapon_class = columns[-12].strip()
                internal_name = columns[-9].strip()
                family = columns[-8].strip()
            else:
                if len(columns) < 35:
                    continue
                disposition_text = columns[-10].strip()
                mastery_text = columns[-9].strip()
                slot = columns[-6].strip()
                weapon_class = columns[-5].strip()
                internal_name = columns[-2].strip()
                family = columns[-1].strip()
            disposition = float(disposition_text)
            mastery = int(float(mastery_text))
        except (ValueError, IndexError):
            continue
        name = columns[0].strip()
        if slot not in WEAPON_SLOTS or not internal_name.startswith("/Lotus/") or not name or not family:
            continue
        marker = _key(name)
        if marker not in variants:
            variants[marker] = {
                "name": name,
                "family": family,
                "disposition": disposition,
                "mastery": mastery,
                "slot": slot,
                "class": weapon_class,
                "internal_name": internal_name,
            }
    return sorted(variants.values(), key=lambda item: item["name"].casefold())


def _price(auction: dict) -> int | None:
    if auction.get("closed") or not auction.get("visible", True):
        return None
    raw = auction.get("buyout_price")
    if raw in (None, 0):
        if not auction.get("is_direct_sell", auction.get("is_direct_sale", False)):
            return None
        raw = auction.get("starting_price")
    try:
        value = int(raw)
    except (TypeError, ValueError):
        return None
    return value if value > 0 else None


def auction_price(auction: dict) -> int | None:
    """Public read-only accessor used by the Discord presentation layer."""
    return _price(auction)


def display_stat(slug: str) -> str:
    return slug.replace("_/_", " / ").replace("_", " ").title()


def _auction_stats(auction: dict) -> tuple[frozenset[str], frozenset[str]]:
    positives: set[str] = set()
    negatives: set[str] = set()
    for attribute in (auction.get("item") or {}).get("attributes") or []:
        slug = str(attribute.get("url_name") or attribute.get("slug") or "").casefold().strip()
        if not slug:
            continue
        (positives if attribute.get("positive", attribute.get("postive", True)) else negatives).add(slug)
    return frozenset(positives), frozenset(negatives)


def _auction_roll_values(
    auction: dict,
) -> tuple[tuple[tuple[str, float | None], ...], tuple[tuple[str, float | None], ...]]:
    positives: list[tuple[str, float | None]] = []
    negatives: list[tuple[str, float | None]] = []
    for attribute in (auction.get("item") or {}).get("attributes") or []:
        slug = str(attribute.get("url_name") or attribute.get("slug") or "").casefold().strip()
        if not slug:
            continue
        try:
            value: float | None = float(attribute.get("value"))
        except (TypeError, ValueError):
            value = None
        target = positives if attribute.get("positive", attribute.get("postive", True)) else negatives
        target.append((slug, value))
    return tuple(positives), tuple(negatives)


def format_roll_stat(slug: str, value: float | None, *, positive: bool) -> str:
    """Format the exact value returned for a live auction attribute."""
    if value is None:
        return f"{'+' if positive else '−'}{display_stat(slug)}"
    unit = RIVEN_STAT_UNITS.get(slug, "percent")
    suffix = {"percent": "%", "seconds": "s", "meters": "m", "flat": ""}.get(unit, "%")
    sign = "+" if value >= 0 else "−"
    magnitude = abs(value)
    number = f"{magnitude:.1f}" if magnitude >= 10 else f"{magnitude:.2f}"
    number = number.rstrip("0").rstrip(".")
    return f"{sign}{number}{suffix} {display_stat(slug)}"


def _weighted_quantile(values: Sequence[tuple[float, float]], quantile: float) -> float:
    if not values:
        raise ValueError("weighted quantile needs at least one value")
    ordered = sorted((float(value), max(0.0, float(weight))) for value, weight in values)
    total = sum(weight for _, weight in ordered)
    if total <= 0:
        return statistics.median(value for value, _ in ordered)
    threshold = min(1.0, max(0.0, quantile)) * total
    running = 0.0
    for value, weight in ordered:
        running += weight
        if running >= threshold:
            return value
    return ordered[-1][0]


def _weekly_resale_ceiling(weekly: "WeeklyPrice | None") -> float | None:
    """Return a conservative ceiling backed by completed-trade evidence.

    Auction asks can contain placeholders such as 888,888 platinum.  A deal
    therefore needs comparable asks that are still plausible beside the
    official completed-trade range for that weapon.
    """
    if weekly is None:
        return None
    maximum = max(0.0, float(weekly.maximum))
    median = max(0.0, float(weekly.median))
    average = max(0.0, float(weekly.average))
    robust_limit = max(median * 8.0, average * 4.0)
    candidates = [value for value in (maximum, robust_limit) if value > 0]
    return min(candidates) if candidates else None


@dataclass(frozen=True)
class RivenStatRange:
    slug: str
    positive: bool
    minimum: float
    maximum: float
    unit: str


def riven_stat_class(weapon: dict) -> str:
    """Return the base-value column used by the selected weapon."""
    group = str(weapon.get("group") or "").casefold()
    riven_type = str(weapon.get("rivenType") or "").casefold()
    slot = str(weapon.get("variant_slot") or "").casefold()
    weapon_class = str(weapon.get("variant_class") or "").casefold()
    if group == "archgun" or "archgun" in slot:
        return "archgun"
    if riven_type in {"melee", "zaw"} or group in {"melee", "zaw"}:
        return "melee"
    if riven_type == "shotgun" or "shotgun" in weapon_class:
        return "shotgun"
    if riven_type == "pistol" or slot == "secondary":
        return "pistol"
    if riven_type == "kitgun":
        # A bare chamber lookup is ambiguous; marketplace auctions use the
        # secondary/pistol values. A named primary variant, when supplied by
        # the local catalog, is routed through its Primary slot above/below.
        return "rifle" if slot == "primary" else "pistol"
    return "rifle"


def disposition_band(value: float) -> str:
    if value >= 1.31:
        return "●●●●● Strong"
    if value >= 1.11:
        return "●●●●○ Above average"
    if value >= 0.90:
        return "●●●○○ Neutral"
    if value >= 0.70:
        return "●●○○○ Below average"
    return "●○○○○ Faint"


def calculate_riven_stat_ranges(
    *,
    slugs: Sequence[str],
    negative: str | None,
    stat_class: str,
    disposition: float,
) -> tuple[RivenStatRange, ...]:
    """Calculate legal 90%-110% max-rank values for one Riven layout."""
    positive_count = len(slugs)
    layout = RIVEN_LAYOUT_WEIGHTS.get((positive_count, negative is not None))
    if layout is None:
        raise ValueError("Rivens must have two or three positive stats and at most one negative stat.")
    bonus_weight, malus_weight = layout
    result: list[RivenStatRange] = []

    def one(slug: str, positive: bool) -> RivenStatRange:
        if positive and slug in RIVEN_NEGATIVE_ONLY:
            raise ValueError(f"{display_stat(slug)} can only appear as a negative stat.")
        if not positive and slug in RIVEN_POSITIVE_ONLY:
            raise ValueError(f"{display_stat(slug)} cannot roll as a negative stat.")
        base = RIVEN_STAT_BASES.get(slug, {}).get(stat_class)
        if base is None:
            raise ValueError(f"{display_stat(slug)} cannot roll on a {stat_class.title()} Riven.")
        weight = bonus_weight if positive else malus_weight
        low_magnitude = base * disposition * weight * 0.90
        high_magnitude = base * disposition * weight * 1.10
        if positive:
            minimum, maximum = low_magnitude, high_magnitude
        else:
            minimum, maximum = -high_magnitude, -low_magnitude
        return RivenStatRange(
            slug=slug,
            positive=positive,
            minimum=minimum,
            maximum=maximum,
            unit=RIVEN_STAT_UNITS.get(slug, "percent"),
        )

    result.extend(one(slug, True) for slug in slugs)
    if negative:
        result.append(one(negative, False))
    return tuple(result)


def format_stat_range(value: RivenStatRange) -> str:
    suffix = {"percent": "%", "seconds": "s", "meters": "m", "flat": ""}[value.unit]

    def number(raw: float) -> str:
        precision = 1 if abs(raw) >= 10 else 2
        return f"{raw:.{precision}f}{suffix}"

    return f"{number(value.minimum)} to {number(value.maximum)}"


@dataclass(frozen=True)
class WeeklyPrice:
    weapon: str
    riven_type: str
    rerolled: bool
    average: float
    median: float
    minimum: float
    maximum: float
    standard_deviation: float
    popularity: float


@dataclass(frozen=True)
class RivenDeal:
    auction_id: str
    weapon_slug: str
    price: int
    peer_value: int
    discount_pct: float
    potential_margin: int
    positives: tuple[str, ...]
    negatives: tuple[str, ...]
    positive_rolls: tuple[tuple[str, float | None], ...]
    negative_rolls: tuple[tuple[str, float | None], ...]
    seller: str
    seller_slug: str
    seller_status: str
    comparable_count: int
    weapon_name: str = ""
    projected_resale: int = 0
    roi_pct: float = 0.0
    weekly_median: float | None = None
    weekly_popularity: float | None = None
    roll_quality_pct: float | None = None
    curated_match: bool = False
    curated_desired_count: int | None = None
    curated_positive_count: int | None = None
    curated_profile: str = ""
    curated_notes: str = ""

    @property
    def url(self) -> str:
        return f"https://warframe.market/auction/{self.auction_id}"

    @property
    def seller_profile_url(self) -> str:
        return f"https://warframe.market/profile/{quote(self.seller_slug or self.seller)}"

    @property
    def ingame_whisper(self) -> str:
        weapon = self.weapon_name or display_stat(self.weapon_slug)
        return f"/w {self.seller} Hi! I'd like to buy your {weapon} Riven listed for {self.price:,} platinum."


def auction_roll_quality(auction: dict, *, stat_class: str, disposition: float) -> float | None:
    """Return the listed roll's 0-100 RNG quality, not a platinum appraisal.

    This uses the supplied base-value/formula data only to distinguish a weak
    numerical roll from a near-maximum roll with the same stat names. It is
    intentionally not a judgement about whether those stats suit a build.
    """
    item = auction.get("item") or {}
    try:
        if int(item.get("mod_rank", 8)) != 8:
            return None
    except (TypeError, ValueError):
        return None
    attributes = item.get("attributes") or []
    positives: list[str] = []
    negative: str | None = None
    actual: dict[tuple[str, bool], float] = {}
    for attribute in attributes:
        slug = str(attribute.get("url_name") or attribute.get("slug") or "").casefold().strip()
        if not slug:
            continue
        positive = bool(attribute.get("positive", attribute.get("postive", True)))
        if positive:
            positives.append(slug)
        else:
            negative = slug
        try:
            actual[(slug, positive)] = abs(float(attribute.get("value")))
        except (TypeError, ValueError):
            return None
    try:
        ranges = calculate_riven_stat_ranges(
            slugs=positives,
            negative=negative,
            stat_class=stat_class,
            disposition=disposition,
        )
    except ValueError:
        return None
    scores: list[float] = []
    for value_range in ranges:
        observed = actual.get((value_range.slug, value_range.positive))
        if observed is None:
            continue
        low = min(abs(value_range.minimum), abs(value_range.maximum))
        high = max(abs(value_range.minimum), abs(value_range.maximum))
        if high <= low:
            continue
        scores.append(min(1.0, max(0.0, (observed - low) / (high - low))))
    return round(statistics.mean(scores) * 100.0, 1) if scores else None


def parse_weekly(rows: Iterable[dict], weapon: str, rerolled: bool) -> WeeklyPrice | None:
    wanted = _key(weapon)
    matches = [
        row for row in rows
        if _key(row.get("compatibility")) == wanted and bool(row.get("rerolled")) == bool(rerolled)
    ]
    if not matches:
        return None
    row = matches[0]

    def number(name: str, fallback: float = 0.0) -> float:
        try:
            return float(row.get(name, fallback))
        except (TypeError, ValueError):
            return fallback

    return WeeklyPrice(
        weapon=str(row.get("compatibility") or weapon),
        riven_type=str(row.get("itemType") or "Riven Mod"),
        rerolled=bool(row.get("rerolled")),
        average=number("avg"),
        median=number("median", number("avg")),
        minimum=number("min"),
        maximum=number("max"),
        standard_deviation=number("stddev"),
        popularity=number("pop"),
    )


def top_weekly_weapons(rows: Iterable[dict], sort_by: str = "popularity", limit: int = 10) -> list[WeeklyPrice]:
    parsed: list[WeeklyPrice] = []
    seen: set[tuple[str, bool]] = set()
    for row in rows:
        compatibility = row.get("compatibility")
        if not compatibility:
            continue
        marker = (_key(compatibility), bool(row.get("rerolled")))
        if marker in seen:
            continue
        seen.add(marker)
        item = parse_weekly([row], str(compatibility), bool(row.get("rerolled")))
        if item:
            parsed.append(item)
    field = {
        "popularity": lambda item: item.popularity,
        "median": lambda item: item.median,
        "average": lambda item: item.average,
        "maximum": lambda item: item.maximum,
    }.get(sort_by, lambda item: item.popularity)
    return sorted(parsed, key=field, reverse=True)[: max(1, limit)]


def _similarity(
    target_pos: frozenset[str], target_neg: frozenset[str],
    other_pos: frozenset[str], other_neg: frozenset[str],
) -> tuple[float, bool]:
    union = target_pos | other_pos
    positive_match = len(target_pos & other_pos) / len(union) if union else 1.0
    same_count = len(target_pos) == len(other_pos)
    negative_match = target_neg == other_neg
    score = positive_match
    if same_count:
        score += 0.08
    if negative_match:
        score += 0.16
    elif target_neg and other_neg:
        score += 0.04
    exact = target_pos == other_pos and target_neg == other_neg
    return min(score, 1.25), exact


def find_riven_deals(
    auctions: Iterable[dict],
    *,
    weapon_slug: str,
    weapon_name: str = "",
    weekly: WeeklyPrice | None = None,
    stat_class: str | None = None,
    disposition: float | None = None,
    minimum_discount_pct: float = 20.0,
    maximum_price: int | None = None,
    online_only: bool = True,
    limit: int = 8,
    roll_rule: dict | None = None,
    curated_only: bool = False,
) -> list[RivenDeal]:
    resale_ceiling = _weekly_resale_ceiling(weekly)
    active: list[tuple[dict, int, frozenset[str], frozenset[str], float | None, object | None]] = []
    for auction in auctions:
        value = _price(auction)
        owner = auction.get("owner") or {}
        status = str(owner.get("status") or "offline").casefold()
        if value is None or (maximum_price is not None and value > maximum_price):
            continue
        if online_only and status not in {"online", "ingame"}:
            continue
        positive, negative = _auction_stats(auction)
        if not positive:
            continue
        quality = (
            auction_roll_quality(auction, stat_class=stat_class, disposition=disposition)
            if stat_class and disposition is not None else None
        )
        curated = evaluate_curated_roll(positive, negative, roll_rule)
        active.append((auction, value, positive, negative, quality, curated))

    deals: list[RivenDeal] = []
    for index, (auction, value, positive, negative, quality, curated) in enumerate(active):
        if curated_only and not (curated and curated.qualifies):
            continue
        if resale_ceiling is not None and value > resale_ceiling:
            continue
        peers: list[tuple[float, float]] = []
        for other_index, (_other, other_value, other_positive, other_negative, other_quality, _curated) in enumerate(active):
            if index == other_index:
                continue
            score, exact = _similarity(positive, negative, other_positive, other_negative)
            if score < 0.58:
                continue
            if resale_ceiling is not None and other_value > resale_ceiling:
                continue
            quality_weight = 1.0
            if quality is not None and other_quality is not None:
                difference = abs(quality - other_quality)
                if difference > 45.0:
                    continue
                quality_weight = max(0.35, 1.0 - difference / 100.0)
            peers.append((other_value, score**3 * (2.0 if exact else 1.0) * quality_weight))
        if len(peers) < 3:
            continue
        peer_value = round(_weighted_quantile(peers, 0.50))
        if peer_value <= value:
            continue
        discount = (peer_value - value) / peer_value * 100.0
        if discount < minimum_discount_pct:
            continue
        # A flip needs room for negotiation and undercutting. Treat 90% of the
        # peer median as the resale target instead of promising the full ask.
        projected_resale = max(1, round(peer_value * 0.90))
        if projected_resale <= value:
            continue
        potential_margin = projected_resale - value
        owner = auction.get("owner") or {}
        positive_rolls, negative_rolls = _auction_roll_values(auction)
        deals.append(RivenDeal(
            auction_id=str(auction.get("id") or ""),
            weapon_slug=weapon_slug,
            price=value,
            peer_value=peer_value,
            discount_pct=discount,
            potential_margin=potential_margin,
            positives=tuple(sorted(positive)),
            negatives=tuple(sorted(negative)),
            positive_rolls=positive_rolls,
            negative_rolls=negative_rolls,
            seller=str(owner.get("ingame_name") or owner.get("slug") or "Unknown"),
            seller_slug=str(owner.get("slug") or owner.get("ingame_name") or "Unknown"),
            seller_status=str(owner.get("status") or "offline"),
            comparable_count=len(peers),
            weapon_name=weapon_name,
            projected_resale=projected_resale,
            roi_pct=potential_margin / value * 100.0,
            weekly_median=weekly.median if weekly else None,
            weekly_popularity=weekly.popularity if weekly else None,
            roll_quality_pct=quality,
            curated_match=bool(curated and curated.qualifies),
            curated_desired_count=curated.desired_count if curated else None,
            curated_positive_count=curated.positive_count if curated else None,
            curated_profile=str((roll_rule or {}).get("positive_expression") or ""),
            curated_notes=str((roll_rule or {}).get("notes") or ""),
        ))
    return sorted(
        deals,
        key=lambda item: (
            item.curated_match,
            item.curated_desired_count or 0,
            item.potential_margin * (0.5 + (item.weekly_popularity or 0.0) / 200.0),
            item.roi_pct,
        ),
        reverse=True,
    )[:limit]


class RivenMarketService:
    def __init__(
        self,
        data_dir: Path | str | None = None,
        platform: str = "pc",
        weapon_source: Path | str | None = None,
    ):
        using_default_data = data_dir is None
        self.data_dir = Path(data_dir or Path(__file__).resolve().parent / "data" / "rivens")
        self.weapon_source = Path(weapon_source) if weapon_source else (
            Path(__file__).resolve().parent.parent / "ALL weapons.txt" if using_default_data else None
        )
        self.platform = platform if platform in OFFICIAL_WEEKLY_URLS else "pc"
        self.weekly_rows: list[dict] = []
        self.weapons: list[dict] = []
        self.variants: list[dict] = []
        self.roll_rules: dict[str, dict] = {}
        self.fetched_at: float = 0.0
        self._auction_cache: dict[str, tuple[float, list[dict]]] = {}
        self._refresh_lock = asyncio.Lock()
        self._flip_scan_lock = asyncio.Lock()
        self._flip_index_lock = asyncio.Lock()
        self._flip_index_task: asyncio.Task | None = None
        self.flip_deals: list[RivenDeal] = []
        self.flip_index_at: float = 0.0
        self.flip_index_scanned: int = 0
        self.flip_index_failures: int = 0
        self.flip_index_error: str = ""
        # This has its own five-request-per-second budget, separate from the
        # relic order-book client, while concurrency remains bounded so slow
        # responses cannot create an unbounded socket backlog.
        self._limiter = AsyncRateLimiter(max_concurrent=4, max_per_second=5.0)
        self._load_cache()
        self._load_weapon_variants()

    @property
    def history_dir(self) -> Path:
        return self.data_dir / "history"

    @property
    def history_snapshot_count(self) -> int:
        return len(list(self.history_dir.glob("*.json"))) if self.history_dir.exists() else 0

    def _load_json(self, path: Path) -> dict | list | None:
        try:
            return json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return None

    def _load_cache(self) -> None:
        latest = self._load_json(self.data_dir / "latest.json")
        if isinstance(latest, dict):
            self.weekly_rows = list(latest.get("rows") or [])
            self.fetched_at = float(latest.get("fetched_at") or 0.0)
        weapons = self._load_json(self.data_dir / "weapons.json")
        if isinstance(weapons, dict):
            self.weapons = list(weapons.get("rows") or [])
        variants = self._load_json(self.data_dir / "weapon_variants.json")
        if isinstance(variants, dict):
            self.variants = list(variants.get("rows") or [])
        roll_rules = self._load_json(self.data_dir / "roll_rules.json")
        if isinstance(roll_rules, dict):
            self.roll_rules = {
                _key(row.get("weapon")): row
                for row in roll_rules.get("rows") or []
                if isinstance(row, dict) and row.get("weapon")
            }
        flip_index = self._load_json(self.data_dir / "flip_index.json")
        if isinstance(flip_index, dict) and flip_index.get("version") == FLIP_INDEX_VERSION:
            loaded: list[RivenDeal] = []
            for row in flip_index.get("deals") or []:
                if not isinstance(row, dict):
                    continue
                try:
                    normalized = dict(row)
                    for name in ("positives", "negatives"):
                        normalized[name] = tuple(normalized.get(name) or ())
                    for name in ("positive_rolls", "negative_rolls"):
                        normalized[name] = tuple(tuple(value) for value in normalized.get(name) or ())
                    loaded.append(RivenDeal(**normalized))
                except (TypeError, ValueError):
                    continue
            self.flip_deals = loaded
            self.flip_index_at = float(flip_index.get("completed_at") or 0.0)
            self.flip_index_scanned = int(flip_index.get("scanned_families") or 0)
            self.flip_index_failures = int(flip_index.get("failed_families") or 0)

    def _load_weapon_variants(self) -> None:
        if self.weapon_source is None or not self.weapon_source.exists():
            return
        try:
            source_mtime = self.weapon_source.stat().st_mtime
        except OSError:
            return
        cache = self._load_json(self.data_dir / "weapon_variants.json")
        cache_mtime = float(cache.get("source_mtime") or 0.0) if isinstance(cache, dict) else 0.0
        if self.variants and cache_mtime >= source_mtime:
            return
        try:
            variants = parse_weapon_variants(self.weapon_source.read_text(encoding="utf-8-sig"))
        except OSError:
            return
        if not variants:
            return
        self.variants = variants
        self._write_json(
            self.data_dir / "weapon_variants.json",
            {
                "source": self.weapon_source.name,
                "source_mtime": source_mtime,
                "generated_at": time.time(),
                "rows": variants,
            },
        )

    def _write_json(self, path: Path, value: object) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        temporary = path.with_suffix(path.suffix + ".tmp")
        temporary.write_text(json.dumps(value, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
        temporary.replace(path)

    async def _get_json(self, session: aiohttp.ClientSession, url: str, params: dict | None = None) -> dict | list:
        last_error: Exception | None = None
        for attempt in range(4):
            try:
                async with self._limiter:
                    async with session.get(url, params=params) as response:
                        if response.status == 429:
                            raw = response.headers.get("Retry-After")
                            try:
                                delay = float(raw) if raw else 2.0 * (attempt + 1)
                            except ValueError:
                                delay = 2.0 * (attempt + 1)
                            await self._limiter.pause(min(max(delay, 1.0), 30.0))
                            last_error = RuntimeError("Riven data source returned 429")
                            continue
                        response.raise_for_status()
                        return parse_public_payload(await response.text())
            except (aiohttp.ClientError, asyncio.TimeoutError) as exc:
                last_error = exc
                if attempt < 3:
                    await asyncio.sleep(min(0.75 * (2**attempt), 4.0))
        raise RuntimeError(f"Riven data request failed: {last_error}")

    async def refresh(self, force: bool = False) -> None:
        if not force and self.weekly_rows and self.weapons and time.time() - self.fetched_at < CACHE_MAX_AGE_SECONDS:
            return
        async with self._refresh_lock:
            if not force and self.weekly_rows and self.weapons and time.time() - self.fetched_at < CACHE_MAX_AGE_SECONDS:
                return
            timeout = aiohttp.ClientTimeout(total=30)
            async with aiohttp.ClientSession(headers=HEADERS, timeout=timeout) as session:
                weekly = await self._get_json(session, OFFICIAL_WEEKLY_URLS[self.platform])
                weapons_envelope = await self._get_json(session, f"{WFM_V2}/riven/weapons")
            if not isinstance(weekly, list):
                raise RuntimeError("DE's weekly Riven feed returned an unexpected format")
            weapons = weapons_envelope.get("data", []) if isinstance(weapons_envelope, dict) else []
            if not isinstance(weapons, list) or not weapons:
                raise RuntimeError("Warframe.market's Riven weapon catalog was empty")

            fetched_at = time.time()
            envelope = {"fetched_at": fetched_at, "platform": self.platform, "rows": weekly}
            day = datetime.now(UTC).date().isoformat()
            self._write_json(self.data_dir / "latest.json", envelope)
            history_path = self.history_dir / f"{day}.json"
            archive_bytes = sum(path.stat().st_size for path in self.history_dir.glob("*.json"))
            old_bytes = history_path.stat().st_size if history_path.exists() else 0
            new_bytes = len(json.dumps(envelope, ensure_ascii=False, indent=2).encode("utf-8"))
            if archive_bytes - old_bytes + new_bytes <= HISTORY_BUDGET_BYTES:
                self._write_json(history_path, envelope)
            else:
                # Preserve existing history. Never fill the disk or delete user evidence.
                print("[riven-history] 32 MiB archive budget reached; new archiving paused. Latest prices still update.")
            self._write_json(self.data_dir / "weapons.json", {"fetched_at": fetched_at, "rows": weapons})
            self.weekly_rows = weekly
            self.weapons = weapons
            self.fetched_at = fetched_at

    async def ensure_fresh(self) -> None:
        await self.refresh(force=False)

    def weapon_name(self, weapon: dict) -> str:
        return str(
            weapon.get("selected_name") or
            ((weapon.get("i18n") or {}).get("en") or {}).get("name") or
            weapon.get("slug") or "Unknown"
        )

    def riven_family_name(self, weapon: dict) -> str:
        return str(
            weapon.get("riven_family") or
            ((weapon.get("i18n") or {}).get("en") or {}).get("name") or
            weapon.get("slug") or "Unknown"
        )

    def roll_rule_for(self, weapon: dict | str) -> dict | None:
        """Return the curated resale-roll profile for a family or variant."""
        if isinstance(weapon, dict):
            candidates = (
                self.riven_family_name(weapon),
                self.weapon_name(weapon),
                str(weapon.get("slug") or ""),
            )
        else:
            candidates = (str(weapon),)
        for candidate in candidates:
            matched = self.roll_rules.get(_key(candidate))
            if matched:
                return matched
        return None

    def _find_market_family(self, query: str) -> dict | None:
        wanted = _key(query)
        exact = [item for item in self.weapons if wanted in {_key(item.get("slug")), _key(self.weapon_name(item))}]
        if exact:
            return exact[0]
        partial = [item for item in self.weapons if wanted and wanted in _key(self.weapon_name(item))]
        return partial[0] if len(partial) == 1 else None

    def find_weapon(self, query: str) -> dict | None:
        wanted = _key(query)
        variant_matches = [item for item in self.variants if wanted == _key(item.get("name"))]
        if not variant_matches:
            variant_matches = [item for item in self.variants if wanted and wanted in _key(item.get("name"))]
            if len(variant_matches) != 1:
                variant_matches = []
        if variant_matches:
            variant = variant_matches[0]
            family = self._find_market_family(str(variant.get("family") or variant.get("name")))
            if family:
                result = dict(family)
                result.update({
                    "selected_name": variant.get("name"),
                    "riven_family": self.weapon_name(family),
                    "variant_disposition": variant.get("disposition"),
                    "variant_mastery": variant.get("mastery"),
                    "variant_slot": variant.get("slot"),
                    "variant_class": variant.get("class"),
                    "variant_internal_name": variant.get("internal_name"),
                    "riven_available": True,
                })
                return result
            return {
                "selected_name": variant.get("name"),
                "riven_family": variant.get("family"),
                "variant_disposition": variant.get("disposition"),
                "variant_mastery": variant.get("mastery"),
                "variant_slot": variant.get("slot"),
                "variant_class": variant.get("class"),
                "variant_internal_name": variant.get("internal_name"),
                "riven_available": False,
            }
        family = self._find_market_family(query)
        if family:
            result = dict(family)
            result["riven_available"] = True
            return result
        return None

    def require_riven_weapon(self, query: str) -> dict:
        weapon = self.find_weapon(query)
        if not weapon:
            raise ValueError(f"I couldn't uniquely match the weapon `{query}`.")
        if not weapon.get("riven_available", bool(weapon.get("slug"))):
            raise ValueError(
                f"**{self.weapon_name(weapon)}** is in the full weapon catalog, but it does not have a tradable Riven family."
            )
        return weapon

    def weapon_suggestions(self, current: str, limit: int = 25) -> list[str]:
        wanted = _key(current)
        # The local full-weapon export supplies variant names while the live
        # marketplace catalog can contain newer families and modular weapons.
        # Combining both prevents either source from hiding valid Rivens from
        # Discord autocomplete.
        names: list[str] = []
        seen: set[str] = set()
        for name in (
            [str(item.get("name")) for item in self.variants if item.get("name")] +
            [self.weapon_name(item) for item in self.weapons]
        ):
            marker = _key(name)
            if marker and marker not in seen:
                names.append(name)
                seen.add(marker)
        starts = [name for name in names if _key(name).startswith(wanted)]
        contains = [name for name in names if wanted in _key(name) and name not in starts]
        return (starts + contains)[:limit]

    async def auctions_for(
        self,
        weapon_slug: str,
        force: bool = False,
        session: aiohttp.ClientSession | None = None,
    ) -> list[dict]:
        cached = self._auction_cache.get(weapon_slug)
        if cached and not force and time.time() - cached[0] < AUCTION_CACHE_SECONDS:
            return cached[1]
        if session is None:
            timeout = aiohttp.ClientTimeout(total=30)
            async with aiohttp.ClientSession(headers=HEADERS, timeout=timeout) as owned_session:
                return await self.auctions_for(weapon_slug, force=force, session=owned_session)
        envelope = await self._get_json(
            session,
            f"{WFM_V1}/auctions/search",
            params={
                "type": "riven",
                "weapon_url_name": weapon_slug,
                "sort_by": "price_asc",
                "buyout_policy": "direct",
            },
        )
        auctions = ((envelope.get("payload") or {}).get("auctions") or []) if isinstance(envelope, dict) else []
        if not isinstance(auctions, list):
            raise RuntimeError("Warframe.market's auction search returned an unexpected format")
        now = time.time()
        for key, (stamp, _) in list(self._auction_cache.items()):
            if now - stamp > AUCTION_CACHE_SECONDS:
                self._auction_cache.pop(key, None)
        self._auction_cache.pop(weapon_slug, None)
        self._auction_cache[weapon_slug] = (now, auctions)
        while len(self._auction_cache) > 4:
            self._auction_cache.pop(next(iter(self._auction_cache)))
        return auctions

    async def auctions_across_market(
        self,
        session: aiohttp.ClientSession,
    ) -> tuple[AuctionPool, int]:
        """Fetch a broad resale pool across all weapons using stat searches.

        Warframe.market caps a search response at 500 rows. Querying every
        possible positive Riven stat at both ends of the price range and
        deduplicating by auction id samples cheap candidates and higher-price
        comparables without one slow request for every weapon family. Every
        catalog family is still evaluated against this pool.
        """
        positive_slugs = sorted(set(RIVEN_STAT_BASES) - RIVEN_NEGATIVE_ONLY)

        async def one(stat_slug: str, sort_by: str) -> tuple[list[dict], bool]:
            try:
                envelope = await self._get_json(
                    session,
                    f"{WFM_V1}/auctions/search",
                    params={
                        "type": "riven",
                        "positive_stats": stat_slug,
                        "sort_by": sort_by,
                        "buyout_policy": "direct",
                    },
                )
                rows = ((envelope.get("payload") or {}).get("auctions") or []) if isinstance(envelope, dict) else []
                return (rows if isinstance(rows, list) else []), False
            except Exception:  # noqa: BLE001 - other stat searches can still complete the pool
                return [], True

        searches = [(slug, sort_by) for slug in positive_slugs for sort_by in ("price_asc", "price_desc")]
        pool = AuctionPool()
        failures = 0
        try:
            for offset in range(0, len(searches), 2):
                batches = await asyncio.gather(*(one(slug, sort_by) for slug, sort_by in searches[offset:offset + 2]))
                for rows, failed in batches:
                    failures += int(failed)
                    pool.add(rows)
                rows = batches = None
            if failures == len(searches):
                raise RuntimeError("Every cross-weapon Riven auction search failed")
            return pool, failures
        except BaseException:
            pool.close()
            raise

    async def scan_flips(
        self,
        *,
        weapon_limit: int | None = None,
        result_limit: int | None = 10,
        maximum_price: int | None = None,
        minimum_discount_pct: float = 25.0,
        online_only: bool = True,
        curated_only: bool = True,
        force_auctions: bool = False,
    ) -> tuple[list[RivenDeal], int]:
        """Scan all Riven families, or a bounded liquid subset, for flips."""
        await self.ensure_fresh()
        weekly_by_weapon: dict[str, WeeklyPrice] = {}
        for row in self.weekly_rows:
            name = str(row.get("compatibility") or "")
            if not name:
                continue
            parsed = parse_weekly([row], name, bool(row.get("rerolled")))
            if not parsed:
                continue
            marker = _key(name)
            existing = weekly_by_weapon.get(marker)
            if existing is None or parsed.popularity > existing.popularity or (
                parsed.popularity == existing.popularity and parsed.rerolled
            ):
                weekly_by_weapon[marker] = parsed

        selected: list[tuple[dict, WeeklyPrice | None]] = [
            (family, weekly_by_weapon.get(_key(self.weapon_name(family))))
            for family in self.weapons
            if family.get("slug")
        ]
        selected.sort(
            key=lambda pair: (
                pair[1].popularity if pair[1] else -1.0,
                self.weapon_name(pair[0]).casefold(),
            ),
            reverse=True,
        )
        if weapon_limit is not None:
            selected = selected[:max(1, weapon_limit)]

        # Decode and score one family at a time; all families are still visited.
        failed_families = 0
        market_auctions = None
        loop = asyncio.get_running_loop()

        async def inspect(
            family: dict,
            weekly: WeeklyPrice | None,
            session: aiohttp.ClientSession,
        ) -> list[RivenDeal]:
            nonlocal failed_families
            slug = str(family.get("slug") or "")
            if not slug:
                return []
            if market_auctions is not None:
                auctions = market_auctions.get(slug, [])
            else:
                try:
                    auctions = await self.auctions_for(slug, force=force_auctions, session=session)
                except Exception:  # noqa: BLE001 - one weapon must not abort the market scan
                    failed_families += 1
                    return []
            try:
                disposition = float(family.get("disposition"))
            except (TypeError, ValueError):
                disposition = None
            # find_riven_deals is pure CPU work (roll-quality scoring across
            # every auction for this weapon) with no side effects, so it's
            # safe on a worker thread. Run hundreds of these synchronously
            # on the event loop during a full-market scan and nothing else
            # (including Discord's own gateway heartbeat) gets a turn for
            # the whole scan - that's what was behind the gateway falling
            # tens of seconds behind.
            return await loop.run_in_executor(
                None,
                lambda: find_riven_deals(
                    auctions,
                    weapon_slug=slug,
                    weapon_name=self.weapon_name(family),
                    weekly=weekly,
                    stat_class=riven_stat_class(family),
                    disposition=disposition,
                    minimum_discount_pct=minimum_discount_pct,
                    maximum_price=maximum_price,
                    online_only=online_only,
                    limit=len(auctions) if result_limit is None else 3,
                    roll_rule=self.roll_rule_for(family),
                    curated_only=curated_only,
                ),
            )

        async with self._flip_scan_lock:
            timeout = aiohttp.ClientTimeout(total=30)
            async with aiohttp.ClientSession(headers=HEADERS, timeout=timeout) as session:
                try:
                    if weapon_limit is None:
                        market_auctions, failed_families = await self.auctions_across_market(session)
                    # Decode/score one family at a time rather than queuing every
                    # family's complete auction list into the executor at once.
                    batches = []
                    for family, weekly in selected:
                        batches.append(await inspect(family, weekly, session))
                finally:
                    if isinstance(market_auctions, AuctionPool):
                        market_auctions.close()
            deals = [deal for batch in batches for deal in batch]
        self.flip_index_failures = failed_families
        deals.sort(
            key=lambda item: (
                item.curated_match,
                item.curated_desired_count or 0,
                item.potential_margin * (0.5 + (item.weekly_popularity or 0.0) / 200.0),
                item.roi_pct,
                item.comparable_count,
            ),
            reverse=True,
        )
        return (deals if result_limit is None else deals[:result_limit]), len(selected)

    async def refresh_flip_index(self, *, force: bool = False) -> tuple[list[RivenDeal], int, float]:
        if not force and self.flip_index_at and time.time() - self.flip_index_at < FLIP_INDEX_MAX_AGE_SECONDS:
            return list(self.flip_deals), self.flip_index_scanned, self.flip_index_at
        async with heavy_operation("riven scan"):
            return await self._refresh_flip_index(force=force)

    async def _refresh_flip_index(self, *, force: bool = False) -> tuple[list[RivenDeal], int, float]:
        """Build and persist a complete curated deal index for every family."""
        if (
            not force
            and self.flip_index_at
            and time.time() - self.flip_index_at < FLIP_INDEX_MAX_AGE_SECONDS
        ):
            return list(self.flip_deals), self.flip_index_scanned, self.flip_index_at
        async with self._flip_index_lock:
            if (
                not force
                and self.flip_index_at
                and time.time() - self.flip_index_at < FLIP_INDEX_MAX_AGE_SECONDS
            ):
                return list(self.flip_deals), self.flip_index_scanned, self.flip_index_at
            deals, scanned = await self.scan_flips(
                weapon_limit=None,
                result_limit=None,
                maximum_price=None,
                minimum_discount_pct=10.0,
                online_only=False,
                curated_only=True,
                force_auctions=force,
            )
            completed_at = time.time()
            self.flip_deals = deals
            self.flip_index_scanned = scanned
            self.flip_index_at = completed_at
            self.flip_index_error = ""
            self._write_json(
                self.data_dir / "flip_index.json",
                {
                    "version": FLIP_INDEX_VERSION,
                    "completed_at": completed_at,
                    "scanned_families": scanned,
                    "failed_families": self.flip_index_failures,
                    "minimum_discount_pct": 10.0,
                    "deals": [asdict(deal) for deal in deals],
                },
            )
            return list(deals), scanned, completed_at

    async def current_flip_index(self) -> tuple[list[RivenDeal], int, float]:
        """Return the latest complete index, refreshing it if absent or stale."""
        return await self.refresh_flip_index(force=False)

    def start_background_index(self, interval_seconds: float = FLIP_INDEX_INTERVAL_SECONDS) -> None:
        if self._flip_index_task and not self._flip_index_task.done():
            return

        async def worker() -> None:
            while True:
                try:
                    await self.refresh_flip_index(force=True)
                except asyncio.CancelledError:
                    raise
                except Exception as exc:  # noqa: BLE001 - keep future scans alive
                    self.flip_index_error = str(exc) or type(exc).__name__
                    print(f"[riven-index] full-market scan failed: {self.flip_index_error}")
                await asyncio.sleep(max(60.0, interval_seconds))

        self._flip_index_task = asyncio.create_task(worker(), name="riven-full-market-index")

    async def close(self) -> None:
        task = self._flip_index_task
        self._flip_index_task = None
        if not task:
            return
        task.cancel()
        await asyncio.gather(task, return_exceptions=True)

def human_age(timestamp: float) -> str:
    if not timestamp:
        return "not downloaded"
    seconds = max(0, int(time.time() - timestamp))
    if seconds < 60:
        return "just now"
    if seconds < 3600:
        return f"{seconds // 60}m ago"
    if seconds < 86400:
        return f"{seconds // 3600}h ago"
    return f"{seconds // 86400}d ago"


def fmt_platinum(value: float) -> str:
    return f"{round(value):,}p" if math.isfinite(value) else "unknown"


async def _smoke_check(weapon_query: str) -> None:
    """Small operator-facing connection check; does not modify Discord."""
    service = RivenMarketService()
    await service.refresh(force=True)
    weapon = service.find_weapon(weapon_query)
    if not weapon:
        raise SystemExit(f"Unknown Riven weapon: {weapon_query}")
    name = service.weapon_name(weapon)
    auctions = await service.auctions_for(str(weapon.get("slug")), force=True)
    print(
        f"Riven data OK: {len(service.weekly_rows):,} weekly rows, "
        f"{len(service.weapons):,} Riven families, {len(service.variants):,} named weapon variants, "
        f"{len(auctions):,} current {name} auctions, "
        f"{service.history_snapshot_count} archived snapshot(s)."
    )


if __name__ == "__main__":
    import argparse

    parser = argparse.ArgumentParser(description="Check the public Riven feeds and archive the current weekly snapshot.")
    parser.add_argument("--weapon", default="Torid", help="Weapon used for the live auction check (default: Torid)")
    arguments = parser.parse_args()
    asyncio.run(_smoke_check(arguments.weapon))
