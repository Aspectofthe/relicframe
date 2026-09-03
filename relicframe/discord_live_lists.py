"""Persistent Discord channels for the live relic lists.

The channels are a presentation layer only: prices are always read from the
live in-memory order books. No price data is written to disk. Each configured
channel owns one persistent Discord message; refreshes edit that message
instead of posting a new list every cycle.
"""
from __future__ import annotations

import asyncio
import json
import os
import time
from dataclasses import dataclass

import discord

import analysis
import formatting as fmt
import order_math
from relic_data import REFINEMENTS

LIST_CHANNELS = {
    "best-overall": "Best Overall",
    "guaranteed-profit": "Guaranteed Profit",
    "expected-profit": "Expected Profit",
    "best-roi": "Best ROI",
    "best-plat-to-trace": "Best Plat/Trace",
    "cheapest": "Cheapest",
    "best-ducat-farming": "Best Ducat Farming",
}
LIST_CATEGORY_NAME = "THE LIST"
PAGE_SIZE = 15
STATE_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "discord_list_state.json")

# These are only used when the matching custom guild emoji is unavailable.
# Custom emoji names follow the server's existing convention, for example
# LithRelicIntact, MesoRelicFlawless, or AxiRelicRadiant.
RELIC_ERA_FALLBACK = {
    "Lith": "🟤",
    "Meso": "🟡",
    "Neo": "⚪",
    "Axi": "🟠",
    "Requiem": "🔴",
}


def _relic_era(relic_name: str) -> str:
    """Return the display era from a relic name such as ``Lith A1``."""
    first_word = relic_name.strip().split(maxsplit=1)[0] if relic_name.strip() else ""
    return first_word.capitalize()


def _relic_emoji_name(relic_name: str, refinement: str) -> str:
    """Build the exact custom emoji name used by the Discord server."""
    return f"{_relic_era(relic_name)}Relic{refinement.strip().capitalize()}"


def _relic_emoji(guild, relic_name: str, refinement: str) -> str:
    """Resolve a guild emoji by name, with an era-specific Unicode fallback."""
    expected_name = _relic_emoji_name(relic_name, refinement)
    for emoji in getattr(guild, "emojis", ()) if guild is not None else ():
        if getattr(emoji, "name", None) == expected_name:
            return str(emoji)
    return RELIC_ERA_FALLBACK.get(_relic_era(relic_name), "💠")


def _void_tear_emoji(guild) -> str:
    for emoji in getattr(guild, "emojis", ()) if guild is not None else ():
        if str(getattr(emoji, "name", "")).casefold() == "voidtear":
            return str(emoji)
    return "🌀"


def _load_state() -> dict:
    try:
        with open(STATE_PATH, "r", encoding="utf-8") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else {}
    except (FileNotFoundError, json.JSONDecodeError, OSError):
        return {}


def _save_state(data: dict) -> None:
    tmp = STATE_PATH + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)
    # os.replace() onto this path occasionally hits a transient
    # WinError 5 (Access is denied) on Windows when antivirus/indexing
    # briefly holds the destination file right after it's written. Retry
    # a few times with a short backoff instead of dropping the update.
    last_error: OSError | None = None
    for attempt in range(5):
        try:
            os.replace(tmp, STATE_PATH)
            return
        except OSError as exc:
            last_error = exc
            time.sleep(0.05 * (attempt + 1))
    raise last_error


def _seller(order: dict) -> str:
    return str((order.get("user") or {}).get("ingameName") or "Unknown seller")


def _seller_slug(order: dict) -> str | None:
    slug = (order.get("user") or {}).get("slug")
    return str(slug) if slug else None


def _profile_url(slug: str | None) -> str | None:
    if not slug:
        return None
    # The public WFM profile route is /profile/{user-slug}.
    return f"https://warframe.market/profile/{slug}"


def _purchase_text(seller: str, relic: str, price: float) -> str:
    return f"/w {seller} Hi! I want to buy: {relic} for {price:.1f} platinum. (warframe.market)"


class ListFilterModal(discord.ui.Modal, title="List filters"):
    max_cost = discord.ui.TextInput(label="Max relic cost (plat)", required=False, placeholder="Leave blank for no maximum")
    min_reward = discord.ui.TextInput(label="Min best reward (plat)", required=False, placeholder="Leave blank for no minimum")

    def __init__(self, manager: "LiveListManager", guild_id: int, channel_key: str):
        super().__init__()
        self.manager = manager
        self.guild_id = guild_id
        self.channel_key = channel_key
        cfg = manager._config(guild_id, channel_key)
        if cfg.get("max_cost") is not None:
            self.max_cost.default = str(cfg["max_cost"])
        if cfg.get("min_reward") is not None:
            self.min_reward.default = str(cfg["min_reward"])

    async def on_submit(self, interaction: discord.Interaction):
        def parse(value: str):
            value = value.strip()
            if not value:
                return None
            try:
                number = float(value)
                return number if number >= 0 else None
            except ValueError:
                return None

        await interaction.response.defer(ephemeral=True)
        cfg = self.manager._config(self.guild_id, self.channel_key)
        cfg["max_cost"] = parse(self.max_cost.value)
        cfg["min_reward"] = parse(self.min_reward.value)
        self.manager._save()
        await self.manager.update_channel(self.guild_id, self.channel_key)
        await interaction.followup.send("✅ Filters updated. The live list was refreshed.", ephemeral=True)


class DropsView(discord.ui.View):
    def __init__(self, manager: "LiveListManager", guild_id: int, channel_key: str, relic_name: str, refinement: str):
        super().__init__(timeout=120)
        self.manager = manager
        self.guild_id = guild_id
        self.channel_key = channel_key
        self.relic_name = relic_name
        self.refinement = refinement
        order, _matched = manager.state.live_market.best_online_relic_order(relic_name, refinement)
        if order:
            seller_slug = _seller_slug(order)
            profile = _profile_url(seller_slug)
            if profile:
                self.add_item(discord.ui.Button(label="Open WFM seller", style=discord.ButtonStyle.link, url=profile, row=1))


class LiveListView(discord.ui.View):
    def __init__(self, manager: "LiveListManager", guild_id: int, channel_key: str, page: int = 1,
                 rows: list[dict] | None = None):
        super().__init__(timeout=None)
        self.manager = manager
        self.guild_id = guild_id
        self.channel_key = channel_key
        self.page = page
        self.refinement = manager._config(guild_id, channel_key).get("refinement", "radiant")
        # Persisted, not just in-memory: without this, every refresh
        # (auto-refresh cycle, Prev/Next, a filter submit - anything that
        # rebuilds this view) constructs a brand-new LiveListView, which
        # would otherwise always start blank and silently drop whatever
        # relic was selected, even seconds after picking it.
        self.selected_relic: str | None = manager._config(guild_id, channel_key).get("selected_relic")
        # rows: pass already-computed rows when the caller has them (e.g.
        # update_channel, which needs the same rows for both the embed AND
        # this view) to avoid recomputing the full relic set a second time
        # - compute_all_rows over the whole tracked relic set isn't free,
        # and calling it twice per channel update, times 7 list channels,
        # times every configured guild, adds up to real wasted CPU time on
        # every single refresh cycle for no benefit. None still works
        # (computes fresh) for standalone construction.
        self._rebuild(rows=rows)

    def _rebuild(self, rows: list[dict] | None = None):
        self.clear_items()
        if rows is None:
            rows = self.manager.rows_for(self.guild_id, self.channel_key, self.refinement)
        total_pages = max(1, (len(rows) + PAGE_SIZE - 1) // PAGE_SIZE)
        self.page = max(1, min(self.page, total_pages))
        start = (self.page - 1) * PAGE_SIZE
        page_rows = rows[start:start + PAGE_SIZE]
        names = [r["relic"].relic_name for r in page_rows]
        if self.selected_relic not in names:
            self.selected_relic = names[0] if names else None

        select_options = [discord.SelectOption(label=n[:100], value=n, default=(n == self.selected_relic)) for n in names]
        if not select_options:
            # Discord rejects a select component with zero options ("Invalid
            # Form Body ... options: This field is required"), even when the
            # component is disabled. If the current filters match nothing,
            # fall back to a single placeholder option instead of an empty
            # list so the message edit itself doesn't fail.
            select_options = [discord.SelectOption(label="No relics match the current filters", value="__none__")]

        select = discord.ui.Select(
            placeholder="Select a relic...",
            options=select_options,
            row=1,
            disabled=not names,
        )
        async def select_cb(interaction: discord.Interaction):
            await interaction.response.defer()
            self.selected_relic = select.values[0]
            self.manager._config(self.guild_id, self.channel_key)["selected_relic"] = self.selected_relic
            await self.manager.update_channel(self.guild_id, self.channel_key, page=self.page)
        select.callback = select_cb
        self.add_item(select)

        filter_button = discord.ui.Button(label="Filters", style=discord.ButtonStyle.primary, row=0)
        async def filter_cb(interaction: discord.Interaction):
            await interaction.response.send_modal(ListFilterModal(self.manager, self.guild_id, self.channel_key))
        filter_button.callback = filter_cb
        self.add_item(filter_button)

        prev = discord.ui.Button(label="◀ Prev", style=discord.ButtonStyle.secondary, row=0, disabled=self.page <= 1)
        async def prev_cb(interaction: discord.Interaction):
            await interaction.response.defer()
            self.page = max(1, self.page - 1)
            self.selected_relic = None
            self.manager._config(self.guild_id, self.channel_key)["selected_relic"] = None
            await self.manager.update_channel(self.guild_id, self.channel_key, page=self.page)
        prev.callback = prev_cb
        self.add_item(prev)

        next_btn = discord.ui.Button(label="Next ▶", style=discord.ButtonStyle.secondary, row=0, disabled=self.page >= total_pages)
        async def next_cb(interaction: discord.Interaction):
            await interaction.response.defer()
            self.page = min(total_pages, self.page + 1)
            self.selected_relic = None
            self.manager._config(self.guild_id, self.channel_key)["selected_relic"] = None
            await self.manager.update_channel(self.guild_id, self.channel_key, page=self.page)
        next_btn.callback = next_cb
        self.add_item(next_btn)

        drops = discord.ui.Button(label="📦 Show drops", style=discord.ButtonStyle.secondary, row=2, disabled=self.selected_relic is None)
        async def drops_cb(interaction: discord.Interaction):
            if self.selected_relic:
                await self.manager.send_drops(interaction, self.guild_id, self.channel_key, self.selected_relic)
            else:
                await interaction.response.send_message("Select a relic first.", ephemeral=True)
        drops.callback = drops_cb
        self.add_item(drops)

        buy = discord.ui.Button(label="💬 /w seller", style=discord.ButtonStyle.success, row=2, disabled=self.selected_relic is None)
        async def buy_cb(interaction: discord.Interaction):
            if self.selected_relic:
                await self.manager.send_buy(interaction, self.guild_id, self.channel_key, self.selected_relic)
            else:
                await interaction.response.send_message("Select a relic first.", ephemeral=True)
        buy.callback = buy_cb
        self.add_item(buy)


class LiveListManager:
    def __init__(self, bot, state):
        self.bot = bot
        self.state = state
        self.data = _load_state()
        self.last_errors: list[str] = []
        self._unavailable_guilds_logged: set[int] = set()

    def _save(self):
        _save_state(self.data)

    def _guild(self, guild_id: int) -> dict:
        return self.data.setdefault("guilds", {}).setdefault(str(guild_id), {})

    def _prune_unavailable_guilds(self, keep_guild_id: int) -> list[int]:
        removed = []
        guilds = self.data.setdefault("guilds", {})
        get_guild = getattr(self.bot, "get_guild", None)
        if not callable(get_guild):
            return removed
        for saved_id in list(guilds):
            try:
                parsed_id = int(saved_id)
            except (TypeError, ValueError):
                parsed_id = None
            if parsed_id != keep_guild_id and (parsed_id is None or get_guild(parsed_id) is None):
                guilds.pop(saved_id, None)
                if parsed_id is not None:
                    removed.append(parsed_id)
                self._unavailable_guilds_logged.discard(parsed_id)
        return removed

    def _config(self, guild_id: int, channel_key: str) -> dict:
        guild = self._guild(guild_id)
        channels = guild.setdefault("channels", {})
        return channels.setdefault(channel_key, {
            "channel_id": None,
            "message_id": None,
            "refinement": "radiant",
            "max_cost": None,
            "min_reward": None,
            "page": 1,
            "selected_relic": None,
        })

    def waiting_embed(self, guild_id: int, channel_key: str) -> discord.Embed:
        cfg = self._config(guild_id, channel_key)
        refinement = cfg.get("refinement", "radiant")
        get_guild = getattr(self.bot, "get_guild", None)
        guild = get_guild(guild_id) if callable(get_guild) else None
        embed = discord.Embed(
            title=f"{_void_tear_emoji(guild)} {LIST_CHANNELS[channel_key]} · Vaulted Online Relics",
            description=(
                "Live market prices have not been loaded yet. An admin can run **`/relics refresh`** "
                "to perform the first bootstrap; this message will then update in place."
            ),
            color=fmt.EMBED_COLOR["white"],
        )
        embed.set_footer(text=f"Waiting for first price refresh · {refinement.capitalize()}")
        return embed

    async def setup_guild(self, guild: discord.Guild) -> str:
        removed_guilds = self._prune_unavailable_guilds(guild.id)
        existing_category = discord.utils.get(guild.categories, name=LIST_CATEGORY_NAME)
        category = existing_category or await guild.create_category(LIST_CATEGORY_NAME, reason="RelicFrame live relic lists")
        created = []
        for key in LIST_CHANNELS:
            cfg = self._config(guild.id, key)
            channel = guild.get_channel(cfg.get("channel_id") or 0)
            if not isinstance(channel, discord.TextChannel):
                channel = discord.utils.get(category.text_channels, name=key)
            if channel is None:
                channel = await guild.create_text_channel(key, category=category, reason="RelicFrame live relic list")
                created.append(key)
            cfg["channel_id"] = channel.id
            try:
                msg = await channel.fetch_message(cfg.get("message_id")) if cfg.get("message_id") else None
            except discord.HTTPException:
                msg = None
            if msg is None:
                if self.state.live_market is None:
                    msg = await channel.send(embed=self.waiting_embed(guild.id, key))
                else:
                    view = LiveListView(self, guild.id, key, cfg.get("page", 1))
                    msg = await channel.send(embed=self.build_embed(guild.id, key, cfg.get("page", 1)), view=view)
            cfg["message_id"] = msg.id
        self._guild(guild.id)["category_id"] = category.id
        self._save()
        await self.update_guild(guild.id)
        return (
            f"Configured **{len(LIST_CHANNELS)}** live channels in **{category.name}**."
            + (f" Created: {', '.join(created)}." if created else "")
            + (f" Removed {len(removed_guilds)} stale server configuration(s)." if removed_guilds else "")
        )

    def base_online_rows(self, refinement: str) -> list[dict]:
        """
        The expensive, PER-REFINEMENT-ONLY part of rows_for(): every
        relic's row at this refinement, restricted to confirmed-vaulted
        relics with a real currently-buyable online listing. Deliberately excludes any
        per-channel filtering (max_cost/min_reward) or ranking - those are
        cheap (sort/filter over an already-computed list) and differ per
        channel, whereas THIS part (analysis.compute_all_rows, the actual
        per-relic profitability math) is identical across every list
        channel that shares a refinement and is the part worth computing
        only once per refresh instead of once per channel.

        Computed fresh from the live in-memory order-book state every
        call - nothing here is cached across calls/refreshes. Calling this
        once and reusing the result across multiple channels WITHIN one
        refresh cycle is not price caching (see this module's own top-of-
        file docstring); it's avoiding redundant recomputation of the
        exact same numbers from the exact same already-fetched live data.
        """
        snap = self.state.build_snapshot(refinement)
        rows = analysis.compute_all_rows(snap, 0.0, "Online only")
        return [
            r for r in rows
            if r["relic"].vaulted is True
            and r.get("online_cost") is not None
            and not r.get("online_zero")
        ]

    async def base_online_rows_async(self, refinement: str) -> list[dict]:
        """Same as base_online_rows(), off the event loop thread - see
        rows_for_async's docstring for why this matters."""
        loop = asyncio.get_running_loop()
        return await loop.run_in_executor(None, lambda: self.base_online_rows(refinement))

    def filter_and_rank(self, base_rows: list[dict], guild_id: int, channel_key: str) -> list[dict]:
        """The cheap, PER-CHANNEL part of rows_for(): this channel's own
        max_cost/min_reward filters and its own rank-by mode, applied on
        top of already-computed base_rows. Sorting/filtering a few hundred
        already-computed dicts is negligible cost - safe to do per channel
        without an executor."""
        cfg = self._config(guild_id, channel_key)
        max_cost = cfg.get("max_cost")
        min_reward = cfg.get("min_reward")
        rows = base_rows
        if max_cost is not None:
            rows = [r for r in rows if r.get("online_cost") is not None and r["online_cost"] <= max_cost]
        if min_reward is not None:
            rows = [r for r in rows if r.get("odds", {}).get("price") is not None and r["odds"]["price"] >= min_reward]
        return analysis.rank_rows(rows, LIST_CHANNELS[channel_key], "Online only")

    def rows_for(self, guild_id: int, channel_key: str, refinement: str) -> list[dict]:
        """Convenience one-shot version (computes base_online_rows fresh
        every call) for standalone callers that only need ONE channel's
        rows and aren't part of a batched multi-channel refresh - e.g. a
        single page-navigation click. For a batched refresh across all
        list channels, compute base_online_rows_async() ONCE per distinct
        refinement and call filter_and_rank() per channel instead - see
        update_guild()."""
        return self.filter_and_rank(self.base_online_rows(refinement), guild_id, channel_key)

    async def rows_for_async(self, guild_id: int, channel_key: str, refinement: str) -> list[dict]:
        """Same as rows_for(), off the event loop thread. See
        base_online_rows_async's and rows_for's docstrings."""
        base_rows = await self.base_online_rows_async(refinement)
        return self.filter_and_rank(base_rows, guild_id, channel_key)

    def build_embed_from_rows(self, rows: list[dict], guild_id: int, channel_key: str, page: int) -> discord.Embed:
        """The actual embed-construction logic, given ALREADY-COMPUTED
        rows - callers that already have rows (e.g. update_channel, which
        needs the same rows for both the embed and the LiveListView)
        should use this directly instead of build_embed(), which
        recomputes rows itself and would otherwise cause the same relic
        set to be computed twice per update - see LiveListView's rows
        parameter docstring for the full explanation."""
        cfg = self._config(guild_id, channel_key)
        refinement = cfg.get("refinement", "radiant")
        total_pages = max(1, (len(rows) + PAGE_SIZE - 1) // PAGE_SIZE)
        page = max(1, min(page, total_pages))
        cfg["page"] = page
        start = (page - 1) * PAGE_SIZE
        page_rows = rows[start:start + PAGE_SIZE]
        lines = []
        get_guild = getattr(self.bot, "get_guild", None)
        guild = get_guild(guild_id) if callable(get_guild) else None
        for rank, row in enumerate(page_rows, start=start + 1):
            order, matched = self.state.live_market.best_online_relic_order(row["relic"].relic_name, refinement)
            seller = _seller(order) if order else "Unknown seller"
            qty = row.get("online_qty")
            qty_text = f" x{qty}" if qty is not None else ""
            fallback = " ~" if not matched else ""
            relic_emoji = _relic_emoji(guild, row["relic"].relic_name, refinement)
            lines.append(
                f"**{rank}. {relic_emoji} {row['relic'].relic_name}** · {row['online_cost']:.1f}p{qty_text}{fallback} · "
                f"EV profit {row['online_profit']['expected_profit']:+.1f}p · "
                f"ROI {row['online_profit']['expected_roi_pct']:.0f}% · "
                f"win {row['online_prob_profit_pct']:.0f}% · "
                f"risk {row['online_risk']}/100 · seller `{seller}`"
            )
        desc = "\n".join(lines) if lines else "No currently-online vaulted relic listings match these filters."
        embed = discord.Embed(
            title=f"{_void_tear_emoji(guild)} {LIST_CHANNELS[channel_key]} · Vaulted Online Relics",
            description=desc,
            color=fmt.category_color(page_rows[0]["online_cat"]) if page_rows else fmt.EMBED_COLOR["white"],
        )
        max_cost = cfg.get("max_cost")
        min_reward = cfg.get("min_reward")
        filter_text = [
            f"Refinement: **{refinement.capitalize()}**",
            "Vault: **Vaulted only**",
            "Seller: **Online only**",
            "Trace rate: **0**",
        ]
        filter_text.append(f"Max cost: **{max_cost:.1f}p**" if max_cost is not None else "Max cost: **Any**")
        filter_text.append(f"Min reward: **{min_reward:.1f}p**" if min_reward is not None else "Min reward: **Any**")
        embed.add_field(name="Filters", value=" · ".join(filter_text), inline=False)
        embed.set_footer(text=f"Page {page}/{total_pages} · {len(rows)} vaulted relics currently sold online · live data · refresh updates this message")
        return embed

    def build_embed(self, guild_id: int, channel_key: str, page: int) -> discord.Embed:
        """Convenience one-shot version for callers that don't already have
        rows computed (e.g. an initial standalone post). Prefer computing
        rows once via rows_for_async() and calling build_embed_from_rows()
        directly when you'll also need those same rows elsewhere (e.g.
        constructing a LiveListView) - see that method's docstring."""
        cfg = self._config(guild_id, channel_key)
        refinement = cfg.get("refinement", "radiant")
        rows = self.rows_for(guild_id, channel_key, refinement)
        return self.build_embed_from_rows(rows, guild_id, channel_key, page)

    async def update_channel(self, guild_id: int, channel_key: str, page: int | None = None,
                              base_rows: list[dict] | None = None):
        """
        base_rows: pass already-computed base_online_rows (see that
        method's docstring) when the caller is updating multiple channels
        in one refresh cycle and already computed them once for this
        refinement - see update_guild(). None (the default) computes them
        fresh for this one channel, which is correct for a standalone,
        single-channel-triggered update (e.g. a page-navigation click or a
        filter-modal submit).
        """
        cfg = self._config(guild_id, channel_key)
        if page is None:
            page = cfg.get("page", 1)
        channel = self.bot.get_channel(cfg.get("channel_id") or 0)
        if not isinstance(channel, discord.TextChannel):
            try:
                channel = await self.bot.fetch_channel(cfg.get("channel_id"))
            except Exception as exc:
                raise RuntimeError(f"Live-list channel {channel_key} is unavailable: {exc}") from exc
        if self.state.live_market is None:
            embed = self.waiting_embed(guild_id, channel_key)
            try:
                msg = await channel.fetch_message(cfg.get("message_id")) if cfg.get("message_id") else None
            except discord.HTTPException:
                msg = None
            if msg is None:
                msg = await channel.send(embed=embed)
                cfg["message_id"] = msg.id
            else:
                await msg.edit(embed=embed, view=None)
            self._save()
            return
        refinement = cfg.get("refinement", "radiant")
        if base_rows is None:
            base_rows = await self.base_online_rows_async(refinement)
        # filter_and_rank is cheap (sort/filter over already-computed
        # dicts) - safe to call per channel without an executor.
        rows = self.filter_and_rank(base_rows, guild_id, channel_key)
        embed = self.build_embed_from_rows(rows, guild_id, channel_key, page)
        view = LiveListView(self, guild_id, channel_key, cfg.get("page", 1), rows=rows)
        try:
            msg = await channel.fetch_message(cfg.get("message_id")) if cfg.get("message_id") else None
        except discord.HTTPException:
            msg = None
        if msg is None:
            msg = await channel.send(embed=embed, view=view)
            cfg["message_id"] = msg.id
        else:
            await msg.edit(embed=embed, view=view)
        self._save()

    async def update_guild(self, guild_id: int):
        """
        Updates all 7 list channels for one guild. The expensive part
        (base_online_rows: full per-relic profitability math for the
        whole tracked relic set) is computed ONCE PER DISTINCT REFINEMENT
        actually configured across this guild's channels - not once per
        channel - since channels sharing a refinement would otherwise
        recompute the exact same numbers from the exact same live data
        redundantly. In the common case (every channel left at the
        default refinement), that's one computation total for all 7
        channels, not seven. This grouping is scoped to THIS single call -
        nothing is persisted or reused on the NEXT refresh cycle, which
        always starts from a fresh live_market snapshot (see
        base_online_rows's docstring on why this isn't price caching).
        """
        self.last_errors = []
        if self.state.live_market is None:
            for key in LIST_CHANNELS:
                try:
                    await self.update_channel(guild_id, key)
                except Exception as exc:  # noqa: BLE001 - one channel must not block the rest
                    self.last_errors.append(f"{key}: {exc}")
            return self.last_errors

        refinements_needed: dict[str, list[str]] = {}
        for key in LIST_CHANNELS:
            refinement = self._config(guild_id, key).get("refinement", "radiant")
            refinements_needed.setdefault(refinement, []).append(key)

        base_rows_by_refinement: dict[str, list[dict]] = {}
        for refinement in refinements_needed:
            try:
                base_rows_by_refinement[refinement] = await self.base_online_rows_async(refinement)
            except Exception as exc:  # noqa: BLE001
                self.last_errors.append(f"{refinement}: {exc}")
                continue

        for refinement, keys in refinements_needed.items():
            base_rows = base_rows_by_refinement.get(refinement)
            if base_rows is None:
                continue
            for key in keys:
                try:
                    await self.update_channel(guild_id, key, base_rows=base_rows)
                except Exception as exc:  # noqa: BLE001
                    # One channel failing must not prevent the remaining six from updating.
                    self.last_errors.append(f"{key}: {exc}")
        return self.last_errors

    async def update_all(self):
        failures = []
        for guild_id in list(self.data.get("guilds", {})):
            try:
                parsed_guild_id = int(guild_id)
                get_guild = getattr(self.bot, "get_guild", None)
                if callable(get_guild) and get_guild(parsed_guild_id) is None:
                    if parsed_guild_id not in self._unavailable_guilds_logged:
                        print(
                            f"[live-lists] saved server {parsed_guild_id} is not available to this bot; "
                            "install the bot to that server, then run /relics setup-list."
                        )
                        self._unavailable_guilds_logged.add(parsed_guild_id)
                    continue
                self._unavailable_guilds_logged.discard(parsed_guild_id)
                failures.extend(await self.update_guild(parsed_guild_id) or [])
            except Exception as exc:  # noqa: BLE001
                failures.append(f"guild {guild_id}: {exc}")
        self.last_errors = failures
        if failures:
            raise RuntimeError(f"{len(failures)} live-list update(s) failed: {failures[0][:300]}")

    async def send_drops(self, interaction: discord.Interaction, guild_id: int, channel_key: str, relic_name: str):
        cfg = self._config(guild_id, channel_key)
        refinement = cfg.get("refinement", "radiant")
        snap = self.state.build_snapshot(refinement)
        relic = self.state.relics[relic_name]
        chances = relic.__class__.__module__  # only used to keep this method side-effect free
        # Use the same relic math used everywhere else for exact refinement odds.
        from relic_data import RARITY_CHANCE
        tier_chances = RARITY_CHANCE[refinement]
        lines = []
        for reward in relic.rewards:
            price = snap.prices.get(reward.reward_name.strip().lower())
            chance = tier_chances.get(reward.rarity, 0.0)
            lines.append(f"**{reward.reward_name}** · {reward.rarity.capitalize()} · {chance:.2f}% · {fmt.plat(price)}")
        embed = discord.Embed(
            title=f"📦 {relic_name} drops · {refinement.capitalize()}",
            description="\n".join(lines),
            color=fmt.BRAND_COLOR,
        )
        await interaction.response.send_message(embed=embed, ephemeral=True)

    async def send_buy(self, interaction: discord.Interaction, guild_id: int, channel_key: str, relic_name: str):
        cfg = self._config(guild_id, channel_key)
        refinement = cfg.get("refinement", "radiant")
        order, _matched = self.state.live_market.best_online_relic_order(relic_name, refinement)
        if order is None:
            await interaction.response.send_message("That online listing disappeared before you clicked it. Refreshing the list will pull the current seller.", ephemeral=True)
            return
        price = order_math.order_unit_price(order)
        if price is None:
            await interaction.response.send_message(
                "That listing no longer has a usable unit price. Refresh the list and try again.", ephemeral=True,
            )
            return
        seller = _seller(order)
        command = _purchase_text(seller, relic_name, price)
        embed = discord.Embed(title=f"💬 Buy {relic_name}", description=f"```text\n{command}\n```", color=fmt.SUCCESS_COLOR)
        embed.add_field(name="Seller", value=f"`{seller}`", inline=True)
        embed.add_field(name="Price", value=f"{price:.1f}p each", inline=True)
        qty = order.get("quantity")
        if qty is not None:
            embed.add_field(name="Available", value=f"x{qty}", inline=True)
        profile = _profile_url(_seller_slug(order))
        if profile:
            embed.add_field(name="Warframe Market", value=f"[Open seller profile]({profile})", inline=False)
        await interaction.response.send_message(embed=embed, ephemeral=True)

    async def setup_command(self, interaction: discord.Interaction):
        if interaction.guild is None:
            await interaction.response.send_message("This command must be used inside a server.", ephemeral=True)
            return
        if not interaction.user.guild_permissions.manage_channels:
            await interaction.response.send_message("You need **Manage Channels** to set up the live lists.", ephemeral=True)
            return
        await interaction.response.defer(ephemeral=True)
        try:
            result = await self.setup_guild(interaction.guild)
            await interaction.followup.send(result, ephemeral=True)
        except discord.Forbidden:
            await interaction.followup.send("I need **Manage Channels** and permission to send/edit messages in the category.", ephemeral=True)
