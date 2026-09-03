using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RelicFrame.Core;

public sealed record ArbitrationEntry(long Activation, long Expiry, string Text, string MissionType, string Enemy,
    string Location, string Tier, string? ResourceBonus);
public sealed record WorldSnapshot(JsonElement World, JsonElement Bounty, DateTimeOffset FetchedAt, bool Stale,
    string Source, string[] RepairedSections);
public sealed record WorldBoard(string Key, string Title, string Description, uint Color = 0x5A67D8);

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
    public static ArbitrationEntry? Current(IEnumerable<ArbitrationEntry> entries, long now) => entries.LastOrDefault(e => e.Activation <= now && now < e.Expiry);
    public static string? TierFor(IEnumerable<ArbitrationEntry> entries, string node, string mission)
    {
        static string Key(string value) => Regex.Replace(value, "\\s*\\([^)]*\\)\\s*$", "").Split(',')[0].Trim().ToLowerInvariant();
        var wanted = Key(node); var matches = entries.Where(e => Key(e.Location) == wanted).ToArray();
        return matches.FirstOrDefault(e => e.MissionType.Equals(mission, StringComparison.OrdinalIgnoreCase))?.Tier
            ?? (matches.Select(e => e.Tier).Distinct(StringComparer.Ordinal).Take(2).Count() == 1 ? matches[0].Tier : null);
    }
}

public sealed class WorldStateClient : IDisposable
{
    private readonly HttpClient client;
    private JsonElement bounty;
    public WorldStateClient(HttpMessageHandler? handler = null)
    {
        client = handler is null ? new(new SocketsHttpHandler { MaxConnectionsPerServer = 2, AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) : new(handler);
        client.Timeout = TimeSpan.FromSeconds(25); client.DefaultRequestHeaders.UserAgent.ParseAdd("RelicFrame-CSharp/0.1");
    }
    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode >= 500 && attempt < 2) { await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct); continue; }
                response.EnsureSuccessStatusCode(); await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024, ct);
                await using var stream = await response.Content.ReadAsStreamAsync(ct); return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (HttpRequestException) when (attempt < 2) { await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct); }
        }
    }
    public async Task<WorldSnapshot> FetchAsync(CancellationToken ct)
    {
        using var world = await GetAsync("https://api.warframestat.us/pc", ct);
        if (world.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid world-state payload.");
        try { using var b = await GetAsync("https://oracle.browse.wf/bounty-cycle", ct); if (b.RootElement.ValueKind == JsonValueKind.Object) bounty = b.RootElement.Clone(); }
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
                    using var nodes = await GetAsync("https://api.warframestat.us/solNodes", ct);
                    var normalized = OfficialFissures(official.RootElement, nodes.RootElement);
                    var root = JsonNode.Parse(copy.GetRawText())!.AsObject(); root["fissures"] = JsonSerializer.SerializeToNode(normalized);
                    using var merged = JsonDocument.Parse(root.ToJsonString()); copy = merged.RootElement.Clone(); repaired = ["fissures"]; source = "official Warframe fallback + WarframeStat.us";
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or JsonException or TaskCanceledException) { }
        }
        return new(copy, bounty, DateTimeOffset.UtcNow, stale, source, repaired);
    }
    private static object[] OfficialFissures(JsonElement raw, JsonElement nodes)
    {
        var tiers = new Dictionary<string, (string Name, int Number)> { ["VoidT1"]=("Lith",1), ["VoidT2"]=("Meso",2), ["VoidT3"]=("Neo",3), ["VoidT4"]=("Axi",4), ["VoidT5"]=("Requiem",5), ["VoidT6"]=("Omnia",6) };
        var missions = new Dictionary<string, string> { ["MT_EXTERMINATION"]="Extermination", ["MT_SURVIVAL"]="Survival", ["MT_RESCUE"]="Rescue", ["MT_MOBILE_DEFENSE"]="Mobile Defense", ["MT_DEFENSE"]="Defense", ["MT_INTEL"]="Spy", ["MT_TERRITORY"]="Interception", ["MT_SABOTAGE"]="Sabotage", ["MT_VOID_CASCADE"]="Void Cascade", ["MT_ALCHEMY"]="Alchemy", ["MT_ASSASSINATION"]="Assassination", ["MT_DISRUPTION"]="Disruption", ["MT_CAPTURE"]="Capture", ["MT_EXCAVATE"]="Excavation" };
        static string MongoDate(JsonElement value)
        {
            var number = value.Get("$date").Get("$numberLong").Text();
            return long.TryParse(number, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O") : "";
        }
        static string Id(JsonElement value) => value.ValueKind == JsonValueKind.Object ? value.Get("$oid").Text() : value.Text();
        string Node(string key) => nodes.Get(key).Get("value").Text(key.Length > 0 ? key : "Unknown node");
        string NodeMission(string key) => nodes.Get(key).Get("type").Text("Skirmish");
        var result = new List<object>();
        foreach (var row in raw.Get("ActiveMissions").Rows())
        {
            var modifier = row.Get("Modifier").Text(); var tier = tiers.GetValueOrDefault(modifier, (modifier.Length > 0 ? modifier : "?", 99)); var node = row.Get("Node").Text(); var mission = row.Get("MissionType").Text();
            result.Add(new { id=Id(row.Get("_id")), activation=MongoDate(row.Get("Activation")), expiry=MongoDate(row.Get("Expiry")), tier=tier.Item1, tierNum=tier.Item2,
                missionType=missions.GetValueOrDefault(mission, NodeMission(node)), node=Node(node), isHard=row.Get("Hard").Bool(), isStorm=false });
        }
        foreach (var row in raw.Get("VoidStorms").Rows())
        {
            var modifier = row.Get("ActiveMissionTier").Text(); var tier = tiers.GetValueOrDefault(modifier, (modifier.Length > 0 ? modifier : "?", 99)); var node = row.Get("Node").Text();
            result.Add(new { id=Id(row.Get("_id")), activation=MongoDate(row.Get("Activation")), expiry=MongoDate(row.Get("Expiry")), tier=tier.Item1, tierNum=tier.Item2,
                missionType=NodeMission(node), node=Node(node), isHard=false, isStorm=true });
        }
        return result.ToArray();
    }
    public void Dispose() => client.Dispose();
}

public static class WorldRender
{
    private const string Footer = "Live world state • auto-updates • WarframeStat.us";
    public static DateTimeOffset? Time(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch)) return DateTimeOffset.FromUnixTimeSeconds(epoch);
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
    private static string FissureLine(JsonElement f, IReadOnlyList<ArbitrationEntry> schedule)
    {
        var tier = ArbitrationSchedule.TierFor(schedule, f.Get("node").Text(), f.Get("missionType").Text());
        var level = tier is null ? "" : $" · **Lvl {tier} tier**";
        return $"**{f.Get("tier").Text("?")} · {f.Get("missionType").Text("Unknown")}**{level} · {f.Get("node").Text("Unknown node")} · {Stamp(f.Get("expiry"))}";
    }
    private static IEnumerable<JsonElement> Fissures(WorldSnapshot data, bool hard, bool storm)
    {
        var now = DateTimeOffset.UtcNow;
        return ActiveRows(data.World.Get("fissures"), now).Where(f => f.Get("isStorm").Bool() == storm && (storm || f.Get("isHard").Bool() == hard))
            .OrderBy(f => f.Get("tierNum").Number() ?? 99).ThenBy(f => Time(f.Get("expiry")));
    }
    public static WorldBoard[] Build(string key, WorldSnapshot data, IReadOnlyList<ArbitrationEntry> schedule)
    {
        var world = data.World; var now = DateTimeOffset.UtcNow;
        if (key == "bot-guide") return [Board(key, "RelicFrame guide", "Use `/rf-status` for health; `/rf-relics` for pricing, odds and Buy-N; `/rf-riven` for resale candidates; `/rf-companion` for evidence-based manual-trait estimates; and the role menu in #role-pings for optional live notifications. Listings and estimates are not guaranteed sales. Managers can stop background refreshes before changing filters or restarting them.", 0x5865F2)];
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
            var alerts = ActiveRows(world.Get("alerts"), now).Select(a => { var m = a.Get("mission"); return $"**{m.Get("type").Text("Unknown")} — {m.Get("faction").Text("Unknown")}** ({m.Get("minEnemyLevel").Text("?")}–{m.Get("maxEnemyLevel").Text("?")}) · {m.Get("node").Text("Unknown node")} · {Stamp(a.Get("expiry"))}\n↳ {Reward(m.Get("reward"))}"; });
            var events = ActiveRows(world.Get("events"), now).Select(e => $"**{e.Get("description").Text(e.Get("tooltip").Text(e.Get("tag").Text("Event")))}** · {Stamp(e.Get("expiry"))}");
            return [Board(key, "🚨 Alerts", Lines(alerts, Empty(data)), 0xE74C3C), Board(key, "🎉 Events", Lines(events, Empty(data)), 0xF39C12)];
        }
        if (key == "world-sortie")
        {
            var s = world.Get("sortie"); var rows = s.Get("variants").Rows().Select(m => $"**{m.Get("missionType").Text("Unknown")}** · {m.Get("modifier").Text("No modifier")} · {m.Get("node").Text()}");
            return [Board(key, "🎯 Sortie", s.ValueKind == JsonValueKind.Object && Active(s, now) ? $"**{s.Get("boss").Text("Unknown boss")}** · resets {Stamp(s.Get("expiry"))}\n{Lines(rows, "No stages listed.")}" : "Current Sortie data is temporarily unavailable.", 0xE67E22)];
        }
        if (key == "world-archon")
        {
            var a = world.Get("archonHunt"); var missions = a.Get("missions").Rows().Select(m => m.Get("type").Text(m.Get("missionType").Text())).Where(s => s.Length > 0);
            return [Board(key, "🐺 Archon Hunt", a.ValueKind == JsonValueKind.Object && Active(a, now) ? $"**{a.Get("boss").Text("Unknown Archon")}** · {string.Join(", ", missions)}\nResets {Stamp(a.Get("expiry"))}" : "Current Archon Hunt data is temporarily unavailable.", 0xC0392B)];
        }
        if (key == "world-steel-path")
        {
            var steel = world.Get("steelPath"); var reward = steel.Get("currentReward");
            return [Board(key, "💀 Steel Path", reward.ValueKind == JsonValueKind.Object ? $"Weekly Honors: **{reward.Get("name").Text("Unknown offering")}** · {reward.Get("cost").Text("?")} Steel Essence\nResets {Stamp(steel.Get("expiry"))}" : "Current Steel Path rotation is temporarily unavailable.", 0x2C3E50)];
        }
        if (key == "world-weekly")
        {
            var choices = world.Get("duviriCycle").Get("choices").Rows().Select(c => $"• The Circuit ({c.Get("category").Text("unknown").Title()}): **{string.Join(", ", c.Get("choices").Rows().Select(x => x.Text()))}**");
            return [Board(key, "📅 Weekly Missions", Lines(new[] { "• Help Clem", "• Ayatan Treasure Hunt" }.Concat(choices).Concat(["• Netracells", "• Break Narmer", "• Descendia"]), "Weekly data unavailable."), 0x16A085)];
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
            if (start <= now && (!end.HasValue || now < end)) baroText = $"At **{baro.Get("location").Text("Unknown relay")}** · leaves {Stamp(baro.Get("expiry"))}\n" + Lines(baro.Get("inventory").Rows().Take(20).Select(i => $"• {i.Get("item").Text("Unknown")} — {i.Get("ducats").Text("?")} Ducats + {i.Get("credits").Text("?")} Credits"), "Inventory unavailable.");
            else baroText = start.HasValue ? $"Next visit: **{baro.Get("location").Text("Unknown relay")}** · arrives {Stamp(baro.Get("activation"))}" : "Baro's next visit is temporarily unavailable.";
            return [Board(key, "🛍️ Darvo's Deal", dealText, 0x3498DB), Board(key, "🏪 Vendors", "Steel Path Honors and Iron Wake weekly offerings are shown in their live rotations.", 0x95A5A6), Board(key, "🛒 Baro Ki'Teer", baroText, 0xF1C40F)];
        }
        if (key == "world-bounties")
        {
            var b = data.Bounty; var sections = b.Get("bounties"); var counts = sections.ValueKind == JsonValueKind.Object ? sections.EnumerateObject().Select(p => $"**{p.Name}** · {p.Value.Rows().Count()} jobs") : [];
            return [Board(key, "📋 Bounties", b.ValueKind == JsonValueKind.Object ? $"Rotation **{b.Get("rot").Text("?")}** · Vault **{b.Get("vaultRot").Text("?")}**\n{Lines(counts, "No bounty jobs listed.")}" : "Current bounty rotations are temporarily unavailable.", 0x27AE60)];
        }
        if (key is "world-fissures" or "world-steel-fissures" or "world-void-storms")
        {
            var hard = key == "world-steel-fissures"; var storm = key == "world-void-storms";
            var title = storm ? "🚀 Void Storms (Railjack)" : hard ? "🌀 Void Fissures (Steel Path)" : "🌀 Void Fissures (Normal)";
            return [Board(key, title, Lines(Fissures(data, hard, storm).Select(f => FissureLine(f, schedule)), FissureEmpty(data)), storm ? 0x2980B9u : hard ? 0x34495Eu : 0x8E44ADu)];
        }
        if (key == "world-invasions")
        {
            var rows = world.Get("invasions").Rows().Where(i => !i.Get("completed").Bool()).Select(i => $"**{i.Get("node").Text("Unknown node")}** · {i.Get("desc").Text("Invasion")} · {i.Get("completion").Number() ?? 0:0.0}%\n↳ {i.Get("attacker").Get("faction").Text("?")}: {Reward(i.Get("attacker").Get("reward"))} | {i.Get("defender").Get("faction").Text("?")}: {Reward(i.Get("defender").Get("reward"))}");
            return [Board(key, "⚔️ Invasions", Lines(rows, Empty(data)), 0xD35400)];
        }
        if (key == "world-arbitration")
        {
            var epoch = now.ToUnixTimeSeconds(); var current = ArbitrationSchedule.Current(schedule, epoch); string head;
            if (current is not null) head = $"**{current.MissionType} — {current.Enemy}**\n{current.Location} · **{current.Tier} tier**{(current.ResourceBonus is null ? "" : " · " + current.ResourceBonus)} · ends <t:{current.Expiry}:R>";
            else { var a = world.Get("arbitration"); head = a.ValueKind == JsonValueKind.Object && !a.Get("expired").Bool() ? $"**{a.Get("type").Text("Unknown")} — {a.Get("enemy").Text("Unknown")}**\n{a.Get("node").Text("Unknown")} · ends {Stamp(a.Get("expiry"))}" : "Current Arbitration unavailable."; }
            var upcoming = schedule.Where(e => e.Activation > epoch).Take(6).Select(e => $"<t:{e.Activation}:f> · **{e.MissionType}** · {e.Location} · {e.Tier} tier");
            return [Board(key, "⚖️ Arbitration", head + "\n\n**Coming next**\n" + Lines(upcoming, "No schedule available."), 0xF39C12)];
        }
        if (key == "world-cascade")
        {
            var active = ActiveRows(world.Get("fissures"), now).Where(f => !f.Get("isStorm").Bool() && f.Get("missionType").Text().Equals("Void Cascade", StringComparison.OrdinalIgnoreCase)).ToArray();
            var normal = active.Where(f => !f.Get("isHard").Bool()).Select(f => FissureLine(f, schedule)); var steel = active.Where(f => f.Get("isHard").Bool()).Select(f => FissureLine(f, schedule));
            return [Board(key, "🌊 Void Cascade Watch", "**Normal Void Cascade fissures**\n" + Lines(normal, FissureEmpty(data)) + "\n\n**Steel Path Void Cascade fissures**\n" + Lines(steel, FissureEmpty(data)) + "\n\nChoose normal and/or Steel Path Cascade notifications in **#role-pings**.", 0x00A8CC)];
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
        result["bounties"] = data.Bounty.ValueKind == JsonValueKind.Object ? [$"{data.Bounty.Get("rot").Text()}|{data.Bounty.Get("expiry").Text()}"] : [];
        return result;
    }
}
