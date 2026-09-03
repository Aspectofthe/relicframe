"""
test_formatting.py
build_detail_embed had no test coverage at all before this file, despite
being reachable from a live bot command (bot.py's relic detail handler)
and crashing with a TypeError on any relic with a known price - see
formatting.py's history for the fix. These tests exist specifically so
that crash can't come back silently.

Run with:
    python -m unittest tests.test_formatting -v
"""

import unittest

import formatting as fmt
from relic_data import Relic, RelicReward
from relic_row import compute_row


def _relic():
    return Relic("Lith A1", rewards=[
        RelicReward("Common A", "common"),
        RelicReward("Common B", "common"),
        RelicReward("Common C", "common"),
        RelicReward("Uncommon A", "uncommon"),
        RelicReward("Uncommon B", "uncommon"),
        RelicReward("Rare A", "rare"),
    ])


def _prices():
    return {
        "common a": 1.0, "common b": 1.0, "common c": 1.0,
        "uncommon a": 5.0, "uncommon b": 5.0, "rare a": 60.0,
    }


class TestBuildDetailEmbed(unittest.TestCase):
    def test_does_not_crash_with_a_known_price_and_no_batch_probability(self):
        # This is exactly the call shape that used to raise TypeError:
        # a known cost (so the "Batch (...) loss chance" line is reached)
        # with the batch-quantity prob_loss_pct still None by design (see
        # relic_row.py) - only the new per-open number is guaranteed known.
        relic = _relic()
        relic_prices = {"Lith A1": {
            "online": 3.0, "online_quantity": 5,
            "offline_included": 4.0, "offline_quantity": 2,
        }}
        row = compute_row(relic, "intact", 0.0, _prices(), relic_prices, {})
        embed = fmt.build_detail_embed(row, "intact", 0.0)
        self.assertIsNotNone(embed)

    def test_shows_buy_n_pointer_instead_of_crashing_when_batch_chance_unknown(self):
        relic = _relic()
        relic_prices = {"Lith A1": {"online": 3.0, "online_quantity": 5}}
        row = compute_row(relic, "intact", 0.0, _prices(), relic_prices, {})
        embed = fmt.build_detail_embed(row, "intact", 0.0)
        online_field = next(f for f in embed.fields if f.name == "Online")
        self.assertIn("/relics buy-n", online_field.value)

    def test_shows_single_open_win_and_loss_chance(self):
        relic = _relic()
        relic_prices = {"Lith A1": {"online": 3.0, "online_quantity": 5}}
        row = compute_row(relic, "intact", 0.0, _prices(), relic_prices, {})
        embed = fmt.build_detail_embed(row, "intact", 0.0)
        online_field = next(f for f in embed.fields if f.name == "Online")
        self.assertIn("Single-open odds:", online_field.value)
        self.assertIn("win chance", online_field.value)
        self.assertIn("loss chance", online_field.value)

    def test_no_price_at_all_still_does_not_crash(self):
        relic = _relic()
        row = compute_row(relic, "intact", 0.0, _prices(), {}, {})
        embed = fmt.build_detail_embed(row, "intact", 0.0)
        self.assertIsNotNone(embed)


if __name__ == "__main__":
    unittest.main()
