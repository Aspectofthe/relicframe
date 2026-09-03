"""
test_slug_registry.py
Tests for slug_registry.py.

Run with:
    python -m unittest test_slug_registry.py -v
"""

import unittest

from relic_data import Relic, RelicReward
from slug_registry import build_required_slugs


class TestBuildRequiredSlugs(unittest.TestCase):
    def test_shared_reward_across_relics_fetched_once(self):
        # "Shared Prime Part" appears in two different relics - the
        # registry must resolve to exactly one slug entry for it, not two.
        relics = {
            "Lith A1": Relic("Lith A1", rewards=[RelicReward("Shared Prime Part", "rare")]),
            "Meso B2": Relic("Meso B2", rewards=[RelicReward("Shared Prime Part", "rare")]),
        }
        name_to_slug = {
            "lith a1 relic": "lith_a1_relic",
            "meso b2 relic": "meso_b2_relic",
            "shared prime part": "shared_prime_part",
        }
        required = build_required_slugs(relics, name_to_slug)
        self.assertEqual(list(required.keys()).count("shared_prime_part"), 1)

    def test_includes_both_relic_slugs_and_reward_slugs(self):
        relics = {"Lith A1": Relic("Lith A1", rewards=[RelicReward("Some Reward", "common")])}
        name_to_slug = {"lith a1 relic": "lith_a1_relic", "some reward": "some_reward"}
        required = build_required_slugs(relics, name_to_slug)
        self.assertIn("lith_a1_relic", required)
        self.assertIn("some_reward", required)

    def test_unresolvable_name_is_omitted_not_an_error(self):
        relics = {"Lith A1": Relic("Lith A1", rewards=[RelicReward("Nonexistent Item", "common")])}
        name_to_slug = {"lith a1 relic": "lith_a1_relic"}  # "nonexistent item" not present
        required = build_required_slugs(relics, name_to_slug)
        self.assertEqual(len(required), 1)
        self.assertIn("lith_a1_relic", required)

    def test_empty_relics_returns_empty(self):
        self.assertEqual(build_required_slugs({}, {"a": "b"}), {})

    def test_total_unique_slug_count_matches_dedup_expectation(self):
        # 3 relics all sharing the same single reward -> 3 relic slugs + 1
        # reward slug = 4 total, never 6 (3 relics x would-be 2 fetches each).
        relics = {
            f"Relic{i}": Relic(f"Relic{i}", rewards=[RelicReward("Common Reward", "common")])
            for i in range(3)
        }
        name_to_slug = {f"relic{i} relic": f"relic{i}_slug" for i in range(3)}
        name_to_slug["common reward"] = "common_reward_slug"
        required = build_required_slugs(relics, name_to_slug)
        self.assertEqual(len(required), 4)


if __name__ == "__main__":
    unittest.main()

