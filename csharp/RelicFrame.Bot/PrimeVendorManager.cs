using System.Net;
using System.Security.Cryptography;
using System.Text;
using Discord;
using Discord.WebSocket;
using RelicFrame.Core;

internal sealed record PrimeVendorBoardState
{
    public ulong AyaChannelId { get; set; }
    public ulong AyaMessageId { get; set; }
    public string AyaHash { get; set; } = "";
    public ulong BaroChannelId { get; set; }
    public ulong BaroMessageId { get; set; }
    public string BaroHash { get; set; } = "";
    public DateTimeOffset? AyaExpiresAt { get; set; }
    public DateTimeOffset? BaroExpiresAt { get; set; }
    public string AyaSort { get; set; } = AyaProfit.Overall;
    public string BaroSort { get; set; } = BaroEconomy.Sales;
}
internal sealed record BaroSaleCache(DateTimeOffset CheckedAt, HistoricalSaleSummary Sales,
    HistoricalSaleSummary? PostVisit = null, DateTimeOffset? VisitEndedAt = null,
    HistoricalSaleSummary? MaxRankSales = null);
internal sealed record BaroVisitItem(string Slug, int Ducats, int Credits);
internal sealed record BaroVisitRecord(DateTimeOffset EndsAt, BaroVisitItem[] Items);
internal sealed record BaroBoardRow(VendorOffer Offer, HistoricalSaleSummary Sales,
    HistoricalSaleSummary? MaxRankSales, HistoricalSaleSummary? PostVisit,
    double? R0Ask, double? MaxRankAsk, int? MaxRank);

internal sealed class PrimeVendorManager : IAsyncDisposable
{
    private readonly DiscordSocketClient bot;
    private readonly LiveMarket market;
    private readonly MarketHttp marketHttp;
    private readonly IReadOnlyDictionary<string, Relic> relics;
    private readonly ulong guildId;
    private readonly string statePath;
    private readonly string salesPath;
    private readonly string visitsPath;
    private readonly HttpClient official = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15), MaxConnectionsPerServer = 1 });
    private readonly SemaphoreSlim gate = new(1, 1);
    private PrimeVendorBoardState state;
    private Dictionary<string, BaroSaleCache> sales;
    private List<BaroVisitRecord> visits;
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private PrimeVendorSnapshot? latestAyaSnapshot;
    private AyaValueRow[] latestAyaRows = [];
    private PrimeVendorSnapshot? latestBaroSnapshot;
    private BaroBoardRow[] latestBaroRows = [];
    private string status = "stopped";
    public string Status => Volatile.Read(ref status);

    public PrimeVendorManager(DiscordSocketClient bot, LiveMarket market, MarketHttp marketHttp,
        IReadOnlyDictionary<string, Relic> relics, ulong guildId, string runtime)
    {
        this.bot = bot; this.market = market; this.marketHttp = marketHttp; this.relics = relics; this.guildId = guildId;
        statePath = Path.Combine(runtime, "prime_vendor_boards.json");
        salesPath = Path.Combine(runtime, "baro_sales_activity.json");
        visitsPath = Path.Combine(runtime, "baro_visit_history.json");
        try { state = File.Exists(statePath) ? Json.Read<PrimeVendorBoardState>(statePath) : new(); }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException) { state = new(); }
        try { sales = File.Exists(salesPath) ? Json.Read<Dictionary<string, BaroSaleCache>>(salesPath) : new(); }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException) { sales = new(); }
        try { visits = File.Exists(visitsPath) ? Json.Read<List<BaroVisitRecord>>(visitsPath) : []; }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException) { visits = []; }
        official.DefaultRequestHeaders.UserAgent.ParseAdd("RelicFrame-CSharp/0.1");
        bot.ButtonExecuted += DispatchButtonAsync;
    }

    public async Task<string> SetupAsync(SocketGuild guild, CancellationToken ct, CancellationToken lifetime)
    {
        if (guild.Id != guildId) return "Prime vendor boards are restricted to the configured server.";
        var member = bot.CurrentUser is null ? null : guild.GetUser(bot.CurrentUser.Id);
        if (member is null || !member.GuildPermissions.ManageChannels) return "I need Manage Channels for the Prime vendor boards.";
        ICategoryChannel category = (ICategoryChannel?)guild.CategoryChannels.FirstOrDefault(channel => channel.Name == "PRIME ECONOMY")
            ?? await guild.CreateCategoryChannelAsync("PRIME ECONOMY");
        ITextChannel aya = (ITextChannel?)guild.GetTextChannel(state.AyaChannelId) ?? (ITextChannel?)guild.TextChannels.FirstOrDefault(channel => channel.CategoryId == category.Id && channel.Name == "aya-planner")
            ?? await guild.CreateTextChannelAsync("aya-planner", properties => { properties.CategoryId = category.Id; properties.Topic = "Live Varzia relics ranked by estimated platinum return per Aya"; });
        ITextChannel baro = (ITextChannel?)guild.GetTextChannel(state.BaroChannelId) ?? (ITextChannel?)guild.TextChannels.FirstOrDefault(channel => channel.CategoryId == category.Id && channel.Name == "baro-investments")
            ?? await guild.CreateTextChannelAsync("baro-investments", properties => { properties.CategoryId = category.Id; properties.Topic = "Baro stock, Ducat costs and rank-zero recorded market sales"; });
        state.AyaChannelId = aya.Id; state.BaroChannelId = baro.Id; Save();
        await PublishAsync(aya, true, "Prime Resurgence · Aya planner", "Checking Digital Extremes' current Varzia manifest and shared market books.", ct);
        await PublishAsync(baro, false, "Baro · investment watchlist", "Checking Digital Extremes' current Baro manifest and recorded market sales.", ct);
        Start(lifetime);
        return "Configured **PRIME ECONOMY / #aya-planner** and **#baro-investments**.";
    }

    private void Start(CancellationToken lifetime)
    {
        if (worker is { IsCompleted: false }) return;
        cancellation?.Dispose(); cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        worker = Task.Run(() => LoopAsync(cancellation.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await UpdateAsync(ct); }
                catch (Exception error) when (!ct.IsCancellationRequested && error is not OutOfMemoryException)
                {
                    status = $"refresh failed ({error.GetType().Name}); retry scheduled"; Console.WriteLine($"[prime-vendors] {error}");
                    try { await ExpireBoardsAsync(ct); }
                    catch (Exception expiryError) when (!ct.IsCancellationRequested && expiryError is not OutOfMemoryException)
                    { Console.WriteLine($"[prime-vendors] expiry notice failed: {expiryError.GetType().Name}"); }
                }
                await Task.Delay(market.ReadyBooks == 0 || market.IsBootstrapping ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(15), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async Task UpdateAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (market.ReadyBooks == 0 || market.IsBootstrapping) { status = "waiting for shared market books"; return; }
            var guild = bot.GetGuild(guildId) ?? throw new InvalidOperationException("Configured server unavailable.");
            var ayaChannel = guild.GetTextChannel(state.AyaChannelId) ?? throw new InvalidOperationException("Aya channel missing.");
            var baroChannel = guild.GetTextChannel(state.BaroChannelId) ?? throw new InvalidOperationException("Baro channel missing.");
            status = "reading official vendor manifest";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await official.GetAsync("https://api.warframe.com/cdn/worldState.php", HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024, deadline.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var document = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token);
            var snapshot = PrimeVendors.Parse(document.RootElement, market.NameForGameRef, DateTimeOffset.UtcNow);
            state.AyaExpiresAt = snapshot.Aya?.EndsAt; state.BaroExpiresAt = snapshot.Baro?.EndsAt; Save();
            await PublishAsync(ayaChannel, true, "Prime Resurgence · Aya planner", await AyaTextAsync(snapshot, ct), ct);
            await PublishAsync(baroChannel, false, "Baro · investment watchlist", await BaroTextAsync(snapshot, ct), ct);
            status = $"ready; Varzia {snapshot.Aya?.Offers.Count ?? 0} relics; Baro {snapshot.Baro?.Offers.Count ?? 0} mapped stock items";
        }
        finally { gate.Release(); }
    }

    private async Task ExpireBoardsAsync(CancellationToken ct)
    {
        var guild = bot.GetGuild(guildId); if (guild is null) return;
        if (state.AyaExpiresAt is { } ayaEnd && ayaEnd <= DateTimeOffset.UtcNow && guild.GetTextChannel(state.AyaChannelId) is { } aya)
            await PublishAsync(aya, true, "Prime Resurgence · Aya planner",
                "The last verified Varzia rotation has ended. Waiting for a fresh official manifest; prior relics are not shown as current.", ct);
        if (state.BaroExpiresAt is { } baroEnd && baroEnd <= DateTimeOffset.UtcNow && guild.GetTextChannel(state.BaroChannelId) is { } baro)
            await PublishAsync(baro, false, "Baro · investment watchlist",
                "The last verified Baro visit has ended. Waiting for a fresh official manifest; prior stock is not shown as current.", ct);
    }

    private async Task<string> AyaTextAsync(PrimeVendorSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.Aya is not { } rotation)
        {
            latestAyaSnapshot = snapshot; latestAyaRows = [];
            return "Varzia's active rotation is unavailable in the official feed. No old rotation is being presented as current.";
        }
        var rows = new List<AyaValueRow>();
        foreach (var offer in rotation.Offers)
        {
            var key = offer.Name.EndsWith(" Relic", StringComparison.OrdinalIgnoreCase) ? offer.Name[..^6] : offer.Name;
            var relic = relics.GetValueOrDefault(key);
            if (!market.IsBookFresh(offer.Name, TimeSpan.FromMinutes(30))) await market.EnsureBookAsync(offer.Name, ct);
            var direct = market.IsBookFresh(offer.Name, TimeSpan.FromMinutes(30)) ? market.Match(offer.Name, "intact", true, relic: true) : new OrderMatch([], false);
            double? sell = direct.SubtypeMatched ? direct.Best?.Price : null;
            double? open = null; double? rare = null; string? bestPart = null; double? bestPartAsk = null;
            var primeRewards = relic?.Rewards.Where(reward => reward.RewardName.Contains(" Prime ", StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
            if (relic is not null && primeRewards.Length > 0 && primeRewards.All(reward => market.IsBookFresh(reward.RewardName, TimeSpan.FromMinutes(30))))
            {
                var prices = primeRewards.ToDictionary(reward => reward.RewardName.ToLowerInvariant(),
                    reward => market.RewardEstimate(reward.RewardName).Price, StringComparer.OrdinalIgnoreCase);
                if (prices.Values.All(price => price is > 0))
                {
                    open = relic.ExpectedValue(Refinement.Intact, prices);
                    var best = primeRewards.OrderByDescending(reward => prices.GetValueOrDefault(reward.RewardName.ToLowerInvariant()) ?? 0)
                        .ThenBy(reward => reward.RewardName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                    if (best is not null) { bestPart = best.RewardName; bestPartAsk = prices.GetValueOrDefault(best.RewardName.ToLowerInvariant()); }
                    rare = relic.Rewards.Where(reward => reward.Rarity.Equals("rare", StringComparison.OrdinalIgnoreCase))
                        .Sum(reward => Relic.Chance(Refinement.Intact, reward.Rarity));
                }
            }
            rows.Add(new(offer.Name, offer.Cost, sell, open, bestPart, bestPartAsk, rare));
        }
        latestAyaSnapshot = snapshot; latestAyaRows = rows.ToArray();
        return AyaText(snapshot, latestAyaRows);
    }

    private string AyaText(PrimeVendorSnapshot snapshot, IReadOnlyList<AyaValueRow> rows)
    {
        if (snapshot.Aya is not { } rotation || rotation.EndsAt <= DateTimeOffset.UtcNow)
            return "Varzia's active rotation is unavailable in the official feed. No old rotation is being presented as current.";
        var mode = state.AyaSort is AyaProfit.PrimeParts or AyaProfit.RelicSale ? state.AyaSort : AyaProfit.Overall;
        var ranked = AyaProfit.Sort(rows, mode);
        var modeLabel = mode switch
        {
            AyaProfit.PrimeParts => "Prime-part opening EV per Aya (from #prime-part-prices)",
            AyaProfit.RelicSale => "intact relic sell ask per Aya",
            _ => "higher of Prime-part opening EV or intact relic ask per Aya"
        };
        var lines = ranked.Take(15).Select((row, index) => $"**{index + 1}. {row.RelicName}** · {row.AyaCost} Aya · intact sell ask {Price(row.DirectSellAsk)} · solo intact Prime-part EV {Price(row.PrimePartEv)}" +
            (row.BestPrimePart is { } part ? $" · top part {part} {Price(row.BestPrimePartAsk)}" : "") +
            (row.RareChance.HasValue ? $" · rare {row.RareChance:0.#}%" : ""));
        var returning = snapshot.ReturningPrimes.Count > 0 ? string.Join(", ", snapshot.ReturningPrimes.Take(12)) : "not mapped";
        var next = "**Next Resurgence:** Not yet announced in the official preview.";
        if (snapshot.NextAya is { } upcoming)
        {
            var featured = PrimeVendors.FeaturedPrimeSets(upcoming.FeaturedPackage, market.PrimeSetNames);
            next = featured.Count > 0
                ? $"**Announced next:** {string.Join(" & ", featured.Select(name => name[..^" Set".Length]))} · starts <t:{upcoming.StartsAt.ToUnixTimeSeconds()}:F> · ends <t:{upcoming.EndsAt.ToUnixTimeSeconds()}:F>."
                : $"**Next Resurgence:** Official preview is available, but the featured Prime names could not be verified against the market catalog. Starts <t:{upcoming.StartsAt.ToUnixTimeSeconds()}:F>.";
        }
        return $"**Sorted by:** {modeLabel}. Varzia rotation ends <t:{rotation.EndsAt.ToUnixTimeSeconds()}:R>. Official manifest <t:{snapshot.SourceTime.ToUnixTimeSeconds()}:R>.\n\n" +
            (ranked.Count == 0 ? "No current Aya relic could be matched to the market catalog." : string.Join('\n', lines)) +
            $"\n\n**Returning Prime gear:** {returning}.\n{next} Future relic stock is not known until its manifest is published; this is an official preview, not a price prediction.\nPrime-part EV uses the same stabilized part prices as #prime-part-prices, weighted by intact drop chances. Unknown prices stay unranked for that sort. EV is one player's average reward value, not guaranteed profit; Aya farm time is not priced. Regal Aya cosmetics are excluded.";
    }

    private async Task DispatchButtonAsync(SocketMessageComponent interaction)
    {
        var aya = interaction.ChannelId == state.AyaChannelId && interaction.Data.CustomId.StartsWith("aya-sort:", StringComparison.Ordinal);
        var baro = interaction.ChannelId == state.BaroChannelId && interaction.Data.CustomId.StartsWith("baro-sort:", StringComparison.Ordinal);
        if (interaction.GuildId != guildId || (!aya && !baro)) return;
        await interaction.DeferAsync(ephemeral: true);
        _ = Task.Run(async () =>
        {
            try
            {
                var mode = interaction.Data.CustomId[(aya ? "aya-sort:" : "baro-sort:").Length..];
                if (aya && mode is not (AyaProfit.Overall or AyaProfit.PrimeParts or AyaProfit.RelicSale) ||
                    baro && mode is not (BaroEconomy.Sales or BaroEconomy.Ducats or BaroEconomy.Credits))
                { await interaction.FollowupAsync("Unknown sort.", ephemeral: true); return; }
                await gate.WaitAsync();
                try
                {
                    if (aya) state.AyaSort = mode; else state.BaroSort = mode;
                    Save();
                    var channel = bot.GetGuild(guildId)?.GetTextChannel(aya ? state.AyaChannelId : state.BaroChannelId);
                    if (channel is null) throw new InvalidOperationException("Vendor board channel unavailable.");
                    var body = aya
                        ? latestAyaSnapshot is { } ayaSnapshot ? AyaText(ayaSnapshot, latestAyaRows) : "Waiting for the current Varzia manifest and Prime-part prices."
                        : latestBaroSnapshot is { } baroSnapshot ? BaroText(baroSnapshot, latestBaroRows) : "Waiting for the current Baro manifest and market prices.";
                    await PublishAsync(channel, aya, aya ? "Prime Resurgence · Aya planner" : "Baro · investment watchlist", body, CancellationToken.None);
                }
                finally { gate.Release(); }
                await interaction.FollowupAsync("Ranking updated.", ephemeral: true);
            }
            catch (Exception error)
            {
                Console.WriteLine($"[vendor-sort] {error}");
                try { if (interaction.HasResponded) await interaction.FollowupAsync("Could not update the sort; the next refresh will retry.", ephemeral: true); }
                catch (Exception replyError) { Console.WriteLine($"[vendor-sort] response failed: {replyError.GetType().Name}"); }
            }
        }, CancellationToken.None);
    }

    private async Task<string> BaroTextAsync(PrimeVendorSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.Baro is not { } rotation)
        {
            latestBaroSnapshot = snapshot; latestBaroRows = [];
            return "Baro is not currently active." +
                (snapshot.NextBaroAt is { } next ? $" Next visit <t:{next.ToUnixTimeSeconds()}:R>." : "") +
                " The next stock will be ranked when published; no previous stock is shown as current.";
        }
        var currentItems = rotation.Offers.Select(offer => market.ItemIdForName(offer.Name) is { } id && market.CatalogItem(id) is { } item
                ? new BaroVisitItem(item.Slug, offer.Cost, offer.Credits) : null)
            .OfType<BaroVisitItem>().DistinctBy(item => item.Slug, StringComparer.Ordinal).ToArray();
        var visitIndex = visits.FindIndex(visit => visit.EndsAt == rotation.EndsAt);
        if (visitIndex < 0 || visits[visitIndex].Items.Length < currentItems.Length)
        {
            if (visitIndex >= 0) visits.RemoveAt(visitIndex);
            visits.Add(new(rotation.EndsAt, currentItems));
            visits = visits.Where(visit => visit.EndsAt >= DateTimeOffset.UtcNow.AddDays(-100))
                .OrderByDescending(visit => visit.EndsAt).Take(10).ToList();
            Json.WriteAtomic(visitsPath, visits);
        }
        var rows = new List<BaroBoardRow>();
        foreach (var offer in rotation.Offers)
        {
            var itemId = market.ItemIdForName(offer.Name);
            var item = itemId is null ? null : market.CatalogItem(itemId);
            if (item is null) continue;
            var priorVisit = visits.Where(visit => visit.EndsAt < DateTimeOffset.UtcNow.AddDays(-3)
                    && visit.Items.Any(stock => stock.Slug == item.Slug))
                .OrderByDescending(visit => visit.EndsAt).FirstOrDefault();
            if (!sales.TryGetValue(item.Slug, out var cached) || item.MaxRank is > 0 && cached.MaxRankSales is null || DateTimeOffset.UtcNow - cached.CheckedAt >
                (cached.Sales.MedianR0.HasValue ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(15))
                || cached.VisitEndedAt != priorVisit?.EndsAt)
            {
                try
                {
                    using var stats = await marketHttp.GetJsonAsync("https://api.warframe.market/v1/items/" + Uri.EscapeDataString(item.Slug) + "/statistics", ct, lowPriority: true);
                    var daily = stats.RootElement.Get("payload").Get("statistics_closed").Get("90days");
                    if (daily.ValueKind != System.Text.Json.JsonValueKind.Array) throw new InvalidDataException("Missing closed-order daily statistics.");
                    HistoricalSaleSummary? postVisit = null;
                    if (priorVisit is not null)
                    {
                        var after = PrimeVendors.SalesBetween(daily, priorVisit.EndsAt.AddDays(1),
                            DateTimeOffset.UtcNow < priorVisit.EndsAt.AddDays(30) ? DateTimeOffset.UtcNow : priorVisit.EndsAt.AddDays(30),
                            item.MaxRank is > 0);
                        if (after.ReportingDays >= 3) postVisit = after;
                    }
                    cached = new(DateTimeOffset.UtcNow, PrimeVendors.Sales(daily, DateTimeOffset.UtcNow, item.MaxRank is > 0),
                        postVisit, priorVisit?.EndsAt,
                        item.MaxRank is > 0 ? PrimeVendors.SalesAtRank(daily, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, item.MaxRank) : null);
                    sales[item.Slug] = cached;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is not OutOfMemoryException)
                { Console.WriteLine($"[baro-stats] {item.Slug}: {error.GetType().Name}"); cached = new(DateTimeOffset.UtcNow, new(null, 0, 0), null, priorVisit?.EndsAt); sales[item.Slug] = cached; }
            }
            if (!market.IsBookFresh(offer.Name, TimeSpan.FromMinutes(30))) await market.EnsureBookAsync(offer.Name, ct);
            var ask = market.IsBookFresh(offer.Name, TimeSpan.FromMinutes(30))
                ? (item.MaxRank is > 0 ? market.MatchRank(offer.Name, 0, true) : market.Match(offer.Name, null, true)).Best?.Price : null;
            var maxAsk = item.MaxRank is > 0 && market.IsBookFresh(offer.Name, TimeSpan.FromMinutes(30))
                ? market.MatchRank(offer.Name, item.MaxRank.Value, true).Best?.Price : null;
            rows.Add(new(offer, cached.Sales, cached.MaxRankSales, cached.PostVisit, ask, maxAsk, item.MaxRank));
        }
        Json.WriteAtomic(salesPath, sales);
        latestBaroSnapshot = snapshot; latestBaroRows = rows.ToArray();
        return BaroText(snapshot, latestBaroRows);
    }

    private string BaroText(PrimeVendorSnapshot snapshot, IReadOnlyList<BaroBoardRow> rows)
    {
        if (snapshot.Baro is not { } rotation || rotation.EndsAt <= DateTimeOffset.UtcNow)
            return "Baro is not currently active. Waiting for fresh official stock.";
        static double? ConfiguredCost(string name)
            => double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0 ? value : null;
        var ducatCost = ConfiguredCost("RELICFRAME_DUCAT_COST_PLAT");
        var creditCost = ConfiguredCost("RELICFRAME_CREDIT_COST_PLAT_PER_100K");
        var netKnown = ducatCost.HasValue && creditCost.HasValue;
        var mode = state.BaroSort is BaroEconomy.Ducats or BaroEconomy.Credits ? state.BaroSort : BaroEconomy.Sales;
        var ranked = BaroEconomy.Sort(rows.Select(row => new BaroValueRow(row.Offer.Name, row.Offer.Cost,
            row.Offer.Credits, row.Sales, row.MaxRankSales)), mode, ducatCost, creditCost);
        var lookup = rows.ToDictionary(row => row.Offer.Name, StringComparer.OrdinalIgnoreCase);
        var lines = ranked.Take(15).Select((value, index) =>
        {
            var row = lookup[value.Name];
            var basis = BaroEconomy.NetValue(value, ducatCost, creditCost) ?? value.R0.MedianR0;
            var perDucat = basis.HasValue && value.Ducats > 0 ? $"{basis.Value / value.Ducats * 100:0.#}p/100D" : "n/a";
            var perCredits = basis.HasValue && value.Credits > 0 ? $"{basis.Value / value.Credits * 100000:0.#}p/100k Cr" : "n/a";
            var max = row.MaxRank is > 0
                ? $" · R{row.MaxRank} 30d {Price(row.MaxRankSales?.MedianR0)} / ask {Price(row.MaxRankAsk)}"
                : "";
            return $"**{index + 1}. {value.Name}** · {value.Ducats}D + {value.Credits:N0} Cr · {value.R0.SalesPerDay:0.#} R0 sales/day\n" +
                $"R0 30d {Price(value.R0.MedianR0)} / ask {Price(row.R0Ask)}{max} · {(netKnown ? "net" : "value")} {perDucat}, {perCredits}" +
                (netKnown && BaroEconomy.NetValue(value, ducatCost, creditCost) is { } net ? $" · margin {net:+0.#;-0.#;0}p" : "") +
                (row.PostVisit?.MedianR0 is { } post ? $" · post-visit R0 {post:0.#}p" : "");
        });
        var label = mode switch
        {
            BaroEconomy.Ducats => netKnown ? "estimated net platinum per 100 Ducats" : "R0 sale value per 100 Ducats",
            BaroEconomy.Credits => netKnown ? "estimated net platinum per 100k Credits" : "R0 sale value per 100k Credits",
            _ => "reported R0 sales per day"
        };
        return $"**Sorted by:** {label}. Baro leaves <t:{rotation.EndsAt.ToUnixTimeSeconds()}:R>. Official stock <t:{snapshot.SourceTime.ToUnixTimeSeconds()}:R>. {ranked.Count}/{rotation.Offers.Count} mapped items.\n\n" +
            (ranked.Count == 0 ? "No mapped tradeable stock is available to rank." : string.Join("\n\n", lines)) +
            "\n\n30-day closed-order medians and reported sales are not guaranteed executions. R0 and max-rank values are separate; max-rank margin is not inferred because ranking consumes Endo and Credits. " +
            (netKnown ? "Net R0 margin subtracts your configured Ducat and Credit opportunity costs; it excludes trade tax."
                : "Value per resource is gross, not profit. Set both `RELICFRAME_DUCAT_COST_PLAT` (p/Ducat) and `RELICFRAME_CREDIT_COST_PLAT_PER_100K` (p/100k Credits) for estimated net R0 profit.");
    }

    private static string Price(double? value) => value is > 0 ? $"{value:0.#}p" : "unknown";

    private async Task PublishAsync(ITextChannel channel, bool aya, string title, string description, CancellationToken ct)
    {
        var text = description.Length <= 4096 ? description : description[..4093] + "…";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text + (aya ? state.AyaSort : state.BaroSort))));
        var oldId = aya ? state.AyaMessageId : state.BaroMessageId;
        var old = oldId == 0 ? null : await channel.GetMessageAsync(oldId) as IUserMessage;
        if (old is not null && (aya ? state.AyaHash : state.BaroHash) == hash) return;
        var embed = new EmbedBuilder().WithTitle(title).WithDescription(text).WithColor(aya ? new Color(0xB58BEEu) : new Color(0xF1C40Fu)).Build();
        var controls = aya ? new ComponentBuilder()
            .WithButton("Best overall", "aya-sort:overall", state.AyaSort == AyaProfit.Overall ? ButtonStyle.Primary : ButtonStyle.Secondary)
            .WithButton("Prime-part profit", "aya-sort:prime-parts", state.AyaSort == AyaProfit.PrimeParts ? ButtonStyle.Primary : ButtonStyle.Secondary)
            .WithButton("Sell relic", "aya-sort:relic-sale", state.AyaSort == AyaProfit.RelicSale ? ButtonStyle.Primary : ButtonStyle.Secondary)
            .Build() : new ComponentBuilder()
            .WithButton("Sales/day", "baro-sort:sales", state.BaroSort == BaroEconomy.Sales ? ButtonStyle.Primary : ButtonStyle.Secondary)
            .WithButton("Per 100 Ducats", "baro-sort:ducats", state.BaroSort == BaroEconomy.Ducats ? ButtonStyle.Primary : ButtonStyle.Secondary)
            .WithButton("Per 100k Credits", "baro-sort:credits", state.BaroSort == BaroEconomy.Credits ? ButtonStyle.Primary : ButtonStyle.Secondary)
            .Build();
        ulong messageId;
        if (old is null) { var sent = await channel.SendMessageAsync(embed: embed, components: controls, allowedMentions: AllowedMentions.None); messageId = sent.Id; }
        else { await old.ModifyAsync(properties => { properties.Embed = embed; properties.Components = controls; properties.AllowedMentions = AllowedMentions.None; }); messageId = old.Id; }
        if (aya) { state.AyaMessageId = messageId; state.AyaHash = hash; }
        else { state.BaroMessageId = messageId; state.BaroHash = hash; }
        Save();
    }

    private void Save() => Json.WriteAtomic(statePath, state);
    public async Task StopAsync() { if (cancellation is not null) await cancellation.CancelAsync(); if (worker is not null) await worker; status = "stopped"; }
    public async ValueTask DisposeAsync() { bot.ButtonExecuted -= DispatchButtonAsync; await StopAsync(); cancellation?.Dispose(); official.Dispose(); gate.Dispose(); }
}
