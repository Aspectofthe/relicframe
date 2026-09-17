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
    public Dictionary<string, string> ManagedOrderIds { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> LastInventory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ManagedOrderQuantities { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ManagedOrderPrices { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> PendingSoldQuantities { get; set; } = new(StringComparer.Ordinal);
    public PersonalMarketSale[] SaleHistory { get; set; } = [];
    public string[] LastActions { get; set; } = [];
    public DateTimeOffset? LastAccountSync { get; set; }
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
    private readonly DiscordSocketClient bot;
    private readonly LiveMarket market;
    private readonly ulong targetGuild;
    private readonly ulong configuredOwner;
    private readonly string inventoryPath;
    private readonly string statePath;
    private readonly int minimumPrice;
    private readonly int undercut;
    private readonly string ownSellerSlug;
    private readonly WfmAccountClient account;
    private readonly PrimeSetCompletion setCompletion;
    private IReadOnlyList<PrimeSetDefinition> setDefinitions = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    private PersonalMarketState state;
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private bool reconciliationPending;
    private string status = "stopped";
    public string Status => Volatile.Read(ref status);
    public string ExcludedSellerSlug => ownSellerSlug;
    public ulong OwnerId => configuredOwner;

    public PersonalMarketManager(DiscordSocketClient bot, LiveMarket market, MarketHttp http, ulong targetGuild, string runtime)
    {
        this.bot = bot; this.market = market; this.targetGuild = targetGuild;
        var settingsPath = Path.Combine(runtime, "personal_market_settings.json");
        PersonalMarketSettings settings;
        try { settings = File.Exists(settingsPath) ? Json.Read<PersonalMarketSettings>(settingsPath) : new(); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { settings = new(); status = "settings unreadable; environment/defaults used"; }
        configuredOwner = ulong.TryParse(Environment.GetEnvironmentVariable("RELICFRAME_PERSONAL_USER_ID"), out var owner) ? owner : settings.DiscordUserId;
        var configuredInventory = Environment.GetEnvironmentVariable("RELICFRAME_PRIME_INVENTORY_JSON");
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var alecaInventory = OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(localData)
            ? Path.Combine(localData, "AlecaFrame", "lastData.dat") : "";
        inventoryPath = Path.GetFullPath(!string.IsNullOrWhiteSpace(configuredInventory) ? configuredInventory : !string.IsNullOrWhiteSpace(settings.InventoryJson) ? settings.InventoryJson : File.Exists(alecaInventory) ? alecaInventory : Path.Combine(runtime, "prime_inventory.json"));
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
                var delay = market.ReadyBooks == 0 || market.IsBootstrapping || reconciliationPending ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5);
                await Task.Delay(delay, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
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
            return PrimeInventory.SubtractPendingSales(raw, state.PendingSoldQuantities, ItemIdForInventory);
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
        // Observe the market first. If AlecaFrame refreshed in the same interval, its
        // matching raw decrease then clears the newly-created temporary deduction.
        sourceActions.AddRange(ReconcileAlecaSnapshot(rawInventory));
        var inventory = PrimeInventory.SubtractPendingSales(rawInventory, state.PendingSoldQuantities, ItemIdForInventory);
        var blockedIds = new HashSet<string>(StringComparer.Ordinal);
        var setInventory = new List<PrimeInventoryEntry>();
        if (marketUsable)
        {
            var topSets = (await setCompletion.AnalyzeAsync(inventory, ownSellerSlug, null, ct, requireComplete: true)).Take(10)
                .Select(row => row.SetName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ownedCounts = inventory.GroupBy(ItemIdForInventory).Where(group => group.Key is not null)
                .ToDictionary(group => group.Key!, group => group.Sum(row => row.Quantity), StringComparer.Ordinal);
            foreach (var definition in setDefinitions)
            {
                var quantity = definition.Components.Min(part => ownedCounts.GetValueOrDefault(part.ItemId) / part.Quantity);
                var protectedSet = topSets.Contains(definition.SetName);
                if (!protectedSet && quantity == 0) continue;
                foreach (var part in definition.Components) blockedIds.Add(part.ItemId);
                if (protectedSet) { blockedIds.Add(definition.SetItemId); continue; }
                await market.RefreshBooksAsync([definition.SetName], force ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5), ct, lowPriority: !force);
                setInventory.Add(new(definition.SetItemId, quantity, definition.SetName));
            }
        }
        var sellableInventory = inventory.Where(row => !blockedIds.Contains(ItemIdForInventory(row) ?? "")).Concat(setInventory).ToArray();
        candidates = sellableInventory.Select(item => new
        {
            Item = item,
            Name = !string.IsNullOrWhiteSpace(item.ItemName) ? item.ItemName : market.NameForGameRef(item.GameRef)
        }).Where(row => row.Name is not null && (row.Name.Contains(" Prime ", StringComparison.OrdinalIgnoreCase) || row.Name.EndsWith(" Prime", StringComparison.OrdinalIgnoreCase))).ToArray();
        var assessed = candidates.Select(row =>
        {
            var estimate = market.RewardEstimate(row.Name!, ownSellerSlug);
            var visible = market.Match(row.Name!, null, false, excludedSellerSlug: ownSellerSlug).Entries;
            var reference = estimate.Price.HasValue ? visible.MinBy(entry => Math.Abs(entry.Price - estimate.Price.Value)) : null;
            var best = estimate.Price.HasValue ? new OrderEntry(estimate.Price.Value, reference?.Quantity, Seller: reference?.Seller ?? "stabilized market") : null;
            return new { row.Item, Name = row.Name!, Best = best, Quote = PrimeInventory.Quote(row.Item, row.Name, estimate.Price, best?.Seller ?? "", minimumPrice, undercut), BookFresh = market.IsBookFresh(row.Name!, TimeSpan.FromMinutes(10)) };
        }).ToArray();
        var quotes = assessed.Select(row => row.Quote).OfType<PersonalMarketQuote>().OrderByDescending(row => row.DraftPrice).ThenBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase).ToArray();
        if (state.AutoPublishEnabled == true && marketUsable && orders is not null)
        {
            var freshIneligible = assessed.Where(row => row.Quote is null && row.BookFresh)
                .Select(row => market.ItemIdForName(row.Name)).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var actions = await ReconcileAccountAsync(sellableInventory, quotes, freshIneligible, orders, sourceActions, ct, blockedIds);
            if (actions.Length > 0) state.LastActions = actions;
        }
        else if (sourceActions.Count > 0) state.LastActions = sourceActions.Take(8).ToArray();
        var pages = Math.Max(1, (quotes.Length + PageSize - 1) / PageSize); state.Page = Math.Clamp(state.Page, 1, pages);
        var rows = quotes.Skip((state.Page - 1) * PageSize).Take(PageSize)
            .Select((row, index) =>
            {
                var itemId = market.ItemIdForName(row.ItemName);
                var managed = itemId is not null && state.ManagedOrderIds.ContainsKey(itemId);
                return $"**{(state.Page - 1) * PageSize + index + 1}. {row.ItemName}** ×{row.Quantity} · lowest {row.LowestAsk}p · {(managed ? "listed" : "target")} **{row.DraftPrice}p**{(row.LowestSeller.Length > 0 ? $" · `{row.LowestSeller}`" : "")}";
            });
        var sourceAge = File.GetLastWriteTimeUtc(inventoryPath);
        var description = quotes.Length == 0
            ? $"No mapped Prime inventory items currently have a non-outlier online ask of at least **{minimumPrice}p**. If this file is still empty, import your inventory JSON at `{inventoryPath}`."
            : string.Join('\n', rows);
        var mode = state.AutoPublishEnabled == true ? "AUTO LISTING ENABLED" : "AUTO LISTING PAUSED";
        description += $"\n\n**{mode}.** Minimum {minimumPrice}p · undercut {undercut}p · stabilized online/recent-visible price · owner listing excluded: {(ownSellerSlug.Length > 0 ? "yes" : "no; set RELICFRAME_WFM_USER_SLUG")} · inventory file <t:{new DateTimeOffset(sourceAge).ToUnixTimeSeconds()}:R>.";
        var belowFloor = assessed.Where(row => row.Quote is null && row.Best is not null && row.Best.Price < minimumPrice)
            .OrderByDescending(row => row.Best!.Price).ThenBy(row => row.Item.ItemName, StringComparer.OrdinalIgnoreCase).ToArray();
        var noAsk = assessed.Count(row => row.Quote is null && row.Best is null);
        var belowExamples = string.Join(", ", belowFloor.Take(3).Select(row => $"{row.Name} {row.Best!.Price:0.#}p"));
        var pricedCandidates = assessed.Count(row => row.Best is not null);
        description += $"\n**Independent listing rules:** every mapped owned Prime part, both **Vaulted and Unvaulted**; THE LIST display filters are not used. Shared relic-scanner price coverage: {pricedCandidates}/{candidates.Length}. Skipped below {minimumPrice}p: {belowFloor.Length}{(belowExamples.Length > 0 ? $" ({belowExamples})" : "")} · no usable in-game ask: {noAsk}.";
        if (state.AutoPublishEnabled == true && !marketUsable) description += $"\nAccount writes wait for usable market books; current market: **{market.Status}**.";
        if (state.LastActions.Length > 0) description += "\n\n**Latest account activity**\n" + string.Join('\n', state.LastActions.Take(6));
        if (state.SaleHistory.Length > 0)
            description += "\n\n**Recent tracked sales**\n" + string.Join('\n', state.SaleHistory.Take(3).Select(sale =>
                $"• **{sale.ItemName}** ×{sale.Quantity}{(sale.PlatinumEach > 0 ? $" at {sale.PlatinumEach}p each" : "")} · <t:{sale.DetectedAt.ToUnixTimeSeconds()}:R>"));
        var pendingSold = state.PendingSoldQuantities.Values.Sum(value => Math.Max(0, value));
        description += $"\nManaged Warframe.market order reductions/closures are tracked as sold; AlecaFrame inventory changes alone are not. Pending stale-cache deduction: **{pendingSold}** item(s).";
        var embed = new EmbedBuilder().WithTitle("Personal Prime Market · automatic listings").WithDescription(description.Length <= 4096 ? description : description[..4093] + "…")
            .WithColor(new Color(state.AutoPublishEnabled == true ? 0x2ECC71u : 0xF39C12u)).WithFooter($"Page {state.Page}/{pages} · {quotes.Length} eligible items · authenticated sync {(state.LastAccountSync.HasValue ? state.LastAccountSync.Value.ToString("u") : "never")}").Build();
        var components = new ComponentBuilder()
            .WithButton("Previous", "personal-market:prev", ButtonStyle.Secondary, disabled: state.Page <= 1)
            .WithButton("Sync listings now", "personal-market:refresh", ButtonStyle.Primary)
            .WithButton("Next", "personal-market:next", ButtonStyle.Secondary, disabled: state.Page >= pages)
            .WithButton(state.AutoPublishEnabled == true ? "Pause auto listing" : "Enable auto listing", "personal-market:auto-toggle", state.AutoPublishEnabled == true ? ButtonStyle.Danger : ButtonStyle.Success, row: 1).Build();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(embed.Description + "\0" + embed.Footer?.Text + "\0" + state.Page)));
        if (!force && state.MessageId != 0 && state.RenderHash == hash) { Save(); status = $"ready; {quotes.Length} eligible; auto listing {(state.AutoPublishEnabled == true ? "enabled" : "paused")}"; return; }
        var old = state.MessageId == 0 ? null : await channel.GetMessageAsync(state.MessageId) as IUserMessage;
        if (old is null) { var sent = await channel.SendMessageAsync(embed: embed, components: components, allowedMentions: AllowedMentions.None); state.MessageId = sent.Id; }
        else await old.ModifyAsync(properties => { properties.Embed = embed; properties.Components = components; properties.AllowedMentions = AllowedMentions.None; });
        state.RenderHash = hash; Save(); status = $"ready; {quotes.Length} eligible; auto listing {(state.AutoPublishEnabled == true ? "enabled" : "paused")}";
    }

    private async Task<string[]> ReconcileAccountAsync(IReadOnlyList<PrimeInventoryEntry> inventory, IReadOnlyList<PersonalMarketQuote> quotes,
        IReadOnlySet<string> freshIneligible, IReadOnlyList<WfmOwnOrder> orders, IEnumerable<string> observedActions, CancellationToken ct,
        IReadOnlySet<string> blockedIds)
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
        // Remove every conflicting component/protected-set order before publishing sets.
        foreach (var order in orders.Where(order => order.Type.Equals("sell", StringComparison.OrdinalIgnoreCase) && blockedIds.Contains(order.ItemId)))
        {
            if (writes >= 25) { reconciliationPending = true; Save(); return ["• Set-part removals pending; set listing waits for the next pass."]; }
            await account.DeleteOrderAsync(order.Id, ct); writes++;
            state.ManagedOrderIds.Remove(order.ItemId); state.ManagedOrderQuantities.Remove(order.ItemId); state.ManagedOrderPrices.Remove(order.ItemId);
            sellOrders.Remove(order.ItemId); Save();
            actions.Add($"• Deleted **{market.NameForItemId(order.ItemId)}**: reserved for a set or protected top-10 completion.");
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

    private List<string> ReconcileAlecaSnapshot(IReadOnlyList<PrimeInventoryEntry> rawInventory)
    {
        var actions = new List<string>();
        var current = rawInventory.ToDictionary(row => row.GameRef, row => row.Quantity, StringComparer.OrdinalIgnoreCase);
        if (state.LastInventory.Count > 0)
        {
            foreach (var previous in state.LastInventory)
            {
                var decrease = previous.Value - current.GetValueOrDefault(previous.Key);
                if (decrease <= 0) continue;
                var row = rawInventory.FirstOrDefault(item => item.GameRef.Equals(previous.Key, StringComparison.OrdinalIgnoreCase))
                    ?? new PrimeInventoryEntry(previous.Key, 0);
                var itemId = ItemIdForInventory(row);
                if (itemId is null || !state.PendingSoldQuantities.TryGetValue(itemId, out var pending) || pending <= 0) continue;
                var cleared = Math.Min(pending, decrease);
                if (cleared >= pending) state.PendingSoldQuantities.Remove(itemId);
                else state.PendingSoldQuantities[itemId] = pending - cleared;
                actions.Add($"• AlecaFrame refreshed; cleared {cleared} stale sold-item deduction for **{market.NameForItemId(itemId) ?? "Prime item"}**.");
            }
        }
        state.LastInventory = current;
        return actions;
    }

    private IEnumerable<string> ObserveManagedOrderChanges(IReadOnlyList<WfmOwnOrder> orders, IReadOnlyList<PrimeInventoryEntry> rawInventory)
    {
        var actions = new List<string>();
        var ordersById = orders.Where(order => order.Type == "sell").ToDictionary(order => order.Id, StringComparer.Ordinal);
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
                // interrupted reconciliation. It is not proof that a trade completed.
                state.ManagedOrderIds.Remove(managed.Key); state.ManagedOrderQuantities.Remove(managed.Key); state.ManagedOrderPrices.Remove(managed.Key);
                actions.Add($"• Managed order for **{market.NameForItemId(managed.Key) ?? "Prime item"}** disappeared; no sale was inferred and owned stock remains eligible for relisting.");
                continue;
            }
            var remaining = current.Quantity;
            var sold = Math.Max(0, previous - remaining);
            if (sold > 0)
            {
                var soldSet = setDefinitions.FirstOrDefault(set => set.SetItemId == managed.Key);
                if (soldSet is null)
                    state.PendingSoldQuantities[managed.Key] = state.PendingSoldQuantities.GetValueOrDefault(managed.Key) + sold;
                else foreach (var part in soldSet.Components)
                    state.PendingSoldQuantities[part.ItemId] = state.PendingSoldQuantities.GetValueOrDefault(part.ItemId) + sold * part.Quantity;
                var name = market.NameForItemId(managed.Key) ?? "Prime item";
                var price = state.ManagedOrderPrices.GetValueOrDefault(managed.Key, current!.Platinum);
                var sale = new PersonalMarketSale(managed.Key, name, managed.Value, sold, price, DateTimeOffset.UtcNow);
                state.SaleHistory = state.SaleHistory.Append(sale).OrderByDescending(row => row.DetectedAt).Take(100).ToArray();
                actions.Add($"• Tracked **{name}** ×{sold} as sold from its managed market-order quantity reduction; stale AlecaFrame inventory is suppressed.");
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
        await interaction.DeferAsync(ephemeral: true);
        _ = Task.Run(async () =>
        {
            try { await ButtonAsync(interaction); }
            catch (Exception error)
            {
                Console.WriteLine($"[personal-market-button] {error.GetType().Name}: {error.Message}");
                try { await interaction.FollowupAsync($"Personal Market action failed: {error.Message}", ephemeral: true); }
                catch (Exception responseError) { Console.WriteLine($"[personal-market-button] Response failed: {responseError.GetType().Name}"); }
            }
        }, CancellationToken.None);
    }
    private async Task ButtonAsync(SocketMessageComponent interaction)
    {
        await gate.WaitAsync();
        try
        {
            if (interaction.Data.CustomId.EndsWith(":prev", StringComparison.Ordinal)) state.Page--;
            if (interaction.Data.CustomId.EndsWith(":next", StringComparison.Ordinal)) state.Page++;
            if (interaction.Data.CustomId.EndsWith(":auto-toggle", StringComparison.Ordinal)) state.AutoPublishEnabled = state.AutoPublishEnabled != true;
            var channel = interaction.Channel as ITextChannel ?? throw new InvalidOperationException("Personal Market channel unavailable.");
            await UpdateLockedAsync(channel, force: true, CancellationToken.None);
            await interaction.FollowupAsync(state.AutoPublishEnabled == true ? "Personal Market synchronized with Warframe.market." : "Automatic listing is paused; inventory view refreshed without account writes.", ephemeral: true);
        }
        catch (Exception e) { await interaction.FollowupAsync($"Refresh failed: {e.Message}", ephemeral: true); }
        finally { gate.Release(); }
    }

    private void Save() => Json.WriteAtomic(statePath, state);
    public async Task StopAsync() { if (cancellation is not null) await cancellation.CancelAsync(); if (worker is not null) await worker; status = "stopped"; }
    public async ValueTask DisposeAsync() { bot.ButtonExecuted -= DispatchButtonAsync; await StopAsync(); cancellation?.Dispose(); gate.Dispose(); }
}
