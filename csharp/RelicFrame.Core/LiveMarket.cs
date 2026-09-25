using System.Collections.Concurrent;
using System.Text.Json;

namespace RelicFrame.Core;

public sealed record MarketSnapshot(DateTimeOffset FetchedAt, Refinement Refinement,
    IReadOnlyDictionary<string, double?> Prices, IReadOnlyDictionary<string, PriceInfo> RelicPrices,
    IReadOnlyDictionary<string, int> Ducats, int ReadyBooks, int TotalBooks);
public sealed record RewardPriceEstimate(double? Price, double? OnlineFloor, double? RecentVisibleMedian,
    int VisibleAsks, bool Stabilized);
public sealed record MarketCatalogItem(string Id, string Slug, string Name, IReadOnlyList<string> Tags, int? MaxRank);

public sealed class LiveMarket : IAsyncDisposable
{
    private readonly MarketHttp http;
    private readonly IReadOnlyDictionary<string, Relic> relics;
    private readonly HashSet<string> unvaultedRewardNames;
    private readonly ConcurrentDictionary<string, OrderBook> books = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> refreshedThisRun = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> names = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, int> ducats = new Dictionary<string, int>();
    private IReadOnlyDictionary<string, string> idToSlug = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, string> idToName = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, string> nameToId = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, string> gameRefToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, MarketCatalogItem> catalogById = new Dictionary<string, MarketCatalogItem>();
    private IReadOnlyList<string> primeSetNames = [];
    private IReadOnlyList<MarketCatalogItem> arcaneItems = [];
    private string[] required = [];
    private readonly object requiredGate = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private readonly string catalogPath;
    private readonly string bookCachePath;
    private sealed record CachedSnapshot(long CreatedAt, string BlacklistKey, MarketSnapshot Value);
    private readonly object snapshotGate = new();
    private readonly Dictionary<Refinement, CachedSnapshot> snapshotCache = [];
    private readonly bool enableWebSocket;
    private WfmWebSocket? webSocket;
    private string status = "stopped";
    private int bootstrapAttempted;
    private int bootstrapTotal;
    private DateTimeOffset bootstrapStartedAt;
    public SellerBlacklist Blacklist { get; }
    public string Status => Volatile.Read(ref status);
    public int ReadyBooks => books.Values.Count(b => b.FetchedAt.HasValue);
    public int RefreshedBooks => refreshedThisRun.Count;
    public int TotalBooks => required.Length;
    public int BootstrapAttempted => Volatile.Read(ref bootstrapAttempted);
    public int BootstrapTotal => Volatile.Read(ref bootstrapTotal);
    public TimeSpan? BootstrapRemaining
    {
        get
        {
            var total = BootstrapTotal; var attempted = BootstrapAttempted;
            if (!IsBootstrapping || total <= 0 || attempted >= total) return null;
            var remaining = total - attempted; var floorSeconds = remaining / Math.Max(.1, http.RequestsPerSecond);
            var elapsed = DateTimeOffset.UtcNow - bootstrapStartedAt;
            var observedSeconds = attempted > 0 && elapsed > TimeSpan.Zero ? elapsed.TotalSeconds / attempted * remaining : 0;
            return TimeSpan.FromSeconds(Math.Max(floorSeconds, observedSeconds));
        }
    }
    public DateTimeOffset? OldestSuccessfulBook
    {
        get
        {
            var stamps = books.Values.Select(book => book.FetchedAt).OfType<DateTimeOffset>().ToArray();
            return stamps.Length == 0 ? null : stamps.Min();
        }
    }
    public bool IsBootstrapping => Status.StartsWith("refreshing", StringComparison.Ordinal);
    public string WebSocketStatus => !enableWebSocket ? "disabled" : webSocket?.Connected == true ? "connected" : "reconnecting/REST-only";
    public LiveMarket(MarketHttp http, IReadOnlyDictionary<string, Relic> relics, string runtimePath, bool enableWebSocket = false)
    {
        this.http = http; this.relics = relics; this.enableWebSocket = enableWebSocket; catalogPath = Path.Combine(runtimePath, "catalog.json");
        bookCachePath = Path.Combine(runtimePath, "order-books");
        unvaultedRewardNames = relics.Values.Where(relic => relic.Vaulted is false).SelectMany(relic => relic.Rewards)
            .Select(reward => reward.RewardName.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        Blacklist = new(Path.Combine(runtimePath, "seller_blacklist.json"));
    }
    public async Task StartAsync(bool forceCatalog, CancellationToken lifetime)
    {
        await lifecycle.WaitAsync(lifetime);
        try
        {
            if (worker is { IsCompleted: false }) return;
            cancellation?.Dispose(); cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            refreshedThisRun.Clear();
            Volatile.Write(ref status, "loading catalog");
            worker = Task.Run(() => RunAsync(forceCatalog, cancellation.Token), CancellationToken.None);
        }
        finally { lifecycle.Release(); }
    }
    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            if (cancellation is not null) await cancellation.CancelAsync();
            if (worker is not null) await worker;
            Volatile.Write(ref status, "stopped (last successful books retained)");
        }
        finally { lifecycle.Release(); }
    }
    private async Task LoadCatalogAsync(bool force, CancellationToken ct)
    {
        JsonDocument? doc = null;
        try
        {
            if (!force && File.Exists(catalogPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(catalogPath) < TimeSpan.FromDays(1))
            {
                try { doc = JsonDocument.Parse(await File.ReadAllTextAsync(catalogPath, ct)); } catch (JsonException) { }
            }
            if (doc is null)
            {
                try { doc = await http.GetJsonAsync("https://api.warframe.market/v2/items", ct); Json.WriteAtomic(catalogPath, doc.RootElement); }
                catch (HttpRequestException) when (File.Exists(catalogPath)) { doc = JsonDocument.Parse(await File.ReadAllTextAsync(catalogPath, ct)); }
            }
            var nextNames = new Dictionary<string, string>(); var nextDucats = new Dictionary<string, int>(); var nextIds = new Dictionary<string, string>(); var nextIdNames = new Dictionary<string, string>(); var nextNameIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var nextGameRefs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var nextCatalog = new Dictionary<string, MarketCatalogItem>(StringComparer.Ordinal);
            foreach (var row in doc.RootElement.Get("data").Rows())
            {
                var displayName = row.Get("i18n").Get("en").Get("name").Text().Trim();
                var name = displayName.ToLowerInvariant();
                var slug = row.Get("slug").Text(); if (name.Length == 0 || slug.Length == 0) continue;
                nextNames[name] = slug; var id = row.Get("id").Text();
                var tags = row.Get("tags").Rows().Select(tag => tag.Text()).Where(tag => tag.Length > 0).ToArray();
                if (id.Length > 0)
                {
                    nextIds[id] = slug; nextIdNames[id] = displayName; nextNameIds[name] = id;
                    nextCatalog[id] = new(id, slug, displayName, tags,
                        row.Get("maxRank").Number() is { } maxRank ? (int?)maxRank : null);
                }
                if (row.Get("ducats").Number() is { } d) nextDucats[name] = (int)d;
                var gameRef = row.Get("gameRef").Text(); if (gameRef.Length > 0) nextGameRefs[gameRef] = displayName;
            }
            if (nextNames.Count == 0) throw new InvalidDataException("Empty marketplace catalog");
            // v2 catalogs may omit ducats; the same WFInfo equipment source used by Python fills that gap.
            var ducatCache = Path.Combine(Path.GetDirectoryName(catalogPath)!, "ducats.json");
            if (File.Exists(ducatCache))
            {
                try { foreach (var pair in Json.Read<Dictionary<string, int>>(ducatCache)) nextDucats.TryAdd(pair.Key, pair.Value); }
                catch (JsonException) { }
            }
            try
            {
                using var filtered = await http.GetJsonAsync("https://api.warframestat.us/wfinfo/filtered_items", ct);
                var equipment = filtered.RootElement.Get("eqmt");
                if (equipment.ValueKind == JsonValueKind.Object)
                    foreach (var prime in equipment.EnumerateObject())
                        if (prime.Value.Get("parts").ValueKind == JsonValueKind.Object)
                            foreach (var part in prime.Value.Get("parts").EnumerateObject())
                                if (part.Value.Get("ducats").Number() is { } value) nextDucats[part.Name.Trim().ToLowerInvariant()] = (int)value;
                if (nextDucats.Count > 0) Json.WriteAtomic(ducatCache, nextDucats);
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is not OutOfMemoryException)
            { Console.WriteLine($"[ducats] {e.GetType().Name}; cached values retained where available"); }
            names = nextNames; ducats = nextDucats; idToSlug = nextIds; idToName = nextIdNames;
            nameToId = nextNameIds;
            gameRefToName = nextGameRefs;
            catalogById = nextCatalog;
            primeSetNames = nextCatalog.Values.Where(item => item.Tags.Contains("prime", StringComparer.OrdinalIgnoreCase)
                    && item.Tags.Contains("set", StringComparer.OrdinalIgnoreCase))
                .Select(item => item.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            arcaneItems = nextCatalog.Values.Where(item => item.Tags.Contains("arcane_enhancement", StringComparer.OrdinalIgnoreCase))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            // Load every known reward book from both Vaulted and Unvaulted relics. Display
            // filtering belongs to THE LIST; Personal Market reuses these same part prices.
            lock (requiredGate)
                required = required.Concat(primeSetNames.Select(name => Resolve(name)))
                    .Concat(relics.Values.SelectMany(r => r.Rewards.Select(reward => Resolve(reward.RewardName)).Append(Resolve(r.RelicName, true))))
                    .Where(s => s is not null).Select(s => s!).Distinct().Order(StringComparer.Ordinal).ToArray();
            Directory.CreateDirectory(bookCachePath);
            foreach (var slug in required)
            {
                var book = books.GetOrAdd(slug, _ => new OrderBook());
                if (!book.FetchedAt.HasValue)
                    book.TryRestoreCache(Path.Combine(bookCachePath, slug + ".rfob"), TimeSpan.FromMinutes(30));
            }
        }
        finally { doc?.Dispose(); }
    }
    public string? Resolve(string name, bool relic = false)
    {
        var key = name.Trim().ToLowerInvariant();
        return (relic ? names.GetValueOrDefault(key + " relic") : null) ?? names.GetValueOrDefault(key);
    }
    private async Task RunAsync(bool force, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                try
                {
                    using var catalogPriority = http.BeginPriorityWork();
                    await LoadCatalogAsync(force, ct); break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Volatile.Write(ref status, $"catalog unavailable ({error.GetType().Name}); retry in 1 minute; old books retained");
                    Console.WriteLine($"[market-catalog] {error}");
                    force = false;
                    await Task.Delay(TimeSpan.FromMinutes(1), ct);
                }
            }
            // Use the live book dictionary so Prime-set and Arcane books registered after
            // startup also receive incremental new-order events.
            webSocket = new(idToSlug, slug => books.ContainsKey(slug), (slug, order) => books.GetOrAdd(slug, _ => new OrderBook()).ApplyCreated(order));
            var webSocketTask = enableWebSocket && idToSlug.Count > 0 ? webSocket.RunAsync(ct) : Task.CompletedTask;
            var failures = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            var missing = required.Where(slug => !books.TryGetValue(slug, out var cached) || !cached.FetchedAt.HasValue).ToArray();
            bootstrapStartedAt = DateTimeOffset.UtcNow;
            Interlocked.Exchange(ref bootstrapAttempted, 0); Interlocked.Exchange(ref bootstrapTotal, missing.Length);
            if (missing.Length > 0) Volatile.Write(ref status, BootstrapStatus(0, missing.Length));
            using (http.BeginPriorityWork())
            {
                await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (slug, token) =>
                {
                    if (await FetchBookAsync(slug, token)) failures.TryRemove(slug, out _); else failures[slug] = "fetch failed";
                    var attempted = Interlocked.Increment(ref bootstrapAttempted);
                    if (attempted == missing.Length || attempted % 10 == 0) Volatile.Write(ref status, BootstrapStatus(attempted, missing.Length));
                });
            }
            Volatile.Write(ref status, failures.IsEmpty ? "ready" : $"partial: {failures.Count} failed books; older data retained where available");
            while (!ct.IsCancellationRequested)
            {
                if (required.Length == 0) { await Task.Delay(TimeSpan.FromSeconds(30), ct); continue; }
                // Smooth the full reconciliation across five minutes instead of producing a periodic API burst.
                var slug = required.OrderBy(item => books.TryGetValue(item, out var book) ? book.FetchedAt ?? DateTimeOffset.MinValue : DateTimeOffset.MinValue).First();
                if (await FetchBookAsync(slug, ct)) failures.TryRemove(slug, out _); else failures[slug] = "fetch failed";
                Volatile.Write(ref status, failures.IsEmpty ? "ready" : $"partial: {failures.Count} failed books; older data retained where available");
                await Task.Delay(TimeSpan.FromMinutes(5).TotalMilliseconds / required.Length < 50 ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromMinutes(5) / required.Length, ct);
            }
            await webSocketTask;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { Volatile.Write(ref status, "failed: " + e.GetType().Name); Console.WriteLine($"[market] worker failed: {e}"); }
    }
    private string BootstrapStatus(int attempted, int total)
    {
        var remaining = Math.Max(0, total - attempted);
        var elapsed = DateTimeOffset.UtcNow - bootstrapStartedAt;
        var observedSeconds = attempted > 0 && elapsed > TimeSpan.Zero ? elapsed.TotalSeconds / attempted * remaining : 0;
        var seconds = (int)Math.Ceiling(Math.Max(remaining / http.RequestsPerSecond, observedSeconds));
        var floor = seconds >= 60 ? $"{seconds / 60}m {seconds % 60}s" : $"{seconds}s";
        var percent = total <= 0 ? 0 : attempted * 100d / total;
        return $"refreshing: {attempted}/{total} attempted ({percent:0.#}%); ETA ~{floor}";
    }
    private async Task<bool> FetchBookAsync(string slug, CancellationToken ct, bool lowPriority = false)
    {
        try
        {
            using var doc = await http.GetJsonAsync("https://api.warframe.market/v2/orders/item/" + Uri.EscapeDataString(slug), ct, lowPriority: lowPriority);
            var rows = doc.RootElement.Get("data"); if (rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Missing order array");
            var book = books.GetOrAdd(slug, _ => new OrderBook()); book.Replace(rows);
            refreshedThisRun[slug] = 0;
            var cache = Path.Combine(bookCachePath, slug + ".rfob");
            try
            {
                if (!File.Exists(cache) || DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) >= TimeSpan.FromMinutes(15)) book.SaveCache(cache);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The live response is already in memory. A transient Windows file lock
                // must not turn a successful market refresh into a failed book.
                Console.WriteLine($"[market-cache] {slug}: {error.GetType().Name}; live book retained, disk cache will retry");
            }
            return true;
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is not OutOfMemoryException)
        { Console.WriteLine($"[market] {slug}: {e.GetType().Name}; old book retained, if any"); return false; }
    }
    public OrderMatch Match(string name, string? tier, bool online, bool relic = false, string? excludedSellerSlug = null, IReadOnlySet<string>? excludedOrderIds = null)
    {
        var slug = Resolve(name, relic);
        return slug is not null && books.TryGetValue(slug, out var book) && book.FetchedAt.HasValue ? book.Match(tier, online, Blacklist.Snapshot, excludedSellerSlug, excludedOrderIds: excludedOrderIds) : new([], false);
    }
    public IReadOnlyList<Relic> SourceRelics(string rewardName)
    {
        var itemId = ItemIdForName(rewardName);
        return relics.Values.Where(relic => relic.Rewards.Any(reward =>
            reward.RewardName.Equals(rewardName, StringComparison.OrdinalIgnoreCase)
            || (itemId is not null && ItemIdForName(reward.RewardName) == itemId))).ToArray();
    }
    public OrderMatch MatchPersonalMarket(string name, string? excludedSellerSlug = null, IReadOnlySet<string>? excludedOrderIds = null)
    {
        // Match the default WFM sell view used for immediate whispers: sellers who are
        // currently in game. Merely-online orders can be cheaper but are not shown in that view.
        var slug = Resolve(name);
        return slug is not null && books.TryGetValue(slug, out var book) && book.FetchedAt.HasValue
            ? book.Match(null, true, Blacklist.Snapshot, excludedSellerSlug, sellerStatus: "ingame", excludedOrderIds: excludedOrderIds)
            : new([], false);
    }
    public double? PersonalMarketListingReference(string name, string? excludedSellerSlug = null, IReadOnlySet<string>? excludedOrderIds = null)
    {
        // Match the actionable WTS view first. If nobody is currently in game,
        // fall back to broader online evidence and finally the stabilized book.
        var ingame = MatchPersonalMarket(name, excludedSellerSlug, excludedOrderIds).Best?.Price;
        if (ingame.HasValue) return ingame;
        var estimate = RewardEstimate(name, excludedSellerSlug, excludedOrderIds);
        return estimate.OnlineFloor ?? estimate.Price;
    }
    public RewardPriceEstimate RewardEstimate(string name, string? excludedSellerSlug = null, IReadOnlySet<string>? excludedOrderIds = null)
    {
        var online = Match(name, null, true, excludedSellerSlug: excludedSellerSlug, excludedOrderIds: excludedOrderIds); var onlineFloor = online.Best?.Price;
        var key = name.Trim().ToLowerInvariant();
        var visible = Match(name, null, false, excludedSellerSlug: excludedSellerSlug, excludedOrderIds: excludedOrderIds).Entries;
        var median = OrderMath.RecentVisibleLowerMedian(visible);
        if (!unvaultedRewardNames.Contains(key))
            return new(onlineFloor ?? median, onlineFloor, median, visible.Count, !onlineFloor.HasValue && median.HasValue);
        return new(OrderMath.StableUnvaultedRewardPrice(visible, onlineFloor), onlineFloor, median, visible.Count, true);
    }
    public double? RewardPrice(string name, bool onlineOnly = true) => onlineOnly ? RewardEstimate(name).Price : Match(name, null, false).Best?.Price;
    public string? NameForGameRef(string gameRef) => gameRefToName.GetValueOrDefault(gameRef);
    public string? ItemIdForName(string name) => nameToId.GetValueOrDefault(name.Trim().ToLowerInvariant());
    public string? NameForItemId(string itemId) => idToName.GetValueOrDefault(itemId);
    public string? SlugForItemId(string itemId) => idToSlug.GetValueOrDefault(itemId);
    public MarketCatalogItem? CatalogItem(string itemId) => catalogById.GetValueOrDefault(itemId);
    public IReadOnlyList<string> PrimeSetNames => primeSetNames;
    public IReadOnlyList<MarketCatalogItem> ArcaneItems => arcaneItems;
    public IReadOnlyList<MarketCatalogItem> RankTenMods => catalogById.Values
        .Where(item => item.MaxRank == 10 && item.Tags.Contains("mod", StringComparer.OrdinalIgnoreCase))
        .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    public bool RegisterBook(string name)
    {
        var slug = Resolve(name); if (slug is null) return false;
        lock (requiredGate)
            if (!required.Contains(slug, StringComparer.Ordinal))
                required = required.Append(slug).Order(StringComparer.Ordinal).ToArray();
        Directory.CreateDirectory(bookCachePath);
        var book = books.GetOrAdd(slug, _ => new OrderBook());
        if (!book.FetchedAt.HasValue) book.TryRestoreCache(Path.Combine(bookCachePath, slug + ".rfob"), TimeSpan.FromMinutes(30));
        return true;
    }
    public OrderMatch MatchRank(string name, int rank, bool online, string? excludedSellerSlug = null)
    {
        var slug = Resolve(name);
        return slug is not null && books.TryGetValue(slug, out var book) && book.FetchedAt.HasValue
            ? book.Match(null, online, Blacklist.Snapshot, excludedSellerSlug, rank: rank)
            : new([], false);
    }
    public async Task<bool> EnsureBookAsync(string name, CancellationToken ct)
    {
        if (!RegisterBook(name)) return false;
        var slug = Resolve(name)!; var book = books[slug];
        if (book.FetchedAt is { } fetched && DateTimeOffset.UtcNow - fetched < TimeSpan.FromMinutes(5)) return true;
        return await FetchBookAsync(slug, ct, lowPriority: true);
    }
    public bool IsBookFresh(string name, TimeSpan maximumAge)
    {
        var slug = Resolve(name);
        return slug is not null && books.TryGetValue(slug, out var book) && book.FetchedAt is { } fetched
            && DateTimeOffset.UtcNow - fetched <= maximumAge;
    }
    public async Task RefreshBooksAsync(IEnumerable<string> itemNames, TimeSpan maximumAge, CancellationToken ct, bool lowPriority = true)
    {
        var slugs = itemNames.Select(name => Resolve(name)).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        await Parallel.ForEachAsync(slugs, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (slug, token) =>
        {
            var book = books.GetOrAdd(slug, _ => new OrderBook());
            if (book.FetchedAt is { } fetched && DateTimeOffset.UtcNow - fetched <= maximumAge) return;
            await FetchBookAsync(slug, token, lowPriority: lowPriority);
        });
    }
    public MarketSnapshot Snapshot(Refinement tier)
    {
        var now = Environment.TickCount64;
        var blacklistKey = string.Join('\0', Blacklist.Names.Order(StringComparer.Ordinal));
        var lifetime = IsBootstrapping ? 1_000 : 10_000;
        lock (snapshotGate)
        {
            if (snapshotCache.TryGetValue(tier, out var cached) && now - cached.CreatedAt < lifetime && cached.BlacklistKey == blacklistKey)
                return cached.Value;
            // Vaulted rewards use actionable online asks. Common Unvaulted rewards blend that
            // floor with the recent visible book so seller status alone cannot create price spikes.
            var prices = relics.Values.SelectMany(r => r.Rewards).Select(r => r.RewardName).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(n => n.ToLowerInvariant(), n => RewardPrice(n), StringComparer.Ordinal);
            var relicPrices = new Dictionary<string, PriceInfo>();
            foreach (var name in relics.Keys)
            {
                var on = Match(name, tier.ToString(), true, true); var off = Match(name, tier.ToString(), false, true);
                relicPrices[name] = new(on.Best?.Price, on.Best?.Quantity, on.SubtypeMatched, on.Best?.IsOutlier ?? false,
                    off.Best?.Price, off.Best?.Quantity, off.SubtypeMatched, off.Best?.IsOutlier ?? false);
            }
            var stamps = books.Values.Select(b => b.FetchedAt).OfType<DateTimeOffset>().ToArray();
            var value = new MarketSnapshot(stamps.Length > 0 ? stamps.Min() : default, tier, prices, relicPrices, ducats, ReadyBooks, required.Length);
            snapshotCache[tier] = new(now, blacklistKey, value);
            return value;
        }
    }
    public async ValueTask DisposeAsync() { await StopAsync(); cancellation?.Dispose(); lifecycle.Dispose(); }
}
