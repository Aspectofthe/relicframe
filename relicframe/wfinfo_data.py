"""
wfinfo_data.py
Parses the community data feed WFInfo (https://github.com/WFCD/WFInfo) uses,
served from https://api.warframestat.us/wfinfo/*. This is the fallback/
convenience data source - prefer parse_official_drops.py against DE's own
export (warframe.com/droptables) when you have one, since that's the
primary source this feed is itself derived from.

filtered_items shape (relevant parts):
{
  "relics": {
    "Lith": { "A1": { "vaulted": true,
      "rare1": "...", "uncommon1": "...", "uncommon2": "...",
      "common1": "...", "common2": "...", "common3": "..." } }
  },
  "eqmt": {
    "Some Prime": { "parts": { "Some Prime Part": {"ducats": 45, ...} } }
  }
}
"""

from relic_data import Relic, RelicReward

_RARITY_PREFIXES = ("rare", "uncommon", "common")


def _rarity_from_key(key: str) -> str | None:
    for prefix in _RARITY_PREFIXES:
        if key.startswith(prefix):
            return prefix
    return None


def build_relics_from_filtered(filtered: dict) -> tuple[dict[str, Relic], dict[str, bool]]:
    """
    Returns (relics, vaulted): relic_name -> Relic (same shape as
    load_relics_from_csv), and relic_name -> bool vaulted status.

    Every Relic's own .vaulted field is set directly from this feed's data
    (not left at the default None/"unknown") - the separate `vaulted`
    return value exists alongside it only as a convenience for callers
    that want a quick vaulted-count summary without walking every Relic
    object individually.
    """
    relics: dict[str, Relic] = {}
    vaulted: dict[str, bool] = {}

    for era, era_relics in filtered.get("relics", {}).items():
        for code, info in era_relics.items():
            name = f"{era} {code}"
            relic = Relic(relic_name=name)
            for key, value in info.items():
                if key == "vaulted":
                    vaulted[name] = bool(value)
                    relic.vaulted = bool(value)
                    continue
                rarity = _rarity_from_key(key)
                if rarity and value:
                    relic.rewards.append(RelicReward(reward_name=str(value), rarity=rarity))
            relics[name] = relic

    return relics, vaulted


def build_ducat_map(filtered: dict) -> dict[str, int]:
    """part name (lowercase) -> ducat value, from the eqmt section."""
    ducats: dict[str, int] = {}
    for prime_info in filtered.get("eqmt", {}).values():
        for part_name, part_info in prime_info.get("parts", {}).items():
            d = part_info.get("ducats")
            if d is not None:
                ducats[part_name.strip().lower()] = int(d)
    return ducats


def write_relics_csv(relics: dict[str, Relic], vaulted: dict[str, bool], path: str) -> None:
    """
    Writes relics in this tool's standard CSV format, plus a vaulted
    column. Reads vaulted status from each Relic's own .vaulted field
    (not the separate `vaulted` dict, which is kept only for
    build_relics_from_filtered's summary-count convenience and can be
    missing entries that .vaulted correctly has as None) - True/False
    write as-is, None writes as a BLANK field, matching the "blank =
    unknown, not assumed False" convention used by parse_official_drops.py
    for the same column. Writing False for actually-unknown status would
    make a confident, wrong claim from data that doesn't support one; a
    reader loading this CSV back via load_relics_from_csv's
    _parse_vaulted() correctly treats a blank as unknown, but only if this
    writer actually leaves it blank instead of guessing False.
    """
    import csv

    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(["relic_name", "reward_name", "rarity", "vaulted"])
        for name, relic in relics.items():
            # relic.vaulted: True/False write as-is; None (unknown) writes
            # as an empty string, never as False - see the docstring above.
            vault_field = "" if relic.vaulted is None else relic.vaulted
            for reward in relic.rewards:
                writer.writerow([name, reward.reward_name, reward.rarity, vault_field])

