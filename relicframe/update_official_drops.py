"""Refresh RelicFrame's relic rewards from Digital Extremes' PC drop tables.

Run from any directory:
    python relicframe/update_official_drops.py --check
    python relicframe/update_official_drops.py

This makes one request to the public DE HTML export. It does not call
Warframe.market, and it does not change Riven weapon catalogs or vault status.
"""

import argparse
import csv
import os
from pathlib import Path
import re
import tempfile
from urllib.request import Request, urlopen

from parse_official_drops import build_standard_relics, parse_html, write_csv


OFFICIAL_URL = (
    "https://warframe-web-assets.nyc3.cdn.digitaloceanspaces.com/"
    "uploads/cms/hnfvc0o3jnfvc873njb03enrf56.html"
)
DEFAULT_CSV = Path(__file__).resolve().parent / "data" / "relics_from_official_data.csv"


def previous_vault_status(path: Path) -> dict[str, bool]:
    if not path.exists():
        return {}
    with path.open(newline="", encoding="utf-8-sig") as source:
        result = {}
        for row in csv.DictReader(source):
            value = row.get("vaulted", "").strip().lower()
            if value in ("true", "false"):
                result[row["relic_name"]] = value == "true"
        return result


def previous_relic_names(path: Path) -> set[str]:
    if not path.exists():
        return set()
    with path.open(newline="", encoding="utf-8-sig") as source:
        return {row["relic_name"] for row in csv.DictReader(source)}


def historical_relics(path: Path) -> dict[str, list[tuple[str, str]]]:
    """Retain previously catalogued rewards absent from the current DE page.

    DE can omit event/Resurgence relics from its current export. Their reward
    tables still matter for relics already owned by players.
    """
    if not path.exists():
        return {}
    result: dict[str, list[tuple[str, str]]] = {}
    with path.open(newline="", encoding="utf-8-sig") as source:
        for row in csv.DictReader(source):
            result.setdefault(row["relic_name"], []).append((row["reward_name"], row["rarity"]))
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="validate and report without changing the CSV")
    parser.add_argument("--output", type=Path, default=DEFAULT_CSV)
    parser.add_argument("--url", default=OFFICIAL_URL, help="official DE HTML URL or a local HTML file")
    args = parser.parse_args()

    if args.url.startswith(("https://", "http://")):
        request = Request(args.url, headers={"User-Agent": "RelicFrame drop-table updater"})
        with urlopen(request, timeout=60) as response:
            content = response.read().decode("utf-8-sig")
    else:
        content = Path(args.url).read_text(encoding="utf-8-sig")

    date = re.search(r"Last Update:</b>\s*([^<\r\n]+)", content, re.IGNORECASE)
    parsed = parse_html(content)
    standard, non_standard = build_standard_relics(parsed)
    if len(standard) < 700:
        raise ValueError(f"Only {len(standard)} standard relics found; refusing to replace the CSV")
    if any(len(rewards) != 6 for rewards in standard.values()):
        raise ValueError("A standard relic does not have exactly six rewards")

    target = args.output.resolve()
    previous = previous_vault_status(target)
    existing = previous_relic_names(target)
    added = sorted(set(standard) - existing)
    omitted = sorted(existing - set(standard))
    old_rewards = historical_relics(target)
    for name in omitted:
        if len(old_rewards[name]) == 6:
            standard[name] = old_rewards[name]
    print(f"Official update: {date.group(1).strip() if date else 'date unavailable'}")
    print(f"Standard relics: {len(standard)}; non-standard: {len(non_standard)}")
    print(f"Previously unknown relics: {len(added)}" + (f" ({', '.join(added[:30])})" if added else ""))
    print(f"Retained historical relics absent from this export: {len(omitted)}")
    if args.check:
        print("Check only; CSV unchanged.")
        return

    target.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=".official-relics-", suffix=".csv", dir=target.parent)
    os.close(fd)
    try:
        # Existing vault flags are carried forward. New flags remain blank:
        # this source publishes rewards, not whether a relic is vaulted.
        write_csv(standard, temporary, previous)
        os.replace(temporary, target)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    print(f"Updated {target}; restart the bot to load the new relic catalog.")


if __name__ == "__main__":
    main()
