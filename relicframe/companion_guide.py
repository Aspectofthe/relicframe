"""Discord guide for companion screenshots, inherited traits, and pricing."""
from __future__ import annotations

import discord

from companion_vision import COMMON_COLORS, RARE_COLORS, UNCOMMON_COLORS


def _color_list(values: set[str]) -> str:
    return ", ".join(value.title() for value in sorted(values))


def _current_examples(appraiser) -> str:
    examples = [
        ("Bulky Lotus Chesa", {"species": "kubrow", "build": "bulky", "pattern": "lotus", "breed": "chesa"}),
        ("Athletic Lotus", {"species": "kubrow", "build": "athletic", "pattern": "lotus"}),
        ("Bulky Merle", {"species": "kubrow", "build": "bulky", "pattern": "merle"}),
        ("Bulky Domino", {"species": "kubrow", "build": "bulky", "pattern": "domino"}),
    ]
    rows = []
    for label, traits in examples:
        try:
            result = appraiser.appraise(**traits)
        except RuntimeError:
            continue
        rows.append(f"**{label}:** {result.low:,}–{result.high:,}p (typical {result.estimate:,}p)")
    return "\n".join(rows) or "Current examples are unavailable until the sales dataset is loaded."


def companion_guide_embeds(appraiser) -> list[discord.Embed]:
    usage = discord.Embed(
        title="🐾 Companion Appraisal Guide — How to use it",
        description=(
            "Run **`/companion appraise`**. Free manual mode needs only species, pattern, and (for Kubrows) build; "
            "add breed and natural color slots for a more specific estimate. No API key or screenshot is required. "
            "If automatic vision is configured, attach a clear screenshot and use any manual fields as corrections.\n\n"
            "**Best screenshot**\n"
            "• Show the whole companion from the side/front in bright neutral lighting.\n"
            "• Use natural colors; remove armor and cosmetic skins.\n"
            "• Turn color correction off and use a white or black ship interior.\n"
            "• For Kubrow colors, preview **Nexus** or **Tigrol** fur pattern so the slots are separated.\n"
            "• Include the imprint/incubator UI when possible so breed text is readable.\n"
            "• One companion per screenshot. Blurry, cropped, tinted, or cosmetic screenshots lower confidence."
        ),
        color=0xB98BFF,
    )
    usage.set_footer(text="Manual mode stays local. An attached screenshot is sent only when automatic vision is configured.")

    value = discord.Embed(
        title="💰 What changes the price",
        description=(
            "Values below are for a **two-imprint set** and remain estimates. Current comparable sales shown by the "
            "appraisal command take priority over an old fixed chart.\n\n"
            "**Build:** Bulky is usually most requested; athletic is mid-range; skinny is normally lowest.\n"
            "**Pattern:** Lotus usually commands the most, then Merle; Hound/Domino are mid-range; Striped/Patchy are base.\n"
            "**Natural colors:** rare slots stack strongly. Matching energy, attractive ordering, black combinations, and clean themes can add value.\n"
            "**Breed:** Chesa/Sunika often price higher, Huras mid/high, Raksa/Sahasa lower/mid—but demand changes.\n"
            "**No inherited value:** height and gender are not stored on imprints. Cosmetics and skins are not inherited."
        ),
        color=0xF1C40F,
    )
    value.add_field(
        name="Historical chart examples",
        value=(
            "Bulky common: Striped/Patchy **40–70p**, Domino **60–80p**, Hound **70–80p**, Merle **80–100p**, Lotus **100–150p**\n"
            "Bulky Lotus: single rare **300–500p**, double rare **700–1,200p**, triple rare **1,800–2,500p**, solid rare **2,500–3,500p**\n"
            "Bulky Merle: single rare **200–450p**, double rare **500–800p**, triple rare **1,400–1,800p**, solid rare **1,500–2,500p**"
        ),
        inline=False,
    )
    value.add_field(name="Current data examples", value=_current_examples(appraiser), inline=False)

    colors = discord.Embed(
        title="🎨 Natural color rarity",
        description=(
            "The bot identifies up to four natural fur slots, then calculates rarity from the canonical tiers below. "
            "It does not treat energy color as a fur slot.\n\n"
            f"**Common:** {_color_list(COMMON_COLORS)}\n\n"
            f"**Uncommon:** {_color_list(UNCOMMON_COLORS)}\n\n"
            f"**Rare:** {_color_list(RARE_COLORS)}"
        ),
        color=0x3498DB,
    )
    colors.add_field(
        name="Calculated tiers",
        value=(
            "1 rare slot = **Single Rare** · 2 = **Double Rare** · the same rare twice = **Double Same Rare** · "
            "3 = **Triple Rare** · 4 = **Quad Rare**. Three/four matching slots become Solid Common, "
            "Solid Uncommon, Solid Rare, or Quad Solid."
        ),
        inline=False,
    )
    colors.set_footer(text="Lighting can make nearby colors look identical; low-confidence detections should be manually overridden.")
    return [usage, value, colors]
