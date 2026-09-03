"""Structured visual trait recognition for companion appraisal screenshots."""
from __future__ import annotations

import json
import os
from dataclasses import dataclass

import aiohttp


COMMON_COLORS = {
    "ash grey", "earth brown", "corpus grey", "hek green", "kril brown",
    "gallium grey", "grustrag grey", "saturn brown",
}
UNCOMMON_COLORS = {
    "sedna grey", "derelict black", "mars red", "infested black", "void black",
    "darvo blue", "ordis grey", "mercury brown",
}
RARE_COLORS = {
    "anyo grey", "ambulas black", "shadow grey", "sargas brown", "jupiter brown",
    "phorid red", "alad blue", "venus brown",
}
NATURAL_COLORS = tuple(sorted(COMMON_COLORS | UNCOMMON_COLORS | RARE_COLORS))
ENERGY_COLORS = ("green", "light gold", "pink", "purple", "blue", "orange/red", "lilac", "black", "gold", "unknown")
PATTERNS = ("striped", "patchy", "hound", "domino", "merle", "lotus", "hyacinth", "unknown")
BUILDS = ("skinny", "athletic", "bulky", "not applicable", "unknown")
BREEDS = ("chesa", "sunika", "huras", "raksa", "sahasa", "smeeta", "adarza", "vasca", "unknown")
SPECIES = ("kubrow", "kavat", "other", "unknown")


@dataclass(frozen=True)
class VisualTraits:
    species: str
    breed: str
    pattern: str
    build: str
    colors: tuple[str, ...]
    energy_color: str
    rarity: str
    natural_colors_visible: str
    cosmetics_detected: bool
    confidence: dict[str, float]
    notes: tuple[str, ...]


class CompanionVisionError(RuntimeError):
    pass


def _canonical(value: str) -> str:
    aliases = {
        "stripe": "striped", "orange": "orange/red", "red": "orange/red",
        "ambulas black (dark brown)": "ambulas black", "anyo grey (navy)": "anyo grey",
        "shadow grey (cream)": "shadow grey", "sargas brown (gold)": "sargas brown",
        "jupiter brown (orange)": "jupiter brown", "venus brown (purple)": "venus brown",
    }
    cleaned = " ".join(str(value or "").strip().casefold().split())
    return aliases.get(cleaned, cleaned)


def rarity_from_colors(colors: list[str] | tuple[str, ...]) -> str:
    slots = [_canonical(color) for color in colors if _canonical(color) in COMMON_COLORS | UNCOMMON_COLORS | RARE_COLORS]
    if not slots:
        return "unknown"
    rare_slots = [color for color in slots if color in RARE_COLORS]
    if len(slots) >= 3 and len(set(slots)) == 1:
        if slots[0] in RARE_COLORS:
            return "quad solid" if len(slots) >= 4 else "solid rare"
        if slots[0] in UNCOMMON_COLORS:
            return "solid uncommon"
        return "solid common"
    if len(rare_slots) >= 4:
        return "quad rare"
    if len(rare_slots) == 3:
        return "triple rare"
    if len(rare_slots) == 2:
        return "double same rare" if len(set(rare_slots)) == 1 else "double rare"
    if len(rare_slots) == 1:
        return "single rare"
    return "uncommon" if any(color in UNCOMMON_COLORS for color in slots) else "common"


def normalize_visual_traits(raw: dict) -> VisualTraits:
    visibility = raw.get("natural_colors_visible") if raw.get("natural_colors_visible") in {"yes", "no", "uncertain"} else "uncertain"
    cosmetics = bool(raw.get("cosmetics_detected"))
    colors = tuple(
        color for color in (_canonical(value) for value in raw.get("colors") or [])
        if color in COMMON_COLORS | UNCOMMON_COLORS | RARE_COLORS
    )[:4]
    confidence = {
        key: max(0.0, min(1.0, float(value)))
        for key, value in (raw.get("confidence") or {}).items()
        if key in {"species", "breed", "pattern", "build", "colors", "energy"}
    }
    rarity = rarity_from_colors(colors) if visibility == "yes" and not cosmetics and confidence.get("colors", 0.0) >= 0.55 else "unknown"
    return VisualTraits(
        species=_canonical(raw.get("species")) if _canonical(raw.get("species")) in SPECIES else "unknown",
        breed=_canonical(raw.get("breed")) if _canonical(raw.get("breed")) in BREEDS else "unknown",
        pattern=_canonical(raw.get("pattern")) if _canonical(raw.get("pattern")) in PATTERNS else "unknown",
        build=_canonical(raw.get("build")) if _canonical(raw.get("build")) in BUILDS else "unknown",
        colors=colors,
        energy_color=_canonical(raw.get("energy_color")) if _canonical(raw.get("energy_color")) in ENERGY_COLORS else "unknown",
        rarity=rarity,
        natural_colors_visible=visibility,
        cosmetics_detected=cosmetics,
        confidence=confidence,
        notes=tuple(str(note)[:180] for note in (raw.get("notes") or [])[:5]),
    )


VISION_SCHEMA = {
    "type": "object",
    "additionalProperties": False,
    "properties": {
        "species": {"type": "string", "enum": list(SPECIES)},
        "breed": {"type": "string", "enum": list(BREEDS)},
        "pattern": {"type": "string", "enum": list(PATTERNS)},
        "build": {"type": "string", "enum": list(BUILDS)},
        "colors": {"type": "array", "maxItems": 4, "items": {"type": "string", "enum": list(NATURAL_COLORS)}},
        "energy_color": {"type": "string", "enum": list(ENERGY_COLORS)},
        "natural_colors_visible": {"type": "string", "enum": ["yes", "no", "uncertain"]},
        "cosmetics_detected": {"type": "boolean"},
        "confidence": {
            "type": "object", "additionalProperties": False,
            "properties": {key: {"type": "number", "minimum": 0, "maximum": 1} for key in ("species", "breed", "pattern", "build", "colors", "energy")},
            "required": ["species", "breed", "pattern", "build", "colors", "energy"],
        },
        "notes": {"type": "array", "maxItems": 5, "items": {"type": "string"}},
    },
    "required": ["species", "breed", "pattern", "build", "colors", "energy_color", "natural_colors_visible", "cosmetics_detected", "confidence", "notes"],
}


VISION_PROMPT = f"""Identify breeder-relevant traits in this Warframe companion screenshot.

Rules:
- Use only visible evidence. Return unknown instead of guessing.
- Kubrow patterns: striped, patchy, hound, domino, merle, lotus. Kavat natural pattern: hyacinth.
- Kubrow builds: skinny (thin neck/chest), athletic, bulky (very wide chest). Kavat build is not applicable.
- Breed usually cannot be known from appearance alone. Use UI text if visible; otherwise unknown.
- Identify natural fur color slots, not armor, skins, lighting, or player-applied cosmetics.
- Canonical natural colors: {', '.join(NATURAL_COLORS)}.
- Energy color is separate from fur and is most reliably visible in eyes/energy effects.
- If a skin, armor, color correction, colored lighting, or non-natural palette prevents identification, mark it and lower confidence.
- Height and gender are irrelevant because imprints do not carry them.
- The notes should briefly state visible evidence or why a field is uncertain. Do not give a price.
"""


class OpenAICompanionVision:
    def __init__(self, api_key: str | None = None, model: str | None = None, timeout_seconds: float = 60.0):
        self.api_key = api_key if api_key is not None else os.environ.get("OPENAI_API_KEY", "")
        self.model = model or os.environ.get("COMPANION_VISION_MODEL", "gpt-5.4-mini")
        self.timeout_seconds = timeout_seconds

    @property
    def configured(self) -> bool:
        return bool(self.api_key)

    async def _post(self, payload: dict) -> dict:
        timeout = aiohttp.ClientTimeout(total=self.timeout_seconds)
        headers = {"Authorization": f"Bearer {self.api_key}", "Content-Type": "application/json"}
        async with aiohttp.ClientSession(timeout=timeout, headers=headers) as session:
            async with session.post("https://api.openai.com/v1/responses", json=payload) as response:
                body = await response.text()
                if response.status >= 400:
                    try:
                        message = json.loads(body).get("error", {}).get("message") or body
                    except json.JSONDecodeError:
                        message = body
                    raise CompanionVisionError(f"Vision request failed ({response.status}): {message[:300]}")
                return json.loads(body)

    async def analyze_url(self, image_url: str, safety_identifier: str | None = None) -> VisualTraits:
        if not self.configured:
            raise CompanionVisionError("Automatic screenshot recognition needs OPENAI_API_KEY in the bot environment.")
        payload = {
            "model": self.model,
            "store": False,
            "reasoning": {"effort": "low"},
            "max_output_tokens": 900,
            "input": [{
                "role": "user",
                "content": [
                    {"type": "input_text", "text": VISION_PROMPT},
                    {"type": "input_image", "image_url": image_url, "detail": "high"},
                ],
            }],
            "text": {"format": {"type": "json_schema", "name": "companion_traits", "strict": True, "schema": VISION_SCHEMA}},
        }
        if safety_identifier:
            payload["safety_identifier"] = safety_identifier[:64]
        response = await self._post(payload)
        output_text = response.get("output_text")
        if not output_text:
            output_text = next(
                (part.get("text") for item in response.get("output") or [] for part in item.get("content") or [] if part.get("type") == "output_text"),
                None,
            )
        if not output_text:
            raise CompanionVisionError("The vision model returned no trait analysis.")
        try:
            return normalize_visual_traits(json.loads(output_text))
        except (json.JSONDecodeError, TypeError, ValueError) as exc:
            raise CompanionVisionError("The vision model returned an invalid trait analysis.") from exc
