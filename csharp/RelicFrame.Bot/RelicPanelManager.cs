using Discord;
using Discord.WebSocket;
using RelicFrame.Core;

internal sealed record RelicPanelGuildState
{
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
    public RelicPanelOptions Options { get; set; } = new();
}
internal sealed record RelicPanelStore
{
    public Dictionary<string, RelicPanelGuildState> Guilds { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class RelicPanelManager : IAsyncDisposable
{
    private readonly DiscordSocketClient bot;
    private readonly LiveMarket market;
    private readonly IReadOnlyDictionary<string, Relic> relics;
    private readonly ulong targetGuild;
    private readonly string path;
    private readonly SemaphoreSlim update = new(1, 1);
    private RelicPanelStore store;
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private string status = "stopped";
    public string Status => Volatile.Read(ref status);
    public RelicPanelManager(DiscordSocketClient bot, LiveMarket market, IReadOnlyDictionary<string, Relic> relics, ulong targetGuild, string runtime)
    {
        this.bot = bot; this.market = market; this.relics = relics; this.targetGuild = targetGuild; path = Path.Combine(runtime, "relic_panels.json");
        try { store = File.Exists(path) ? Json.Read<RelicPanelStore>(path) : new(); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { store = new(); status = "saved panel state unreadable; setup required"; }
    }
    private RelicPanelGuildState State(ulong id) => store.Guilds.GetValueOrDefault(id.ToString()) ?? (store.Guilds[id.ToString()] = new());
    private void Save() => Json.WriteAtomic(path, store);
    public async Task<string> SetupAsync(SocketGuild guild, ITextChannel? selected, CancellationToken ct, CancellationToken lifetime)
    {
        if (guild.Id != targetGuild) return "C# preview setup is restricted to its configured test guild.";
        if (!guild.CurrentUser.GuildPermissions.ManageChannels) return "I need Manage Channels to create or repair the relic panel.";
        var state = State(guild.Id); ITextChannel? channel = selected ?? guild.GetTextChannel(state.ChannelId);
        if (channel is null)
        {
            var category = guild.CategoryChannels.FirstOrDefault(c => c.Name == WorldManager.CategoryName);
            channel = guild.TextChannels.FirstOrDefault(c => c.Name == "relic-deals");
            channel ??= await guild.CreateTextChannelAsync("relic-deals", p => { p.CategoryId = category?.Id; p.Topic = "Live Radiant relic value and risk rankings"; });
        }
        state.ChannelId = channel.Id; state.Options ??= new(); Save();
        await market.StartAsync(false, lifetime); Start(lifetime); await UpdateAsync(ct);
        return $"Relic panel configured in <#{channel.Id}>. Default: Radiant, Best Overall, best of online/all sellers; panel render every minute.";
    }
    public void Start(CancellationToken lifetime)
    {
        if (worker is { IsCompleted: false }) return; cancellation?.Dispose(); cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        worker = Task.Run(() => LoopAsync(cancellation.Token)); status = "starting";
    }
    public async Task StopAsync()
    {
        if (cancellation is not null) await cancellation.CancelAsync(); if (worker is not null) await worker; status = "stopped; panel and filters retained";
    }
    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var began = DateTimeOffset.UtcNow;
                try { await UpdateAsync(ct); }
                catch (Exception e) when (!ct.IsCancellationRequested && e is Discord.Net.HttpException or InvalidOperationException or IOException)
                { status = $"update failed ({e.GetType().Name}); previous panel retained"; }
                var wait = TimeSpan.FromMinutes(1) - (DateTimeOffset.UtcNow - began); await Task.Delay(wait > TimeSpan.FromSeconds(5) ? wait : TimeSpan.FromSeconds(5), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public async Task UpdateAsync(CancellationToken ct)
    {
        await update.WaitAsync(ct);
        try
        {
            var guild = bot.GetGuild(targetGuild) ?? throw new InvalidOperationException("Test guild unavailable."); var state = State(targetGuild);
            var channel = guild.GetTextChannel(state.ChannelId) ?? throw new InvalidOperationException("Relic panel channel missing; run /rf-panel setup.");
            var tier = Enum.TryParse<Refinement>(state.Options.Refinement, true, out var parsed) ? parsed : Refinement.Radiant;
            var snapshot = market.Snapshot(tier);
            string description;
            if (snapshot.ReadyBooks == 0) description = $"Market bootstrap in progress: {market.Status}; {snapshot.ReadyBooks}/{snapshot.TotalBooks} books.\nFilters: {Summary(state.Options)}";
            else
            {
                var rows = RelicPanel.Select(relics, snapshot, state.Options);
                description = string.Join('\n', rows.Select((r, i) => $"**{i + 1}. {r.Relic.RelicName}** · {r.OverallCategory} · online {Price(r.Online.Cost)}p · EV {r.Online.Profit.ExpectedValue:0.##}p · risk {r.BestRisk}/100").DefaultIfEmpty("No relics match the current filters."));
                description += $"\n\nFilters: {Summary(state.Options)}\nMarket: {market.Status}; {snapshot.ReadyBooks}/{snapshot.TotalBooks} books; oldest successful book {(snapshot.FetchedAt == default ? "unknown" : $"<t:{snapshot.FetchedAt.ToUnixTimeSeconds()}:R>")}. Asks can change; profit is not guaranteed.";
            }
            var embed = new EmbedBuilder().WithTitle($"Relic rankings — {state.Options.Refinement}").WithDescription(description.Length <= 4096 ? description : description[..4093] + "…").WithColor(new Color(0x8E44ADu)).Build();
            var old = state.MessageId == 0 ? null : await channel.GetMessageAsync(state.MessageId) as IUserMessage;
            if (old is null) { var sent = await channel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None); state.MessageId = sent.Id; }
            else await old.ModifyAsync(p => { p.Embed = embed; p.AllowedMentions = AllowedMentions.None; });
            status = $"ready; one-minute render; {state.Options.Refinement}/{state.Options.Sort}"; Save();
        }
        finally { update.Release(); }
    }
    private static string Price(double? value) => value?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
    private static string Summary(RelicPanelOptions o) => $"{o.Refinement}; {o.Sort}; {o.Scope}; vault {o.Vault}; min ROI {Price(o.MinRoi)}; max cost {Price(o.MaxCost)}; min reward {Price(o.MinReward)}; guaranteed only {o.GuaranteedOnly}";
    public static ApplicationCommandProperties BuildCommand()
    {
        var group = new SlashCommandBuilder().WithName("rf-panel").WithDescription("C# preview: persistent filtered relic rankings");
        var setup = new SlashCommandOptionBuilder().WithName("setup").WithDescription("Create or move the default Radiant panel").WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption("channel", ApplicationCommandOptionType.Channel, "Existing text channel; omit to create relic-deals"); group.AddOption(setup);
        var config = new SlashCommandOptionBuilder().WithName("config").WithDescription("Change filters, then immediately re-render").WithType(ApplicationCommandOptionType.SubCommand);
        config.AddOption(new SlashCommandOptionBuilder().WithName("refinement").WithDescription("Relic refinement").WithType(ApplicationCommandOptionType.String).AddChoice("Intact","Intact").AddChoice("Exceptional","Exceptional").AddChoice("Flawless","Flawless").AddChoice("Radiant","Radiant"));
        var sort = new SlashCommandOptionBuilder().WithName("sort").WithDescription("Ranking mode").WithType(ApplicationCommandOptionType.String); foreach (var mode in new[] { "Best Overall", "Guaranteed Profit", "Expected Profit", "Best ROI", "Cheapest", "Best Plat/Trace", "Best Ducat Farming" }) sort.AddChoice(mode, mode); config.AddOption(sort);
        config.AddOption(new SlashCommandOptionBuilder().WithName("scope").WithDescription("Seller scope").WithType(ApplicationCommandOptionType.String).AddChoice("Online only","Online only").AddChoice("All sellers","Offline only").AddChoice("Best of either","Both (best of either)"));
        config.AddOption(new SlashCommandOptionBuilder().WithName("vault").WithDescription("Vault status").WithType(ApplicationCommandOptionType.String).AddChoice("All","All").AddChoice("Vaulted","Vaulted").AddChoice("Unvaulted","Unvaulted").AddChoice("Unknown","Unknown"));
        config.AddOption("min_roi", ApplicationCommandOptionType.Number, "Minimum ROI; use -1 to clear", minValue:-1); config.AddOption("max_cost", ApplicationCommandOptionType.Number, "Maximum cost; use -1 to clear", minValue:-1);
        config.AddOption("min_reward", ApplicationCommandOptionType.Number, "Minimum reward; use -1 to clear", minValue:-1); config.AddOption("guaranteed_only", ApplicationCommandOptionType.Boolean, "Require profitable every outcome"); group.AddOption(config);
        foreach (var name in new[] { "refresh", "start", "stop", "status", "help" }) group.AddOption(new SlashCommandOptionBuilder().WithName(name).WithDescription($"Relic panel: {name}").WithType(ApplicationCommandOptionType.SubCommand));
        return group.Build();
    }
    public async Task<string> CommandAsync(SocketSlashCommand command, CancellationToken ct, CancellationToken lifetime)
    {
        var sub = command.Data.Options.Single(); var name = sub.Name; string Get(string key) => sub.Options.FirstOrDefault(o => o.Name == key)?.Value?.ToString() ?? "";
        if (name == "help") return "The persistent relic panel defaults to Radiant/Best Overall and re-renders once per minute. Use config to change filters; -1 clears a numeric filter. /rf-panel stop is the panel kill switch. /rf-relics stop separately stops market fetching; start it again before expecting fresh prices.";
        if (name == "status") return $"Relic panel: {Status}; market: {market.Status}.";
        if (command.User is not SocketGuildUser user || !user.GuildPermissions.ManageGuild) return "Manage Server permission is required.";
        var guild = bot.GetGuild(targetGuild) ?? throw new InvalidOperationException("Test guild unavailable.");
        if (name == "setup") return await SetupAsync(guild, sub.Options.FirstOrDefault(o => o.Name == "channel")?.Value as ITextChannel, ct, lifetime);
        if (name == "stop") { await StopAsync(); return "One-minute panel rendering stopped; message and filters retained. Use /rf-relics stop too if you want to stop API fetching."; }
        if (name == "start") { await market.StartAsync(false, lifetime); Start(lifetime); return "Market and one-minute panel workers started."; }
        if (name == "config")
        {
            var current = State(targetGuild).Options; double? Number(string key, double? old) => double.TryParse(Get(key), out var n) ? n < 0 ? null : n : old;
            State(targetGuild).Options = current with { Refinement = Get("refinement") is { Length: > 0 } refinement ? refinement : current.Refinement,
                Sort = Get("sort") is { Length: > 0 } sort ? sort : current.Sort, Scope = Get("scope") is { Length: > 0 } scope ? scope : current.Scope,
                Vault = Get("vault") is { Length: > 0 } vault ? vault : current.Vault, MinRoi = Number("min_roi", current.MinRoi), MaxCost = Number("max_cost", current.MaxCost),
                MinReward = Number("min_reward", current.MinReward), GuaranteedOnly = bool.TryParse(Get("guaranteed_only"), out var guaranteed) ? guaranteed : current.GuaranteedOnly };
            Save(); await UpdateAsync(ct); return "Panel filters saved and rendered: " + Summary(State(targetGuild).Options);
        }
        await UpdateAsync(ct); return "Relic panel refreshed from the current price snapshot.";
    }
    public async ValueTask DisposeAsync() { await StopAsync(); cancellation?.Dispose(); update.Dispose(); }
}
