"""Turn a folder of saved text and images into a portable chat archive.

This intentionally works only with files the user already has.  It never
logs into Discord, reads a user token, or scrapes a server.  The resulting
JSONL is also the stable input format for the future companion appraiser.
"""
from __future__ import annotations

import argparse
import hashlib
import html
import json
import os
import re
import shutil
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path


TEXT_EXTENSIONS = {".txt"}
IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".webp", ".gif"}
SUPPORTED_EXTENSIONS = TEXT_EXTENSIONS | IMAGE_EXTENSIONS


@dataclass
class ChatEntry:
    entry_id: str
    timestamp: datetime
    author: str
    text: str
    source_files: list[str]
    attachments: list[Path]


def _read_text(path: Path) -> str:
    for encoding in ("utf-8-sig", "utf-16", "cp1252"):
        try:
            return path.read_text(encoding=encoding).strip()
        except (UnicodeError, OSError):
            continue
    return path.read_text(encoding="utf-8", errors="replace").strip()


def _timestamp_from_name(path: Path) -> datetime | None:
    name = path.stem
    patterns = (
        (r"(?<!\d)(20\d{2})[-_.](\d{1,2})[-_.](\d{1,2})(?:[ T_-](\d{1,2})[-_.:](\d{2})(?:[-_.:](\d{2}))?)?", "ymd"),
        (r"(?<!\d)(\d{1,2})[-_.](\d{1,2})[-_.](20\d{2})(?:[ T_-](\d{1,2})[-_.:](\d{2})(?:[-_.:](\d{2}))?)?", "mdy"),
    )
    for pattern, order in patterns:
        match = re.search(pattern, name)
        if not match:
            continue
        values = [int(value) if value else 0 for value in match.groups()]
        if order == "ymd":
            year, month, day, hour, minute, second = values
        else:
            month, day, year, hour, minute, second = values
        try:
            return datetime(year, month, day, hour, minute, second, tzinfo=timezone.utc)
        except ValueError:
            continue
    return None


def _file_timestamp(path: Path) -> datetime:
    named = _timestamp_from_name(path)
    if named is not None:
        return named
    return datetime.fromtimestamp(path.stat().st_mtime, tz=timezone.utc)


def _safe_filename(value: str) -> str:
    clean = re.sub(r"[^A-Za-z0-9._-]+", "-", value).strip(".-")
    return clean or "attachment"


def _author_for(path: Path, input_dir: Path) -> str:
    relative_parent = path.parent.relative_to(input_dir)
    if str(relative_parent) == ".":
        return "Imported"
    return relative_parent.parts[-1]


def collect_entries(input_dir: str | os.PathLike, output_dir: str | os.PathLike | None = None) -> list[ChatEntry]:
    source_root = Path(input_dir).expanduser().resolve()
    if not source_root.is_dir():
        raise ValueError(f"Input folder does not exist: {source_root}")
    excluded = Path(output_dir).expanduser().resolve() if output_dir else None
    groups: dict[tuple[str, str], list[Path]] = {}
    for path in source_root.rglob("*"):
        if not path.is_file() or path.suffix.lower() not in SUPPORTED_EXTENSIONS:
            continue
        resolved = path.resolve()
        if excluded is not None and (resolved == excluded or excluded in resolved.parents):
            continue
        relative = path.relative_to(source_root)
        key = (str(relative.parent).casefold(), path.stem.casefold())
        groups.setdefault(key, []).append(path)

    entries: list[ChatEntry] = []
    for files in groups.values():
        files.sort(key=lambda item: item.name.casefold())
        text_parts = [_read_text(path) for path in files if path.suffix.lower() in TEXT_EXTENSIONS]
        attachments = [path for path in files if path.suffix.lower() in IMAGE_EXTENSIONS]
        relative_files = [str(path.relative_to(source_root)) for path in files]
        digest = hashlib.sha256("\0".join(relative_files).encode("utf-8")).hexdigest()[:16]
        entries.append(ChatEntry(
            entry_id=digest,
            timestamp=min(_file_timestamp(path) for path in files),
            author=_author_for(files[0], source_root),
            text="\n\n".join(part for part in text_parts if part),
            source_files=relative_files,
            attachments=attachments,
        ))
    entries.sort(key=lambda entry: (entry.timestamp, entry.source_files[0].casefold()))
    return entries


def _copy_attachment(path: Path, source_root: Path, assets_dir: Path) -> str:
    relative = str(path.relative_to(source_root))
    prefix = hashlib.sha256(relative.encode("utf-8")).hexdigest()[:10]
    filename = f"{prefix}-{_safe_filename(path.name)}"
    destination = assets_dir / filename
    shutil.copy2(path, destination)
    return f"assets/{filename}"


def _render_html(records: list[dict], title: str) -> str:
    messages = []
    previous_day = None
    for record in records:
        stamp = datetime.fromisoformat(record["timestamp"])
        day = stamp.strftime("%B %d, %Y")
        if day != previous_day:
            messages.append(f'<div class="day"><span>{html.escape(day)}</span></div>')
            previous_day = day
        author = html.escape(record["author"])
        initial = html.escape((record["author"][:1] or "?").upper())
        text = html.escape(record["text"]).replace("\n", "<br>")
        attachment_html = "".join(
            f'<a href="{html.escape(asset)}" target="_blank">'
            f'<img loading="lazy" src="{html.escape(asset)}" alt="Saved attachment"></a>'
            for asset in record["attachments"]
        )
        sources = ", ".join(html.escape(value) for value in record["source_files"])
        messages.append(
            '<article class="message">'
            f'<div class="avatar">{initial}</div><div class="body">'
            f'<div><strong>{author}</strong><time>{stamp.strftime("%Y-%m-%d %H:%M UTC")}</time></div>'
            f'<div class="content">{text}</div><div class="attachments">{attachment_html}</div>'
            f'<details><summary>Original files</summary>{sources}</details></div></article>'
        )
    body = "\n".join(messages) or '<div class="empty">No supported files were found.</div>'
    return f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{html.escape(title)}</title>
<style>
:root{{--bg:#313338;--panel:#2b2d31;--text:#dbdee1;--muted:#949ba4;--link:#00a8fc}}
*{{box-sizing:border-box}} body{{margin:0;background:var(--bg);color:var(--text);font:16px/1.35 Arial,sans-serif}}
header{{position:sticky;top:0;padding:16px 24px;background:#1e1f22;border-bottom:1px solid #111;z-index:2}}
header h1{{font-size:18px;margin:0}} main{{max-width:1100px;margin:auto;padding:18px 0 60px}}
.message{{display:flex;gap:14px;padding:10px 24px}} .message:hover{{background:#2e3035}}
.avatar{{width:40px;height:40px;flex:0 0 40px;border-radius:50%;display:grid;place-items:center;background:#5865f2;color:white;font-weight:bold}}
.body{{min-width:0;max-width:920px}} strong{{color:white}} time{{font-size:12px;color:var(--muted);margin-left:8px}}
.content{{white-space:normal;margin-top:3px;overflow-wrap:anywhere}} .attachments{{display:flex;gap:8px;flex-wrap:wrap;margin-top:8px}}
.attachments img{{display:block;max-width:460px;max-height:520px;border-radius:8px;object-fit:contain;background:#1e1f22}}
.day{{display:flex;align-items:center;gap:10px;color:var(--muted);font-size:12px;margin:20px 24px 8px}}
.day:before,.day:after{{content:'';height:1px;background:#4e5058;flex:1}} details{{font-size:11px;color:var(--muted);margin-top:5px}}
.empty{{padding:40px;text-align:center;color:var(--muted)}}
</style></head><body><header><h1>{html.escape(title)}</h1></header><main>{body}</main></body></html>"""


def build_chat_archive(
    input_dir: str | os.PathLike,
    output_dir: str | os.PathLike | None = None,
) -> dict:
    source_root = Path(input_dir).expanduser().resolve()
    destination = (
        Path(output_dir).expanduser().resolve()
        if output_dir
        else source_root.parent / f"{source_root.name}_chat_export"
    )
    entries = collect_entries(source_root, destination)
    assets_dir = destination / "assets"
    assets_dir.mkdir(parents=True, exist_ok=True)

    records = []
    for entry in entries:
        assets = [_copy_attachment(path, source_root, assets_dir) for path in entry.attachments]
        records.append({
            "id": entry.entry_id,
            "timestamp": entry.timestamp.isoformat(),
            "author": entry.author,
            "text": entry.text,
            "attachments": assets,
            "source_files": entry.source_files,
        })

    with (destination / "messages.jsonl").open("w", encoding="utf-8", newline="\n") as target:
        for record in records:
            target.write(json.dumps(record, ensure_ascii=False) + "\n")

    transcript = []
    for record in records:
        transcript.append(f'[{record["timestamp"]}] {record["author"]}')
        if record["text"]:
            transcript.append(record["text"])
        transcript.extend(f'[Attachment: {value}]' for value in record["attachments"])
        transcript.append("")
    (destination / "transcript.txt").write_text("\n".join(transcript), encoding="utf-8")
    (destination / "index.html").write_text(
        _render_html(records, f"{source_root.name} — Imported Chat"), encoding="utf-8",
    )
    return {
        "input": str(source_root),
        "output": str(destination),
        "messages": len(records),
        "text_files": sum(path.lower().endswith(".txt") for record in records for path in record["source_files"]),
        "images": sum(len(record["attachments"]) for record in records),
    }


def _choose_folder() -> str | None:
    try:
        import tkinter as tk
        from tkinter import filedialog
    except ImportError:
        return None
    root = tk.Tk()
    root.withdraw()
    try:
        chosen = filedialog.askdirectory(title="Choose the folder containing saved sales text and images")
        return chosen or None
    finally:
        root.destroy()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Convert saved TXT and image files into a portable chat archive.")
    parser.add_argument("input", nargs="?", help="Folder containing the saved files; omit to use a folder picker")
    parser.add_argument("--output", help="Optional output folder")
    args = parser.parse_args(argv)
    input_dir = args.input or _choose_folder()
    if not input_dir:
        parser.error("No input folder was selected.")
    result = build_chat_archive(input_dir, args.output)
    print(f'Created {result["messages"]} chat entries from {result["text_files"]} text files and {result["images"]} images.')
    print(f'Open: {Path(result["output"]) / "index.html"}')
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
