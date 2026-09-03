"""
fetch_relic_data.py
Downloads the complete relic drop-table data from the community feed
WFInfo uses. Fallback/convenience option - prefer parse_official_drops.py
against DE's own export when you have one.

Run with:  python fetch_relic_data.py
Output:    relics_from_wfinfo.csv
"""

import wfm_api
from wfinfo_data import build_relics_from_filtered, write_relics_csv

OUTPUT_PATH = "relics_from_wfinfo.csv"


def main():
    print("Downloading relic drop-table data...")
    filtered = wfm_api.get_filtered_items()

    relics, vaulted = build_relics_from_filtered(filtered)
    write_relics_csv(relics, vaulted, OUTPUT_PATH)

    vaulted_count = sum(1 for v in vaulted.values() if v)
    print(f"Wrote {len(relics)} relics ({vaulted_count} vaulted) to {OUTPUT_PATH}")
    print("Load this file in the app with 'Load Relic CSV'.")
    print()
    print("Sanity check: expect somewhere around 768-773 total relics "
          "(vaulted + unvaulted combined). If far off, the feed may be stale.")


if __name__ == "__main__":
    main()

