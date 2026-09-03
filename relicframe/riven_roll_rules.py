"""Curated weapon-roll rules used by the Riven resale scanner.

The source workbook describes desirable positives with compact expressions.
For example, ``CC MS FR/CD/DMG`` means critical chance and multishot are
required, while the remaining positive may be fire rate, critical damage, or
base damage.  A slash-only expression is a pool from which at least two useful
positives must be present.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Iterable


TOKEN_TO_SLUG = {
    "AMMO": "ammo_maximum",
    "AS": "fire_rate_/_attack_speed",
    "CC": "critical_chance",
    "CD": "critical_damage",
    "COLD": "cold_damage",
    "DMG": "base_damage_/_melee_damage",
    "DTC": "damage_vs_corpus",
    "DTG": "damage_vs_grineer",
    "DTI": "damage_vs_infested",
    "EFF": "channeling_efficiency",
    "ELEC": "electric_damage",
    "FIN": "finisher_damage",
    "FR": "fire_rate_/_attack_speed",
    "HEAT": "heat_damage",
    "IC": "channeling_damage",
    "IMP": "impact_damage",
    "MAG": "magazine_capacity",
    "MS": "multishot",
    "PFS": "projectile_speed",
    "PT": "punch_through",
    "PUNC": "puncture_damage",
    "RANGE": "range",
    "REC": "recoil",
    "RECOIL": "recoil",
    "RLS": "reload_speed",
    "SC": "status_chance",
    "SD": "status_duration",
    "SLASH": "slash_damage",
    "SLIDE": "critical_chance_on_slide_attack",
    "TOX": "toxin_damage",
    "ZOOM": "zoom",
}
ELEMENTAL_SLUGS = frozenset({"cold_damage", "electric_damage", "heat_damage", "toxin_damage"})


def _token_options(token: str) -> frozenset[str]:
    clean = token.strip().upper()
    if clean == "ELEMENT":
        return ELEMENTAL_SLUGS
    try:
        return frozenset({TOKEN_TO_SLUG[clean]})
    except KeyError as exc:
        raise ValueError(f"Unknown Riven roll shorthand: {clean or token!r}") from exc


def parse_rule_expression(expression: str) -> tuple[dict, ...]:
    """Parse the workbook's compact positive-stat expression.

    Standalone tokens are mandatory groups. Slash-separated tokens form the
    allowed pool for any remaining positive slots. ``ELEMENT`` is a wildcard
    for the four basic elemental damage stats.
    """
    alternatives: list[dict] = []
    for raw_alternative in re.split(r"\s+or\s+", str(expression or "").strip(), flags=re.IGNORECASE):
        mandatory: list[list[str]] = []
        pool: set[str] = set()
        for raw_group in raw_alternative.split():
            group = raw_group.strip().strip(",;")
            if not group:
                continue
            pieces = [piece for piece in group.split("/") if piece]
            options: set[str] = set()
            for piece in pieces:
                options.update(_token_options(piece))
            if len(pieces) == 1:
                mandatory.append(sorted(options))
            else:
                pool.update(options)
        if mandatory or pool:
            alternatives.append({"mandatory": mandatory, "pool": sorted(pool)})
    if not alternatives:
        raise ValueError("Riven roll expression was empty")
    return tuple(alternatives)


def parse_negative_expression(expression: str) -> tuple[str, ...]:
    """Expand a slash/space-separated harmless-negative list to slugs."""
    slugs: set[str] = set()
    for token in re.findall(r"[A-Za-z]+", str(expression or "")):
        if token.casefold() == "or":
            continue
        slugs.update(_token_options(token))
    return tuple(sorted(slugs))


@dataclass(frozen=True)
class CuratedRollMatch:
    qualifies: bool
    desired_count: int
    positive_count: int
    harmless_negative: bool
    no_dead_positive: bool
    mandatory_matched: bool
    alternative: int | None = None

    @property
    def label(self) -> str:
        if not self.qualifies:
            return "does not match curated roll"
        return f"curated {self.desired_count}/{self.positive_count} positives + harmless negative"


def evaluate_curated_roll(
    positives: Iterable[str],
    negatives: Iterable[str],
    rule: dict | None,
) -> CuratedRollMatch | None:
    """Check a live auction against one weapon's workbook rule.

    The workbook's INFO sheet defines a high-quality roll as two desired
    positives, a harmless negative, and no dead third positive. A weapon's
    standalone shorthand tokens remain mandatory, even when that requires all
    three positives.
    """
    if not rule:
        return None
    positive_set = frozenset(str(value).casefold() for value in positives)
    negative_set = frozenset(str(value).casefold() for value in negatives)
    harmless = frozenset(str(value).casefold() for value in rule.get("harmless_negatives") or ())
    harmless_negative = bool(negative_set and negative_set <= harmless)
    best: CuratedRollMatch | None = None
    for index, alternative in enumerate(rule.get("alternatives") or ()): 
        mandatory_groups = [frozenset(group) for group in alternative.get("mandatory") or ()]
        pool = frozenset(alternative.get("pool") or ())
        desired = pool | frozenset(value for group in mandatory_groups for value in group)
        mandatory_matched = all(bool(group & positive_set) for group in mandatory_groups)
        desired_count = len(positive_set & desired)
        no_dead_positive = bool(positive_set) and positive_set <= desired
        qualifies = (
            desired_count >= 2
            and mandatory_matched
            and no_dead_positive
            and harmless_negative
        )
        candidate = CuratedRollMatch(
            qualifies=qualifies,
            desired_count=desired_count,
            positive_count=len(positive_set),
            harmless_negative=harmless_negative,
            no_dead_positive=no_dead_positive,
            mandatory_matched=mandatory_matched,
            alternative=index,
        )
        if best is None or (
            candidate.qualifies,
            candidate.desired_count,
            candidate.mandatory_matched,
            candidate.no_dead_positive,
        ) > (
            best.qualifies,
            best.desired_count,
            best.mandatory_matched,
            best.no_dead_positive,
        ):
            best = candidate
    return best

