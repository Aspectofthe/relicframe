"""
slug_registry.py
Builds the ONE deduplicated set of every distinct WFM item slug the bot
actually needs to track - every relic itself (tradable as its own item)
plus every unique reward across every relic's drop table.

Why this matters: the same reward item (e.g. a specific Prime part)
routinely appears in more than one relic's drop table, and relic_data's
all_reward_names() already dedupes across relics for that reason - but
the OLD synchronous pipeline still issued one fetch per relic name and one
per reward name independently, with no single merged view of "every
distinct slug we need, fetched exactly once." This module is that merged
view: a slug that happens to serve five different relics' offline pricing
AND appears as a reward in three others is still fetched exactly once by
the live-market layer, never five-plus-three times.
"""

from __future__ import annotations

from relic_data import Relic, all_reward_names
from wfm_api import find_relic_slug


def build_required_slugs(
    relics: dict[str, Relic], name_to_slug: dict[str, str]
) -> dict[str, str]:
    """
    Returns {slug: label} for every unique slug needed - label is a
    human-readable name for logging/debugging (the relic or reward name
    that resolved to this slug; if more than one name maps to the same
    slug, whichever is seen first wins, since the label is cosmetic only
    and never affects which orders get fetched).

    A relic or reward name with no resolvable slug (not found in
    name_to_slug) is simply omitted - same "missing is not an error, just
    excluded" pattern used throughout this project's price handling.
    """
    required: dict[str, str] = {}

    for relic_name in relics.keys():
        slug = find_relic_slug(name_to_slug, relic_name)
        if slug and slug not in required:
            required[slug] = relic_name

    for reward_name in all_reward_names(relics):
        slug = name_to_slug.get(reward_name.strip().lower())
        if slug and slug not in required:
            required[slug] = reward_name

    return required

