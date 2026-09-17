using System.Security.Cryptography;
using System.Text;
using Discord;
using Discord.WebSocket;
using RelicFrame.Core;

internal sealed record RelicListState
{
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
    public string Refinement { get; set; } = "Radiant";
    public double? MaxCost { get; set; }
    public double? MinReward { get; set; }
    public int Page { get; set; } = 1;
    public string? SelectedRelic { get; set; }
    public string RenderHash { get; set; } = "";
    public bool CleanupPending { get; set; }
}
internal sealed record RelicPanelGuildState
{
    public ulong CategoryId { get; set; }
    public Dictionary<string, RelicListState> Channels { get; set; } = new(StringComparer.Ordinal);
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
    public RelicPanelOptions Options { get; set; } = new();
    public bool CleanupBotMessages { get; set; }
}
internal sealed record RelicPanelStore
{
    public Dictionary<string, RelicPanelGuildState> Guilds { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class RelicPanelManager : IAsyncDisposable
{
    internal const string CategoryName = "THE LIST";
    internal const string GuideKey = "how-it-works";
    internal const string PrimePricesKey = "prime-part-prices";
    private const string ComponentRevision = "rf-list-components-v2";
    private const int PageSize = 15;
    private const double VaultedRewardFloor = 5;
    internal static readonly IReadOnlyDictionary<string, string> Lists = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["best-overall"] = "Best Overall", ["expected-profit"] = "Expected Profit", ["best-roi"] = "Best ROI",
        ["best-plat-to-trace"] = "Best Plat/Trace", ["cheapest"] = "Cheapest",
        ["best-ducat-farming"] = "Best Ducat Farming", ["guaranteed-profit"] = "Guaranteed Profit",
        ["lowest-risk"] = "Lowest Risk", ["best-win-chance"] = "Best Win Chance"
    };
    private sealed record PrimePartRow(string Name, RewardPriceEstimate Estimate, (string Relic, string Rarity, bool? Vaulted)[] Sources);
    private readonly DiscordSocketClient bot;
    private readonly LiveMarket market;
    private readonly IReadOnlyDictionary<string, Relic> relics;
    private readonly HashSet<string> unvaultedRewardNames;
    private readonly ulong targetGuild;
    private readonly string path;
    private readonly SemaphoreSlim update = new(1, 1);
    private readonly SemaphoreSlim setup = new(1, 1);
    private RelicPanelStore store;
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private string status = "stopped";
    public string Status => Volatile.Read(ref status);

    public RelicPanelManager(DiscordSocketClient bot, LiveMarket market, IReadOnlyDictionary<string, Relic> relics, ulong targetGuild, string runtime)
    {
        this.bot = bot; this.market = market; this.relics = relics; this.targetGuild = targetGuild; path = Path.Combine(runtime, "relic_panels.json");
        unvaultedRewardNames = relics.Values.Where(relic => relic.Vaulted is false).SelectMany(relic => relic.Rewards)
            .Select(reward => reward.RewardName.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        try { store = File.Exists(path) ? Json.Read<RelicPanelStore>(path) : new(); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { store = new(); status = "saved panel state unreadable; setup required"; }
        bot.ButtonExecuted += DispatchButtonAsync;
        bot.SelectMenuExecuted += DispatchSelectAsync;
    }
    private RelicPanelGuildState State(ulong id)
    {
        store.Guilds ??= new(StringComparer.Ordinal);
        var state = store.Guilds.GetValueOrDefault(id.ToString()) ?? (store.Guilds[id.ToString()] = new());
        state.Channels ??= new(StringComparer.Ordinal); state.Options ??= new(); return state;
    }
    private RelicListState ListState(ulong guild, string key)
    {
        var channels = State(guild).Channels;
        return channels.GetValueOrDefault(key) ?? (channels[key] = new());
    }
    private SocketGuildUser? CurrentBotMember(SocketGuild guild) => bot.CurrentUser is { } current ? guild.GetUser(current.Id) : null;
    private void Save() => Json.WriteAtomic(path, store);

    public async Task<string> SetupAsync(SocketGuild guild, CancellationToken ct, CancellationToken lifetime)
    {
        if (guild.Id != targetGuild) return "C# setup is restricted to its configured server.";
        var botMember = CurrentBotMember(guild);
        if (botMember is null) return "Discord is still loading this bot's server membership. Try setup again in a few seconds.";
        if (!botMember.GuildPermissions.ManageChannels) return "I need Manage Channels to create or repair THE LIST.";
        await setup.WaitAsync(ct);
        try
        {
            var state = State(guild.Id);
            ICategoryChannel? category = guild.GetCategoryChannel(state.CategoryId) ?? guild.CategoryChannels.FirstOrDefault(c => c.Name == CategoryName);
            category ??= await guild.CreateCategoryChannelAsync(CategoryName); state.CategoryId = category.Id; var created = new List<string>();
            foreach (var (key, title) in Lists)
            {
                var saved = ListState(guild.Id, key); var topic = $"{title} · vaulted relics sold by online sellers";
                ITextChannel? channel = guild.GetTextChannel(saved.ChannelId) ?? guild.TextChannels.FirstOrDefault(c => c.CategoryId == category.Id && c.Name == key);
                if (channel is null) { channel = await guild.CreateTextChannelAsync(key, p => { p.CategoryId = category.Id; p.Topic = topic; }); created.Add(key); }
                else if (channel.CategoryId != category.Id || channel.Topic != topic) await channel.ModifyAsync(p => { p.CategoryId = category.Id; p.Topic = topic; });
                saved.ChannelId = channel.Id; saved.RenderHash = ""; saved.CleanupPending = true;
            }
            var primeState = ListState(guild.Id, PrimePricesKey);
            const string primeTopic = "All Prime parts sorted by current price · select a part to see its relics";
            ITextChannel? primeChannel = guild.GetTextChannel(primeState.ChannelId) ?? guild.TextChannels.FirstOrDefault(c => c.CategoryId == category.Id && c.Name == PrimePricesKey);
            if (primeChannel is null) { primeChannel = await guild.CreateTextChannelAsync(PrimePricesKey, p => { p.CategoryId = category.Id; p.Topic = primeTopic; }); created.Add(PrimePricesKey); }
            else if (primeChannel.CategoryId != category.Id || primeChannel.Topic != primeTopic)
                await primeChannel.ModifyAsync(p => { p.CategoryId = category.Id; p.Topic = primeTopic; });
            primeState.ChannelId = primeChannel.Id; primeState.RenderHash = ""; primeState.CleanupPending = true;
            var guideState = ListState(guild.Id, GuideKey);
            ITextChannel? guide = guild.GetTextChannel(guideState.ChannelId) ?? guild.TextChannels.FirstOrDefault(c => c.CategoryId == category.Id && c.Name == GuideKey);
            if (guide is null) { guide = await guild.CreateTextChannelAsync(GuideKey, p => { p.CategoryId = category.Id; p.Topic = "How RelicFrame calculates profit, ROI, risk, traces, and ducats"; }); created.Add(GuideKey); }
            else if (guide.CategoryId != category.Id || guide.Topic != "How RelicFrame calculates profit, ROI, risk, traces, and ducats")
                await guide.ModifyAsync(p => { p.CategoryId = category.Id; p.Topic = "How RelicFrame calculates profit, ROI, risk, traces, and ducats"; });
            guideState.ChannelId = guide.Id; guideState.RenderHash = ""; guideState.CleanupPending = true;
            Save(); await market.StartAsync(false, lifetime); Start(lifetime); await UpdateAsync(ct);
            return $"Configured {Lists.Count} ranking channels, **#{PrimePricesKey}**, and **#{GuideKey}** in **{CategoryName}**." + (created.Count > 0 ? $" Created: {string.Join(", ", created)}." : " Existing channels were repaired.");
        }
        finally { setup.Release(); }
    }
    public void Start(CancellationToken lifetime)
    {
        if (worker is { IsCompleted: false }) return; cancellation?.Dispose(); cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        worker = Task.Run(() => LoopAsync(cancellation.Token)); status = "starting";
    }
    public async Task StopAsync()
    {
        if (cancellation is not null) await cancellation.CancelAsync(); if (worker is not null) await worker;
        status = "stopped; list messages and filters retained";
    }
    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var began = DateTimeOffset.UtcNow;
                try { await UpdateAsync(ct); }
                catch (Exception e) when (!ct.IsCancellationRequested && e is not OutOfMemoryException)
                { status = $"update failed ({e.GetType().Name}); previous lists retained"; Console.WriteLine($"[lists] {e}"); }
                var wait = TimeSpan.FromMinutes(5) - (DateTimeOffset.UtcNow - began); await Task.Delay(wait > TimeSpan.FromSeconds(5) ? wait : TimeSpan.FromSeconds(5), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public async Task UpdateAsync(CancellationToken ct)
    {
        await update.WaitAsync(ct);
        try
        {
            var guild = bot.GetGuild(targetGuild) ?? throw new InvalidOperationException("Configured server unavailable.");
            var botMember = CurrentBotMember(guild) ?? throw new InvalidOperationException("Discord has not loaded this bot's server membership yet.");
            var state = State(targetGuild); if (state.Channels.Count == 0) throw new InvalidOperationException("THE LIST is not configured; run /rf-panel setup.");
            var failures = new List<string>();
            var sellerCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var refinementGroup in Lists.Keys.GroupBy(key => ListState(targetGuild, key).Refinement, StringComparer.OrdinalIgnoreCase))
            {
                var tier = Enum.TryParse<Refinement>(refinementGroup.Key, true, out var parsed) ? parsed : Refinement.Radiant; var snapshot = market.Snapshot(tier);
                RelicRow[] baseRows = snapshot.ReadyBooks == 0 ? [] : relics.Values.Where(r => r.Vaulted is true)
                    .Select(r => RelicRow.Compute(r, tier, snapshot.Prices, snapshot.RelicPrices.GetValueOrDefault(r.RelicName) ?? new(), snapshot.Ducats, scope: "Online only"))
                    .Where(r => r.Online.Profit.PriceKnown && r.Online.Cost.HasValue && !r.Online.ZeroQuantity && HasMeaningfulVaultedReward(r, snapshot)).ToArray();
                foreach (var key in refinementGroup)
                    try { await UpdateListAsync(guild, botMember.Id, key, tier, snapshot, baseRows, sellerCache); }
                    catch (Exception e) when (e is Discord.Net.HttpException or InvalidOperationException or IOException) { failures.Add($"{key}: {e.GetType().Name}"); }
            }
            try { await UpdatePrimePricesAsync(guild, botMember.Id, market.Snapshot(Refinement.Radiant)); }
            catch (Exception e) when (e is Discord.Net.HttpException or InvalidOperationException or IOException) { failures.Add($"{PrimePricesKey}: {e.GetType().Name}"); }
            try { await UpdateGuideAsync(guild, botMember.Id); }
            catch (Exception e) when (e is Discord.Net.HttpException or InvalidOperationException or IOException) { failures.Add($"{GuideKey}: {e.GetType().Name}"); }
            Save(); status = failures.Count == 0 ? $"ready; {Lists.Count} rankings + Prime prices + guide" : $"partial: {failures.Count} list failures; {failures[0]}";
        }
        finally { update.Release(); }
    }
    private async Task UpdateListAsync(SocketGuild guild, ulong botUserId, string key, Refinement tier, MarketSnapshot snapshot, RelicRow[] baseRows, Dictionary<string, string?> sellerCache)
    {
        var saved = ListState(guild.Id, key); var channel = guild.GetTextChannel(saved.ChannelId) ?? throw new InvalidOperationException($"#{key} is missing; run setup again.");
        string description; uint color = 0x95A5A6;
        RelicRow[] ranked = [];
        if (snapshot.ReadyBooks == 0 || market.Status == "loading catalog" || market.IsBootstrapping) description = $"Live market bootstrap is in progress: {market.Status}; {snapshot.ReadyBooks}/{snapshot.TotalBooks} books loaded. ROI and risk rankings appear after the initial sweep so partial prices are not presented as final.";
        else
        {
            ranked = RelicRow.Rank(baseRows.Where(r => r.Passes("Online only", "Vaulted", maxCost: saved.MaxCost, minReward: saved.MinReward,
                guaranteedOnly: key == "guaranteed-profit")), Lists[key], "Online only").ToArray();
            var totalPages = Math.Max(1, (ranked.Length + PageSize - 1) / PageSize); saved.Page = Math.Clamp(saved.Page, 1, totalPages);
            var pageRows = ranked.Skip((saved.Page - 1) * PageSize).Take(PageSize).ToArray();
            if (saved.SelectedRelic is null || !pageRows.Any(row => row.Relic.RelicName == saved.SelectedRelic)) saved.SelectedRelic = pageRows.FirstOrDefault()?.Relic.RelicName;
            description = string.Join('\n', pageRows.Select((r, offset) =>
            {
                var sellerKey = tier + "\0" + r.Relic.RelicName;
                if (!sellerCache.TryGetValue(sellerKey, out var seller)) sellerCache[sellerKey] = seller = market.Match(r.Relic.RelicName, tier.ToString(), true, true).Best?.Seller;
                var rank = (saved.Page - 1) * PageSize + offset + 1;
                return $"**{rank}. {ApplicationEmojis.Relic(r.Relic.RelicName, tier)} {r.Relic.RelicName}** · {Price(r.Online.Cost)}p{Quantity(r.Online.Quantity)} · EV profit {Signed(r.Online.Profit.ExpectedProfit)}p · ROI {Percent(r.Online.Profit.ExpectedRoiPct)} · win {Percent(r.Online.WinChance)} · risk {r.Online.Risk}/100 · seller `{(string.IsNullOrWhiteSpace(seller) ? "Unknown" : seller)}`";
            }));
            if (description.Length == 0) description = "No currently-online vaulted relic listings match these filters.";
            color = pageRows.FirstOrDefault()?.Online.Category switch { "green" => 0x2ECC71u, "yellow" => 0xF1C40Fu, "red" => 0xE74C3Cu, _ => 0x95A5A6u };
            description += $"\n\nRefinement: **{tier}** · Vault: **Vaulted only** · Sellers: **Online only** · Trace rate: **0** · Max cost: **{Filter(saved.MaxCost)}** · Min reward: **{Filter(saved.MinReward)}**\nPage **{saved.Page}/{totalPages}** · {ranked.Length} matching relics · market {market.Status}; {snapshot.ReadyBooks}/{snapshot.TotalBooks} books; oldest successful book {(snapshot.FetchedAt == default ? "unknown" : $"<t:{snapshot.FetchedAt.ToUnixTimeSeconds()}:R>")}.";
        }
        var title = $"{ApplicationEmojis.VoidTearBlack} {Lists[key]} · Vaulted Online Relics";
        description = description.Length <= 4096 ? description : description[..4093] + "…";
        var renderHash = Hash(ComponentRevision, title, description, color.ToString(), saved.Page.ToString(), saved.SelectedRelic ?? "");
        if (saved.MessageId != 0 && saved.RenderHash == renderHash && !saved.CleanupPending) return;
        var embed = new EmbedBuilder().WithTitle(title).WithDescription(description).WithColor(new Color(color)).Build();
        var components = BuildComponents(key, ranked, saved);
        var old = saved.MessageId == 0 ? null : await channel.GetMessageAsync(saved.MessageId) as IUserMessage;
        if (old is null) { var sent = await channel.SendMessageAsync(embed: embed, components: components, allowedMentions: AllowedMentions.None); saved.MessageId = sent.Id; }
        else await old.ModifyAsync(p => { p.Embed = embed; p.Components = components; p.AllowedMentions = AllowedMentions.None; });
        saved.RenderHash = renderHash;
        if (saved.CleanupPending) { await DiscordCleanup.BotMessagesAsync(channel, botUserId, saved.MessageId); saved.CleanupPending = false; }
    }
    private static MessageComponent BuildComponents(string key, RelicRow[] ranked, RelicListState saved)
    {
        var totalPages = Math.Max(1, (ranked.Length + PageSize - 1) / PageSize);
        var pageRows = ranked.Skip((saved.Page - 1) * PageSize).Take(PageSize).ToArray(); var hasRows = pageRows.Length > 0;
        var options = pageRows.Select(row => new SelectMenuOptionBuilder(row.Relic.RelicName, row.Relic.RelicName, isDefault: row.Relic.RelicName == saved.SelectedRelic)).ToList();
        if (options.Count == 0) options.Add(new SelectMenuOptionBuilder("No relics match the current filters", "__none__"));
        return new ComponentBuilder()
            .WithButton("◀ Prev", $"rf-list:prev:{key}", ButtonStyle.Secondary, disabled: saved.Page <= 1, row: 0)
            .WithButton("Next ▶", $"rf-list:next:{key}", ButtonStyle.Secondary, disabled: saved.Page >= totalPages, row: 0)
            .WithButton("Show drops", $"rf-list:drops:{key}", ButtonStyle.Secondary, new Emoji("📦"), disabled: !hasRows, row: 0)
            .WithButton("/w seller", $"rf-list:buy:{key}", ButtonStyle.Success, new Emoji("💬"), disabled: !hasRows, row: 0)
            .WithSelectMenu($"rf-list:select:{key}", options, "Select a relic…", disabled: !hasRows, row: 1)
            .Build();
    }
    private PrimePartRow[] PrimePartRows()
    {
        return relics.Values.SelectMany(relic => relic.Rewards
                .Where(reward => reward.RewardName.Contains(" Prime ", StringComparison.OrdinalIgnoreCase))
                .Select(reward => (relic, reward)))
            .GroupBy(row => row.reward.RewardName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PrimePartRow(group.Key, market.RewardEstimate(group.Key), group
                .Select(row => (row.relic.RelicName, row.reward.Rarity, row.relic.Vaulted))
                .Distinct().OrderBy(row => row.RelicName, StringComparer.OrdinalIgnoreCase)
                .Select(row => (row.RelicName, row.Rarity, row.Vaulted)).ToArray()))
            .OrderByDescending(row => row.Estimate.Price.HasValue)
            .ThenByDescending(row => row.Estimate.Price)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private PrimePartRow? FindPrimePart(string name)
    {
        var sources = relics.Values.SelectMany(relic => relic.Rewards
                .Where(reward => reward.RewardName.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(reward => (relic.RelicName, reward.Rarity, relic.Vaulted)))
            .Distinct().OrderBy(row => row.RelicName, StringComparer.OrdinalIgnoreCase)
            .Select(row => (row.RelicName, row.Rarity, row.Vaulted)).ToArray();
        return sources.Length == 0 ? null : new(name, market.RewardEstimate(name), sources);
    }
    private async Task UpdatePrimePricesAsync(SocketGuild guild, ulong botUserId, MarketSnapshot snapshot)
    {
        var saved = ListState(guild.Id, PrimePricesKey); var channel = guild.GetTextChannel(saved.ChannelId) ?? throw new InvalidOperationException($"#{PrimePricesKey} is missing; run setup again.");
        var rows = market.IsBootstrapping || snapshot.ReadyBooks == 0 ? [] : PrimePartRows();
        var totalPages = Math.Max(1, (rows.Length + PageSize - 1) / PageSize); saved.Page = Math.Clamp(saved.Page, 1, totalPages);
        var pageRows = rows.Skip((saved.Page - 1) * PageSize).Take(PageSize).ToArray();
        if (saved.SelectedRelic is null || !pageRows.Any(row => row.Name.Equals(saved.SelectedRelic, StringComparison.OrdinalIgnoreCase))) saved.SelectedRelic = pageRows.FirstOrDefault()?.Name;
        string description;
        if (market.IsBootstrapping || snapshot.ReadyBooks == 0)
            description = $"Live market bootstrap is in progress: {market.Status}; {snapshot.ReadyBooks}/{snapshot.TotalBooks} books loaded. Prime-part prices appear after the complete initial sweep.";
        else
        {
            description = string.Join('\n', pageRows.Select((row, offset) =>
            {
                var availability = row.Sources.Any(source => source.Vaulted is false) ? "Unvaulted" : "Vaulted";
                return $"**{(saved.Page - 1) * PageSize + offset + 1}. {row.Name}** · {Price(row.Estimate.Price)}p · {availability} · {row.Sources.Length} relic{(row.Sources.Length == 1 ? "" : "s")}";
            }));
            if (description.Length == 0) description = "No priced Prime parts are available from the current relic catalog.";
            description += $"\n\nSorted by estimated sell price, highest first. Select a part to see every source relic and the price basis.\nPage **{saved.Page}/{totalPages}** · {rows.Length} Prime parts · market {market.Status}; {snapshot.ReadyBooks}/{snapshot.TotalBooks} books.";
        }
        description = description.Length <= 4096 ? description : description[..4093] + "…";
        const string title = "💎 Prime Part Prices · Highest First";
        var hash = Hash(ComponentRevision, title, description, saved.Page.ToString(), saved.SelectedRelic ?? "");
        if (saved.MessageId != 0 && saved.RenderHash == hash && !saved.CleanupPending) return;
        var embed = new EmbedBuilder().WithTitle(title).WithDescription(description).WithColor(new Color(0xB084F5)).Build();
        var components = BuildPrimePriceComponents(rows, saved);
        var old = saved.MessageId == 0 ? null : await channel.GetMessageAsync(saved.MessageId) as IUserMessage;
        if (old is null) { var sent = await channel.SendMessageAsync(embed: embed, components: components, allowedMentions: AllowedMentions.None); saved.MessageId = sent.Id; }
        else await old.ModifyAsync(p => { p.Embed = embed; p.Components = components; p.AllowedMentions = AllowedMentions.None; });
        saved.RenderHash = hash;
        if (saved.CleanupPending) { await DiscordCleanup.BotMessagesAsync(channel, botUserId, saved.MessageId); saved.CleanupPending = false; }
    }
    private static MessageComponent BuildPrimePriceComponents(PrimePartRow[] rows, RelicListState saved)
    {
        var totalPages = Math.Max(1, (rows.Length + PageSize - 1) / PageSize);
        var pageRows = rows.Skip((saved.Page - 1) * PageSize).Take(PageSize).ToArray(); var hasRows = pageRows.Length > 0;
        var options = pageRows.Select(row => new SelectMenuOptionBuilder(row.Name, row.Name, $"{Price(row.Estimate.Price)}p · {row.Sources.Length} source relics", isDefault: row.Name.Equals(saved.SelectedRelic, StringComparison.OrdinalIgnoreCase))).ToList();
        if (!hasRows) options.Add(new SelectMenuOptionBuilder("No Prime parts available", "__none__"));
        return new ComponentBuilder()
            .WithButton("◀ Prev", $"rf-list:prev:{PrimePricesKey}", ButtonStyle.Secondary, disabled: saved.Page <= 1, row: 0)
            .WithButton("Next ▶", $"rf-list:next:{PrimePricesKey}", ButtonStyle.Secondary, disabled: saved.Page >= totalPages, row: 0)
            .WithSelectMenu($"rf-list:select:{PrimePricesKey}", options, "Select a Prime part…", disabled: !hasRows, row: 1).Build();
    }
    private async Task ShowPrimePartAsync(SocketMessageComponent interaction, string selected)
    {
        var row = FindPrimePart(selected);
        if (row is null) { await interaction.FollowupAsync("That Prime part is no longer in the current relic catalog.", ephemeral: true); return; }
        var basis = row.Estimate.Stabilized && row.Estimate.OnlineFloor is { } online && row.Estimate.RecentVisibleMedian is { } recent
            ? $"Stabilized estimate **{Price(row.Estimate.Price)}p** · online floor {Price(online)}p · recent-visible median {Price(recent)}p"
            : $"Current online floor **{Price(row.Estimate.Price)}p**";
        var sources = row.Sources.Select(source => $"**{source.Relic}** · {System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(source.Rarity.ToLowerInvariant())} · {(source.Vaulted is false ? "Unvaulted" : "Vaulted")}");
        var description = basis + "\n\n" + string.Join('\n', sources);
        if (description.Length > 4096) description = description[..4093] + "…";
        var embed = new EmbedBuilder().WithTitle($"💎 {row.Name}").WithDescription(description).WithColor(new Color(0xB084F5))
            .WithFooter("Prices are current asks, not completed sales. Unvaulted parts use the stabilized online/recent-visible estimate.").Build();
        await interaction.FollowupAsync(embed: embed, ephemeral: true, allowedMentions: AllowedMentions.None);
    }
    private async Task UpdateGuideAsync(SocketGuild guild, ulong botUserId)
    {
        var saved = ListState(guild.Id, GuideKey); var channel = guild.GetTextChannel(saved.ChannelId) ?? throw new InvalidOperationException($"#{GuideKey} is missing; run setup again.");
        var embed = new EmbedBuilder().WithTitle("How THE LIST works").WithColor(new Color(0x5865F2))
            .WithDescription($"Every ranking uses live Warframe.market sell orders for **Vaulted relics containing at least one Vaulted reward worth {VaultedRewardFloor:0}p or more**. Mixed Vaulted/Unvaulted rewards are allowed, but an Unvaulted reward cannot qualify the relic by itself. Values can move while the initial market sweep loads; the panels wait for that sweep before presenting ROI/risk as final.")
            .AddField("Cost", "The cheapest usable online price for one relic at the selected refinement. `×N` is the quantity advertised at that price. A `~`/mismatch warning means the exact refinement was unavailable and another tier was used.")
            .AddField("Expected value and expected profit", "Expected value (EV) is the probability-weighted average platinum value of all six rewards. **Expected profit = EV − relic cost − optional trace cost.** It describes a long-run average, not what one opening guarantees.")
            .AddField("ROI", "**ROI = expected profit ÷ total cost × 100.** Positive ROI means the average modeled return exceeds cost; negative ROI means it does not. A high ROI can still lose on an individual opening.")
            .AddField("Guaranteed / worst-case profit", "Worst-case profit uses the least valuable reward. A relic is green only when even that outcome exceeds its total cost. Yellow has positive expected profit but can lose; red has non-positive expected profit; white means price data is missing.")
            .AddField("Win chance", "The chance that one opening's reward value is greater than the modeled total cost. It is different from ROI: ROI measures average return size, while win chance measures how often one opening finishes ahead.")
            .AddField("Risk score · 0–100", "Lower is safer. The heuristic adds up to 40 points for loss probability, up to 25 for worst-case loss severity, 15 for a refinement fallback, 10 for a lone/outlier listing, and 10 when a listing is not immediately actionable. It is a quick comparison aid, not a guarantee.")
            .AddField("Plat per Trace", "Expected reward value divided by traces spent from Intact: 25 Exceptional, 50 Flawless, or 100 Radiant. It measures trace efficiency and does not subtract the relic's purchase price.")
            .AddField("Ducat farming", "Expected ducats weights every reward's ducat value by its drop chance. Ducats per platinum compares that expected ducat return with the relic cost; higher is better.")
            .AddField("What each channel sorts", "**Best Overall** combines expected profit, capped ROI, win chance and risk. **Expected Profit** sorts average platinum gain. **Best ROI** sorts raw percentage return; lower risk and higher profit break ties. **Best Win Chance** sorts the probability that one opening finishes above total cost, highest first. **Best Plat/Trace** favors value per trace. **Cheapest** sorts acquisition cost. **Best Ducat Farming** favors expected ducats per platinum. **Guaranteed Profit** excludes every relic whose worst reward is not profitable, then sorts worst-case profit. **Lowest Risk** sorts risk upward.")
            .AddField("Using the controls", "Use **Prev/Next** to change pages, choose a relic, then **Show drops** or **/w seller**. In **#prime-part-prices**, choose a part to see its price and every source relic. Drop rows sort Rare → Uncommon → Common, then price and name. Controls reply privately. Managers can change ranking filters with `/rf-panel config`.")
            .AddField("Freshness and efficiency", "Warframe.market books reconcile continuously across five minutes. The first REST pass has priority over Riven scanning. Full snapshots are shared for 10 seconds (1 during bootstrap), **Show drops** reads only six books, and Prime selections acknowledge before looking up one part. Controls run outside the gateway task, `/rf-status` performs no full snapshot, and three command workers keep requests moving. THE LIST writes only when visible results change. WebSocket updates help but cannot replace REST or API limits.")
            .WithFooter("Market asks can change or disappear. RelicFrame never contacts sellers or performs trades.").Build();
        const string guideRevision = "the-list-guide-2026-09-07-sort-audit-v10";
        if (saved.MessageId != 0 && saved.RenderHash == guideRevision && !saved.CleanupPending) return;
        var old = saved.MessageId == 0 ? null : await channel.GetMessageAsync(saved.MessageId) as IUserMessage;
        if (old is null) { var sent = await channel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None); saved.MessageId = sent.Id; }
        else await old.ModifyAsync(p => { p.Embed = embed; p.Components = new ComponentBuilder().Build(); p.AllowedMentions = AllowedMentions.None; });
        saved.RenderHash = guideRevision;
        if (saved.CleanupPending) { await DiscordCleanup.BotMessagesAsync(channel, botUserId, saved.MessageId); saved.CleanupPending = false; }
    }
    private async Task UpdateSingleAsync(string key, CancellationToken ct)
    {
        await update.WaitAsync(ct);
        try
        {
            var guild = bot.GetGuild(targetGuild) ?? throw new InvalidOperationException("Configured server unavailable.");
            var member = CurrentBotMember(guild) ?? throw new InvalidOperationException("Discord has not loaded this bot's server membership yet.");
            if (key == PrimePricesKey) { await UpdatePrimePricesAsync(guild, member.Id, market.Snapshot(Refinement.Radiant)); Save(); return; }
            var saved = ListState(targetGuild, key); var tier = Enum.TryParse<Refinement>(saved.Refinement, true, out var parsed) ? parsed : Refinement.Radiant;
            var snapshot = market.Snapshot(tier);
            var rows = snapshot.ReadyBooks == 0 ? [] : relics.Values.Where(relic => relic.Vaulted is true)
                .Select(relic => RelicRow.Compute(relic, tier, snapshot.Prices, snapshot.RelicPrices.GetValueOrDefault(relic.RelicName) ?? new(), snapshot.Ducats, scope: "Online only"))
                .Where(row => row.Online.Profit.PriceKnown && row.Online.Cost.HasValue && !row.Online.ZeroQuantity && HasMeaningfulVaultedReward(row, snapshot)).ToArray();
            await UpdateListAsync(guild, member.Id, key, tier, snapshot, rows, new(StringComparer.OrdinalIgnoreCase)); Save();
        }
        finally { update.Release(); }
    }
    private bool HasMeaningfulVaultedReward(RelicRow row, MarketSnapshot snapshot) => row.Relic.Rewards
        .Where(reward => !unvaultedRewardNames.Contains(reward.RewardName.Trim().ToLowerInvariant()))
        .Any(reward => snapshot.Prices.GetValueOrDefault(reward.RewardName.Trim().ToLowerInvariant()) is >= VaultedRewardFloor);
    private static string Hash(params string[] values) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', values))));
    internal static bool TryComponent(string customId, out string action, out string key)
    {
        action = key = "";
        if (!customId.StartsWith("rf-list:", StringComparison.Ordinal)) return false;
        var payload = customId[8..];
        var parts = payload.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var knownActions = new HashSet<string>(["prev", "next", "drops", "buy", "select"], StringComparer.Ordinal);
        action = parts.FirstOrDefault(knownActions.Contains) ?? "";
        key = parts.FirstOrDefault(part => Lists.ContainsKey(part) || part == PrimePricesKey) ?? "";

        // The first Prime-price panel briefly shipped with these two layouts. Keep
        // them readable so a click made while the bot is replacing that message is
        // still acknowledged and can show the part's source relics.
        if (key.Length == 0 && payload.Contains("prime", StringComparison.Ordinal)) key = PrimePricesKey;
        if (action.Length == 0 && payload.Contains("select", StringComparison.Ordinal)) action = "select";
        return action.Length > 0 && key.Length > 0;
    }
    private async Task DispatchButtonAsync(SocketMessageComponent interaction)
    {
        if (interaction.GuildId != targetGuild || !interaction.Data.CustomId.StartsWith("rf-list:", StringComparison.Ordinal)) return;
        if (!TryComponent(interaction.Data.CustomId, out var action, out _) || action is not ("prev" or "next" or "drops" or "buy"))
        {
            Console.WriteLine($"[relic-panel-interaction] Unsupported button id '{interaction.Data.CustomId}' on message {interaction.Message.Id}.");
            await interaction.RespondAsync("This list control is obsolete. Use the buttons on the current ranking message.", ephemeral: true); return;
        }
        await interaction.DeferAsync(ephemeral: action is "drops" or "buy");
        DispatchComponent(interaction, HandleButtonAsync);
    }
    private async Task DispatchSelectAsync(SocketMessageComponent interaction)
    {
        if (interaction.GuildId != targetGuild || !interaction.Data.CustomId.StartsWith("rf-list:", StringComparison.Ordinal)) return;
        if (!TryComponent(interaction.Data.CustomId, out var action, out var key) || action != "select")
        {
            Console.WriteLine($"[relic-panel-interaction] Unsupported select id '{interaction.Data.CustomId}' on message {interaction.Message.Id}.");
            await interaction.RespondAsync("This list menu is obsolete. Use the controls on the current ranking message.", ephemeral: true); return;
        }
        var selected = interaction.Data.Values.FirstOrDefault();
        if (selected is null || selected == "__none__") { await interaction.RespondAsync(key == PrimePricesKey ? "No Prime part is available on this page." : "No relic is available on this page.", ephemeral: true); return; }
        await interaction.DeferAsync(ephemeral: key == PrimePricesKey);
        DispatchComponent(interaction, HandleSelectAsync);
    }
    private static void DispatchComponent(SocketMessageComponent interaction, Func<SocketMessageComponent, Task> handler)
    {
        _ = Task.Run(async () =>
        {
            try { await handler(interaction); }
            catch (Exception error)
            {
                Console.WriteLine($"[relic-panel-interaction] {error.GetType().Name}: {error.Message}");
                try
                {
                    const string message = "That list action could not finish. The saved board is still available; try again shortly.";
                    if (interaction.HasResponded) await interaction.FollowupAsync(message, ephemeral: true);
                    else await interaction.RespondAsync(message, ephemeral: true);
                }
                catch (Exception responseError) { Console.WriteLine($"[relic-panel-interaction] Response failed: {responseError.GetType().Name}"); }
            }
        }, CancellationToken.None);
    }
    private async Task HandleSelectAsync(SocketMessageComponent interaction)
    {
        if (interaction.GuildId != targetGuild || !interaction.Data.CustomId.StartsWith("rf-list:", StringComparison.Ordinal)) return;
        if (!TryComponent(interaction.Data.CustomId, out _, out var key)) return;
        var selected = interaction.Data.Values.FirstOrDefault();
        if (key == PrimePricesKey)
        {
            if (selected is null || selected == "__none__") return;
            var savedPrime = ListState(targetGuild, key); savedPrime.SelectedRelic = selected; Save(); await ShowPrimePartAsync(interaction, selected); return;
        }
        if (selected is null || selected == "__none__" || !relics.ContainsKey(selected)) { await interaction.FollowupAsync("That relic is no longer available on this page.", ephemeral: true); return; }
        var saved = ListState(targetGuild, key); saved.SelectedRelic = selected; Save();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await UpdateSingleAsync(key, timeout.Token);
    }
    private async Task HandleButtonAsync(SocketMessageComponent interaction)
    {
        if (interaction.GuildId != targetGuild || !interaction.Data.CustomId.StartsWith("rf-list:", StringComparison.Ordinal)) return;
        if (!TryComponent(interaction.Data.CustomId, out var action, out var key)) return;
        var saved = ListState(targetGuild, key);
        if (action is "prev" or "next")
        {
            saved.Page = Math.Max(1, saved.Page + (action == "next" ? 1 : -1)); saved.SelectedRelic = null; Save();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await UpdateSingleAsync(key, timeout.Token); return;
        }
        if (key == PrimePricesKey) { await interaction.FollowupAsync("Select a Prime part from the menu to view its price and source relics.", ephemeral: true); return; }
        var relicName = saved.SelectedRelic;
        if (relicName is null || !relics.TryGetValue(relicName, out var relic)) { await interaction.FollowupAsync("Select a relic first.", ephemeral: true); return; }
        var tier = Enum.TryParse<Refinement>(saved.Refinement, true, out var parsed) ? parsed : Refinement.Radiant;
        if (action == "drops")
        {
            var rewardPrices = relic.Rewards.ToDictionary(reward => reward.RewardName, reward => market.RewardEstimate(reward.RewardName), StringComparer.OrdinalIgnoreCase);
            var lines = relic.DisplayRewards(reward => rewardPrices.GetValueOrDefault(reward.RewardName)?.Price).Select(reward =>
            {
                var estimate = rewardPrices.GetValueOrDefault(reward.RewardName); var price = estimate?.Price;
                var rarity = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(reward.Rarity.ToLowerInvariant());
                var availability = unvaultedRewardNames.Contains(reward.RewardName.Trim().ToLowerInvariant()) ? "Unvaulted" : "Vaulted";
                var basis = estimate is { Stabilized: true, OnlineFloor: { } online, RecentVisibleMedian: { } recent }
                    ? $" (online {Price(online)}p · recent median {Price(recent)}p)" : "";
                return $"**{reward.RewardName}** · {rarity} · {Relic.Chance(tier, reward.Rarity):0.##}% · {Price(price)}p{basis} · {availability}";
            });
            var embed = new EmbedBuilder().WithTitle($"📦 {relicName} drops · {tier}").WithDescription(string.Join('\n', lines))
                .WithColor(new Color(0x5865F2)).WithFooter($"Vaulted: online floor. Unvaulted: stabilized online/recent-visible estimate. Qualification requires a Vaulted reward worth at least {VaultedRewardFloor:0}p.").Build();
            await interaction.FollowupAsync(embed: embed, ephemeral: true, allowedMentions: AllowedMentions.None); return;
        }
        if (action == "buy")
        {
            var order = market.Match(relicName, tier.ToString(), true, true).Best;
            if (order is null || string.IsNullOrWhiteSpace(order.Seller)) { await interaction.FollowupAsync("That online listing disappeared. Refreshing the list will select the next current seller.", ephemeral: true); return; }
            var seller = order.Seller.Replace('`', '\''); var whisper = $"/w {seller} Hi! I want to buy: {relicName} for {order.Price:0.##} platinum. (warframe.market)";
            var embed = new EmbedBuilder().WithTitle($"💬 Buy {relicName}").WithDescription($"```text\n{whisper}\n```")
                .WithColor(new Color(0x2ECC71)).AddField("Seller", $"`{seller}`", true).AddField("Price", $"{order.Price:0.##}p each", true)
                .AddField("Available", order.Quantity.HasValue ? $"×{order.Quantity:0.##}" : "Not reported", true);
            if (!string.IsNullOrWhiteSpace(order.SellerSlug)) embed.AddField("Warframe Market", $"[Open seller profile](https://warframe.market/profile/{Uri.EscapeDataString(order.SellerSlug)})");
            embed.WithFooter("Copy the /w command into Warframe yourself. RelicFrame never contacts the seller.");
            await interaction.FollowupAsync(embed: embed.Build(), ephemeral: true, allowedMentions: AllowedMentions.None);
        }
    }
    private static string Price(double? value) => value?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
    private static string Signed(double value) => value.ToString("+0.##;-0.##;0", System.Globalization.CultureInfo.InvariantCulture);
    private static string Percent(double? value) => value?.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%" ?? "unknown";
    private static string Quantity(double? value) => value.HasValue ? $" ×{value.Value:0.##}" : "";
    private static string Filter(double? value) => value.HasValue ? Price(value) + "p" : "Any";

    public static ApplicationCommandProperties BuildCommand()
    {
        var group = new SlashCommandBuilder().WithName("rf-panel").WithDescription("Persistent THE LIST relic rankings");
        group.AddOption(new SlashCommandOptionBuilder().WithName("setup").WithDescription("Create or repair THE LIST rankings and guide").WithType(ApplicationCommandOptionType.SubCommand));
        var config = new SlashCommandOptionBuilder().WithName("config").WithDescription("Change one list's saved filters").WithType(ApplicationCommandOptionType.SubCommand);
        var list = new SlashCommandOptionBuilder().WithName("list").WithDescription("List channel to configure").WithType(ApplicationCommandOptionType.String).WithRequired(true);
        foreach (var (key, title) in Lists) list.AddChoice(title, key); config.AddOption(list);
        config.AddOption(new SlashCommandOptionBuilder().WithName("refinement").WithDescription("Relic refinement").WithType(ApplicationCommandOptionType.String).AddChoice("Intact","Intact").AddChoice("Exceptional","Exceptional").AddChoice("Flawless","Flawless").AddChoice("Radiant","Radiant"));
        config.AddOption("max_cost", ApplicationCommandOptionType.Number, "Maximum cost; use -1 to clear", minValue:-1);
        config.AddOption("min_reward", ApplicationCommandOptionType.Number, "Minimum best reward; use -1 to clear", minValue:-1); group.AddOption(config);
        foreach (var name in new[] { "refresh", "start", "stop", "status", "help" }) group.AddOption(new SlashCommandOptionBuilder().WithName(name).WithDescription($"THE LIST: {name}").WithType(ApplicationCommandOptionType.SubCommand));
        return group.Build();
    }
    public async Task<string> CommandAsync(SocketSlashCommand command, CancellationToken ct, CancellationToken lifetime)
    {
        var sub = command.Data.Options.Single(); var name = sub.Name; string Get(string key) => sub.Options.FirstOrDefault(o => o.Name == key)?.Value?.ToString() ?? "";
        if (name == "help") return $"`/rf-panel setup` creates THE LIST with {Lists.Count} interactive rankings, #{PrimePricesKey}, and #{GuideKey}. Rankings default to Radiant, vaulted relics and online sellers. Select a relic for drops or /w; select a Prime part for its price and source relics. Use config for ranking filters. Boards update every five minutes; `/rf-panel stop` is the render kill switch.";
        if (name == "status") return $"THE LIST: {Status}; market: {market.Status}.";
        if (command.User is not SocketGuildUser user || !user.GuildPermissions.ManageGuild) return "Manage Server permission is required.";
        var guild = bot.GetGuild(targetGuild) ?? throw new InvalidOperationException("Configured server unavailable.");
        if (name == "setup") return await SetupAsync(guild, ct, lifetime);
        if (name == "stop") { await StopAsync(); return "THE LIST rendering stopped; messages and filters retained."; }
        if (name == "start") { await market.StartAsync(false, lifetime); Start(lifetime); return "Market and THE LIST workers started."; }
        if (name == "config")
        {
            var key = Get("list"); if (!Lists.ContainsKey(key)) return "Unknown list channel."; var saved = ListState(targetGuild, key);
            if (Get("refinement") is { Length: > 0 } refinement) saved.Refinement = refinement;
            if (double.TryParse(Get("max_cost"), out var maxCost)) saved.MaxCost = maxCost < 0 ? null : maxCost;
            if (double.TryParse(Get("min_reward"), out var minReward)) saved.MinReward = minReward < 0 ? null : minReward;
            Save(); await UpdateAsync(ct); return $"#{key} filters saved: {saved.Refinement}, max cost {Filter(saved.MaxCost)}, min reward {Filter(saved.MinReward)}.";
        }
        await UpdateAsync(ct); return $"All {Lists.Count} THE LIST rankings, Prime-part prices, and the guide refreshed from the current market snapshot.";
    }
    public async ValueTask DisposeAsync()
    {
        bot.ButtonExecuted -= DispatchButtonAsync; bot.SelectMenuExecuted -= DispatchSelectAsync;
        await StopAsync(); cancellation?.Dispose(); update.Dispose(); setup.Dispose();
    }
}
