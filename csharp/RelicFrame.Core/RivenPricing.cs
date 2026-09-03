using System.Text.Json;
using System.Text.RegularExpressions;

namespace RelicFrame.Core;

public sealed record WeeklyPrice(string Weapon, string RivenType, bool Rerolled, double Average, double Median,
    double Minimum, double Maximum, double StandardDeviation, double Popularity);
public sealed record RivenStatRange(string Slug, bool Positive, double Minimum, double Maximum, string Unit);
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

public static class RivenPricing
{
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
            var weighted = basis * disposition * (positive ? bonus : malus);
            return new(slug, positive, positive ? weighted * .9 : -weighted * 1.1, positive ? weighted * 1.1 : -weighted * .9, Unit(slug));
        }
        return positives.Select(s => One(s, true)).Concat(negative is null ? [] : new[] { One(negative, false) }).ToArray();
    }
    private static IEnumerable<(string Slug, bool Positive, double? Value)> Attributes(JsonElement auction) =>
        auction.Get("item").Get("attributes").Rows().Select(a => ((a.Get("url_name").Text() is { Length: > 0 } slug ? slug : a.Get("slug").Text()).Trim().ToLowerInvariant(),
            a.Get("positive").ValueKind == JsonValueKind.Undefined ? a.Get("postive").Bool(true) : a.Get("positive").Bool(true), a.Get("value").Number())).Where(a => a.Item1.Length > 0);
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
            var low = Math.Min(Math.Abs(range.Minimum), Math.Abs(range.Maximum)); var high = Math.Max(Math.Abs(range.Minimum), Math.Abs(range.Maximum));
            if (high > low) scores.Add(Math.Clamp((Math.Abs(observed) - low) / (high - low), 0, 1));
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
            row.Get("min").Number() ?? 0, row.Get("max").Number() ?? 0, row.Get("stddev").Number() ?? 0, row.Get("pop").Number() ?? 0);
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
            active.Add(new(a, price.Value, pos, neg, statClass is not null && disposition.HasValue ? RollQuality(a, statClass, disposition.Value) : null,
                RivenRules.Evaluate(pos, neg, rollRule)));
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
                if (i == j) continue; var b = active[j]; var union = a.Pos.Union(b.Pos).Count();
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
        .ThenByDescending(d => d.CuratedDesiredCount ?? 0).ThenByDescending(d => d.PotentialMargin * (.5 + (d.WeeklyPopularity ?? 0) / 200)).ThenByDescending(d => d.RoiPct);
}
