using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RelicFrame.Core;

public static class OfficialDropTables
{
    public const string Url = "https://warframe-web-assets.nyc3.cdn.digitaloceanspaces.com/uploads/cms/hnfvc0o3jnfvc873njb03enrf56.html";
    private static readonly Regex Section = new("<h3\\s+id=[\"']relicRewards[\"'][^>]*>.*?</h3>(.*?)<h3\\b", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex Row = new("<tr\\b[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex Cell = new("<(th|td)\\b[^>]*>(.*?)</\\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex Tag = new("<[^>]+>", RegexOptions.Singleline);
    private static readonly Regex Header = new("^(Lith|Meso|Neo|Axi|Requiem)(?:\\s+(\\S+))?\\s+Relic\\s+\\((Intact|Exceptional|Flawless|Radiant)\\)$", RegexOptions.IgnoreCase);
    private static readonly Regex Chance = new("^(?:Very Common|Common|Uncommon|Rare|Ultra Rare|Legendary)\\s*\\(([\\d.]+)%\\)$", RegexOptions.IgnoreCase);

    public static IReadOnlyDictionary<string, Relic> ParseRelics(string html)
    {
        var section = Section.Match(html);
        if (!section.Success) throw new InvalidDataException("Official drop-table HTML has no Relics section");
        var result = new Dictionary<string, Relic>(StringComparer.Ordinal);
        string? name = null;
        string? tier = null;
        foreach (Match row in Row.Matches(section.Groups[1].Value))
        {
            var cells = Cell.Matches(row.Groups[1].Value).Cast<Match>().ToArray();
            string Value(Match cell) => WebUtility.HtmlDecode(Tag.Replace(cell.Groups[2].Value, "")).Trim();
            if (cells.Length == 1 && cells[0].Groups[1].Value.Equals("th", StringComparison.OrdinalIgnoreCase))
            {
                var header = Header.Match(Value(cells[0]));
                if (header.Success)
                {
                    name = (header.Groups[1].Value + " " + header.Groups[2].Value).Trim();
                    tier = header.Groups[3].Value.ToLowerInvariant();
                    if (tier == "intact") result[name] = new Relic(name, new List<Reward>());
                }
                else name = tier = null;
            }
            else if (name is not null && tier == "intact" && cells.Length == 2)
            {
                var chance = Chance.Match(Value(cells[1]));
                if (!chance.Success || !double.TryParse(chance.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) continue;
                var rarity = Math.Abs(pct - 2) <= .05 ? "rare" : Math.Abs(pct - 11) <= .05 ? "uncommon" : Math.Abs(pct - 25.33) <= .05 ? "common" : null;
                if (rarity is not null && result.TryGetValue(name, out var relic))
                    ((List<Reward>)relic.Rewards).Add(new Reward(Value(cells[0]), rarity));
            }
            else if (cells.Length != 0) name = tier = null;
        }
        return result.Where(pair => pair.Value.Rewards.Count == 6).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    public static async Task<string> RefreshAsync(string csvPath, bool checkOnly, HttpClient? client = null, string? htmlOverride = null)
    {
        using var owned = client is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(60) } : null;
        var html = htmlOverride ?? await (client ?? owned!).GetStringAsync(Url);
        var published = Regex.Match(html, "Last Update:</b>\\s*([^<\\r\\n]+)", RegexOptions.IgnoreCase);
        var latest = ParseRelics(html);
        if (latest.Count < 700) throw new InvalidDataException($"Only {latest.Count} standard relics found; CSV not replaced");
        var previous = File.Exists(csvPath) ? Relic.LoadCsv(csvPath) : new Dictionary<string, Relic>();
        var merged = new Dictionary<string, Relic>(latest, StringComparer.Ordinal);
        foreach (var (name, relic) in previous)
        {
            if (!merged.ContainsKey(name)) merged[name] = relic;
            else merged[name] = merged[name] with { Vaulted = relic.Vaulted };
        }
        var added = latest.Keys.Except(previous.Keys).Count();
        var retained = previous.Keys.Except(latest.Keys).Count();
        var summary = $"[official-drops] published={published.Groups[1].Value.Trim()} official={latest.Count} new={added} historical_retained={retained} catalog={merged.Count}";
        if (checkOnly) return summary + "; check only, CSV unchanged";

        var directory = Path.GetDirectoryName(Path.GetFullPath(csvPath))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".official-relics-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
            {
                await writer.WriteLineAsync("relic_name,reward_name,rarity,vaulted");
                foreach (var relic in merged.Values.OrderBy(relic => relic.RelicName, StringComparer.Ordinal))
                    foreach (var reward in relic.Rewards)
                        await writer.WriteLineAsync($"{Csv(relic.RelicName)},{Csv(reward.RewardName)},{Csv(reward.Rarity)},{relic.Vaulted?.ToString() ?? ""}");
            }
            File.Move(temporary, csvPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return summary + "; CSV updated; restart the bot";
    }

    private static string Csv(string value) => value.Contains(',') || value.Contains('"') || value.Contains('\n')
        ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
