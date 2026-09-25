using System.Security.Cryptography;
using System.Text;
using Discord;
using Discord.WebSocket;
using RelicFrame.Core;

internal sealed record PersonalMarketState
{
    public int ReconciliationVersion { get; set; }
    public ulong CategoryId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
    public int Page { get; set; } = 1;
    public string RenderHash { get; set; } = "";
    public bool? AutoPublishEnabled { get; set; }
    public bool SellCompleteSets { get; set; } = true;
    public bool ProtectIncompleteSets { get; set; } = true;
    public Dictionary<string, string> ManagedOrderIds { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> LastInventory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ManagedOrderQuantities { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ManagedOrderPrices { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> PendingSoldQuantities { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, MissingManagedOrderHold> MissingManagedOrderHolds { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ObservedInventoryDecrease> RecentInventoryDecreases { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ObservedInventoryDecrease> RecentOrderReductions { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> UnmatchedAlecaSaleQuantities { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ObservedInventoryDecrease> RecentGameSales { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ObservedInventoryDecrease> RecentAlecaSales { get; set; } = new(StringComparer.Ordinal);
    public string[] ProcessedGameTradeIds { get; set; } = [];
    public DateTimeOffset? AlecaTradeWatermark { get; set; }
    public string[] ProcessedAlecaTradeIds { get; set; } = [];
    public PersonalMarketSale[] SaleHistory { get; set; } = [];
    public string[] LastActions { get; set; } = [];
    public DateTimeOffset? LastAccountSync { get; set; }
    public DateOnly? PortfolioBaselineDate { get; set; }
    public double? PortfolioBaselineAskValue { get; set; }
    public int PortfolioBaselinePricedItems { get; set; }
}
internal sealed record PersonalMarketSale(string ItemId, string ItemName, string OrderId, int Quantity, int PlatinumEach, DateTimeOffset DetectedAt);
internal sealed record PersonalMarketSettings
{
    public ulong DiscordUserId { get; set; }
    public string WfmUserSlug { get; set; } = "";
    public int MinimumPlat { get; set; } = 10;
    public int Undercut { get; set; } = 1;
    public string InventoryJson { get; set; } = "";
    public bool AutoPublish { get; set; }
}

internal sealed class PersonalMarketManager : IAsyncDisposable
{
    private const int PageSize = 12;
    private sealed record BoardSnapshot(PersonalMarketQuote[] Quotes, IReadOnlySet<string> ManagedItemIds,
        string EmptyDescription, string DescriptionTail,
        DateTimeOffset? LastAccountSync, bool AutoPublishEnabled, bool SellCompleteSets, bool ProtectIncompleteSets);
    private readonly DiscordSocketClient bot;
    private readonly LiveMarket market;
    private readonly MarketHttp http;
    private readonly ulong targetGuild;
    private readonly ulong configuredOwner;
    private readonly string inventoryPath;
    private readonly string alecaPublicTokenPath;
    private readonly string statePath;
    private readonly int minimumPrice;
    private readonly int undercut;
    private readonly string ownSellerSlug;
    private readonly WfmAccountClient account;
    private readonly PrimeSetCompletion setCompletion;
    private IReadOnlyList<PrimeSetDefinition> setDefinitions = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim renderGate = new(1, 1);
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly FileSystemWatcher? inventoryWatcher;
    private PersonalMarketState state;
    private BoardSnapshot? boardSnapshot;
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private bool reconciliationPending;
    private string status = "stopped";
    private string alecaTradeStatus = "not checked";
    public string Status => Volatile.Read(ref status);
    public string ExcludedSellerSlug => ownSellerSlug;
    public ulong OwnerId => configuredOwner;

    public PersonalMarketManager(DiscordSocketClient bot, LiveMarket market, MarketHttp http, ulong targetGuild, string runtime)
    {
        this.bot = bot; this.market = market; this.http = http; this.targetGuild = targetGuild;
        var settingsPath = Path.Combine(runtime, "personal_market_settings.json");
        PersonalMarketSettings settings;
        try
        {
            if (!File.Exists(settingsPath)) Json.WriteAtomic(settingsPath, new PersonalMarketSettings());
            settings = Json.Read<PersonalMarketSettings>(settingsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { settings = new(); status = "settings unreadable; environment/defaults used"; }
        configuredOwner = ulong.TryParse(Environment.GetEnvironmentVariable("RELICFRAME_PERSONAL_USER_ID"), out var owner) ? owner : settings.DiscordUserId;
        var configuredInventory = Environment.GetEnvironmentVariable("RELICFRAME_PRIME_INVENTORY_JSON");
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var alecaInventory = OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(localData)
            ? Path.Combine(localData, "AlecaFrame", "lastData.dat") : "";
        inventoryPath = Path.GetFullPath(!string.IsNullOrWhiteSpace(configuredInventory) ? configuredInventory : !string.IsNullOrWhiteSpace(settings.InventoryJson) ? settings.InventoryJson : File.Exists(alecaInventory) ? alecaInventory : Path.Combine(runtime, "prime_inventory.json"));
        var configuredAlecaTokenPath = Environment.GetEnvironmentVariable("RELICFRAME_ALECA_PUBLIC_TOKEN_FILE");
        alecaPublicTokenPath = Path.GetFullPath(!string.IsNullOrWhiteSpace(configuredAlecaTokenPath)
            ? configuredAlecaTokenPath : Path.Combine(runtime, "aleca-public-token.txt"));
        statePath = Path.Combine(runtime, "personal_market.json");
        minimumPrice = int.TryParse(Environment.GetEnvironmentVariable("RELICFRAME_PERSONAL_MIN_PLAT"), out var min) ? Math.Clamp(min, 1, 900000) : Math.Clamp(settings.MinimumPlat, 1, 900000);
        undercut = int.TryParse(Environment.GetEnvironmentVariable("RELICFRAME_PERSONAL_UNDERCUT"), out var cut) ? Math.Clamp(cut, 1, 3) : Math.Clamp(settings.Undercut, 1, 3);
        ownSellerSlug = Environment.GetEnvironmentVariable("RELICFRAME_WFM_USER_SLUG")?.Trim() ?? settings.WfmUserSlug.Trim();
        var configuredTokenPath = Environment.GetEnvironmentVariable("RELICFRAME_WFM_TOKEN_FILE");
        var tokenPath = !string.IsNullOrWhiteSpace(configuredTokenPath) ? Path.GetFullPath(configuredTokenPath)
            : OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(localData) ? Path.Combine(localData, "AlecaFrame", "WFMarketToken.tk")
            : Path.Combine(runtime, "wfm-token.txt");
        account = new(http, tokenPath);
        setCompletion = new(market, http, Path.Combine(runtime, "personal_market_set_components.json"));
        try { state = File.Exists(statePath) ? Json.Read<PersonalMarketState>(statePath) : new(); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { state = new(); status = "saved state unreadable; setup required"; }
        state.ManagedOrderIds ??= new(StringComparer.Ordinal); state.LastInventory ??= new(StringComparer.OrdinalIgnoreCase);
        state.ManagedOrderQuantities ??= new(StringComparer.Ordinal); state.ManagedOrderPrices ??= new(StringComparer.Ordinal);
        state.PendingSoldQuantities ??= new(StringComparer.Ordinal);
        state.MissingManagedOrderHolds ??= new(StringComparer.Ordinal);
        state.RecentInventoryDecreases ??= new(StringComparer.Ordinal);
        state.RecentOrderReductions ??= new(StringComparer.Ordinal);
        state.UnmatchedAlecaSaleQuantities ??= new(StringComparer.Ordinal);
        state.RecentGameSales ??= new(StringComparer.Ordinal);
        state.RecentAlecaSales ??= new(StringComparer.Ordinal);
        state.ProcessedGameTradeIds ??= [];
        state.ProcessedAlecaTradeIds ??= [];
        state.SaleHistory ??= []; state.LastActions ??= [];
        if (state.ReconciliationVersion < 2)
        {
            // Version 1 treated a disappeared order as a completed sale. Preserve
            // that state for audit, then remove only the deductions/history created
            // by that unsafe inference so current AlecaFrame inventory can relist.
            if (File.Exists(statePath) && (state.PendingSoldQuantities.Count > 0 || state.SaleHistory.Length > 0))
                File.Copy(statePath, statePath + ".pre-safe-reconcile.bak", true);
            state.PendingSoldQuantities.Clear(); state.SaleHistory = [];
            state.LastActions = ["• Repaired legacy missing-order deductions; current AlecaFrame inventory is eligible again."];
            state.ReconciliationVersion = 2;
            Save();
        }
        state.AutoPublishEnabled ??= bool.TryParse(Environment.GetEnvironmentVariable("RELICFRAME_PERSONAL_AUTO_PUBLISH"), out var autoPublish) ? autoPublish : settings.AutoPublish;
        var inventoryDirectory = Path.GetDirectoryName(inventoryPath);
        if (!string.IsNullOrWhiteSpace(inventoryDirectory) && Directory.Exists(inventoryDirectory))
        {
            inventoryWatcher = new FileSystemWatcher(inventoryDirectory, Path.GetFileName(inventoryPath))
            { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            inventoryWatcher.Changed += (_, _) => Wake();
            inventoryWatcher.Created += (_, _) => Wake();
            inventoryWatcher.Renamed += (_, _) => Wake();
            inventoryWatcher.EnableRaisingEvents = true;
        }
        bot.ButtonExecuted += DispatchButtonAsync;
    }

    public async Task<string> SetupAsync(SocketGuild guild, CancellationToken ct, CancellationToken lifetime)
    {
        if (guild.Id != targetGuild) return "Personal Market is restricted to its configured server.";
        var ownerId = configuredOwner == 0 ? guild.OwnerId : configuredOwner;
        IGuildUser? owner = (IGuildUser?)guild.GetUser(ownerId) ?? await bot.Rest.GetGuildUserAsync(guild.Id, ownerId);
        if (owner is null) return $"Discord could not find personal-market user `{ownerId}` in this server. Confirm Developer Mode > Copy User ID was used for the intended account.";
        var current = bot.CurrentUser is null ? null : guild.GetUser(bot.CurrentUser.Id);
        if (current is null || !current.GuildPermissions.ManageChannels) return "I need Manage Channels to create the private Personal Market board.";
        await gate.WaitAsync(ct);
        try
        {
            ICategoryChannel? category = guild.GetCategoryChannel(state.CategoryId) ?? guild.CategoryChannels.FirstOrDefault(c => c.Name == "PERSONAL MARKET");
            category ??= await guild.CreateCategoryChannelAsync("PERSONAL MARKET"); state.CategoryId = category.Id;
            await category.AddPermissionOverwriteAsync(guild.EveryoneRole, new OverwritePermissions(viewChannel: PermValue.Deny));
            await category.AddPermissionOverwriteAsync(owner, new OverwritePermissions(viewChannel: PermValue.Allow, readMessageHistory: PermValue.Allow, sendMessages: PermValue.Allow));
            await category.AddPermissionOverwriteAsync(current, new OverwritePermissions(viewChannel: PermValue.Allow, readMessageHistory: PermValue.Allow, sendMessages: PermValue.Allow, manageMessages: PermValue.Allow));
            ITextChannel? channel = guild.GetTextChannel(state.ChannelId) ?? guild.TextChannels.FirstOrDefault(c => c.CategoryId == category.Id && c.Name == "personal-market");
            channel ??= await guild.CreateTextChannelAsync("personal-market", properties =>
            {
                properties.CategoryId = category.Id;
                properties.Topic = "Private Prime inventory and owner-controlled automatic Warframe.market sell listings";
            });
            if (channel.Topic != "Private Prime inventory and owner-controlled automatic Warframe.market sell listings")
                await channel.ModifyAsync(properties => properties.Topic = "Private Prime inventory and owner-controlled automatic Warframe.market sell listings");
            await channel.AddPermissionOverwriteAsync(guild.EveryoneRole, new OverwritePermissions(viewChannel: PermValue.Deny));
            await channel.AddPermissionOverwriteAsync(owner, new OverwritePermissions(viewChannel: PermValue.Allow, readMessageHistory: PermValue.Allow, sendMessages: PermValue.Allow));
            await channel.AddPermissionOverwriteAsync(current, new OverwritePermissions(viewChannel: PermValue.Allow, readMessageHistory: PermValue.Allow, sendMessages: PermValue.Allow, manageMessages: PermValue.Allow));
            state.ChannelId = channel.Id;
            if (!File.Exists(inventoryPath) && Path.GetExtension(inventoryPath).Equals(".json", StringComparison.OrdinalIgnoreCase)) Json.WriteAtomic(inventoryPath, Array.Empty<object>());
            if (!File.Exists(inventoryPath)) return $"Personal Market channel was configured, but inventory source `{inventoryPath}` does not exist.";
            Save(); Start(lifetime);
            try
            {
                await UpdateLockedAsync(channel, force: true, ct);
                return $"Configured owner-only **PERSONAL MARKET / #personal-market**; automatic listing is {(state.AutoPublishEnabled == true ? "enabled" : "paused")}.";
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is not OutOfMemoryException)
            {
                status = $"initial refresh failed ({e.GetType().Name}); automatic retry scheduled";
                Console.WriteLine($"[personal-market] initial refresh failed; channel retained\n{e}");
                return $"Configured owner-only **PERSONAL MARKET / #personal-market**; initial inventory refresh failed ({e.GetType().Name}) and will retry without blocking other bot setup.";
            }
        }
        finally { gate.Release(); }
    }

    public void Start(CancellationToken lifetime)
    {
        if (worker is { IsCompleted: false }) return;
        cancellation?.Dispose(); cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        worker = Task.Run(() => LoopAsync(cancellation.Token), CancellationToken.None); status = "starting";
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await UpdateAsync(force: false, ct); }
                catch (Exception e) when (!ct.IsCancellationRequested && e is not OutOfMemoryException)
                { status = $"refresh failed ({e.GetType().Name}); previous board retained"; Console.WriteLine($"[personal-market] {e}"); }
                var delay = market.ReadyBooks == 0 || market.IsBootstrapping || reconciliationPending || File.Exists(alecaPublicTokenPath)
                    ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5);
                await wake.WaitAsync(delay, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void Wake()
    {
        try { if (wake.CurrentCount == 0) wake.Release(); }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException) { }
    }

    public async Task UpdateAsync(bool force, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var guild = bot.GetGuild(targetGuild) ?? throw new InvalidOperationException("Configured server unavailable.");
            var channel = guild.GetTextChannel(state.ChannelId) ?? throw new InvalidOperationException("Personal Market channel is missing; restart the bot to repair it.");
            await UpdateLockedAsync(channel, force, ct);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<PrimeInventoryEntry>> InventoryAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var raw = PrimeInventory.Load(inventoryPath);
            return AvailableInventory(raw);
        }
        finally { gate.Release(); }
    }

    private async Task UpdateLockedAsync(ITextChannel channel, bool force, CancellationToken ct)
    {
        var rawInventory = PrimeInventory.Load(inventoryPath);
        var candidates = rawInventory.Select(item => new
        {
            Item = item,
            Name = !string.IsNullOrWhiteSpace(item.ItemName) ? item.ItemName : market.NameForGameRef(item.GameRef)
        }).Where(row => row.Name is not null && (row.Name.Contains(" Prime ", StringComparison.OrdinalIgnoreCase) || row.Name.EndsWith(" Prime", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (market.ReadyBooks > 0 && !market.IsBootstrapping)
        {
            status = $"checking {candidates.Length} owned-item prices";
            await market.RefreshBooksAsync(candidates.Select(row => row.Name!), force ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5), ct, lowPriority: !force);
        }
        var sourceActions = new List<string>();
        if (market.ReadyBooks > 0 && !market.IsBootstrapping)
        {
            var definitions = new List<PrimeSetDefinition>();
            foreach (var setName in market.PrimeSetNames)
            {
                var family = setName.EndsWith(" Set", StringComparison.OrdinalIgnoreCase) ? setName[..^4] : setName;
                if (!candidates.Any(row => row.Name!.StartsWith(family + " ", StringComparison.OrdinalIgnoreCase))) continue;
                definitions.Add(await setCompletion.DefinitionAsync(setName, ct)
                    ?? throw new InvalidDataException($"Set metadata unavailable for {setName}; account writes paused."));
            }
            setDefinitions = definitions;
        }
        IReadOnlyList<WfmOwnOrder>? orders = null;
        var marketUsable = market.ReadyBooks > 0 && !market.IsBootstrapping;
        if (marketUsable && (state.AutoPublishEnabled == true || state.ManagedOrderIds.Count > 0))
        {
            orders = await account.OrdersAsync(ct);
            sourceActions.AddRange(ObserveManagedOrderChanges(orders, rawInventory));
        }
        var ownOrderIds = orders?.Select(order => order.Id).ToHashSet(StringComparer.Ordinal)
            ?? state.ManagedOrderIds.Values.ToHashSet(StringComparer.Ordinal);
        // Record vanished/reduced managed orders before reading completed trades,
        // so a sale reported in this same cycle can resolve their provisional hold.
        sourceActions.AddRange(await ObserveAlecaTradesAsync(rawInventory, ct));
        // Observe the market first. If AlecaFrame refreshed in the same interval, its
        // matching raw decrease then clears the newly-created temporary deduction.
        sourceActions.AddRange(ReconcileAlecaSnapshot(rawInventory));
        var inventory = AvailableInventory(rawInventory);
        var portfolioPositions = inventory.Select(item => new
        {
            Item = item,
            Name = !string.IsNullOrWhiteSpace(item.ItemName) ? item.ItemName : market.NameForGameRef(item.GameRef)
        }).Where(row => row.Name is not null &&
            (row.Name.Contains(" Prime ", StringComparison.OrdinalIgnoreCase) || row.Name.EndsWith(" Prime", StringComparison.OrdinalIgnoreCase)))
          .Select(row => new PortfolioPosition(row.Name!, row.Item.Quantity,
              market.RewardEstimate(row.Name!, ownSellerSlug, ownOrderIds).Price,
              market.IsBookFresh(row.Name!, TimeSpan.FromMinutes(10)))).ToArray();
        var portfolio = PortfolioValuation.Calculate(portfolioPositions);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (state.PortfolioBaselineDate != today && portfolio.PricedItems > 0)
        {
            state.PortfolioBaselineDate = today;
            state.PortfolioBaselineAskValue = portfolio.EstimatedAskValue;
            state.PortfolioBaselinePricedItems = portfolio.PricedItems;
        }
        var listingPlan = new PrimeSetListingPlan([], new Dictionary<string, int>(StringComparer.Ordinal));
        if (marketUsable)
        {
            // Consider every represented set rather than only the profit top 10.
            // PrimeSetListingPlan narrows this collection to sets exactly one required
            // component type away, after complete sets have already been allocated.
            var protectedSets = state.ProtectIncompleteSets
                ? setDefinitions.Select(row => row.SetName).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stock = inventory.Select(row => new
            {
                Item = row, ItemId = ItemIdForInventory(row),
                Name = !string.IsNullOrWhiteSpace(row.ItemName) ? row.ItemName : market.NameForGameRef(row.GameRef)
            }).Where(row => row.ItemId is not null && row.Name is not null)
              .Select(row => new PrimeSetStock(row.ItemId!, row.Name!, row.Item.Quantity));
            listingPlan = PrimeSetListingPlan.Build(stock, setDefinitions, protectedSets,
                state.SellCompleteSets, state.ProtectIncompleteSets);
            if (listingPlan.Sellable.Any(row => setDefinitions.Any(set => set.SetItemId == row.ItemId)))
                await market.RefreshBooksAsync(listingPlan.Sellable.Where(row => setDefinitions.Any(set => set.SetItemId == row.ItemId))
                    .Select(row => row.ItemName), force ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5), ct, lowPriority: !force);
        }
        var sellableInventory = marketUsable
            ? listingPlan.Sellable.Select(row => new PrimeInventoryEntry(row.ItemId, row.Quantity, row.ItemName)).ToArray()
            : inventory.ToArray();
        candidates = sellableInventory.Select(item => new
        {
            Item = item,
            Name = !string.IsNullOrWhiteSpace(item.ItemName) ? item.ItemName : market.NameForGameRef(item.GameRef)
        }).Where(row => row.Name is not null && (row.Name.Contains(" Prime ", StringComparison.OrdinalIgnoreCase) || row.Name.EndsWith(" Prime", StringComparison.OrdinalIgnoreCase))).ToArray();
        var assessed = candidates.Select(row =>
        {
            var bookFresh = market.IsBookFresh(row.Name!, TimeSpan.FromMinutes(10));
            var estimate = bookFresh ? market.RewardEstimate(row.Name!, ownSellerSlug, ownOrderIds) : new RewardPriceEstimate(null, null, null, 0, false);
            var visible = market.Match(row.Name!, null, false, excludedSellerSlug: ownSellerSlug, excludedOrderIds: ownOrderIds).Entries;
            // Undercut the actionable in-game WTS competitor shown by the market UI,
            // not a cheaper merely-online/offline order or a blended valuation.
            var listingReference = bookFresh ? market.PersonalMarketListingReference(row.Name!, ownSellerSlug, ownOrderIds) : null;
            var reference = listingReference.HasValue ? visible.MinBy(entry => Math.Abs(entry.Price - listingReference.Value)) : null;
            var best = listingReference.HasValue ? new OrderEntry(listingReference.Value, reference?.Quantity,
                Seller: reference?.Seller ?? "market reference") : null;
            return new { row.Item, Name = row.Name!, Best = best, Quote = PrimeInventory.Quote(row.Item, row.Name, listingReference, best?.Seller ?? "", minimumPrice, undercut), BookFresh = bookFresh };
        }).ToArray();
        var quotes = assessed.Select(row => row.Quote).OfType<PersonalMarketQuote>().OrderByDescending(row => row.DraftPrice).ThenBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase).ToArray();
        if (state.AutoPublishEnabled == true && marketUsable && orders is not null)
        {
            var freshIneligible = assessed.Where(row => row.Quote is null && row.BookFresh)
                .Select(row => market.ItemIdForName(row.Name)).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var actions = await ReconcileAccountAsync(sellableInventory, quotes, freshIneligible, orders, sourceActions, ct,
                listingPlan.ControlledQuantities);
            if (actions.Length > 0) state.LastActions = actions;
        }
        else if (sourceActions.Count > 0) state.LastActions = sourceActions.Take(8).ToArray();
        var sourceAge = File.GetLastWriteTimeUtc(inventoryPath);
        var emptyDescription = $"No mapped Prime inventory items currently have a non-outlier online ask of at least **{minimumPrice}p**. If this file is still empty, import your inventory JSON at `{inventoryPath}`.";
        var description = "";
        var mode = state.AutoPublishEnabled == true ? "AUTO LISTING ENABLED" : "AUTO LISTING PAUSED";
        description += $"\n\n**{mode}.** Minimum {minimumPrice}p · undercut {undercut}p · stabilized online/recent-visible price · owner listing excluded: {(ownOrderIds.Count > 0 ? "authenticated order IDs" : ownSellerSlug.Length > 0 ? "seller slug" : "no; waiting for authenticated orders")} · inventory file <t:{new DateTimeOffset(sourceAge).ToUnixTimeSeconds()}:R>.";
        description += $"\n**Complete sets:** {(state.SellCompleteSets ? "sell sets and only surplus parts" : "sell parts separately")} · **sets one component type away:** {(state.ProtectIncompleteSets ? "reserve one completion's owned parts; sell only extras" : "sell their parts")}.";
        var belowFloor = assessed.Where(row => row.Quote is null && row.Best is not null && row.Best.Price < minimumPrice)
            .OrderByDescending(row => row.Best!.Price).ThenBy(row => row.Item.ItemName, StringComparer.OrdinalIgnoreCase).ToArray();
        var noAsk = assessed.Count(row => row.Quote is null && row.Best is null);
        var belowExamples = string.Join(", ", belowFloor.Take(3).Select(row => $"{row.Name} {row.Best!.Price:0.#}p"));
        var pricedCandidates = assessed.Count(row => row.Best is not null);
        description += $"\n**Independent listing rules:** every mapped owned Prime part, both **Vaulted and Unvaulted**; THE LIST display filters are not used. Shared relic-scanner price coverage: {pricedCandidates}/{candidates.Length}. Skipped below {minimumPrice}p: {belowFloor.Length}{(belowExamples.Length > 0 ? $" ({belowExamples})" : "")} · no usable in-game ask: {noAsk}.";
        if (portfolio.TotalItems > 0)
        {
            description += $"\n\n**Prime portfolio** · estimated ask value **{portfolio.EstimatedAskValue:0.#}p** for {portfolio.PricedUnits}/{portfolio.TotalUnits} units ({portfolio.PricedItems}/{portfolio.TotalItems} item types with fresh prices). Unpriced stock is excluded; this is not realized profit or guaranteed sale value.";
            if (state.PortfolioBaselineDate == today && state.PortfolioBaselineAskValue is { } baseline && state.PortfolioBaselinePricedItems == portfolio.PricedItems)
                description += $" Today vs first priced snapshot: **{portfolio.EstimatedAskValue - baseline:+0.#;-0.#;0}p** (includes inventory changes and price moves).";
        }
        if (state.AutoPublishEnabled == true && !marketUsable) description += $"\nAccount writes wait for usable market books; current market: **{market.Status}**.";
        if (state.LastActions.Length > 0) description += "\n\n**Latest account activity**\n" + string.Join('\n', state.LastActions.Take(6));
        if (state.SaleHistory.Length > 0)
        {
            var confirmedSales = state.SaleHistory.Where(sale => sale.OrderId.StartsWith("alecaframe:", StringComparison.Ordinal) && sale.PlatinumEach > 0).ToArray();
            var gross = confirmedSales.Sum(sale => (long)sale.Quantity * sale.PlatinumEach);
            description += $"\n\n**Sale ledger** · {gross}p completed-trade gross ({confirmedSales.Length} priced trades); {state.SaleHistory.Length - confirmedSales.Length} unpriced or provisional entries. Purchase cost is unknown, so net profit cannot be calculated.\n" + string.Join('\n', state.SaleHistory.Take(3).Select(sale =>
                $"• **{sale.ItemName}** ×{sale.Quantity}{(sale.PlatinumEach > 0 ? $" at {sale.PlatinumEach}p each" : "")} · {(sale.OrderId.StartsWith("alecaframe:", StringComparison.Ordinal) ? "completed trade" : "order change; provisional")} · <t:{sale.DetectedAt.ToUnixTimeSeconds()}:R>"));
        }
        var pendingSold = state.PendingSoldQuantities.Values.Sum(value => Math.Max(0, value));
        var heldOrders = state.MissingManagedOrderHolds.Values.Sum(hold => Math.Max(0, hold.Quantity));
        var alecaTradeMode = File.Exists(alecaPublicTokenPath) ? alecaTradeStatus
            : $"not connected; add a trades-only public token at `{alecaPublicTokenPath}`";
        description += $"\nAlecaFrame {alecaTradeMode}. Completed sales and managed Warframe.market quantity reductions remove stale stock immediately. Pending stale-cache deduction: **{pendingSold}** item(s) · missing-order safety hold: **{heldOrders}** unit(s). A vanished order is not counted as a sale or automatically relisted from stale inventory.";
        var snapshot = new BoardSnapshot(quotes, state.ManagedOrderIds.Keys.ToHashSet(StringComparer.Ordinal),
            emptyDescription, description, state.LastAccountSync,
            state.AutoPublishEnabled == true, state.SellCompleteSets, state.ProtectIncompleteSets);
        Volatile.Write(ref boardSnapshot, snapshot);
        await RenderBoardAsync(channel, snapshot, force, ct);
        status = $"ready; {quotes.Length} eligible; auto listing {(state.AutoPublishEnabled == true ? "enabled" : "paused")}";
    }

    private (Embed Embed, MessageComponent Components, string Hash) BuildBoard(BoardSnapshot snapshot, int page)
    {
        var pages = Math.Max(1, (snapshot.Quotes.Length + PageSize - 1) / PageSize);
        var rows = snapshot.Quotes.Skip((page - 1) * PageSize).Take(PageSize)
            .Select((row, index) =>
            {
                var itemId = market.ItemIdForName(row.ItemName);
                var managed = itemId is not null && snapshot.ManagedItemIds.Contains(itemId);
                return $"**{(page - 1) * PageSize + index + 1}. {row.ItemName}** ×{row.Quantity} · lowest {row.LowestAsk}p · {(managed ? "listed" : "target")} **{row.DraftPrice}p**{(row.LowestSeller.Length > 0 ? $" · `{row.LowestSeller}`" : "")}";
            });
        var description = (snapshot.Quotes.Length == 0 ? snapshot.EmptyDescription : string.Join('\n', rows)) + snapshot.DescriptionTail;
        var embed = new EmbedBuilder().WithTitle("Personal Prime Market · automatic listings")
            .WithDescription(description.Length <= 4096 ? description : description[..4093] + "…")
            .WithColor(new Color(snapshot.AutoPublishEnabled ? 0x2ECC71u : 0xF39C12u))
            .WithFooter($"Page {page}/{pages} · {snapshot.Quotes.Length} eligible items · authenticated sync {(snapshot.LastAccountSync.HasValue ? snapshot.LastAccountSync.Value.ToString("u") : "never")}").Build();
        var components = new ComponentBuilder()
            .WithButton("Previous", "personal-market:prev", ButtonStyle.Secondary, disabled: page <= 1)
            .WithButton("Sync listings now", "personal-market:refresh", ButtonStyle.Primary)
            .WithButton("Next", "personal-market:next", ButtonStyle.Secondary, disabled: page >= pages)
            .WithButton(snapshot.AutoPublishEnabled ? "Pause auto listing" : "Enable auto listing", "personal-market:auto-toggle", snapshot.AutoPublishEnabled ? ButtonStyle.Danger : ButtonStyle.Success, row: 1)
            .WithButton(snapshot.SellCompleteSets ? "Sell sets: ON" : "Sell sets: OFF", "personal-market:sets-toggle", ButtonStyle.Secondary, row: 1)
            .WithButton(snapshot.ProtectIncompleteSets ? "Protect completions: ON" : "Protect completions: OFF", "personal-market:completion-toggle", ButtonStyle.Secondary, row: 1).Build();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(embed.Description + "\0" + embed.Footer?.Text + "\0" + page)));
        return (embed, components, hash);
    }

    private async Task RenderBoardAsync(ITextChannel channel, BoardSnapshot snapshot, bool force, CancellationToken ct)
    {
        await renderGate.WaitAsync(ct);
        try
        {
            var pages = Math.Max(1, (snapshot.Quotes.Length + PageSize - 1) / PageSize);
            state.Page = Math.Clamp(state.Page, 1, pages);
            var board = BuildBoard(snapshot, state.Page);
            if (!force && state.MessageId != 0 && state.RenderHash == board.Hash) { Save(); return; }
            var old = state.MessageId == 0 ? null : await channel.GetMessageAsync(state.MessageId) as IUserMessage;
            if (old is null) { var sent = await channel.SendMessageAsync(embed: board.Embed, components: board.Components, allowedMentions: AllowedMentions.None); state.MessageId = sent.Id; }
            else await old.ModifyAsync(properties => { properties.Embed = board.Embed; properties.Components = board.Components; properties.AllowedMentions = AllowedMentions.None; });
            state.RenderHash = board.Hash; Save();
        }
        finally { renderGate.Release(); }
    }

    private async Task<string[]> ReconcileAccountAsync(IReadOnlyList<PrimeInventoryEntry> inventory, IReadOnlyList<PersonalMarketQuote> quotes,
        IReadOnlySet<string> freshIneligible, IReadOnlyList<WfmOwnOrder> orders, IEnumerable<string> observedActions, CancellationToken ct,
        IReadOnlyDictionary<string, int> controlledQuantities)
    {
        var sellOrders = orders.Where(order => order.Type.Equals("sell", StringComparison.OrdinalIgnoreCase)).GroupBy(order => order.ItemId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(order => order.Visible).ThenBy(order => order.Id, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        WfmOwnOrder? ExistingOrder(string itemId)
        {
            if (!sellOrders.TryGetValue(itemId, out var matches)) return null;
            var managedId = state.ManagedOrderIds.GetValueOrDefault(itemId);
            return managedId is null ? matches[0] : matches.FirstOrDefault(order => order.Id == managedId) ?? matches[0];
        }
        var owned = inventory.Select(ItemIdForInventory).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var eligible = quotes.Select(quote => market.ItemIdForName(quote.ItemName)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var actions = new List<string>(observedActions); var writes = 0; reconciliationPending = false;
        // Reduce conflicting orders before any set or surplus-part listing is published.
        // A set and its parts must never be listed against the same physical units.
        foreach (var controlled in controlledQuantities)
        {
            if (!sellOrders.TryGetValue(controlled.Key, out var existing)) continue;
            if (controlled.Value > 0 && existing.Length == 1 && existing[0].Quantity <= controlled.Value) continue;
            foreach (var order in existing)
            {
                if (writes >= 25) { reconciliationPending = true; Save(); return ["• Set/part order reductions pending; new listings wait for the next pass."]; }
                await account.DeleteOrderAsync(order.Id, ct); writes++;
                state.ManagedOrderIds.Remove(controlled.Key); state.ManagedOrderQuantities.Remove(controlled.Key); state.ManagedOrderPrices.Remove(controlled.Key);
                Save();
            }
            sellOrders.Remove(controlled.Key);
            actions.Add($"• Removed conflicting **{market.NameForItemId(controlled.Key)}** order before updating reserved set/part quantities.");
        }
        foreach (var managed in state.ManagedOrderIds.ToArray())
        {
            var shouldDelete = PrimeInventory.ShouldDeleteManagedListing(owned.Contains(managed.Key), eligible.Contains(managed.Key), freshIneligible.Contains(managed.Key));
            var existing = ExistingOrder(managed.Key);
            if (!shouldDelete || existing is null) continue;
            if (writes >= 25) { actions.Add("• Write limit reached; remaining removals continue in about 30 seconds."); reconciliationPending = true; break; }
            await account.DeleteOrderAsync(existing.Id, ct);
            state.ManagedOrderIds.Remove(managed.Key); state.ManagedOrderQuantities.Remove(managed.Key); state.ManagedOrderPrices.Remove(managed.Key);
            var name = market.NameForItemId(managed.Key) ?? "Prime item";
            var reason = owned.Contains(managed.Key) ? "no longer meets the current price/availability rules" : "is no longer owned";
            actions.Add($"• Deleted **{name}** because it {reason}."); writes++; Save();
            sellOrders.Remove(managed.Key);
        }
        foreach (var quote in quotes)
        {
            var itemId = market.ItemIdForName(quote.ItemName); if (itemId is null) continue;
            var existing = ExistingOrder(itemId);
            if (existing is null)
            {
                if (writes >= 25) { actions.Add("• Write limit reached; remaining items continue in about 30 seconds."); reconciliationPending = true; break; }
                var created = await account.CreateSellAsync(itemId, quote.DraftPrice, quote.Quantity, ct);
                if (created.Id.Length == 0) throw new InvalidDataException($"Warframe.market returned no order ID for {quote.ItemName}.");
                state.ManagedOrderIds[itemId] = created.Id; state.ManagedOrderQuantities[itemId] = created.Quantity; state.ManagedOrderPrices[itemId] = created.Platinum;
                actions.Add($"• Listed **{quote.ItemName}** ×{quote.Quantity} at **{quote.DraftPrice}p**."); writes++; Save();
            }
            else
            {
                state.ManagedOrderIds[itemId] = existing.Id;
                var accountChanged = false;
                if (existing.Platinum != quote.DraftPrice || existing.Quantity != quote.Quantity || !existing.Visible)
                {
                    if (writes >= 25) { actions.Add("• Write limit reached; remaining items continue in about 30 seconds."); reconciliationPending = true; break; }
                    var updated = await account.UpdateSellAsync(existing.Id, quote.DraftPrice, quote.Quantity, true, ct);
                    actions.Add($"• Updated **{quote.ItemName}** ×{quote.Quantity} to **{quote.DraftPrice}p**."); writes++; accountChanged = true;
                    state.ManagedOrderQuantities[itemId] = updated.Quantity > 0 ? updated.Quantity : quote.Quantity;
                    state.ManagedOrderPrices[itemId] = updated.Platinum > 0 ? updated.Platinum : quote.DraftPrice;
                }
                if (!accountChanged) { state.ManagedOrderQuantities[itemId] = existing.Quantity; state.ManagedOrderPrices[itemId] = existing.Platinum; }
                if (accountChanged) Save();
            }
        }
        state.LastAccountSync = DateTimeOffset.UtcNow;
        if (actions.Count == 0) actions.Add("• Account already matched the current inventory and target prices.");
        Save(); return actions.Take(8).ToArray();
    }

    private string? ItemIdForInventory(PrimeInventoryEntry row)
    {
        if (setDefinitions.Any(set => set.SetItemId == row.GameRef)) return row.GameRef;
        var name = !string.IsNullOrWhiteSpace(row.ItemName) ? row.ItemName : market.NameForGameRef(row.GameRef);
        return string.IsNullOrWhiteSpace(name) ? null : market.ItemIdForName(name);
    }

    private IReadOnlyList<PrimeInventoryEntry> AvailableInventory(IReadOnlyList<PrimeInventoryEntry> raw)
    {
        var suppressed = TradeInventoryCache.SuppressedStock(
            state.PendingSoldQuantities, state.MissingManagedOrderHolds, setDefinitions);
        return PrimeInventory.SubtractPendingSales(raw, suppressed, ItemIdForInventory);
    }

    public async Task RecordGameTradeAsync(EeLogCompletedTrade trade, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (state.ProcessedGameTradeIds.Contains(trade.Id, StringComparer.Ordinal)) return;
            var raw = PrimeInventory.Load(inventoryPath);
            var owned = raw.Select(ItemIdForInventory).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var actions = new List<string>();
            var soldItems = new List<(string ItemId, int Quantity)>();
            foreach (var given in trade.Given)
            {
                var itemId = market.ItemIdForName(given.Name);
                if (itemId is null || (!owned.Contains(itemId) && !state.ManagedOrderIds.ContainsKey(itemId))) continue;
                var alreadyFromAleca = TradeInventoryCache.ConsumeMatchingTradeCredit(
                    state.RecentAlecaSales, itemId, given.Quantity, trade.ObservedAt);
                var remaining = given.Quantity - alreadyFromAleca;
                if (remaining <= 0) continue;
                TradeInventoryCache.ConsumeMissingOrderHold(state.MissingManagedOrderHolds, itemId, remaining);
                var stillInCache = TradeInventoryCache.QuantityStillInCache(itemId, remaining,
                    trade.ObservedAt, state.RecentInventoryDecreases, setDefinitions);
                var unmatchedOrder = TradeInventoryCache.QuantityStillInCache(itemId, remaining,
                    trade.ObservedAt, state.RecentOrderReductions, []);
                var deduction = TradeInventoryCache.AdditionalConfirmedSaleDeduction(stillInCache, unmatchedOrder);
                AddPendingSale(itemId, deduction);
                if (unmatchedOrder > 0)
                    state.UnmatchedAlecaSaleQuantities[itemId] = state.UnmatchedAlecaSaleQuantities.GetValueOrDefault(itemId) + unmatchedOrder;
                TradeInventoryCache.RememberTradeCredit(state.RecentGameSales, itemId, remaining, trade.ObservedAt);
                soldItems.Add((itemId, remaining));
                actions.Add($"• Warframe confirmed a trade of **{market.NameForItemId(itemId) ?? given.Name}** ×{remaining}; {deduction} stale unit(s) suppressed before AlecaFrame refreshes.");
            }
            state.ProcessedGameTradeIds = state.ProcessedGameTradeIds.Append(trade.Id).TakeLast(1000).ToArray();
            if (actions.Count > 0) state.LastActions = actions.Take(8).ToArray();
            Save();
            if (soldItems.Any(item => state.ManagedOrderIds.ContainsKey(item.ItemId)))
            {
                try { await CloseConfirmedGameOrdersAsync(soldItems, ct); }
                catch (Exception error) when (!ct.IsCancellationRequested && error is not OutOfMemoryException)
                    { Console.WriteLine($"[personal-market] Immediate confirmed-trade order close failed ({error.GetType().Name}); normal reconciliation will retry."); }
            }
            if (actions.Count > 0) Wake();
        }
        finally { gate.Release(); }
    }

    private async Task CloseConfirmedGameOrdersAsync(IReadOnlyList<(string ItemId, int Quantity)> soldItems, CancellationToken ct)
    {
        var orders = (await account.OrdersAsync(ct)).ToDictionary(order => order.Id, StringComparer.Ordinal);
        foreach (var sold in soldItems)
        {
            if (!state.ManagedOrderIds.TryGetValue(sold.ItemId, out var orderId)) continue;
            if (!orders.TryGetValue(orderId, out var current))
            {
                state.ManagedOrderIds.Remove(sold.ItemId);
                state.ManagedOrderQuantities.Remove(sold.ItemId);
                state.ManagedOrderPrices.Remove(sold.ItemId);
                state.UnmatchedAlecaSaleQuantities.Remove(sold.ItemId);
                Save();
                continue;
            }
            var previous = state.ManagedOrderQuantities.GetValueOrDefault(sold.ItemId);
            if (previous <= 0) continue; // Unknown baseline: do not risk closing another copy.
            var alreadyClosed = Math.Min(sold.Quantity, Math.Max(0, previous - current.Quantity));
            var toClose = Math.Min(Math.Max(0, sold.Quantity - alreadyClosed), Math.Max(0, current.Quantity));
            if (toClose > 0) await account.CloseOrderAsync(orderId, toClose, ct);
            var remaining = current.Quantity - toClose;
            if (remaining <= 0)
            {
                state.ManagedOrderIds.Remove(sold.ItemId);
                state.ManagedOrderQuantities.Remove(sold.ItemId);
                state.ManagedOrderPrices.Remove(sold.ItemId);
            }
            else state.ManagedOrderQuantities[sold.ItemId] = remaining;
            var unmatched = Math.Max(0, state.UnmatchedAlecaSaleQuantities.GetValueOrDefault(sold.ItemId) - alreadyClosed - toClose);
            if (unmatched == 0) state.UnmatchedAlecaSaleQuantities.Remove(sold.ItemId);
            else state.UnmatchedAlecaSaleQuantities[sold.ItemId] = unmatched;
            Save();
        }
    }

    private async Task<IReadOnlyList<string>> ObserveAlecaTradesAsync(IReadOnlyList<PrimeInventoryEntry> rawInventory, CancellationToken ct)
    {
        if (!File.Exists(alecaPublicTokenPath)) { alecaTradeStatus = "not connected"; return []; }
        string token;
        try { token = (await File.ReadAllTextAsync(alecaPublicTokenPath, ct)).Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            alecaTradeStatus = "token file unreadable";
            Console.WriteLine($"[personal-market] AlecaFrame public-token file unavailable ({error.GetType().Name}).");
            return ["• AlecaFrame completed-trade feed is unavailable; its public-token file could not be read."];
        }
        if (token.Length is < 8 or > 2048)
        {
            alecaTradeStatus = "token file invalid";
            return ["• AlecaFrame completed-trade feed is disabled because its public-token file is empty or invalid."];
        }

        IReadOnlyList<AlecaCompletedTrade> trades;
        try
        {
            using var payload = await http.GetJsonAsync(
                $"https://stats.alecaframe.com/api/stats/public?token={Uri.EscapeDataString(token)}", ct, lowPriority: true);
            trades = AlecaTradeHistory.Parse(payload.RootElement);
        }
        catch (Exception error) when (!ct.IsCancellationRequested && error is not OutOfMemoryException)
        {
            alecaTradeStatus = $"last check failed ({error.GetType().Name})";
            Console.WriteLine($"[personal-market] AlecaFrame completed-trade feed failed ({error.GetType().Name}); no inventory changes were made.");
            return ["• AlecaFrame completed-trade feed could not be checked; no inventory was deducted."];
        }
        alecaTradeStatus = $"completed-trade feed checked <t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:R>; {trades.Count} record(s) returned";
        if (trades.Count == 0) return [];

        var processed = state.ProcessedAlecaTradeIds.ToHashSet(StringComparer.Ordinal);
        var firstSync = !state.AlecaTradeWatermark.HasValue;
        if (firstSync)
        {
            state.AlecaTradeWatermark = TradeInventoryCache.FirstTradeWatermark(
                new DateTimeOffset(File.GetLastWriteTimeUtc(inventoryPath), TimeSpan.Zero), DateTimeOffset.UtcNow).AddTicks(1);
            // Historical trades already reflected in the source inventory must
            // not be deducted. Recent trades newer than that source are handled
            // by the normal loop below, including on the first successful poll.
        }

        var ownedIds = rawInventory.Select(ItemIdForInventory).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var actions = new List<string>();
        var observedNew = false;
        var watermark = state.AlecaTradeWatermark.GetValueOrDefault();
        foreach (var trade in trades.Where(row => row.Timestamp >= watermark && !processed.Contains(row.Id)))
        {
            observedNew = true;
            processed.Add(trade.Id);
            if (trade.Timestamp > state.AlecaTradeWatermark) state.AlecaTradeWatermark = trade.Timestamp;
            if (!trade.IsSale) continue;
            var mapped = trade.Sent.Select(item => new
            {
                Item = item,
                Name = item.DisplayName.Length > 0 ? item.DisplayName : market.NameForGameRef(item.Name) ?? item.Name,
                ItemId = market.ItemIdForName(item.DisplayName.Length > 0 ? item.DisplayName : market.NameForGameRef(item.Name) ?? item.Name)
            }).Where(row => row.ItemId is not null && (ownedIds.Contains(row.ItemId) || state.ManagedOrderIds.ContainsKey(row.ItemId) ||
                setDefinitions.Any(set => set.SetItemId == row.ItemId))).ToArray();
            foreach (var sold in mapped)
            {
                var releasedHold = TradeInventoryCache.ConsumeMissingOrderHold(
                    state.MissingManagedOrderHolds, sold.ItemId!, sold.Item.Quantity);
                var alreadyFromGame = TradeInventoryCache.ConsumeMatchingTradeCredit(
                    state.RecentGameSales, sold.ItemId!, sold.Item.Quantity, trade.Timestamp);
                var remaining = sold.Item.Quantity - alreadyFromGame;
                var stillInCache = TradeInventoryCache.QuantityStillInCache(sold.ItemId!, remaining,
                    trade.Timestamp, state.RecentInventoryDecreases, setDefinitions);
                var unmatchedOrderQuantity = TradeInventoryCache.QuantityStillInCache(sold.ItemId!, remaining,
                    trade.Timestamp, state.RecentOrderReductions, []);
                var additionalDeduction = TradeInventoryCache.AdditionalConfirmedSaleDeduction(
                    stillInCache, unmatchedOrderQuantity);
                AddPendingSale(sold.ItemId!, additionalDeduction);
                if (unmatchedOrderQuantity > 0)
                    state.UnmatchedAlecaSaleQuantities[sold.ItemId!] =
                        state.UnmatchedAlecaSaleQuantities.GetValueOrDefault(sold.ItemId!) + unmatchedOrderQuantity;
                TradeInventoryCache.RememberTradeCredit(state.RecentAlecaSales, sold.ItemId!, remaining, trade.Timestamp);
                var priceEach = mapped.Length == 1 ? Math.Max(0, trade.TotalPlatinum / sold.Item.Quantity) : 0;
                state.SaleHistory = state.SaleHistory.Append(new(sold.ItemId!, sold.Name, "alecaframe:" + trade.Id[..12],
                    sold.Item.Quantity, priceEach, trade.Timestamp)).OrderByDescending(row => row.DetectedAt).Take(100).ToArray();
                actions.Add(additionalDeduction > 0
                    ? $"• AlecaFrame completed sale: removed **{sold.Name}** ×{additionalDeduction} from cached available inventory."
                    : $"• AlecaFrame completed sale of **{sold.Name}** ×{sold.Item.Quantity} was already accounted for by inventory or a managed-order reduction.");
                if (releasedHold > 0)
                    actions.Add($"• Replaced the missing-order safety hold for **{sold.Name}** ×{releasedHold} with confirmed-trade reconciliation.");
            }
        }
        state.ProcessedAlecaTradeIds = trades.Where(row => row.Timestamp == state.AlecaTradeWatermark && processed.Contains(row.Id))
            .Select(row => row.Id).Distinct(StringComparer.Ordinal).Take(1000).ToArray();
        if (observedNew || firstSync) Save();
        return actions;
    }

    private void AddPendingSale(string itemId, int quantity)
    {
        if (quantity <= 0) return;
        var soldSet = setDefinitions.FirstOrDefault(set => set.SetItemId == itemId);
        if (soldSet is null)
            state.PendingSoldQuantities[itemId] = state.PendingSoldQuantities.GetValueOrDefault(itemId) + quantity;
        else foreach (var part in soldSet.Components)
            state.PendingSoldQuantities[part.ItemId] = state.PendingSoldQuantities.GetValueOrDefault(part.ItemId) + quantity * part.Quantity;
    }

    private List<string> ReconcileAlecaSnapshot(IReadOnlyList<PrimeInventoryEntry> rawInventory)
    {
        var actions = new List<string>();
        var now = DateTimeOffset.UtcNow;
        TradeInventoryCache.ForgetOldDecreases(state.RecentInventoryDecreases, now);
        TradeInventoryCache.ForgetOldDecreases(state.RecentOrderReductions, now);
        TradeInventoryCache.ForgetOldDecreases(state.RecentGameSales, now);
        TradeInventoryCache.ForgetOldDecreases(state.RecentAlecaSales, now);
        var current = rawInventory.ToDictionary(row => row.GameRef, row => row.Quantity, StringComparer.OrdinalIgnoreCase);
        var rawByItemId = rawInventory.Select(row => (Id: ItemIdForInventory(row), row.Quantity))
            .Where(row => row.Id is not null).GroupBy(row => row.Id!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Quantity), StringComparer.Ordinal);
        var decreasesByItemId = new Dictionary<string, int>(StringComparer.Ordinal);
        if (state.LastInventory.Count > 0)
        {
            foreach (var previous in state.LastInventory)
            {
                var decrease = previous.Value - current.GetValueOrDefault(previous.Key);
                if (decrease <= 0) continue;
                var row = rawInventory.FirstOrDefault(item => item.GameRef.Equals(previous.Key, StringComparison.OrdinalIgnoreCase))
                    ?? new PrimeInventoryEntry(previous.Key, 0);
                var itemId = ItemIdForInventory(row);
                if (itemId is null) continue;
                decreasesByItemId[itemId] = decreasesByItemId.GetValueOrDefault(itemId) + decrease;
                var pending = state.PendingSoldQuantities.GetValueOrDefault(itemId);
                var cleared = Math.Min(Math.Max(0, pending), decrease);
                if (cleared > 0)
                {
                    if (cleared >= pending) state.PendingSoldQuantities.Remove(itemId);
                    else state.PendingSoldQuantities[itemId] = pending - cleared;
                    actions.Add($"• AlecaFrame refreshed; cleared {cleared} stale sold-item deduction for **{market.NameForItemId(itemId) ?? "Prime item"}**.");
                }
                // Keep the full change as evidence for a late trade-feed event,
                // including stock already suppressed by an order reduction.
                var previousDecrease = state.RecentInventoryDecreases.GetValueOrDefault(itemId);
                var total = previousDecrease is not null && now - previousDecrease.ObservedAt <= TimeSpan.FromHours(2)
                    ? previousDecrease.Quantity + decrease : decrease;
                state.RecentInventoryDecreases[itemId] = new(total, now);
            }
            foreach (var pendingAleca in state.UnmatchedAlecaSaleQuantities.ToArray())
            {
                var soldSet = setDefinitions.FirstOrDefault(set => set.SetItemId == pendingAleca.Key);
                var confirmed = soldSet is null
                    ? decreasesByItemId.GetValueOrDefault(pendingAleca.Key)
                    : soldSet.Components.Min(part => decreasesByItemId.GetValueOrDefault(part.ItemId) / Math.Max(1, part.Quantity));
                if (confirmed <= 0) continue;
                if (confirmed >= pendingAleca.Value) state.UnmatchedAlecaSaleQuantities.Remove(pendingAleca.Key);
                else state.UnmatchedAlecaSaleQuantities[pendingAleca.Key] = pendingAleca.Value - confirmed;
            }
        }
        var snapshotTime = new DateTimeOffset(File.GetLastWriteTimeUtc(inventoryPath), TimeSpan.Zero);
        foreach (var held in state.MissingManagedOrderHolds.ToArray())
        {
            var set = setDefinitions.FirstOrDefault(definition => definition.SetItemId == held.Key);
            var remaining = set is null ? rawByItemId.GetValueOrDefault(held.Key)
                : set.Components.Count == 0 ? 0
                : set.Components.Min(component => rawByItemId.GetValueOrDefault(component.ItemId) / Math.Max(1, component.Quantity));
            if (!TradeInventoryCache.FreshSnapshotResolvesHold(held.Value, remaining, snapshotTime)) continue;
            state.MissingManagedOrderHolds.Remove(held.Key);
            actions.Add($"• Fresh inventory confirms **{market.NameForItemId(held.Key) ?? "Prime item"}** stock fell from {held.Value.Quantity} to {remaining}; its missing-order safety hold was cleared.");
        }
        state.LastInventory = current;
        return actions;
    }

    private IEnumerable<string> ObserveManagedOrderChanges(IReadOnlyList<WfmOwnOrder> orders, IReadOnlyList<PrimeInventoryEntry> rawInventory)
    {
        var actions = new List<string>();
        var ordersById = orders.Where(order => order.Type == "sell").ToDictionary(order => order.Id, StringComparer.Ordinal);
        foreach (var held in state.MissingManagedOrderHolds.ToArray())
        {
            if (!ordersById.TryGetValue(held.Value.OrderId, out var restored)) continue;
            state.MissingManagedOrderHolds.Remove(held.Key);
            state.ManagedOrderIds[held.Key] = restored.Id;
            state.ManagedOrderQuantities[held.Key] = restored.Quantity;
            state.ManagedOrderPrices[held.Key] = restored.Platinum;
            actions.Add($"• **{market.NameForItemId(held.Key) ?? "Prime item"}** order returned; its missing-order safety hold was cleared.");
        }
        foreach (var managed in state.ManagedOrderIds.ToArray())
        {
            ordersById.TryGetValue(managed.Value, out var current);
            var previous = state.ManagedOrderQuantities.GetValueOrDefault(managed.Key);
            if (previous <= 0 && current is not null)
            {
                state.ManagedOrderQuantities[managed.Key] = current.Quantity; state.ManagedOrderPrices[managed.Key] = current.Platinum;
                continue;
            }
            if (previous <= 0)
            {
                var raw = rawInventory.FirstOrDefault(row => ItemIdForInventory(row) == managed.Key)?.Quantity ?? 0;
                previous = Math.Max(0, raw);
            }
            if (current is null)
            {
                // An absent order can be a manual delete, upstream omission, or an
                // interrupted reconciliation. It is not proof of a sale, but its
                // stock cannot be safely relisted from an unchanged AlecaFrame file.
                var raw = rawInventory.FirstOrDefault(row => ItemIdForInventory(row) == managed.Key)?.Quantity ?? 0;
                var set = setDefinitions.FirstOrDefault(definition => definition.SetItemId == managed.Key);
                if (set is not null)
                    raw = set.Components.Count == 0 ? 0 : set.Components.Min(component =>
                        (rawInventory.FirstOrDefault(row => ItemIdForInventory(row) == component.ItemId)?.Quantity ?? 0)
                        / Math.Max(1, component.Quantity));
                // Hold all currently shown copies, not merely the vanished
                // order's quantity. Otherwise a stale extra copy can be
                // published as a fresh order and disappear again in a loop.
                var held = Math.Max(0, raw);
                if (held > 0 && !state.MissingManagedOrderHolds.ContainsKey(managed.Key))
                    state.MissingManagedOrderHolds[managed.Key] = new(managed.Value, held, DateTimeOffset.UtcNow);
                state.ManagedOrderIds.Remove(managed.Key); state.ManagedOrderQuantities.Remove(managed.Key); state.ManagedOrderPrices.Remove(managed.Key);
                actions.Add($"• Managed order for **{market.NameForItemId(managed.Key) ?? "Prime item"}** disappeared; no sale was inferred, and {held} uncertain unit(s) are held from relisting until a confirmed trade or fresh inventory resolves them.");
                continue;
            }
            var remaining = current.Quantity;
            var sold = Math.Max(0, previous - remaining);
            if (sold > 0)
            {
                var name = market.NameForItemId(managed.Key) ?? "Prime item";
                var alreadyTracked = Math.Min(sold, Math.Max(0, state.UnmatchedAlecaSaleQuantities.GetValueOrDefault(managed.Key)));
                if (alreadyTracked > 0)
                {
                    var unmatched = state.UnmatchedAlecaSaleQuantities[managed.Key] - alreadyTracked;
                    if (unmatched == 0) state.UnmatchedAlecaSaleQuantities.Remove(managed.Key);
                    else state.UnmatchedAlecaSaleQuantities[managed.Key] = unmatched;
                    actions.Add($"• Warframe.market confirmed AlecaFrame's tracked sale of **{name}** ×{alreadyTracked}; no second deduction was made.");
                }
                var newlyObserved = sold - alreadyTracked;
                if (newlyObserved > 0)
                {
                    AddPendingSale(managed.Key, newlyObserved);
                    var previousReduction = state.RecentOrderReductions.GetValueOrDefault(managed.Key);
                    var now = DateTimeOffset.UtcNow;
                    var total = previousReduction is not null && now - previousReduction.ObservedAt <= TimeSpan.FromHours(2)
                        ? previousReduction.Quantity + newlyObserved : newlyObserved;
                    state.RecentOrderReductions[managed.Key] = new(total, now);
                    var price = state.ManagedOrderPrices.GetValueOrDefault(managed.Key, current!.Platinum);
                    var sale = new PersonalMarketSale(managed.Key, name, managed.Value, newlyObserved, price, DateTimeOffset.UtcNow);
                    state.SaleHistory = state.SaleHistory.Append(sale).OrderByDescending(row => row.DetectedAt).Take(100).ToArray();
                    actions.Add($"• Tracked **{name}** ×{newlyObserved} as sold from its managed market-order quantity reduction; stale AlecaFrame inventory is suppressed.");
                }
            }
            state.ManagedOrderQuantities[managed.Key] = current!.Quantity; state.ManagedOrderPrices[managed.Key] = current.Platinum;
        }
        return actions;
    }

    private async Task DispatchButtonAsync(SocketMessageComponent interaction)
    {
        if (interaction.GuildId != targetGuild || !interaction.Data.CustomId.StartsWith("personal-market:", StringComparison.Ordinal)) return;
        var ownerId = configuredOwner == 0 ? interaction.GuildId is { } id ? bot.GetGuild(id)?.OwnerId ?? 0 : 0 : configuredOwner;
        if (interaction.User.Id != ownerId) { await interaction.RespondAsync("This private board belongs to its configured owner.", ephemeral: true); return; }
        if (interaction.Message.Id != state.MessageId)
        {
            await interaction.RespondAsync("This Personal Market message is outdated. Use the current board.", ephemeral: true);
            return;
        }
        if (interaction.Data.CustomId.EndsWith(":prev", StringComparison.Ordinal) ||
            interaction.Data.CustomId.EndsWith(":next", StringComparison.Ordinal))
        {
            var snapshot = Volatile.Read(ref boardSnapshot);
            if (snapshot is not null)
            {
                try { await TurnPageAsync(interaction, snapshot); }
                catch (Exception error)
                {
                    Console.WriteLine($"[personal-market-page] {error.GetType().Name}: {error.Message}");
                    try
                    {
                        if (interaction.HasResponded) await interaction.FollowupAsync("Page change failed. Try again.", ephemeral: true);
                        else await interaction.RespondAsync("Page change failed. Try again.", ephemeral: true);
                    }
                    catch { }
                }
                return;
            }
        }
        await interaction.DeferAsync(ephemeral: true);
        _ = Task.Run(async () =>
        {
            try { await ButtonAsync(interaction); }
            catch (Exception error)
            {
                Console.WriteLine($"[personal-market-button] {error.GetType().Name}: {error.Message}");
                try { await interaction.ModifyOriginalResponseAsync(response => response.Content = $"Personal Market action failed: {error.Message}"); }
                catch (Exception responseError) { Console.WriteLine($"[personal-market-button] Response failed: {responseError.GetType().Name}"); }
            }
        }, CancellationToken.None);
    }

    private async Task TurnPageAsync(SocketMessageComponent interaction, BoardSnapshot snapshot)
    {
        await renderGate.WaitAsync();
        try
        {
            var pages = Math.Max(1, (snapshot.Quotes.Length + PageSize - 1) / PageSize);
            var delta = interaction.Data.CustomId.EndsWith(":next", StringComparison.Ordinal) ? 1 : -1;
            var nextPage = Math.Clamp(state.Page + delta, 1, pages);
            var board = BuildBoard(snapshot, nextPage);
            await interaction.UpdateAsync(response =>
            {
                response.Embed = board.Embed;
                response.Components = board.Components;
                response.AllowedMentions = AllowedMentions.None;
            });
            state.Page = nextPage;
            state.RenderHash = board.Hash;
        }
        finally { renderGate.Release(); }
    }
    private async Task ButtonAsync(SocketMessageComponent interaction)
    {
        await interaction.ModifyOriginalResponseAsync(response => response.Content = "Applying Personal Market changes…");
        await gate.WaitAsync();
        try
        {
            if (interaction.Data.CustomId.EndsWith(":prev", StringComparison.Ordinal)) state.Page--;
            if (interaction.Data.CustomId.EndsWith(":next", StringComparison.Ordinal)) state.Page++;
            if (interaction.Data.CustomId.EndsWith(":auto-toggle", StringComparison.Ordinal)) state.AutoPublishEnabled = state.AutoPublishEnabled != true;
            if (interaction.Data.CustomId.EndsWith(":sets-toggle", StringComparison.Ordinal)) state.SellCompleteSets = !state.SellCompleteSets;
            if (interaction.Data.CustomId.EndsWith(":completion-toggle", StringComparison.Ordinal)) state.ProtectIncompleteSets = !state.ProtectIncompleteSets;
            Save();
            var channel = interaction.Channel as ITextChannel ?? throw new InvalidOperationException("Personal Market channel unavailable.");
            await UpdateLockedAsync(channel, force: true, CancellationToken.None);
            await interaction.ModifyOriginalResponseAsync(response => response.Content = state.AutoPublishEnabled == true
                ? "Personal Market synchronized with Warframe.market using the selected set/completion rules."
                : "Automatic listing is paused; inventory view and set/completion rules refreshed without account writes.");
        }
        catch (Exception e) { await interaction.ModifyOriginalResponseAsync(response => response.Content = $"Refresh failed: {e.Message}"); }
        finally { gate.Release(); }
    }

    private void Save() => Json.WriteAtomic(statePath, state);
    public async Task StopAsync() { if (cancellation is not null) await cancellation.CancelAsync(); if (worker is not null) await worker; status = "stopped"; }
    public async ValueTask DisposeAsync() { bot.ButtonExecuted -= DispatchButtonAsync; await StopAsync(); inventoryWatcher?.Dispose(); cancellation?.Dispose(); gate.Dispose(); wake.Dispose(); }
}
