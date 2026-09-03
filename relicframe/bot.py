

"""
bot.py
Discord bot exposing RelicFrame's full feature set (filters, rank-by modes,
refinement comparison, N-relic buy calculator, ducat farming, item search,
odds tables) as slash commands, built on top of the same relic_data.py /
wfm_api.py / item_catalog_cache.py the desktop app uses - so numbers never
disagree between the two.

Setup:
    pip install -r requirements.txt
    export DISCORD_BOT_TOKEN=...              # from the Discord Developer Portal
    export RELIC_CSV_PATH=relics_from_official_data.csv   # optional, this is the default
    python bot.py

Pricing runs on the live-market layer (live_market.py): the FIRST
`/relics refresh` bootstraps a complete order book for every unique
relic/reward item concurrently (seconds to low tens of seconds for the
full relic set, not the old pipeline's several-minutes-per-refresh - see
live_market.py's module docstring for the full explanation), then stays
current in the background via a WebSocket feed and a continuous
reconciliation sweep. Every `/relics refresh` after the first is
effectively instant - it just rebuilds a Snapshot from whatever the
live market already has in memory.
"""

from __future__ import annotations

import argparse
import asyncio
import hashlib
import os
import time
from pathlib import Path

import discord
from discord import app_commands
from discord.ext import commands

import analysis
import wfm_api
from relic_data import (
    REFINEMENTS, find_relics_for_reward, cheapest_combination_cost,
    chance_of_at_least_one,
)
from state import state
import formatting as fmt
from companion_appraisal import CompanionAppraiser
from companion_guide import companion_guide_embeds
from companion_vision import (
    NATURAL_COLORS, CompanionVisionError, OpenAICompanionVision, rarity_from_colors,
)
from discord_live_lists import LiveListManager
from discord_world_state import (
    WorldStateManager, arbitration_embed, cascade_embed,
)
from local_env import load_local_env
from discord_automation import RefreshController, GuildAutomation, SingleRefreshOutput
from riven_market import (
    RivenDeal, RivenMarketService, auction_price, display_stat, find_riven_deals, format_roll_stat,
    disposition_band, fmt_platinum, human_age,
    parse_weekly, riven_stat_class, top_weekly_weapons,
)
from riven_trade_chat import RivenTradeChatLog, price_summary

load_local_env()

REFINEMENT_CHOICES = [app_commands.Choice(name=r.capitalize(), value=r) for r in REFINEMENTS]
CHANNEL_CHOICES = [
    app_commands.Choice(name="Online + Offline", value="Online + Offline"),
    app_commands.Choice(name="Online only", value="Online only"),
    app_commands.Choice(name="Offline only", value="Offline only"),
]
VAULT_CHOICES = [
    app_commands.Choice(name="Any", value="Any"),
    app_commands.Choice(name="Unvaulted", value="Unvaulted"),
    app_commands.Choice(name="Vaulted", value="Vaulted"),
    app_commands.Choice(name="Unknown", value="Unknown"),
]
RANK_CHOICES = [app_commands.Choice(name=m, value=m) for m in analysis.RANK_MODES]
OBJECTIVE_CHOICES = [
    app_commands.Choice(name="Expected Profit", value="expected_profit"),
    app_commands.Choice(name="ROI", value="expected_roi_pct"),
    app_commands.Choice(name="Guaranteed (Worst-Case) Profit", value="worst_case_profit"),
    app_commands.Choice(name="Chance of Profit", value="chance_of_profit_pct"),
]
AUTO_CHOICES = [
    app_commands.Choice(name="Start recurring auto-refresh", value="on"),
    app_commands.Choice(name="Stop recurring auto-refresh", value="off"),
    app_commands.Choice(name="Check auto-refresh status", value="status"),
]
PAGE_SIZE = 8
REFRESH_COOLDOWN_SECONDS = 120
KUBROW_BUILD_CHOICES = [app_commands.Choice(name=value.capitalize(), value=value) for value in ("skinny", "athletic", "bulky", "not applicable")]
KUBROW_PATTERN_CHOICES = [app_commands.Choice(name=value.capitalize(), value=value) for value in ("striped", "patchy", "hound", "domino", "merle", "lotus", "hyacinth")]
KUBROW_BREED_CHOICES = [app_commands.Choice(name=value.capitalize(), value=value) for value in ("chesa", "sunika", "huras", "raksa", "sahasa", "smeeta", "adarza", "vasca")]
KUBROW_RARITY_CHOICES = [
    app_commands.Choice(name=value.title(), value=value) for value in (
        "common", "uncommon", "single rare", "double rare", "double same rare", "triple rare",
        "solid common", "solid uncommon", "solid rare", "quad rare", "quad solid",
    )
]
COMPANION_SPECIES_CHOICES = [
    app_commands.Choice(name="Kubrow", value="kubrow"),
    app_commands.Choice(name="Kavat", value="kavat"),
]
COMPANION_COLOR_CHOICES = [
    app_commands.Choice(name=value.title(), value=value) for value in NATURAL_COLORS
]
RIVEN_TOP_CHOICES = [
    app_commands.Choice(name="Completed-trade popularity", value="popularity"),
    app_commands.Choice(name="Median sale price", value="median"),
    app_commands.Choice(name="Average sale price", value="average"),
    app_commands.Choice(name="Highest recorded sale", value="maximum"),
]
class RelicBot(commands.Bot):
    def __init__(self):
        intents = discord.Intents.default()
        super().__init__(command_prefix="!", intents=intents)
        self._provisioning_task = None

    async def on_ready(self):
        print(f"Logged in as {self.user}. Automatically preparing channels and relic refresh.")
        if self._provisioning_task is None or self._provisioning_task.done():
            self._provisioning_task = asyncio.create_task(automation.startup())

    async def on_guild_join(self, guild):
        await automation.startup([guild])

    async def on_guild_remove(self, guild):
        automation.remove(guild.id)

    async def setup_hook(self):
        state.load_relics()
        if not companion_vision.configured:
            print("[companion] OPENAI_API_KEY is not set; automatic screenshot recognition is disabled.")
        await self.tree.sync()
        self.add_view(RefreshStopView())
        world_feed.start()
        riven_market.start_background_index()

    async def close(self):
        if self._provisioning_task is not None:
            self._provisioning_task.cancel()
            await asyncio.gather(self._provisioning_task, return_exceptions=True)
        await refresh_controller.stop()
        await world_feed.close()
        await riven_market.close()
        await super().close()


bot = RelicBot()
relics_group = app_commands.Group(name="relics", description="RelicFrame - relic profitability tools")
live_lists = LiveListManager(bot, state)
world_feed = WorldStateManager(bot)
companion_appraiser = CompanionAppraiser(
    Path(__file__).resolve().parent / "data" / "companion_current_market" / "price_evidence_deduplicated.jsonl",
    Path(__file__).resolve().parent / "data" / "companion_sales" / "price_evidence.jsonl",
)
companion_vision = OpenAICompanionVision()
riven_market = RivenMarketService()
riven_trade_chat = RivenTradeChatLog(
    Path(__file__).resolve().parent / "data" / "rivens" / "trade_chat_offers.jsonl",
    [riven_market.weapon_name(row) for row in riven_market.weapons],
)

# Per-guild "don't hammer Warframe Market by accident" throttle for anything
# that actually triggers a fetch (manual refresh, or starting auto-refresh).
# A plain dict + timestamps instead of @app_commands.checks.cooldown because
# the cooldown now needs to apply to only two of `/relics refresh`'s three
# `auto` modes - stopping or checking status is free and shouldn't wait.
_refresh_cooldowns: dict[int | None, float] = {}

def _cooldown_ready(guild_id: int | None) -> bool:
    last = _refresh_cooldowns.get(guild_id)
    return last is None or (time.time() - last) >= REFRESH_COOLDOWN_SECONDS


def _cooldown_remaining(guild_id: int | None) -> float:
    last = _refresh_cooldowns.get(guild_id, 0.0)
    return max(0.0, REFRESH_COOLDOWN_SECONDS - (time.time() - last))


def _mark_cooldown(guild_id: int | None) -> None:
    _refresh_cooldowns[guild_id] = time.time()


# ---------- shared helpers ----------

async def relic_name_autocomplete(interaction: discord.Interaction, current: str):
    current = current.lower()
    names = [n for n in state.relics.keys() if current in n.lower()]
    return [app_commands.Choice(name=n, value=n) for n in names[:25]]


async def reward_name_autocomplete(interaction: discord.Interaction, current: str):
    current = current.lower()
    names = [n for n in analysis.all_reward_names(state.relics) if current in n.lower()]
    return [app_commands.Choice(name=n, value=n) for n in names[:25]]


async def riven_weapon_autocomplete(interaction: discord.Interaction, current: str):
    return [app_commands.Choice(name=name, value=name) for name in riven_market.weapon_suggestions(current)]


def get_rows(channel_scope: str, trace_rate: float) -> list[dict]:
    snap = state.require_snapshot()
    return analysis.compute_all_rows(snap, trace_rate, channel_scope)


class FiltersModal(discord.ui.Modal, title="Set filters"):
    def __init__(self, view: "ListGUI"):
        super().__init__()
        self.view_ref = view
        self.min_roi_input = discord.ui.TextInput(
            label="Min ROI %", required=False,
            default=f"{view.min_roi:g}" if view.min_roi is not None else "",
        )
        self.max_cost_input = discord.ui.TextInput(
            label="Max relic cost (plat)", required=False,
            default=f"{view.max_cost:g}" if view.max_cost is not None else "",
        )
        self.min_reward_input = discord.ui.TextInput(
            label="Min best-reward price (plat)", required=False,
            default=f"{view.min_reward:g}" if view.min_reward is not None else "",
        )
        self.trace_rate_input = discord.ui.TextInput(
            label="Plat per void trace (0 = you farm your own)", required=False,
            default=f"{view.trace_rate:g}",
        )
        for item in (self.min_roi_input, self.max_cost_input, self.min_reward_input, self.trace_rate_input):
            self.add_item(item)

    async def on_submit(self, interaction: discord.Interaction):
        def _parse(text: str) -> float | None:
            text = text.strip()
            if not text:
                return None
            try:
                return float(text)
            except ValueError:
                return None

        self.view_ref.min_roi = _parse(self.min_roi_input.value)
        self.view_ref.max_cost = _parse(self.max_cost_input.value)
        self.view_ref.min_reward = _parse(self.min_reward_input.value)
        # Trace rate has no "unset" state - blank/invalid just means 0 (farm your own).
        self.view_ref.trace_rate = _parse(self.trace_rate_input.value) or 0.0
        self.view_ref.page = 1
        self.view_ref._recompute()
        await interaction.response.edit_message(embed=self.view_ref.current_embed(), view=self.view_ref)


class ListGUI(discord.ui.View):
    """
    Interactive `/relics list` - dropdowns for rank-by/channel/vault, a
    toggle for guaranteed-profit-only, and a Filters button (opens a modal
    for min ROI / max cost / min reward price / trace rate), all applied
    live against the cached snapshot with no re-fetch needed. Default
    trace rate is 0 (farm your own traces) unless the person overrides it.
    """
    def __init__(
        self, snap, rank_by: str = "Best Overall", channel_scope: str = "Online + Offline",
        vault_filter: str = "Any", min_roi: float | None = None, max_cost: float | None = None,
        min_reward: float | None = None, guaranteed_only: bool = False, trace_rate: float = 0.0,
        title_emoji: str = "📊",
    ):
        super().__init__(timeout=600)
        self.snap = snap
        self.rank_by = rank_by
        self.channel_scope = channel_scope
        self.vault_filter = vault_filter
        self.min_roi = min_roi
        self.max_cost = max_cost
        self.min_reward = min_reward
        self.guaranteed_only = guaranteed_only
        self.trace_rate = trace_rate
        self.title_emoji = title_emoji
        self.page = 1
        self.message: discord.Message | None = None
        self.rows: list[dict] = []
        self.total_pages = 1
        self._recompute()

    def _recompute(self):
        rows = analysis.compute_all_rows(self.snap, self.trace_rate, self.channel_scope)
        rows = [
            r for r in rows
            if analysis.passes_filters(
                r, self.channel_scope,
                vault_filter=None if self.vault_filter == "Any" else self.vault_filter,
                min_roi=self.min_roi, max_cost=self.max_cost, min_reward=self.min_reward,
                guaranteed_only=self.guaranteed_only,
            )
        ]
        self.rows = analysis.rank_rows(rows, self.rank_by, self.channel_scope)
        self.total_pages = max(1, (len(self.rows) + PAGE_SIZE - 1) // PAGE_SIZE)
        self.page = min(self.page, self.total_pages)
        self._sync_controls()

    def _sync_controls(self):
        self.prev_button.disabled = self.page <= 1
        self.next_button.disabled = self.page >= self.total_pages
        self.guaranteed_button.style = discord.ButtonStyle.success if self.guaranteed_only else discord.ButtonStyle.secondary
        self.guaranteed_button.label = f"🟢 Guaranteed only: {'ON' if self.guaranteed_only else 'OFF'}"
        for option in self.rank_select.options:
            option.default = (option.value == self.rank_by)
        for option in self.channel_select.options:
            option.default = (option.value == self.channel_scope)
        for option in self.vault_select.options:
            option.default = (option.value == self.vault_filter)

    def current_embed(self) -> discord.Embed:
        start = (self.page - 1) * PAGE_SIZE
        page_rows = self.rows[start:start + PAGE_SIZE]
        embed = fmt.build_list_embed(
            page_rows, self.page, self.total_pages, len(self.rows),
            self.snap.refinement, self.rank_by, self.channel_scope, self.snap,
            title_emoji=self.title_emoji,
        )
        filt_bits = [f"Vault: {self.vault_filter}"]
        if self.min_roi is not None:
            filt_bits.append(f"Min ROI {self.min_roi:.0f}%")
        if self.max_cost is not None:
            filt_bits.append(f"Max cost {self.max_cost:.0f}p")
        if self.min_reward is not None:
            filt_bits.append(f"Min reward price {self.min_reward:.0f}p")
        if self.guaranteed_only:
            filt_bits.append("Guaranteed profit only")
        filt_bits.append(f"Trace rate {self.trace_rate:.2f}p")
        embed.add_field(name="Filters", value=" · ".join(filt_bits), inline=False)
        return embed

    @discord.ui.select(
        placeholder="Rank by...", row=0,
        options=[discord.SelectOption(label=m, value=m) for m in analysis.RANK_MODES],
    )
    async def rank_select(self, interaction: discord.Interaction, select: discord.ui.Select):
        self.rank_by = select.values[0]
        self.page = 1
        self._recompute()
        await interaction.response.edit_message(embed=self.current_embed(), view=self)

    @discord.ui.select(
        placeholder="Channel...", row=1,
        options=[discord.SelectOption(label=c, value=c) for c in
                 ["Online + Offline", "Online only", "Offline only"]],
    )
    async def channel_select(self, interaction: discord.Interaction, select: discord.ui.Select):
        self.channel_scope = select.values[0]
        self.page = 1
        self._recompute()
        await interaction.response.edit_message(embed=self.current_embed(), view=self)

    @discord.ui.select(
        placeholder="Vault status...", row=2,
        options=[discord.SelectOption(label=v, value=v) for v in
                 ["Any", "Unvaulted", "Vaulted", "Unknown"]],
    )
    async def vault_select(self, interaction: discord.Interaction, select: discord.ui.Select):
        self.vault_filter = select.values[0]
        self.page = 1
        self._recompute()
        await interaction.response.edit_message(embed=self.current_embed(), view=self)

    @discord.ui.button(label="◀ Prev", style=discord.ButtonStyle.secondary, row=3)
    async def prev_button(self, interaction: discord.Interaction, button: discord.ui.Button):
        self.page = max(1, self.page - 1)
        self._sync_controls()
        await interaction.response.edit_message(embed=self.current_embed(), view=self)

    @discord.ui.button(label="Next ▶", style=discord.ButtonStyle.secondary, row=3)
    async def next_button(self, interaction: discord.Interaction, button: discord.ui.Button):
        self.page = min(self.total_pages, self.page + 1)
        self._sync_controls()
        await interaction.response.edit_message(embed=self.current_embed(), view=self)

    @discord.ui.button(label="🟢 Guaranteed only: OFF", style=discord.ButtonStyle.secondary, row=3)
    async def guaranteed_button(self, interaction: discord.Interaction, button: discord.ui.Button):
        self.guaranteed_only = not self.guaranteed_only
        self.page = 1
        self._recompute()
        await interaction.response.edit_message(embed=self.current_embed(), view=self)

    @discord.ui.button(label="Filters...", style=discord.ButtonStyle.primary, row=3)
    async def filters_button(self, interaction: discord.Interaction, button: discord.ui.Button):
        await interaction.response.send_modal(FiltersModal(self))

    async def on_timeout(self):
        for item in self.children:
            item.disabled = True
        if self.message is not None:
            try:
                await self.message.edit(view=self)
            except discord.HTTPException:
                pass


# ---------- persistent live list channels ----------

@relics_group.command(name="setup-list", description="Create/repair the seven persistent live relic-list channels.")
async def relics_setup_list(interaction: discord.Interaction):
    await live_lists.setup_command(interaction)


# ---------- live Warframe world state ----------

world_group = app_commands.Group(
    name="world",
    description="Live Warframe cycles, missions, fissures, vendors, and alerts",
)


@world_group.command(name="setup", description="Create/repair all live world-state channels and opt-in ping roles.")
async def world_setup(interaction: discord.Interaction):
    await world_feed.setup_command(interaction)


@world_group.command(name="refresh", description="Refresh all configured live world-state messages now.")
async def world_refresh(interaction: discord.Interaction):
    await world_feed.refresh_command(interaction)


@world_group.command(name="status", description="Check the live feed, polling interval, and Arbitration schedule.")
async def world_status(interaction: discord.Interaction):
    await interaction.response.send_message(embed=world_feed.status_embed(), ephemeral=True)


@world_group.command(name="help", description="Show how to set up and use the live Warframe feed.")
async def world_help(interaction: discord.Interaction):
    embed = discord.Embed(
        title="🌐 RelicFrame live world feed",
        description=(
            "On startup or joining a server I create the **WARFRAME LIVE** category automatically. "
            "An admin can use **`/world setup`** to repair it. "
            "Use #role-pings to choose your own notification roles.\n\n"
            "Feed messages refresh in place every minute, so channels do not fill with duplicate posts. "
            "Pings are only sent when a rotation or item is newly detected."
        ),
        color=0x5865F2,
    )
    embed.add_field(
        name="Commands",
        value=(
            "`/world setup` — create or repair the feed\n"
            "`/world refresh` — refresh it now\n"
            "`/world status` — show freshness and schedule status\n"
            "`/world arbitration` — current and upcoming rotation\n"
            "`/world cascade` — normal and Steel Path Cascade fissures"
        ),
        inline=False,
    )
    embed.add_field(
        name="Bot permissions",
        value="Manage Channels, Manage Roles, View Channels, Send Messages, Embed Links, and Read Message History.",
        inline=False,
    )
    embed.set_footer(text="The bot's role must sit above the opt-in ping roles.")
    await interaction.response.send_message(embed=embed, ephemeral=True)


async def _world_data_for_command(interaction: discord.Interaction):
    if world_feed.latest is not None:
        return world_feed.latest
    await interaction.response.defer(ephemeral=True)
    world_feed.latest = await world_feed.client.fetch()
    world_feed.last_success = time.time()
    return world_feed.latest


@world_group.command(name="arbitration", description="Show the current and upcoming Arbitration schedule.")
async def world_arbitration(interaction: discord.Interaction):
    try:
        data = await _world_data_for_command(interaction)
    except Exception as e:  # noqa: BLE001
        if interaction.response.is_done():
            await interaction.followup.send(f"World-state lookup failed: {e}", ephemeral=True)
        else:
            await interaction.response.send_message(f"World-state lookup failed: {e}", ephemeral=True)
        return
    embed = arbitration_embed(
        world_feed.arbitration_entries, data,
        icons=world_feed.emoji_map(interaction.guild_id),
    )
    if interaction.response.is_done():
        await interaction.followup.send(embed=embed, ephemeral=True)
    else:
        await interaction.response.send_message(embed=embed, ephemeral=True)


@world_group.command(name="cascade", description="Show active normal and Steel Path Void Cascade fissures.")
async def world_cascade(interaction: discord.Interaction):
    try:
        data = await _world_data_for_command(interaction)
    except Exception as e:  # noqa: BLE001
        if interaction.response.is_done():
            await interaction.followup.send(f"World-state lookup failed: {e}", ephemeral=True)
        else:
            await interaction.response.send_message(f"World-state lookup failed: {e}", ephemeral=True)
        return
    embed = cascade_embed(
        world_feed.arbitration_entries, data,
        icons=world_feed.emoji_map(interaction.guild_id),
    )
    if interaction.response.is_done():
        await interaction.followup.send(embed=embed, ephemeral=True)
    else:
        await interaction.response.send_message(embed=embed, ephemeral=True)


# ---------- companion appraisal ----------

companion_group = app_commands.Group(
    name="companion",
    description="Kubrow and companion pricing tools",
)


@companion_group.command(name="appraise", description="Identify companion traits from a screenshot and estimate its imprint value.")
@app_commands.choices(
    species=COMPANION_SPECIES_CHOICES,
    build=KUBROW_BUILD_CHOICES,
    pattern=KUBROW_PATTERN_CHOICES,
    breed=KUBROW_BREED_CHOICES,
    rarity=KUBROW_RARITY_CHOICES,
    color_1=COMPANION_COLOR_CHOICES,
    color_2=COMPANION_COLOR_CHOICES,
    color_3=COMPANION_COLOR_CHOICES,
    color_4=COMPANION_COLOR_CHOICES,
)
@app_commands.describe(
    image="Optional screenshot; manual trait entry works without an API key",
    species="Companion type; inferred from breed/pattern when omitted",
    build="Optional correction for the detected body build",
    pattern="Optional correction for the detected natural pattern",
    breed="Optional correction when the breed is known",
    rarity="Optional correction for the calculated color rarity",
    color_1="First natural fur color slot",
    color_2="Second natural fur color slot",
    color_3="Third natural fur color slot",
    color_4="Fourth natural fur color slot when present",
)
async def companion_appraise(
    interaction: discord.Interaction,
    image: discord.Attachment | None = None,
    species: app_commands.Choice[str] | None = None,
    build: app_commands.Choice[str] | None = None,
    pattern: app_commands.Choice[str] | None = None,
    breed: app_commands.Choice[str] | None = None,
    rarity: app_commands.Choice[str] | None = None,
    color_1: app_commands.Choice[str] | None = None,
    color_2: app_commands.Choice[str] | None = None,
    color_3: app_commands.Choice[str] | None = None,
    color_4: app_commands.Choice[str] | None = None,
):
    if image:
        content_type = (image.content_type or "").casefold()
        image_extension = Path(image.filename).suffix.casefold()
        if not content_type.startswith("image/") and image_extension not in {".png", ".jpg", ".jpeg", ".webp", ".gif"}:
            await interaction.response.send_message("Please attach a PNG, JPG, WEBP, or GIF screenshot.", ephemeral=True)
            return
    await interaction.response.defer(ephemeral=True)
    visual = None
    vision_error = None
    if image and companion_vision.configured:
        safety_id = hashlib.sha256(str(interaction.user.id).encode("utf-8")).hexdigest()[:32]
        try:
            visual = await companion_vision.analyze_url(image.url, safety_identifier=safety_id)
        except CompanionVisionError as exc:
            vision_error = str(exc)

    detected_species = species.value if species else (visual.species if visual and visual.species not in {"unknown", "other"} else None)
    selected_build = build.value if build else (visual.build if visual and visual.build not in {"unknown", "not applicable"} else None)
    selected_pattern = pattern.value if pattern else (visual.pattern if visual and visual.pattern != "unknown" else None)
    selected_breed = breed.value if breed else (visual.breed if visual and visual.breed != "unknown" else None)
    manual_colors = tuple(choice.value for choice in (color_1, color_2, color_3, color_4) if choice)
    selected_colors = manual_colors or (visual.colors if visual else ())
    calculated_rarity = rarity_from_colors(selected_colors)
    selected_rarity = rarity.value if rarity else (
        calculated_rarity if calculated_rarity != "unknown" else
        (visual.rarity if visual and visual.rarity != "unknown" else None)
    )
    if not detected_species:
        detected_species = "kavat" if selected_breed in {"smeeta", "adarza", "vasca"} or selected_pattern == "hyacinth" else "kubrow"
    if detected_species == "kavat":
        selected_build = None

    missing = []
    if not selected_pattern:
        missing.append("pattern")
    if detected_species == "kubrow" and not selected_build:
        missing.append("build")
    if missing:
        recognition_note = (
            "The screenshot could not fill them automatically. " if image else "No paid API or screenshot is required. "
        )
        await interaction.followup.send(
            f"Please provide {' and '.join(f'`{name}`' for name in missing)}. {recognition_note}"
            "You can also enter breed, natural color slots, or a rarity override for a more specific estimate.",
            ephemeral=True,
        )
        return
    selected = {
        "species": detected_species,
        "build": selected_build,
        "pattern": selected_pattern,
        "breed": selected_breed,
        "rarity": selected_rarity,
    }
    try:
        result = companion_appraiser.appraise(**selected)
    except RuntimeError as exc:
        result = None
        appraisal_error = str(exc)
    else:
        appraisal_error = None

    traits = [detected_species.capitalize()]
    for value in (selected_build, selected_pattern, selected_breed, selected_rarity):
        if value:
            traits.append(value.title())
    if selected_colors:
        traits.append("Colors: " + ", ".join(value.title() for value in selected_colors))
    if result:
        price_description = (
            f"**{result.low:,}–{result.high:,} platinum** for the two-imprint set\n"
            f"Typical estimate: **{result.estimate:,}p**"
        )
    else:
        price_description = f"Traits were analyzed, but a price could not be calculated: {appraisal_error}"
    embed = discord.Embed(
        title=f"🐾 {detected_species.capitalize()} imprint appraisal",
        description=price_description,
        color=0xB98BFF,
    )
    embed.add_field(name="Traits used", value=" · ".join(traits), inline=False)
    if visual:
        colors = ", ".join(color.title() for color in visual.colors) or "Unknown"
        confidence = " · ".join(f"{key.title()} {value:.0%}" for key, value in visual.confidence.items())
        detected = (
            f"Pattern: **{visual.pattern.title()}** · Build: **{visual.build.title()}** · Breed: **{visual.breed.title()}**\n"
            f"Natural colors: **{colors}** · Calculated rarity: **{visual.rarity.title()}**\n"
            f"Energy: **{visual.energy_color.title()}** · Natural palette visible: **{visual.natural_colors_visible.title()}**\n"
            f"Confidence: {confidence or 'Unavailable'}"
        )
        if visual.notes:
            detected += "\n" + "\n".join(f"• {note}" for note in visual.notes)
        embed.add_field(name="Detected from screenshot", value=detected[:1024], inline=False)
    if result:
        embed.add_field(
            name="Comparable evidence",
            value=(
                f"{result.current_comparable_count} current sales posts + "
                f"{result.historical_comparable_count} lower-weight HTML posts\n"
                f"{result.screenshot_count} with attachments · {result.exact_trait_matches} exact trait matches\n"
                f"Price confidence: **{result.confidence}** · newest comparable: `{result.newest_timestamp[:10]}`"
            ),
            inline=False,
        )
    embed.add_field(
        name="Important",
        value=(
            "This is a market estimate, not a guaranteed sale. Asking prices are discounted and mixed multi-pet "
            "posts are excluded. Manual options override automatic detections. Use `/companion guide` for the "
            "correct screenshot setup and trait-value explanation."
        ),
        inline=False,
    )
    if vision_error:
        embed.add_field(name="Vision warning", value=vision_error[:1024], inline=False)
    if image:
        embed.set_image(url=image.url)
    mode = "automatic screenshot + manual overrides" if visual else "manual trait entry"
    embed.set_footer(text=f"Mode: {mode} · Current sales are primary · HTML history is lower-weight")
    await interaction.followup.send(embed=embed, ephemeral=True)


@companion_group.command(name="guide", description="How companion screenshot appraisal, inherited traits, rarity, and prices work.")
async def companion_guide(interaction: discord.Interaction):
    await interaction.response.send_message(embeds=companion_guide_embeds(companion_appraiser), ephemeral=True)


# ---------- Riven pricing and resale analysis ----------

riven_group = app_commands.Group(
    name="riven",
    description="Riven prices, market activity, and buy-low/sell-high leads",
)


def _weekly_line(item) -> str:
    if not item:
        return "No matching completed trades in the latest official feed"
    roll = "Rolled" if item.rerolled else "Unrolled"
    return (
        f"**{roll}:** median **{fmt_platinum(item.median)}** · average **{fmt_platinum(item.average)}** · "
        f"range {fmt_platinum(item.minimum)}–{fmt_platinum(item.maximum)} · popularity **{item.popularity:g}/100**"
    )


def _deal_roll_text(deal: RivenDeal) -> str:
    positives = deal.positive_rolls or tuple((slug, None) for slug in deal.positives)
    negatives = deal.negative_rolls or tuple((slug, None) for slug in deal.negatives)
    values = [format_roll_stat(slug, value, positive=True) for slug, value in positives]
    values.extend(format_roll_stat(slug, value, positive=False) for slug, value in negatives)
    return " · ".join(values) or "Roll values unavailable"


class RivenContactSelect(discord.ui.Select):
    def __init__(self, deals: list[RivenDeal]):
        self.deals = deals
        options = [
            discord.SelectOption(
                label=f"{deal.weapon_name or display_stat(deal.weapon_slug)} · {deal.price:,}p"[:100],
                description=f"{deal.seller} · projected +{deal.potential_margin:,}p"[:100],
                value=str(index),
            )
            for index, deal in enumerate(deals[:25])
        ]
        super().__init__(placeholder="Choose a listing to contact its seller…", options=options)

    async def callback(self, interaction: discord.Interaction):
        deal = self.deals[int(self.values[0])]
        embed = discord.Embed(
            title=f"Contact {deal.seller} about the {deal.weapon_name or display_stat(deal.weapon_slug)} Riven",
            description=(
                f"**Listed price:** {deal.price:,} platinum\n"
                f"**Exact roll:** {_deal_roll_text(deal)}\n\n"
                "Copy this into Warframe chat:\n"
                f"```text\n{deal.ingame_whisper}\n```\n"
                "Or use the marketplace buttons below. Warframe.market may require you to sign in before messaging."
            ),
            color=0x5CBE7B,
        )
        view = discord.ui.View(timeout=900)
        view.add_item(discord.ui.Button(label="Open marketplace listing", url=deal.url, emoji="🛒"))
        view.add_item(discord.ui.Button(label="Seller profile", url=deal.seller_profile_url, emoji="👤"))
        await interaction.response.send_message(embed=embed, view=view, ephemeral=True)


class RivenContactView(discord.ui.View):
    def __init__(self, deals: list[RivenDeal]):
        super().__init__(timeout=900)
        self.add_item(RivenContactSelect(deals))


def _flip_deal_line(deal: RivenDeal) -> str:
    liquidity = f"activity {deal.weekly_popularity:g}/100" if deal.weekly_popularity is not None else "activity unknown"
    quality = f" · roll RNG {deal.roll_quality_pct:.0f}/100" if deal.roll_quality_pct is not None else ""
    return (
        f"**[{deal.weapon_name} · buy {deal.price:,}p → target {deal.projected_resale:,}p]({deal.url})**\n"
        f"Potential **+{deal.potential_margin:,}p / {deal.roi_pct:.0f}% ROI** · {deal.discount_pct:.0f}% below peers · "
        f"{liquidity} · {deal.comparable_count} peers{quality}\n"
        f"{_deal_roll_text(deal)} · `{deal.seller}` ({deal.seller_status})\n"
        f"✅ Curated {deal.curated_desired_count}/{deal.curated_positive_count} positives + harmless negative"
    )


class RivenPageButton(discord.ui.Button):
    def __init__(self, owner: "RivenFlipResultsView", delta: int):
        label = "Previous" if delta < 0 else "Next"
        emoji = "◀️" if delta < 0 else "▶️"
        super().__init__(label=label, emoji=emoji, style=discord.ButtonStyle.secondary)
        self.owner = owner
        self.delta = delta

    async def callback(self, interaction: discord.Interaction):
        self.owner.page = min(self.owner.total_pages - 1, max(0, self.owner.page + self.delta))
        self.owner.rebuild()
        await interaction.response.edit_message(embed=self.owner.current_embed(), view=self.owner)


class RivenFlipResultsView(discord.ui.View):
    PAGE_SIZE = 5

    def __init__(self, deals: list[RivenDeal], scanned: int, indexed_at: float, failures: int = 0):
        super().__init__(timeout=900)
        self.deals = deals
        self.scanned = scanned
        self.indexed_at = indexed_at
        self.failures = failures
        self.page = 0
        self.total_pages = max(1, (len(deals) + self.PAGE_SIZE - 1) // self.PAGE_SIZE)
        self.rebuild()

    def page_deals(self) -> list[RivenDeal]:
        start = self.page * self.PAGE_SIZE
        return self.deals[start:start + self.PAGE_SIZE]

    def rebuild(self) -> None:
        self.clear_items()
        current = self.page_deals()
        if current:
            self.add_item(RivenContactSelect(current))
        previous = RivenPageButton(self, -1)
        previous.disabled = self.page <= 0
        following = RivenPageButton(self, 1)
        following.disabled = self.page >= self.total_pages - 1
        self.add_item(previous)
        self.add_item(following)

    def current_embed(self) -> discord.Embed:
        embed = discord.Embed(
            title=f"📈 All current Riven flip deals · {len(self.deals):,} found",
            description="\n\n".join(_flip_deal_line(deal) for deal in self.page_deals()),
            color=0x5CBE7B,
        )
        embed.add_field(
            name="Contact seller",
            value="Choose a listing below to copy its in-game whisper or open its Warframe.market listing/profile.",
            inline=False,
        )
        embed.add_field(
            name="How this list works",
            value=(
                "The background index checks every Riven family. Listings need a curated good-roll match and at least a 10% supported "
                "discount. The resale target is 90% of the comparable-listing median; it is not a guaranteed sale."
            ),
            inline=False,
        )
        embed.set_footer(
            text=(
                f"Page {self.page + 1}/{self.total_pages} · scanned {self.scanned} families · "
                f"full index completed {human_age(self.indexed_at)}"
                + (f" · {self.failures} market search failure(s)" if self.failures else " · all market searches succeeded")
            )
        )
        return embed


@riven_group.command(name="price", description="Show official completed-trade prices and current live asks for a weapon Riven.")
@app_commands.autocomplete(weapon=riven_weapon_autocomplete)
@app_commands.describe(weapon="Weapon name, such as Torid or Magistar")
async def riven_price(interaction: discord.Interaction, weapon: str):
    await interaction.response.defer(ephemeral=True)
    try:
        await riven_market.ensure_fresh()
        matched = riven_market.require_riven_weapon(weapon)
        name = riven_market.weapon_name(matched)
        family_name = riven_market.riven_family_name(matched)
        slug = str(matched.get("slug") or "")
        auctions = await riven_market.auctions_for(slug)
    except Exception as exc:  # noqa: BLE001 - friendly command boundary
        await interaction.followup.send(f"Riven price lookup failed: {exc}", ephemeral=True)
        return

    unrolled = parse_weekly(riven_market.weekly_rows, family_name, False)
    rolled = parse_weekly(riven_market.weekly_rows, family_name, True)
    live = [(auction_price(item), item) for item in auctions]
    live = [(price, item) for price, item in live if price is not None]
    online = [
        (price, item) for price, item in live
        if str((item.get("owner") or {}).get("status") or "").casefold() in {"online", "ingame"}
    ]
    cheapest = min((price for price, _ in online), default=None)
    disposition = matched.get("variant_disposition", matched.get("disposition"))
    details = [
        f"Riven family: **{family_name}**" if family_name != name else f"Type: **{str(matched.get('rivenType') or 'unknown').title()}**",
        f"Weapon: **{str(matched.get('variant_slot') or matched.get('group') or 'unknown').title()} · {str(matched.get('variant_class') or matched.get('rivenType') or 'unknown').title()}**",
        f"Disposition: **{disposition:g} · {disposition_band(float(disposition))}**" if isinstance(disposition, (int, float)) else "Disposition: unknown",
        f"Live direct asks: **{len(live):,}** · online sellers: **{len(online):,}**",
    ]
    if cheapest is not None:
        details.append(f"Lowest online ask: **{cheapest:,}p**")
    embed = discord.Embed(
        title=f"🟣 {name} Riven market",
        description="\n".join(details),
        color=0x8A55D7,
    )
    embed.add_field(
        name="Completed trades · latest DE weekly feed",
        value=f"{_weekly_line(unrolled)}\n{_weekly_line(rolled)}",
        inline=False,
    )
    embed.add_field(
        name="How to read this",
        value=(
            "Completed-trade numbers are real aggregate sales, but do not include each roll's stats. "
            "Live auctions include exact stats, but their prices are only asks. Use `/riven deals` or `/riven flips` for resale leads."
        ),
        inline=False,
    )
    embed.set_footer(
        text=f"Data refreshed {human_age(riven_market.fetched_at)} · {riven_market.history_snapshot_count} weekly snapshot(s) saved"
    )
    await interaction.followup.send(embed=embed, ephemeral=True)


@riven_group.command(name="top", description="Rank weapon Rivens from the latest official completed-trade feed.")
@app_commands.choices(sort_by=RIVEN_TOP_CHOICES)
@app_commands.describe(sort_by="Ranking signal", limit="Number of rows")
async def riven_top(
    interaction: discord.Interaction,
    sort_by: app_commands.Choice[str] | None = None,
    limit: app_commands.Range[int, 3, 15] = 10,
):
    await interaction.response.defer(ephemeral=True)
    try:
        await riven_market.ensure_fresh()
    except Exception as exc:  # noqa: BLE001
        await interaction.followup.send(f"Riven ranking failed: {exc}", ephemeral=True)
        return
    mode = sort_by.value if sort_by else "popularity"
    rows = top_weekly_weapons(riven_market.weekly_rows, mode, limit)
    lines = []
    for index, item in enumerate(rows, 1):
        roll = "rolled" if item.rerolled else "unrolled"
        lines.append(
            f"**{index}. {item.weapon}** ({roll}) · median {fmt_platinum(item.median)} · "
            f"avg {fmt_platinum(item.average)} · popularity {item.popularity:g}/100"
        )
    embed = discord.Embed(
        title="🟣 Weekly Riven rankings",
        description="\n".join(lines) or "No official weekly rows were available.",
        color=0x8A55D7,
    )
    embed.add_field(
        name="Source",
        value="Digital Extremes' latest completed-trade aggregates. Popularity is DE's relative activity score, not a raw transaction count.",
        inline=False,
    )
    embed.set_footer(text=f"Sorted by {mode} · data refreshed {human_age(riven_market.fetched_at)}")
    await interaction.followup.send(embed=embed, ephemeral=True)


@riven_group.command(name="deals", description="Find buy-low/sell-high Riven listings for one weapon.")
@app_commands.autocomplete(weapon=riven_weapon_autocomplete)
@app_commands.describe(
    weapon="Weapon Riven to scan",
    maximum_price="Optional maximum amount you are willing to spend",
    minimum_discount="Minimum discount versus similar active rolls",
    online_only="Only show sellers currently online or in game",
)
async def riven_deals(
    interaction: discord.Interaction,
    weapon: str,
    maximum_price: app_commands.Range[int, 1, 100000] | None = None,
    minimum_discount: app_commands.Range[float, 5.0, 80.0] = 20.0,
    online_only: bool = True,
):
    await interaction.response.defer(ephemeral=True)
    try:
        await riven_market.ensure_fresh()
        matched = riven_market.require_riven_weapon(weapon)
        name = riven_market.weapon_name(matched)
        family_name = riven_market.riven_family_name(matched)
        slug = str(matched.get("slug") or "")
        auctions = await riven_market.auctions_for(slug, force=True)
        weekly = parse_weekly(riven_market.weekly_rows, family_name, True) or parse_weekly(
            riven_market.weekly_rows, family_name, False
        )
        disposition_raw = matched.get("variant_disposition", matched.get("disposition"))
        disposition = float(disposition_raw) if disposition_raw is not None else None
        roll_rule = riven_market.roll_rule_for(matched)
        deals = find_riven_deals(
            auctions,
            weapon_slug=slug,
            weapon_name=name,
            weekly=weekly,
            stat_class=riven_stat_class(matched),
            disposition=disposition,
            minimum_discount_pct=minimum_discount,
            maximum_price=maximum_price,
            online_only=online_only,
            limit=8,
            roll_rule=roll_rule,
            curated_only=bool(roll_rule),
        )
    except Exception as exc:  # noqa: BLE001
        await interaction.followup.send(f"Riven deal scan failed: {exc}", ephemeral=True)
        return

    if not deals:
        await interaction.followup.send(
            f"No reliable {name} leads met those filters right now. Curated weapons must also match the supplied good-roll guide. "
            "Try a lower minimum discount or include offline sellers.",
            ephemeral=True,
        )
        return
    lines = []
    for deal in deals:
        lines.append(
            f"**[Buy {deal.price:,}p → target {deal.projected_resale:,}p]({deal.url})** · "
            f"**+{deal.potential_margin:,}p / {deal.roi_pct:.0f}% ROI** · {deal.discount_pct:.0f}% below peers\n"
            f"{_deal_roll_text(deal)}\n`{deal.seller}` ({deal.seller_status}) · {deal.comparable_count} peers"
            + (f" · roll RNG {deal.roll_quality_pct:.0f}/100" if deal.roll_quality_pct is not None else "")
            + (
                f"\n✅ Curated match: {deal.curated_desired_count}/{deal.curated_positive_count} desired positives + harmless negative"
                if deal.curated_match else ""
            )
        )
    embed = discord.Embed(
        title=f"🔎 {name} Riven flip leads",
        description="\n\n".join(lines),
        color=0x5CBE7B,
    )
    embed.add_field(
        name="Good-roll profile",
        value=(
            f"`{deals[0].curated_profile}`\nOnly profiles with at least two desired positives, no dead third positive, "
            "and a listed harmless negative are flagged."
            if deals[0].curated_profile else
            "This weapon is not in the supplied roll workbook yet, so only comparable-listing evidence was used."
        ),
        inline=False,
    )
    embed.add_field(
        name="Contact seller",
        value="Choose a listing in the menu below for a copyable in-game whisper and direct marketplace/profile buttons.",
        inline=False,
    )
    embed.add_field(
        name="Risk warning",
        value=(
            "Target resale is 90% of the supported comparable-listing median so there is room to undercut. It is not a guaranteed sale. "
            "The bot never contacts sellers or performs trades."
        ),
        inline=False,
    )
    family_note = f" · {family_name} Riven family" if family_name != name else ""
    embed.set_footer(text=f"Fresh Warframe.market auction scan · cached afterward for 10 minutes{family_note}")
    await interaction.followup.send(embed=embed, view=RivenContactView(deals), ephemeral=True)


@riven_group.command(name="flips", description="Show every current deal from the background full-market Riven scan.")
@app_commands.describe(
    maximum_buy="Maximum platinum you are willing to spend",
    minimum_discount="Minimum supported discount; the complete index starts at 10%",
    online_only="Hide current listings whose seller is presently offline",
)
async def riven_flips(
    interaction: discord.Interaction,
    maximum_buy: app_commands.Range[int, 1, 100000] | None = None,
    minimum_discount: app_commands.Range[float, 10.0, 80.0] = 10.0,
    online_only: bool = False,
):
    await interaction.response.defer(ephemeral=True)
    status = await interaction.followup.send(
        "Loading the latest completed full-market Riven deal index…",
        ephemeral=True,
        wait=True,
    )
    try:
        indexed, scanned, indexed_at = await riven_market.current_flip_index()
    except Exception as exc:  # noqa: BLE001
        await status.edit(content=f"The full-market Riven index could not be loaded: {exc}")
        return
    deals = [
        deal for deal in indexed
        if deal.discount_pct >= minimum_discount
        and (maximum_buy is None or deal.price <= maximum_buy)
        and (not online_only or deal.seller_status.casefold() in {"online", "ingame"})
    ]
    if not deals:
        await status.edit(
            content=(
                f"The latest complete scan checked **{scanned}** Riven families, but none of its "
                "current deals matched those filters."
            )
        )
        return
    view = RivenFlipResultsView(deals, scanned, indexed_at, riven_market.flip_index_failures)
    await status.edit(content=None, embed=view.current_embed(), view=view)


def _decode_trade_chat_file(payload: bytes) -> str:
    for encoding in ("utf-8-sig", "utf-16", "cp1252"):
        try:
            return payload.decode(encoding)
        except UnicodeError:
            continue
    return payload.decode("utf-8", errors="replace")


@riven_group.command(name="chatlog", description="Import copied or OCR-extracted Warframe trade-chat offers into the local log.")
@app_commands.describe(
    text="Paste one or more WTS/WTB trade-chat lines",
    file="Optional TXT file containing copied or OCR-extracted trade chat",
)
async def riven_chatlog(
    interaction: discord.Interaction,
    text: app_commands.Range[str, 1, 6000] | None = None,
    file: discord.Attachment | None = None,
):
    await interaction.response.defer(ephemeral=True)
    parts = [text] if text else []
    if file:
        if Path(file.filename).suffix.casefold() not in {".txt", ".log", ".csv"}:
            await interaction.followup.send(
                "Attach a TXT, LOG, or CSV text export. For a screenshot, use Windows Snipping Tool → Text Actions → Copy all text first.",
                ephemeral=True,
            )
            return
        if file.size > 2_000_000:
            await interaction.followup.send("That file is over the 2 MB import limit.", ephemeral=True)
            return
        parts.append(_decode_trade_chat_file(await file.read()))
    if not parts:
        await interaction.followup.send(
            "Paste trade-chat text or attach a TXT file. Screenshots need to be converted with Snipping Tool → Text Actions first.",
            ephemeral=True,
        )
        return
    result = riven_trade_chat.import_text("\n".join(parts), source="discord")
    preview = []
    for offer in result.added[:8]:
        price = f"{offer.price:,}p" if offer.price is not None else "price not detected"
        preview.append(f"**{offer.action} {offer.weapon}** · {price} · {offer.seller}")
    embed = discord.Embed(
        title="💬 Trade-chat observations imported",
        description="\n".join(preview) or "No new Riven offers were found in that text.",
        color=0x8A55D7,
    )
    embed.add_field(name="New observations", value=f"**{len(result.added):,}**", inline=True)
    embed.add_field(name="Duplicates skipped", value=f"**{result.duplicate_count:,}**", inline=True)
    embed.add_field(name="Unparsed lines", value=f"**{result.unparsed_line_count:,}**", inline=True)
    embed.add_field(
        name="Important",
        value="These are WTS/WTB asking offers, not confirmed sales. Exact Riven rolls still need to be inspected before treating one as a flip.",
        inline=False,
    )
    embed.set_footer(text="Stored locally · identical observations are deduplicated per day")
    await interaction.followup.send(embed=embed, ephemeral=True)


@riven_group.command(name="chatstats", description="Summarize locally imported WTS/WTB Riven trade-chat offers.")
@app_commands.autocomplete(weapon=riven_weapon_autocomplete)
@app_commands.describe(weapon="Optional weapon or Riven family", days="Use observations from the last 1–90 days")
async def riven_chatstats(
    interaction: discord.Interaction,
    weapon: str | None = None,
    days: app_commands.Range[int, 1, 90] = 30,
):
    family_name = None
    if weapon:
        try:
            family = riven_market.require_riven_weapon(weapon)
            family_name = riven_market.weapon_name(family)
        except ValueError as exc:
            await interaction.response.send_message(str(exc), ephemeral=True)
            return
    offers = riven_trade_chat.recent(weapon=family_name, days=days)
    if not offers:
        target = f" for **{family_name}**" if family_name else ""
        await interaction.response.send_message(
            f"No imported trade-chat Riven offers{target} from the last {days} days. Use `/riven chatlog` first.",
            ephemeral=True,
        )
        return
    embed = discord.Embed(
        title=f"💬 Trade-chat offer data · {family_name or 'all Rivens'}",
        description=f"**{len(offers):,}** locally imported observations from the last {days} days.",
        color=0x8A55D7,
    )
    if family_name:
        summary = price_summary(offers)
        wts = "No priced WTS observations" if summary["wts_count"] == 0 else (
            f"{summary['wts_count']} offers · lowest **{summary['wts_min']:,}p** · median **{summary['wts_median']:g}p**"
        )
        wtb = "No priced WTB observations" if summary["wtb_count"] == 0 else (
            f"{summary['wtb_count']} offers · highest **{summary['wtb_max']:,}p** · median **{summary['wtb_median']:g}p**"
        )
        embed.add_field(name="WTS asks", value=wts, inline=False)
        embed.add_field(name="WTB bids", value=wtb, inline=False)
        if summary["possible_spread"] is not None:
            embed.add_field(
                name="Raw possible spread",
                value=f"**{summary['possible_spread']:,}p** before negotiation—only meaningful if the rolls are comparable.",
                inline=False,
            )
    else:
        grouped: dict[str, list] = {}
        for offer in offers:
            grouped.setdefault(offer.weapon, []).append(offer)
        lines = []
        for name, rows in sorted(grouped.items(), key=lambda pair: len(pair[1]), reverse=True)[:15]:
            summary = price_summary(rows)
            lines.append(
                f"**{name}** · {len(rows)} offers · "
                f"WTS low {summary['wts_min'] if summary['wts_min'] is not None else '—'}p · "
                f"WTB high {summary['wtb_max'] if summary['wtb_max'] is not None else '—'}p"
            )
        embed.add_field(name="Most observed families", value="\n".join(lines), inline=False)
    embed.add_field(
        name="Evidence limit",
        value="Trade chat records asking offers, not completed trades. Use `/riven deals` to inspect exact rolls and DE sale history.",
        inline=False,
    )
    await interaction.response.send_message(embed=embed, ephemeral=True)


@riven_group.command(name="refresh", description="Download and archive the newest official Riven data and catalogs.")
@app_commands.default_permissions(manage_guild=True)
async def riven_refresh(interaction: discord.Interaction):
    await interaction.response.defer(ephemeral=True)
    try:
        await riven_market.refresh(force=True)
    except Exception as exc:  # noqa: BLE001
        await interaction.followup.send(f"Riven refresh failed: {exc}", ephemeral=True)
        return
    await interaction.followup.send(
        f"Riven data refreshed: **{len(riven_market.weekly_rows):,}** weekly trade rows, "
        f"**{len(riven_market.weapons):,}** Riven families, **{len(riven_market.variants):,}** named weapon variants, "
        f"**{riven_market.history_snapshot_count}** dated snapshot(s) saved.",
        ephemeral=True,
    )


@riven_group.command(name="guide", description="Explain Riven prices, flip leads, data sources, and limitations.")
async def riven_guide(interaction: discord.Interaction):
    embed = discord.Embed(
        title="🟣 RelicFrame Riven guide",
        description="The Riven tools are free and do not use OpenAI.",
        color=0x8A55D7,
    )
    embed.add_field(
        name="Commands",
        value=(
            "`/riven price weapon:Torid` — actual weekly baseline + current asks\n"
            "`/riven top` — highest activity or sale-price weapons\n"
            "`/riven deals` — flip leads for one selected weapon\n"
            "`/riven flips` — browse every current deal from the background full-market index\n"
            "`/riven chatlog` — import copied/OCR trade-chat WTS and WTB lines\n"
            "`/riven chatstats` — summarize the locally collected trade-chat offers\n"
            "`/riven refresh` — admin download/archive of the newest snapshot"
        ),
        inline=False,
    )
    embed.add_field(
        name="Rolls and contacting sellers",
        value=(
            "Each lead shows the exact numerical stats from the live listing. Choose it in the contact menu to get a copyable in-game "
            "`/w` whisper plus buttons for its Warframe.market auction and seller profile. The bot prepares the contact but never sends a "
            "message or trade automatically."
        ),
        inline=False,
    )
    embed.add_field(
        name="What counts as a good resale roll",
        value=(
            "The included workbook supplies 417 weapon-family profiles. A match needs at least two listed positives, every required stat in "
            "that weapon's profile, no unwanted third positive, and one of its listed harmless negatives. Three wanted positives are ranked "
            "ahead of two. Dex Nikana is the only current marketplace family not present in that workbook."
        ),
        inline=False,
    )
    embed.add_field(
        name="What determines value",
        value=(
            "Every named weapon variant in `ALL weapons.txt` is searchable. Prime, Vandal, Wraith, Prisma, Kuva, Tenet, Coda, and other variants "
            "are routed to the correct shared Riven family while keeping the selected variant's own disposition. Weapons that cannot use a Riven are identified clearly.\n\n"
            "The background scanner checks every live Riven family about every 15 minutes and saves its last complete index across restarts. "
            "`/riven flips` reads that index immediately and provides pages until every matching deal has been shown. Completed-trade activity "
            "boosts liquid opportunities in the ranking. "
            "Listings are compared only with similar stat combinations, and the supplied disposition/base-value formula separates weak "
            "numerical rolls from strong ones. Several peers are required, and the resale target is 90% of their median."
        ),
        inline=False,
    )
    embed.add_field(
        name="Honest data limits",
        value=(
            "DE gives real completed-trade aggregates but not the stats of each sold roll. Warframe.market gives exact rolls but only asks. "
            "The public feed exposes the newest week, so the bot archives each download and reports how many historical snapshots it truly has. "
            "Warframe trade chat has no public feed. `/riven chatlog` can analyze text you explicitly provide and stores it locally as offer data, "
            "but the bot cannot silently read game chat and never labels WTS/WTB messages as completed sales."
        ),
        inline=False,
    )
    embed.set_footer(text="No lead is a guaranteed sale—inspect the exact roll before spending platinum.")
    await interaction.response.send_message(embed=embed, ephemeral=True)


# ---------- /relics list ----------

@relics_group.command(name="list", description="Interactive, ranked relic list - dropdowns and a Filters button, same options as the desktop app.")
@app_commands.choices(refinement=REFINEMENT_CHOICES, channel=CHANNEL_CHOICES, vault=VAULT_CHOICES, rank_by=RANK_CHOICES)
@app_commands.describe(
    min_roi="Minimum expected ROI %% (either channel)", max_cost="Maximum relic cost in plat",
    min_reward="Minimum price of the relic's best reward", guaranteed_only="Only show 🟢 Guaranteed Profit relics",
    trace_rate="Plat value per void trace, if buying traces (default 0 = you farm your own)",
)
async def relics_list(
    interaction: discord.Interaction,
    refinement: app_commands.Choice[str] | None = None,
    channel: app_commands.Choice[str] | None = None,
    vault: app_commands.Choice[str] | None = None,
    rank_by: app_commands.Choice[str] | None = None,
    min_roi: float | None = None,
    max_cost: float | None = None,
    min_reward: float | None = None,
    guaranteed_only: bool = False,
    trace_rate: float = 0.0,
):
    try:
        snap = state.require_snapshot()
    except RuntimeError as e:
        await interaction.response.send_message(str(e), ephemeral=True)
        return

    refinement_v = (refinement.value if refinement else snap.refinement)
    if refinement_v != snap.refinement:
        await interaction.response.send_message(
            f"The cached snapshot was fetched at **{snap.refinement.capitalize()}** refinement. "
            f"Run `/relics refresh refinement:{refinement_v}` to get prices at {refinement_v.capitalize()}.",
            ephemeral=True,
        )
        return

    await interaction.response.defer()
    view = ListGUI(
        snap,
        rank_by=rank_by.value if rank_by else "Best Overall",
        channel_scope=channel.value if channel else "Online + Offline",
        vault_filter=vault.value if vault else "Any",
        min_roi=min_roi, max_cost=max_cost, min_reward=min_reward,
        guaranteed_only=guaranteed_only, trace_rate=trace_rate,
        title_emoji=world_feed.emoji_map(interaction.guild_id)["fissure"],
    )
    msg = await interaction.followup.send(embed=view.current_embed(), view=view)
    view.message = msg


# ---------- /relics detail ----------

@relics_group.command(name="detail", description="Full profitability breakdown for one relic.")
@app_commands.autocomplete(relic=relic_name_autocomplete)
@app_commands.choices(refinement=REFINEMENT_CHOICES)
async def relics_detail(
    interaction: discord.Interaction, relic: str,
    refinement: app_commands.Choice[str] | None = None, trace_rate: float = 0.0,
):
    try:
        snap = state.require_snapshot()
    except RuntimeError as e:
        await interaction.response.send_message(str(e), ephemeral=True)
        return
    if relic not in state.relics:
        await interaction.response.send_message(f"No relic named `{relic}` in the loaded CSV.", ephemeral=True)
        return

    refinement_v = refinement.value if refinement else snap.refinement
    if refinement_v != snap.refinement:
        await interaction.response.send_message(
            f"Cached snapshot is at {snap.refinement.capitalize()}. "
            f"Run `/relics refresh refinement:{refinement_v}` first.", ephemeral=True,
        )
        return

    row = analysis.compute_row(state.relics[relic], snap, trace_rate, "Online + Offline")
    await interaction.response.send_message(embed=fmt.build_detail_embed(row, refinement_v, trace_rate))


# ---------- /relics find (item search) ----------

@relics_group.command(name="find", description="Which relics drop a specific item, cheapest expected cost first.")
@app_commands.autocomplete(item=reward_name_autocomplete)
@app_commands.choices(refinement=REFINEMENT_CHOICES)
async def relics_find(
    interaction: discord.Interaction, item: str,
    refinement: app_commands.Choice[str] | None = None, min_roi: float | None = None,
):
    if not state.relics:
        await interaction.response.send_message("No relic data loaded.", ephemeral=True)
        return
    refinement_v = refinement.value if refinement else state.default_refinement
    results = find_relics_for_reward(item, state.relics, refinement_v)
    if not results:
        await interaction.response.send_message(f"No relics found containing an item matching `{item}`.", ephemeral=True)
        return

    snap = state.snapshot
    rows_by_name = {}
    if snap is not None and snap.refinement == refinement_v:
        for r in analysis.compute_all_rows(snap, 0.0, "Online + Offline"):
            rows_by_name[r["relic"].relic_name] = r

    priced, unpriced = [], []
    filtered_by_roi = 0
    for r in results:
        row = rows_by_name.get(r["relic_name"])
        relic_cost = roi_pct = None
        if row is not None:
            if row["online_cost"] is not None:
                relic_cost, roi_pct = row["online_cost"], row["online_profit"]["expected_roi_pct"]
            elif row["offline_cost"] is not None:
                relic_cost, roi_pct = row["offline_cost"], row["offline_profit"]["expected_roi_pct"]

        if min_roi is not None and (roi_pct is None or roi_pct < min_roi):
            filtered_by_roi += 1
            continue

        entry = dict(r)
        entry["relic_cost"], entry["roi_pct"] = relic_cost, roi_pct
        if relic_cost is not None and r["expected_openings"] is not None:
            entry["cost_per_expected_copy"] = relic_cost * r["expected_openings"]
            priced.append(entry)
        else:
            unpriced.append(entry)

    priced.sort(key=lambda e: e["cost_per_expected_copy"])
    lines = []
    for e in (priced + unpriced)[:20]:
        chance = f"{e['chance_pct']:.2f}%"
        opens = f"{e['expected_openings']:.1f}" if e["expected_openings"] else "-"
        cost = f"{e['relic_cost']:.1f}p" if e["relic_cost"] is not None else "-"
        per_copy = f"{e['cost_per_expected_copy']:.1f}p" if "cost_per_expected_copy" in e else "-"
        roi = f"{e['roi_pct']:.0f}%" if e["roi_pct"] is not None else "-"
        lines.append(
            f"**{e['relic_name']}** {fmt.VAULT_EMOJI[e['vaulted']]} - {e['reward_name']} "
            f"({e['rarity']}, {chance}) · exp. openings {opens} · relic {cost} · "
            f"exp. cost/copy **{per_copy}** · ROI {roi}"
        )
    note = ""
    if unpriced:
        note += f"\n({len(unpriced)} relic(s) shown without cost - run `/relics refresh` for pricing.)"
    if min_roi is not None and filtered_by_roi:
        note += f"\n({filtered_by_roi} relic(s) hidden below {min_roi:.0f}% ROI.)"

    embed = discord.Embed(
        title=f"Relics dropping “{item}” · {refinement_v.capitalize()}",
        description="\n".join(lines) + note,
    )
    await interaction.response.send_message(embed=embed)


# ---------- /relics odds ----------

@relics_group.command(name="odds", description="Chance of getting >=1 copy of a specific reward within N opens.")
@app_commands.autocomplete(relic=relic_name_autocomplete, reward=reward_name_autocomplete)
@app_commands.choices(refinement=REFINEMENT_CHOICES)
async def relics_odds(
    interaction: discord.Interaction, relic: str, reward: str,
    refinement: app_commands.Choice[str] | None = None,
):
    if relic not in state.relics:
        await interaction.response.send_message(f"No relic named `{relic}`.", ephemeral=True)
        return
    refinement_v = refinement.value if refinement else state.default_refinement
    match = None
    for r in find_relics_for_reward(reward, {relic: state.relics[relic]}, refinement_v):
        match = r
        break
    if match is None:
        await interaction.response.send_message(f"`{relic}` doesn't drop anything matching `{reward}`.", ephemeral=True)
        return

    table = chance_of_at_least_one(match["chance_pct"], [1, 3, 6, 10, 20, 50])
    lines = [f"{row['n']:>3} opens: {row['chance_at_least_one_pct']:.1f}%" for row in table]
    embed = discord.Embed(
        title=f"{match['reward_name']} from {relic}",
        description=f"{match['chance_pct']:.2f}% per open ({refinement_v.capitalize()})\n```\n" + "\n".join(lines) + "\n```",
    )
    await interaction.response.send_message(embed=embed)


# ---------- /relics compare (refinement comparison) ----------

@relics_group.command(name="compare", description="Intact -> Radiant comparison for one relic; which tier is actually worth refining to.")
@app_commands.autocomplete(relic=relic_name_autocomplete)
@app_commands.choices(objective=OBJECTIVE_CHOICES)
async def relics_compare(
    interaction: discord.Interaction, relic: str,
    objective: app_commands.Choice[str] | None = None, trace_rate: float = 0.0,
):
    try:
        snap = state.require_snapshot()
    except RuntimeError as e:
        await interaction.response.send_message(str(e), ephemeral=True)
        return
    if relic not in state.relics:
        await interaction.response.send_message(f"No relic named `{relic}`.", ephemeral=True)
        return

    r = state.relics[relic]
    row = analysis.compute_row(r, snap, trace_rate, "Online + Offline")
    cost = row["online_cost"] if row["online_cost"] is not None else row["offline_cost"]
    rows = r.refinement_comparison(snap.prices, cost, trace_rate)

    lines = []
    for rr in rows:
        incr = f"{rr['incremental_ev_per_trace']:+.4f}p/trace" if rr["incremental_ev_per_trace"] is not None else "-"
        lines.append(
            f"**{rr['tier'].capitalize()}** ({rr['trace_cost']} traces): EV {fmt.plat(rr['expected_value'])} · "
            f"profit {fmt.signed_plat(rr['expected_profit'])} ({fmt.pct(rr['expected_roi_pct'])}) · "
            f"worst {fmt.signed_plat(rr['worst_case_profit'])} · P(profit) {rr['chance_of_profit_pct']:.0f}% · "
            f"rare slot {rr['rare_slot_chance_pct']:.1f}% · {incr}"
        )

    objective_v = objective.value if objective else "expected_profit"
    best = r.best_refinement(snap.prices, cost, trace_rate, objective_v)
    objective_label = next(c.name for c in OBJECTIVE_CHOICES if c.value == objective_v)

    embed = discord.Embed(
        title=f"Refinement comparison · {relic}",
        description="\n".join(lines) + (
            f"\n\n**Recommended for {objective_label}: {best['tier'].capitalize()}**" if best else ""
        ),
    )
    if cost is None:
        embed.set_footer(text="No relic price available - numbers above use 0p relic cost as a placeholder.")
    await interaction.response.send_message(embed=embed)


# ---------- /relics buyn ----------

@relics_group.command(name="buyn", description="Real cost & risk of buying N of a relic (sweeps the actual sell-order book).")
@app_commands.autocomplete(relic=relic_name_autocomplete)
@app_commands.choices(refinement=REFINEMENT_CHOICES)
async def relics_buyn(
    interaction: discord.Interaction, relic: str, n: app_commands.Range[int, 1, 500],
    refinement: app_commands.Choice[str] | None = None, trace_rate: float = 0.0,
):
    try:
        snap = state.require_snapshot()
    except RuntimeError as e:
        await interaction.response.send_message(str(e), ephemeral=True)
        return
    if relic not in state.relics:
        await interaction.response.send_message(f"No relic named `{relic}`.", ephemeral=True)
        return
    if state.live_market is None:
        await interaction.response.send_message(
            "Live market isn't running yet - run `/relics refresh` first.", ephemeral=True
        )
        return

    await interaction.response.defer()
    refinement_v = refinement.value if refinement else snap.refinement
    r = state.relics[relic]
    row = analysis.compute_row(r, snap, trace_rate, "Online + Offline")

    lines = []
    for label, include_offline, cost, zero in (
        ("Online", False, row["online_cost"], row["online_zero"]),
        ("Offline", True, row["offline_cost"], row["offline_zero"]),
    ):
        if cost is None:
            reason = "0 quantity available" if zero else "no listing"
            lines.append(f"**{label}**: can't calculate - {reason}.")
            continue
        # Straight from the live in-memory order book - no network call,
        # and never capped to "the top 5" the way the old /top-based path
        # was (see live_market.LiveMarket.relic_matching_entries's docstring).
        order_book = state.live_market.relic_matching_entries(relic, refinement_v, include_offline)
        if not order_book:
            lines.append(f"**{label}**: can't calculate - no listing.")
            continue

        combo = cheapest_combination_cost(order_book, n)
        if combo["units_filled"] == 0:
            lines.append(f"**{label}**: can't calculate - no listing.")
            continue

        result = r.buy_n_analysis(
            refinement_v, snap.prices, cost, trace_rate, n,
            total_relic_cost_override=combo["total_cost"],
        )
        method = "Monte Carlo estimate" if result["approx"] else "exact"
        fill_note = ""
        if not combo["fully_filled"]:
            fill_note = f" ⚠ only {combo['units_filled']}/{n} actually available right now"
        lines.append(
            f"**{label}** ({method}): {combo['units_filled']} unit(s) / {len(combo['orders_used'])} seller(s), "
            f"avg {combo['avg_price_per_unit']:.1f}p/relic, total {result['total_cost']:.1f}p →\n"
            f"　exp. profit {fmt.signed_plat(result['expected_profit'])} · worst case {fmt.signed_plat(result['worst_case_profit'])}"
            f"{fill_note}\n"
            f"　P(profit) {result['prob_profit_pct']:.1f}% · P(loss) {result['prob_loss_pct']:.1f}% · "
            f"P(breakeven) {result['prob_breakeven_pct']:.1f}%"
        )

    embed = discord.Embed(title=f"Buying {n}x {relic} · {refinement_v.capitalize()}", description="\n\n".join(lines))
    await interaction.followup.send(embed=embed)


# ---------- /relics refresh & status ----------

async def _run_refresh(sendable, refinement_v: str, force_catalog_refresh: bool) -> tuple[bool, str]:
    """
    Does the actual fetch-with-progress work shared by a manual one-time
    refresh and every auto-refresh cycle. `sendable` is anything with an
    async `.send(...)` that returns an object with an async `.edit(...)`
    - both `interaction.followup` and a plain `discord.TextChannel` satisfy
    that, so the same code path posts progress into an interaction
    response or straight into a channel picked for auto-refresh.
    Returns (success, human-readable summary) so callers (e.g. the
    auto-refresh loop) can track outcomes without re-parsing embed text.
    """
    if not state.relics:
        state.load_relics()
    n_relics = len(state.relics)

    already_running = state.live_market is not None
    if already_running:
        progress_msg = await sendable.send(view=RefreshStopView(), embed=discord.Embed(
            title=f"🔄 Refreshing · {refinement_v.capitalize()}",
            description="Live market is already running - rebuilding from current data (instant).",
            color=fmt.BRAND_COLOR,
        ))
    else:
        progress_msg = await sendable.send(view=RefreshStopView(), embed=discord.Embed(
            title=f"🔄 Starting live market · {refinement_v.capitalize()}",
            description=(
                f"Bootstrapping full order books for **{n_relics}** relics and their rewards, "
                f"concurrently. This is a one-time cost - after this, refreshes are instant."
            ),
            color=fmt.BRAND_COLOR,
        ))

    loop = asyncio.get_running_loop()
    started = time.time()
    last_edit = {"t": 0.0}
    progress_tasks = set()

    async def edit_progress(embed):
        try:
            await progress_msg.edit(embed=embed)
        except discord.HTTPException:
            pass

    def progress_cb(done, total):
        # Called from the SAME event loop (live_market's bootstrap is a
        # real coroutine, not a thread executor like the old pipeline
        # was) - still throttled so we don't spam Discord's message-edit
        # rate limit, and the final tick always gets through.
        now = time.time()
        is_last_tick = done >= total
        if now - last_edit["t"] < 2.5 and not is_last_tick:
            return
        last_edit["t"] = now
        frac = (done / total) if total else 1.0
        elapsed = now - started
        eta = (elapsed * (1 - frac) / frac) if frac > 0 else None
        eta_str = f"~{eta:.0f}s left" if eta is not None else "estimating..."
        embed = discord.Embed(
            title=f"🔄 Bootstrapping live market · {refinement_v.capitalize()}",
            description=(
                f"Fetching order books: {done}/{total} unique items ({frac * 100:.0f}%)\n"
                f"{eta_str} · elapsed {elapsed:.0f}s"
            ),
            color=fmt.BRAND_COLOR,
        )
        task = loop.create_task(edit_progress(embed))
        progress_tasks.add(task)
        task.add_done_callback(progress_tasks.discard)

    try:
        snap = await state.refresh(refinement_v, force_catalog_refresh=force_catalog_refresh, progress_cb=progress_cb)
    except asyncio.CancelledError:
        for task in progress_tasks:
            task.cancel()
        await asyncio.gather(*progress_tasks, return_exceptions=True)
        try:
            await progress_msg.edit(embed=discord.Embed(
                title="🛑 Relic refresh stopped", description="An admin can restart with /relics refresh auto:Start.",
                color=fmt.BRAND_COLOR), view=None)
        except discord.HTTPException:
            pass
        raise
    except Exception as e:  # noqa: BLE001
        await progress_msg.edit(embed=discord.Embed(
            title="❌ Refresh failed", description=str(e), color=fmt.ERROR_COLOR,
        ))
        return False, str(e)
    finally:
        for task in progress_tasks:
            task.cancel()
        await asyncio.gather(*progress_tasks, return_exceptions=True)

    elapsed = time.time() - started
    market = state.live_market
    bootstrap_note = (
        f" (bootstrap took {market.bootstrap_seconds:.0f}s)"
        if market and market.bootstrap_seconds and not already_running else ""
    )
    desc = f"**{len(snap.relics)}** relics at **{refinement_v.capitalize()}** in {elapsed:.0f}s{bootstrap_note}.\n"
    desc += f"Item catalog: {snap.catalog_source}."
    if market and market.reconciler and market.reconciler.bootstrap_failures:
        desc += f"\n⚠ {len(market.reconciler.bootstrap_failures)} item(s) failed to fetch and will be retried automatically."
    if snap.unmatched_rewards:
        desc += f"\n{len(snap.unmatched_rewards)} reward(s) had no price match."
    if snap.no_relic_price:
        desc += f"\n{len(snap.no_relic_price)} relic(s) had no {refinement_v} listing online."
    await progress_msg.edit(embed=discord.Embed(title="✅ Refresh complete", description=desc, color=fmt.SUCCESS_COLOR))
    # The seven persistent list channels are edited in place. They are not
    # new price snapshots or cached pricing; they are just Discord views of
    # the live in-memory order books.
    try:
        await live_lists.update_all()
    except Exception as e:  # noqa: BLE001
        print(f"[live-lists] update failed: {str(e) or type(e).__name__}")
    return True, desc


refresh_controller = RefreshController(state, _run_refresh)
automation = GuildAutomation(bot, world_feed, live_lists, refresh_controller)


async def _start_auto_refresh(
    channel: discord.abc.Messageable, guild_id: int | None, interval_minutes: float,
    refinement_v: str, force_catalog_refresh: bool,
) -> None:
    await refresh_controller.start(SingleRefreshOutput(channel), guild_id, interval_minutes, refinement_v, force_catalog_refresh)


def _can_manage_refresh(interaction):
    return interaction.guild is not None and interaction.user.guild_permissions.manage_guild


async def _kill_refresh(interaction):
    if not _can_manage_refresh(interaction):
        await interaction.response.send_message("Manage Server permission is required to stop shared pricing.", ephemeral=True)
        return
    await interaction.response.defer(ephemeral=True)
    was_running = await refresh_controller.stop()
    _refresh_cooldowns.clear()  # allow immediate restart with new settings
    await interaction.followup.send(
        ("🛑 Relic refresh and its pricing worker stopped." if was_running else "Relic pricing is already stopped.")
        + " Lists stay visible. Use `/relics refresh auto:Start` with your new settings to restart. "
        "This affects all servers using this bot; mission and Riven feeds continue.", ephemeral=True)


class RefreshStopView(discord.ui.View):
    def __init__(self):
        super().__init__(timeout=None)

    @discord.ui.button(label="Stop relic refresh", style=discord.ButtonStyle.danger,
                       custom_id="relicframe:stop-refresh")
    async def stop_button(self, interaction, button):
        await _kill_refresh(interaction)


@relics_group.command(name="stop", description="Kill shared relic refresh and pricing; allows an immediate restart with new settings.")
@app_commands.default_permissions(manage_guild=True)
async def relics_stop(interaction: discord.Interaction):
    await _kill_refresh(interaction)


@relics_group.command(
    name="refresh",
    description="Re-fetch live prices for every relic, or start/stop a recurring auto-refresh.",
)
@app_commands.choices(refinement=REFINEMENT_CHOICES, auto=AUTO_CHOICES)
@app_commands.describe(
    refinement="Refinement tier to fetch prices at (default: the snapshot's current tier).",
    force_catalog_refresh="Ignore the cached item catalog and re-download it from Warframe Market.",
    auto="Manage a recurring background refresh instead of doing a one-time refresh.",
    channel="Where auto-refresh summaries get posted (auto:Start only - defaults to this channel).",
    interval_minutes="Minutes between auto-refresh cycles, 1-1440 (auto:Start only, default 1).",
)
async def relics_refresh(
    interaction: discord.Interaction,
    refinement: app_commands.Choice[str] | None = None,
    force_catalog_refresh: bool = True,
    auto: app_commands.Choice[str] | None = None,
    channel: discord.TextChannel | None = None,
    interval_minutes: app_commands.Range[float, 1, 1440] = 1.0,
):
    refinement_v = refinement.value if refinement else state.default_refinement

    # Checking or stopping auto-refresh never touches the network, so
    # neither is subject to the fetch cooldown below.
    if auto is not None and auto.value == "status":
        cfg = state.auto_refresh
        if not cfg.enabled:
            await interaction.response.send_message(
                "Auto-refresh is currently **off**. Use `auto:Start` to enable it.", ephemeral=True,
            )
            return
        if cfg.last_run is None:
            last = "first cycle hasn't completed yet"
        else:
            last = f"{fmt.age_str(time.time() - cfg.last_run)} ({cfg.last_status})"
        await interaction.response.send_message(embed=discord.Embed(
            title="🔁 Auto-refresh status",
            description=(
                f"Every **{cfg.interval_minutes:.0f} min** into "
                f"{f'<#{cfg.channel_id}>' if cfg.channel_id else 'all configured #refresh channels'}, "
                f"at **{cfg.refinement.capitalize()}**.\nLast cycle: {last}."
            ),
            color=fmt.BRAND_COLOR,
        ), ephemeral=True)
        return

    if auto is not None and auto.value == "off":
        await _kill_refresh(interaction)
        return

    if not _can_manage_refresh(interaction):
        await interaction.response.send_message("Manage Server permission is required to change shared pricing.", ephemeral=True)
        return

    # Both a plain one-time refresh and starting auto-refresh trigger an
    # immediate fetch, so both share the per-guild cooldown.
    if not _cooldown_ready(interaction.guild_id):
        await interaction.response.send_message(
            f"A refresh was run recently in this server - try again in "
            f"{_cooldown_remaining(interaction.guild_id):.0f}s.",
            ephemeral=True,
        )
        return
    _mark_cooldown(interaction.guild_id)

    if auto is not None and auto.value == "on":
        target_channel = channel or interaction.channel
        await interaction.response.defer()
        await _start_auto_refresh(target_channel, interaction.guild_id, interval_minutes, refinement_v, force_catalog_refresh)
        where = "" if target_channel.id == interaction.channel.id else f" into {target_channel.mention}"
        await interaction.followup.send(
            f"🔁 Auto-refresh started: every **{interval_minutes:.0f} min**{where}, "
            f"at **{refinement_v.capitalize()}**. Running the first cycle now..."
        )
        return

    await interaction.response.defer()
    try:
        await refresh_controller.run_once(interaction.followup, refinement_v, force_catalog_refresh)
    except RuntimeError as exc:
        await interaction.followup.send(str(exc), ephemeral=True)


@relics_group.command(name="status", description="Age of the cached price snapshot, and auto-refresh status.")
async def relics_status(interaction: discord.Interaction):
    snap = state.snapshot
    market = state.live_market
    if snap is None:
        age = None
        desc = (
            f"Loaded relic definitions: **{len(state.relics):,}**\n"
            "Price snapshot: **not started** — run `/relics refresh`."
        )
    else:
        age = time.time() - snap.fetched_at
        desc = (
            f"**{len(snap.relics)}** relics at **{snap.refinement.capitalize()}**, fetched {fmt.age_str(age)}.\n"
            f"Catalog: {snap.catalog_source} ({fmt.age_str(snap.catalog_age_seconds)})."
        )
    if market and market.reconciler:
        failures = market.reconciler.bootstrap_failures
        desc += (
            f"\nLive order books: **{market.bootstrap_slug_count:,} tracked** · "
            f"**{len(failures):,} pending retry**"
        )
    elif market:
        desc += "\nLive market: **starting**"
    else:
        desc += "\nLive market: **stopped**"
    if state._last_error:
        desc += f"\nLast market error: `{state._last_error[:400]}`"
    if live_lists.last_errors:
        desc += f"\nPersistent list errors: **{len(live_lists.last_errors)}** · `{live_lists.last_errors[0][:300]}`"
    cfg = state.auto_refresh
    if cfg.enabled:
        desc += (
            f"\n🔁 Auto-refresh: every **{cfg.interval_minutes:.0f} min** into "
            f"{f'<#{cfg.channel_id}>' if cfg.channel_id else 'all configured #refresh channels'} "
            f"at **{cfg.refinement.capitalize()}**."
        )
        if cfg.last_status == "error" and cfg.last_error:
            desc += f"\nLast auto-refresh error: `{cfg.last_error[:350]}`"
    else:
        desc += "\n🔁 Auto-refresh: off."
    healthy = not state._last_error and not live_lists.last_errors
    color = fmt.freshness_color(age) if age is not None and healthy else (fmt.BRAND_COLOR if healthy else fmt.ERROR_COLOR)
    embed = discord.Embed(title="📈 Relic market status", description=desc, color=color)
    await interaction.response.send_message(embed=embed)


# ---------- seller blacklist ----------
# Excludes a specific WFM seller's listings from every relic price
# computation bot-wide (all guilds, online + offline, list/detail/buy-n) -
# not a display filter. See seller_blacklist.py's module docstring.

@relics_group.command(name="blacklist-add", description="Stop a WFM seller's listings from being used anywhere in the bot.")
@app_commands.describe(username="The seller's exact Warframe Market in-game name")
async def relics_blacklist_add(interaction: discord.Interaction, username: str):
    if not interaction.user.guild_permissions.manage_channels:
        await interaction.response.send_message("You need **Manage Channels** to manage the seller blacklist.", ephemeral=True)
        return
    added = state.seller_blacklist.add(username)
    if not added:
        await interaction.response.send_message(f"`{username}` is already blacklisted.", ephemeral=True)
        return
    # Drops their already-cached listings from every currently-tracked
    # slug right now, instead of waiting for each one's next
    # reconciliation pass to naturally pick up the exclusion.
    removed_orders = state.live_market.store.purge_seller(username) if state.live_market else 0
    await interaction.response.send_message(
        f"✅ Blacklisted `{username}`. Removed {removed_orders} already-cached listing(s) immediately — "
        "their listings won't be used for pricing anywhere in the bot going forward.",
        ephemeral=True,
    )


@relics_group.command(name="blacklist-remove", description="Un-blacklist a WFM seller.")
@app_commands.describe(username="The seller's exact Warframe Market in-game name")
async def relics_blacklist_remove(interaction: discord.Interaction, username: str):
    if not interaction.user.guild_permissions.manage_channels:
        await interaction.response.send_message("You need **Manage Channels** to manage the seller blacklist.", ephemeral=True)
        return
    removed = state.seller_blacklist.remove(username)
    if removed:
        await interaction.response.send_message(
            f"✅ Removed `{username}` from the blacklist. Their listings will reappear on the next "
            "reconciliation pass for each affected relic (not instant, since nothing was cached to restore).",
            ephemeral=True,
        )
    else:
        await interaction.response.send_message(f"`{username}` wasn't on the blacklist.", ephemeral=True)


@relics_group.command(name="blacklist-list", description="Show every WFM seller currently blacklisted from the bot's listings.")
async def relics_blacklist_list(interaction: discord.Interaction):
    names = state.seller_blacklist.list_names()
    if not names:
        await interaction.response.send_message("The seller blacklist is empty.", ephemeral=True)
        return
    listing = "\n".join(f"- `{n}`" for n in names)
    await interaction.response.send_message(f"**Blacklisted sellers ({len(names)}):**\n{listing}", ephemeral=True)


# ---------- /relics help ----------

@relics_group.command(name="help", description="Quick guide to every /relics command and what the colors/emoji mean.")
async def relics_help(interaction: discord.Interaction):
    embed = discord.Embed(
        title="📖 RelicFrame - Quick Guide",
        description=(
            "RelicFrame prices every relic against Warframe Market and ranks which ones are "
            "actually worth opening right now. Start with **/relics refresh**, then **/relics list**."
        ),
        color=fmt.BRAND_COLOR,
    )
    embed.add_field(
        name="🔄 Getting prices",
        value=(
            "`/relics refresh` - update the live market and all configured list channels.\n"
            "`/relics setup-list` - create/repair the seven persistent live list channels.\n"
            "`/relics refresh auto:Start channel:#refresh interval_minutes:1` - keep it fresh automatically, "
            "posting a summary into the channel you pick.\n"
            "`/relics stop` / `/relics refresh auto:Stop` - kill shared relic pricing and allow an immediate restart.\n"
            "`/relics refresh auto:Status` - inspect the refresh loop.\n"
            "`/relics status` - age of the cached snapshot + auto-refresh state."
        ),
        inline=False,
    )
    embed.add_field(
        name="📊 Browsing relics",
        value=(
            "The persistent `THE LIST` channels are always Online-only, Trace rate 0, and update in place on refresh.\n"
            "`/relics list` - interactive ranked table (dropdowns + a Filters button), paginated.\n"
            "`/relics detail relic:<name>` - full online/offline breakdown for one relic.\n"
            "`/relics find item:<name>` - which relics drop a specific item, cheapest first.\n"
            "`/relics odds relic:<name> reward:<name>` - pull-chance table across 1-50 opens.\n"
            "`/relics compare relic:<name>` - Intact → Radiant side-by-side, which tier to refine to.\n"
            "`/relics buyn relic:<name> n:<count>` - real cost of buying N of a relic from the order book."
        ),
        inline=False,
    )
    embed.add_field(
        name="🚫 Seller blacklist",
        value=(
            "`/relics blacklist-add username:<name>` - stop a seller's listings from being used anywhere in the bot.\n"
            "`/relics blacklist-remove username:<name>` - un-blacklist a seller.\n"
            "`/relics blacklist-list` - show every currently blacklisted seller."
        ),
        inline=False,
    )
    embed.add_field(
        name="🎨 What the colors/emoji mean",
        value=(
            "🟢 Guaranteed profit · 🟡 Expected profit only · 🔴 Guaranteed loss · ⚪ Unknown/no price\n"
            "🟠 Vaulted · 🟢 Unvaulted (vault dot) · ⚪ Vault status unknown\n"
            "Risk score: 🟢 low · 🟡 medium · 🔴 high\n"
            "`~` after a price = fallback match, not an exact subtype match · "
            "⚠ = an outlier price excluded from normal averaging"
        ),
        inline=False,
    )
    embed.set_footer(text="Read #bot-guide for all features and setup instructions.")
    await interaction.response.send_message(embed=embed, ephemeral=True)


bot.tree.add_command(relics_group)
bot.tree.add_command(world_group)
bot.tree.add_command(companion_group)
bot.tree.add_command(riven_group)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--auto-refresh-minutes", type=float, default=1.0,
                         help="Refresh interval in minutes (1-1440, default 1).")
    parser.add_argument("--auto-refresh-channel-id", type=int, default=None,
                         help="Channel ID to post auto-refresh summaries into (used with "
                              "--auto-refresh-minutes). Falls back to the AUTO_REFRESH_CHANNEL_ID "
                              "env var, then to each server's #refresh channel. Can "
                              "also just be set later via `/relics refresh auto:Start channel:...`.")
    parser.add_argument("--refinement", default="radiant", choices=REFINEMENTS)
    args = parser.parse_args()
    if not 1 <= args.auto_refresh_minutes <= 1440:
        parser.error("--auto-refresh-minutes must be between 1 and 1440")

    state.default_refinement = args.refinement
    token = os.environ.get("DISCORD_BOT_TOKEN")
    if not token:
        raise SystemExit("Set DISCORD_BOT_TOKEN in your environment first.")

    automation.minutes = args.auto_refresh_minutes
    automation.refinement = args.refinement
    automation.channel_id = args.auto_refresh_channel_id or (
        int(os.environ["AUTO_REFRESH_CHANNEL_ID"]) if os.environ.get("AUTO_REFRESH_CHANNEL_ID") else None
    )

    bot.run(token)


if __name__ == "__main__":
    main()
