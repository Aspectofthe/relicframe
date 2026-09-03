using System.Text.RegularExpressions;
using System.Text.Json;

namespace RelicFrame.Core;

public sealed record RuleAlternative(IReadOnlyList<string[]> Mandatory, string[] Pool);
public sealed record RollRule(IReadOnlyList<RuleAlternative> Alternatives, string[] HarmlessNegatives,
    string PositiveExpression = "", string Notes = "");
public sealed record RollMatch(bool Qualifies, int DesiredCount, int PositiveCount, bool HarmlessNegative,
    bool NoDeadPositive, bool MandatoryMatched, int Alternative);

public static class RivenRules
{
    public static readonly IReadOnlyDictionary<string, string> Tokens = new Dictionary<string, string>
    {
        ["AMMO"]="ammo_maximum", ["AS"]="fire_rate_/_attack_speed", ["CC"]="critical_chance", ["CD"]="critical_damage",
        ["COLD"]="cold_damage", ["DMG"]="base_damage_/_melee_damage", ["DTC"]="damage_vs_corpus", ["DTG"]="damage_vs_grineer",
        ["DTI"]="damage_vs_infested", ["EFF"]="channeling_efficiency", ["ELEC"]="electric_damage", ["FIN"]="finisher_damage",
        ["FR"]="fire_rate_/_attack_speed", ["HEAT"]="heat_damage", ["IC"]="channeling_damage", ["IMP"]="impact_damage",
        ["MAG"]="magazine_capacity", ["MS"]="multishot", ["PFS"]="projectile_speed", ["PT"]="punch_through",
        ["PUNC"]="puncture_damage", ["RANGE"]="range", ["REC"]="recoil", ["RECOIL"]="recoil", ["RLS"]="reload_speed",
        ["SC"]="status_chance", ["SD"]="status_duration", ["SLASH"]="slash_damage", ["SLIDE"]="critical_chance_on_slide_attack",
        ["TOX"]="toxin_damage", ["ZOOM"]="zoom"
    };
    private static IEnumerable<string> Expand(string token)
    {
        token = token.Trim().ToUpperInvariant();
        return token == "ELEMENT" ? ["cold_damage", "electric_damage", "heat_damage", "toxin_damage"]
            : Tokens.TryGetValue(token, out var slug) ? [slug] : throw new ArgumentException($"Unknown Riven shorthand: {token}");
    }
    public static RuleAlternative[] Parse(string expression)
    {
        var result = new List<RuleAlternative>();
        foreach (var alternative in Regex.Split(expression.Trim(), @"\s+or\s+", RegexOptions.IgnoreCase))
        {
            var mandatory = new List<string[]>(); var pool = new HashSet<string>();
            foreach (var group in alternative.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var pieces = group.Trim(',', ';').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (pieces.Length == 0) continue;
                var options = pieces.SelectMany(Expand).Distinct().Order(StringComparer.Ordinal).ToArray();
                if (pieces.Length == 1) mandatory.Add(options); else pool.UnionWith(options);
            }
            if (mandatory.Count > 0 || pool.Count > 0) result.Add(new(mandatory, pool.Order(StringComparer.Ordinal).ToArray()));
        }
        return result.Count == 0 ? throw new ArgumentException("Riven expression is empty") : result.ToArray();
    }
    public static string[] ParseNegatives(string expression) => Regex.Matches(expression, "[A-Za-z]+")
        .Select(m => m.Value).Where(v => !v.Equals("or", StringComparison.OrdinalIgnoreCase)).SelectMany(Expand).Distinct().Order(StringComparer.Ordinal).ToArray();
    public static RollMatch? Evaluate(IEnumerable<string> positives, IEnumerable<string> negatives, RollRule? rule)
    {
        if (rule is null) return null;
        var pos = positives.Select(s => s.ToLowerInvariant()).ToHashSet(); var neg = negatives.Select(s => s.ToLowerInvariant()).ToHashSet();
        var harmless = neg.Count > 0 && neg.IsSubsetOf(rule.HarmlessNegatives.Select(s => s.ToLowerInvariant()));
        RollMatch? best = null;
        for (var i = 0; i < rule.Alternatives.Count; i++)
        {
            var alt = rule.Alternatives[i]; var desired = alt.Pool.Concat(alt.Mandatory.SelectMany(g => g)).ToHashSet();
            var mandatory = alt.Mandatory.All(g => g.Any(pos.Contains));
            var count = pos.Count(desired.Contains); var clean = pos.Count > 0 && pos.IsSubsetOf(desired);
            var candidate = new RollMatch(count >= 2 && mandatory && clean && harmless, count, pos.Count, harmless, clean, mandatory, i);
            static (bool, int, bool, bool) Key(RollMatch r) => (r.Qualifies, r.DesiredCount, r.MandatoryMatched, r.NoDeadPositive);
            if (best is null || Key(candidate).CompareTo(Key(best)) > 0) best = candidate;
        }
        return best;
    }
    public static RollRule Read(JsonElement e) => new(e.Get("alternatives").Rows().Select(a => new RuleAlternative(
        a.Get("mandatory").Rows().Select(g => g.Rows().Select(x => x.Text()).ToArray()).ToArray(),
        a.Get("pool").Rows().Select(x => x.Text()).ToArray())).ToArray(),
        e.Get("harmless_negatives").Rows().Select(x => x.Text()).ToArray(), e.Get("positive_expression").Text(), e.Get("notes").Text());
}
