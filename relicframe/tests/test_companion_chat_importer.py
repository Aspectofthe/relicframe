import json
import os
import tempfile
import unittest
from pathlib import Path

from companion_chat_importer import build_chat_archive, collect_entries


class TestCompanionChatImporter(unittest.TestCase):
    def test_matching_text_and_image_become_one_chat_entry(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "inbox"
            seller = root / "Seller One"
            seller.mkdir(parents=True)
            (seller / "2026-08-24_sale.txt").write_text("Sold Lotus Kubrow for 250p", encoding="utf-8")
            (seller / "2026-08-24_sale.png").write_bytes(b"not-a-real-png")

            entries = collect_entries(root)

            self.assertEqual(len(entries), 1)
            self.assertEqual(entries[0].author, "Seller One")
            self.assertEqual(entries[0].text, "Sold Lotus Kubrow for 250p")
            self.assertEqual(len(entries[0].attachments), 1)

    def test_archive_is_portable_and_escapes_message_html(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "sales"
            output = Path(temp) / "export"
            root.mkdir()
            (root / "message.txt").write_text("<script>alert('no')</script>", encoding="utf-8")
            (root / "photo.png").write_bytes(b"image")

            result = build_chat_archive(root, output)

            self.assertEqual(result["messages"], 2)
            self.assertTrue((output / "index.html").is_file())
            self.assertTrue((output / "transcript.txt").is_file())
            self.assertTrue((output / "messages.jsonl").is_file())
            page = (output / "index.html").read_text(encoding="utf-8")
            self.assertNotIn("<script>alert", page)
            self.assertIn("&lt;script&gt;", page)
            records = [json.loads(line) for line in (output / "messages.jsonl").read_text(encoding="utf-8").splitlines()]
            self.assertEqual(len(records), 2)
            image_record = next(record for record in records if record["attachments"])
            self.assertTrue((output / image_record["attachments"][0]).is_file())

    def test_output_inside_input_is_not_reimported(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "sales"
            output = root / "export"
            root.mkdir()
            (root / "one.txt").write_text("one", encoding="utf-8")
            output.mkdir()
            (output / "old.txt").write_text("old generated content", encoding="utf-8")

            entries = collect_entries(root, output)

            self.assertEqual(len(entries), 1)
            self.assertEqual(entries[0].text, "one")


if __name__ == "__main__":
    unittest.main()
