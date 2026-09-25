using System.Text.Json;
using Discord;
using Discord.WebSocket;
using RelicFrame.Core;

internal sealed record SyndicateBoardState
{
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
    public string Syndicate { get; set; } = "All";
    public string Sort { get; set; } = "profit";
    public int Page { get; set; }
    public int Standing { get; set; } = 25000;
}
internal sealed record SyndicateVendorCache(DateTimeOffset FetchedAt, JsonElement Data);
internal sealed record SyndicateActivity(DateTimeOffset FetchedAt, HistoricalSaleSummary Sales);

internal sealed class SyndicateManager : IAsyncDisposable
{
    private const string SourceUrl = "https://raw.githubusercontent.com/WFCD/warframe-drop-data/gh-pages/data/syndicates.json";
    private readonly DiscordSocketClient bot;
    private readonly LiveMarket market;
    private readonly MarketHttp http;
    private readonly ulong guildId;
    private readonly string statePath, vendorPath, activityPath;
    private readonly HttpClient source = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim renderGate = new(1, 1);
    private readonly SyndicateBoardState state;
    private readonly Dictionary<string, SyndicateActivity> activity;
    private SyndicateVendorCache? vendors;
    private SyndicateProfitRow[] rows = [];
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private string status = "waiting for market";
    public string Status => Volatile.Read(ref status);

    public SyndicateManager(DiscordSocketClient bot, LiveMarket market, MarketHttp http, ulong guildId, string runtime)
    {
        this.bot = bot; this.market = market; this.http = http; this.guildId = guildId;
        statePath = Path.Combine(runtime, "syndicate_board.json"); vendorPath = Path.Combine(runtime, "syndicate_offers.json");
        activityPath = Path.Combine(runtime, "syndicate_activity.json");
        try { state = File.Exists(statePath) ? Json.Read<SyndicateBoardState>(statePath) : new(); }
        catch (Exception e) when (e is IOException or JsonException) { state = new(); }
        try { activity = File.Exists(activityPath) ? Json.Read<Dictionary<string, SyndicateActivity>>(activityPath) : []; }
        catch (Exception e) when (e is IOException or JsonException) { activity = []; }
        try { vendors = File.Exists(vendorPath) ? Json.Read<SyndicateVendorCache>(vendorPath) : null; }
        catch (Exception e) when (e is IOException or JsonException) { vendors = null; }
        source.DefaultRequestHeaders.UserAgent.ParseAdd("RelicFrame-CSharp/0.1");
        bot.ButtonExecuted += OnButtonAsync; bot.SelectMenuExecuted += OnSelectAsync; bot.ModalSubmitted += OnModalAsync;
    }

    public async Task<string> SetupAsync(SocketGuild guild, CancellationToken lifetime)
    {
        if (guild.Id != guildId) return "Syndicate board is restricted to the configured server.";
        if (bot.CurrentUser is null || guild.GetUser(bot.CurrentUser.Id)?.GuildPermissions.ManageChannels != true)
            return "Manage Channels permission is needed for the Syndicate board.";
        ICategoryChannel category = (ICategoryChannel?)guild.CategoryChannels.FirstOrDefault(c => c.Name == "SYNDICATE ECONOMY")
            ?? await guild.CreateCategoryChannelAsync("SYNDICATE ECONOMY");
        ITextChannel channel = (ITextChannel?)guild.GetTextChannel(state.ChannelId)
            ?? (ITextChannel?)guild.TextChannels.FirstOrDefault(c => c.CategoryId == category.Id && c.Name == "standing-profit")
            ?? await guild.CreateTextChannelAsync("standing-profit", p => { p.CategoryId = category.Id; p.Topic = "Syndicate standing to platinum: augments, weapons and components"; });
        state.ChannelId = channel.Id; Json.WriteAtomic(statePath, state);
        await RenderAsync();
        if (worker is not { IsCompleted: false })
        {
            cancellation?.Dispose(); cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            worker = Task.Run(() => LoopAsync(cancellation.Token));
        }
        return "Configured SYNDICATE ECONOMY / #standing-profit.";
    }

    private MarketCatalogItem? Resolve(string name) => market.ItemIdForName(name) is { } id ? market.CatalogItem(id) : null;
    private async Task<IReadOnlyList<SyndicateOffer>> OffersAsync(CancellationToken ct)
    {
        if (vendors is null || DateTimeOffset.UtcNow - vendors.FetchedAt > TimeSpan.FromDays(1))
        {
            try
            {
                using var response = await source.GetAsync(SourceUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024, ct);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (SyndicateProfit.Parse(document.RootElement, Resolve).Count == 0) throw new InvalidDataException("No mapped standing offers.");
                vendors = new(DateTimeOffset.UtcNow, document.RootElement.Clone()); Json.WriteAtomic(vendorPath, vendors);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is not OutOfMemoryException && vendors is not null && DateTimeOffset.UtcNow - vendors.FetchedAt <= TimeSpan.FromDays(7))
            { Console.WriteLine($"[syndicate-source] {e.GetType().Name}; using dated vendor cache"); }
        }
        if (vendors is null || DateTimeOffset.UtcNow - vendors.FetchedAt > TimeSpan.FromDays(7)) throw new InvalidDataException("Vendor catalog is unavailable or stale.");
        return SyndicateProfit.Parse(vendors.Data, Resolve);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (http.ChallengePausedUntil.HasValue)
                    { status = "Warframe.market Cloudflare challenge; previous standing board retained"; await Task.Delay(TimeSpan.FromSeconds(30), ct); continue; }
                    if (market.IsBootstrapping || market.ReadyBooks == 0)
                    { await Task.Delay(TimeSpan.FromSeconds(30), ct); continue; }
                    var offers = await OffersAsync(ct);
                    var groups = offers.GroupBy(offer => offer.Item.Id).ToArray();
                    var next = new List<SyndicateProfitRow>();
                    for (var index = 0; index < groups.Length; index++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var group = groups[index]; var item = group.First().Item;
                        status = $"pricing offerings {index + 1}/{groups.Length}";
                        try
                        {
                            if (!activity.TryGetValue(item.Id, out var cached) || DateTimeOffset.UtcNow - cached.FetchedAt > TimeSpan.FromHours(6))
                            {
                                using var stats = await http.GetJsonAsync("https://api.warframe.market/v1/items/" + Uri.EscapeDataString(item.Slug) + "/statistics", ct, lowPriority: true);
                                var daily = stats.RootElement.Get("payload").Get("statistics_closed").Get("90days");
                                if (daily.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Missing daily statistics.");
                                cached = new(DateTimeOffset.UtcNow, PrimeVendors.Sales(daily, DateTimeOffset.UtcNow, item.MaxRank is > 0));
                                activity[item.Id] = cached;
                            }
                            if (cached.Sales.ReportingDays < 3 || cached.Sales.SalesPerDay < 1) continue;
                            await market.EnsureBookAsync(item.Name, ct);
                            if (!market.IsBookFresh(item.Name, TimeSpan.FromMinutes(5))) continue;
                            var ask = (item.MaxRank is > 0 ? market.MatchRank(item.Name, 0, true) : market.Match(item.Name, null, true)).Best?.Price;
                            foreach (var offer in group)
                                if (SyndicateProfit.Evaluate(offer, ask, cached.Sales, DateTimeOffset.UtcNow) is { } row) next.Add(row);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (MarketChallengeException) { throw; }
                        catch (Exception e) when (e is not OutOfMemoryException)
                        { Console.WriteLine($"[syndicate-price] {item.Slug}: {e.GetType().Name}; excluded from refresh"); }
                        if ((index + 1) % 25 == 0) { rows = next.ToArray(); await RenderAsync(); }
                    }
                    rows = next.ToArray(); Json.WriteAtomic(activityPath, activity);
                    status = $"ready; {rows.Length}/{offers.Count} offerings have fresh prices and sufficient activity";
                    await RenderAsync();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (MarketChallengeException)
                { status = "Warframe.market Cloudflare challenge; previous standing board retained"; }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    status = "refresh failed; retry scheduled"; rows = [];
                    Console.WriteLine($"[syndicate] {e.GetType().Name}");
                    try { await RenderAsync(); } catch (Exception renderError) when (renderError is not OutOfMemoryException)
                    { Console.WriteLine($"[syndicate-render] {renderError.GetType().Name}"); }
                }
                await Task.Delay(http.ChallengePausedUntil.HasValue ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(15), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task RenderAsync()
    {
        await renderGate.WaitAsync();
        try
        {
            var channel = bot.GetGuild(guildId)?.GetTextChannel(state.ChannelId); if (channel is null) return;
            var eligible = SyndicateProfit.Sort(rows.Where(row => DateTimeOffset.UtcNow - row.CheckedAt <= TimeSpan.FromMinutes(25) &&
                (state.Syndicate == "All" || row.Offer.Syndicate == state.Syndicate)), state.Sort);
            var pages = Math.Max(1, (eligible.Count + 7) / 8); state.Page = Math.Clamp(state.Page, 0, pages - 1);
            var lines = eligible.Skip(state.Page * 8).Take(8).Select((row, i) =>
            {
                var converted = SyndicateProfit.Convert(row, state.Standing);
                return $"**{state.Page * 8 + i + 1}. {row.Offer.Item.Name}** · {row.Price:0.#}p · {row.SalesPerDay:0.#} sales/day\n" +
                    $"{row.Offer.Syndicate} · {row.Offer.RequiredRank} · {row.Offer.Standing:N0} standing · {row.Per25000Standing:0.#}p/25k\n" +
                    $"Budget buys {converted.Quantity} → {converted.Value:0.#}p gross · {converted.Remaining:N0} standing left";
            });
            var mode = state.Sort switch { "value" => "platinum per 25k standing", "sales" => "reported sales/day", _ => "return per 25k × min(1, sales/day ÷ 5)" };
            var description = $"{Status}\n**{state.Syndicate}** · budget {state.Standing:N0} standing · page {state.Page + 1}/{pages}\nSort: {mode}\n\n" +
                (eligible.Count == 0 ? "No fresh, sufficiently active offers for this filter yet." : string.Join("\n\n", lines)) +
                $"\n\nVendor data: [WFCD/wiki]({SourceUrl}), fetched {(vendors is null ? "unknown" : $"<t:{vendors.FetchedAt.ToUnixTimeSeconds()}:R>")}. " +
                "Rank-zero values use the lower of online asks and 30-day medians; requires ≥1 reported sale/day and ≥3 reporting days. Each budget example is an alternative, not a combined shopping list. Standing/rank are not read from your account. Weapons must be unused and eligible to trade. Gross value excludes trade tax; bulk sales may take time.";
            var menu = new SelectMenuBuilder().WithCustomId("standing:syndicate").WithPlaceholder("Choose your Syndicate")
                .AddOption("All Syndicates", "All", isDefault: state.Syndicate == "All");
            foreach (var name in SyndicateProfit.Names) menu.AddOption(name, name, isDefault: state.Syndicate == name);
            var controls = new ComponentBuilder().WithSelectMenu(menu, row: 0)
                .WithButton("Best return", "standing:profit", state.Sort == "profit" ? ButtonStyle.Primary : ButtonStyle.Secondary, row: 1)
                .WithButton("Per 25k standing", "standing:value", state.Sort == "value" ? ButtonStyle.Primary : ButtonStyle.Secondary, row: 1)
                .WithButton("Sales/day", "standing:sales", state.Sort == "sales" ? ButtonStyle.Primary : ButtonStyle.Secondary, row: 1)
                .WithButton("Prev", "standing:prev", disabled: state.Page == 0, row: 1)
                .WithButton("Next", "standing:next", disabled: state.Page >= pages - 1, row: 1)
                .WithButton("Set standing budget", "standing:budget", row: 2).Build();
            var embed = new EmbedBuilder().WithTitle("Syndicate standing → platinum").WithColor(new Color(0x4DBBC5u)).WithDescription(description).Build();
            if (state.MessageId != 0 && await channel.GetMessageAsync(state.MessageId) is IUserMessage old)
                await old.ModifyAsync(p => { p.Embed = embed; p.Components = controls; p.AllowedMentions = AllowedMentions.None; });
            else state.MessageId = (await channel.SendMessageAsync(embed: embed, components: controls, allowedMentions: AllowedMentions.None)).Id;
            Json.WriteAtomic(statePath, state);
        }
        finally { renderGate.Release(); }
    }

    private bool Own(SocketMessageComponent interaction) => interaction.GuildId == guildId && interaction.ChannelId == state.ChannelId && interaction.Data.CustomId.StartsWith("standing:", StringComparison.Ordinal);
    private async Task OnButtonAsync(SocketMessageComponent interaction)
    {
        if (!Own(interaction)) return;
        if (interaction.Data.CustomId == "standing:budget")
        {
            await interaction.RespondWithModalAsync(new ModalBuilder().WithTitle("Standing budget").WithCustomId("standing:budget-submit")
                .AddTextInput("Available standing (0–132000)", "budget", value: state.Standing.ToString(), maxLength: 6).Build());
            return;
        }
        await interaction.DeferAsync(ephemeral: true);
        _ = UpdateAsync(interaction.Data.CustomId["standing:".Length..], null, null, interaction);
    }
    private async Task OnSelectAsync(SocketMessageComponent interaction)
    {
        if (!Own(interaction) || interaction.Data.CustomId != "standing:syndicate") return;
        var selected = interaction.Data.Values.FirstOrDefault();
        if (selected != "All" && !SyndicateProfit.Names.Contains(selected)) return;
        await interaction.DeferAsync(ephemeral: true);
        _ = UpdateAsync(null, selected, null, interaction);
    }
    private async Task OnModalAsync(SocketModal modal)
    {
        if (modal.GuildId != guildId || modal.ChannelId != state.ChannelId || modal.Data.CustomId != "standing:budget-submit") return;
        if (!int.TryParse(modal.Data.Components.FirstOrDefault(c => c.CustomId == "budget")?.Value, out var budget) || budget is < 0 or > 132000)
        { await modal.RespondAsync("Enter a whole number from 0 to 132000.", ephemeral: true); return; }
        await modal.DeferAsync(ephemeral: true);
        _ = UpdateAsync(null, null, budget, modal);
    }
    private async Task UpdateAsync(string? mode, string? selected, int? budget, SocketInteraction interaction)
    {
        try
        {
            await interaction.ModifyOriginalResponseAsync(response => response.Content = "Updating standing planner…");
            await renderGate.WaitAsync();
            try
            {
                if (mode == "prev") state.Page--; else if (mode == "next") state.Page++;
                else if (mode is "profit" or "value" or "sales") { state.Sort = mode; state.Page = 0; }
                if (selected is not null) { state.Syndicate = selected; state.Page = 0; }
                if (budget.HasValue) { state.Standing = budget.Value; state.Page = 0; }
            }
            finally { renderGate.Release(); }
            await RenderAsync(); await interaction.ModifyOriginalResponseAsync(response => response.Content = "Standing planner updated.");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[standing-interaction] {e}");
            try { await interaction.ModifyOriginalResponseAsync(response => response.Content = "Standing planner could not be updated. Try again shortly."); } catch { }
        }
    }
    public async Task StopAsync() { if (cancellation is not null) await cancellation.CancelAsync(); if (worker is not null) await worker; }
    public async ValueTask DisposeAsync()
    { bot.ButtonExecuted -= OnButtonAsync; bot.SelectMenuExecuted -= OnSelectAsync; bot.ModalSubmitted -= OnModalAsync; await StopAsync(); cancellation?.Dispose(); source.Dispose(); renderGate.Dispose(); }
}
