"""Local, read-only RelicFrame health check. No Discord or market requests."""
from __future__ import annotations

import argparse
import asyncio
import importlib
import json
import os
import sys
from pathlib import Path

from companion_appraisal import CompanionAppraiser
from local_env import load_local_env
from state import DEFAULT_RELIC_CSV, BotState
from world_state import DEFAULT_ARBITRATION_PATH, WorldStateClient, load_arbitration_schedule


ROOT = Path(__file__).resolve().parent
load_local_env()


async def _live_checks(schedule) -> list[str]:
    """Make one world-state fetch and one small market fetch when explicitly requested."""
    from discord_world_state import CHANNELS, build_channel_embeds
    from http_client import WfmHttpClient

    results = []
    world_client = WorldStateClient()
    try:
        world = await world_client.fetch()
    finally:
        await world_client.close()
    rendered = sum(bool(build_channel_embeds(key, world, schedule)) for key in CHANNELS if key != "world-pings")
    results.append(
        f"Live world state: {world.get('mission_source', 'unknown')} mission source; "
        f"{rendered}/{len(CHANNELS) - 1} boards rendered"
    )

    async with WfmHttpClient(max_concurrent=1, max_per_second=5.0) as market_client:
        orders = await market_client.get_full_order_book("lith_a1_relic")
    results.append(f"Warframe Market: Lith A1 full order book returned {len(orders):,} orders")
    return results


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Read-only RelicFrame health check.")
    parser.add_argument(
        "--live", action="store_true",
        help="Also make one Warframe world-state request and one Warframe Market order-book request.",
    )
    args = parser.parse_args(argv)
    passed: list[str] = []
    warnings: list[str] = []
    errors: list[str] = []

    if sys.version_info >= (3, 11):
        passed.append(f"Python {sys.version.split()[0]}")
    else:
        errors.append(f"Python {sys.version.split()[0]} is too old; use 3.11 or newer")

    for module_name in ("requests", "aiohttp", "discord"):
        try:
            module = importlib.import_module(module_name)
        except ImportError as exc:
            errors.append(f"Missing dependency {module_name}: {exc}")
        else:
            passed.append(f"{module_name} {getattr(module, '__version__', 'installed')}")

    bot_state = BotState()
    try:
        relic_count = bot_state.load_relics()
    except Exception as exc:  # noqa: BLE001 - diagnostic should report every issue
        errors.append(f"Relic dataset failed to load: {exc}")
    else:
        malformed = [name for name, relic in bot_state.relics.items() if len(relic.rewards) != 6]
        vault_counts = {
            "vaulted": sum(relic.vaulted is True for relic in bot_state.relics.values()),
            "unvaulted": sum(relic.vaulted is False for relic in bot_state.relics.values()),
            "unknown": sum(relic.vaulted is None for relic in bot_state.relics.values()),
        }
        if malformed:
            errors.append(f"{len(malformed)} relics do not have six reward slots")
        else:
            passed.append(
                f"{relic_count:,} relics ({vault_counts['vaulted']} vaulted, "
                f"{vault_counts['unvaulted']} unvaulted, {vault_counts['unknown']} unknown)"
            )

    current = ROOT / "data" / "companion_current_market" / "price_evidence_deduplicated.jsonl"
    historical = ROOT / "data" / "companion_sales" / "price_evidence.jsonl"
    try:
        appraiser = CompanionAppraiser(current, historical)
    except Exception as exc:  # noqa: BLE001
        errors.append(f"Companion evidence failed to load: {exc}")
    else:
        sources = {source: 0 for source in ("current", "historical")}
        for row in appraiser.records:
            sources[row.get("_appraisal_source", "current")] += 1
        passed.append(
            f"{len(appraiser.records):,} companion price records "
            f"({sources['current']:,} current, {sources['historical']:,} historical)"
        )

    schedule_path = os.environ.get("ARBITRATION_SCHEDULE_PATH", DEFAULT_ARBITRATION_PATH)
    schedule = load_arbitration_schedule(schedule_path)
    if schedule:
        passed.append(f"{len(schedule):,} Arbitration schedule entries")
    else:
        warnings.append(f"No Arbitration schedule entries loaded from {schedule_path}")

    if args.live:
        try:
            passed.extend(asyncio.run(_live_checks(schedule)))
        except Exception as exc:  # noqa: BLE001 - turn network failures into a useful report
            errors.append(f"Live service check failed: {exc}")

    for filename in ("discord_list_state.json", "discord_world_state.json"):
        path = ROOT / filename
        if not path.exists():
            warnings.append(f"{filename} does not exist yet (created during Discord setup)")
            continue
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
            guilds = data.get("guilds", {})
            invalid_ids = [key for key in guilds if not str(key).isdigit()]
            if invalid_ids:
                warnings.append(f"{filename} has {len(invalid_ids)} invalid saved server ID(s)")
            else:
                passed.append(f"{filename}: valid ({len(guilds)} configured server(s))")
        except (OSError, json.JSONDecodeError, AttributeError) as exc:
            errors.append(f"{filename} is invalid: {exc}")

    if os.environ.get("DISCORD_BOT_TOKEN"):
        passed.append("Discord bot token is set (value hidden)")
    else:
        warnings.append("DISCORD_BOT_TOKEN is not set in this terminal")
    if os.environ.get("OPENAI_API_KEY"):
        warnings.append("Optional OpenAI vision is enabled; manual companion mode also remains available")
    else:
        passed.append("Companion manual mode needs no API key")

    network_note = "two small network checks" if args.live else "no network requests"
    print(f"RelicFrame diagnostics (read-only; {network_note})\n")
    for message in passed:
        print(f"[OK]   {message}")
    for message in warnings:
        print(f"[WARN] {message}")
    for message in errors:
        print(f"[FAIL] {message}")
    print(f"\nResult: {len(passed)} passed, {len(warnings)} warning(s), {len(errors)} failure(s).")
    print(f"Relic data: {DEFAULT_RELIC_CSV}")
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
