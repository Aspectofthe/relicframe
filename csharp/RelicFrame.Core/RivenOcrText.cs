using System.Globalization;
using System.Text.RegularExpressions;

namespace RelicFrame.Core;

public sealed record RivenOcrStat(string Name, double? Value);
public sealed record RivenOcrDraft(string WeaponName, int? ModRank, int? MasteryRank, int? Rerolls,
    RivenOcrStat[] Positives, RivenOcrStat? Negative, double Confidence, string[] Notes, string RawText = "");

public static partial class RivenOcrText
{
    private sealed record Candidate(string Slug, double Value, bool Negative, double Score, int Pass, double Position);
    private sealed record ResolvedCandidate(string Slug, double Value, bool Negative, double Score, int Support, double Position);
    [GeneratedRegex(@"(?<multiplier>[xX×])?\s*(?<sign>[+\-−–—])?\s*(?<number>[0-9Oo]{1,4}(?:[.,][0-9Oo]{1,3})?)\s*%?[^A-Za-z\r\n]{0,6}(?<name>[A-Za-z][A-Za-z /&().'-]{2,})", RegexOptions.IgnoreCase)]
    private static partial Regex ValueFirst();
    [GeneratedRegex(@"(?<multiplier>[xX×])?\s*(?<sign>[+\-−–—])?\s*(?<number>[0-9Oo]{1,4}(?:[.,][0-9Oo]{1,3})?)\s*%?", RegexOptions.IgnoreCase)]
    private static partial Regex ValueToken();
    [GeneratedRegex(@"(?<sign>[+\-−–—])\s*(?<number>[0-9Oo]{1,4}(?:[.,][0-9Oo]{1,3})?)")]
    private static partial Regex SignedNumber();
    [GeneratedRegex(@"\bMR\s*[:.]?\s*(?<value>\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex Mastery();
    [GeneratedRegex(@"\brank\s*[:.]?\s*(?<current>\d)\s*/\s*(?<maximum>\d)", RegexOptions.IgnoreCase)]
    private static partial Regex Rank();
    [GeneratedRegex(@"(?:rerolls?|rolls?)\s*[:.]?\s*(?<value>\d{1,4})", RegexOptions.IgnoreCase)]
    private static partial Regex Rerolls();
    [GeneratedRegex(@"\bMR\s*[:.]?\s*\d{1,2}\D{1,30}(?<value>\d{1,4})\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex RerollsNearMastery();
    private static readonly IReadOnlyDictionary<string, string> Affixes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["laci"]="chance_to_gain_extra_combo_count", ["nus"]="chance_to_gain_extra_combo_count", ["ampi"]="ammo_maximum", ["bin"]="ammo_maximum",
        ["manti"]="damage_vs_corpus", ["tron"]="damage_vs_corpus", ["argi"]="damage_vs_grineer", ["con"]="damage_vs_grineer",
        ["pura"]="damage_vs_infested", ["ada"]="damage_vs_infested", ["geli"]="cold_damage", ["do"]="cold_damage",
        ["tempi"]="combo_duration", ["nem"]="combo_duration", ["crita"]="critical_chance", ["cron"]="critical_chance",
        ["pleci"]="critical_chance_on_slide_attack", ["nent"]="critical_chance_on_slide_attack", ["acri"]="critical_damage", ["tis"]="critical_damage",
        ["visi"]="base_damage_/_melee_damage", ["ata"]="base_damage_/_melee_damage", ["vexi"]="electric_damage", ["tio"]="electric_damage",
        ["igni"]="heat_damage", ["pha"]="heat_damage", ["exi"]="finisher_damage", ["cta"]="finisher_damage",
        ["croni"]="fire_rate_/_attack_speed", ["dra"]="fire_rate_/_attack_speed", ["conci"]="projectile_speed", ["nak"]="projectile_speed",
        ["para"]="channeling_damage", ["um"]="channeling_damage", ["magna"]="impact_damage", ["ton"]="impact_damage",
        ["arma"]="magazine_capacity", ["tin"]="magazine_capacity", ["forti"]="channeling_efficiency", ["us"]="channeling_efficiency",
        ["sati"]="multishot", ["can"]="multishot", ["toxi"]="toxin_damage", ["tox"]="toxin_damage",
        ["lexi"]="punch_through", ["nok"]="punch_through", ["insi"]="puncture_damage", ["cak"]="puncture_damage",
        ["feva"]="reload_speed", ["tak"]="reload_speed", ["locti"]="range", ["tor"]="range",
        ["sci"]="slash_damage", ["sus"]="slash_damage", ["hexa"]="status_chance", ["dex"]="status_chance",
        ["deci"]="status_duration", ["des"]="status_duration", ["zeti"]="recoil", ["mag"]="recoil",
        ["hera"]="zoom", ["lis"]="zoom"
    };

    public static RivenOcrDraft Parse(IEnumerable<(string Text, float Confidence)> passes, IEnumerable<string> weaponNames)
    {
        var rows = passes.Where(pass => !string.IsNullOrWhiteSpace(pass.Text)).ToArray();
        if (rows.Length == 0) return new("", null, null, null, [], null, 0, ["No text was recognized."]);
        var lines = rows.SelectMany((pass, passIndex) =>
        {
            var passLines = pass.Text.Replace('\r', '\n').Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return passLines.Select((line, lineIndex) =>
            {
                // The melee slide-critical label commonly wraps after "Critical Chance".
                // Join its continuation before matching stat names, otherwise it is
                // mistaken for the separate +Critical Chance attribute on the same card.
                var text = Clean(line);
                if (lineIndex + 1 < passLines.Length && ValueToken().IsMatch(text)
                    && Regex.IsMatch(text, @"\bcritical\s+chance\b", RegexOptions.IgnoreCase)
                    && Regex.IsMatch(passLines[lineIndex + 1], @"^\s*(?:for|on)\s+slide\s+attack\b", RegexOptions.IgnoreCase))
                    text += " " + Clean(passLines[lineIndex + 1]);
                return (Text: text, pass.Confidence, Pass: passIndex,
                    Position: passLines.Length <= 1 ? 0d : lineIndex / (double)(passLines.Length - 1));
            });
        }).Where(line => line.Text.Length > 0).ToArray();
        var weapons = weaponNames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var weapon = weapons.Select(name => (Name: name, Score: lines.Max(line => WeaponScore(line.Text, name))))
            .OrderByDescending(match => match.Score).ThenByDescending(match => match.Name.Length).FirstOrDefault();
        var weaponName = weapon.Score >= .60 ? weapon.Name : "";
        var titleStats = weaponName.Length == 0 ? [] : DecodeTitleStats(lines, weaponName);
        var candidates = new List<Candidate>();
        var normalizedMultipliers = false;
        foreach (var line in lines)
        {
            var normalizedLine = NormalizeSigns(line.Text);
            if (Regex.IsMatch(normalizedLine, @"\b(?:MR|rank|rerolls?|rolls?)\b", RegexOptions.IgnoreCase)) continue;
            var structured = ValueFirst().Match(normalizedLine);
            var match = structured.Success ? structured : ValueToken().Match(normalizedLine); if (!match.Success) continue;
            var numberText = match.Groups["number"].Value.Replace('O', '0').Replace('o', '0').Replace(',', '.');
            if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
            var multiplier = match.Groups["multiplier"].Success;
            if (multiplier) value = (value - 1) * 100;
            // Damage/status icons are pictures rather than characters. Depending on scale,
            // Tesseract may emit punctuation, a random letter, or nothing for them. Match
            // the known label anywhere after the value instead of requiring a clean gap.
            var rawName = structured.Success ? structured.Groups["name"].Value.Trim(' ', '.', ':', '-')
                : normalizedLine[(match.Index + match.Length)..].Trim(' ', '.', ':', '-');
            var tokenOnly = ValueToken().Match(normalizedLine);
            var fullTail = tokenOnly.Success ? normalizedLine[(tokenOnly.Index + tokenOnly.Length)..].Trim(' ', '.', ':', '-') : rawName;
            var best = RivenPricing.AllowedStats("rifle", true).Concat(RivenPricing.AllowedStats("melee", true))
                .Concat(RivenPricing.AllowedStats("rifle", false)).Concat(RivenPricing.AllowedStats("melee", false))
                .Distinct(StringComparer.Ordinal).Select(slug => (Slug: slug, Name: RivenPricing.DisplayName(slug), Score: Math.Max(StatLabelScore(rawName, slug), StatLabelScore(fullTail, slug))))
                .OrderByDescending(stat => stat.Score).First();
            if (best.Score < .62 || !PlausibleValue(best.Slug, value)) continue;
            if (multiplier) normalizedMultipliers = true;
            var sign = match.Groups["sign"].Value;
            candidates.Add(new(best.Slug, value, sign is "-" or "−" or "–" or "—" || multiplier && value < 0,
                best.Score * .75 + line.Confidence * .25, line.Pass, line.Position));
        }
        var resolved = candidates.GroupBy(item => item.Slug).Select(group =>
        {
            var perPass = group.GroupBy(item => item.Pass).Select(items => items.OrderByDescending(item => item.Score).First()).ToArray();
            var negativeVotes = perPass.Count(item => item.Negative);
            var isNegative = negativeVotes >= Math.Max(1, (int)Math.Ceiling(perPass.Length * .5));
            var matching = perPass.Where(item => item.Negative == isNegative).ToArray();
            if (matching.Length == 0) matching = perPass;
            var orderedValues = matching.Select(item => item.Value).Order().ToArray();
            var median = orderedValues.Length % 2 == 1 ? orderedValues[orderedValues.Length / 2]
                : (orderedValues[orderedValues.Length / 2 - 1] + orderedValues[orderedValues.Length / 2]) / 2;
            var agreement = 1 - Math.Min(1, matching.Select(item => Math.Abs(item.Value - median)).DefaultIfEmpty(0).Average() / Math.Max(10, Math.Abs(median)));
            return new ResolvedCandidate(group.Key, median, isNegative,
                matching.Average(item => item.Score) + Math.Min(.12, (perPass.Length - 1) * .025) + agreement * .04,
                perPass.Length, matching.Average(item => item.Position));
        }).OrderByDescending(item => item.Score).ThenByDescending(item => item.Support).ToArray();
        // Different OCR passes can interpret an elemental icon as a second label on
        // the same physical value. Keep the clearly stronger reading, and when the
        // card produces more than three candidates prefer corroborated or cleanly
        // matched labels over one-pass texture/capacity hallucinations.
        resolved = resolved.Where(item => !resolved.Any(other => other != item
                && Math.Abs(other.Position - item.Position) <= .06
                && Math.Abs(Math.Abs(other.Value) - Math.Abs(item.Value)) <= Math.Max(.6, Math.Abs(item.Value) * .03)
                && other.Score > item.Score + .08)).ToArray();
        if (resolved.Length > 3)
        {
            var reliable = resolved.Where(item => item.Support >= 2 || item.Score >= .92).ToArray();
            if (reliable.Length >= 2) resolved = reliable;
        }
        // A generated Riven name encodes its positive attributes. Prefer those labels when
        // icon glyphs or the purple card texture make a stat line look like another attribute.
        var titleGuided = titleStats.Length is 2 or 3 && titleStats.Any(slug => resolved.Any(item => item.Slug == slug && !item.Negative));
        var titleCorrected = false;
        ResolvedCandidate[] selected;
        if (titleGuided)
        {
            var guidedPositives = new List<ResolvedCandidate>(); var consumed = new HashSet<ResolvedCandidate>();
            foreach (var slug in titleStats)
            {
                var exact = resolved.FirstOrDefault(item => item.Slug == slug && !item.Negative);
                var recovered = RecoverSignedValue(slug, lines, resolved);
                if (exact is not null)
                {
                    var peers = titleStats.Where(other => other != slug)
                        .Select(other => resolved.FirstOrDefault(item => item.Slug == other && !item.Negative)).OfType<ResolvedCandidate>().ToArray();
                    if (recovered is not null && peers.Length >= 2
                        && PositiveScaleError(recovered, peers) + .04 < PositiveScaleError(exact, peers))
                    { guidedPositives.Add(recovered); titleCorrected = true; }
                    else { guidedPositives.Add(exact); consumed.Add(exact); }
                    continue;
                }
                if (recovered is not null) { guidedPositives.Add(recovered); titleCorrected = true; continue; }
                // Comparison arrows and elemental icons sometimes preserve the number but
                // corrupt its label. The Riven name supplies the missing label; retain the
                // best unmatched value only when it is physically plausible for that stat.
                var replacement = resolved.Where(item => !item.Negative && !consumed.Contains(item)
                        && !titleStats.Contains(item.Slug, StringComparer.Ordinal) && PlausibleValue(slug, item.Value))
                    .OrderByDescending(item => item.Support).ThenByDescending(item => item.Score).FirstOrDefault();
                if (replacement is not null)
                {
                    guidedPositives.Add(replacement with { Slug = slug, Score = replacement.Score - .08 });
                    consumed.Add(replacement); titleCorrected = true;
                }
            }
            var negatives = resolved.Where(item => item.Negative && !DuplicatesGuidedLine(item, guidedPositives)
                    && ConsistentImpliedNegative(item, guidedPositives, titleStats.Length))
                .OrderByDescending(item => item.Support).ThenByDescending(item => item.Score).Take(1).ToList();
            if (negatives.Count == 0)
            {
                // The title contains positives only. One additional displayed attribute is
                // therefore the negative even when OCR loses its minus sign (or when harmful
                // +Weapon Recoil is displayed with a plus sign).
                var implied = resolved.Where(item => !item.Negative && !consumed.Contains(item)
                        && !titleStats.Contains(item.Slug, StringComparer.Ordinal)
                        && !DuplicatesGuidedLine(item, guidedPositives)
                        && ConsistentImpliedNegative(item, guidedPositives, titleStats.Length))
                    .OrderByDescending(item => item.Position).ThenByDescending(item => item.Support).FirstOrDefault();
                if (implied is not null && resolved.Count(item => !item.Negative) >= titleStats.Length + 1)
                { negatives.Add(implied with { Negative = true }); titleCorrected = true; }
            }
            selected = guidedPositives.Concat(negatives).DistinctBy(item => item.Slug).Take(4).ToArray();
        }
        else selected = resolved.Take(4).ToArray();
        var inferredNegative = false;
        var explicitNegative = selected.Where(item => item.Negative).OrderByDescending(item => item.Support).ThenByDescending(item => item.Score).FirstOrDefault();
        if (explicitNegative is null && selected.Length == 4)
        {
            explicitNegative = InferNegative(selected);
            inferredNegative = true;
        }
        var negative = explicitNegative is null ? null : new RivenOcrStat(explicitNegative.Slug, -Math.Abs(explicitNegative.Value));
        var positives = selected.Where(item => item.Slug != explicitNegative?.Slug).Take(3)
            .Select(item => new RivenOcrStat(item.Slug, Math.Abs(item.Value))).ToArray();
        var rank = Metadata(rows, Rank(), "current", value => value is >= 0 and <= 8,
            match => int.TryParse(match.Groups["maximum"].Value, out var maximum) && maximum == 8, requireStrongConsensus: true);
        var mastery = Metadata(rows, Mastery(), "value", value => value is >= 8 and <= 16);
        var rerolls = Metadata(rows, Rerolls(), "value", value => value is >= 0 and <= 100_000)
            ?? Metadata(rows, RerollsNearMastery(), "value", value => value is >= 0 and <= 100_000);
        var supported = selected.Length == 0 ? 0 : selected.Average(item => Math.Min(1, item.Support / (double)Math.Min(3, rows.Length)));
        var confidence = Math.Clamp(rows.Average(pass => pass.Confidence) * .35 + (weaponName.Length > 0 ? .2 : 0) + Math.Min(3, positives.Length) * .1 + (negative is not null ? .05 : 0) + supported * .1 - (inferredNegative ? .06 : 0), 0, 1);
        var notes = new List<string>();
        if (weaponName.Length == 0) notes.Add("Weapon name needs correction.");
        if (positives.Length is < 2 or > 3) notes.Add("Expected two or three positive stats.");
        if (inferredNegative) notes.Add("Four attributes were consistently read but the minus glyph was unclear; shared stat scaling and card layout provisionally identified the negative. Verify it before confirming.");
        if (titleGuided) notes.Add(titleCorrected
            ? "The generated Riven name provisionally corrected a damaged stat label or missing negative sign; verify the correction."
            : "The generated Riven name was used as a local cross-check for positive stat labels.");
        if (normalizedMultipliers) notes.Add("Faction multiplier text was normalized to its equivalent bonus percentage for grading.");
        if (!rank.HasValue) notes.Add("Mod rank needs verification.");
        if (!mastery.HasValue) notes.Add("Mastery Rank needs verification.");
        if (!rerolls.HasValue) notes.Add("Reroll count needs verification.");
        if (confidence < .72) notes.Add("Low-confidence read: carefully verify every field.");
        return new(weaponName, rank, mastery, rerolls, positives, negative, confidence, notes.ToArray(), rows.OrderByDescending(row => row.Confidence).First().Text);
    }

    private static string NormalizeSigns(string value) => value.Replace('—', '-').Replace('–', '-').Replace('−', '-');
    private static int? Metadata(IEnumerable<(string Text, float Confidence)> passes, Regex regex, string group,
        Func<int, bool> valid, Func<Match, bool>? validMatch = null, bool requireStrongConsensus = false)
    {
        var rows = passes.ToArray();
        var found = new List<(int Value, float Confidence, int Pass)>();
        for (var passIndex = 0; passIndex < rows.Length; passIndex++)
            foreach (Match match in regex.Matches(NormalizeSigns(rows[passIndex].Text)))
                if (validMatch?.Invoke(match) is not false && int.TryParse(match.Groups[group].Value, out var value) && valid(value))
                    found.Add((value, rows[passIndex].Confidence, passIndex));
        var votes = found
            .GroupBy(vote => vote.Value).Select(grouping =>
            {
                var perPass = grouping.GroupBy(vote => vote.Pass).Select(group => group.MaxBy(vote => vote.Confidence)).ToArray();
                return (Value: grouping.Key, Support: perPass.Length, Confidence: perPass.Sum(vote => vote.Confidence));
            }).OrderByDescending(vote => vote.Support).ThenByDescending(vote => vote.Confidence).ThenByDescending(vote => vote.Value).ToArray();
        var minimumSupport = rows.Length <= 1 ? 1 : requireStrongConsensus ? Math.Min(3, rows.Length) : 2;
        if (votes.Length == 0 || votes[0].Support < minimumSupport && votes.Length > 1) return null;
        return votes[0].Value;
    }
    private static bool PlausibleValue(string slug, double value)
    {
        if (!double.IsFinite(value)) return false;
        if (RivenPricing.CombinedStats.Contains(slug)) return Math.Abs(value) is >= .5 and <= 1000;
        if (!RivenPricing.Bases.TryGetValue(slug, out var columns)) return false;
        var bases = columns.OfType<double>().ToArray();
        if (bases.Length == 0) return false;
        var magnitude = Math.Abs(value);
        // Covers mod ranks 0–8, disposition 0.5–1.55, legal stat-count multipliers,
        // negatives, and the normal grade range. The floor still rejects artifacts such
        // as +1% Fire Rate while allowing genuine low-rank values such as +7.8% Toxin.
        return magnitude >= bases.Min() * .025 && magnitude <= bases.Max() * 2.60;
    }
    private static bool ConsistentImpliedNegative(ResolvedCandidate candidate, IReadOnlyList<ResolvedCandidate> positives, int positiveCount)
    {
        if (positives.Count < 2 || positiveCount is not (2 or 3)) return false;
        var (positiveMultiplier, negativeMultiplier) = positiveCount == 3 ? (.9375, .75) : (1.2375, .495);
        for (var column = 0; column < 5; column++)
        {
            if (Basis(candidate.Slug, column) is not double negativeBase
                || positives.Any(item => Basis(item.Slug, column) is null)) continue;
            var normalized = positives.Select(item => Math.Abs(item.Value) / (Basis(item.Slug, column)!.Value * positiveMultiplier)).Order().ToArray();
            var center = normalized[normalized.Length / 2];
            var negativeScale = Math.Abs(candidate.Value) / (negativeBase * negativeMultiplier);
            var ratio = negativeScale / Math.Max(.001, center);
            if (ratio is >= .65 and <= 1.45) return true;
        }
        return false;
    }
    private static bool DuplicatesGuidedLine(ResolvedCandidate candidate, IReadOnlyList<ResolvedCandidate> guided)
        => guided.Any(item => Math.Abs(item.Position - candidate.Position) <= .06
            && Math.Abs(Math.Abs(item.Value) - Math.Abs(candidate.Value)) <= Math.Max(.6, Math.Abs(item.Value) * .03));
    private static ResolvedCandidate? RecoverSignedValue(string slug,
        IEnumerable<(string Text, float Confidence, int Pass, double Position)> lines, IReadOnlyList<ResolvedCandidate> resolved)
    {
        var values = new List<(double Value, double Score, int Pass, double Position)>();
        foreach (var line in lines)
        {
            var normalized = NormalizeSigns(line.Text);
            // Rank-comparison cards contain both the low-rank and full-rank number on one
            // line. Relabeling either as an orphan would be ambiguous, so leave that line
            // to the normal stat-label parser.
            if (normalized.Contains('>') || SignedNumber().Matches(normalized).Count != 1) continue;
            var match = SignedNumber().Match(normalized);
            if (!match.Success || match.Groups["sign"].Value != "+") continue;
            var number = match.Groups["number"].Value.Replace('O', '0').Replace('o', '0').Replace(',', '.');
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !PlausibleValue(slug, value)) continue;
            if (resolved.Any(item => Math.Abs(Math.Abs(item.Value) - value) < .15)) continue;
            values.Add((value, line.Confidence, line.Pass, line.Position));
        }
        var best = values.GroupBy(item => Math.Round(item.Value, 1)).Select(group =>
        {
            var perPass = group.GroupBy(item => item.Pass).Select(pass => pass.OrderByDescending(item => item.Score).First()).ToArray();
            return new ResolvedCandidate(slug, group.Key, false, perPass.Average(item => item.Score) * .25 + .45,
                perPass.Length, perPass.Average(item => item.Position));
        }).OrderByDescending(item => item.Support).ThenByDescending(item => item.Score).FirstOrDefault();
        return best;
    }
    private static double PositiveScaleError(ResolvedCandidate candidate, IReadOnlyList<ResolvedCandidate> peers)
    {
        return Enumerable.Range(0, 5).Where(column => Basis(candidate.Slug, column).HasValue
                && peers.All(item => Basis(item.Slug, column).HasValue))
            .Select(column =>
            {
                var scales = peers.Append(candidate).Select(item => Math.Abs(item.Value) / Basis(item.Slug, column)!.Value).ToArray();
                var mean = scales.Average();
                return Math.Sqrt(scales.Average(value => Math.Pow(value - mean, 2))) / Math.Max(.001, mean);
            }).DefaultIfEmpty(10).Min();
    }
    private static string[] DecodeTitleStats(IEnumerable<(string Text, float Confidence, int Pass, double Position)> lines, string weaponName)
    {
        var weaponKey = Key(weaponName); var votes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pass in lines.GroupBy(line => line.Pass))
        {
            var top = pass.OrderBy(line => line.Position).Take(5).Select(line => line.Text).ToArray();
            for (var start = 0; start < top.Length; start++)
                for (var count = 1; count <= 2 && start + count <= top.Length; count++)
                {
                    var key = Key(string.Join(' ', top.Skip(start).Take(count))); var weaponAt = key.IndexOf(weaponKey, StringComparison.Ordinal);
                    if (weaponAt < 0) continue;
                    var tail = key[(weaponAt + weaponKey.Length)..];
                    var decoded = TokenizeAffixes(tail);
                    if (decoded.Length is 2 or 3)
                    {
                        var signature = string.Join('\0', decoded); votes[signature] = votes.GetValueOrDefault(signature) + 1;
                    }
                }
        }
        if (votes.Count == 0) return [];
        return votes.OrderByDescending(pair => pair.Value).ThenByDescending(pair => pair.Key.Length).First().Key.Split('\0');
    }
    private static string[] TokenizeAffixes(string text)
    {
        if (text.Length < 4 || text.Length > 32) return [];
        string[]? best = null;
        void Visit(int offset, List<string> stats)
        {
            if (offset == text.Length)
            {
                var unique = stats.Distinct(StringComparer.Ordinal).ToArray();
                if (unique.Length is 2 or 3 && (best is null || unique.Length > best.Length)) best = unique;
                return;
            }
            if (stats.Count >= 3) return;
            foreach (var token in Affixes.Keys.Where(token => text.AsSpan(offset).StartsWith(token, StringComparison.Ordinal)).OrderByDescending(token => token.Length))
            {
                stats.Add(Affixes[token]); Visit(offset + token.Length, stats); stats.RemoveAt(stats.Count - 1);
            }
        }
        Visit(0, []); return best ?? [];
    }
    private static ResolvedCandidate InferNegative(ResolvedCandidate[] candidates)
    {
        // All four attributes share one disposition. Try every legal weapon class and choose the
        // negative assignment whose normalized values agree most closely; line order breaks ties.
        return candidates.Select(candidate => (Candidate: candidate, Error: Enumerable.Range(0, 5)
            .Where(column => candidates.All(item => Basis(item.Slug, column).HasValue))
            .Select(column =>
            {
                var estimates = candidates.Select(item => Math.Abs(item.Value) /
                    (Basis(item.Slug, column)!.Value * (item.Slug == candidate.Slug ? .75 : .9375))).ToArray();
                var mean = estimates.Average();
                var spread = Math.Sqrt(estimates.Average(value => Math.Pow(value - mean, 2))) / Math.Max(.1, mean);
                var rangePenalty = estimates.Sum(value => value is < .45 or > 1.65 ? .35 : 0);
                return spread + rangePenalty;
            }).DefaultIfEmpty(10).Min() - candidate.Position * .015))
            .OrderBy(item => item.Error).ThenByDescending(item => item.Candidate.Position).First().Candidate;
    }
    private static string[] OcrNames(string slug) => slug switch
    {
        "base_damage_/_melee_damage" => ["damage", "base damage", "melee damage"],
        "fire_rate_/_attack_speed" => ["fire rate", "attack speed"],
        "cold_damage" => ["cold", "cold damage"],
        "electric_damage" => ["electricity", "electric damage", "electricity damage"],
        "heat_damage" => ["heat", "heat damage"],
        "toxin_damage" => ["toxin", "toxin damage"],
        "impact_damage" => ["impact", "impact damage"],
        "puncture_damage" => ["puncture", "puncture damage"],
        "slash_damage" => ["slash", "slash damage"],
        "damage_vs_corpus" => ["damage to corpus", "damage vs corpus"],
        "damage_vs_grineer" => ["damage to grineer", "damage vs grineer"],
        "damage_vs_infested" => ["damage to infested", "damage vs infested"],
        "recoil" => ["recoil", "weapon recoil"],
        "chance_to_gain_extra_combo_count" => ["additional combo count chance", "chance to gain extra combo count"],
        "chance_to_gain_combo_count" => ["chance to gain combo count"],
        "critical_chance_on_slide_attack" => ["critical chance for slide attack", "critical chance on slide attack", "slide attack critical chance"],
        _ => [RivenPricing.DisplayName(slug)]
    };
    private static double? Basis(string slug, int column) =>
        RivenPricing.Bases.TryGetValue(slug, out var values) && column >= 0 && column < values.Length ? values[column] : null;
    private static double StatLabelScore(string raw, string slug)
    {
        var rawKey = Key(raw);
        // Polarity/capacity fragments and truncated words are not stat labels. Exact
        // short names such as Gas remain valid, but "V" must not become Viral and
        // "Wea" must not become Weapon Recoil through substring scoring.
        if (rawKey.Length == 0) return 0;
        return OcrNames(slug).Select(name => Key(name)).Where(name => name.Length > 0).Select(name =>
        {
            if (rawKey == name) return 1.2;
            if (rawKey.Length < 4) return 0;
            if (rawKey.EndsWith(name, StringComparison.Ordinal)) return 1.12;
            if (rawKey.Contains(name, StringComparison.Ordinal)) return 1.05;
            var suffix = rawKey.Length > name.Length + 4 ? rawKey[^Math.Min(rawKey.Length, name.Length + 4)..] : rawKey;
            return Similarity(suffix, name);
        }).DefaultIfEmpty(0).Max();
    }
    private static string Clean(string value) => Regex.Replace(value, @"\s+", " ").Trim();
    private static string Key(string value) => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static string Words(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
    private static double WeaponScore(string line, string weapon)
    {
        var words = " " + Words(line) + " "; var wanted = Words(weapon);
        if (wanted.Length > 0 && words.Contains(" " + wanted + " ", StringComparison.Ordinal)) return 2 + Math.Min(50, wanted.Length) / 100d;
        var left = Key(line); var right = Key(weapon);
        if (right.Length < 5 || left.Any(char.IsDigit) || left.Length > right.Length + 28) return 0;
        return Similarity(left, right);
    }
    private static double Similarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return 0;
        if (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal))
            return Math.Min(left.Length, right.Length) / (double)Math.Max(left.Length, right.Length) * .35 + .65;
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1]; current[0] = i;
            for (var j = 1; j <= right.Length; j++) current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            previous = current;
        }
        return 1 - previous[^1] / (double)Math.Max(left.Length, right.Length);
    }
}
