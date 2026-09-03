"""formatting.py - turns analysis.py's raw dicts into Discord-friendly text."""

from __future__ import annotations

import time

import discord

CATEGORY_EMOJI = {"green": "🟢", "yellow": "🟡", "red": "🔴", "white": "⚪"}
CATEGORY_LABEL = {
    "green": "Guaranteed Profit", "yellow": "Expected Only",
    "red": "Guaranteed Loss", "white": "Unknown",
}
VAULT_EMOJI = {True: "🟠", False: "🟢", None: "⚪"}
VAULT_LABEL = {True: "Vaulted", False: "Unvaulted", None: "Unknown"}

# ---------- Color palette (used so embeds carry the same green/yellow/red/
# grey signal as the emoji, visible at a glance in the Discord sidebar
# strip and in push notifications where emoji don't always render) ----------
EMBED_COLOR = {
    "green": discord.Color.from_rgb(59, 165, 93),
    "yellow": discord.Color.from_rgb(240, 186, 42),
    "red": discord.Color.from_rgb(217, 68, 68),
    "white": discord.Color.light_grey(),
}
BRAND_COLOR = discord.Color.from_rgb(88, 101, 242)   # neutral/info embeds (help, top rewards, in-progress)
SUCCESS_COLOR = EMBED_COLOR["green"]
ERROR_COLOR = EMBED_COLOR["red"]


def category_color(cat: str) -> discord.Color:
    return EMBED_COLOR.get(cat, BRAND_COLOR)


def risk_emoji(score: int | None) -> str:
    if score is None:
        return "⚪"
    if score <= 25:
        return "🟢"
    if score <= 60:
        return "🟡"
    return "🔴"


def freshness_color(age_seconds: float | None) -> discord.Color:
    """Green if the snapshot is recent, yellow if getting stale, red if old
    enough that prices may no longer be trustworthy."""
    if age_seconds is None:
        return ERROR_COLOR
    if age_seconds < 1800:
        return SUCCESS_COLOR
    if age_seconds < 7200:
        return EMBED_COLOR["yellow"]
    return ERROR_COLOR


def plat(v: float | None) -> str:
    return f"{v:.1f}p" if v is not None else "-"


def signed_plat(v: float | None) -> str:
    return f"{v:+.1f}p" if v is not None else "-"


def pct(v: float | None) -> str:
    return f"{v:.0f}%" if v is not None else "-"


def fmt_channel_cost(cost, qty, matched, outlier) -> str:
    if cost is None:
        return "-"
    s = f"{cost:.1f}p"
    if qty is not None:
        s += f" (x{qty})"
    if not matched:
        s += " ~"  # fallback tier, not an exact match
    if outlier:
        s += " ⚠"
    return s


def age_str(age_seconds: float | None) -> str:
    if age_seconds is None:
        return "no cache"
    if age_seconds < 60:
        return f"{age_seconds:.0f}s ago"
    if age_seconds < 3600:
        return f"{age_seconds / 60:.0f}m ago"
    return f"{age_seconds / 3600:.1f}h ago"


def row_summary_line(rank: int, row: dict, channel_scope: str) -> str:
    relic = row["relic"]
    odds = row["odds"]
    if channel_scope == "Online only":
        cat = row["online_cat"]
    elif channel_scope == "Offline only":
        cat = row["offline_cat"]
    else:
        cat = row["overall_category"]
    online = fmt_channel_cost(row["online_cost"], row["online_qty"], row["online_matched"], row["online_outlier"])
    offline = fmt_channel_cost(row["offline_cost"], row["offline_qty"], row["offline_matched"], row["offline_outlier"])
    return (
        f"**{rank}. {CATEGORY_EMOJI[cat]} {relic.relic_name}** {VAULT_EMOJI[relic.vaulted]}\n"
        f"　Online {online} · Offline {offline} · best reward **{odds['name'] or '?'}** ({plat(odds['price'])})\n"
        f"　Exp. profit online {signed_plat(row['online_profit']['expected_profit'])} "
        f"(x{row['online_batch']['n']}) · "
        f"offline {signed_plat(row['offline_profit']['expected_profit'])} "
        f"(x{row['offline_batch']['n']}) · "
        f"risk {risk_emoji(row['best_risk'])} {row['best_risk']}/100"
    )


def build_list_embed(rows_page: list[dict], page: int, total_pages: int, total_rows: int,
                      refinement: str, rank_by: str, channel_scope: str, snap,
                      title_emoji: str = "📊") -> discord.Embed:
    title = f"{title_emoji} Relics ranked by {rank_by} · {refinement.capitalize()}"
    desc_lines = [row_summary_line((page - 1) * 10 + i + 1, r, channel_scope) for i, r in enumerate(rows_page)]
    if rows_page:
        top_cat = (
            rows_page[0]["online_cat"] if channel_scope == "Online only"
            else rows_page[0]["offline_cat"] if channel_scope == "Offline only"
            else rows_page[0]["overall_category"]
        )
        color = category_color(top_cat)
    else:
        color = EMBED_COLOR["white"]
    embed = discord.Embed(
        title=title, description="\n\n".join(desc_lines) or "No relics match these filters.", color=color,
    )
    snap_age = None if snap is None else time.time() - snap.fetched_at
    embed.set_footer(
        text=f"Page {page}/{total_pages} · {total_rows} relic(s) match · prices as of {age_str(snap_age)}"
    )
    return embed


def build_detail_embed(row: dict, refinement: str, trace_rate: float) -> discord.Embed:
    relic = row["relic"]
    embed = discord.Embed(
        title=f"{CATEGORY_EMOJI[row['overall_category']]} {relic.relic_name} · {refinement.capitalize()}",
        description=f"Vault: {VAULT_EMOJI[relic.vaulted]} {VAULT_LABEL[relic.vaulted]}",
        color=category_color(row["overall_category"]),
    )
    for label, cost, qty, matched, outlier, profit, cat, risk, batch, prob_profit, prob_loss in (
        ("Online", row["online_cost"], row["online_qty"], row["online_matched"], row["online_outlier"],
         row["online_profit"], row["online_cat"], row["online_risk"], row.get("online_batch"),
         row["online_prob_profit_pct"], row["online_prob_loss_pct"]),
        ("Offline", row["offline_cost"], row["offline_qty"], row["offline_matched"], row["offline_outlier"],
         row["offline_profit"], row["offline_cat"], row["offline_risk"], row.get("offline_batch"),
         row["offline_prob_profit_pct"], row["offline_prob_loss_pct"]),
    ):
        cost_str = fmt_channel_cost(cost, qty, matched, outlier)
        batch_n = (batch or {}).get("n", 1)
        # Per-SINGLE-open odds (n=1), weighted across every reward slot by
        # its real rarity chance - always known once cost is, cheap
        # regardless of batch size. Distinct from the batch line below,
        # which is scaled to whatever quantity is actually listed (n).
        single_open_odds = (
            f"Single-open odds: {pct(prob_profit)} win chance / {pct(prob_loss)} loss chance\n"
            if profit["price_known"] else ""
        )
        # The batch's own loss-chance across all n units is intentionally
        # not computed here for arbitrary n (see relic_row.py's comment on
        # why) - show it only when it happens to be known, and point at
        # the exact Buy-N calculator otherwise instead of formatting a
        # None (which crashed here previously).
        batch_loss_chance = (batch or {}).get("prob_loss_pct")
        batch_loss_str = (
            f"{batch_loss_chance:.1f}%" if batch_loss_chance is not None
            else "see `/relics buy-n` for exact odds at this quantity"
        )
        value = (
            f"{CATEGORY_EMOJI[cat]} {CATEGORY_LABEL[cat]}\n"
            f"Cost: {cost_str}\n"
            f"Exp. value: {plat(profit['expected_value'])} · Exp. profit: {signed_plat(profit['expected_profit'])} "
            f"({pct(profit['expected_roi_pct'])} ROI)\n"
            f"{single_open_odds}"
            f"Batch ({batch_n}): total cost {plat((batch or {}).get('total_cost'))} · "
            f"exp. profit {signed_plat((batch or {}).get('expected_profit'))} · "
            f"loss chance {batch_loss_str}\n"
            f"Worst case: {plat(profit['worst_case_value'])} · profit {signed_plat(profit['worst_case_profit'])} "
            f"({pct(profit['worst_case_roi_pct'])} ROI)\n"
            f"Risk score: {risk_emoji(risk)} {risk}/100"
        )
        embed.add_field(name=label, value=value, inline=True)

    odds = row["odds"]
    if odds["name"]:
        exp_open = f"{odds['expected_openings']:.1f}" if odds["expected_openings"] else "-"
        embed.add_field(
            name="Best reward",
            value=(
                f"**{odds['name']}** ({odds['rarity']} slot)\n"
                f"Price: {plat(odds['price'])} · chance {odds['chance_pct']:.2f}% "
                f"({exp_open} exp. openings, {odds['chance_within_10']:.0f}% within 10 opens)"
            ),
            inline=False,
        )

    ppt = row["plat_per_trace"]
    ducat_info = row["ducat_info"]
    best_ducat = row["best_ducat_reward"]
    footer_bits = []
    if ppt is not None:
        footer_bits.append(f"Plat/trace: {ppt:.3f}p")
    if ducat_info["ducats_per_plat"] is not None:
        footer_bits.append(
            f"Ducats: {ducat_info['expected_ducats']:.1f} exp/open · "
            f"{ducat_info['plat_per_ducat']:.2f}p per ducat"
        )
    if best_ducat:
        footer_bits.append(f"Best ducat reward: {best_ducat['name']} ({best_ducat['ducats']}d)")
    if footer_bits:
        embed.add_field(name="Efficiency", value="\n".join(footer_bits), inline=False)

    return embed


def build_top_rewards_embed(relic, refinement: str, prices: dict, n: int = 6) -> discord.Embed:
    rows = relic.top_rewards(refinement, prices, n=n)
    lines = [f"{r['name']} · {r['rarity']} · {plat(r['price'])} · {r['chance_pct']:.2f}%" for r in rows]
    return discord.Embed(
        title=f"💎 Top rewards · {relic.relic_name} ({refinement.capitalize()})",
        description="\n".join(lines) or "No reward data.",
        color=BRAND_COLOR,
    )
