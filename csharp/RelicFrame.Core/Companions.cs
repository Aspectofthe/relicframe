using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RelicFrame.Core;

public static class CompanionColors
{
    public static readonly HashSet<string> Common = ["ash grey", "earth brown", "corpus grey", "hek green", "kril brown", "gallium grey", "grustrag grey", "saturn brown"];
    public static readonly HashSet<string> Uncommon = ["sedna grey", "derelict black", "mars red", "infested black", "void black", "darvo blue", "ordis grey", "mercury brown"];
    public static readonly HashSet<string> Rare = ["anyo grey", "ambulas black", "shadow grey", "sargas brown", "jupiter brown", "phorid red", "alad blue", "venus brown"];
    public static string Canonical(string value)
    {
        var key = Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");
        return key switch
        {
            "stripe" => "striped", "orange" or "red" => "orange/red", "ambulas black (dark brown)" => "ambulas black",
            "anyo grey (navy)" => "anyo grey", "shadow grey (cream)" => "shadow grey", "sargas brown (gold)" => "sargas brown",
            "jupiter brown (orange)" => "jupiter brown", "venus brown (purple)" => "venus brown", _ => key
        };
    }
    public static string Rarity(IEnumerable<string> colors)
    {
        var slots = colors.Select(Canonical).Where(c => Common.Contains(c) || Uncommon.Contains(c) || Rare.Contains(c)).ToArray();
        if (slots.Length == 0) return "unknown";
        var rare = slots.Where(Rare.Contains).ToArray();
        if (slots.Length >= 3 && slots.Distinct().Count() == 1)
            return Rare.Contains(slots[0]) ? slots.Length >= 4 ? "quad solid" : "solid rare" : Uncommon.Contains(slots[0]) ? "solid uncommon" : "solid common";
        return rare.Length switch { >= 4 => "quad rare", 3 => "triple rare", 2 => rare.Distinct().Count() == 1 ? "double same rare" : "double rare",
            1 => "single rare", _ => slots.Any(Uncommon.Contains) ? "uncommon" : "common" };
    }
}

public sealed record CompanionEvidence(string Timestamp, IReadOnlyDictionary<string, string[]> Traits, string Classification,
    double Low, double High, bool Attachments, string Source, double SourceWeight);
public sealed record Appraisal(int Estimate, int Low, int High, int ComparableCount, int ScreenshotCount, string Confidence,
    int ExactTraitMatches, string NewestTimestamp, int CurrentComparableCount, int HistoricalComparableCount);
public sealed class CompanionAppraiser
{
    private static readonly Dictionary<string, double> Groups = new() { ["species"] = 4, ["breed"] = 1.5, ["pattern"] = 2.5, ["build"] = 2, ["rarity"] = 2.5, ["color"] = 1 };
    private static readonly Dictionary<string, double> Classes = new() { ["confirmed_sale"] = 1, ["listing"] = .75, ["price_mention"] = .45, ["appraisal"] = .35 };
    public IReadOnlyList<CompanionEvidence> Records { get; }
    private readonly DateTimeOffset? newest;
    private static DateTimeOffset? Time(string text) => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
    public CompanionAppraiser(IEnumerable<CompanionEvidence> records)
    {
        Records = records.ToArray(); newest = Records.Select(r => Time(r.Timestamp)).Where(t => t.HasValue).Max();
    }
    public static IEnumerable<CompanionEvidence> Load(string path, string source = "current", double sourceWeight = 1)
    {
        var unique = new Dictionary<string, CompanionEvidence>();
        if (!File.Exists(path)) return [];
        foreach (var line in File.ReadLines(path))
        {
            using var doc = JsonDocument.Parse(line); var r = doc.RootElement;
            var amounts = r.Get("amounts").Rows().ToArray(); if (amounts.Length != 1) continue;
            var low = amounts[0].Get("low").Number() ?? 0; var high = amounts[0].Get("high").Number() ?? low;
            if (low < 5 || high < low || high > 10_000) continue;
            var text = Regex.Replace(r.Get("text").Text().ToLowerInvariant().Trim(), @"\s+", " ");
            var assets = r.Get("attachments").Rows().Select(a => a.Get("filename").Text() + ":" + a.Get("url").Text()).Order(StringComparer.Ordinal);
            var fingerprint = string.Join('\0', new[] { text }.Concat(assets));
            if (fingerprint.Length == 0) fingerprint = r.Get("message_id").Text();
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));
            var traits = new Dictionary<string, string[]>();
            if (r.Get("traits").ValueKind == JsonValueKind.Object)
                foreach (var p in r.Get("traits").EnumerateObject()) traits[p.Name] = p.Value.Rows().Select(v => v.Text()).ToArray();
            var row = new CompanionEvidence(r.Get("timestamp").Text(), traits, r.Get("classification").Text(), low, high,
                r.Get("attachments").Rows().Any(), source, sourceWeight);
            if (!unique.TryGetValue(key, out var prev) || string.CompareOrdinal(row.Timestamp, prev.Timestamp) >= 0) unique[key] = row;
        }
        return unique.Values;
    }
    public Appraisal Appraise(IReadOnlyDictionary<string, string?> requested)
    {
        if (Records.Count == 0) throw new InvalidOperationException("Companion evidence is missing; transfer the private JSONL files separately.");
        var traits = requested.Where(p => !string.IsNullOrEmpty(p.Value)).ToDictionary(p => p.Key, p => p.Value!.ToLowerInvariant());
        var candidates = Records.Select(row =>
        {
            double score = 0; int exact = 0;
            foreach (var (group, value) in traits)
            {
                var found = row.Traits.GetValueOrDefault(group, []).Select(s => s.ToLowerInvariant()).ToHashSet();
                var w = Groups.GetValueOrDefault(group, 1);
                if (found.Contains(value)) { score += w; exact++; } else if (found.Count > 0) score -= w * .75;
            }
            return (Row: row, Score: score, Exact: exact);
        }).Where(c => c.Score > 0).ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("No usable comparables match those traits.");
        var best = candidates.Max(c => c.Score);
        var selected = candidates.Where(c => c.Score >= best - 1.5).ToArray();
        var weighted = selected.Select(c =>
        {
            var date = Time(c.Row.Timestamp);
            var age = newest.HasValue && date.HasValue ? Math.Max(0, (newest.Value - date.Value).TotalDays) : 365;
            var weight = Math.Max(.0001, c.Row.SourceWeight * Classes.GetValueOrDefault(c.Row.Classification, .25) * Math.Pow(.5, age / 120) * Math.Pow(1.8, c.Score));
            return (Value: (c.Row.Low + c.Row.High) / 2, Weight: weight, c.Row.Source);
        }).ToArray();
        var currentWeight = weighted.Where(w => w.Source == "current").Sum(w => w.Weight);
        var historicalWeight = weighted.Where(w => w.Source == "historical").Sum(w => w.Weight);
        var scale = currentWeight > 0 && historicalWeight > currentWeight * .35 ? currentWeight * .35 / historicalWeight : 1;
        var values = weighted.Select(w => (w.Value, Weight: w.Weight * (w.Source == "historical" ? scale : 1))).OrderBy(w => w.Value).ThenBy(w => w.Weight).ToArray();
        int Quantile(double q)
        {
            var target = values.Sum(w => w.Weight) * q; double sum = 0;
            foreach (var (value, weight) in values) { sum += weight; if (sum >= target) return RoundPlat(value); }
            return RoundPlat(values[^1].Value);
        }
        var estimate = Quantile(.5); var current = selected.Count(c => c.Row.Source == "current");
        var exactCurrent = selected.Count(c => c.Row.Source == "current" && c.Exact == traits.Count);
        return new(estimate, Math.Min(estimate, Quantile(.2)), Math.Max(estimate, Quantile(.8)), selected.Length,
            selected.Count(c => c.Row.Attachments), current >= 20 && exactCurrent >= 8 ? "High" : current >= 8 && exactCurrent >= 3 ? "Medium" : "Low",
            selected.Count(c => c.Exact == traits.Count), selected.Select(c => c.Row.Timestamp).Max(StringComparer.Ordinal) ?? "",
            current, selected.Count(c => c.Row.Source == "historical"));
    }
    private static int RoundPlat(double value) { var step = value < 1000 ? 10 : 50; return Math.Max(step, (int)Math.Round(value / step) * step); }
}
