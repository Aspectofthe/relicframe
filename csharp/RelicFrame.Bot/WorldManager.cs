using System.Security.Cryptography;
using System.Text;
using Discord;
using Discord.WebSocket;
using RelicFrame.Core;

internal sealed record ChannelState
{
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
    public string RenderHash { get; set; } = "";
}
internal sealed record WorldGuildState
{
    public ulong CategoryId { get; set; }
    public Dictionary<string, ChannelState> Channels { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, ulong> Roles { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string[]>? Signatures { get; set; }
    public Dictionary<string, ulong> PingMessages { get; init; } = new(StringComparer.Ordinal);
    public List<ulong> RoleMenuMessages { get; init; } = [];
}
internal sealed record WorldStore
{
    public Dictionary<string, WorldGuildState> Guilds { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class WorldManager : IAsyncDisposable
{
    internal const string CategoryName = "WARFRAME LIVE";
    internal static readonly IReadOnlyDictionary<string, (string Name, string Topic)> Channels =
        new Dictionary<string, (string, string)>
        {
            ["bot-guide"]=("bot-guide","How to use RelicFrame and every feature"), ["world-pings"]=("role-pings","Choose your notification roles"),
            ["world-cycles"]=("world-cycles","Environments"), ["world-news"]=("warframe-news","News and KinePage"), ["world-alerts"]=("alerts","Alerts and Events"),
            ["world-sortie"]=("sortie","Daily Sortie"), ["world-archon"]=("archon","Weekly Archon Hunt"), ["world-steel-path"]=("steel-path","Daily Steel Path Incursions"),
            ["world-weekly"]=("weekly","Weekly missions and Circuit choices"), ["world-archimedea"]=("archimedea","Deep and Temporal Archimedea"),
            ["world-vendors"]=("vendors","Darvo, Baro, Steel Path Honors, Iron Wake"), ["world-bounties"]=("bounties","Zariman, Cavia, and Hex bounties"),
            ["world-fissures"]=("fissures","Normal Void Fissures"), ["world-steel-fissures"]=("steel-fissures","Steel Path Void Fissures"),
            ["world-void-storms"]=("void-storms","Railjack Void Storms"), ["world-invasions"]=("invasions","Active invasions"),
            ["world-arbitration"]=("arbitration","Current and upcoming Arbitrations"), ["world-cascade"]=("cascade","Normal and Steel Path Void Cascade fissures")
        };
    internal static readonly IReadOnlyDictionary<string, (string Name, string Emoji, string Channel)> Roles = BuildRoles();
    private static IReadOnlyDictionary<string, (string, string, string)> BuildRoles()
    {
        var roles = new Dictionary<string, (string, string, string)>
        {
            ["cascade"]=("Normal Cascade Fissure Ping","🌊","world-cascade"), ["steel_cascade"]=("Steel Path Cascade Fissure Ping","🌊","world-cascade"),
            ["arbitration"]=("Arbitration Ping","⚖️","world-arbitration"), ["alerts"]=("Alerts Ping","🚨","world-alerts"), ["events"]=("Events Ping","🎉","world-alerts"),
            ["sortie"]=("Sortie Ping","🎯","world-sortie"), ["archon"]=("Archon Hunt Ping","🐺","world-archon"), ["steel_path"]=("Steel Path Ping","💀","world-steel-path"),
            ["bounties"]=("Bounties Ping","📋","world-bounties"), ["fissures"]=("Fissures Ping","🌀","world-fissures"), ["steel_fissures"]=("Steel Fissures Ping","☠️","world-steel-fissures"),
            ["void_storms"]=("Void Storms Ping","🚀","world-void-storms"), ["invasions"]=("Invasions Ping","⚔️","world-invasions"), ["baro"]=("Baro Ki'Teer Ping","🛒","world-vendors"),
            ["news"]=("Warframe News Ping","📰","world-news"), ["archimedea"]=("Archimedea Ping","🧬","world-archimedea")
        };
        foreach (var (tier, emoji) in new[] { ("S","💎"),("A","🟥"),("B","🟧"),("C","🟨"),("D","🟩"),("F","⬜") })
            roles["arbitration_" + tier.ToLowerInvariant()] = ($"Arbitration Tier {tier} Ping", emoji, "world-arbitration");
        foreach (var (tier, emoji) in new[] { ("Lith","🟤"),("Meso","🟡"),("Neo","⚪"),("Axi","🟠"),("Requiem","🔴"),("Omnia","🟣") })
        {
            roles["fissure_" + tier.ToLowerInvariant()] = ($"Normal Fissure {tier} Ping", emoji, "world-fissures");
            roles["steel_fissure_" + tier.ToLowerInvariant()] = ($"Steel Fissure {tier} Ping", emoji, "world-steel-fissures");
            roles["void_storm_" + tier.ToLowerInvariant()] = ($"Void Storm {tier} Ping", emoji, "world-void-storms");
        }
        return roles;
    }
    private readonly DiscordSocketClient bot;
    private readonly ulong targetGuild;
    private readonly string statePath;
    private readonly ArbitrationEntry[] schedule;
    private readonly WorldStateClient client;
    private readonly SemaphoreSlim refresh = new(1, 1);
    private readonly SemaphoreSlim setup = new(1, 1);
    private WorldStore store;
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private string status = "stopped";
    private DateTimeOffset? lastSuccess;
    public string Status => Volatile.Read(ref status);
    public DateTimeOffset? LastSuccess => lastSuccess;
    public WorldManager(DiscordSocketClient bot, ulong targetGuild, string runtime, string schedulePath)
    {
        this.bot = bot; this.targetGuild = targetGuild; statePath = Path.Combine(runtime, "world_state.json");
        schedule = ArbitrationSchedule.Load(schedulePath, Environment.GetEnvironmentVariable("WORLD_STATE_TIMEZONE") ?? "America/New_York");
        try { store = File.Exists(statePath) ? Json.Read<WorldStore>(statePath) : new(); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { store = new(); status = "saved state unreadable; setup required"; }
        client = new(); bot.ButtonExecuted += HandleButtonAsync;
    }
    private WorldGuildState State(ulong guild) => store.Guilds.GetValueOrDefault(guild.ToString()) ?? (store.Guilds[guild.ToString()] = new());
    private void Save() => Json.WriteAtomic(statePath, store);
    public async Task<string> SetupAsync(SocketGuild guild, CancellationToken ct)
    {
        if (guild.Id != targetGuild) return "C# preview setup is restricted to its configured test guild.";
        if (!guild.CurrentUser.GuildPermissions.ManageChannels || !guild.CurrentUser.GuildPermissions.ManageRoles)
            return "I need Manage Channels and Manage Roles before I can configure the live-feed category.";
        await setup.WaitAsync(ct);
        try
        {
            var state = State(guild.Id);
            ICategoryChannel? category = guild.GetCategoryChannel(state.CategoryId) ?? guild.CategoryChannels.FirstOrDefault(c => c.Name == CategoryName);
            category ??= await guild.CreateCategoryChannelAsync(CategoryName);
            state.CategoryId = category.Id;
            foreach (var (key, definition) in Channels)
            {
                var saved = state.Channels.GetValueOrDefault(key) ?? (state.Channels[key] = new());
                ITextChannel? channel = guild.GetTextChannel(saved.ChannelId) ?? guild.TextChannels.FirstOrDefault(c => c.CategoryId == category.Id && (c.Name == definition.Name || c.Name == key));
                if (channel is null) channel = await guild.CreateTextChannelAsync(definition.Name, p => { p.CategoryId = category.Id; p.Topic = definition.Topic; });
                else if (channel.Name != definition.Name || channel.Topic != definition.Topic) await channel.ModifyAsync(p => { p.Name = definition.Name; p.Topic = definition.Topic; p.CategoryId = category.Id; });
                saved.ChannelId = channel.Id;
            }
            foreach (var (key, definition) in Roles)
            {
                state.Roles.TryGetValue(key, out var id); IRole? role = guild.GetRole(id) ?? guild.Roles.FirstOrDefault(r => r.Name == definition.Name);
                if (role is null && key == "cascade") role = guild.Roles.FirstOrDefault(r => r.Name == "Void Cascade Ping");
                if (role is null) role = await guild.CreateRoleAsync(definition.Name, GuildPermissions.None, isMentionable: true);
                else if ((role.Name != definition.Name || !role.IsMentionable) && role is SocketRole socketRole) await socketRole.ModifyAsync(p => { p.Name = definition.Name; p.Mentionable = true; });
                state.Roles[key] = role.Id;
            }
            Save(); await UpdateRoleMenusAsync(guild, state); return $"Configured {Channels.Count} live channels and {Roles.Count} opt-in roles in {CategoryName}. Old messages authored by this bot are removed as each managed board refreshes; user messages are preserved.";
        }
        finally { setup.Release(); }
    }
    private async Task UpdateRoleMenusAsync(SocketGuild guild, WorldGuildState state)
    {
        var channel = guild.GetTextChannel(state.Channels["world-pings"].ChannelId) ?? throw new InvalidOperationException("Role-pings channel is missing.");
        var groups = Roles.Chunk(20).ToArray();
        for (var index = 0; index < groups.Length; index++)
        {
            var components = new ComponentBuilder(); var row = 0; var position = 0;
            foreach (var (key, role) in groups[index])
            {
                if (position == 5) { position = 0; row++; }
                components.WithButton(role.Name.Replace(" Ping", ""), "rf-role:" + key, ButtonStyle.Secondary, new Emoji(role.Emoji), row: row); position++;
            }
            IUserMessage? message = null;
            if (index < state.RoleMenuMessages.Count) message = await channel.GetMessageAsync(state.RoleMenuMessages[index]) as IUserMessage;
            var content = index == 0 ? "Use these buttons to add or remove your own notification roles. New activity is pinged once, not every refresh." : "More notification roles:";
            if (message is null)
            {
                message = await channel.SendMessageAsync(content, components: components.Build(), allowedMentions: AllowedMentions.None);
                if (index < state.RoleMenuMessages.Count) state.RoleMenuMessages[index] = message.Id; else state.RoleMenuMessages.Add(message.Id);
            }
            else await message.ModifyAsync(p => { p.Content = content; p.Components = components.Build(); p.AllowedMentions = AllowedMentions.None; });
        }
        await DiscordCleanup.BotMessagesAsync(channel, guild.CurrentUser.Id, state.RoleMenuMessages.ToArray());
        Save();
    }
    private async Task HandleButtonAsync(SocketMessageComponent interaction)
    {
        if (interaction.GuildId != targetGuild || !interaction.Data.CustomId.StartsWith("rf-role:", StringComparison.Ordinal)) return;
        var key = interaction.Data.CustomId[8..]; if (!Roles.ContainsKey(key) || interaction.User is not SocketGuildUser user) { await interaction.RespondAsync("Role unavailable.", ephemeral: true); return; }
        var state = State(targetGuild); var guild = bot.GetGuild(targetGuild); var role = guild?.GetRole(state.Roles.GetValueOrDefault(key));
        if (role is null) { await interaction.RespondAsync("That role is missing. Ask a manager to run /rf-world setup.", ephemeral: true); return; }
        await interaction.DeferAsync(ephemeral: true);
        if (user.Roles.Any(r => r.Id == role.Id)) { await user.RemoveRoleAsync(role); await interaction.FollowupAsync($"Removed **{role.Name}**.", ephemeral: true); }
        else { await user.AddRoleAsync(role); await interaction.FollowupAsync($"Added **{role.Name}**.", ephemeral: true); }
    }
    public void Start(CancellationToken lifetime)
    {
        if (worker is { IsCompleted: false }) return; cancellation?.Dispose(); cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        worker = Task.Run(() => LoopAsync(cancellation.Token));
    }
    public async Task StopAsync()
    {
        if (cancellation is not null) await cancellation.CancelAsync(); if (worker is not null) await worker;
        status = "stopped; last boards retained";
    }
    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var started = DateTimeOffset.UtcNow;
                try { await RefreshAsync(ct); }
                catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or System.Text.Json.JsonException or IOException or InvalidOperationException or Discord.Net.HttpException or TaskCanceledException)
                { status = $"refresh failed ({e.GetType().Name}); boards retained"; Console.WriteLine($"[world] {status}"); }
                var wait = TimeSpan.FromSeconds(60) - (DateTimeOffset.UtcNow - started); await Task.Delay(wait > TimeSpan.FromSeconds(5) ? wait : TimeSpan.FromSeconds(5), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public async Task RefreshAsync(CancellationToken ct)
    {
        await refresh.WaitAsync(ct);
        try
        {
            status = "fetching"; var data = await client.FetchAsync(ct); var guild = bot.GetGuild(targetGuild) ?? throw new InvalidOperationException("Test guild unavailable.");
            var state = State(targetGuild); if (state.Channels.Count == 0) throw new InvalidOperationException("Run /rf-world setup.");
            var failures = new List<string>();
            foreach (var key in Channels.Keys.Where(k => k != "world-pings"))
            {
                try { await UpsertBoardAsync(guild, state, key, WorldRender.Build(key, data, schedule), guild.CurrentUser.Id, state.PingMessages.GetValueOrDefault(key)); }
                catch (Exception e) when (e is Discord.Net.HttpException or InvalidOperationException) { failures.Add($"{key}: {e.GetType().Name}"); }
            }
            try { await SendNewPingsAsync(guild, state, WorldRender.Signatures(data, schedule)); }
            catch (Exception e) when (e is Discord.Net.HttpException or InvalidOperationException) { failures.Add("pings: " + e.GetType().Name); }
            lastSuccess = DateTimeOffset.UtcNow; status = failures.Count == 0 ? $"ready ({data.Source})" : $"partial: {failures.Count} board/ping failures; {failures[0]}"; Save();
        }
        finally { refresh.Release(); }
    }
    private static Embed Render(WorldBoard board) => new EmbedBuilder().WithTitle(board.Title).WithDescription(board.Description).WithColor(new Color(board.Color)).Build();
    private static async Task UpsertBoardAsync(SocketGuild guild, WorldGuildState state, string key, WorldBoard[] boards, ulong botUserId, ulong pingMessageId)
    {
        if (boards.Length == 0) return; var saved = state.Channels[key]; var channel = guild.GetTextChannel(saved.ChannelId) ?? throw new InvalidOperationException("Saved feed channel missing.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', boards.SelectMany(b => new[] { b.Title, b.Description, b.Color.ToString() })))));
        var existing = saved.MessageId == 0 ? null : await channel.GetMessageAsync(saved.MessageId) as IUserMessage;
        if (hash != saved.RenderHash || existing is null)
        {
            var embeds = boards.Take(10).Select(Render).ToArray();
            if (existing is null) { var sent = await channel.SendMessageAsync(embeds: embeds, allowedMentions: AllowedMentions.None); saved.MessageId = sent.Id; }
            else await existing.ModifyAsync(p => { p.Embeds = embeds; p.AllowedMentions = AllowedMentions.None; });
            saved.RenderHash = hash;
        }
        await DiscordCleanup.BotMessagesAsync(channel, botUserId, saved.MessageId, pingMessageId);
    }
    private async Task SendNewPingsAsync(SocketGuild guild, WorldGuildState state, IReadOnlyDictionary<string, string[]> next)
    {
        if (state.Signatures is null) { state.Signatures = next.ToDictionary(p => p.Key, p => p.Value); return; }
        var triggered = new Dictionary<string, List<SocketRole>>(StringComparer.Ordinal);
        foreach (var (key, ids) in next)
        {
            if (!state.Signatures.TryGetValue(key, out var previous)) { state.Signatures[key] = ids; continue; }
            if (!ids.Except(previous).Any()) { state.Signatures[key] = ids; continue; }
            var role = guild.GetRole(state.Roles.GetValueOrDefault(key)); if (role is null || !Roles.TryGetValue(key, out var definition)) continue;
            if (!triggered.TryGetValue(definition.Channel, out var list)) triggered[definition.Channel] = list = [];
            list.Add(role); state.Signatures[key] = ids;
        }
        foreach (var (channelKey, roles) in triggered)
        {
            var channel = guild.GetTextChannel(state.Channels[channelKey].ChannelId) ?? throw new InvalidOperationException("Ping channel missing.");
            if (state.PingMessages.TryGetValue(channelKey, out var previousId) && await channel.GetMessageAsync(previousId) is IUserMessage previous)
                try { await previous.DeleteAsync(); } catch (Discord.Net.HttpException) { }
            var message = await channel.SendMessageAsync(string.Join(' ', roles.DistinctBy(r => r.Id).Select(r => r.Mention)) + "\nNew matching live activity is available.", allowedMentions: new AllowedMentions(AllowedMentionTypes.Roles));
            state.PingMessages[channelKey] = message.Id;
        }
    }
    public static ApplicationCommandProperties BuildCommand()
    {
        var group = new SlashCommandBuilder().WithName("rf-world").WithDescription("C# preview: live Warframe boards and roles");
        foreach (var name in new[] { "setup", "refresh", "start", "stop", "status", "help" }) group.AddOption(new SlashCommandOptionBuilder().WithName(name).WithDescription($"World feeds: {name}").WithType(ApplicationCommandOptionType.SubCommand));
        return group.Build();
    }
    public async Task<string> CommandAsync(SocketSlashCommand command, CancellationToken ct, CancellationToken lifetime)
    {
        var name = command.Data.Options.Single().Name;
        if (name == "help") return "The C# world preview creates WARFRAME LIVE with bot-guide, role-pings, world-cycles, warframe-news and the remaining channels without the old world- prefix. It refreshes each minute and only pings new signatures. Managed channels keep the current bot board/ping and remove older bot-authored messages without deleting user posts. Manage Server is required for setup/start/stop/manual refresh.";
        if (name == "status") return $"World feeds: {Status}; last success {(LastSuccess is { } last ? $"<t:{last.ToUnixTimeSeconds()}:R>" : "never")}; schedule entries {schedule.Length}.";
        if (command.User is not SocketGuildUser user || !user.GuildPermissions.ManageGuild) return "Manage Server permission is required.";
        var guild = bot.GetGuild(targetGuild) ?? throw new InvalidOperationException("Test guild unavailable.");
        if (name == "setup") return await SetupAsync(guild, ct);
        if (name == "refresh") { await RefreshAsync(ct); return "World boards refreshed. " + Status; }
        if (name == "stop") { await StopAsync(); return "World refresh stopped; boards and deduplication state retained."; }
        Start(lifetime); return "World refresh worker started; it updates once per minute.";
    }
    public async ValueTask DisposeAsync()
    {
        bot.ButtonExecuted -= HandleButtonAsync; await StopAsync(); client.Dispose(); cancellation?.Dispose(); refresh.Dispose(); setup.Dispose();
    }
}
