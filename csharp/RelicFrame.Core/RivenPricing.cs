using System.Text.Json;
using System.Text.RegularExpressions;

namespace RelicFrame.Core;

public sealed record WeeklyPrice(string Weapon, string RivenType, bool Rerolled, double Average, double Median,
    double Minimum, double Maximum, double StandardDeviation, double Popularity, DateTimeOffset? SourceAsOf = null);
public sealed record RivenStatRange(string Slug, bool Positive, double Minimum, double Maximum, string Unit);
public sealed record RivenStatGrade(string Slug, bool Positive, double Value, double VariancePct, string Grade);
public sealed record RivenAppraisal(string WeaponName, string WeaponSlug, string[] Positives, string? Negative,
    int ExactAsks, int SimilarAsks, int OnlineAsks, int? LowestAsk, int? MedianAsk, int? HighestAsk,
    int RecommendedPrice, int QuickPrice, int PatientPrice, string Confidence, IReadOnlyDictionary<string, int> PriceBands,
    double? MedianListingAgeHours, int ObservedClosures, double? MedianObservedLifetimeHours,
    WeeklyPrice? WeeklyCompletedTrades, double? RollQualityPct, string RollSignature,
    int ConfirmedSales, double? ConfirmedMedianPrice, double? ConfirmedMedianLifetimeHours,
    int MasteryRank, int ModRank, int Rerolls, int EndoValue, string EvidenceNote,
    string BuildGuidance = "", IReadOnlyList<RivenStatGrade>? StatGrades = null,
    int? DesiredPositiveCount = null, bool? PreferredRoll = null, string RollUsefulness = "",
    string DesirabilityProfileSource = "", string DesirabilityExpression = "", string[]? SupportedHarmlessNegatives = null,
    int ExcludedAskOutliers = 0, double? ListingAgeAdjustmentPct = null, double? SaleVelocityAdjustmentPct = null);
public sealed record RivenDeal(string AuctionId, string WeaponSlug, int Price, int PeerValue, double DiscountPct,
    int PotentialMargin, string[] Positives, string[] Negatives, object[][] PositiveRolls, object[][] NegativeRolls,
    string Seller, string SellerSlug, string SellerStatus, int ComparableCount, string WeaponName,
    int ProjectedResale, double RoiPct, double? WeeklyMedian, double? WeeklyPopularity, double? RollQualityPct,
    bool CuratedMatch, int? CuratedDesiredCount, int? CuratedPositiveCount, string CuratedProfile, string CuratedNotes)
{
    // Links are conveniences only; the bot never sends a seller a message or purchases an item.
    [System.Text.Json.Serialization.JsonIgnore] public string Url => $"https://warframe.market/auction/{Uri.EscapeDataString(AuctionId)}";
    [System.Text.Json.Serialization.JsonIgnore] public string SellerProfileUrl => $"https://warframe.market/profile/{Uri.EscapeDataString(SellerSlug.Length > 0 ? SellerSlug : Seller)}";
    [System.Text.Json.Serialization.JsonIgnore] public string IngameWhisper => $"/w {Seller} Hi! I'd like to buy your {(WeaponName.Length > 0 ? WeaponName : WeaponSlug)} Riven listed for {Price:N0} platinum.";
}
public sealed record RivenEndoDeal(string AuctionId, string WeaponSlug, string WeaponName, int Price, int Endo,
    double PlatPerThousandEndo, string Seller, string SellerSlug, string SellerStatus)
{
    [System.Text.Json.Serialization.JsonIgnore] public string Url => $"https://warframe.market/auction/{Uri.EscapeDataString(AuctionId)}";
    [System.Text.Json.Serialization.JsonIgnore] public string IngameWhisper => $"/w {Seller} Hi! I'd like to buy your {WeaponName} Riven listed for {Price:N0} platinum.";
}
public sealed record RivenAskRange(int Total, int Online, int? Low, int? Median, int? High,
    int? OnlineLow, int? OnlineMedian, int? OnlineHigh);

public static class RivenPricing
{
    public static int EndoValue(int masteryRank, int modRank, int rerolls)
    {
        if (masteryRank is < 8 or > 16) throw new ArgumentOutOfRangeException(nameof(masteryRank), "Riven Mastery Rank must be 8–16.");
        if (modRank is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(modRank), "Riven mod rank must be 0–8.");
        if (rerolls is < 0 or > 100_000) throw new ArgumentOutOfRangeException(nameof(rerolls), "Rerolls must be 0–100,000.");
        return 100 * (masteryRank - 8) + (int)Math.Floor(22.5 * Math.Pow(2, modRank)) + 200 * rerolls - 7;
    }
    public static int EndoValue(JsonElement auction) => EndoValue(
        (int)(auction.Get("item").Get("mastery_level").Number() ?? auction.Get("item").Get("mastery_rank").Number() ?? 8),
        (int)(auction.Get("item").Get("mod_rank").Number() ?? 0),
        (int)(auction.Get("item").Get("re_rolls").Number() ?? auction.Get("item").Get("rerolls").Number() ?? 0));
    // Columns are rifle, shotgun, pistol, archgun, melee. Null means an illegal stat/class combination.
    public static readonly IReadOnlyDictionary<string, double?[]> Bases = new Dictionary<string, double?[]>
    {
        ["chance_to_gain_extra_combo_count"] = [null,null,null,null,58.77], ["chance_to_gain_combo_count"] = [null,null,null,null,58.77],
        ["ammo_maximum"] = [49.95,90,90,99.9,null],
        ["damage_vs_corpus"] = [45,45,45,45,45], ["damage_vs_grineer"] = [45,45,45,45,45], ["damage_vs_infested"] = [45,45,45,45,45],
        ["cold_damage"] = [90,90,90,119.7,90], ["electric_damage"] = [90,90,90,119.7,90], ["heat_damage"] = [90,90,90,119.7,90], ["toxin_damage"] = [90,90,90,119.7,90],
        ["combo_duration"] = [null,null,null,null,8.1], ["critical_chance"] = [149.99,90,149.99,99.9,180],
        ["critical_chance_on_slide_attack"] = [null,null,null,null,120], ["critical_damage"] = [120,90,90,80.1,90],
        ["base_damage_/_melee_damage"] = [165,164.7,219.6,99.9,164.7], ["finisher_damage"] = [null,null,null,null,119.7],
        ["fire_rate_/_attack_speed"] = [60.03,90,74.7,60.03,54.9], ["projectile_speed"] = [90,90,90,null,null],
        ["channeling_damage"] = [null,null,null,null,24.5], ["impact_damage"] = [119.97,119.97,119.97,90,119.7],
        ["magazine_capacity"] = [50,50,50,60.3,null], ["channeling_efficiency"] = [null,null,null,null,73.44],
        ["multishot"] = [90,119.7,119.7,60.3,null], ["punch_through"] = [2.7,2.7,2.7,2.7,null],
        ["puncture_damage"] = [119.97,119.97,119.97,90,119.7], ["reload_speed"] = [50,50,50,99.9,null],
        ["range"] = [null,null,null,null,1.94], ["slash_damage"] = [119.97,119.97,119.97,90,119.7],
        ["status_chance"] = [90,90,90,60.3,90], ["status_duration"] = [99.99,99.99,99.99,99.99,99.99],
        ["recoil"] = [90,90,90,90,null], ["zoom"] = [59.99,null,80.1,59.99,null]
    };
    public static string Key(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "");
    public static string Unit(string slug) => slug switch { "combo_duration" => "seconds", "channeling_damage" => "flat", "punch_through" or "range" => "meters", _ => "percent" };
    public static string StatClass(JsonElement weapon)
    {
        var group = weapon.Get("group").Text().ToLowerInvariant(); var type = weapon.Get("rivenType").Text().ToLowerInvariant();
        var slot = weapon.Get("variant_slot").Text().ToLowerInvariant(); var kind = weapon.Get("variant_class").Text().ToLowerInvariant();
        if (group == "archgun" || slot.Contains("archgun")) return "archgun";
        if (type is "melee" or "zaw" || group is "melee" or "zaw") return "melee";
        if (type == "shotgun" || kind.Contains("shotgun")) return "shotgun";
        if (type == "pistol" || slot == "secondary") return "pistol";
        return type == "kitgun" && slot != "primary" ? "pistol" : "rifle";
    }
    public static RivenStatRange[] Ranges(string[] positives, string? negative, string statClass, double disposition)
    {
        var index = Array.IndexOf(new[] { "rifle", "shotgun", "pistol", "archgun", "melee" }, statClass);
        if (index < 0 || !double.IsFinite(disposition) || disposition <= 0) throw new ArgumentException("Invalid stat class or disposition.");
        var (bonus, malus) = (positives.Length, negative is not null) switch
        { (2,false) => (.99,0d), (2,true) => (1.2375,.495), (3,false) => (.75,0d), (3,true) => (.9375,.75), _ => throw new ArgumentException("Use two or three positive stats and at most one negative.") };
        RivenStatRange One(string slug, bool positive)
        {
            if (positive && slug == "chance_to_gain_combo_count" || !positive && slug is "cold_damage" or "electric_damage" or "heat_damage" or "toxin_damage" or "punch_through" or "chance_to_gain_extra_combo_count")
                throw new ArgumentException("Illegal positive/negative stat.");
            if (!Bases.TryGetValue(slug, out var columns) || columns[index] is not double basis) throw new ArgumentException("Stat cannot roll on this class.");
            if (!positive && slug == "chance_to_gain_combo_count") basis = 104.85;
            var weighted = basis * disposition * (positive ? bonus : malus);
            return new(slug, positive, positive ? weighted * .9 : -weighted * 1.1, positive ? weighted * 1.1 : -weighted * .9, Unit(slug));
        }
        return positives.Select(s => One(s, true)).Concat(negative is null ? [] : new[] { One(negative, false) }).ToArray();
    }
    public static IEnumerable<(string Slug, bool Positive, double? Value)> Attributes(JsonElement auction) =>
        auction.Get("item").Get("attributes").Rows().Select(a => ((a.Get("url_name").Text() is { Length: > 0 } slug ? slug : a.Get("slug").Text()).Trim().ToLowerInvariant(),
            a.Get("positive").ValueKind == JsonValueKind.Undefined ? a.Get("postive").Bool(true) : a.Get("positive").Bool(true), a.Get("value").Number())).Where(a => a.Item1.Length > 0);
    public static string NormalizeStat(string value)
    {
        var key = Key(value);
        var modern = key switch
        {
            "initialcombo" => "channeling_damage",
            "heavyattackefficiency" => "channeling_efficiency",
            "additionalcombocountchance" => "chance_to_gain_extra_combo_count",
            "chancetogaincombocount" => "chance_to_gain_combo_count",
            _ => null
        };
        if (modern is not null) return modern;
        var exact = Bases.Keys.FirstOrDefault(s => Key(s) == key);
        if (exact is not null) return exact;
        var token = RivenRules.Tokens.FirstOrDefault(pair => Key(pair.Key) == key).Value;
        return token ?? throw new ArgumentException($"Unknown Riven stat '{value}'. Pick a value from autocomplete.");
    }
    public static string DisplayName(string slug) => slug switch
    {
        "channeling_damage" => "Initial Combo",
        "channeling_efficiency" => "Heavy Attack Efficiency",
        "chance_to_gain_extra_combo_count" => "Additional Combo Count Chance",
        "chance_to_gain_combo_count" => "Chance to Gain Combo Count",
        "base_damage_/_melee_damage" => "Damage / Melee Damage",
        "fire_rate_/_attack_speed" => "Fire Rate / Attack Speed",
        _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(slug.Replace('_', ' '))
    };
    public static string[] AllowedStats(string statClass, bool positive)
    {
        var index = Array.IndexOf(new[] { "rifle", "shotgun", "pistol", "archgun", "melee" }, statClass);
        if (index < 0) return [];
        return Bases.Where(pair => pair.Value[index].HasValue)
            .Where(pair => positive ? pair.Key != "chance_to_gain_combo_count" : pair.Key is not ("cold_damage" or "electric_damage" or "heat_damage" or "toxin_damage" or "punch_through" or "chance_to_gain_extra_combo_count"))
            .Select(pair => pair.Key).OrderBy(DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static string Signature(IEnumerable<string> positives, string? negative) =>
        string.Join('|', positives.Select(NormalizeStat).Distinct().Order(StringComparer.Ordinal)) + "/" +
        (string.IsNullOrWhiteSpace(negative) ? "" : NormalizeStat(negative));
    private static double Median(IEnumerable<double> source)
    {
        var values = source.Order().ToArray(); if (values.Length == 0) return 0;
        return values.Length % 2 == 1 ? values[values.Length / 2] : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
    }
    private static int[] WithoutExtremeAskOutliers(IEnumerable<int> source)
    {
        var values = source.Where(value => value > 0).Order().ToArray();
        if (values.Length < 5) return values;
        var middle = Median(values.Select(value => (double)value));
        var mad = Median(values.Select(value => Math.Abs(value - middle)));
        if (mad > 0)
        {
            var scale = 1.4826 * mad;
            return values.Where(value => Math.Abs(value - middle) / scale <= 4.5).ToArray();
        }
        var low = Math.Max(1, middle / 3); var high = Math.Max(middle * 3, middle + 25);
        return values.Where(value => value >= low && value <= high).ToArray();
    }
    public static RollRule LearnMarketRule(IEnumerable<JsonElement> auctions, string statClass, double disposition = 1)
    {
        var rows = auctions.Select(a => (Price: AuctionPrice(a), Attrs: Attributes(a).ToArray())).Where(x => x.Price.HasValue && x.Attrs.Count(a => a.Positive) >= 2).ToArray();
        var overall = Math.Max(1, Median(rows.Select(r => (double)r.Price!.Value)));
        var criticalChancePrior = statClass == "shotgun" ? .65 : disposition >= 1.31 ? 3.35 : disposition >= 1.1 ? 3.0 : disposition >= .9 ? 2.25 : 1.25;
        double Prior(string stat) => statClass == "melee" ? stat switch
        {
            "critical_damage" => 3.2, "critical_chance" => criticalChancePrior, "range" => 2.5, "fire_rate_/_attack_speed" => 2.2,
            "base_damage_/_melee_damage" => 1.8, "toxin_damage" => 1.5, "combo_duration" => 1.2, "status_chance" => 1.0, _ => .2
        } : stat switch
        {
            "multishot" => 3.3, "critical_damage" => 3.0, "critical_chance" => criticalChancePrior, "base_damage_/_melee_damage" => 2.0,
            "fire_rate_/_attack_speed" => 1.5, "toxin_damage" => 1.4, "status_chance" => 1.1, "reload_speed" => .8,
            "cold_damage" or "heat_damage" or "electric_damage" => .7, "projectile_speed" or "punch_through" => .5, _ => .1
        };
        var ranked = AllowedStats(statClass, true).Select(stat =>
        {
            var sample = rows.Where(r => r.Attrs.Any(a => a.Positive && a.Slug == stat)).Select(r => (double)r.Price!.Value).ToArray();
            var market = sample.Length >= 3 ? Math.Clamp(Math.Log(Math.Max(1, Median(sample)) / overall) * 2.5, -1.5, 2.5) : 0;
            return (Stat: stat, Score: Prior(stat) + market, Support: sample.Length);
        }).OrderByDescending(x => x.Score).ThenByDescending(x => x.Support).Take(6).ToArray();
        var legalNegatives = AllowedStats(statClass, false).ToHashSet(StringComparer.Ordinal);
        var negativeStats = rows.SelectMany(r => r.Attrs.Where(a => !a.Positive && legalNegatives.Contains(a.Slug)).Select(a => a.Slug)).Distinct().Select(stat =>
        {
            var sample = rows.Where(r => r.Attrs.Any(a => !a.Positive && a.Slug == stat)).Select(r => (double)r.Price!.Value).ToArray();
            return (Stat: stat, Support: sample.Length, Ratio: sample.Length == 0 ? 0 : Median(sample) / overall);
        }).Where(x => x.Support >= 3 && x.Ratio >= .90).OrderByDescending(x => x.Ratio).Take(4).Select(x => x.Stat).ToHashSet();
        negativeStats.Remove("finisher_damage");
        if (statClass != "melee" && statClass != "shotgun") negativeStats.Add("zoom");
        var profile = string.Join(" · ", ranked.Take(4).Select(x => DisplayName(x.Stat)));
        var note = $"disposition-aware weapon-class priors plus live asking-price premiums; {rows.Length} priced listings" +
            (statClass == "shotgun" ? "; shotgun Riven Critical Chance is conservatively down-weighted" : disposition < .9 ? "; low disposition down-weights Critical Chance" : "");
        return new([new RuleAlternative([], ranked.Select(x => x.Stat).ToArray())], negativeStats.Order(StringComparer.Ordinal).ToArray(), profile, note);
    }
    public static RivenAppraisal Appraise(IEnumerable<JsonElement> auctions, string weaponName, string weaponSlug,
        string[] positives, string? negative, WeeklyPrice? weekly, IEnumerable<(string Signature, double LifetimeHours)> observedClosures,
        string statClass = "rifle", double disposition = 1, double?[]? positiveValues = null, double? negativeValue = null,
        IEnumerable<(string Signature, int Price, double? LifetimeHours)>? confirmedSales = null,
        int masteryRank = 8, int modRank = 0, int rerolls = 0, RollRule? rollRule = null, string profileSource = "")
    {
        positives = positives.Select(NormalizeStat).Distinct().ToArray();
        negative = string.IsNullOrWhiteSpace(negative) ? null : NormalizeStat(negative);
        if (positives.Length is < 2 or > 3) throw new ArgumentException("Choose two or three different positive stats.");
        if (negative is not null && positives.Contains(negative, StringComparer.Ordinal))
            throw new ArgumentException("The same Riven attribute cannot be both positive and negative.");
        if (positiveValues is not null && positiveValues.Any(value => value.HasValue && (!double.IsFinite(value.Value) || value.Value <= 0)))
            throw new ArgumentException("Positive Riven values must be finite numbers greater than zero.");
        if (negativeValue.HasValue && (!double.IsFinite(negativeValue.Value) || negativeValue.Value == 0))
            throw new ArgumentException("A negative Riven value must be a finite non-zero number; either sign is accepted.");
        if (negative is null && negativeValue.HasValue) throw new ArgumentException("A negative value was entered without selecting a negative stat.");
        var requestedRanges = Ranges(positives, negative, statClass, disposition);
        var wantedPos = positives.ToHashSet();
        var rows = auctions.Select(a =>
        {
            var attrs = Attributes(a).ToArray(); var pos = attrs.Where(x => x.Positive).Select(x => x.Slug).ToHashSet();
            var neg = attrs.FirstOrDefault(x => !x.Positive).Slug; var price = AuctionPrice(a);
            var intersection = pos.Intersect(wantedPos).Count(); var union = pos.Union(wantedPos).Count();
            var similarity = union == 0 ? 0 : (double)intersection / union;
            if ((neg ?? "") == (negative ?? "")) similarity += .20;
            var created = DateTimeOffset.TryParse(a.Get("created").Text(), out var parsed) ? parsed : (DateTimeOffset?)null;
            return (Price: price, Pos: pos, Neg: neg, Similarity: similarity, Status: a.Get("owner").Get("status").Text().ToLowerInvariant(), Created: created);
        }).Where(r => r.Price.HasValue).ToArray();
        var exact = rows.Where(r => r.Pos.SetEquals(wantedPos) && (r.Neg ?? "") == (negative ?? "")).ToArray();
        var similar = rows.Where(r => r.Similarity >= .66).OrderByDescending(r => r.Similarity).ThenBy(r => r.Price).ToArray();
        var evidence = exact.Length >= 3 ? exact : similar.Take(20).ToArray();
        var rawPrices = evidence.Select(r => r.Price!.Value).Order().ToArray();
        var prices = WithoutExtremeAskOutliers(rawPrices);
        if (prices.Length == 0) prices = rawPrices;
        var excludedAskOutliers = rawPrices.Length - prices.Length;
        var retainedAskPrices = prices.ToHashSet();
        var retainedEvidence = evidence.Where(row => retainedAskPrices.Contains(row.Price!.Value)).ToArray();
        var median = prices.Length == 0 ? (int?)null : (int)Math.Round(Median(prices.Select(x => (double)x)));
        var signature = Signature(positives, negative);
        var confirmedRaw = (confirmedSales ?? []).Where(x => x.Signature == signature && x.Price > 0).ToArray();
        var confirmedPrices = WithoutExtremeAskOutliers(confirmedRaw.Select(x => x.Price));
        var confirmedSet = confirmedPrices.ToHashSet();
        var confirmed = confirmedRaw.Where(x => confirmedSet.Contains(x.Price)).ToArray();
        var confirmedMedian = confirmed.Length == 0 ? (double?)null : Median(confirmed.Select(x => (double)x.Price));
        var confirmedLifetimes = confirmed.Where(x => x.LifetimeHours.HasValue).Select(x => x.LifetimeHours!.Value).ToArray();
        var ages = retainedEvidence.Where(r => r.Created.HasValue).Select(r => Math.Max(0, (DateTimeOffset.UtcNow - r.Created!.Value).TotalHours)).ToArray();
        var medianAge = ages.Length == 0 ? (double?)null : Median(ages);
        double? listingAgeAdjustment = retainedEvidence.Length >= 5 && ages.Length >= 3 && medianAge.HasValue ? medianAge.Value switch
        {
            > 24 * 90 => -10,
            > 24 * 30 => -5,
            > 24 * 14 => -2,
            _ => 0
        } : null;
        var confirmedMedianLifetime = confirmedLifetimes.Length == 0 ? (double?)null : Median(confirmedLifetimes);
        double? saleVelocityAdjustment = confirmed.Length >= 3 && confirmedLifetimes.Length >= 3 && confirmedMedianLifetime.HasValue ? confirmedMedianLifetime.Value switch
        {
            <= 24 => 5,
            <= 72 => 2,
            <= 24 * 14 => 0,
            <= 24 * 30 => -4,
            _ => -8
        } : null;
        var weeklyAnchor = weekly is { Median: > 0 } ? weekly.Median : (double?)null;
        var anchors = new List<(double Value, double Weight)>();
        if (median.HasValue) anchors.Add((median.Value * (1 + (listingAgeAdjustment ?? 0) / 100), .40));
        if (weeklyAnchor.HasValue) anchors.Add((weeklyAnchor.Value, weekly?.SourceAsOf is { } asOf && DateTimeOffset.UtcNow - asOf > TimeSpan.FromDays(90) ? .05 : .25));
        if (confirmedMedian.HasValue) anchors.Add((confirmedMedian.Value * (1 + (saleVelocityAdjustment ?? 0) / 100), .60));
        var estimate = anchors.Count == 0 ? 0 : anchors.Sum(x => x.Value * x.Weight) / anchors.Sum(x => x.Weight);
        double? quality = null;
        var grades = new List<RivenStatGrade>();
        if (positiveValues is not null && positiveValues.Any(v => v.HasValue))
        {
            try
            {
                var supplied = positives.Select((slug, i) => (Slug: slug, Value: i < positiveValues.Length ? positiveValues[i] : null, Positive: true))
                    .Concat(negative is not null ? [(negative, negativeValue, false)] : []).Where(x => x.Value.HasValue).ToArray();
                var scores = supplied.Select(x =>
                {
                    var range = requestedRanges.First(r => r.Slug == x.Slug && r.Positive == x.Positive);
                    grades.Add(Grade(range, x.Value!.Value));
                    return QualityScore(range, x.Value.Value);
                }).ToArray();
                if (scores.Length > 0) quality = Math.Round(Median(scores) * 100, 1);
            }
            catch (ArgumentException) { quality = null; }
        }
        var qualityFactor = quality.HasValue ? .85 + .30 * quality.Value / 100 : 1;
        var assessment = AssessRoll(weaponName, positives, negative, rollRule, requestedRanges, positiveValues);
        if (estimate > 0 && assessment is not null)
        {
            var allPrices = rows.Select(row => row.Price!.Value).Order().ToArray();
            var floorSample = allPrices.Take(Math.Max(1, (int)Math.Ceiling(allPrices.Length * .20))).Select(value => (double)value);
            var trashAnchor = Median(floorSample);
            var usefulness = assessment.Preferred ? 1d : assessment.Desired switch { 0 => .12, 1 => .30, 2 => .58, _ => .78 };
            estimate = trashAnchor + Math.Max(0, estimate - trashAnchor) * usefulness;
        }
        var recommendation = estimate <= 0 ? 0 : Math.Max(1, (int)Math.Round(estimate * .95 * qualityFactor / 5) * 5);
        var quick = recommendation <= 0 ? 0 : Math.Max(1, (int)Math.Round(recommendation * .88 / 5) * 5);
        var patient = recommendation <= 0 ? 0 : Math.Max(1, (int)Math.Round(recommendation * 1.15 / 5) * 5);
        var confidence = exact.Length >= 8 ? "high" : exact.Length >= 3 || similar.Length >= 8 ? "medium" : prices.Length > 0 || weekly is not null ? "low" : "insufficient";
        var bands = prices.GroupBy(p => p < 100 ? "under 100p" : p < 250 ? "100–249p" : p < 500 ? "250–499p" : p < 1000 ? "500–999p" : "1000p+")
            .ToDictionary(g => g.Key, g => g.Count());
        var closures = observedClosures.Where(x => x.Signature == signature).Select(x => x.LifetimeHours).Where(x => x >= 0).ToArray();
        var endo = EndoValue(masteryRank, modRank, rerolls);
        var guidance = BuildGuidance(statClass, disposition, positives, negative);
        return new(weaponName, weaponSlug, positives, negative, exact.Length, similar.Length,
            exact.Count(r => r.Status is "online" or "ingame"), prices.Length == 0 ? null : prices[0], median, prices.Length == 0 ? null : prices[^1], recommendation, quick, patient, confidence, bands,
            medianAge, closures.Length, closures.Length == 0 ? null : Median(closures), weekly,
            quality, signature, confirmed.Length, confirmedMedian, confirmedMedianLifetime,
            masteryRank, modRank, rerolls, endo,
            "Exact-roll figures are live asks. Weekly figures are completed family trades without roll details. Observed closures may be sales or withdrawals.",
            guidance, grades, assessment?.Desired, assessment?.Preferred, assessment?.Note ?? "",
            profileSource, rollRule?.PositiveExpression ?? "", rollRule?.HarmlessNegatives ?? [], excludedAskOutliers,
            listingAgeAdjustment, saleVelocityAdjustment);
    }
    private sealed record RollAssessment(int Desired, bool Preferred, string Note);
    private static RollRule ApplyKnownRollConditions(string weaponName, IReadOnlyCollection<string> positives,
        IReadOnlyList<RivenStatRange> ranges, IReadOnlyDictionary<string, double?> values, RollRule rule, out string conditionNote)
    {
        var notes = ""; var key = Key(weaponName); var effective = rule;
        double? Value(string stat) => values.GetValueOrDefault(stat) is { } observed ? Math.Abs(observed) : null;
        double Minimum(string stat) => ranges.FirstOrDefault(range => range.Positive && range.Slug == stat)?.Minimum ?? 0;
        double Maximum(string stat) => ranges.FirstOrDefault(range => range.Positive && range.Slug == stat)?.Maximum ?? 0;
        void ExcludeStat(string stat, string message)
        {
            effective = effective with { Alternatives = effective.Alternatives.Where(alt =>
            {
                var desired = alt.Pool.Concat(alt.Mandatory.SelectMany(group => group));
                return !desired.Contains(stat);
            }).ToArray() };
            notes += message + " ";
        }
        if (key.StartsWith("verglas", StringComparison.Ordinal) && positives.Contains("critical_chance"))
        {
            const double breakpoint = 257.2; var entered = Value("critical_chance"); var ceiling = Maximum("critical_chance");
            if (entered < breakpoint || !entered.HasValue && ceiling < breakpoint)
            {
                ExcludeStat("critical_chance", $"Verglas CC cannot satisfy the {breakpoint:0.0}% Tenacious Bond breakpoint for this arrangement (modeled ceiling {ceiling:0.#}%); every CC-dependent route was excluded.");
            }
        }
        if (key == "exergis" && positives.Contains("magazine_capacity"))
        {
            var entered = Value("magazine_capacity"); var ceiling = Maximum("magazine_capacity");
            if (entered < 50 || !entered.HasValue && ceiling < 50)
                ExcludeStat("magazine_capacity", $"Exergis Magazine Capacity cannot reach its 50% weapon-profile minimum for this arrangement ({(entered.HasValue ? $"entered {entered:0.#}%" : $"modeled ceiling {ceiling:0.#}%")}).");
        }
        if (key.StartsWith("rubico", StringComparison.Ordinal) && positives.Contains("multishot"))
        {
            var required = key.Contains("prime", StringComparison.Ordinal) ? 50 : 73.1;
            var entered = Value("multishot"); var ceiling = Maximum("multishot");
            if (entered < required || !entered.HasValue && ceiling < required)
                ExcludeStat("multishot", $"Rubico Multishot cannot reach the {(required == 50 ? "Prime" : "base/family-safe")} {required:0.#}% minimum ({(entered.HasValue ? $"entered {entered:0.#}%" : $"modeled ceiling {ceiling:0.#}%")}).");
        }
        if (key.EndsWith("simulor", StringComparison.Ordinal) && positives.Contains("multishot"))
        {
            var entered = Value("multishot"); var floor = Minimum("multishot"); var ceiling = Maximum("multishot");
            if (entered.HasValue && Math.Abs(entered.Value - 100) > .5 || !entered.HasValue && (ceiling < 99.5 || floor > 100.5))
                ExcludeStat("multishot", $"Simulor Multishot cannot meet its approximately 100% orb-stacking target ({(entered.HasValue ? $"entered {entered:0.#}%" : $"modeled range {floor:0.#}–{ceiling:0.#}%")}).");
        }
        if (key.StartsWith("vulkar", StringComparison.Ordinal) && positives.Contains("critical_chance"))
        {
            var required = key.Contains("wraith", StringComparison.Ordinal) ? 200 : 210;
            var entered = Value("critical_chance"); var ceiling = Maximum("critical_chance");
            if (entered <= required || !entered.HasValue && ceiling <= required)
                ExcludeStat("critical_chance", $"Vulkar Critical Chance cannot pass the >{required}% {(required == 200 ? "Wraith" : "normal/family-safe")} breakpoint ({(entered.HasValue ? $"entered {entered:0.#}%" : $"modeled ceiling {ceiling:0.#}%")}).");
        }
        if (key == "zenith" && positives.Contains("multishot"))
        {
            var entered = Value("multishot"); var ceiling = Maximum("multishot");
            if (entered <= 100 || !entered.HasValue && ceiling <= 100)
                ExcludeStat("multishot", $"Zenith Multishot cannot pass the >100% Profit-Taker breakpoint ({(entered.HasValue ? $"entered {entered:0.#}%" : $"modeled ceiling {ceiling:0.#}%")}).");
        }
        conditionNote = notes; return effective;
    }
    private static RollAssessment? AssessRoll(string weaponName, string[] positives, string? negative, RollRule? rule,
        IReadOnlyList<RivenStatRange> ranges, double?[]? positiveValues)
    {
        if (rule is null) return null;
        var values = positives.Select((stat, index) => (stat, value: positiveValues is not null && index < positiveValues.Length ? positiveValues[index] : null))
            .ToDictionary(pair => pair.stat, pair => pair.value);
        var effectiveRule = ApplyKnownRollConditions(weaponName, positives, ranges, values, rule, out var conditionNote);
        var note = conditionNote + rule.Notes;
        var match = RivenRules.Evaluate(positives, negative is null ? [] : [negative], effectiveRule);
        if (match is null) return new(0, false, ("No weapon-profile alternative remains after its numeric breakpoint checks. " + note).Trim());
        var effectiveDesired = match.MandatoryMatched ? match.DesiredCount : 0;
        var explanation = (match.MandatoryMatched
                ? $"{match.DesiredCount}/{positives.Length} positives match the applicable weapon profile"
                : $"{match.DesiredCount} individually useful positive(s) were found, but a mandatory stat pair is incomplete, so 0/{positives.Length} count as an effective selling profile") + "; " +
            (match.HarmlessNegative ? "the negative is supported as low-impact" : negative is null ? "there is no value-boosting harmless negative" : "the negative is not supported as harmless") + ". " + note;
        return new(effectiveDesired, match.Qualifies, explanation.Trim());
    }
    public static RivenStatGrade Grade(RivenStatRange range, double value)
    {
        var middle = (Math.Abs(range.Minimum) + Math.Abs(range.Maximum)) / 2;
        var variance = middle <= 0 ? 0 : (Math.Abs(value) / middle - 1) * 100 * (range.Positive ? 1 : -1);
        var grade = variance >= 9.5 ? "S" : variance >= 7.5 ? "A+" : variance >= 5.5 ? "A" : variance >= 3.5 ? "A-" :
            variance >= 1.5 ? "B+" : variance >= -1.5 ? "B" : variance >= -3.5 ? "B-" : variance >= -5.5 ? "C+" :
            variance >= -7.5 ? "C" : variance >= -9.5 ? "C-" : "F";
        return new(range.Slug, range.Positive, value, Math.Round(variance, 3), grade);
    }
    private static double QualityScore(RivenStatRange range, double value)
    {
        var low = Math.Min(Math.Abs(range.Minimum), Math.Abs(range.Maximum));
        var high = Math.Max(Math.Abs(range.Minimum), Math.Abs(range.Maximum));
        if (high <= low) return .5;
        var position = Math.Clamp((Math.Abs(value) - low) / (high - low), 0, 1);
        return range.Positive ? position : 1 - position;
    }
    private static string BuildGuidance(string statClass, double disposition, IReadOnlyCollection<string> positives, string? negative)
    {
        var notes = new List<string>();
        if (positives.Contains("critical_damage")) notes.Add("Critical Damage assumes the weapon's real build already uses a Critical Damage mod");
        if (positives.Contains("critical_chance") && statClass == "shotgun") notes.Add("Shotgun Riven Critical Chance is based on the weaker shotgun scaler and normally cannot replace Critical Deceleration by itself");
        else if (positives.Contains("critical_chance") && disposition < .9) notes.Add("Critical Chance is disposition-sensitive; at this low disposition verify that the Riven can actually replace the build's normal Critical Chance slot");
        if (negative == "finisher_damage" && statClass == "melee") notes.Add("−Finisher is not treated as universally harmless, especially on nikanas, daggers, dual daggers, claws, swords, fists, scythes and Melee Crescendo setups");
        return string.Join(". ", notes) + (notes.Count > 0 ? "." : "Weapon-specific exceptions still require build review.");
    }
    public static double? RollQuality(JsonElement auction, string statClass, double disposition)
    {
        var rank = auction.Get("item").Get("mod_rank");
        if (rank.ValueKind != JsonValueKind.Undefined && (int)(rank.Number() ?? -1) != 8) return null;
        var attrs = Attributes(auction).ToArray(); if (attrs.Any(a => !a.Value.HasValue)) return null;
        RivenStatRange[] ranges;
        try { ranges = Ranges(attrs.Where(a => a.Positive).Select(a => a.Slug).ToArray(), attrs.LastOrDefault(a => !a.Positive).Slug, statClass, disposition); }
        catch (ArgumentException) { return null; }
        var scores = new List<double>();
        foreach (var range in ranges)
        {
            var observed = attrs.Last(a => a.Slug == range.Slug && a.Positive == range.Positive).Value!.Value;
            scores.Add(QualityScore(range, observed));
        }
        return scores.Count > 0 ? Math.Round(scores.Average() * 100, 1) : null;
    }
    public static int? AuctionPrice(JsonElement auction)
    {
        if (auction.Get("closed").Bool() || !auction.Get("visible").Bool(true)) return null;
        var raw = auction.Get("buyout_price");
        if (raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || raw.Number() == 0)
        {
            if (!auction.Get("is_direct_sell").Bool(auction.Get("is_direct_sale").Bool())) return null;
            raw = auction.Get("starting_price");
        }
        // Python int rejects a string such as "10.5" but truncates an actual JSON float.
        if (raw.ValueKind == JsonValueKind.String) return int.TryParse(raw.Text(), out var integer) && integer > 0 ? integer : null;
        var value = raw.Number(); return value is >= 1 and <= int.MaxValue ? (int)value : null;
    }
    public static WeeklyPrice? ParseWeekly(IEnumerable<JsonElement> rows, string weapon, bool rerolled)
    {
        var row = rows.FirstOrDefault(r => Key(r.Get("compatibility").Text()) == Key(weapon) && r.Get("rerolled").Bool() == rerolled);
        if (row.ValueKind == JsonValueKind.Undefined) return null;
        return new(row.Get("compatibility").Text(weapon), row.Get("itemType").Text("Riven Mod"), rerolled,
            row.Get("avg").Number() ?? 0, row.Get("median").Number() ?? row.Get("avg").Number() ?? 0,
            row.Get("min").Number() ?? 0, row.Get("max").Number() ?? 0, row.Get("stddev").Number() ?? 0, row.Get("pop").Number() ?? 0,
            new DateTimeOffset(2024, 5, 13, 0, 0, 0, TimeSpan.Zero));
    }
    public static RivenAskRange DesiredAskRange(IEnumerable<JsonElement> auctions, string weaponName,
        string statClass, double disposition, RollRule rule)
    {
        var matches = new List<(int Price, bool Online)>();
        foreach (var auction in auctions)
        {
            var price = AuctionPrice(auction); if (!price.HasValue) continue;
            var attrs = Attributes(auction).ToArray();
            var positives = attrs.Where(attribute => attribute.Positive).Select(attribute => attribute.Slug).ToHashSet(StringComparer.Ordinal);
            var negatives = attrs.Where(attribute => !attribute.Positive).Select(attribute => attribute.Slug).ToHashSet(StringComparer.Ordinal);
            if (positives.Count is < 2 or > 3 || negatives.Count > 1) continue;
            RivenStatRange[] ranges;
            try { ranges = Ranges(positives.ToArray(), negatives.FirstOrDefault(), statClass, disposition); }
            catch (ArgumentException) { continue; }
            var values = attrs.Where(attribute => attribute.Positive).GroupBy(attribute => attribute.Slug)
                .ToDictionary(group => group.Key, group => group.Last().Value);
            var effective = ApplyKnownRollConditions(weaponName, positives, ranges, values, rule, out _);
            if (RivenRules.Evaluate(positives, negatives, effective)?.Qualifies != true) continue;
            var sellerStatus = auction.Get("owner").Get("status").Text("offline").ToLowerInvariant();
            matches.Add((price.Value, sellerStatus is "online" or "ingame"));
        }
        static (int? Low, int? Median, int? High) Band(IEnumerable<int> source)
        {
            var values = WithoutExtremeAskOutliers(source);
            if (values.Length == 0) return (null, null, null);
            return (values[0], (int)Math.Round(RivenPricing.Median(values.Select(value => (double)value))), values[^1]);
        }
        var all = Band(matches.Select(match => match.Price));
        var online = Band(matches.Where(match => match.Online).Select(match => match.Price));
        return new(matches.Count, matches.Count(match => match.Online), all.Low, all.Median, all.High,
            online.Low, online.Median, online.High);
    }
    private sealed record Candidate(JsonElement Auction, int Price, HashSet<string> Pos, HashSet<string> Neg, double? Quality, RollMatch? Curated);
    public static RivenDeal[] FindDeals(IEnumerable<JsonElement> auctions, string weaponSlug, string weaponName = "", WeeklyPrice? weekly = null,
        string? statClass = null, double? disposition = null, double minimumDiscountPct = 20, int? maximumPrice = null,
        bool onlineOnly = true, int limit = 8, RollRule? rollRule = null, bool curatedOnly = false,
        string curatedProfile = "", string curatedNotes = "", CancellationToken ct = default)
    {
        double? ceiling = null;
        if (weekly is not null)
        {
            var candidates = new[] { Math.Max(0, weekly.Maximum), Math.Max(Math.Max(0, weekly.Median) * 8, Math.Max(0, weekly.Average) * 4) }.Where(v => v > 0).ToArray();
            if (candidates.Length > 0) ceiling = candidates.Min();
        }
        var active = new List<Candidate>();
        foreach (var a in auctions)
        {
            ct.ThrowIfCancellationRequested(); var price = AuctionPrice(a); var status = a.Get("owner").Get("status").Text("offline").ToLowerInvariant();
            if (!price.HasValue || maximumPrice.HasValue && price > maximumPrice || onlineOnly && status is not ("online" or "ingame")) continue;
            var attrs = Attributes(a).ToArray(); var pos = attrs.Where(x => x.Positive).Select(x => x.Slug).ToHashSet(); var neg = attrs.Where(x => !x.Positive).Select(x => x.Slug).ToHashSet();
            if (pos.Count == 0) continue;
            RollMatch? match = null;
            if (rollRule is not null)
            {
                var effectiveRule = rollRule;
                if (statClass is not null && disposition.HasValue)
                {
                    RivenStatRange[] ranges;
                    try { ranges = Ranges(pos.ToArray(), neg.FirstOrDefault(), statClass, disposition.Value); }
                    catch (ArgumentException) { ranges = []; }
                    var values = attrs.Where(attribute => attribute.Positive).GroupBy(attribute => attribute.Slug)
                        .ToDictionary(group => group.Key, group => group.Last().Value);
                    effectiveRule = ApplyKnownRollConditions(weaponName, pos, ranges, values, rollRule, out _);
                }
                match = RivenRules.Evaluate(pos, neg, effectiveRule);
            }
            active.Add(new(a, price.Value, pos, neg, statClass is not null && disposition.HasValue ? RollQuality(a, statClass, disposition.Value) : null, match));
        }
        var deals = new List<RivenDeal>();
        for (var i = 0; i < active.Count; i++)
        {
            ct.ThrowIfCancellationRequested(); var a = active[i];
            if (curatedOnly && a.Curated?.Qualifies != true || ceiling.HasValue && a.Price > ceiling) continue;
            var peers = new List<(double Value, double Weight)>();
            for (var j = 0; j < active.Count; j++)
            {
                if ((j & 255) == 0) ct.ThrowIfCancellationRequested();
                if (i == j) continue; var b = active[j];
                if (curatedOnly && b.Curated?.Qualifies != true) continue;
                var union = a.Pos.Union(b.Pos).Count();
                var score = union > 0 ? (double)a.Pos.Intersect(b.Pos).Count() / union : 1;
                if (a.Pos.Count == b.Pos.Count) score += .08;
                var negEqual = a.Neg.SetEquals(b.Neg);
                score += negEqual ? .16 : a.Neg.Count > 0 && b.Neg.Count > 0 ? .04 : 0;
                score = Math.Min(1.25, score);
                if (score < .58 || ceiling.HasValue && b.Price > ceiling) continue;
                var qualityWeight = 1d;
                if (a.Quality.HasValue && b.Quality.HasValue)
                { var diff = Math.Abs(a.Quality.Value - b.Quality.Value); if (diff > 45) continue; qualityWeight = Math.Max(.35, 1 - diff / 100); }
                peers.Add((b.Price, Math.Pow(score, 3) * (a.Pos.SetEquals(b.Pos) && negEqual ? 2 : 1) * qualityWeight));
            }
            if (peers.Count < 3) continue;
            var threshold = peers.Sum(p => p.Weight) * .5; var running = 0d; var median = 0;
            foreach (var peer in peers.OrderBy(p => p.Value).ThenBy(p => p.Weight)) { running += peer.Weight; if (running >= threshold) { median = (int)Math.Round(peer.Value); break; } }
            if (median <= a.Price) continue;
            var discount = (median - a.Price) / (double)median * 100;
            var resale = Math.Max(1, (int)Math.Round(median * .90));
            if (discount < minimumDiscountPct || resale <= a.Price) continue;
            var owner = a.Auction.Get("owner"); var attrs = Attributes(a.Auction).ToArray();
            object[][] Rolls(bool positive) => attrs.Where(x => x.Positive == positive).Select(x => new object[] { x.Slug, x.Value! }).ToArray();
            var seller = owner.Get("ingame_name").Text(); var slug = owner.Get("slug").Text();
            deals.Add(new(a.Auction.Get("id").Text(), weaponSlug, a.Price, median, discount, resale - a.Price,
                a.Pos.Order(StringComparer.Ordinal).ToArray(), a.Neg.Order(StringComparer.Ordinal).ToArray(), Rolls(true), Rolls(false),
                seller.Length > 0 ? seller : slug.Length > 0 ? slug : "Unknown", slug.Length > 0 ? slug : seller.Length > 0 ? seller : "Unknown",
                owner.Get("status").Text("offline"), peers.Count, weaponName, resale, (resale - a.Price) / (double)a.Price * 100,
                weekly?.Median, weekly?.Popularity, a.Quality, a.Curated?.Qualifies == true, a.Curated?.DesiredCount, a.Curated?.PositiveCount, curatedProfile, curatedNotes));
        }
        return Sort(deals).Take(Math.Max(0, limit)).ToArray();
    }
    public static IOrderedEnumerable<RivenDeal> Sort(IEnumerable<RivenDeal> deals) => deals.OrderByDescending(d => d.CuratedMatch)
        .ThenByDescending(d => d.CuratedDesiredCount ?? 0).ThenByDescending(d => d.PotentialMargin * (.5 + (d.WeeklyPopularity ?? 0) / 200))
        .ThenByDescending(d => d.RoiPct).ThenByDescending(d => d.PotentialMargin).ThenByDescending(d => d.ComparableCount).ThenBy(d => d.Price)
        .ThenBy(d => d.WeaponName, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.AuctionId, StringComparer.Ordinal);
}
