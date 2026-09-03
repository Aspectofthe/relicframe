import unittest
from types import SimpleNamespace

from companion_guide import companion_guide_embeds


class FakeAppraiser:
    def appraise(self, **_traits):
        return SimpleNamespace(low=100, high=300, estimate=200)


class TestCompanionGuide(unittest.TestCase):
    def test_guide_covers_usage_prices_and_color_tiers_within_discord_limits(self):
        embeds = companion_guide_embeds(FakeAppraiser())
        text = "\n".join((embed.title or "") + "\n" + (embed.description or "") for embed in embeds)
        self.assertEqual(len(embeds), 3)
        self.assertIn("How to use", text)
        self.assertIn("What changes the price", text)
        self.assertIn("Natural color rarity", text)
        self.assertIn("2,500–3,500p", "\n".join(field.value for embed in embeds for field in embed.fields))
        self.assertLessEqual(sum(len(embed) for embed in embeds), 6000)
        self.assertTrue(all(len(embed.fields) <= 25 for embed in embeds))


if __name__ == "__main__":
    unittest.main()
