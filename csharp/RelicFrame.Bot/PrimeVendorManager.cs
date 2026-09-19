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
}
internal sealed record BaroSaleCache(DateTimeOffset CheckedAt, HistoricalSaleSummary Sales,
    HistoricalSaleSummary? PostVisit = null, DateTimeOffset? VisitEndedAt = null);
internal sealed record BaroVisitItem(string Slug, int Ducats, int Credits);
internal sealed record BaroVisitRecord(DateTimeOffset EndsAt, BaroVisitItem[] Items);

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
        if (snapshot.Aya is not { } rotation) return "Varzia's active rotation is unavailable in the official feed. No old rotation is being presented as current.";
        var rows = new List<(string Name, int Aya, double? Sell, double? Open, double? Rare)>();
        foreach (var offer in rotation.Offers)
        {
            var key = offer.Name.EndsWith(" Relic", StringComparison.OrdinalIgnoreCase) ? offer.Name[..^6] : offer.Name;
            var relic = relics.GetValueOrDefault(key);
            if (!market.IsBookFresh(offer.Name, TimeSpan.FromMinutes(30))) await market.EnsureBookAsync(offer.Name, ct);
            var direct = market.IsBookFresh(offer.Name, TimeSpan.FromMinutes(30)) ? market.Match(offer.Name, "intact", true, relic: true) : new OrderMatch([], false);
            double? sell = direct.SubtypeMatched ? direct.Best?.Price : null;
            double? open = null; double? rare = null;
            if (relic is not null && relic.Rewards.All(reward => market.IsBookFresh(reward.RewardName, TimeSpan.FromMinutes(30))))
            {
                var prices = relic.Rewards.ToDictionary(reward => reward.RewardName.ToLowerInvariant(),
                    reward => market.RewardEstimate(reward.RewardName).Price, StringComparer.OrdinalIgnoreCase);
                if (prices.Values.All(price => price is > 0))
                {
                    open = relic.ExpectedValue(Refinement.Intact, prices);
                    rare = relic.Rewards.Where(reward => reward.Rarity.Equals("rare", StringComparison.OrdinalIgnoreCase))
                        .Sum(reward => Relic.Chance(Refinement.Intact, reward.Rarity));
                }
            }
            rows.Add((offer.Name, offer.Cost, sell, open, rare));
        }
        var ranked = rows.OrderByDescending(row => Math.Max(row.Sell ?? 0, row.Open ?? 0) / row.Aya)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var lines = ranked.Take(15).Select((row, index) => $"**{index + 1}. {row.Name}** · {row.Aya} Aya · intact sell ask {Price(row.Sell)} · solo intact open EV {Price(row.Open)}" +
            (row.Rare.HasValue ? $" · rare {row.Rare:0.#}%" : ""));
        var returning = snapshot.ReturningPrimes.Count > 0 ? string.Join(", ", snapshot.ReturningPrimes.Take(12)) : "not mapped";
        return $"Varzia rotation ends <t:{rotation.EndsAt.ToUnixTimeSeconds()}:R>. Official manifest <t:{snapshot.SourceTime.ToUnixTimeSeconds()}:R>.\n\n" +
            (ranked.Length == 0 ? "No current Aya relic could be matched to the market catalog." : string.Join('\n', lines)) +
            $"\n\n**Returning Prime gear:** {returning}.\nOne Aya buys each listed relic at Varzia. Open EV is one player's average reward value, not guaranteed sale proceeds; no Aya/hour estimate without measured farm time. Regal Aya cosmetics are excluded.";
    }

    private async Task<string> BaroTextAsync(PrimeVendorSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.Baro is not { } rotation) return "Baro is not currently active." +
            (snapshot.NextBaroAt is { } next ? $" Next visit <t:{next.ToUnixTimeSeconds()}:R>." : "") +
            " The next stock will be ranked when published; no previous stock is shown as current.";
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
        var rows = new List<(VendorOffer Offer, HistoricalSaleSummary Sales, HistoricalSaleSummary? PostVisit,
            int? PreviousDucats, double? Ask)>();
        foreach (var offer in rotation.Offers)
        {
            var itemId = market.ItemIdForName(offer.Name);
            var item = itemId is null ? null : market.CatalogItem(itemId);
            if (item is null) continue;
            var priorVisit = visits.Where(visit => visit.EndsAt < DateTimeOffset.UtcNow.AddDays(-3)
                    && visit.Items.Any(stock => stock.Slug == item.Slug))
                .OrderByDescending(visit => visit.EndsAt).FirstOrDefault();
            if (!sales.TryGetValue(item.Slug, out var cached) || DateTimeOffset.UtcNow - cached.CheckedAt >
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
                        postVisit, priorVisit?.EndsAt);
                    sales[item.Slug] = cached;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is not OutOfMemoryException)
                { Console.WriteLine($"[baro-stats] {item.Slug}: {error.GetType().Name}"); cached = new(DateTimeOffset.UtcNow, new(null, 0, 0), null, priorVisit?.EndsAt); sales[item.Slug] = cached; }
            }
            if (!market.IsBookFresh(offer.Name, TimeSpan.FromMinutes(30))) await market.EnsureBookAsync(offer.Name, ct);
            var ask = market.IsBookFresh(offer.Name, TimeSpan.FromMinutes(30))
                ? (item.MaxRank is > 0 ? market.MatchRank(offer.Name, 0, true) : market.Match(offer.Name, null, true)).Best?.Price : null;
            rows.Add((offer, cached.Sales, cached.PostVisit,
                priorVisit?.Items.FirstOrDefault(stock => stock.Slug == item.Slug)?.Ducats, ask));
        }
        Json.WriteAtomic(salesPath, sales);
        var costKnown = double.TryParse(Environment.GetEnvironmentVariable("RELICFRAME_DUCAT_COST_PLAT"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ducatCost) && ducatCost >= 0;
        var ranked = rows.Where(row => row.Sales.MedianR0.HasValue)
            .OrderByDescending(row => costKnown ? (row.PostVisit?.MedianR0 ?? row.Sales.MedianR0!.Value) - row.Offer.Cost * ducatCost :
                (row.PostVisit?.MedianR0 ?? row.Sales.MedianR0!.Value) / row.Offer.Cost * Math.Min(1, row.Sales.SalesPerDay / 5))
            .ThenByDescending(row => row.Sales.SalesPerDay).ToArray();
        var lines = ranked.Take(15).Select((row, index) =>
            $"**{index + 1}. {row.Offer.Name}** · {row.Offer.Cost} Ducats + {row.Offer.Credits:N0} Credits · 30d R0 median {Price(row.Sales.MedianR0)}" +
            (row.PostVisit?.MedianR0 is { } post ? $" · tracked post-visit {post:0.#}p" : "") +
            $" · live R0 ask {Price(row.Ask)} · reported {row.Sales.SalesPerDay:0.#}/day" +
            (costKnown ? $" · estimated margin {(row.PostVisit?.MedianR0 ?? row.Sales.MedianR0!.Value) - row.Offer.Cost * ducatCost:+0.#;-0.#;0}p*" : "") +
            (costKnown && row.PostVisit?.MedianR0 is { } past && row.PreviousDucats is { } historicalCost
                ? $" · prior-visit modeled margin {past - historicalCost * ducatCost:+0.#;-0.#;0}p*" : ""));
        return $"At Baro until <t:{rotation.EndsAt.ToUnixTimeSeconds()}:R>. Official stock <t:{snapshot.SourceTime.ToUnixTimeSeconds()}:R>. {ranked.Length}/{rotation.Offers.Count} mapped stock items have recent closed-order activity.\n\n" +
            (ranked.Length == 0 ? "No stock item has enough recent rank-zero sales to rank safely." : string.Join('\n', lines)) +
            "\n\nOnly market-tradeable, mapped stock is ranked. Closed-order activity is not a confirmed trade or a future-price forecast. Post-visit medians appear only after this bot observed a prior Baro visit and at least three later reporting days; otherwise the 30-day median includes periods when he was present." +
            (costKnown ? " *Margin uses your configured platinum-per-Ducat cost, excludes Credits and trade tax." : " Set `RELICFRAME_DUCAT_COST_PLAT` to show an estimated platinum margin; otherwise ranking favors historical platinum per Ducat with liquidity.");
    }

    private static string Price(double? value) => value is > 0 ? $"{value:0.#}p" : "unknown";

    private async Task PublishAsync(ITextChannel channel, bool aya, string title, string description, CancellationToken ct)
    {
        var text = description.Length <= 4096 ? description : description[..4093] + "…";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var oldId = aya ? state.AyaMessageId : state.BaroMessageId;
        var old = oldId == 0 ? null : await channel.GetMessageAsync(oldId) as IUserMessage;
        if (old is not null && (aya ? state.AyaHash : state.BaroHash) == hash) return;
        var embed = new EmbedBuilder().WithTitle(title).WithDescription(text).WithColor(aya ? new Color(0xB58BEEu) : new Color(0xF1C40Fu)).Build();
        ulong messageId;
        if (old is null) { var sent = await channel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None); messageId = sent.Id; }
        else { await old.ModifyAsync(properties => { properties.Embed = embed; properties.AllowedMentions = AllowedMentions.None; }); messageId = old.Id; }
        if (aya) { state.AyaMessageId = messageId; state.AyaHash = hash; }
        else { state.BaroMessageId = messageId; state.BaroHash = hash; }
        Save();
    }

    private void Save() => Json.WriteAtomic(statePath, state);
    public async Task StopAsync() { if (cancellation is not null) await cancellation.CancelAsync(); if (worker is not null) await worker; status = "stopped"; }
    public async ValueTask DisposeAsync() { await StopAsync(); cancellation?.Dispose(); official.Dispose(); gate.Dispose(); }
}
