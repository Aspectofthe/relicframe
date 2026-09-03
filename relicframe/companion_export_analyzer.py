"""Extract companion pricing evidence from DiscordChatExporter HTML or JSON files.

The parser is streaming so very large exports can be processed without loading
the complete HTML document into memory.  It stores the full normalized message
history in compressed JSONL and a smaller, reviewable pricing-evidence file.
"""
from __future__ import annotations

import argparse
import csv
import gzip
import hashlib
import json
import re
from collections import Counter, deque
from datetime import datetime
from html.parser import HTMLParser
from pathlib import Path
from typing import Iterable, Iterator


NUMBER_PATTERN = r"(?:\d{1,3}(?:,\d{3})+|\d{1,6})(?:\.\d{1,2})?"
PRICE_RE = re.compile(
    rf"(?<![\w])(?P<low>{NUMBER_PATTERN})(?P<low_k>k)?"
    rf"(?:\s*(?:-|–|—|to|/)\s*(?P<high>{NUMBER_PATTERN})(?P<high_k>k)?)?"
    r"\s*(?:p|pl|plat|platinum)\b",
    re.IGNORECASE,
)
K_PRICE_RE = re.compile(rf"(?<![\w])(?P<low>{NUMBER_PATTERN})\s*k\b", re.IGNORECASE)
SALE_RE = re.compile(r"\b(?:sold|bought|purchased|paid|went\s+for|sale\s+for|buyer\s+paid)\b", re.I)
LISTING_RE = re.compile(r"\b(?:wts|wtb|selling|buying|asking|offer(?:ing)?|auction|reserve)\b", re.I)
APPRAISAL_RE = re.compile(r"\b(?:price\s*check|pc|worth|value|valued|price|apprais(?:al|e|ed))\b|how much", re.I)

TRAIT_GROUPS = {
    "species": ("kubrow", "kavat"),
    "breed": ("chesa", "sunika", "huras", "raksa", "sahasa", "smeeta", "adarza", "vasca", "helminth"),
    "pattern": ("striped", "stripe", "patchy", "hound", "domino", "merle", "lotus", "hyacinth"),
    "build": ("skinny", "athletic", "bulky"),
    "rarity": ("single rare", "double rare", "double same rare", "triple rare", "quad rare", "solid common", "solid uncommon", "solid rare", "quad solid", "common", "uncommon"),
    "color": (
        "ash grey", "earth brown", "corpus grey", "hek green", "kril brown", "gallium grey",
        "grustrag grey", "saturn brown", "sedna grey", "derelict black", "mars red",
        "infested black", "void black", "darvo blue", "ordis grey", "mercury brown",
        "anyo grey", "ambulas black", "shadow grey", "sargas brown", "jupiter brown",
        "phorid red", "alad blue", "venus brown", "green", "light gold", "pink", "purple",
        "blue", "orange", "red", "lilac", "black", "gold", "cyan", "lime", "white",
    ),
}


def _classes(attrs: dict[str, str | None]) -> set[str]:
    return set((attrs.get("class") or "").split())


def _clean_text(parts: list[str]) -> str:
    text = "".join(parts).replace("\r", "")
    text = re.sub(r"[ \t]+", " ", text)
    text = re.sub(r" *\n *", "\n", text)
    return re.sub(r"\n{3,}", "\n\n", text).strip()


def _normalize_timestamp(value: str) -> str:
    for pattern in ("%A, %B %d, %Y %I:%M %p", "%B %d, %Y %I:%M %p"):
        try:
            return datetime.strptime(value, pattern).isoformat()
        except ValueError:
            pass
    return value


class DiscordExportParser(HTMLParser):
    """Incrementally parse the message fields emitted by DiscordChatExporter."""

    def __init__(self, channel: str, source_file: str):
        super().__init__(convert_charrefs=True)
        self.channel = channel
        self.source_file = source_file
        self.div_depth = 0
        self.group_depth: int | None = None
        self.group_author = "Unknown"
        self.group_user_id = ""
        self.container_depth: int | None = None
        self.content_depth: int | None = None
        self.attachment_depth: int | None = None
        self.capture_author = False
        self.author_parts: list[str] = []
        self.pending_attachment_url = ""
        self.current: dict | None = None
        self.completed: deque[dict] = deque()

    def handle_starttag(self, tag: str, attrs_list: list[tuple[str, str | None]]) -> None:
        attrs = dict(attrs_list)
        classes = _classes(attrs)
        if tag == "div":
            self.div_depth += 1
            if "chatlog__message-group" in classes:
                self.group_depth = self.div_depth
                self.group_author = "Unknown"
                self.group_user_id = ""
            if "chatlog__message-container" in classes and attrs.get("data-message-id"):
                self.container_depth = self.div_depth
                self.current = {
                    "message_id": attrs["data-message-id"],
                    "channel": self.channel,
                    "source_file": self.source_file,
                    "author": self.group_author,
                    "user_id": self.group_user_id,
                    "timestamp": "",
                    "text": "",
                    "attachments": [],
                }
            if self.current is not None and "chatlog__short-timestamp" in classes:
                raw = attrs.get("title") or ""
                self.current["timestamp"] = _normalize_timestamp(raw)
            if self.current is not None and "chatlog__content" in classes:
                self.content_depth = self.div_depth
                self.current["_text_parts"] = []
            if self.current is not None and "chatlog__attachment" in classes:
                self.attachment_depth = self.div_depth
                self.pending_attachment_url = ""
        elif tag == "span" and self.current is not None:
            if "chatlog__author" in classes:
                self.capture_author = True
                self.author_parts = []
                self.current["user_id"] = attrs.get("data-user-id") or ""
            if "chatlog__timestamp" in classes or "chatlog__short-timestamp" in classes:
                raw = attrs.get("title") or ""
                self.current["timestamp"] = _normalize_timestamp(raw)
        elif tag == "a" and self.current is not None and self.attachment_depth is not None:
            self.pending_attachment_url = attrs.get("href") or ""
        elif tag == "img" and self.current is not None:
            if self.content_depth is not None and "chatlog__emoji" in classes:
                self.current["_text_parts"].append(attrs.get("alt") or "")
            if self.attachment_depth is not None and "chatlog__attachment-media" in classes:
                url = self.pending_attachment_url or attrs.get("src") or ""
                title = attrs.get("title") or ""
                name_match = re.search(r"(?:Image|Video|Audio):\s*(.+?)(?:\s*\(|$)", title)
                attachment = {
                    "url": url,
                    "filename": name_match.group(1) if name_match else "",
                    "title": title,
                }
                if url and attachment not in self.current["attachments"]:
                    self.current["attachments"].append(attachment)
        elif tag == "br" and self.current is not None and self.content_depth is not None:
            self.current["_text_parts"].append("\n")

    def handle_data(self, data: str) -> None:
        if self.current is None:
            return
        if self.capture_author:
            self.author_parts.append(data)
        if self.content_depth is not None:
            self.current["_text_parts"].append(data)

    def handle_endtag(self, tag: str) -> None:
        if tag == "span" and self.capture_author:
            author = _clean_text(self.author_parts) or "Unknown"
            self.capture_author = False
            self.group_author = author
            if self.current is not None:
                self.current["author"] = author
                self.group_user_id = self.current.get("user_id", "")
        if tag != "div":
            return
        if self.current is not None and self.content_depth == self.div_depth:
            self.current["text"] = _clean_text(self.current.pop("_text_parts", []))
            self.content_depth = None
        if self.attachment_depth == self.div_depth:
            self.attachment_depth = None
            self.pending_attachment_url = ""
        if self.current is not None and self.container_depth == self.div_depth:
            self.current.pop("_text_parts", None)
            self.completed.append(self.current)
            self.current = None
            self.container_depth = None
            self.content_depth = None
            self.attachment_depth = None
        if self.group_depth == self.div_depth:
            self.group_depth = None
        self.div_depth -= 1

    def drain(self) -> Iterator[dict]:
        while self.completed:
            yield self.completed.popleft()


def channel_from_filename(path: Path) -> str:
    match = re.search(r" - ([^\[]+?)\s*\[\d+\]\.html$", path.name, re.I)
    return match.group(1).strip() if match else path.stem


def _json_attachment(raw: dict, source_dir: Path) -> dict:
    relative = str(raw.get("url") or "")
    local = (source_dir / relative).resolve() if relative else None
    return {
        "url": relative,
        "filename": str(raw.get("fileName") or (Path(relative).name if relative else "")),
        "title": "",
        "file_size_bytes": raw.get("fileSizeBytes"),
        "local_path": str(local) if local else "",
        "exists": bool(local and local.is_file()),
    }


def iter_discord_json_messages(path: str | Path) -> Iterator[dict]:
    """Normalize a DiscordChatExporter JSON file and restore chronological order."""
    source = Path(path)
    with source.open("r", encoding="utf-8-sig") as stream:
        payload = json.load(stream)
    channel = str((payload.get("channel") or {}).get("name") or source.stem)
    raw_messages = sorted(payload.get("messages") or [], key=lambda item: str(item.get("timestamp") or ""))
    for raw in raw_messages:
        forwarded = raw.get("forwardedMessage") or None
        outer_text = str(raw.get("content") or "").strip()
        forwarded_text = str((forwarded or {}).get("content") or "").strip()
        text = "\n\n".join(part for part in (outer_text, forwarded_text) if part)
        attachment_rows = list(raw.get("attachments") or [])
        if forwarded:
            attachment_rows.extend(forwarded.get("attachments") or [])
        author = raw.get("author") or {}
        reference = raw.get("reference") or {}
        yield {
            "message_id": str(raw.get("id") or ""),
            "channel": channel,
            "source_file": source.name,
            "author": str(author.get("nickname") or author.get("name") or "Unknown"),
            "user_id": str(author.get("id") or ""),
            "timestamp": str(raw.get("timestamp") or ""),
            "timestamp_edited": raw.get("timestampEdited"),
            "forwarded": bool(forwarded),
            "forwarded_timestamp": (forwarded or {}).get("timestamp"),
            "reference_message_id": str(reference.get("messageId") or ""),
            "text": text,
            "attachments": [_json_attachment(item, source.parent) for item in attachment_rows],
        }


def iter_export_messages(path: str | Path, chunk_size: int = 1024 * 1024) -> Iterator[dict]:
    source = Path(path)
    parser = DiscordExportParser(channel_from_filename(source), source.name)
    with source.open("r", encoding="utf-8", errors="replace", newline="") as stream:
        while chunk := stream.read(chunk_size):
            parser.feed(chunk)
            yield from parser.drain()
    parser.close()
    yield from parser.drain()


def iter_source_messages(path: str | Path) -> Iterator[dict]:
    source = Path(path)
    if source.suffix.casefold() == ".json":
        yield from iter_discord_json_messages(source)
    elif source.suffix.casefold() in (".html", ".htm"):
        yield from iter_export_messages(source)
    else:
        raise ValueError(f"Unsupported Discord export format: {source}")


def content_fingerprint(message: dict) -> str:
    """Identify reposts without merging different pets that reuse the same ad text."""
    normalized = re.sub(r"\s+", " ", message.get("text", "")).strip().casefold()
    attachment_signatures = []
    for attachment in message.get("attachments", []):
        filename = str(attachment.get("filename") or "").strip().casefold()
        size = attachment.get("file_size_bytes")
        attachment_signatures.append(f"{filename}:{size if size is not None else ''}")
    payload = "\0".join((normalized, *sorted(attachment_signatures)))
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()[:20] if payload.strip("\0") else ""


def extract_amounts(text: str) -> list[dict]:
    amounts = []
    for match in PRICE_RE.finditer(text):
        low = float(match.group("low").replace(",", ""))
        if match.group("low_k"):
            low *= 1000
        high_text = match.group("high")
        high = float(high_text.replace(",", "")) if high_text else low
        if high_text and match.group("high_k"):
            high *= 1000
        amounts.append({"low": low, "high": high, "raw": match.group(0)})
    occupied = [match.span() for match in PRICE_RE.finditer(text)]
    if SALE_RE.search(text) or LISTING_RE.search(text) or APPRAISAL_RE.search(text):
        for match in K_PRICE_RE.finditer(text):
            if any(start <= match.start() < end for start, end in occupied):
                continue
            value = float(match.group("low").replace(",", "")) * 1000
            amounts.append({"low": value, "high": value, "raw": match.group(0)})
    return amounts


def extract_traits(text: str) -> dict[str, list[str]]:
    lowered = text.casefold()
    found: dict[str, list[str]] = {}
    for group, values in TRAIT_GROUPS.items():
        matches = [value for value in values if re.search(rf"\b{re.escape(value)}\b", lowered)]
        if matches:
            found[group] = matches
    return found


def classify_price_evidence(text: str) -> tuple[str | None, list[dict]]:
    amounts = extract_amounts(text)
    if amounts and SALE_RE.search(text):
        return "confirmed_sale", amounts
    if amounts and LISTING_RE.search(text):
        return "listing", amounts
    if APPRAISAL_RE.search(text) and (amounts or re.search(r"\b(?:kubrow|kavat|imprint|print)\b", text, re.I)):
        return "appraisal", amounts
    if amounts:
        return "price_mention", amounts
    return None, []


def companion_relevance(text: str, traits: dict[str, list[str]], has_attachment: bool) -> tuple[int, str]:
    score = 0
    if traits:
        score += 2
    if any(group in traits for group in ("breed", "pattern", "build", "rarity")):
        score += 2
    if re.search(r"\b(?:kubrow|kavat|imprints?|prints?|puppy|kitty|companion|breed(?:ing)?)\b", text, re.I):
        score += 2
    if has_attachment:
        score += 1
    if re.search(r"\b(?:riven|prime\s+set|warframe\s+market|mod|arcane)\b", text, re.I):
        score -= 4
    label = "high" if score >= 4 else "medium" if score >= 2 else "low"
    return score, label


def _context_record(message: dict) -> dict:
    return {
        "message_id": message["message_id"],
        "author": message["author"],
        "timestamp": message["timestamp"],
        "text": message["text"],
        "attachments": message["attachments"],
    }


def build_datasets(sources: Iterable[str | Path], output_dir: str | Path) -> dict:
    sources = list(sources)
    destination = Path(output_dir)
    destination.mkdir(parents=True, exist_ok=True)
    full_path = destination / "messages.jsonl.gz"
    evidence_path = destination / "price_evidence.jsonl"
    csv_path = destination / "price_evidence.csv"
    dedup_evidence_path = destination / "price_evidence_deduplicated.jsonl"
    dedup_csv_path = destination / "price_evidence_deduplicated.csv"
    counts = Counter()
    per_channel = Counter()
    first_timestamp: dict[str, str] = {}
    last_timestamp: dict[str, str] = {}
    previous: deque[dict] = deque(maxlen=3)
    message_fingerprints = Counter()
    evidence_fingerprints = Counter()
    local_asset_paths: set[str] = set()
    deduplicated: dict[str, tuple[dict, dict]] = {}
    csv_fields = (
        "channel", "message_id", "timestamp", "author", "classification",
        "companion_relevance", "companion_relevance_score", "price_low", "price_high",
        "text", "attachment_count", "forwarded", "content_fingerprint", "traits",
    )

    with gzip.open(full_path, "wt", encoding="utf-8", newline="\n") as full, evidence_path.open(
        "w", encoding="utf-8", newline="\n"
    ) as evidence, csv_path.open("w", encoding="utf-8", newline="") as csv_stream:
        csv_writer = csv.DictWriter(csv_stream, fieldnames=csv_fields)
        csv_writer.writeheader()
        for source in sources:
            previous.clear()
            for message in iter_source_messages(source):
                fingerprint = content_fingerprint(message)
                message["content_fingerprint"] = fingerprint
                if fingerprint:
                    message_fingerprints[fingerprint] += 1
                full.write(json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n")
                counts["messages"] += 1
                counts["attachments"] += len(message["attachments"])
                counts["forwarded_messages"] += int(bool(message.get("forwarded")))
                counts["messages_with_attachments"] += int(bool(message["attachments"]))
                for attachment in message["attachments"]:
                    local_path = str(attachment.get("local_path") or "")
                    if not local_path:
                        continue
                    local_asset_paths.add(local_path)
                    if attachment.get("exists"):
                        counts["local_attachment_references"] += 1
                    else:
                        counts["missing_attachment_references"] += 1
                per_channel[message["channel"]] += 1
                if message["timestamp"]:
                    first_timestamp.setdefault(message["channel"], message["timestamp"])
                    last_timestamp[message["channel"]] = message["timestamp"]
                classification, amounts = classify_price_evidence(message["text"])
                if classification:
                    if fingerprint:
                        evidence_fingerprints[fingerprint] += 1
                    record = dict(message)
                    record["classification"] = classification
                    record["amounts"] = amounts
                    record["traits"] = extract_traits(message["text"])
                    record["context_before"] = [_context_record(item) for item in previous]
                    context_text = "\n".join(item["text"] for item in previous) + "\n" + message["text"]
                    record["context_traits"] = extract_traits(context_text)
                    context_has_attachment = bool(
                        message["attachments"] or any(item["attachments"] for item in previous)
                    )
                    relevance_score, relevance = companion_relevance(
                        context_text, record["context_traits"], context_has_attachment,
                    )
                    record["companion_relevance_score"] = relevance_score
                    record["companion_relevance"] = relevance
                    evidence.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n")
                    counts["price_evidence"] += 1
                    counts[classification] += 1
                    low = min((amount["low"] for amount in amounts), default="")
                    high = max((amount["high"] for amount in amounts), default="")
                    csv_row = {
                        "channel": message["channel"],
                        "message_id": message["message_id"],
                        "timestamp": message["timestamp"],
                        "author": message["author"],
                        "classification": classification,
                        "companion_relevance": relevance,
                        "companion_relevance_score": relevance_score,
                        "price_low": low,
                        "price_high": high,
                        "text": message["text"],
                        "attachment_count": len(message["attachments"]),
                        "forwarded": bool(message.get("forwarded")),
                        "content_fingerprint": fingerprint,
                        "traits": json.dumps(record["context_traits"], ensure_ascii=False),
                    }
                    csv_writer.writerow(csv_row)
                    deduplicated[fingerprint or message["message_id"]] = (record, csv_row)
                previous.append(message)

    with dedup_evidence_path.open("w", encoding="utf-8", newline="\n") as evidence, dedup_csv_path.open(
        "w", encoding="utf-8", newline=""
    ) as csv_stream:
        csv_writer = csv.DictWriter(csv_stream, fieldnames=csv_fields)
        csv_writer.writeheader()
        for record, csv_row in sorted(deduplicated.values(), key=lambda pair: pair[0]["timestamp"]):
            evidence.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n")
            csv_writer.writerow(csv_row)

    summary = {
        **counts,
        "channels": dict(per_channel),
        "first_timestamp": first_timestamp,
        "last_timestamp": last_timestamp,
        "unique_local_assets": len(local_asset_paths),
        "unique_message_content": len(message_fingerprints),
        "duplicate_message_rows": sum(count - 1 for count in message_fingerprints.values()),
        "unique_price_evidence_content": len(evidence_fingerprints),
        "duplicate_price_evidence_rows": sum(count - 1 for count in evidence_fingerprints.values()),
        "deduplicated_price_evidence": len(deduplicated),
        "sources": [str(Path(source).resolve()) for source in sources],
        "outputs": {
            "messages": str(full_path.resolve()),
            "price_evidence": str(evidence_path.resolve()),
            "price_evidence_csv": str(csv_path.resolve()),
            "deduplicated_price_evidence": str(dedup_evidence_path.resolve()),
            "deduplicated_price_evidence_csv": str(dedup_csv_path.resolve()),
        },
        "note": "Classifications are heuristic candidates for review, not automatically verified sales.",
    }
    (destination / "summary.json").write_text(json.dumps(summary, indent=2, ensure_ascii=False), encoding="utf-8")
    return summary


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Extract companion pricing evidence from Discord HTML/JSON exports.")
    parser.add_argument("sources", nargs="+", help="DiscordChatExporter HTML or JSON files")
    parser.add_argument("--output", required=True, help="Folder for normalized datasets")
    args = parser.parse_args(argv)
    summary = build_datasets(args.sources, args.output)
    print(json.dumps(summary, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
