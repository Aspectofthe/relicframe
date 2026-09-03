using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RelicFrame.Core;

public sealed record RivenIndex(DateTimeOffset CompletedAt, int ScannedFamilies, int FailedSearches, RivenDeal[] Deals);

// Disk-backed auction pool: retain whole records, decode one weapon family at a time.
// The runtime owns only its GUID-named temporary directory, never a user's export folder.
public sealed class AuctionPool : IDisposable
{
    private readonly string directory;
    private readonly Dictionary<string, string> files = new(StringComparer.Ordinal);
    private long bytes;
    public AuctionPool(string runtime)
    {
        directory = Path.Combine(Path.GetFullPath(runtime), "auction-pool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }
    public void Add(IEnumerable<JsonElement> rows)
    {
        foreach (var group in rows.Where(r => r.Get("id").Text().Length > 0).GroupBy(r => r.Get("item").Get("weapon_url_name").Text()))
        {
            if (group.Key.Length == 0) continue;
            if (!files.TryGetValue(group.Key, out var path))
                files[group.Key] = path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(group.Key))) + ".jsonl");
            using var writer = new StreamWriter(path, append: true, new UTF8Encoding(false));
            foreach (var row in group)
            {
                var json = row.GetRawText(); bytes += Encoding.UTF8.GetByteCount(json) + 1;
                if (bytes > 128 * 1024 * 1024) throw new InvalidDataException("Auction scan exceeded the 128 MiB temporary disk budget; previous index retained.");
                writer.WriteLine(json);
            }
        }
    }
    public JsonElement[] Read(string slug)
    {
        if (!files.TryGetValue(slug, out var path)) return [];
        var records = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal); long retainedBytes = 0;
        foreach (var line in File.ReadLines(path))
        {
            using var doc = JsonDocument.Parse(line); var id = doc.RootElement.Get("id").Text();
            var size = Encoding.UTF8.GetByteCount(line); retainedBytes += size - sizes.GetValueOrDefault(id);
            if (retainedBytes > 8 * 1024 * 1024) throw new InvalidDataException("One weapon family exceeded the 8 MiB decoded-record budget; previous index retained.");
            sizes[id] = size; records[id] = doc.RootElement.Clone();
        }
        return records.Values.ToArray();
    }
    public void Dispose()
    {
        // Delete exact files created by this instance; no recursive operation or broad directory glob.
        foreach (var file in files.Values) if (File.Exists(file)) File.Delete(file);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false);
    }
}

public sealed class RivenMarket : IAsyncDisposable
{
    private readonly MarketHttp http;
    private readonly string runtime;
    private readonly Dictionary<string, RollRule> rules = new(StringComparer.Ordinal);
    private readonly List<string> weaponNames = [];
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private RivenIndex index = new(default, 0, 0, []);
    private string status = "Stopped; no C# scan started";
    private JsonElement[] weekly = [];
    public RivenIndex Index => Volatile.Read(ref index);
    public string Status => Volatile.Read(ref status);
    public IReadOnlyList<JsonElement> Weekly => Volatile.Read(ref weekly);
    public IReadOnlyList<string> WeaponNames => weaponNames;
    public RivenMarket(MarketHttp http, string runtime, string? rulePath = null)
    {
        this.http = http; this.runtime = Path.Combine(Path.GetFullPath(runtime), "rivens");
        if (rulePath is not null && File.Exists(rulePath))
        {
            using var file = File.OpenRead(rulePath); using var doc = JsonDocument.Parse(file);
            foreach (var row in doc.RootElement.Get("rows").Rows()) { var weapon = row.Get("weapon").Text(); if (weapon.Length == 0) continue; rules[RivenPricing.Key(weapon)] = RivenRules.Read(row); weaponNames.Add(weapon); }
        }
        var path = Path.Combine(this.runtime, "flip_index.json");
        if (File.Exists(path))
        {
            try { index = Json.Read<RivenIndex>(path) ?? index; status = "Stopped; saved index available (check age)"; }
            catch (JsonException) { status = "Saved index unreadable; start a fresh scan"; }
        }
    }
    public async Task StartAsync(CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            if (worker is { IsCompleted: false }) return;
            cancellation?.Dispose(); cancellation = new CancellationTokenSource();
            worker = Task.Run(() => LoopAsync(cancellation.Token));
        }
        finally { lifecycle.Release(); }
    }
    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            cancellation?.Cancel(); if (worker is not null) await worker;
            status = "Stopped; previous completed index retained";
        }
        finally { lifecycle.Release(); }
    }
    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await ScanAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is HttpRequestException or JsonException or InvalidDataException or IOException or TaskCanceledException)
                { status = $"Scan failed ({error.GetType().Name}); previous index retained; retry in 15 minutes"; }
                await Task.Delay(TimeSpan.FromMinutes(15), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private async Task ScanAsync(CancellationToken ct)
    {
        status = "Fetching weekly completed-trade data and weapon catalog";
        using var weeklyDoc = await http.GetJsonAsync("https://www-static.warframe.com/repos/weeklyRivensPC.json", ct, allowObjectLiteral: true);
        if (weeklyDoc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Weekly feed was not an array.");
        using var weaponsDoc = await http.GetJsonAsync("https://api.warframe.market/v2/riven/weapons", ct);
        var weapons = weaponsDoc.RootElement.Get("data").Rows().ToArray();
        if (weapons.Length == 0) throw new InvalidDataException("Empty weapon catalog.");
        weekly = weeklyDoc.RootElement.Rows().Select(r => r.Clone()).ToArray();
        Directory.CreateDirectory(runtime);
        var envelope = new { fetched_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), platform = "pc", rows = weekly };
        Json.WriteAtomic(Path.Combine(runtime, "latest.json"), envelope);
        var history = Path.Combine(runtime, "history"); Directory.CreateDirectory(history);
        var snapshot = Path.Combine(history, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd") + ".json");
        var historyBytes = Directory.EnumerateFiles(history, "*.json").Sum(p => new FileInfo(p).Length);
        var newBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Json.Options).Length;
        if (historyBytes - (File.Exists(snapshot) ? new FileInfo(snapshot).Length : 0) + newBytes <= 32 * 1024 * 1024) Json.WriteAtomic(snapshot, envelope);
        using var pool = new AuctionPool(runtime);
        var searches = RivenPricing.Bases.Keys.Where(s => s != "chance_to_gain_combo_count").Order(StringComparer.Ordinal)
            .SelectMany(s => new[] { (Stat: s, Sort: "price_asc"), (Stat: s, Sort: "price_desc") }).ToArray();
        var failures = 0;
        // Sequential downloads share the application's 5/sec budget with relic prices; no request storm.
        for (var i = 0; i < searches.Length; i++)
        {
            ct.ThrowIfCancellationRequested(); status = $"Auction searches {i + 1}/{searches.Length} ({failures} failed)";
            try
            {
                using var doc = await http.GetJsonAsync($"https://api.warframe.market/v1/auctions/search?type=riven&positive_stats={Uri.EscapeDataString(searches[i].Stat)}&sort_by={searches[i].Sort}&buyout_policy=direct", ct);
                var rows = doc.RootElement.Get("payload").Get("auctions");
                if (rows.ValueKind != JsonValueKind.Array) throw new JsonException("Missing auction array.");
                pool.Add(rows.Rows());
            }
            catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or JsonException or TaskCanceledException) { failures++; }
        }
        if (failures == searches.Length) throw new HttpRequestException("Every auction search failed.");
        var prices = weekly.Select(r => RivenPricing.ParseWeekly([r], r.Get("compatibility").Text(), r.Get("rerolled").Bool()))
            .OfType<WeeklyPrice>().GroupBy(p => RivenPricing.Key(p.Weapon)).ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Popularity).ThenByDescending(p => p.Rerolled).First());
        var deals = new List<RivenDeal>(); var scanned = 0;
        foreach (var weapon in weapons)
        {
            ct.ThrowIfCancellationRequested(); var slug = weapon.Get("slug").Text(); if (slug.Length == 0) continue;
            var name = weapon.Get("i18n").Get("en").Get("name").Text(slug);
            status = $"Evaluating weapon families {scanned + 1}/{weapons.Length}; {deals.Count} candidates";
            var auctions = pool.Read(slug); var key = RivenPricing.Key(name);
            rules.TryGetValue(key, out var rule); prices.TryGetValue(key, out var price);
            deals.AddRange(RivenPricing.FindDeals(auctions, slug, name, price, RivenPricing.StatClass(weapon), weapon.Get("disposition").Number(),
                minimumDiscountPct: 10, onlineOnly: false, limit: auctions.Length, rollRule: rule, curatedOnly: true,
                curatedProfile: rule?.PositiveExpression ?? "", curatedNotes: rule?.Notes ?? "", ct: ct));
            scanned++;
        }
        var result = new RivenIndex(DateTimeOffset.UtcNow, scanned, failures,
            RivenPricing.Sort(deals).ThenByDescending(d => d.ComparableCount).ToArray());
        Json.WriteAtomic(Path.Combine(runtime, "flip_index.json"), result); Volatile.Write(ref index, result);
        status = $"Complete: {scanned} families, {result.Deals.Length} candidates, {failures} failed searches; next scan in 15 minutes";
    }
    public async ValueTask DisposeAsync() { await StopAsync(); cancellation?.Dispose(); lifecycle.Dispose(); }
}
