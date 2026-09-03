"""
parse_official_drops.py
Parses the official Digital Extremes drop-table export (the "Last Update:
<date>" .txt file from warframe.com/droptables) into this tool's CSV
format. This is the PRIMARY data source - DE's own numbers, not a
third-party copy of them.

Why percentages, not DE's printed word: DE's own text only uses two rarity
words ("Uncommon" and "Rare") to describe what this tool treats as three
slot types (common/uncommon/rare - 3/2/1 slots). At Intact refinement DE
prints BOTH the 25.33% slots and the 11.00% slots as "Uncommon" - only the
2.00% slot says "Rare". Trusting the word would misclassify the 25.33%
slots as uncommon instead of common. This matches the numeric percentage
against RARITY_CHANCE (relic_data.py) instead, which is the real source of
truth already used everywhere else in this tool.

Usage:
    python parse_official_drops.py path/to/Last_Update_*.txt [vault_status_doc.txt]
Output:
    relics_from_official_data.csv   (standard 6-slot relics, with a
                                      'vaulted' column if the optional
                                      vault-status doc is provided)
    non_standard_relics.txt         (anything that didn't fit - see below)
"""

import csv
import re
import sys

from relic_data import RARITY_CHANCE

HEADER_RE = re.compile(
    r"^(Lith|Meso|Neo|Axi|Requiem)(?:\s+(\S+))?\s+Relic\s+\((Intact|Exceptional|Flawless|Radiant)\)\s*$"
)
REWARD_RE = re.compile(r"^(.+?)\t(?:Very Common|Common|Uncommon|Rare|Ultra Rare|Legendary)\s*\(([\d.]+)%\)\s*$")

# How close a printed percentage needs to be to one of our known slot
# chances to count as a match - DE rounds to 2 decimals, our table does
# too, so this only needs to absorb float noise.
TOLERANCE = 0.05


def parse_file(path: str) -> dict[str, dict[str, list[tuple[str, float]]]]:
    """Returns {relic_name: {refinement: [(reward_name, percent), ...]}}."""
    relics: dict[str, dict[str, list[tuple[str, float]]]] = {}
    current_key = None
    current_tier = None

    with open(path, "r", encoding="utf-8", errors="replace") as f:
        for raw_line in f:
            line = raw_line.rstrip("\r\n")
            header = HEADER_RE.match(line)
            if header:
                era, code, tier = header.groups()
                name = f"{era} {code}".strip() if code else era
                current_key = name
                current_tier = tier.lower()
                relics.setdefault(current_key, {}).setdefault(current_tier, [])
                continue

            if current_key is None:
                continue

            reward = REWARD_RE.match(line)
            if reward:
                item_name, pct_str = reward.groups()
                relics[current_key][current_tier].append((item_name.strip(), float(pct_str)))
            else:
                current_key = None
                current_tier = None

    return relics


def classify_rarity(percent: float, refinement: str) -> str | None:
    """Matches a printed percentage back to our common/uncommon/rare slot chance."""
    for rarity, chance in RARITY_CHANCE[refinement].items():
        if abs(chance - percent) <= TOLERANCE:
            return rarity
    return None


def build_standard_relics(parsed: dict) -> tuple[dict[str, list[tuple[str, str]]], list[str]]:
    """
    Returns (standard, non_standard). standard: relic_name -> [(reward_name,
    rarity), ...] for relics fitting the normal 6-slot model at Intact.
    non_standard: relic names that didn't fit (wrong slot count, or a
    percentage matching no known slot chance) - need manual handling.
    """
    standard = {}
    non_standard = []

    for relic_name, tiers in parsed.items():
        intact = tiers.get("intact")
        if not intact or len(intact) != 6:
            non_standard.append(relic_name)
            continue

        rewards = []
        ok = True
        for item_name, pct in intact:
            rarity = classify_rarity(pct, "intact")
            if rarity is None:
                ok = False
                break
            rewards.append((item_name, rarity))

        if ok:
            standard[relic_name] = rewards
        else:
            non_standard.append(relic_name)

    return standard, non_standard


def write_csv(
    standard: dict[str, list[tuple[str, str]]], path: str, vaulted: dict[str, bool] | None = None
) -> None:
    include_vaulted = vaulted is not None
    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        header = ["relic_name", "reward_name", "rarity"]
        if include_vaulted:
            header.append("vaulted")
        writer.writerow(header)
        for relic_name, rewards in standard.items():
            for reward_name, rarity in rewards:
                row = [relic_name, reward_name, rarity]
                if include_vaulted:
                    row.append(vaulted.get(relic_name, ""))  # blank = unknown, not assumed False
                writer.writerow(row)


def main():
    if len(sys.argv) < 2:
        print("Usage: python parse_official_drops.py path/to/Last_Update_*.txt [vault_status_doc.txt]")
        sys.exit(1)

    path = sys.argv[1]
    vault_doc_path = sys.argv[2] if len(sys.argv) > 2 else None

    print(f"Parsing {path} ...")
    parsed = parse_file(path)
    print(f"Found {len(parsed)} relic name(s) with at least one refinement block.")

    standard, non_standard = build_standard_relics(parsed)

    vaulted = None
    if vault_doc_path:
        from vault_status import parse_vault_status
        vaulted = parse_vault_status(vault_doc_path)
        matched = sum(1 for name in standard if name in vaulted)
        print(f"Matched vault status for {matched}/{len(standard)} relics from {vault_doc_path}")

    write_csv(standard, "relics_from_official_data.csv", vaulted)

    with open("non_standard_relics.txt", "w", encoding="utf-8") as f:
        for name in sorted(non_standard):
            f.write(name + "\n")

    print(f"Wrote {len(standard)} standard relics to relics_from_official_data.csv")
    if non_standard:
        print(f"{len(non_standard)} relic(s) didn't fit the standard 6-slot model - "
              f"see non_standard_relics.txt (e.g. Requiem Eterna, which has 8 slots "
              f"and no refinement tiers at all).")
    print()
    print("Sanity check: expect somewhere around 768-773 standard relics. If far off, "
          "double check the header regex still matches this export's format.")


if __name__ == "__main__":
    main()

