namespace RelicFrame.Core;

public sealed record ArcaneQuote(string Name, string Slug, int BaseVosfor, int MaxRank, int CopiesAtMax,
    double? RankZeroPrice, double? RankZeroAdjusted, int RankZeroAsks,
    double? MaxRankPrice, double? MaxRankAdjusted, int MaxRankAsks,
    double RankZeroDissolveValue, double MaxRankDissolveValue,
    string RankZeroRecommendation, string MaxRankRecommendation);

public sealed record ArcanePackResult(string Name, double DirectSellEv, double OptimalEv, double PlatPerHundredVosfor,
    double ExpectedRecycledVosfor, double SellChance, int PricedArcanes, int TotalArcanes);

public sealed record ArcaneEconomyResult(IReadOnlyList<ArcaneQuote> Quotes, IReadOnlyList<ArcanePackResult> Packs,
    double PlatinumPerVosfor, int ReadyBooks, int TotalBooks);
public sealed record ArcanePackMember(string Name, string Rarity, double Chance, int BaseVosfor);

public sealed class ArcaneEconomy(LiveMarket market)
{
    private sealed record Tier(double Chance, int Vosfor, string[] Names);
    private sealed record Pack(string Name, Tier[] Tiers);
    private sealed record Price(double? Raw, double? Adjusted, int Asks);

    private static readonly Pack[] Packs =
    [
        new("Cavia", [
            T(.45, 18, "Melee Fortification", "Melee Retaliation"),
            T(.50, 24, "Arcane Battery", "Arcane Ice Storm", "Melee Afflictions", "Melee Animosity", "Melee Exposure", "Melee Influence", "Melee Vortex", "Secondary Fortifier", "Secondary Surge"),
            T(.05, 84, "Melee Crescendo", "Melee Duplicate")]),
        new("Duviri", [
            T(.45, 18, "Arcane Intention", "Magus Aggress"),
            T(.50, 24, "Arcane Power Ramp", "Primary Blight", "Primary Exhilarate", "Primary Obstruct", "Shotgun Vendetta", "Akimbo Slip Shot", "Secondary Outburst"),
            T(.05, 84, "Arcane Reaper", "Longbow Sharpshot", "Secondary Shiver")]),
        new("Eidolon", [
            T(.40, 14, "Arcane Consequence", "Arcane Ice", "Arcane Momentum", "Arcane Nullifier", "Arcane Tempo", "Arcane Warmth"),
            T(.35, 21, "Arcane Acceleration", "Arcane Agility", "Arcane Awakening", "Arcane Deflection", "Arcane Eruption", "Arcane Guardian", "Arcane Healing", "Arcane Phantasm", "Arcane Resistance", "Arcane Strike", "Arcane Trickery", "Arcane Velocity", "Arcane Victory"),
            T(.20, 28, "Arcane Aegis", "Arcane Arachne", "Arcane Avenger", "Arcane Fury", "Arcane Precision", "Arcane Pulse", "Arcane Rage", "Arcane Ultimatum"),
            T(.05, 98, "Arcane Barrier", "Arcane Energize", "Arcane Grace")]),
        new("Holdfasts", [T(1, 22, "Arcane Blessing", "Arcane Rise", "Molt Augmented", "Molt Efficiency", "Molt Reconstruct", "Molt Vigor", "Fractalized Reset", "Primary Frostbite", "Cascadia Accuracy", "Cascadia Empowered", "Cascadia Flare", "Cascadia Overcharge", "Conjunction Voltage", "Emergence Dissipate", "Emergence Renewed", "Emergence Savior", "Eternal Eradicate", "Eternal Logistics", "Eternal Onslaught")]),
        new("Höllvania", [
            T(.95, 24, "Arcane Bellicose", "Arcane Camisado", "Arcane Crepuscular", "Arcane Impetus", "Arcane Truculence", "Melee Doughty", "Primary Crux", "Secondary Enervate"),
            T(.05, 84, "Arcane Escapist", "Arcane Hot Shot", "Arcane Universal Fallout")]),
        new("Necralisk", [T(1, 24, "Arcane Double Back", "Arcane Steadfast", "Theorem Contagion", "Theorem Demulcent", "Theorem Infection", "Primary Plated Round", "Secondary Encumber", "Secondary Kinship", "Residual Boils", "Residual Malodor", "Residual Shock", "Residual Viremia")]),
        new("Ostron", [
            T(.60, 12, "Magus Husk", "Magus Vigor", "Virtuos Null", "Virtuos Tempo"),
            T(.30, 18, "Exodia Triumph", "Exodia Valor", "Magus Cadence", "Magus Cloud", "Magus Replenish", "Virtuos Fury", "Virtuos Strike"),
            T(.10, 24, "Exodia Brave", "Exodia Force", "Exodia Hunt", "Exodia Might", "Magus Elevate", "Magus Nourish", "Virtuos Ghost", "Virtuos Shadow")]),
        new("Solaris", [
            T(.60, 12, "Magus Accelerant", "Magus Anomaly", "Magus Drive", "Magus Firewall", "Magus Overload", "Virtuos Spike", "Virtuos Surge"),
            T(.30, 18, "Magus Glitch", "Magus Repair", "Virtuos Forge", "Virtuos Trojan"),
            T(.10, 24, "Pax Bolt", "Pax Charge", "Pax Seeker", "Pax Soar", "Magus Destruct", "Magus Lockdown", "Magus Melt", "Magus Revert")]),
        new("Steel Path", [T(1, 20, "Arcane Blade Charger", "Arcane Bodyguard", "Arcane Pistoleer", "Arcane Primary Charger", "Arcane Tanker", "Primary Deadhead", "Primary Dexterity", "Primary Merciless", "Secondary Deadhead", "Secondary Dexterity", "Secondary Merciless")])
    ];

    private static readonly IReadOnlyDictionary<string, int> CollectionVosfor = Packs.SelectMany(pack => pack.Tiers)
        .SelectMany(tier => tier.Names.Select(name => new KeyValuePair<string, int>(name, tier.Vosfor)))
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    private static Tier T(double chance, int vosfor, params string[] names) => new(chance, vosfor, names);
    public static IReadOnlyList<string> CollectionNames => Packs.Select(pack => pack.Name).ToArray();
    public static IReadOnlyList<ArcanePackMember> CollectionMembers(string collection) => Packs
        .FirstOrDefault(pack => pack.Name.Equals(collection, StringComparison.OrdinalIgnoreCase))?.Tiers
        .SelectMany(tier => tier.Names.Select(name => new ArcanePackMember(name, Rarity(tier.Vosfor), tier.Chance / tier.Names.Length, tier.Vosfor))).ToArray() ?? [];

    private static string Rarity(int vosfor) => vosfor switch
    {
        12 or 14 => "Common",
        18 or 21 => "Uncommon",
        20 or 22 or 24 or 28 => "Rare",
        84 or 98 => "Legendary",
        _ => "Other"
    };

    public async Task<ArcaneEconomyResult> AnalyzeAsync(string? excludedSellerSlug, IProgress<(int Current, int Total)>? progress, CancellationToken ct)
    {
        var items = market.ArcaneItems.Where(item => BaseVosfor(item) > 0).ToArray();
        foreach (var item in items) market.RegisterBook(item.Name);
        var current = 0;
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (item, token) =>
        {
            await market.EnsureBookAsync(item.Name, token);
            progress?.Report((Interlocked.Increment(ref current), items.Length));
        });

        var input = items.Select(item =>
        {
            var maxRank = item.MaxRank is > 0 ? item.MaxRank.Value : 5;
            var copies = (maxRank + 1) * (maxRank + 2) / 2;
            return (Item: item, Vosfor: BaseVosfor(item), MaxRank: maxRank, Copies: copies,
                R0: Estimate(item.Name, 0, excludedSellerSlug), Max: Estimate(item.Name, maxRank, excludedSellerSlug));
        }).ToArray();
        var byName = input.ToDictionary(row => row.Item.Name, StringComparer.OrdinalIgnoreCase);

        double shadow = 0;
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var next = Packs.Max(pack => PackEv(pack, byName, shadow).Optimal / 200d);
            if (!double.IsFinite(next)) { shadow = 0; break; }
            if (Math.Abs(next - shadow) < .0001) { shadow = next; break; }
            shadow = next;
        }

        var quotes = input.Select(row =>
        {
            var rankZeroDust = row.Vosfor * shadow;
            var maxDust = row.Vosfor * row.Copies * shadow;
            return new ArcaneQuote(row.Item.Name, row.Item.Slug, row.Vosfor, row.MaxRank, row.Copies,
                row.R0.Raw, row.R0.Adjusted, row.R0.Asks, row.Max.Raw, row.Max.Adjusted, row.Max.Asks,
                rankZeroDust, maxDust, Recommend(row.R0.Adjusted, rankZeroDust), Recommend(row.Max.Adjusted, maxDust));
        }).ToArray();
        var packs = Packs.Select(pack =>
        {
            var result = PackEv(pack, byName, shadow);
            return new ArcanePackResult(pack.Name, result.Direct, result.Optimal, result.Optimal / 2,
                result.Recycled, result.SellChance, result.Priced, result.Total);
        }).OrderByDescending(pack => pack.OptimalEv).ThenBy(pack => pack.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(quotes, packs, shadow, items.Count(item => Estimate(item.Name, 0, excludedSellerSlug).Raw.HasValue), items.Length);
    }

    private Price Estimate(string name, int rank, string? excludedSellerSlug)
    {
        var online = market.MatchRank(name, rank, true, excludedSellerSlug).Best?.Price;
        var visible = market.MatchRank(name, rank, false, excludedSellerSlug).Entries;
        var median = OrderMath.RecentVisibleLowerMedian(visible);
        var raw = online.HasValue && median.HasValue ? Math.Min((online.Value + median.Value) / 2, median.Value * 1.5) : online ?? median;
        var confidence = visible.Count switch { >= 5 => 1d, >= 3 => .9, 2 => .8, 1 => .65, _ => 0 };
        return new(raw, raw * confidence, visible.Count);
    }

    private static (double Direct, double Optimal, double Recycled, double SellChance, int Priced, int Total) PackEv(
        Pack pack, IReadOnlyDictionary<string, (MarketCatalogItem Item, int Vosfor, int MaxRank, int Copies, Price R0, Price Max)> prices, double shadow)
    {
        double direct = 0, optimal = 0, recycled = 0, sellChance = 0; var priced = 0; var total = 0;
        foreach (var tier in pack.Tiers)
        {
            if (tier.Names.Length == 0) continue;
            var chance = tier.Chance / tier.Names.Length;
            foreach (var name in tier.Names)
            {
                total++;
                var sell = prices.TryGetValue(name, out var quoted) ? quoted.R0.Adjusted : null;
                if (sell.HasValue) priced++;
                var sale = sell ?? 0; var dust = tier.Vosfor * shadow;
                direct += chance * sale;
                optimal += chance * Math.Max(sale, dust);
                if (sale >= dust && sell.HasValue) sellChance += chance; else recycled += chance * tier.Vosfor;
            }
        }
        return (direct * 3, optimal * 3, recycled * 3, sellChance, priced, total);
    }

    private static string Recommend(double? sell, double dust)
    {
        if (!sell.HasValue) return "DISSOLVE";
        if (sell.Value >= dust * 1.1) return "SELL";
        if (dust >= sell.Value * 1.1) return "DISSOLVE";
        return "HOLD";
    }

    private static int BaseVosfor(MarketCatalogItem item)
    {
        if (item.Name is "Exodia Contagion" or "Exodia Epidemic") return 0;
        if (CollectionVosfor.TryGetValue(item.Name, out var exact)) return exact;
        if (item.Tags.Contains("legendary", StringComparer.OrdinalIgnoreCase)) return 84;
        if (item.Tags.Contains("rare", StringComparer.OrdinalIgnoreCase)) return 24;
        if (item.Tags.Contains("uncommon", StringComparer.OrdinalIgnoreCase)) return 18;
        if (item.Tags.Contains("common", StringComparer.OrdinalIgnoreCase)) return 12;
        return 0;
    }
}
