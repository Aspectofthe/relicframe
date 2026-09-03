"""Startup provisioning and one cancellable, shared relic refresh worker."""
import asyncio
import time

import discord


class RefreshController:
    def __init__(self, state, runner):
        self.state = state
        self.runner = runner
        self.task = None
        self.manual = set()
        self.lock = asyncio.Lock()
        self.configured = False

    async def _stop(self):
        tasks = list(self.manual)
        if self.task is not None:
            tasks.append(self.task)
        running = any(not task.done() for task in tasks) or self.state.live_market is not None
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        self.task = None
        self.manual.clear()
        self.state.auto_refresh.enabled = False
        await self.state.close()
        return running

    async def stop(self):
        async with self.lock:
            self.configured = True  # joins/reconnects must not undo a user's stop
            return await self._stop()

    async def start(self, channel, guild_id, minutes=1.0, refinement="radiant", force=True, *, default=False):
        async with self.lock:
            if default and self.configured:
                return False
            await self._stop()
            self.configured = True
            cfg = self.state.auto_refresh
            cfg.enabled = True
            cfg.channel_id = getattr(channel, "id", None)
            cfg.guild_id = guild_id
            cfg.interval_minutes = minutes
            cfg.refinement = refinement
            cfg.force_catalog_refresh = force
            cfg.last_run = cfg.last_status = cfg.last_error = None
            self.task = asyncio.create_task(self._worker(channel, minutes, refinement, force))
            return True

    async def run_once(self, channel, refinement, force):
        async with self.lock:
            self.configured = True
            if self.task is not None and not self.task.done():
                raise RuntimeError("Stop the shared auto-refresh with /relics stop before a one-time refresh.")
            if self.manual:
                raise RuntimeError("A refresh is already running. Use /relics stop to cancel it.")
            task = asyncio.create_task(self.runner(channel, refinement, force))
            self.manual.add(task)
        try:
            return await task
        finally:
            self.manual.discard(task)

    async def _worker(self, channel, minutes, refinement, force):
        while True:
            try:
                ok, summary = await self.runner(channel, refinement, force)
                self.state.auto_refresh.last_status = "ok" if ok else "error"
                self.state.auto_refresh.last_error = None if ok else summary
            except asyncio.CancelledError:
                raise
            except Exception as exc:
                self.state.auto_refresh.last_status = "error"
                self.state.auto_refresh.last_error = str(exc) or type(exc).__name__
                print(f"[auto-refresh] {self.state.auto_refresh.last_error}")
            self.state.auto_refresh.last_run = time.time()
            await asyncio.sleep(minutes * 60)


class RefreshBroadcast:
    """Reuse each guild's refresh message and share a single price fetch."""
    id = None

    def __init__(self, automation):
        self.automation = automation

    async def send(self, **kwargs):
        messages = []
        for guild_id, channel in list(self.automation.outputs.items()):
            cfg = self.automation.world._guild(guild_id).setdefault("refresh", {})
            try:
                message = None
                if cfg.get("message_id"):
                    try:
                        message = await channel.fetch_message(cfg["message_id"])
                    except discord.NotFound:
                        pass
                if message is None:
                    message = await channel.send(**kwargs, allowed_mentions=discord.AllowedMentions.none())
                else:
                    await message.edit(**kwargs, allowed_mentions=discord.AllowedMentions.none())
                cfg["message_id"] = message.id
                messages.append(message)
            except discord.HTTPException as exc:
                print(f"[auto-refresh] output for guild {guild_id} failed: {exc}")
        self.automation.world._save()
        if not messages:
            raise RuntimeError("No accessible #refresh channels; check Send Messages and Embed Links permissions.")
        return BroadcastMessage(messages)


class SingleRefreshOutput:
    """Edit a single summary for an explicitly selected auto-refresh channel."""
    def __init__(self, channel):
        self.channel = channel
        self.id = channel.id
        self.message = None

    async def send(self, **kwargs):
        if self.message is not None:
            try:
                await self.message.edit(**kwargs, allowed_mentions=discord.AllowedMentions.none())
                return self.message
            except discord.NotFound:
                self.message = None
        self.message = await self.channel.send(**kwargs, allowed_mentions=discord.AllowedMentions.none())
        return self.message


class BroadcastMessage:
    def __init__(self, messages):
        self.messages = messages

    async def edit(self, **kwargs):
        results = await asyncio.gather(*(message.edit(**kwargs) for message in self.messages), return_exceptions=True)
        for result in results:
            if isinstance(result, Exception):
                print(f"[auto-refresh] progress message failed: {result}")


class GuildAutomation:
    def __init__(self, bot, world, lists, refresh):
        self.bot, self.world, self.lists, self.refresh = bot, world, lists, refresh
        self.outputs = {}
        self.prepared = set()
        self.lock = asyncio.Lock()
        self.minutes = 1.0
        self.refinement = "radiant"
        self.channel_id = None

    async def prepare(self, guild):
        if guild.id in self.prepared or getattr(guild, "unavailable", False):
            return
        success = True
        for label, manager in (("world", self.world), ("relic lists", self.lists)):
            try:
                await manager.setup_guild(guild, automatic=True)
            except Exception as exc:
                success = False
                print(f"[auto-setup] {guild.id} {label}: {exc}. Check bot channel/role permissions.")
        try:
            cfg = self.world._guild(guild.id).setdefault("refresh", {})
            category_id = self.world._guild(guild.id).get("category_id")
            category = guild.get_channel(category_id or 0)
            channel = guild.get_channel(cfg.get("channel_id") or 0)
            if not isinstance(channel, discord.TextChannel):
                channel = discord.utils.get(getattr(category, "text_channels", []), name="refresh")
            if channel is None:
                channel = await guild.create_text_channel("refresh", category=category,
                    topic="Relic pricing status. Admin kill switch: /relics stop", reason="RelicFrame automatic refresh")
            cfg["channel_id"] = channel.id
            self.outputs[guild.id] = channel
            self.world._save()
        except Exception as exc:
            success = False
            print(f"[auto-setup] {guild.id} refresh channel: {exc}")
        if success:
            self.prepared.add(guild.id)

    async def startup(self, guilds=None):
        async with self.lock:
            for guild in list(self.bot.guilds if guilds is None else guilds):
                await self.prepare(guild)
            if self.outputs and not self.refresh.configured:
                channel = RefreshBroadcast(self)
                guild_id = None
                if self.channel_id:
                    try:
                        channel = self.bot.get_channel(self.channel_id) or await self.bot.fetch_channel(self.channel_id)
                        guild_id = getattr(getattr(channel, "guild", None), "id", None)
                        channel = SingleRefreshOutput(channel)
                    except discord.HTTPException as exc:
                        print(f"[auto-setup] configured refresh channel failed: {exc}; using #refresh channels")
                await self.refresh.start(channel, guild_id, self.minutes, self.refinement, True, default=True)

    def remove(self, guild_id):
        self.outputs.pop(guild_id, None)
        self.prepared.discard(guild_id)
