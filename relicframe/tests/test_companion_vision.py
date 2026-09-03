import json
import unittest

from companion_vision import (
    CompanionVisionError,
    OpenAICompanionVision,
    normalize_visual_traits,
    rarity_from_colors,
)


def response_traits(**overrides):
    data = {
        "species": "kubrow", "breed": "chesa", "pattern": "lotus", "build": "bulky",
        "colors": ["phorid red", "alad blue", "venus brown"], "energy_color": "blue",
        "natural_colors_visible": "yes", "cosmetics_detected": False,
        "confidence": {"species": 0.99, "breed": 0.8, "pattern": 0.9, "build": 0.95, "colors": 0.75, "energy": 0.7},
        "notes": ["Natural fur appears visible."],
    }
    data.update(overrides)
    return data


class TestColorRarity(unittest.TestCase):
    def test_rare_slot_counts_and_solids(self):
        self.assertEqual(rarity_from_colors(["phorid red"]), "single rare")
        self.assertEqual(rarity_from_colors(["phorid red", "alad blue"]), "double rare")
        self.assertEqual(rarity_from_colors(["phorid red", "phorid red"]), "double same rare")
        self.assertEqual(rarity_from_colors(["phorid red", "alad blue", "venus brown"]), "triple rare")
        self.assertEqual(rarity_from_colors(["phorid red"] * 3), "solid rare")
        self.assertEqual(rarity_from_colors(["ash grey"] * 3), "solid common")
        self.assertEqual(rarity_from_colors(["derelict black"] * 3), "solid uncommon")

    def test_normalization_uses_canonical_colors(self):
        traits = normalize_visual_traits(response_traits(colors=["Phorid Red", "ANYO GREY (NAVY)"]))
        self.assertEqual(traits.colors, ("phorid red", "anyo grey"))
        self.assertEqual(traits.rarity, "double rare")

    def test_unclear_or_cosmetic_colors_do_not_create_a_rarity_price_tier(self):
        unclear = normalize_visual_traits(response_traits(natural_colors_visible="uncertain"))
        cosmetic = normalize_visual_traits(response_traits(cosmetics_detected=True))
        self.assertEqual(unclear.rarity, "unknown")
        self.assertEqual(cosmetic.rarity, "unknown")


class TestVisionRequest(unittest.IsolatedAsyncioTestCase):
    async def test_structured_image_request_and_response(self):
        class FakeVision(OpenAICompanionVision):
            async def _post(self, payload):
                self.payload = payload
                return {"output_text": json.dumps(response_traits())}

        vision = FakeVision(api_key="test-key", model="test-model")
        result = await vision.analyze_url("https://cdn.example/pet.png", "hashed-user")
        self.assertEqual(result.pattern, "lotus")
        self.assertEqual(result.rarity, "triple rare")
        self.assertFalse(vision.payload["store"])
        self.assertEqual(vision.payload["input"][0]["content"][1]["type"], "input_image")
        self.assertEqual(vision.payload["text"]["format"]["type"], "json_schema")
        self.assertEqual(vision.payload["safety_identifier"], "hashed-user")

    async def test_missing_key_is_explained(self):
        vision = OpenAICompanionVision(api_key="")
        with self.assertRaisesRegex(CompanionVisionError, "OPENAI_API_KEY"):
            await vision.analyze_url("https://cdn.example/pet.png")
