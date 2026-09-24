using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RelicFrame.Core;

public sealed record ArbitrationEntry(long Activation, long Expiry, string Text, string MissionType, string Enemy,
    string Location, string Tier, string? ResourceBonus);
public sealed record WorldSnapshot(JsonElement World, JsonElement Bounty, DateTimeOffset FetchedAt, bool Stale,
    string Source, string[] RepairedSections, JsonElement Nodes = default);
public sealed record WorldBoard(string Key, string Title, string Description, uint Color = 0x5A67D8);

public sealed record RelicMissionRating(string Grade, int SortOrder, string Pace, string Reason);
public sealed record RelicMissionEvaluation(string Grade, int RewardScore, string ProfitBand, string[] Rewards);

public static class RelicMissionGrade
{
    // These grades answer one question only: how efficiently a public fissure can
    // open relics with a normal squad. They do not reuse Arbitration's node tiers.
    private static readonly IReadOnlyDictionary<string, RelicMissionRating> Ratings =
        new Dictionary<string, RelicMissionRating>(StringComparer.OrdinalIgnoreCase)
        {
            ["capture"] = R("A", 1, "Fast", "short objective and extraction"),
            ["extermination"] = R("A", 1, "Fast", "reactant and objective progress together"),
            ["crossfire"] = R("A", 1, "Fast", "reactant and objective progress together"),
            ["rescue"] = R("A", 1, "Fast", "short objective with a direct extraction"),
            ["sabotage"] = R("F", 5, "Long", "multi-stage route"),
            ["hive"] = R("B", 2, "Fast", "three objectives add route variance"),
            ["disruption"] = R("A", 1, "Endless", "fast rotations with a coordinated squad"),
            ["excavation"] = R("F", 5, "Long", "multiple excavators and reactant timing"),
            ["survival"] = R("F", 5, "Long", "fixed five-minute rotations"),
            ["void cascade"] = R("S", 0, "Best", "fast rotations with valuable Zariman side rewards"),
            ["void flood"] = R("B", 2, "Endless", "repeatable rotations with movement overhead"),
            ["alchemy"] = R("B", 2, "Endless", "repeatable rotations with squad-dependent speed"),
            ["spy"] = R("F", 5, "Long", "vault travel and failure risk"),
            ["skirmish"] = R("B", 2, "Railjack", "repeatable Void Storm with travel overhead"),
            ["volatile"] = R("B", 2, "Railjack", "focused Railjack objective with travel overhead"),
            ["defense"] = R("C", 3, "Endless", "fixed waves and enemy pathing limit speed"),
            ["interception"] = R("F", 5, "Long", "fixed rounds with four-point coordination"),
            ["mobile defense"] = R("F", 5, "Long", "multiple unskippable defense timers"),
            ["assault"] = R("C", 3, "Fixed", "several sequential objectives"),
            ["pursuit"] = R("C", 3, "Railjack", "travel and target phases add variance"),
            ["rush"] = R("C", 3, "Railjack", "travel and multiple targets add variance"),
            ["legacyte harvest"] = R("C", 3, "Endless", "multi-stage repeatable objective"),
            ["hell scrub"] = R("C", 3, "Endless", "timed repeatable objective"),
            ["stage defense"] = R("C", 3, "Endless", "fixed defensive stages"),
            ["shrine defense"] = R("C", 3, "Fixed", "fixed defensive stages"),
            ["void armageddon"] = R("C", 3, "Endless", "multi-stage objective with travel between points"),
            ["ascension"] = R("C", 3, "Fixed", "fixed elevator and extraction phases"),
            ["abyssal zone"] = R("C", 3, "Fixed", "special objective adds a fixed completion cost"),
            ["faceoff"] = R("C", 3, "Fixed", "multi-stage objective with variable opposing progress"),
            ["mirror defense"] = R("D", 4, "Endless", "long fixed rotations split across two targets"),
            ["orphix"] = R("D", 4, "Railjack", "gear-gated objective with long rotations"),
            ["hijack"] = R("F", 5, "Long", "escort speed and pathing limit completion"),
            ["assassination"] = R("D", 4, "Fixed", "boss phases make completion time inconsistent"),
            ["infested salvage"] = R("D", 4, "Endless", "fixed rotations with console upkeep"),
            ["defection"] = R("F", 5, "Slow", "long escort rotations and unreliable pathing"),
            ["free roam"] = R("F", 5, "Unsupported", "not a standard public fissure route"),
            ["junction"] = R("F", 5, "Unsupported", "not a repeatable fissure route")
        };
    private static readonly IReadOnlyDictionary<string, int> BaseScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["void cascade"]=110, ["capture"]=100, ["extermination"]=97, ["crossfire"]=96, ["rescue"]=94,
        ["disruption"]=89, ["void flood"]=78, ["alchemy"]=76, ["hive"]=72, ["volatile"]=70, ["skirmish"]=68,
        ["defense"]=62, ["legacyte harvest"]=57,
        ["hell scrub"]=56, ["stage defense"]=55, ["shrine defense"]=55, ["void armageddon"]=54,
        ["ascension"]=53, ["assault"]=52, ["pursuit"]=51, ["rush"]=50, ["abyssal zone"]=49,
        ["faceoff"]=48, ["mirror defense"]=43, ["orphix"]=41, ["assassination"]=37, ["infested salvage"]=35,
        ["excavation"]=28, ["sabotage"]=27, ["spy"]=26, ["survival"]=25, ["interception"]=23,
        ["mobile defense"]=22, ["hijack"]=20, ["defection"]=15,
        ["free roam"]=10, ["junction"]=5
    };
    private static readonly HashSet<string> Endless = new(StringComparer.OrdinalIgnoreCase)
    {
        "disruption", "excavation", "survival", "void cascade", "void flood", "alchemy", "defense",
        "interception", "legacyte harvest", "hell scrub", "stage defense", "mirror defense", "infested salvage", "void armageddon"
    };

    private static RelicMissionRating R(string grade, int order, string pace, string reason) => new(grade, order, pace, reason);
    private static string Key(string missionType)
    {
        var value = Regex.Replace(missionType.Trim(), "[_-]+", " ");
        value = Regex.Replace(value, "\\s+", " ").ToLowerInvariant();
        return value switch
        {
            "exterminate" => "extermination",
            "mobile defence" => "mobile defense",
            "mirror defence" => "mirror defense",
            "orphix venom" => "orphix",
            _ => value
        };
    }
    public static RelicMissionRating Rate(string missionType)
        => Ratings.GetValueOrDefault(Key(missionType)) ?? R("U", 6, "Unrated", "new mission type; review required");
    public static string For(string missionType) => Rate(missionType).Grade;
    public static int GradeOrder(string grade) => grade.ToUpperInvariant() switch { "S" => 0, "A" => 1, "B" => 2, "C" => 3, "D" => 4, "F" => 5, _ => 6 };
    private static int GradeBase(string grade) => grade.ToUpperInvariant() switch { "S" => 110, "A" => 90, "B" => 75, "C" => 60, "D" => 40, "F" => 20, _ => -1 };
    public static RelicMissionEvaluation Evaluate(string missionType, string node, bool steelPath, bool voidStorm, string? assignedGrade = null)
    {
        var key = Key(missionType); var known = BaseScores.TryGetValue(key, out var score);
        if (!known) return new("U", -1, "profit unknown", ["Prime reward", "Void Traces", "base mission reward; new mission type needs review"]);
        var grade = assignedGrade is { Length: > 0 } && GradeOrder(assignedGrade) < 6 ? assignedGrade.ToUpperInvariant() : Rate(missionType).Grade;
        if (assignedGrade is { Length: > 0 }) score = GradeBase(grade);
        var rewards = new List<string> { "Prime reward", "Void Traces", Endless.Contains(key) ? "rotation reward" : "mission-completion reward" };

        if (Endless.Contains(key))
        {
            score += 3;
            rewards.Add("stacking endless boosters"); rewards.Add("refined relic every fifth rotation");
        }
        if (key == "excavation") { score += 2; rewards.Add("Cryotic"); }
        if (key == "disruption") { score += 2; rewards.Add("fast scaling rotation table"); }
        if (key is "void cascade" or "void flood" or "void armageddon") { score += 3; rewards.Add("Zariman rotation resources/rewards"); }
        if (key is "alchemy" or "mirror defense") { score += 2; rewards.Add("Entrati Laboratory rotation rewards"); }
        if (node.Contains("Circulus", StringComparison.OrdinalIgnoreCase) || node.Contains("Yuvarium", StringComparison.OrdinalIgnoreCase))
        { score += 4; rewards.Add("Conjunction Survival Lua Thrax Plasm/rotation rewards"); }

        if (steelPath)
        {
            score += 10; rewards.Add("+1 Steel Essence per relic cracked");
            if (Endless.Contains(key)) { score += 2; rewards.Add("Acolyte Steel Essence/Arcane opportunity during long runs"); }
        }
        if (voidStorm)
        {
            score += 8; rewards.Add("separate Void Storm reward-table item (including Endo/relic or Holokey chances)");
            if (steelPath) { score += 4; rewards.Add("+2 Steel Essence on Steel Path Railjack mission completion"); }
        }
        score = Math.Min(120, score);
        var band = grade switch { "S" => "top tier", "A" => "A tier", "B" => "B tier", "C" => "C tier", "D" => "D tier", "F" => "F tier", _ => "unrated" };
        return new(grade, score, band, rewards.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }
}

public static class ArbitrationSchedule
{
    private static readonly Regex DateLine = new("^(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun),\\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex EntryLine = new("^(\\d{2})(\\d{2})\\s+•\\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex Detail = new("^(?<mission>.+?)\\s+-\\s+(?<enemy>.+?)\\s+@\\s+(?<location>.+?)(?:\\s+\\((?<details>.+)\\))?$", RegexOptions.Compiled);
    public static ArbitrationEntry[] Load(string path, string timezone = "America/New_York", int? startYear = null)
    {
        if (!File.Exists(path)) return [];
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(timezone); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York"); }
        var year = startYear ?? TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Year; int? previousMonth = null; DateOnly? current = null;
        var result = new List<ArbitrationEntry>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim().TrimStart('\ufeff'); if (line.Length == 0) continue;
            var date = DateLine.Match(line);
            if (date.Success)
            {
                var value = date.Groups[1].Value; var explicitYear = Regex.IsMatch(value, "\\b\\d{4}$");
                var patterns = explicitYear ? new[] { "MMMM d, yyyy" } : new[] { "MMMM d, 2000" };
                if (!DateTime.TryParseExact(explicitYear ? value : value + ", 2000", patterns, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) { current = null; continue; }
                if (!explicitYear && previousMonth == 12 && parsed.Month == 1) year++;
                if (explicitYear) year = parsed.Year; previousMonth = parsed.Month; current = new(year, parsed.Month, parsed.Day); continue;
            }
            var entry = EntryLine.Match(line); if (!entry.Success || current is null) continue;
            var text = entry.Groups[3].Value.Trim(); var details = Detail.Match(text);
            var mission = details.Success ? details.Groups["mission"].Value : text;
            var enemy = details.Success ? details.Groups["enemy"].Value : "Unknown";
            var location = details.Success ? details.Groups["location"].Value : "Unknown";
            var extras = details.Success ? details.Groups["details"].Value : "";
            var tierMatch = Regex.Match(extras, "\\b([A-Z])\\s+tier\\b", RegexOptions.IgnoreCase);
            var bonusMatch = Regex.Match(extras, "(\\d+%\\s+resource bonus)", RegexOptions.IgnoreCase);
            var local = current.Value.ToDateTime(new(int.Parse(entry.Groups[1].Value), int.Parse(entry.Groups[2].Value)), DateTimeKind.Unspecified);
            var start = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUnixTimeSeconds();
            result.Add(new(start, start + 3600, text, mission, enemy, location, tierMatch.Success ? tierMatch.Groups[1].Value.ToUpperInvariant() : "F", bonusMatch.Success ? bonusMatch.Value : null));
        }
        return result.ToArray();
    }
    public static ArbitrationEntry[] ParseBrowse(string raw, string tiersScript, JsonElement nodes, DateTimeOffset? now = null)
    {
        var tiers = Regex.Matches(tiersScript, @"(?<node>(?:Sol|Clan)Node\d+)\s*:\s*""(?<tier>[SABCDF])""", RegexOptions.IgnoreCase)
            .ToDictionary(match => match.Groups["node"].Value, match => match.Groups["tier"].Value.ToUpperInvariant(), StringComparer.Ordinal);
        var instant = now ?? DateTimeOffset.UtcNow;
        var first = instant.AddHours(-2).ToUnixTimeSeconds();
        var last = instant.AddDays(90).ToUnixTimeSeconds();
        var result = new List<ArbitrationEntry>();
        foreach (var rawLine in raw.Split('\n'))
        {
            var parts = rawLine.Trim().TrimStart('\ufeff').Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var activation)
                || activation < first || activation > last) continue;
            var nodeKey = parts[1]; var node = nodes.Get(nodeKey);
            var location = node.Get("value").Text(nodeKey);
            var mission = node.Get("type").Text("Unknown mission");
            var enemy = node.Get("enemy").Text("Unknown faction");
            result.Add(new(activation, activation + 3600, nodeKey, mission, enemy, location,
                tiers.GetValueOrDefault(nodeKey, "F"), null));
        }
        return result.OrderBy(entry => entry.Activation).ToArray();
    }
    public static ArbitrationEntry? Current(IEnumerable<ArbitrationEntry> entries, long now) => entries.LastOrDefault(e => e.Activation <= now && now < e.Expiry);
    public static string? TierFor(IEnumerable<ArbitrationEntry> entries, string node, string mission)
    {
        static string Key(string value) => Regex.Replace(value, "\\s*\\([^)]*\\)\\s*$", "").Split(',')[0].Trim().ToLowerInvariant();
        var wanted = Key(node); var matches = entries.Where(e => Key(e.Location) == wanted).ToArray();
        return matches.FirstOrDefault(e => e.MissionType.Equals(mission, StringComparison.OrdinalIgnoreCase))?.Tier
            ?? (matches.Select(e => e.Tier).Distinct(StringComparer.Ordinal).Take(2).Count() == 1 ? matches[0].Tier : null);
    }
}

public sealed record ArbitrationScheduleCache(DateTimeOffset FetchedAt, ArbitrationEntry[] Entries);

/// <summary>Published Arbitration rotation with an on-disk last-known-good copy.</summary>
public sealed class ArbitrationScheduleFeed : IDisposable
{
    private const string ScheduleUrl = "https://browse.wf/arbys.txt";
    private const string TiersUrl = "https://raw.githubusercontent.com/calamity-inc/browse.wf/senpai/supplemental-data/arbyTiers.js";
    private readonly string cachePath;
    private readonly HttpClient client;
    private ArbitrationScheduleCache cache;
    public ArbitrationScheduleFeed(string cachePath, HttpMessageHandler? handler = null)
    {
        this.cachePath = cachePath;
        client = handler is null ? new(new SocketsHttpHandler { MaxConnectionsPerServer = 2, AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(10) }) : new(handler);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RelicFrame-CSharp/0.1");
        try { cache = File.Exists(cachePath) ? Json.Read<ArbitrationScheduleCache>(cachePath) : new(DateTimeOffset.MinValue, []); }
        catch (Exception error) when (error is IOException or JsonException) { cache = new(DateTimeOffset.MinValue, []); }
    }
    public ArbitrationEntry[] Cached => cache.Entries ?? [];
    public async Task<ArbitrationEntry[]> GetAsync(JsonElement nodes, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var usable = Cached.Any(entry => entry.Activation <= now.ToUnixTimeSeconds() && now.ToUnixTimeSeconds() < entry.Expiry)
            && Cached.Any(entry => entry.Activation > now.ToUnixTimeSeconds());
        if (usable && now - cache.FetchedAt < TimeSpan.FromHours(6)) return Cached;
        if (nodes.ValueKind != JsonValueKind.Object) return Cached;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var scheduleTask = client.GetStringAsync(ScheduleUrl, deadline.Token);
            var tiersTask = client.GetStringAsync(TiersUrl, deadline.Token);
            await Task.WhenAll(scheduleTask, tiersTask);
            var entries = ArbitrationSchedule.ParseBrowse(await scheduleTask, await tiersTask, nodes, now);
            if (!entries.Any(entry => entry.Activation <= now.ToUnixTimeSeconds() && now.ToUnixTimeSeconds() < entry.Expiry)
                || !entries.Any(entry => entry.Activation > now.ToUnixTimeSeconds()))
                throw new InvalidDataException("Published Arbitration schedule did not cover the current rotation.");
            cache = new(now, entries);
            Json.WriteAtomic(cachePath, cache);
        }
        catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or IOException or JsonException or InvalidDataException or TaskCanceledException)
        {
            // A prior validated cache remains authoritative during upstream outages.
        }
        return Cached;
    }
    public void Dispose() => client.Dispose();
}

public sealed class WorldStateClient : IDisposable
{
    private readonly HttpClient client;
    private JsonElement bounty;
    private JsonElement nodes;
    public WorldStateClient(HttpMessageHandler? handler = null)
    {
        client = handler is null ? new(new SocketsHttpHandler { MaxConnectionsPerServer = 2, AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) : new(handler);
        client.Timeout = Timeout.InfiniteTimeSpan; client.DefaultRequestHeaders.UserAgent.ParseAdd("RelicFrame-CSharp/0.1");
    }
    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(12));
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if ((int)response.StatusCode >= 500 && attempt < 1) { await Task.Delay(TimeSpan.FromSeconds(1), ct); continue; }
                if (!response.IsSuccessStatusCode)
                {
                    var source = new Uri(url);
                    throw new HttpRequestException($"World source {source.Host}{source.AbsolutePath} returned {(int)response.StatusCode} ({response.ReasonPhrase})", null, response.StatusCode);
                }
                await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024, deadline.Token);
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token); return await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token);
            }
            catch (HttpRequestException error) when (attempt < 1 && (error.StatusCode is null || (int)error.StatusCode >= 500))
            { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < 1) { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
        }
    }
    public async Task<WorldSnapshot> FetchAsync(CancellationToken ct)
    {
        JsonDocument? world = null;
        try { world = await GetAsync("https://api.warframestat.us/pc/", ct); }
        catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or JsonException or TaskCanceledException)
        {
            // The translated combined endpoint has had periods where every /pc route
            // returns 404 while DE's official feed and the node dictionary stay live.
            // Keep fissures current from the official feed; WorldManager retains all
            // non-fissure boards instead of replacing them with empty placeholders.
            return await OfficialOnlyAsync(ct);
        }
        using (world)
        {
        if (world.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid world-state payload.");
        try { using var b = await GetAsync("https://oracle.browse.wf/bounty-cycle", ct); if (b.RootElement.ValueKind == JsonValueKind.Object) bounty = b.RootElement.Clone(); }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or JsonException or TaskCanceledException) { }
        if (nodes.ValueKind != JsonValueKind.Object)
            try { using var n = await GetAsync("https://api.warframestat.us/solNodes/", ct); if (n.RootElement.ValueKind == JsonValueKind.Object) nodes = n.RootElement.Clone(); }
            catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or JsonException or TaskCanceledException) { }
        var copy = world.RootElement.Clone(); var timestamp = WorldRender.Time(copy.Get("timestamp"));
        var stale = !timestamp.HasValue || DateTimeOffset.UtcNow - timestamp.Value > TimeSpan.FromMinutes(10);
        string[] repaired = []; var source = "WarframeStat.us";
        if (stale)
        {
            try
            {
                using var official = await GetAsync("https://api.warframe.com/cdn/worldState.php", ct);
                var stamp = official.RootElement.Get("Time").Number();
                if (stamp.HasValue && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - stamp.Value is >= 0 and <= 600)
                {
                    var nodeData = nodes;
                    if (nodeData.ValueKind != JsonValueKind.Object) { using var fetchedNodes = await GetAsync("https://api.warframestat.us/solNodes/", ct); nodeData = fetchedNodes.RootElement.Clone(); nodes = nodeData; }
                    var normalized = OfficialFissures(official.RootElement, nodeData);
                    var root = JsonNode.Parse(copy.GetRawText())!.AsObject(); root["fissures"] = JsonSerializer.SerializeToNode(normalized);
                    using var merged = JsonDocument.Parse(root.ToJsonString()); copy = merged.RootElement.Clone(); repaired = ["fissures"]; source = "official Warframe fallback + WarframeStat.us";
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or JsonException or TaskCanceledException) { }
        }
        return new(copy, bounty, DateTimeOffset.UtcNow, stale, source, repaired, nodes);
        }
    }
    private async Task<WorldSnapshot> OfficialOnlyAsync(CancellationToken ct)
    {
        if (nodes.ValueKind != JsonValueKind.Object)
            try { using var n = await GetAsync("https://api.warframestat.us/solNodes/", ct); if (n.RootElement.ValueKind == JsonValueKind.Object) nodes = n.RootElement.Clone(); }
            catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or JsonException or TaskCanceledException) { }
        using var official = await GetAsync("https://api.warframe.com/cdn/worldState.php", ct);
        var stamp = official.RootElement.Get("Time").Number();
        if (!stamp.HasValue) throw new InvalidDataException("Official world-state payload had no timestamp.");
        var normalized = OfficialFissures(official.RootElement, nodes);
        var payload = new
        {
            timestamp = DateTimeOffset.FromUnixTimeSeconds((long)stamp.Value).ToString("O"),
            buildLabel = official.RootElement.Get("BuildLabel").Text(),
            fissures = normalized
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return new(document.RootElement.Clone(), bounty, DateTimeOffset.UtcNow, true,
            "official Warframe fallback (fissures only)", ["fissures"], nodes);
    }
    private static object[] OfficialFissures(JsonElement raw, JsonElement nodes)
    {
        var tiers = new Dictionary<string, (string Name, int Number)> { ["VoidT1"]=("Lith",1), ["VoidT2"]=("Meso",2), ["VoidT3"]=("Neo",3), ["VoidT4"]=("Axi",4), ["VoidT5"]=("Requiem",5), ["VoidT6"]=("Omnia",6) };
        var missions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MT_EXTERMINATION"]="Extermination", ["MT_SURVIVAL"]="Survival", ["MT_RESCUE"]="Rescue",
            ["MT_MOBILE_DEFENSE"]="Mobile Defense", ["MT_DEFENSE"]="Defense", ["MT_INTEL"]="Spy",
            ["MT_TERRITORY"]="Interception", ["MT_SABOTAGE"]="Sabotage", ["MT_HIVE"]="Hive",
            ["MT_VOID_CASCADE"]="Void Cascade", ["MT_VOID_FLOOD"]="Void Flood", ["MT_VOID_ARMAGEDDON"]="Void Armageddon",
            ["MT_ALCHEMY"]="Alchemy", ["MT_ASSASSINATION"]="Assassination", ["MT_DISRUPTION"]="Disruption",
            ["MT_CAPTURE"]="Capture", ["MT_EXCAVATE"]="Excavation", ["MT_CROSS_FIRE"]="Crossfire",
            ["MT_ASSAULT"]="Assault", ["MT_HIJACK"]="Hijack", ["MT_INFESTED_SALVAGE"]="Infested Salvage",
            ["MT_EVACUATION"]="Defection", ["MT_MIRROR_DEFENSE"]="Mirror Defense", ["MT_ASCENSION"]="Ascension",
            ["MT_RAILJACK"]="Skirmish", ["MT_VOLATILE"]="Volatile", ["MT_ORPHIX"]="Orphix"
        };
        static string MongoDate(JsonElement value)
        {
            var number = value.Get("$date").Get("$numberLong").Text();
            return long.TryParse(number, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O") : "";
        }
        static string Id(JsonElement value) => value.ValueKind == JsonValueKind.Object ? value.Get("$oid").Text() : value.Text();
        string Node(string key) => nodes.Get(key).Get("value").Text(key.Length > 0 ? key : "Unknown node");
        string NodeMission(string key) => nodes.Get(key).Get("type").Text("Skirmish");
        string NodeEnemy(string key) => nodes.Get(key).Get("enemy").Text("Unknown faction");
        var result = new List<object>();
        foreach (var row in raw.Get("ActiveMissions").Rows())
        {
            var modifier = row.Get("Modifier").Text(); var tier = tiers.GetValueOrDefault(modifier, (modifier.Length > 0 ? modifier : "?", 99)); var node = row.Get("Node").Text(); var mission = row.Get("MissionType").Text();
            result.Add(new { id=Id(row.Get("_id")), activation=MongoDate(row.Get("Activation")), expiry=MongoDate(row.Get("Expiry")), tier=tier.Item1, tierNum=tier.Item2,
                missionType=missions.GetValueOrDefault(mission, NodeMission(node)), node=Node(node), enemy=NodeEnemy(node), isHard=row.Get("Hard").Bool(), isStorm=false });
        }
        foreach (var row in raw.Get("VoidStorms").Rows())
        {
            var modifier = row.Get("ActiveMissionTier").Text(); var tier = tiers.GetValueOrDefault(modifier, (modifier.Length > 0 ? modifier : "?", 99)); var node = row.Get("Node").Text();
            result.Add(new { id=Id(row.Get("_id")), activation=MongoDate(row.Get("Activation")), expiry=MongoDate(row.Get("Expiry")), tier=tier.Item1, tierNum=tier.Item2,
                missionType=NodeMission(node), node=Node(node), enemy=NodeEnemy(node), isHard=row.Get("Hard").Bool(), isStorm=true });
        }
        return result.ToArray();
    }
    public void Dispose() => client.Dispose();
}

public static class WorldRender
{
    private const string Footer = "Live world state • auto-updates • Digital Extremes / WarframeStat.us";
    public static DateTimeOffset? Time(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch))
            return Math.Abs(epoch) > 50_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : DateTimeOffset.FromUnixTimeSeconds(epoch);
        return DateTimeOffset.TryParse(value.Text(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }
    private static string Stamp(JsonElement value, string style = "R") => Time(value) is { } t ? $"<t:{t.ToUnixTimeSeconds()}:{style}>" : "time unavailable";
    private static bool Active(JsonElement row, DateTimeOffset now)
    {
        var start = Time(row.Get("activation")); var end = Time(row.Get("expiry"));
        return (!start.HasValue || start <= now) && (!end.HasValue || now < end);
    }
    private static IEnumerable<JsonElement> ActiveRows(JsonElement rows, DateTimeOffset now) => rows.Rows().Where(r => Active(r, now));
    private static string Trim(string value, int length = 3900) => value.Length <= length ? value : value[..(length - 15)].TrimEnd() + "\n…more omitted";
    private static string Lines(IEnumerable<string> rows, string empty) { var text = string.Join('\n', rows.Where(r => r.Length > 0)); return Trim(text.Length > 0 ? text : empty); }
    private static string Empty(WorldSnapshot data) => data.Stale ? "Live mission source is delayed; retrying automatically." : "Nothing active right now.";
    private static string FissureEmpty(WorldSnapshot data) => data.Stale && !data.RepairedSections.Contains("fissures") ? "Live mission source is delayed; retrying automatically." : "Nothing active right now.";
    private static WorldBoard Board(string key, string title, string text, uint color) => new(key, title, Trim(text + $"\n\n_{Footer}_"), color);
    private static string Reward(JsonElement reward)
    {
        var values = new List<string>(); if (reward.Get("credits").Number() is > 0 and var credits) values.Add($"{credits:N0} Credits");
        values.AddRange(reward.Get("items").Rows().Select(v => v.Text()));
        values.AddRange(reward.Get("countedItems").Rows().Select(v => $"{v.Get("count").Number() ?? 1:0}× {v.Get("type").Text(v.Get("key").Text("Item"))}"));
        return values.Count > 0 ? string.Join(", ", values) : "No reward listed";
    }
    private static RelicMissionEvaluation FissureEvaluation(JsonElement f, IReadOnlyList<ArbitrationEntry> schedule)
    {
        var mission = f.Get("missionType").Text("Unknown"); var node = f.Get("node").Text();
        var defenseGrade = mission.Equals("Defense", StringComparison.OrdinalIgnoreCase) ? ArbitrationSchedule.TierFor(schedule, node, "Defense") ?? "C" : null;
        return RelicMissionGrade.Evaluate(mission, node, f.Get("isHard").Bool(), f.Get("isStorm").Bool(), defenseGrade);
    }
    private static (string Node, string Planet) NodeAndPlanet(string value)
    {
        var match = Regex.Match(value.Trim(), "^(?<node>.+?)\\s*\\((?<planet>[^()]*)\\)$");
        if (match.Success) return (match.Groups["node"].Value.Trim(), match.Groups["planet"].Value.Trim());
        var comma = value.LastIndexOf(',');
        return comma > 0 ? (value[..comma].Trim(), value[(comma + 1)..].Trim()) : (value.Trim().Length > 0 ? value.Trim() : "Unknown node", "Unknown region");
    }
    private static string FissureLine(JsonElement f, IReadOnlyList<ArbitrationEntry> schedule)
    {
        var era = f.Get("tier").Text("?"); var mission = f.Get("missionType").Text("Unknown");
        var evaluation = FissureEvaluation(f, schedule); var location = NodeAndPlanet(f.Get("node").Text("Unknown node"));
        var fullLocation = $"{location.Node} ({location.Planet})";
        var enemy = f.Get("enemy").Text(f.Get("faction").Text());
        if (enemy.Length == 0 || enemy.Equals("Unknown faction", StringComparison.OrdinalIgnoreCase))
            enemy = schedule.FirstOrDefault(entry => entry.Location.Equals(fullLocation, StringComparison.OrdinalIgnoreCase))?.Enemy ?? "Unknown faction";
        var icon = ApplicationEmojis.Mission(mission);
        if (icon.Length == 0) icon = ApplicationEmojis.VoidFissureNode;
        return $"{icon} **{era} {mission} — {enemy}**\n{fullLocation} · **{evaluation.Grade} tier** · ends {Stamp(f.Get("expiry"))}";
    }
    private static readonly IReadOnlyDictionary<string, string> BountyChallenges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ZarimanCascadeCompleteWavesEasyChallenge"] = "Complete Void Cascade waves",
        ["ZarimanAssassinateUseAllTurretsChallenge"] = "Use every turret during Void Armageddon",
        ["ZarimanMobDefProtectShieldsChallenge"] = "Keep the Mobile Defense target's shields protected",
        ["ZarimanFindMelicaCacheChallenge"] = "Find Melica's cache",
        ["ZarimanDefeatVoidAngelChallenge"] = "Defeat a Void Angel",
        ["EntratiLabDestroyDecorationChallenge"] = "Destroy laboratory decorations",
        ["EntratiLabKillVoidRigEasyChallenge"] = "Defeat a Voidrig",
        ["EntratiLabDefeatDoppelgangerChallenge"] = "Defeat the doppelganger",
        ["EntratiLabKillMurmurHardChallenge"] = "Defeat Murmur enemies",
        ["EntratiLabActivateLohkSurgeHardChallenge"] = "Activate a Lohk Surge",
        ["VaniaHighKillEasy"] = "Reach the required kill count",
        ["VaniaSafeCracker"] = "Open Techrot safes",
        ["VaniaDestroyPropsNormal"] = "Destroy Scaldra supplies",
        ["VaniaDestroyVehiclesHard"] = "Destroy Scaldra vehicles",
        ["VaniaDestroyPropsVeryHard"] = "Destroy the high-tier Scaldra supplies",
        ["VaniaInfestedCrossfire"] = "Complete the Techrot/Scaldra crossfire objective",
        ["LichVaniaCaptureTargets"] = "Capture the Technocyte Coda targets"
    };
    private static string PathTail(string value) => value[(value.LastIndexOf('/') + 1)..];
    private static string Humanize(string value)
    {
        value = PathTail(value).Replace('_', ' ');
        value = Regex.Replace(value, "(?<=[a-z0-9])(?=[A-Z])", " ");
        return value.Trim();
    }
    private static string BountyChallenge(string path)
    {
        var key = PathTail(path);
        if (BountyChallenges.TryGetValue(key, out var friendly)) return friendly;
        var difficulty = key.Contains("VeryHard", StringComparison.OrdinalIgnoreCase) ? "very hard" : key.Contains("Hard", StringComparison.OrdinalIgnoreCase) ? "hard" : key.Contains("Easy", StringComparison.OrdinalIgnoreCase) ? "easy" : key.Contains("Normal", StringComparison.OrdinalIgnoreCase) ? "normal" : "";
        key = Regex.Replace(key, "^(?:Zariman|EntratiLab|Vania|LichVania)", "", RegexOptions.IgnoreCase);
        key = Regex.Replace(key, "(?:VeryHard|Hard|Easy|Normal)?Challenge$", "", RegexOptions.IgnoreCase);
        key = Regex.Replace(key, "(?:VeryHard|Hard|Easy|Normal)$", "", RegexOptions.IgnoreCase);
        var text = Humanize(key); return text.Length == 0 ? "Special bounty objective" : text + (difficulty.Length == 0 ? "" : $" ({difficulty})");
    }
    private static string BountyGroup(string key) => key switch
    {
        "ZarimanSyndicate" => "The Holdfasts · Zariman",
        "EntratiLabSyndicate" => "Cavia · Albrecht's Laboratories",
        "HexSyndicate" => "The Hex · Höllvania",
        _ => Humanize(key)
    };
    // WarframeStat.us can lag behind newly added Public Export nodes. Keep a small,
    // reviewed fallback for the Höllvania bounty keys so raw SolNode IDs never leak
    // into the board while the upstream node dictionary catches up.
    private static readonly IReadOnlyDictionary<string, (string Location, string Mission)> BountyNodeFallback =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["SolNode850"] = ("Köbinn West (Höllvania)", "Legacyte Harvest"),
            ["SolNode851"] = ("Mischta Ramparts (Höllvania)", "Hell-Scrub"),
            ["SolNode852"] = ("Old Konderuk (Höllvania)", "Hell-Scrub"),
            ["SolNode853"] = ("Mausoleum East (Höllvania)", "Exterminate"),
            ["SolNode854"] = ("Rhu Manor (Höllvania)", "Exterminate"),
            ["SolNode855"] = ("Lower Vehrvod (Höllvania)", "Faceoff"),
            ["SolNode856"] = ("Victory Plaza (Höllvania)", "Assassination"),
            ["SolNode857"] = ("Vehrvod District (Höllvania)", "Faceoff"),
            ["SolNode858"] = ("Solstice Square (Höllvania)", "Stage Defense")
        };
    private static string BountyJob(JsonElement row, JsonElement nodes, int index)
    {
        var nodeKey = row.Get("node").Text(); var node = nodes.Get(nodeKey);
        BountyNodeFallback.TryGetValue(nodeKey, out var fallback);
        var location = node.Get("value").Text(fallback.Location ?? (nodeKey.Length > 0 ? nodeKey : "Unknown location"));
        var mission = node.Get("type").Text(fallback.Mission ?? ""); var challenge = BountyChallenge(row.Get("challenge").Text());
        var allyPath = row.Get("ally").Text(); var ally = allyPath.Length == 0 ? "" : Regex.Replace(PathTail(allyPath), "AllyAgent$", "", RegexOptions.IgnoreCase);
        return $"**{index}. {location}**{(mission.Length > 0 ? $" · {mission}" : "")}\n↳ {challenge}{(ally.Length > 0 ? $" · Ally: **{Humanize(ally)}**" : "")}";
    }
    private static string[] BountyPages(IEnumerable<string> jobs, int limit = 3600)
    {
        var pages = new List<string>(); var current = new List<string>(); var length = 0;
        foreach (var job in jobs)
        {
            var addition = (current.Count == 0 ? 0 : 1) + job.Length;
            if (current.Count > 0 && length + addition > limit)
            {
                pages.Add(string.Join('\n', current)); current.Clear(); length = 0; addition = job.Length;
            }
            current.Add(job); length += addition;
        }
        if (current.Count > 0) pages.Add(string.Join('\n', current));
        return pages.Count > 0 ? pages.ToArray() : ["No jobs listed for this syndicate."];
    }
    private static IEnumerable<JsonElement> Fissures(WorldSnapshot data, bool hard, bool storm, IReadOnlyList<ArbitrationEntry> schedule)
    {
        var now = DateTimeOffset.UtcNow;
        return ActiveRows(data.World.Get("fissures"), now).Where(f => f.Get("isStorm").Bool() == storm && (storm || f.Get("isHard").Bool() == hard))
            .OrderBy(f => RelicMissionGrade.GradeOrder(FissureEvaluation(f, schedule).Grade))
            .ThenByDescending(f => FissureEvaluation(f, schedule).RewardScore)
            .ThenBy(f => f.Get("tierNum").Number() ?? 99).ThenBy(f => Time(f.Get("expiry")))
            .ThenBy(f => f.Get("node").Text(), StringComparer.OrdinalIgnoreCase);
    }
    private static string[] FissurePages(IEnumerable<JsonElement> rows, string empty, IReadOnlyList<ArbitrationEntry> schedule, int limit = 3000)
    {
        var lines = rows.Select(row => FissureLine(row, schedule)).ToArray(); if (lines.Length == 0) return [empty];
        var pages = new List<string>(); var current = new List<string>(); var length = 0;
        foreach (var line in lines)
        {
            var addition = (current.Count == 0 ? 0 : 1) + line.Length;
            if (current.Count > 0 && length + addition > limit)
            { pages.Add(string.Join('\n', current)); current.Clear(); length = 0; addition = line.Length; }
            current.Add(line); length += addition;
        }
        if (current.Count > 0) pages.Add(string.Join('\n', current)); return pages.ToArray();
    }
    public static WorldBoard[] Build(string key, WorldSnapshot data, IReadOnlyList<ArbitrationEntry> schedule)
    {
        var world = data.World; var now = DateTimeOffset.UtcNow;
        if (key == "bot-guide") return
        [
            Board(key, "RelicFrame — complete starting guide", "**Install:** No Oracle/server is needed. Install .NET 10. In Discord's Developer Portal create a bot, enable **Message Content Intent**, invite it with `bot` + `applications.commands`, and grant Manage Channels/Roles. Copy the server ID; run `csharp\\run-bot.cmd` on Windows or `./csharp/run-bot.sh` on Linux. Enter the token/ID only in the launcher. Full instructions: `csharp/SETUP_GUIDE.md`.\n\n**First run:** Check `/rf-status`; `/rf-world setup` and `/rf-panel setup` repair boards without deleting user posts.\n\n**Data:** listings and Trade Chat posts are asks, not sales. Evidence types stay separate; failures retain the last successful snapshot.", 0x5865F2),
            Board(key, "World channels and notifications", "WARFRAME LIVE covers cycles, news, alerts, hunts, weekly activities, vendors, bounties, fissures, storms, invasions, Arbitration and Cascade. If the translated feed fails, DE's official feed keeps fissures current while other boards retain their last complete data.\n\nUse **#role-pings** to opt in. The first observation is a silent baseline; later pings occur only for new activity and replace the prior bot ping. `/rf-world refresh` refreshes now; `start`, `stop` and `status` control or inspect the worker. Control commands require Manage Server.", 0x3498DB),
            Board(key, "Bounties", "**#bounties displays every live job** for The Holdfasts, Cavia and The Hex. Entries show node, mission, bonus objective and Hex ally; the header shows rotation, Isolation Vault rotation and expiry. Feed gaps use reviewed Höllvania names instead of raw `SolNode` IDs.\n\nThese are current assignments—not full reward tables or a guarantee that every job is unlocked for you.", 0x27AE60),
            Board(key, "Relics and rankings", "THE LIST has nine rankings. A relic qualifies when Vaulted with a Vaulted reward asking at least 5p online; an Unvaulted reward cannot qualify it alone. **Show drops** orders Rare → Uncommon → Common. **#prime-part-prices** sorts every Prime reward by price; select one for all source relics.\n\n**#aya-planner** shows current Varzia relics, direct asks and open EV. **#baro-investments** compares current stock, Ducats and rank-zero market activity. Neither guarantees a sale.\n\nVaulted rewards use the online floor. Unvaulted rewards blend it with the five-cheapest recent median, capped at 150% of the baseline so offline sellers cannot create a spike. EV is weighted value; profit subtracts cost; ROI divides by cost. Best ROI sorts raw ROI; risk only breaks ties. Guaranteed Profit excludes any relic with a losing worst reward. Green guarantees modeled profit, yellow has positive EV with risk, red has non-positive EV, and white lacks prices.", 0x8E44AD),
            Board(key, "Riven appraisal", "Use **#riven-appraisal** or `/rf-riven appraise` with 2–3 legal positives and an optional negative. Local YOLO/PaddleOCR isolates stat rows while Tesseract independently reads the whole card and footer. Icon noise is tolerated and every result stays editable. Combined elements, Status Damage and Weak Point Damage are recognized, but stay ungraded until their final ranges are published.\n\nFair value uses the median of matching asks and confirmed exact-roll sales. Quick and patient prices use the lower and upper quartiles. Old listings receive a small adjustment; outliers are filtered, and only **Record sold** confirms a sale. The 418-family guide is primary and gaps are labelled fallback.", 0xA855F7),
            Board(key, "Riven rolls, flips and Endo", "**#riven-desired-rolls** sorts local 30-day WTB wants, then legacy DE popularity and qualifying supply. `/rf-riven desired` is searchable. U/R are stale DE family medians, separate from asks. Flips use the same desired-roll rules; grade cannot rescue dead stats.\n\n**#riven-endo-buying** separately ranks platinum per 1,000 dissolve Endo and ignores roll desirability.", 0xF1C40F),
            Board(key, "Trade Chat evidence", "The launcher uses two local readers. **EE.log** cheaply imports your outgoing Riven posts as `ee-log-outgoing`; Warframe omits incoming public chat from that file. **Screen OCR** reads visible incoming WTS/WTB/WTT text only while Warframe is the foreground app. It samples the configured chat rectangle every few seconds, skips identical frames and immediately discards images. Parsed offers are `screen-ocr-incoming`.\n\nOCR can miss or misread stylized text; all posts remain unconfirmed advertisements, not sales. The reader never sends input, reads game memory, intercepts traffic or connects to private chat. Disable it with `RELICFRAME_TRADE_OCR=false`.", 0x95A5A6),
            Board(key, "Personal market, OCR and safety", "**#personal-market** uses all mapped owned Prime parts, independent of THE LIST. It undercuts the stabilized price by 1–3p without crossing your floor. Sync allows 25 writes per pass; Pause stops writes. Ineligible orders are deleted. Order quantity reductions provisionally suppress stale stock; only completed AlecaFrame trades count toward confirmed gross. A disappeared managed order holds its cached stock from relisting until a confirmed trade, returned order, or newer reduced inventory resolves it; disappearance alone is not a sale.\n\nWindows uses `run-bot.cmd` with encrypted credentials; Linux uses `run-bot.sh` with mode-600 credentials. In **#riven-appraisal**, local image OCR checks affixes and stat ranges before an editable draft; other channels are ignored. It stores no image/text and makes no OpenAI, LLM, or generative-AI request. Companion features are disabled. Never share credentials.", 0x00AEEF)
        ];
        if (key == "world-cycles")
        {
            var specs = new[] { ("Plains of Eidolon / Earth", "cetusCycle"), ("Orb Vallis", "vallisCycle"), ("Cambion Drift", "cambionCycle"), ("Duviri", "duviriCycle"), ("Zariman", "zarimanCycle") };
            return [Board(key, "🌍 Environments", Lines(specs.Select(s => { var c = world.Get(s.Item2); return c.Get("state").Text().Length > 0 ? $"**{s.Item1}** · {c.Get("state").Text().Title()} · {Stamp(c.Get("expiry"))}" : $"**{s.Item1}** · Data temporarily unavailable."; }), "Cycle data temporarily unavailable."), 0x2D9CDB)];
        }
        if (key == "world-news")
        {
            var news = world.Get("news").Rows().OrderByDescending(n => Time(n.Get("date"))).Take(12).Select(n => n.Get("link").Text().Length > 0 ? $"{Stamp(n.Get("date"))} · [{n.Get("message").Text("Warframe news")}]({n.Get("link").Text()})" : $"{Stamp(n.Get("date"))} · {n.Get("message").Text("Warframe news")}");
            var kine = world.Get("kinepage"); return [Board(key, "📰 News", Lines(news, "No current news."), 0x3498DB), Board(key, "📻 KinePage", kine.Get("message").Text("No new messages. Scanning…") + (kine.Get("timestamp").Text().Length > 0 ? $"\nPosted {Stamp(kine.Get("timestamp"))}" : ""), 0x9B59B6)];
        }
        if (key == "world-alerts")
        {
            var alerts = ActiveRows(world.Get("alerts"), now).Select(a => { var m = a.Get("mission"); return $"{ApplicationEmojis.AlertNode} **{m.Get("type").Text("Unknown")} — {m.Get("faction").Text("Unknown")}** ({m.Get("minEnemyLevel").Text("?")}–{m.Get("maxEnemyLevel").Text("?")}) · {m.Get("node").Text("Unknown node")} · {Stamp(a.Get("expiry"))}\n↳ {Reward(m.Get("reward"))}"; });
            var events = ActiveRows(world.Get("events"), now).Select(e => $"**{e.Get("description").Text(e.Get("tooltip").Text(e.Get("tag").Text("Event")))}** · {Stamp(e.Get("expiry"))}");
            return [Board(key, $"{ApplicationEmojis.AlertNode} Alerts", Lines(alerts, Empty(data)), 0xE74C3C), Board(key, "🎉 Events", Lines(events, Empty(data)), 0xF39C12)];
        }
        if (key == "world-sortie")
        {
            var s = world.Get("sortie"); var rows = s.Get("variants").Rows().Select(m => $"{ApplicationEmojis.SortieNode} **{m.Get("missionType").Text("Unknown")}** · {m.Get("modifier").Text("No modifier")} · {m.Get("node").Text()}");
            return [Board(key, $"{ApplicationEmojis.SortieBlack} Sortie", s.ValueKind == JsonValueKind.Object && Active(s, now) ? $"{ApplicationEmojis.BossNode} **{s.Get("boss").Text("Unknown boss")}** · resets {Stamp(s.Get("expiry"))}\n{Lines(rows, "No stages listed.")}" : "Current Sortie data is temporarily unavailable.", 0xE67E22)];
        }
        if (key == "world-archon")
        {
            var a = world.Get("archonHunt"); var missions = a.Get("missions").Rows().Select(m => m.Get("type").Text(m.Get("missionType").Text())).Where(s => s.Length > 0)
                .Select(m => ApplicationEmojis.Mission(m) is { Length: > 0 } icon ? $"{icon} {m}" : m);
            return [Board(key, $"{ApplicationEmojis.ArchonHuntNode} Archon Hunt", a.ValueKind == JsonValueKind.Object && Active(a, now) ? $"{ApplicationEmojis.BossNode} **{a.Get("boss").Text("Unknown Archon")}** · {string.Join(", ", missions)}\nResets {Stamp(a.Get("expiry"))}" : "Current Archon Hunt data is temporarily unavailable.", 0xC0392B)];
        }
        if (key == "world-steel-path")
        {
            var steel = world.Get("steelPath"); var reward = steel.Get("currentReward");
            return [Board(key, "💀 Steel Path", reward.ValueKind == JsonValueKind.Object ? $"Weekly Honors: **{reward.Get("name").Text("Unknown offering")}** · {reward.Get("cost").Text("?")} Steel Essence\nResets {Stamp(steel.Get("expiry"))}" : "Current Steel Path rotation is temporarily unavailable.", 0x2C3E50)];
        }
        if (key == "world-weekly")
        {
            var choices = world.Get("duviriCycle").Get("choices").Rows().Select(c => $"• The Circuit ({c.Get("category").Text("unknown").Title()}): **{string.Join(", ", c.Get("choices").Rows().Select(x => x.Text()))}**");
            return [Board(key, "📅 Weekly Missions", Lines(new[] { "• Help Clem", "• Ayatan Treasure Hunt" }.Concat(choices).Concat(["• Netracells", $"{ApplicationEmojis.IconNarmerWhite} Break Narmer", "• Descendia"]), "Weekly data unavailable."), 0x16A085)];
        }
        if (key == "world-archimedea")
        {
            var boards = world.Get("archimedeas").Rows().Select(a => Board(key, a.Get("typeKey").Text().Contains("HEX") ? "🧬 Temporal Archimedea" : "🧬 Deep Archimedea",
                "Resets " + Stamp(a.Get("expiry")) + "\n" + Lines(a.Get("missions").Rows().Select(m => $"**{m.Get("missionType").Text("Mission")}** · {(m.Get("deviation").Get("name").Text("No deviation"))}"), "No missions listed."), 0x8E44AD)).Take(10).ToArray();
            return boards.Length > 0 ? boards : [Board(key, "🧬 Archimedea", "Current modifiers are unavailable.", 0x8E44AD)];
        }
        if (key == "world-vendors")
        {
            var deal = ActiveRows(world.Get("dailyDeals"), now).FirstOrDefault();
            var dealText = deal.ValueKind == JsonValueKind.Object ? $"**{deal.Get("item").Text("Unknown item")}**\n{deal.Get("salePrice").Text("?")} Platinum · {deal.Get("discount").Text("?")}% off · {Math.Max(0, (deal.Get("total").Number() ?? 0) - (deal.Get("sold").Number() ?? 0)):0}/{deal.Get("total").Text("0")} in stock · ends {Stamp(deal.Get("expiry"))}" : "No active Darvo deal right now.";
            var baro = world.Get("voidTrader"); var start = Time(baro.Get("activation")); var end = Time(baro.Get("expiry")); string baroText;
            if (start <= now && (!end.HasValue || now < end)) baroText = $"At **{baro.Get("location").Text("Unknown relay")}** · leaves {Stamp(baro.Get("expiry"))}\n" + Lines(baro.Get("inventory").Rows().Take(20).Select(i => $"• {i.Get("item").Text("Unknown")} — {ApplicationEmojis.OrokinDucats} {i.Get("ducats").Text("?")} Ducats + {i.Get("credits").Text("?")} Credits"), "Inventory unavailable.");
            else baroText = start.HasValue ? $"Next visit: **{baro.Get("location").Text("Unknown relay")}** · arrives {Stamp(baro.Get("activation"))}" : "Baro's next visit is temporarily unavailable.";
            return [Board(key, "🛍️ Darvo's Deal", dealText, 0x3498DB), Board(key, "🏪 Vendors", "Steel Path Honors and Iron Wake weekly offerings are shown in their live rotations.", 0x95A5A6), Board(key, $"{ApplicationEmojis.OrokinDucats} Baro Ki'Teer", baroText, 0xF1C40F)];
        }
        if (key == "world-bounties")
        {
            var b = data.Bounty; var sections = b.Get("bounties");
            if (b.ValueKind != JsonValueKind.Object || sections.ValueKind != JsonValueKind.Object)
                return [Board(key, "📋 Bounties", "Current bounty rotations are temporarily unavailable.", 0x27AE60)];
            var header = Board(key, "📋 All Current Bounty Jobs", $"Rotation **{b.Get("rot").Text("?")}** · Isolation Vault **{b.Get("vaultRot").Text("?")}** · changes {Stamp(b.Get("expiry"))}\nEvery job supplied by the live rotation is listed below; account unlocks may differ.", 0x27AE60);
            var jobs = sections.EnumerateObject().SelectMany(section =>
            {
                var pages = BountyPages(section.Value.Rows().Select((row, index) => BountyJob(row, data.Nodes, index + 1)));
                return pages.Select((page, index) => Board(key, BountyGroup(section.Name) + (pages.Length > 1 ? $" · {index + 1}/{pages.Length}" : ""), page, 0x16A085));
            }).Take(9).ToArray();
            return [header, ..jobs];
        }
        if (key is "world-fissures" or "world-steel-fissures" or "world-void-storms")
        {
            var hard = key == "world-steel-fissures"; var storm = key == "world-void-storms";
            var title = storm ? $"{ApplicationEmojis.VoidTearBlack} Void Storms (Railjack)" : hard ? $"{ApplicationEmojis.VoidFissureNode} Void Fissures (Steel Path)" : $"{ApplicationEmojis.VoidFissureNode} Void Fissures (Normal)";
            var color = storm ? 0x2980B9u : hard ? 0x34495Eu : 0x8E44ADu;
            var pages = FissurePages(Fissures(data, hard, storm, schedule), FissureEmpty(data), schedule);
            var activeBoards = pages.Select((page, index) => new WorldBoard(key, title + (pages.Length > 1 ? $" · {index + 1}/{pages.Length}" : ""), page, color)).Take(10).ToArray();
            return activeBoards;
        }
        if (key == "world-invasions")
        {
            var rows = world.Get("invasions").Rows().Where(i => !i.Get("completed").Bool()).Select(i => $"**{i.Get("node").Text("Unknown node")}** · {i.Get("desc").Text("Invasion")} · {i.Get("completion").Number() ?? 0:0.0}%\n↳ {i.Get("attacker").Get("faction").Text("?")}: {Reward(i.Get("attacker").Get("reward"))} | {i.Get("defender").Get("faction").Text("?")}: {Reward(i.Get("defender").Get("reward"))}");
            return [Board(key, "⚔️ Invasions", Lines(rows, Empty(data)), 0xD35400)];
        }
        if (key == "world-arbitration")
        {
            var epoch = now.ToUnixTimeSeconds(); var current = ArbitrationSchedule.Current(schedule, epoch); string head;
            if (current is not null) head = $"{ApplicationEmojis.VitusEssence} **{current.MissionType} — {current.Enemy}**\n{current.Location} · **{current.Tier} tier**{(current.ResourceBonus is null ? "" : " · " + current.ResourceBonus)} · ends <t:{current.Expiry}:R>";
            else { var a = world.Get("arbitration"); head = a.ValueKind == JsonValueKind.Object && !a.Get("expired").Bool() ? $"{ApplicationEmojis.VitusEssence} **{a.Get("type").Text("Unknown")} — {a.Get("enemy").Text("Unknown")}**\n{a.Get("node").Text("Unknown")} · ends {Stamp(a.Get("expiry"))}" : "Current Arbitration unavailable."; }
            var upcoming = schedule.Where(e => e.Activation > epoch).Take(6).Select(e => $"{ApplicationEmojis.ArbitrationNode} <t:{e.Activation}:f> · **{e.MissionType}** · {e.Location} · {e.Tier} tier");
            return [Board(key, $"{ApplicationEmojis.ArbitrationNode} Arbitration", head + "\n\n**Coming next**\n" + Lines(upcoming, "No schedule available."), 0xF39C12)];
        }
        if (key == "world-cascade")
        {
            var active = ActiveRows(world.Get("fissures"), now).Where(f => !f.Get("isStorm").Bool() && f.Get("missionType").Text().Equals("Void Cascade", StringComparison.OrdinalIgnoreCase)).ToArray();
            var normal = active.Where(f => !f.Get("isHard").Bool()).Select(f => FissureLine(f, schedule)); var steel = active.Where(f => f.Get("isHard").Bool()).Select(f => FissureLine(f, schedule));
            return [new WorldBoard(key, $"{ApplicationEmojis.ThraxPlasm} Void Cascade Watch", "**Normal Void Cascade fissures**\n" + Lines(normal, FissureEmpty(data)) + "\n\n**Steel Path Void Cascade fissures**\n" + Lines(steel, FissureEmpty(data)), 0x00A8CC)];
        }
        return [];
    }
    private static string Title(this string value) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());
    public static IReadOnlyDictionary<string, string[]> Signatures(WorldSnapshot data, IReadOnlyList<ArbitrationEntry> schedule)
    {
        var now = DateTimeOffset.UtcNow; var result = new Dictionary<string, string[]>();
        var all = ActiveRows(data.World.Get("fissures"), now).Where(f => !f.Get("isStorm").Bool()).ToArray();
        string Id(JsonElement f) => f.Get("id").Text($"{f.Get("node").Text()}|{f.Get("tier").Text()}|{f.Get("expiry").Text()}");
        void Add(string key, IEnumerable<JsonElement> rows) => result[key] = rows.Select(Id).Order(StringComparer.Ordinal).ToArray();
        Add("fissures", all.Where(f => !f.Get("isHard").Bool())); Add("steel_fissures", all.Where(f => f.Get("isHard").Bool()));
        var storms = ActiveRows(data.World.Get("fissures"), now).Where(f => f.Get("isStorm").Bool()).ToArray(); Add("void_storms", storms);
        foreach (var tier in new[] { "Lith", "Meso", "Neo", "Axi", "Requiem", "Omnia" })
        { Add("fissure_" + tier.ToLowerInvariant(), all.Where(f => !f.Get("isHard").Bool() && f.Get("tier").Text() == tier)); Add("steel_fissure_" + tier.ToLowerInvariant(), all.Where(f => f.Get("isHard").Bool() && f.Get("tier").Text() == tier)); Add("void_storm_" + tier.ToLowerInvariant(), storms.Where(f => f.Get("tier").Text() == tier)); }
        Add("cascade", all.Where(f => !f.Get("isHard").Bool() && f.Get("missionType").Text().Equals("Void Cascade", StringComparison.OrdinalIgnoreCase)));
        Add("steel_cascade", all.Where(f => f.Get("isHard").Bool() && f.Get("missionType").Text().Equals("Void Cascade", StringComparison.OrdinalIgnoreCase)));
        var current = ArbitrationSchedule.Current(schedule, now.ToUnixTimeSeconds()); result["arbitration"] = current is null ? [] : [$"{current.Activation}"];
        foreach (var tier in new[] { "S", "A", "B", "C", "D", "F" }) result["arbitration_" + tier.ToLowerInvariant()] = current?.Tier == tier ? [$"{current.Activation}"] : [];
        void Raw(string key, IEnumerable<JsonElement> rows) => result[key] = rows.Select(r => r.Get("id").Text(r.Get("expiry").Text(r.Get("activation").Text(r.GetRawText())))).Order(StringComparer.Ordinal).ToArray();
        Raw("alerts", ActiveRows(data.World.Get("alerts"), now)); Raw("events", ActiveRows(data.World.Get("events"), now));
        Raw("invasions", data.World.Get("invasions").Rows().Where(i => !i.Get("completed").Bool())); Raw("news", data.World.Get("news").Rows());
        var sortie = data.World.Get("sortie"); Raw("sortie", sortie.ValueKind == JsonValueKind.Object && Active(sortie, now) ? [sortie] : []);
        var archon = data.World.Get("archonHunt"); Raw("archon", archon.ValueKind == JsonValueKind.Object && Active(archon, now) ? [archon] : []);
        var steel = data.World.Get("steelPath"); Raw("steel_path", steel.ValueKind == JsonValueKind.Object ? [steel] : []);
        Raw("archimedea", data.World.Get("archimedeas").Rows());
        var baro = data.World.Get("voidTrader"); Raw("baro", baro.ValueKind == JsonValueKind.Object ? [baro] : []);
        var bountySections = data.Bounty.Get("bounties");
        result["bounties"] = bountySections.ValueKind == JsonValueKind.Object
            ? bountySections.EnumerateObject().SelectMany(section => section.Value.Rows().Select(row => $"{section.Name}|{row.Get("node").Text()}|{row.Get("challenge").Text()}|{row.Get("ally").Text()}")).Order(StringComparer.Ordinal).ToArray()
            : [];
        return result;
    }
}
