import json
import tempfile
import unittest
from pathlib import Path

from companion_appraisal import CompanionAppraiser


def row(price, *, build="bulky", pattern="lotus", breed="chesa", classification="listing", timestamp="2026-08-01T00:00:00-04:00"):
    return {
        "timestamp": timestamp, "classification": classification,
        "text": f"{build} {pattern} {breed} {price}p",
        "amounts": [{"low": price, "high": price}],
        "traits": {"build": [build], "pattern": [pattern], "breed": [breed]},
        "attachments": [{"filename": "proof.png"}],
    }


class TestCompanionAppraisal(unittest.TestCase):
    def test_exact_comparables_outrank_conflicting_traits(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "evidence.jsonl"
            records = [row(400), row(500), row(600), row(3000, build="skinny", pattern="merle")]
            path.write_text("\n".join(json.dumps(item) for item in records), encoding="utf-8")
            result = CompanionAppraiser(path).appraise(build="bulky", pattern="lotus", breed="chesa")
        self.assertEqual(result.estimate, 500)
        self.assertEqual(result.comparable_count, 3)
        self.assertEqual(result.screenshot_count, 3)

    def test_ignores_ambiguous_multi_price_posts(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "evidence.jsonl"
            good = row(700)
            ambiguous = row(5000)
            ambiguous["amounts"].append({"low": 100, "high": 100})
            path.write_text("\n".join(json.dumps(item) for item in (good, ambiguous)), encoding="utf-8")
            result = CompanionAppraiser(path).appraise(build="bulky", pattern="lotus")
        self.assertEqual(result.estimate, 700)
        self.assertEqual(result.comparable_count, 1)

    def test_historical_html_evidence_cannot_overpower_current_sales(self):
        with tempfile.TemporaryDirectory() as temp:
            current = Path(temp) / "current.jsonl"
            historical = Path(temp) / "historical.jsonl"
            current.write_text(json.dumps(row(500)), encoding="utf-8")
            old_rows = [
                row(5000, timestamp=f"2026-07-{day:02d}T00:00:00")
                for day in range(1, 21)
            ]
            for index, item in enumerate(old_rows):
                item["text"] = f"historical listing {index}"
            historical.write_text("\n".join(json.dumps(item) for item in old_rows), encoding="utf-8")
            result = CompanionAppraiser(current, historical).appraise(build="bulky", pattern="lotus")
        self.assertEqual(result.estimate, 500)
        self.assertEqual(result.current_comparable_count, 1)
        self.assertEqual(result.historical_comparable_count, 20)

    def test_kavat_can_use_historical_species_evidence(self):
        with tempfile.TemporaryDirectory() as temp:
            current = Path(temp) / "current.jsonl"
            historical = Path(temp) / "historical.jsonl"
            current.write_text("", encoding="utf-8")
            item = row(120, build="not applicable", pattern="hyacinth", breed="smeeta")
            item["traits"]["species"] = ["kavat"]
            historical.write_text(json.dumps(item), encoding="utf-8")
            result = CompanionAppraiser(current, historical).appraise(species="kavat", breed="smeeta", pattern="hyacinth")
        self.assertEqual(result.estimate, 120)
        self.assertEqual(result.current_comparable_count, 0)
        self.assertEqual(result.historical_comparable_count, 1)


if __name__ == "__main__":
    unittest.main()
