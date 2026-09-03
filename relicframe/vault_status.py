"""
vault_status.py
Parses the reference doc listing which relics are currently unvaulted
("Unvaulted/Available Relics" section) vs vaulted ("Vaulted/Unavailable
Relics" section) - real vault status, not inferred from whether someone
happens to be selling it right now.

Usage as a library:
    from vault_status import parse_vault_status
    vaulted = parse_vault_status("path/to/doc.txt")  # {relic_name: bool}
"""

import re

RELIC_NAME_RE = re.compile(r"^(Lith|Meso|Neo|Axi|Requiem)\s+[A-Za-z0-9]+$")


def parse_vault_status(path: str) -> dict[str, bool]:
    """
    Returns {relic_name: is_vaulted}. Relics not found in either section at
    all simply won't be present in the returned dict - callers should treat
    a missing key as "unknown," not silently assume either status.
    """
    vaulted: dict[str, bool] = {}
    section = None  # None, "unvaulted", or "vaulted"

    with open(path, "r", encoding="utf-8", errors="replace") as f:
        for raw_line in f:
            line = raw_line.strip()
            if not line:
                continue
            if "Unvaulted/Available Relics" in line:
                section = "unvaulted"
                continue
            if "Vaulted/Unavailable Relics" in line:
                section = "vaulted"
                continue
            if section is None:
                continue
            if RELIC_NAME_RE.match(line):
                vaulted[line] = (section == "vaulted")

    return vaulted


def apply_vault_status(relics: dict, vaulted: dict[str, bool]) -> int:
    """
    Sets .vaulted on each Relic object in `relics` (relic_data.Relic
    instances) from the parsed status map, in place. Returns how many
    relics were matched and updated, so callers can sanity-check coverage.
    """
    matched = 0
    for name, relic in relics.items():
        if name in vaulted:
            relic.vaulted = vaulted[name]
            matched += 1
    return matched

