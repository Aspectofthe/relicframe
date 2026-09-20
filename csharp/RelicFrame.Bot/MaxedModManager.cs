using System.Globalization;
using Discord;
using Discord.WebSocket;
using RelicFrame.Core;

internal sealed record MaxedModBoardState
{
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
    public int Page { get; set; }
    public string Sort { get; set; } = "profit";
}
internal sealed record MaxedModActivity(DateTimeOffset CheckedAt, HistoricalSaleSummary Sales);

internal sealed class MaxedModManager : IAsyncDisposable
{
    private readonly DiscordSocketClient bot;
    private readonly LiveMarket market;
    private readonly MarketHttp http;
    private readonly ulong guildId;
    private readonly string statePath;
    private readonly string activityPath;
    private readonly SemaphoreSlim renderGate = new(1, 1);
    private readonly MaxedModBoardState state;
    private readonly Dictionary<string, MaxedModActivity> activity;
    private ModProfitRow[] rows = [];
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private string status = "waiting for market";
    public string Status => Volatile.Read(ref status);

    public MaxedModManager(DiscordSocketClient bot, LiveMarket market, MarketHttp http, ulong guildId, string runtime)
    {
        this.bot = bot; this.market = market; this.http = http; this.guildId = guildId;
        statePath = Path.Combine(runtime, "maxed_mod_board.json");
        activityPath = Path.Combine(runtime, "maxed_mod_activity.json");
        try { state = File.Exists(statePath) ? Json.Read<MaxedModBoardState>(statePath) : new(); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { state = new(); }
        try { activity = File.Exists(activityPath) ? Json.Read<Dictionary<string, MaxedModActivity>>(activityPath) : []; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { activity = []; }
        bot.ButtonExecuted += OnButtonAsync;
    }

    public async Task<string> SetupAsync(SocketGuild guild, CancellationToken lifetime)
    {
        if (guild.Id != guildId) return "Mod board is restricted to the configured server.";
        if (bot.CurrentUser is null || guild.GetUser(bot.CurrentUser.Id)?.GuildPermissions.ManageChannels != true)
            return "Manage Channels permission is needed for the mod profit board.";
        ICategoryChannel category = (ICategoryChannel?)guild.CategoryChannels.FirstOrDefault(c => c.Name == "MOD ECONOMY")
            ?? await guild.CreateCategoryChannelAsync("MOD ECONOMY");
        ITextChannel channel = (ITextChannel?)guild.GetTextChannel(state.ChannelId)
            ?? (ITextChannel?)guild.TextChannels.FirstOrDefault(c => c.CategoryId == category.Id && c.Name == "maxed-mod-profit")
            ?? await guild.CreateTextChannelAsync("maxed-mod-profit", p => { p.CategoryId = category.Id; p.Topic = "R0 to R10 mod resale gains, upgrade costs and reported max-rank sales"; });
        state.ChannelId = channel.Id;
        Json.WriteAtomic(statePath, state);
        await RenderAsync();
        if (worker is not { IsCompleted: false })
        {
            cancellation?.Dispose(); cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            worker = Task.Run(() => LoopAsync(cancellation.Token));
        }
        return "Configured MOD ECONOMY / #maxed-mod-profit.";
    }

    private static double? Cost(string variable) => double.TryParse(Environment.GetEnvironmentVariable(variable),
        NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0 ? value : null;

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (market.IsBootstrapping || market.ReadyBooks == 0)
                    { status = "waiting for market"; await Task.Delay(TimeSpan.FromSeconds(30), ct); continue; }
                    var mods = market.RankTenMods;
                    var next = new List<ModProfitRow>();
                    var endoCost = Cost("RELICFRAME_ENDO_COST_PLAT_PER_1000");
                    var creditCost = Cost("RELICFRAME_CREDIT_COST_PLAT_PER_100K");
                    for (var index = 0; index < mods.Count; index++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var mod = mods[index];
                        status = $"pricing R10 mods {index + 1}/{mods.Count}";
                        var upgrade = MaxedModProfit.UpgradeCost(mod.Tags, 10);
                        if (upgrade is null) continue;
                        try
                        {
                            if (!activity.TryGetValue(mod.Slug, out var cached) || DateTimeOffset.UtcNow - cached.CheckedAt > TimeSpan.FromHours(6))
                            {
                                using var stats = await http.GetJsonAsync("https://api.warframe.market/v1/items/" + Uri.EscapeDataString(mod.Slug) + "/statistics", ct, lowPriority: true);
                                var daily = stats.RootElement.Get("payload").Get("statistics_closed").Get("90days");
                                if (daily.ValueKind != System.Text.Json.JsonValueKind.Array) throw new InvalidDataException("Missing daily statistics.");
                                var now = DateTimeOffset.UtcNow;
                                cached = new(now, PrimeVendors.SalesAtRank(daily, now.AddDays(-30), now, 10));
                                activity[mod.Slug] = cached;
                            }
                            if (cached.Sales.ReportingDays < 3 || cached.Sales.SalesPerDay < 1) continue;
                            await market.EnsureBookAsync(mod.Name, ct);
                            if (!market.IsBookFresh(mod.Name, TimeSpan.FromMinutes(5))) continue;
                            var result = MaxedModProfit.Evaluate(mod.Name, market.MatchRank(mod.Name, 0, true).Best?.Price,
                                market.MatchRank(mod.Name, 10, true).Best?.Price, cached.Sales, upgrade, DateTimeOffset.UtcNow, endoCost, creditCost);
                            if (result is not null) next.Add(result);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception e) when (e is not OutOfMemoryException)
                        { Console.WriteLine($"[maxed-mod] {mod.Slug}: {e.GetType().Name}; excluded from this refresh"); }
                        if ((index + 1) % 20 == 0) { rows = next.ToArray(); await RenderAsync(); }
                    }
                    rows = next.ToArray();
                    Json.WriteAtomic(activityPath, activity);
                    status = $"ready; {rows.Length} liquid, priced R10 mods / {mods.Count} catalog mods";
                    await RenderAsync();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    status = "refresh failed; retry scheduled"; Console.WriteLine($"[maxed-mod] {e.GetType().Name}");
                    try { await RenderAsync(); }
                    catch (Exception renderError) when (renderError is not OutOfMemoryException)
                    { Console.WriteLine($"[maxed-mod-render] {renderError.GetType().Name}"); }
                }
                await Task.Delay(TimeSpan.FromMinutes(15), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task RenderAsync()
    {
        await renderGate.WaitAsync();
        try
        {
            var channel = bot.GetGuild(guildId)?.GetTextChannel(state.ChannelId);
            if (channel is null) return;
            var eligible = MaxedModProfit.Sort(rows.Where(row => DateTimeOffset.UtcNow - row.CheckedAt <= TimeSpan.FromMinutes(25)), state.Sort);
            var pages = Math.Max(1, (eligible.Count + 7) / 8);
            state.Page = Math.Clamp(state.Page, 0, pages - 1);
            var lines = eligible.Skip(state.Page * 8).Take(8).Select((row, i) =>
                $"**{state.Page * 8 + i + 1}. {row.Name}** · R0 {row.BuyR0:0.#}p → R10 {row.SellMax:0.#}p · {row.SalesPerDay:0.#} R10 sales/day\n" +
                $"Upgrade: {row.Cost.Endo:N0} Endo + {row.Cost.Credits:N0} Credits · gross gain {row.Uplift:+0.#;-0.#;0}p" +
                (row.NetProfit is { } net ? $" · net {net:+0.#;-0.#;0}p" : "") +
                $" · {(row.NetProfit.HasValue ? "net" : "gross")} {row.ReturnPer1000Endo:0.##}p/1k Endo · checked <t:{row.CheckedAt.ToUnixTimeSeconds()}:R>");
            var mode = state.Sort switch { "endo" => "return per 1,000 Endo", "sales" => "R10 sales/day", _ => "gain × min(1, R10 sales/day ÷ 5)" };
            var text = $"{Status}\n**Sort:** {mode} · page {state.Page + 1}/{pages}\n\n" +
                (eligible.Count == 0 ? "Waiting for fresh rank-specific prices and sufficient R10 activity." : string.Join("\n\n", lines)) +
                "\n\nR0 cost uses the current online ask; R10 resale is capped at the lower of the online ask and 30-day R10 median. Requires ≥1 reported sale/day and ≥3 reporting days. Gross gain excludes upgrade resources. Net appears when Endo and Credit costs are configured; trade tax is excluded. Reported activity and asks do not guarantee a sale.";
            var controls = new ComponentBuilder()
                .WithButton("Best return", "mod-profit:profit", state.Sort == "profit" ? ButtonStyle.Primary : ButtonStyle.Secondary)
                .WithButton("Per 1k Endo", "mod-profit:endo", state.Sort == "endo" ? ButtonStyle.Primary : ButtonStyle.Secondary)
                .WithButton("Sales/day", "mod-profit:sales", state.Sort == "sales" ? ButtonStyle.Primary : ButtonStyle.Secondary)
                .WithButton("Prev", "mod-profit:prev", disabled: state.Page == 0)
                .WithButton("Next", "mod-profit:next", disabled: state.Page >= pages - 1).Build();
            var embed = new EmbedBuilder().WithTitle("Maxed-mod profit · R0 → R10").WithColor(new Color(0x49B98Fu)).WithDescription(text).Build();
            if (state.MessageId != 0 && await channel.GetMessageAsync(state.MessageId) is IUserMessage old)
                await old.ModifyAsync(p => { p.Embed = embed; p.Components = controls; p.AllowedMentions = AllowedMentions.None; });
            else state.MessageId = (await channel.SendMessageAsync(embed: embed, components: controls, allowedMentions: AllowedMentions.None)).Id;
            Json.WriteAtomic(statePath, state);
        }
        finally { renderGate.Release(); }
    }

    private async Task OnButtonAsync(SocketMessageComponent interaction)
    {
        if (interaction.GuildId != guildId || interaction.ChannelId != state.ChannelId || !interaction.Data.CustomId.StartsWith("mod-profit:", StringComparison.Ordinal)) return;
        await interaction.DeferAsync(ephemeral: true);
        _ = Task.Run(async () =>
        {
            try
            {
                var mode = interaction.Data.CustomId["mod-profit:".Length..];
                await renderGate.WaitAsync();
                try
                {
                    if (mode == "prev") state.Page--; else if (mode == "next") state.Page++;
                    else if (mode is "profit" or "endo" or "sales") { state.Sort = mode; state.Page = 0; }
                }
                finally { renderGate.Release(); }
                await RenderAsync();
                await interaction.FollowupAsync("Mod ranking updated.", ephemeral: true);
            }
            catch (Exception e) { Console.WriteLine($"[mod-profit-interaction] {e.GetType().Name}"); }
        });
    }

    public async Task StopAsync() { if (cancellation is not null) await cancellation.CancelAsync(); if (worker is not null) await worker; }
    public async ValueTask DisposeAsync()
    { bot.ButtonExecuted -= OnButtonAsync; await StopAsync(); cancellation?.Dispose(); renderGate.Dispose(); }
}
