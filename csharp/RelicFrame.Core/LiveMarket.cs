using System.Collections.Concurrent;
using System.Text.Json;

namespace RelicFrame.Core;

public sealed record MarketSnapshot(DateTimeOffset FetchedAt, Refinement Refinement,
    IReadOnlyDictionary<string, double?> Prices, IReadOnlyDictionary<string, PriceInfo> RelicPrices,
    IReadOnlyDictionary<string, int> Ducats, int ReadyBooks, int TotalBooks);

public sealed class LiveMarket : IAsyncDisposable
{
    private readonly MarketHttp http;
    private readonly IReadOnlyDictionary<string, Relic> relics;
    private readonly ConcurrentDictionary<string, OrderBook> books = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> names = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, int> ducats = new Dictionary<string, int>();
    private IReadOnlyDictionary<string, string> idToSlug = new Dictionary<string, string>();
    private string[] required = [];
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private readonly string catalogPath;
    private readonly bool enableWebSocket;
    private WfmWebSocket? webSocket;
    private string status = "stopped";
    public SellerBlacklist Blacklist { get; }
    public string Status => Volatile.Read(ref status);
    public int ReadyBooks => books.Values.Count(b => b.FetchedAt.HasValue);
    public int TotalBooks => required.Length;
    public string WebSocketStatus => !enableWebSocket ? "disabled" : webSocket?.Connected == true ? "connected" : "reconnecting/REST-only";
    public LiveMarket(MarketHttp http, IReadOnlyDictionary<string, Relic> relics, string runtimePath, bool enableWebSocket = false)
    { this.http = http; this.relics = relics; this.enableWebSocket = enableWebSocket; catalogPath = Path.Combine(runtimePath, "catalog.json"); Blacklist = new(Path.Combine(runtimePath, "seller_blacklist.json")); }
    public async Task StartAsync(bool forceCatalog, CancellationToken lifetime)
    {
        await lifecycle.WaitAsync(lifetime);
        try
        {
            if (worker is { IsCompleted: false }) return;
            cancellation?.Dispose(); cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
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
            var nextNames = new Dictionary<string, string>(); var nextDucats = new Dictionary<string, int>(); var nextIds = new Dictionary<string, string>();
            foreach (var row in doc.RootElement.Get("data").Rows())
            {
                var name = row.Get("i18n").Get("en").Get("name").Text().Trim().ToLowerInvariant();
                var slug = row.Get("slug").Text(); if (name.Length == 0 || slug.Length == 0) continue;
                nextNames[name] = slug; var id = row.Get("id").Text(); if (id.Length > 0) nextIds[id] = slug; if (row.Get("ducats").Number() is { } d) nextDucats[name] = (int)d;
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
            catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or JsonException or TaskCanceledException)
            { Console.WriteLine($"[ducats] {e.GetType().Name}; cached values retained where available"); }
            names = nextNames; ducats = nextDucats; idToSlug = nextIds;
            required = relics.Values.SelectMany(r => r.Rewards.Select(reward => Resolve(reward.RewardName)).Append(Resolve(r.RelicName, true)))
                .Where(s => s is not null).Select(s => s!).Distinct().Order(StringComparer.Ordinal).ToArray();
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
            await LoadCatalogAsync(force, ct);
            webSocket = new(idToSlug, required.ToHashSet(StringComparer.Ordinal), (slug, order) => books.GetOrAdd(slug, _ => new OrderBook()).ApplyCreated(order));
            var webSocketTask = enableWebSocket && idToSlug.Count > 0 ? webSocket.RunAsync(ct) : Task.CompletedTask;
            var failures = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            Volatile.Write(ref status, "refreshing");
            await Parallel.ForEachAsync(required, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (slug, token) =>
            {
                if (await FetchBookAsync(slug, token)) failures.TryRemove(slug, out _); else failures[slug] = "fetch failed";
            });
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
        catch (Exception e) { Volatile.Write(ref status, "failed: " + e.GetType().Name); Console.WriteLine($"[market] worker failed: {e.GetType().Name}"); }
    }
    private async Task<bool> FetchBookAsync(string slug, CancellationToken ct)
    {
        try
        {
            using var doc = await http.GetJsonAsync("https://api.warframe.market/v2/orders/item/" + Uri.EscapeDataString(slug), ct);
            var rows = doc.RootElement.Get("data"); if (rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Missing order array");
            books.GetOrAdd(slug, _ => new OrderBook()).Replace(rows); return true;
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException)
        { Console.WriteLine($"[market] {slug}: {e.GetType().Name}; old book retained, if any"); return false; }
    }
    public OrderMatch Match(string name, string? tier, bool online, bool relic = false)
    {
        var slug = Resolve(name, relic);
        return slug is not null && books.TryGetValue(slug, out var book) && book.FetchedAt.HasValue ? book.Match(tier, online, Blacklist.Snapshot) : new([], false);
    }
    public MarketSnapshot Snapshot(Refinement tier)
    {
        var prices = relics.Values.SelectMany(r => r.Rewards).Select(r => r.RewardName).Distinct()
            .ToDictionary(n => n.ToLowerInvariant(), n => Match(n, null, false).Best?.Price);
        var relicPrices = new Dictionary<string, PriceInfo>();
        foreach (var name in relics.Keys)
        {
            var on = Match(name, tier.ToString(), true, true); var off = Match(name, tier.ToString(), false, true);
            relicPrices[name] = new(on.Best?.Price, on.Best?.Quantity, on.SubtypeMatched, on.Best?.IsOutlier ?? false,
                off.Best?.Price, off.Best?.Quantity, off.SubtypeMatched, off.Best?.IsOutlier ?? false);
        }
        var stamps = books.Values.Select(b => b.FetchedAt).OfType<DateTimeOffset>().ToArray();
        return new(stamps.Length > 0 ? stamps.Min() : default, tier, prices, relicPrices, ducats, ReadyBooks, required.Length);
    }
    public async ValueTask DisposeAsync() { await StopAsync(); cancellation?.Dispose(); lifecycle.Dispose(); }
}
