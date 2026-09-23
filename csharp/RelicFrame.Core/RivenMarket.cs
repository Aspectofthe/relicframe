using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace RelicFrame.Core;

public sealed record RivenIndex(DateTimeOffset CompletedAt, int ScannedFamilies, int FailedSearches, RivenDeal[] Deals,
    int ModelVersion = 8, RivenEndoDeal[]? EndoDeals = null, int CuratedProfiles = 0, int FallbackProfiles = 0,
    RivenDesiredRollSummary[]? DesiredRolls = null);
public sealed record RivenDesiredRollSummary(string WeaponName, string WeaponSlug, string StatClass, double Disposition,
    string PositiveExpression, string[] HarmlessNegatives, string ProfileSource, string ProfileNotes,
    RivenAskRange DesiredAsks, WeeklyPrice? Unrolled, WeeklyPrice? Rolled);
public sealed record RivenWeaponOverview(string Name, string Slug, string Type, double? Disposition,
    WeeklyPrice? Unrolled, WeeklyPrice? Rolled, int LiveAsks, int OnlineAsks, double? LowestOnlineAsk);
public sealed record RivenAppraisalForm(string WeaponName, string WeaponSlug, string StatClass, double Disposition,
    string[] PositiveStats, string[] NegativeStats, string ProfileSource, string PositiveExpression,
    string[] HarmlessNegatives, string ProfileNotes);
public sealed record RivenAuctionObservation(string AuctionId, string WeaponSlug, string RollSignature, int Price,
    DateTimeOffset ListedAt, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, DateTimeOffset? ClosedObservedAt);
public sealed record RivenRecordedOutcome(string Id, string WeaponName, string WeaponSlug, string RollSignature,
    string Outcome, int? Price, DateTimeOffset? ListedAt, DateTimeOffset RecordedAt, DateTimeOffset? SoldAt, string ReporterHash);

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
    private string[] weaponNames = [];
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim references = new(1, 1);
    private readonly SemaphoreSlim observationsLock = new(1, 1);
    private readonly Dictionary<string, RivenAuctionObservation> observations = new(StringComparer.Ordinal);
    private DateTimeOffset lastObservationSave;
    private bool observationsDirty;
    private readonly List<RivenRecordedOutcome> recordedOutcomes = [];
    private readonly ConcurrentDictionary<string, (DateTimeOffset FetchedAt, JsonElement[] Rows)> auctionCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> auctionFetchLocks = new(StringComparer.Ordinal);
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private RivenIndex index = new(default, 0, 0, []);
    private string status = "Stopped; no C# scan started";
    private readonly object progressGate = new();
    private string progressStage = "stopped";
    private int progressCurrent;
    private int progressTotal;
    private DateTimeOffset progressDeadline;
    private JsonElement[] weekly = [];
    private JsonElement[] weapons = [];
    private JsonElement[] variants = [];
    private string[] marketplaceAttributes = [];
    public RivenIndex Index => Volatile.Read(ref index);
    public string Status => Volatile.Read(ref status);
    public TimeSpan? EstimatedRemaining
    {
        get
        {
            lock (progressGate)
            {
                if (progressTotal <= 0 || progressCurrent >= progressTotal) return null;
                var remaining = progressDeadline - DateTimeOffset.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : null;
            }
        }
    }
    public string ProgressStage { get { lock (progressGate) return progressStage; } }
    public IReadOnlyList<JsonElement> Weekly => Volatile.Read(ref weekly);
    public IReadOnlyList<string> WeaponNames => Volatile.Read(ref weaponNames);
    public RivenMarket(MarketHttp http, string runtime, string? rulePath = null)
    {
        var knownNames = new List<string>();
        this.http = http; this.runtime = Path.Combine(Path.GetFullPath(runtime), "rivens");
        if (rulePath is not null && File.Exists(rulePath))
        {
            using var file = File.OpenRead(rulePath); using var doc = JsonDocument.Parse(file);
            foreach (var row in doc.RootElement.Get("rows").Rows()) { var weapon = row.Get("weapon").Text(); if (weapon.Length == 0) continue; rules[RivenPricing.Key(weapon)] = RivenRules.Read(row); knownNames.Add(weapon); }
        }
        var variantPath = rulePath is null ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rulePath))!, "weapon_variants.json");
        if (variantPath is not null && File.Exists(variantPath))
        {
            try
            {
                using var file = File.OpenRead(variantPath); using var document = JsonDocument.Parse(file);
                variants = document.RootElement.Get("rows").Rows().Select(row => row.Clone()).ToArray();
                knownNames.AddRange(variants.Select(row => row.Get("name").Text()).Where(name => name.Length > 0));
            }
            catch (JsonException) { }
        }
        var path = Path.Combine(this.runtime, "flip_index.json");
        if (File.Exists(path))
        {
            try
            {
                var saved = Json.Read<RivenIndex>(path);
                if (saved.ModelVersion == 8) { index = saved; status = "Stopped; saved index available (check age)"; }
                else status = "Saved flips use the retired rule model; rebuilding automatically";
            }
            catch (JsonException) { status = "Saved index unreadable; start a fresh scan"; }
        }
        var observationsPath = Path.Combine(this.runtime, "auction_observations.json");
        if (File.Exists(observationsPath))
        {
            try
            {
                foreach (var row in Json.Read<RivenAuctionObservation[]>(observationsPath) ?? []) observations[row.AuctionId] = row;
                lastObservationSave = File.GetLastWriteTimeUtc(observationsPath);
            }
            catch (JsonException) { }
        }
        var outcomePath = Path.Combine(this.runtime, "recorded_outcomes.json");
        if (File.Exists(outcomePath)) try { recordedOutcomes.AddRange(Json.Read<RivenRecordedOutcome[]>(outcomePath)); } catch (JsonException) { }
        weaponNames = knownNames.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public async Task StartAsync(CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            if (worker is { IsCompleted: false }) return;
            cancellation?.Dispose(); cancellation = new CancellationTokenSource();
            SetProgress("starting", 0, 0);
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
            await PersistObservationsAsync(CancellationToken.None, force: true);
            status = "Stopped; previous completed index retained";
            SetProgress("stopped", 1, 1);
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
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Console.WriteLine($"[riven-scan] {error}");
                    status = $"Scan failed ({error.GetType().Name}); previous index retained; retry in 15 minutes";
                    SetProgress("retry wait", 1, 1);
                }
                await Task.Delay(TimeSpan.FromMinutes(15), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private async Task ScanAsync(CancellationToken ct)
    {
        lock (progressGate) progressDeadline = DateTimeOffset.UtcNow.AddMinutes(7);
        SetProgress("reference feeds", 0, 3);
        status = "Fetching legacy DE trade archive and current weapon catalog";
        using var weeklyDoc = await http.GetJsonAsync("https://www-static.warframe.com/repos/weeklyRivensPC.json", ct, allowObjectLiteral: true, lowPriority: true);
        SetProgress("reference feeds", 1, 3);
        if (weeklyDoc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Weekly feed was not an array.");
        using var weaponsDoc = await http.GetJsonAsync("https://api.warframe.market/v2/riven/weapons", ct, lowPriority: true);
        SetProgress("reference feeds", 2, 3);
        var nextMarketplaceAttributes = await FetchMarketplaceAttributesAsync(ct, lowPriority: true);
        SetProgress("reference feeds", 3, 3);
        var catalog = weaponsDoc.RootElement.Get("data").Rows().ToArray();
        if (catalog.Length == 0) throw new InvalidDataException("Empty weapon catalog.");
        weekly = weeklyDoc.RootElement.Rows().Select(r => r.Clone()).ToArray();
        weapons = catalog.Select(r => r.Clone()).ToArray();
        marketplaceAttributes = nextMarketplaceAttributes;
        PublishWeaponNames(catalog);
        Directory.CreateDirectory(runtime);
        var envelope = new { fetched_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), platform = "pc", rows = weekly };
        Json.WriteAtomic(Path.Combine(runtime, "latest.json"), envelope);
        var history = Path.Combine(runtime, "history"); Directory.CreateDirectory(history);
        var snapshot = Path.Combine(history, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd") + ".json");
        var historyBytes = Directory.EnumerateFiles(history, "*.json").Sum(p => new FileInfo(p).Length);
        var newBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Json.Options).Length;
        if (historyBytes - (File.Exists(snapshot) ? new FileInfo(snapshot).Length : 0) + newBytes <= 32 * 1024 * 1024) Json.WriteAtomic(snapshot, envelope);
        using var pool = new AuctionPool(runtime);
        var searches = RivenPricing.Bases.Keys.Concat(marketplaceAttributes.Where(RivenPricing.CombinedStats.Contains))
            .Distinct(StringComparer.Ordinal).Where(s => s != "chance_to_gain_combo_count").Order(StringComparer.Ordinal)
            .SelectMany(s => new[] { (Stat: s, Sort: "price_asc"), (Stat: s, Sort: "price_desc") }).ToArray();
        var failures = 0;
        // Sequential downloads share the application's 5/sec budget with relic prices; no request storm.
        SetProgress("auction searches", 0, searches.Length);
        for (var i = 0; i < searches.Length; i++)
        {
            ct.ThrowIfCancellationRequested(); status = $"Auction searches {i + 1}/{searches.Length} ({failures} failed)";
            try
            {
                using var doc = await http.GetJsonAsync($"https://api.warframe.market/v1/auctions/search?type=riven&positive_stats={Uri.EscapeDataString(searches[i].Stat)}&sort_by={searches[i].Sort}&buyout_policy=direct", ct, lowPriority: true);
                var rows = doc.RootElement.Get("payload").Get("auctions");
                if (rows.ValueKind != JsonValueKind.Array) throw new JsonException("Missing auction array.");
                pool.Add(rows.Rows());
            }
            catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or JsonException or TaskCanceledException) { failures++; }
            finally { SetProgress("auction searches", i + 1, searches.Length); }
        }
        if (failures == searches.Length) throw new HttpRequestException("Every auction search failed.");
        var prices = weekly.Select(r => RivenPricing.ParseWeekly([r], r.Get("compatibility").Text(), r.Get("rerolled").Bool()))
            .OfType<WeeklyPrice>().GroupBy(p => RivenPricing.Key(p.Weapon)).ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Popularity).ThenByDescending(p => p.Rerolled).First());
        var deals = new List<RivenDeal>(); var endoDeals = new List<RivenEndoDeal>(); var desiredRolls = new List<RivenDesiredRollSummary>(); var scanned = 0;
        var curatedProfiles = 0; var fallbackProfiles = 0;
        SetProgress("weapon evaluation", 0, catalog.Length);
        for (var weaponIndex = 0; weaponIndex < catalog.Length; weaponIndex++)
        {
            var weapon = catalog[weaponIndex];
            ct.ThrowIfCancellationRequested(); var slug = weapon.Get("slug").Text();
            if (slug.Length == 0) { SetProgress("weapon evaluation", weaponIndex + 1, catalog.Length); continue; }
            var name = weapon.Get("i18n").Get("en").Get("name").Text(slug);
            status = $"Evaluating weapon families {scanned + 1}/{catalog.Length}; {deals.Count} candidates";
            var auctions = pool.Read(slug); var key = RivenPricing.Key(name);
            endoDeals.AddRange(auctions.Select(auction =>
            {
                var auctionPrice = RivenPricing.AuctionPrice(auction); if (!auctionPrice.HasValue) return null;
                int endo; try { endo = RivenPricing.EndoValue(auction); } catch (ArgumentOutOfRangeException) { return null; }
                if (endo <= 0) return null; var owner = auction.Get("owner"); var seller = owner.Get("ingame_name").Text(owner.Get("slug").Text("Unknown"));
                return new RivenEndoDeal(auction.Get("id").Text(), slug, name, auctionPrice.Value, endo, auctionPrice.Value * 1000d / endo,
                    seller, owner.Get("slug").Text(seller), owner.Get("status").Text("offline"));
            }).OfType<RivenEndoDeal>().OrderBy(d => d.PlatPerThousandEndo).Take(5));
            prices.TryGetValue(key, out var price);
            var (rule, profileSource) = ResolveDesirabilityRule(name, auctions, RivenPricing.StatClass(weapon), weapon.Get("disposition").Number() ?? 1);
            if (profileSource == "curated weapon profile") curatedProfiles++; else fallbackProfiles++;
            var statClass = RivenPricing.StatClass(weapon); var disposition = weapon.Get("disposition").Number() ?? 1;
            desiredRolls.Add(new(name, slug, statClass, disposition, rule.PositiveExpression, rule.HarmlessNegatives,
                profileSource, rule.Notes, RivenPricing.DesiredAskRange(auctions, name, statClass, disposition, rule),
                RivenPricing.ParseWeekly(weekly, name, false), RivenPricing.ParseWeekly(weekly, name, true)));
            deals.AddRange(RivenPricing.FindDeals(auctions, slug, name, price, RivenPricing.StatClass(weapon), weapon.Get("disposition").Number(),
                minimumDiscountPct: 10, onlineOnly: false, limit: auctions.Length, rollRule: rule, curatedOnly: true,
                curatedProfile: rule.PositiveExpression, curatedNotes: $"{profileSource}: {rule.Notes}", ct: ct));
            scanned++;
            SetProgress("weapon evaluation", weaponIndex + 1, catalog.Length);
        }
        string[] watched;
        await observationsLock.WaitAsync(ct);
        try { watched = observations.Values.GroupBy(o => o.WeaponSlug).OrderByDescending(g => g.Max(o => o.LastSeenAt)).Take(50).Select(g => g.Key).ToArray(); }
        finally { observationsLock.Release(); }
        SetProgress("listing lifecycle refresh", 0, watched.Length);
        for (var i = 0; i < watched.Length; i++)
        {
            ct.ThrowIfCancellationRequested(); status = $"Refreshing observed listing lifecycles {i + 1}/{watched.Length}";
            try { await FetchAuctionsAsync(watched[i], ct, force: true, lowPriority: true); }
            catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or JsonException or TaskCanceledException or IOException) { failures++; }
            finally { SetProgress("listing lifecycle refresh", i + 1, watched.Length); }
        }
        await PersistObservationsAsync(ct, force: true);
        var result = new RivenIndex(DateTimeOffset.UtcNow, scanned, failures,
            RivenPricing.Sort(deals).ThenByDescending(d => d.ComparableCount).ToArray(), 8,
            endoDeals.DistinctBy(d => d.AuctionId).OrderBy(d => d.PlatPerThousandEndo).ThenByDescending(d => d.Endo).Take(1000).ToArray(),
            curatedProfiles, fallbackProfiles, desiredRolls.OrderBy(row => row.WeaponName, StringComparer.OrdinalIgnoreCase).ToArray());
        Json.WriteAtomic(Path.Combine(runtime, "flip_index.json"), result); Volatile.Write(ref index, result);
        status = $"Complete: {scanned} families ({curatedProfiles} curated, {fallbackProfiles} fallback), {result.Deals.Length} candidates, {failures} failed searches; next scan in 15 minutes";
        SetProgress("complete", 1, 1);
    }
    private void SetProgress(string stage, int current, int total)
    {
        lock (progressGate)
        {
            progressStage = stage; progressCurrent = Math.Max(0, current); progressTotal = Math.Max(0, total);
        }
    }
    private async Task EnsureReferencesAsync(CancellationToken ct)
    {
        if (weekly.Length > 0 && weapons.Length > 0) return;
        await references.WaitAsync(ct);
        try
        {
            if (weekly.Length > 0 && weapons.Length > 0) return;
            using var weeklyDoc = await http.GetJsonAsync("https://www-static.warframe.com/repos/weeklyRivensPC.json", ct, allowObjectLiteral: true);
            using var weaponsDoc = await http.GetJsonAsync("https://api.warframe.market/v2/riven/weapons", ct);
            var nextMarketplaceAttributes = await FetchMarketplaceAttributesAsync(ct, lowPriority: false);
            var nextWeekly = weeklyDoc.RootElement.Rows().Select(r => r.Clone()).ToArray();
            var nextWeapons = weaponsDoc.RootElement.Get("data").Rows().Select(r => r.Clone()).ToArray();
            if (nextWeekly.Length == 0 || nextWeapons.Length == 0) throw new InvalidDataException("Riven reference feeds were empty.");
            Volatile.Write(ref weekly, nextWeekly); Volatile.Write(ref weapons, nextWeapons);
            Volatile.Write(ref marketplaceAttributes, nextMarketplaceAttributes);
            PublishWeaponNames(nextWeapons);
        }
        finally { references.Release(); }
    }
    private async Task<string[]> FetchMarketplaceAttributesAsync(CancellationToken ct, bool lowPriority)
    {
        try
        {
            using var document = await http.GetJsonAsync("https://api.warframe.market/v2/riven/attributes", ct, lowPriority: lowPriority);
            return ReadMarketplaceAttributes(document.RootElement);
        }
        catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or JsonException or TaskCanceledException)
        {
            Console.WriteLine($"[riven-attributes] {error.GetType().Name}: keeping the previous attribute catalog");
            return Volatile.Read(ref marketplaceAttributes);
        }
    }
    private static string[] ReadMarketplaceAttributes(JsonElement root) => root.Get("data").Rows()
        .Select(row => row.Get("slug").Text(row.Get("urlName").Text(row.Get("url_name").Text())))
        .Where(value => value.Length > 0).Select(value =>
        {
            try { return RivenPricing.NormalizeStat(value); }
            catch (ArgumentException) { return ""; }
        }).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    private void PublishWeaponNames(IEnumerable<JsonElement> catalog)
    {
        var marketNames = catalog.Select(row => row.Get("i18n").Get("en").Get("name").Text()).Where(name => name.Length > 0);
        Volatile.Write(ref weaponNames, weaponNames.Concat(marketNames).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }
    private sealed record ResolvedWeapon(JsonElement Market, string Name, string Family, double? Disposition, string Type, string StatClass);
    private (RollRule Rule, string Source) ResolveDesirabilityRule(string family, IEnumerable<JsonElement> auctions, string statClass, double disposition)
    {
        if (rules.TryGetValue(RivenPricing.Key(family), out var curated))
        {
            var safe = ApplyWeaponSafety(family, curated, statClass);
            if (safe.Alternatives.Count > 0) return (safe, "curated weapon profile");
            var fallback = RivenPricing.LearnMarketRule(auctions, statClass, disposition);
            return (fallback with { Notes = $"The curated profile had no legal {statClass} alternative, so a conservative class/market fallback was used. {fallback.Notes}" },
                "market/class fallback (incompatible curated profile)");
        }
        return (RivenPricing.LearnMarketRule(auctions, statClass, disposition), "market/class fallback");
    }
    private RollRule ApplyWeaponSafety(string family, RollRule rule, string statClass)
    {
        var legalPositives = RivenPricing.AllowedStats(statClass, true).ToHashSet(StringComparer.Ordinal);
        var legalNegatives = RivenPricing.AllowedStats(statClass, false).ToHashSet(StringComparer.Ordinal);
        var removed = new HashSet<string>(StringComparer.Ordinal);
        var alternatives = new List<RuleAlternative>();
        foreach (var alternative in rule.Alternatives)
        {
            var mandatory = alternative.Mandatory.Select(group => group.Where(legalPositives.Contains).Distinct(StringComparer.Ordinal).ToArray()).ToArray();
            foreach (var stat in alternative.Mandatory.SelectMany(group => group).Concat(alternative.Pool).Where(stat => !legalPositives.Contains(stat))) removed.Add(stat);
            if (mandatory.Any(group => group.Length == 0)) continue;
            var pool = alternative.Pool.Where(legalPositives.Contains).Distinct(StringComparer.Ordinal).ToArray();
            if (mandatory.SelectMany(group => group).Concat(pool).Distinct(StringComparer.Ordinal).Count() >= 2)
                alternatives.Add(new(mandatory, pool));
        }
        var negatives = rule.HarmlessNegatives.Where(legalNegatives.Contains).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var stat in rule.HarmlessNegatives.Where(stat => !legalNegatives.Contains(stat))) removed.Add(stat);
        var key = RivenPricing.Key(family);
        var sensitiveClasses = new HashSet<string>(["nikana", "dagger", "dual daggers", "claws", "sword", "fist", "scythe"], StringComparer.OrdinalIgnoreCase);
        var finisherSensitive = variants.Any(row =>
            (RivenPricing.Key(row.Get("family").Text()) == key || RivenPricing.Key(row.Get("name").Text()) == key) &&
            sensitiveClasses.Any(kind => row.Get("class").Text().Contains(kind, StringComparison.OrdinalIgnoreCase)));
        var notes = new List<string>();
        if (rule.Notes.Length > 0) notes.Add(rule.Notes.Trim());
        if (finisherSensitive && negatives.Contains("finisher_damage"))
        {
            negatives = negatives.Where(stat => stat != "finisher_damage").ToArray();
            notes.Add("Finisher Damage is excluded from harmless negatives for this weapon class because finishers or Melee Crescendo may matter.");
        }
        if (removed.Count > 0)
            notes.Add("Profile attributes unavailable to this weapon class were ignored: " + string.Join(", ", removed.Select(RivenPricing.DisplayName).Order(StringComparer.OrdinalIgnoreCase)) + ".");
        return rule with { Alternatives = alternatives, HarmlessNegatives = negatives, Notes = string.Join(' ', notes) };
    }
    private ResolvedWeapon ResolveWeapon(string query)
    {
        var wanted = RivenPricing.Key(query);
        var exact = weapons.Where(w => wanted == RivenPricing.Key(w.Get("slug").Text()) || wanted == RivenPricing.Key(w.Get("i18n").Get("en").Get("name").Text())).ToArray();
        var matches = exact.Length > 0 ? exact : weapons.Where(w => wanted.Length > 0 && RivenPricing.Key(w.Get("i18n").Get("en").Get("name").Text()).Contains(wanted, StringComparison.Ordinal)).Take(2).ToArray();
        if (matches.Length == 1)
        {
            var market = matches[0]; var name = market.Get("i18n").Get("en").Get("name").Text(market.Get("slug").Text());
            return new(market, name, name, market.Get("disposition").Number(), market.Get("rivenType").Text("unknown"), RivenPricing.StatClass(market));
        }
        var variantExact = variants.Where(v => wanted == RivenPricing.Key(v.Get("name").Text())).ToArray();
        var variantMatches = variantExact.Length > 0 ? variantExact : variants.Where(v => wanted.Length > 0 && RivenPricing.Key(v.Get("name").Text()).Contains(wanted, StringComparison.Ordinal)).Take(2).ToArray();
        if (variantMatches.Length != 1) throw new ArgumentException($"I couldn't uniquely match the weapon '{query}'.");
        var variant = variantMatches[0]; var family = variant.Get("family").Text();
        var familyMatches = weapons.Where(w => RivenPricing.Key(w.Get("i18n").Get("en").Get("name").Text()) == RivenPricing.Key(family)).ToArray();
        if (familyMatches.Length != 1) throw new ArgumentException($"'{query}' is known, but its Riven family '{family}' is unavailable in the marketplace catalog.");
        var slot = variant.Get("slot").Text().ToLowerInvariant(); var kind = variant.Get("class").Text().ToLowerInvariant();
        var statClass = slot.Contains("archgun") ? "archgun" : slot == "melee" ? "melee" : kind.Contains("shotgun") ? "shotgun" : slot == "secondary" ? "pistol" : "rifle";
        return new(familyMatches[0], variant.Get("name").Text(family), family, variant.Get("disposition").Number(), variant.Get("class").Text(slot), statClass);
    }
    public string? TryGetStatClass(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || Volatile.Read(ref weapons).Length == 0) return null;
        try { return ResolveWeapon(query).StatClass; }
        catch (ArgumentException) { return null; }
    }
    private async Task<JsonElement[]> FetchAuctionsAsync(string slug, CancellationToken ct, bool force = false, bool lowPriority = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && auctionCache.TryGetValue(slug, out var cached) && now - cached.FetchedAt < TimeSpan.FromSeconds(30)) return cached.Rows;
        var gate = auctionFetchLocks.GetOrAdd(slug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (!force && auctionCache.TryGetValue(slug, out cached) && now - cached.FetchedAt < TimeSpan.FromSeconds(30)) return cached.Rows;
            using var document = await http.GetJsonAsync($"https://api.warframe.market/v1/auctions/search?type=riven&weapon_url_name={Uri.EscapeDataString(slug)}&sort_by=price_asc&buyout_policy=direct", ct, lowPriority: lowPriority);
            var auctions = document.RootElement.Get("payload").Get("auctions");
            if (auctions.ValueKind != JsonValueKind.Array) throw new JsonException("Missing auction array.");
            var rows = auctions.Rows().Select(row => row.Clone()).ToArray();
            await ObserveAsync(slug, rows, ct, persist: !force);
            auctionCache[slug] = (DateTimeOffset.UtcNow, rows);
            foreach (var stale in auctionCache.Where(pair => DateTimeOffset.UtcNow - pair.Value.FetchedAt > TimeSpan.FromMinutes(2)).ToArray())
                auctionCache.TryRemove(stale.Key, out _);
            if (auctionCache.Count > 64) foreach (var old in auctionCache.OrderBy(pair => pair.Value.FetchedAt).Take(auctionCache.Count - 64).ToArray())
                auctionCache.TryRemove(old.Key, out _);
            return rows;
        }
        finally { gate.Release(); }
    }
    private async Task ObserveAsync(string weaponSlug, JsonElement[] auctions, CancellationToken ct, bool persist)
    {
        await observationsLock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow; var active = new HashSet<string>(StringComparer.Ordinal);
            foreach (var auction in auctions)
            {
                var id = auction.Get("id").Text(); var price = RivenPricing.AuctionPrice(auction); if (id.Length == 0 || !price.HasValue) continue;
                active.Add(id); var attrs = RivenPricing.Attributes(auction).ToArray();
                var signature = RivenPricing.Signature(attrs.Where(a => a.Positive).Select(a => a.Slug), attrs.FirstOrDefault(a => !a.Positive).Slug);
                var listed = DateTimeOffset.TryParse(auction.Get("created").Text(), out var parsed) ? parsed : now;
                observations[id] = observations.TryGetValue(id, out var old)
                    ? old with { Price = price.Value, RollSignature = signature, LastSeenAt = now, ClosedObservedAt = null }
                    : new(id, weaponSlug, signature, price.Value, listed, now, now, null);
            }
            foreach (var pair in observations.Where(p => p.Value.WeaponSlug == weaponSlug && p.Value.ClosedObservedAt is null && !active.Contains(p.Key)).ToArray())
                observations[pair.Key] = pair.Value with { ClosedObservedAt = now };
            var retained = observations.Values.OrderByDescending(o => o.LastSeenAt).Take(50_000).ToArray();
            observations.Clear(); foreach (var row in retained) observations[row.AuctionId] = row;
            observationsDirty = true;
            if (persist && now - lastObservationSave >= TimeSpan.FromMinutes(1)) SaveObservationsUnsafe(now);
        }
        finally { observationsLock.Release(); }
    }
    private void SaveObservationsUnsafe(DateTimeOffset now)
    {
        if (!observationsDirty) return;
        Directory.CreateDirectory(runtime);
        Json.WriteAtomic(Path.Combine(runtime, "auction_observations.json"), observations.Values.OrderByDescending(row => row.LastSeenAt).ToArray());
        observationsDirty = false; lastObservationSave = now;
    }
    private async Task PersistObservationsAsync(CancellationToken ct, bool force = false)
    {
        await observationsLock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (force || now - lastObservationSave >= TimeSpan.FromMinutes(1)) SaveObservationsUnsafe(now);
        }
        finally { observationsLock.Release(); }
    }
    public async Task<RivenWeaponOverview> GetWeaponOverviewAsync(string query, CancellationToken ct = default)
    {
        await EnsureReferencesAsync(ct); var resolved = ResolveWeapon(query); var weapon = resolved.Market;
        var slug = weapon.Get("slug").Text(); var name = resolved.Name;
        var auctions = await FetchAuctionsAsync(slug, ct);
        var asks = auctions.Select(row => (Price: RivenPricing.AuctionPrice(row), Status: row.Get("owner").Get("status").Text().ToLowerInvariant()))
            .Where(row => row.Price.HasValue).ToArray();
        var online = asks.Where(row => row.Status is "online" or "ingame").ToArray();
        return new(name, slug, resolved.Type, resolved.Disposition,
            RivenPricing.ParseWeekly(weekly, resolved.Family, false), RivenPricing.ParseWeekly(weekly, resolved.Family, true), asks.Length, online.Length,
            online.Select(row => row.Price).Min());
    }
    public async Task<RivenAppraisalForm> GetAppraisalFormAsync(string query, CancellationToken ct = default)
    {
        await EnsureReferencesAsync(ct); var resolved = ResolveWeapon(query);
        var auctions = await FetchAuctionsAsync(resolved.Market.Get("slug").Text(), ct);
        var (rule, source) = ResolveDesirabilityRule(resolved.Family, auctions, resolved.StatClass, resolved.Disposition ?? 1);
        return new(resolved.Name, resolved.Market.Get("slug").Text(), resolved.StatClass, resolved.Disposition ?? 1,
            RivenPricing.AllowedStats(resolved.StatClass, true), RivenPricing.AllowedStats(resolved.StatClass, false),
            source, rule.PositiveExpression, rule.HarmlessNegatives, rule.Notes);
    }
    public async Task<RivenDeal[]> FindDealsAsync(string query, int? maximumPrice = null, double minimumDiscountPct = 20, bool onlineOnly = true, CancellationToken ct = default)
    {
        await EnsureReferencesAsync(ct); var resolved = ResolveWeapon(query); var weapon = resolved.Market;
        var slug = weapon.Get("slug").Text(); var name = resolved.Name;
        var auctions = await FetchAuctionsAsync(slug, ct);
        var weeklyPrice = new[] { true, false }.Select(rerolled => RivenPricing.ParseWeekly(weekly, resolved.Family, rerolled)).FirstOrDefault(p => p is not null);
        var (rule, profileSource) = ResolveDesirabilityRule(resolved.Family, auctions, resolved.StatClass, resolved.Disposition ?? 1);
        return RivenPricing.FindDeals(auctions, slug, name, weeklyPrice, resolved.StatClass, resolved.Disposition,
            minimumDiscountPct, maximumPrice, onlineOnly, 8, rule, true, rule.PositiveExpression, $"{profileSource}: {rule.Notes}", ct);
    }
    public async Task<RivenAppraisal> AppraiseAsync(string query, string[] positives, string? negative, bool rerolled = true,
        double?[]? positiveValues = null, double? negativeValue = null, int masteryRank = 8, int modRank = 0, int rerolls = 0,
        CancellationToken ct = default)
    {
        await EnsureReferencesAsync(ct); var resolved = ResolveWeapon(query); var slug = resolved.Market.Get("slug").Text();
        var auctions = await FetchAuctionsAsync(slug, ct); var weeklyPrice = RivenPricing.ParseWeekly(weekly, resolved.Family, rerolled);
        var signature = RivenPricing.Signature(positives, negative);
        (string Signature, double LifetimeHours)[] closures;
        await observationsLock.WaitAsync(ct);
        (string Signature, int Price, double? LifetimeHours)[] confirmed;
        try
        {
            closures = observations.Values.Where(o => o.WeaponSlug == slug && o.ClosedObservedAt.HasValue && o.RollSignature == signature)
                .Select(o => (o.RollSignature, Math.Max(0, (o.ClosedObservedAt!.Value - o.ListedAt).TotalHours))).ToArray();
            confirmed = recordedOutcomes.Where(o => o.WeaponSlug == slug && o.RollSignature == signature && o.Outcome == "sold" && o.Price.HasValue)
                .Select(o => (o.RollSignature, o.Price!.Value, o.ListedAt.HasValue && o.SoldAt.HasValue ? Math.Max(0, (o.SoldAt.Value - o.ListedAt.Value).TotalHours) : (double?)null)).ToArray();
        }
        finally { observationsLock.Release(); }
        var (appraisalRule, profileSource) = ResolveDesirabilityRule(resolved.Family, auctions, resolved.StatClass, resolved.Disposition ?? 1);
        return RivenPricing.Appraise(auctions, resolved.Name, slug, positives, negative, weeklyPrice, closures,
            resolved.StatClass, resolved.Disposition ?? 1, positiveValues, negativeValue, confirmed, masteryRank, modRank, rerolls, appraisalRule, profileSource);
    }
    public async Task<bool> RecordOutcomeAsync(RivenAppraisal appraisal, string outcome, int? price, DateTimeOffset? listedAt,
        DateTimeOffset? soldAt, string reporterHash, CancellationToken ct = default)
    {
        if (outcome is not ("sold" or "still_listed" or "withdrawn")) throw new ArgumentException("Unknown outcome.");
        if (outcome == "sold" && price is not > 0) throw new ArgumentException("A completed sale needs a positive platinum price.");
        if (price is > 1_000_000) throw new ArgumentException("Recorded platinum price exceeds the supported maximum of 1,000,000.");
        if (string.IsNullOrWhiteSpace(reporterHash) || reporterHash.Length > 128) throw new ArgumentException("Invalid evidence reporter.");
        var now = DateTimeOffset.UtcNow; DateTimeOffset? effectiveSoldAt = outcome == "sold" ? soldAt ?? now : null;
        if (listedAt > now.AddMinutes(5) || effectiveSoldAt > now.AddMinutes(5)) throw new ArgumentException("Evidence timestamps cannot be in the future.");
        if (listedAt.HasValue && effectiveSoldAt.HasValue && effectiveSoldAt < listedAt) throw new ArgumentException("Sold time cannot be before listed time.");
        await observationsLock.WaitAsync(ct);
        try
        {
            var duplicate = recordedOutcomes.Any(row => row.ReporterHash == reporterHash && row.WeaponSlug == appraisal.WeaponSlug &&
                row.RollSignature == appraisal.RollSignature && row.Outcome == outcome && row.Price == price && now - row.RecordedAt < TimeSpan.FromMinutes(10));
            if (duplicate) return false;
            recordedOutcomes.Add(new(Guid.NewGuid().ToString("N"), appraisal.WeaponName, appraisal.WeaponSlug, appraisal.RollSignature,
                outcome, price, listedAt, now, effectiveSoldAt, reporterHash));
            if (recordedOutcomes.Count > 50_000) recordedOutcomes.RemoveRange(0, recordedOutcomes.Count - 50_000);
            Directory.CreateDirectory(runtime); Json.WriteAtomic(Path.Combine(runtime, "recorded_outcomes.json"), recordedOutcomes.ToArray());
            return true;
        }
        finally { observationsLock.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync(); cancellation?.Dispose(); lifecycle.Dispose(); references.Dispose(); observationsLock.Dispose();
        foreach (var gate in auctionFetchLocks.Values) gate.Dispose(); auctionFetchLocks.Clear(); auctionCache.Clear();
    }
}
