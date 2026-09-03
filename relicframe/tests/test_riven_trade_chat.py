import tempfile
import unittest
from datetime import UTC, datetime
from pathlib import Path

from riven_trade_chat import RivenTradeChatLog, price_summary


class TestRivenTradeChatLog(unittest.TestCase):
    def test_parses_ocr_style_wts_and_wtb_lines(self):
        with tempfile.TemporaryDirectory() as temp:
            log = RivenTradeChatLog(Path(temp) / "offers.jsonl", ["Torid", "Dual Toxocyst"])
            offers, unparsed = log.parse(
                "[12:01] Seller.One: WTS [Torid Acri-critacan] 450p\n"
                "[12:02] Buyer: WTB [Dual Toxocyst Riven] 900 plat",
                observed_at=datetime(2026, 8, 25, tzinfo=UTC),
            )
        self.assertEqual(unparsed, 0)
        self.assertEqual([(offer.action, offer.weapon, offer.price) for offer in offers], [
            ("WTS", "Torid", 450),
            ("WTB", "Dual Toxocyst", 900),
        ])
        self.assertEqual(offers[0].seller, "Seller.One")

    def test_import_is_deduplicated_for_same_observation_day(self):
        with tempfile.TemporaryDirectory() as temp:
            log = RivenTradeChatLog(Path(temp) / "offers.jsonl", ["Torid"])
            stamp = datetime(2026, 8, 25, tzinfo=UTC)
            first = log.import_text("Seller: WTS [Torid Riven] 400p", observed_at=stamp)
            second = log.import_text("Seller: WTS [Torid Riven] 400p", observed_at=stamp)
            loaded = log.load()
        self.assertEqual(len(first.added), 1)
        self.assertEqual(len(second.added), 0)
        self.assertEqual(second.duplicate_count, 1)
        self.assertEqual(len(loaded), 1)

    def test_summary_keeps_offers_separate_from_sales(self):
        with tempfile.TemporaryDirectory() as temp:
            log = RivenTradeChatLog(Path(temp) / "offers.jsonl", ["Torid"])
            stamp = datetime.now(UTC)
            result = log.import_text(
                "A: WTS [Torid A] 300p\nB: WTS [Torid B] 500p\nC: WTB [Torid C] 700p",
                observed_at=stamp,
            )
            summary = price_summary(result.added)
        self.assertEqual(summary["wts_median"], 400)
        self.assertEqual(summary["wtb_max"], 700)
        self.assertEqual(summary["possible_spread"], 400)


if __name__ == "__main__":
    unittest.main()
