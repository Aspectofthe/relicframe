import gzip
import json
import tempfile
import unittest
from pathlib import Path

from companion_export_analyzer import (
    build_datasets,
    classify_price_evidence,
    content_fingerprint,
    extract_amounts,
    iter_discord_json_messages,
    iter_export_messages,
)


SAMPLE = """<!doctype html><html><body>
<div class="chatlog__message-group">
  <div class="chatlog__message-container" data-message-id="1"><div class="chatlog__message">
    <div class="chatlog__message-primary"><div class="chatlog__header">
      <span class="chatlog__author" data-user-id="u1">Breeder</span>
      <span class="chatlog__timestamp" title="Monday, August 24, 2026 1:30 PM">8/24/2026</span>
    </div><div class="chatlog__content chatlog__markdown"><span>Sold bulky Lotus Kubrow for 250p</span></div></div>
  </div></div>
  <div class="chatlog__message-container" data-message-id="2"><div class="chatlog__message">
    <div class="chatlog__short-timestamp" title="Monday, August 24, 2026 1:31 PM">1:31 PM</div>
    <div class="chatlog__message-primary"><div class="chatlog__content chatlog__markdown"><span>Proof</span></div>
    <div class="chatlog__attachment"><a href="https://example.test/sale.png"><img class="chatlog__attachment-media" src="thumb" title="Image: sale.png (1 MB)"></a></div></div>
  </div></div>
</div></body></html>"""


class TestCompanionExportAnalyzer(unittest.TestCase):
    def test_json_export_restores_order_and_resolves_forwarded_media(self):
        with tempfile.TemporaryDirectory() as temp:
            source = Path(temp) / "Guild - kubrow_sales [456].json"
            asset = Path(temp) / "sale-proof.png"
            asset.write_bytes(b"image")
            payload = {
                "channel": {"name": "kubrow_sales"},
                "messages": [
                    {
                        "id": "new", "timestamp": "2026-08-24T12:00:00-04:00", "content": "later",
                        "author": {"id": "u2", "name": "Buyer", "nickname": None},
                        "attachments": [],
                    },
                    {
                        "id": "old", "timestamp": "2026-08-24T11:00:00-04:00", "content": "",
                        "author": {"id": "u1", "name": "Seller", "nickname": "Breeder"},
                        "attachments": [], "reference": {"messageId": "original"},
                        "forwardedMessage": {
                            "timestamp": "2026-08-20T10:00:00-04:00",
                            "content": "WTS bulky Lotus Kubrow 250p",
                            "attachments": [{
                                "url": asset.name, "fileName": "proof.png", "fileSizeBytes": 5,
                            }],
                        },
                    },
                ],
            }
            source.write_text(json.dumps(payload), encoding="utf-8")
            messages = list(iter_discord_json_messages(source))

        self.assertEqual([message["message_id"] for message in messages], ["old", "new"])
        self.assertEqual(messages[0]["author"], "Breeder")
        self.assertTrue(messages[0]["forwarded"])
        self.assertIn("WTS bulky Lotus", messages[0]["text"])
        self.assertTrue(messages[0]["attachments"][0]["exists"])

    def test_stream_parser_keeps_group_author_and_attachment(self):
        with tempfile.TemporaryDirectory() as temp:
            source = Path(temp) / "Guild - kubrow_breeding [123].html"
            source.write_text(SAMPLE, encoding="utf-8")
            messages = list(iter_export_messages(source, chunk_size=37))
        self.assertEqual(len(messages), 2)
        self.assertEqual(messages[1]["author"], "Breeder")
        self.assertEqual(messages[1]["attachments"][0]["filename"], "sale.png")
        self.assertEqual(messages[0]["timestamp"], "2026-08-24T13:30:00")
        self.assertEqual(messages[1]["timestamp"], "2026-08-24T13:31:00")

    def test_price_extraction_and_classification(self):
        self.assertEqual(extract_amounts("asking 100-150p")[0]["high"], 150)
        self.assertEqual(extract_amounts("sold for 2,400p")[0]["low"], 2400)
        self.assertEqual(extract_amounts("sold for 1.5k")[0]["low"], 1500)
        kind, amounts = classify_price_evidence("I sold both prints for 275 plat")
        self.assertEqual(kind, "confirmed_sale")
        self.assertEqual(amounts[0]["low"], 275)

    def test_duplicate_fingerprint_includes_attachment_identity(self):
        base = {"text": "WTS bulky Lotus 250p"}
        first = {**base, "attachments": [{"filename": "image.png", "file_size_bytes": 100}]}
        repost = {**base, "attachments": [{"filename": "image.png", "file_size_bytes": 100}]}
        different_pet = {**base, "attachments": [{"filename": "image.png", "file_size_bytes": 101}]}
        self.assertEqual(content_fingerprint(first), content_fingerprint(repost))
        self.assertNotEqual(content_fingerprint(first), content_fingerprint(different_pet))

    def test_builds_full_and_review_datasets(self):
        with tempfile.TemporaryDirectory() as temp:
            source = Path(temp) / "Guild - kubrow_breeding [123].html"
            output = Path(temp) / "out"
            source.write_text(SAMPLE, encoding="utf-8")
            summary = build_datasets([source], output)
            with gzip.open(output / "messages.jsonl.gz", "rt", encoding="utf-8") as stream:
                records = [json.loads(line) for line in stream]
            evidence = [json.loads(line) for line in (output / "price_evidence.jsonl").read_text(encoding="utf-8").splitlines()]
            dedup_json_exists = (output / "price_evidence_deduplicated.jsonl").is_file()
            dedup_csv_exists = (output / "price_evidence_deduplicated.csv").is_file()
        self.assertEqual(summary["messages"], 2)
        self.assertEqual(summary["confirmed_sale"], 1)
        self.assertEqual(summary["deduplicated_price_evidence"], 1)
        self.assertEqual(len(records), 2)
        self.assertEqual(evidence[0]["traits"]["build"], ["bulky"])
        self.assertTrue(dedup_json_exists)
        self.assertTrue(dedup_csv_exists)


if __name__ == "__main__":
    unittest.main()
