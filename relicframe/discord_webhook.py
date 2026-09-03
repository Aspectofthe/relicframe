"""Publish RelicFrame market analysis through a Discord webhook.

The webhook URL is read only from DISCORD_WEBHOOK_URL. It is deliberately
not accepted as a command-line argument so it is less likely to be saved in
shell history, IDE screenshots, or process lists.

Examples:
    python discord_webhook.py --test-webhook
    python discord_webhook.py
    python discord_webhook.py --interval-minutes 10 --green-only
"""

from __future__ import annotations

import argparse
import asyncio
import os
import time

import aiohttp
import discord

import analysis
import formatting as fmt
from relic_data import REFINEMENTS
from local_env import load_local_env
from state import state

load_local_env()

WEBHOOK_URL_ENV = "DISCORD_WEBHOOK_URL"
CHANNEL_SCOPES = ("Online only", "Offline only", "Online + Offline")
VAULT_FILTERS = ("All", "Unvaulted", "Vaulted", "Unknown")


def _limit_value(value: str) -> int:
    parsed = int(value)
    if not 1 <= parsed <= 10:
        raise argparse.ArgumentTypeError("limit must be from 1 to 10")
    return parsed


def _non_negative_float(value: str) -> float:
    parsed = float(value)
    if parsed < 0:
        raise argparse.ArgumentTypeError("value cannot be negative")
    return parsed


def _positive_float(value: str) -> float:
    parsed = float(value)
    if parsed <= 0:
        raise argparse.ArgumentTypeError("value must be greater than zero")
    return parsed


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Post the current RelicFrame rankings to a Discord webhook.",
    )
    parser.add_argument(
        "--test-webhook",
        action="store_true",
        help="Post a small connection test without fetching market data.",
    )
    parser.add_argument("--relic-csv", help="Optional full path to the relic CSV.")
    parser.add_argument(
        "--refinement", choices=REFINEMENTS, default="radiant",
        help="Relic refinement tier (default: radiant).",
    )
    parser.add_argument(
        "--rank-by", choices=analysis.RANK_MODES, default="Best Overall",
        help="Ranking mode used for the webhook list.",
    )
    parser.add_argument(
        "--channel-scope", choices=CHANNEL_SCOPES, default="Online + Offline",
        help="Which seller-presence groups are included.",
    )
    parser.add_argument("--vault", choices=VAULT_FILTERS, default="All")
    parser.add_argument("--min-roi", type=float)
    parser.add_argument("--max-cost", type=_non_negative_float)
    parser.add_argument("--min-reward", type=_non_negative_float)
    parser.add_argument("--trace-rate", type=_non_negative_float, default=0.0)
    parser.add_argument("--green-only", action="store_true")
    parser.add_argument(
        "--limit", type=_limit_value, default=10,
        help="Number of ranked relics in the message, from 1 to 10.",
    )
    parser.add_argument(
        "--interval-minutes", type=_non_negative_float, default=0.0,
        help="Continuously update one webhook message at this interval; 0 posts once.",
    )
    parser.add_argument(
        "--message-id", type=int,
        help="Edit an existing message created by this webhook instead of posting a new one.",
    )
    parser.add_argument(
        "--max-per-second", type=_positive_float, default=5.0,
        help="Maximum Warframe Market request starts per second (default: 5).",
    )
    parser.add_argument(
        "--max-concurrent", type=_limit_value, default=10,
        help="Maximum concurrent market requests, from 1 to 10 (default: 10).",
    )
    parser.add_argument("--force-catalog-refresh", action="store_true")
    parser.add_argument("--enable-websocket", action="store_true")
    parser.add_argument("--username", default="RelicFrame")
    return parser


def select_rows(snapshot, args: argparse.Namespace) -> list[dict]:
    rows = analysis.compute_all_rows(snapshot, args.trace_rate, args.channel_scope)
    rows = [
        row for row in rows
        if analysis.passes_filters(
            row,
            args.channel_scope,
            vault_filter=args.vault,
            min_roi=args.min_roi,
            max_cost=args.max_cost,
            min_reward=args.min_reward,
            guaranteed_only=args.green_only,
        )
    ]
    return analysis.rank_rows(rows, args.rank_by, args.channel_scope)


def build_ranking_embed(snapshot, rows: list[dict], args: argparse.Namespace) -> discord.Embed:
    selected = rows[: args.limit]
    embed = fmt.build_list_embed(
        selected,
        page=1,
        total_pages=1,
        total_rows=len(rows),
        refinement=args.refinement,
        rank_by=args.rank_by,
        channel_scope=args.channel_scope,
        snap=snapshot,
    )
    embed.set_author(name="RelicFrame market report")
    return embed


def _status_content(snapshot, shown: int, total: int, args: argparse.Namespace) -> str:
    generated = int(time.time())
    return (
        f"**RelicFrame update** - showing {shown} of {total} matching relics "
        f"({args.refinement.capitalize()}, {args.channel_scope}) - <t:{generated}:R>"
    )


async def _send_or_edit(
    webhook: discord.Webhook,
    message_id: int | None,
    content: str,
    embed: discord.Embed,
    username: str,
) -> int:
    allowed_mentions = discord.AllowedMentions.none()
    if message_id is not None:
        try:
            message = await webhook.edit_message(
                message_id,
                content=content,
                embed=embed,
                allowed_mentions=allowed_mentions,
            )
            return message.id
        except discord.NotFound:
            print("The saved webhook message no longer exists; posting a new one.")

    message = await webhook.send(
        content=content,
        embed=embed,
        username=username,
        allowed_mentions=allowed_mentions,
        wait=True,
    )
    return message.id


async def _post_connection_test(webhook: discord.Webhook, username: str) -> None:
    embed = discord.Embed(
        title="RelicFrame webhook connected",
        description="The webhook is configured correctly. No market data was fetched.",
        color=fmt.SUCCESS_COLOR,
    )
    await webhook.send(
        embed=embed,
        username=username,
        allowed_mentions=discord.AllowedMentions.none(),
        wait=True,
    )
    print("Webhook connection test posted successfully.")


async def run(args: argparse.Namespace) -> None:
    webhook_url = os.environ.get(WEBHOOK_URL_ENV, "").strip()
    if not webhook_url:
        raise SystemExit(
            f"Set {WEBHOOK_URL_ENV} in your PyCharm run configuration first. "
            "Do not paste the webhook URL into the source code."
        )

    try:
        async with aiohttp.ClientSession() as session:
            webhook = discord.Webhook.from_url(webhook_url, session=session)
            if args.test_webhook:
                await _post_connection_test(webhook, args.username)
                return

            count = state.load_relics(args.relic_csv)
            print(f"Loaded {count} relics. Starting the live market bootstrap...")

            last_progress = -1

            def progress(done: int, total: int) -> None:
                nonlocal last_progress
                percent = int((done / total) * 100) if total else 100
                if percent == 100 or percent >= last_progress + 5:
                    print(f"Market bootstrap: {done}/{total} ({percent}%)")
                    last_progress = percent

            await state.ensure_live_market_started(
                force_catalog_refresh=args.force_catalog_refresh,
                progress_cb=progress,
                max_concurrent=args.max_concurrent,
                max_per_second=args.max_per_second,
                enable_websocket=args.enable_websocket,
            )

            message_id = args.message_id
            while True:
                snapshot = state.build_snapshot(args.refinement)
                rows = select_rows(snapshot, args)
                embed = build_ranking_embed(snapshot, rows, args)
                message_id = await _send_or_edit(
                    webhook,
                    message_id,
                    _status_content(snapshot, min(args.limit, len(rows)), len(rows), args),
                    embed,
                    args.username,
                )
                print(f"Webhook message updated successfully. Message ID: {message_id}")

                if args.interval_minutes <= 0:
                    break
                await asyncio.sleep(args.interval_minutes * 60)
    finally:
        if state.live_market is not None:
            await state.live_market.stop()
            state.live_market = None


def main() -> None:
    args = build_parser().parse_args()
    try:
        asyncio.run(run(args))
    except KeyboardInterrupt:
        print("Webhook publisher stopped.")
    except (aiohttp.ClientError, discord.HTTPException, ValueError) as exc:
        raise SystemExit(f"Webhook publisher failed: {exc}") from exc


if __name__ == "__main__":
    main()
