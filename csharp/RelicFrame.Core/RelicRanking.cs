namespace RelicFrame.Core;

public sealed record PriceInfo(double? Online = null, double? OnlineQuantity = null, bool OnlineSubtypeMatched = true,
    bool OnlineIsOutlier = false, double? OfflineIncluded = null, double? OfflineQuantity = null,
    bool OfflineSubtypeMatched = true, bool OfflineIsOutlier = false);
public sealed record RelicChannel(Profit Profit, string Category, bool ZeroQuantity, double? Cost, double? Quantity,
    bool Matched, bool Outlier, int Risk, double? WinChance, double? LossChance);
public sealed record RelicRow(Relic Relic, RelicChannel Online, RelicChannel Offline, string OverallCategory, int BestRisk,
    Odds? Odds, double? PlatPerTrace, DucatEfficiency DucatInfo)
{
    public IEnumerable<Profit> Profits(string scope) => scope switch
    { "Online only" => [Online.Profit], "Offline only" => [Offline.Profit], _ => [Online.Profit, Offline.Profit] };
    public static RelicRow Compute(Relic relic, Refinement tier, IReadOnlyDictionary<string, double?> prices,
        PriceInfo info, IReadOnlyDictionary<string, int> ducats, double traceRate = 0, string scope = "Online + Offline")
    {
        RelicChannel Channel(double? cost, double? quantity, bool matched, bool outlier, bool online)
        {
            var usable = cost.HasValue && (!quantity.HasValue || quantity > 0) ? cost : null;
            var p = relic.Profitability(tier, prices, usable, traceRate);
            var dist = relic.BuyN(tier, prices, usable, traceRate, 1);
            double? loss = p.PriceKnown ? dist.ProbLossPct : null;
            return new(p, p.Category, cost.HasValue && quantity == 0, cost, quantity, matched, outlier,
                p.Risk(loss, matched, outlier, online), p.PriceKnown ? dist.ProbProfitPct : null, loss);
        }
        var on = Channel(info.Online, info.OnlineQuantity, info.OnlineSubtypeMatched, info.OnlineIsOutlier, true);
        var off = Channel(info.OfflineIncluded, info.OfflineQuantity, info.OfflineSubtypeMatched, info.OfflineIsOutlier, false);
        var category = scope == "Online only" ? on.Category : scope == "Offline only" ? off.Category
            : CategoryRank(on.Category) >= CategoryRank(off.Category) ? on.Category : off.Category;
        var risk = scope == "Online only" ? on.Risk : scope == "Offline only" ? off.Risk : Math.Min(on.Risk, off.Risk);
        return new(relic, on, off, category, risk, relic.BestRewardOdds(tier, prices) ?? new Odds("", 0, "", 0, null, 0), relic.PlatPerTrace(tier, prices),
            relic.DucatEfficiency(tier, ducats, info.Online ?? info.OfflineIncluded, traceRate));
    }
    public bool Passes(string scope, string vault = "Any", double? minRoi = null, double? maxCost = null,
        double? minReward = null, bool guaranteedOnly = false)
    {
        if (vault == "Vaulted" && Relic.Vaulted is not true || vault == "Unvaulted" && Relic.Vaulted is not false || vault == "Unknown" && Relic.Vaulted is not null) return false;
        var known = Profits(scope).Where(p => p.PriceKnown).ToArray();
        if (minRoi.HasValue && !known.Any(p => p.ExpectedRoiPct >= minRoi)) return false;
        if (maxCost.HasValue && !known.Any(p => p.TotalCost <= maxCost)) return false;
        if (minReward.HasValue && !(Odds?.Price >= minReward)) return false;
        return !guaranteedOnly || OverallCategory == "green";
    }
    public double[] RankKey(string mode, string scope)
    {
        var profits = Profits(scope).ToArray();
        var hasPrice = profits.Any(p => p.PriceKnown) ? 1 : 0;
        var known = profits.Where(p => p.PriceKnown).ToArray();
        var roi = known.Where(p => p.ExpectedRoiPct.HasValue).Select(p => p.ExpectedRoiPct!.Value).DefaultIfEmpty(double.NegativeInfinity).Max();
        return mode switch
        {
            "Guaranteed Profit" => [hasPrice, profits.Max(p => p.WorstCaseProfit)],
            "Expected Profit" => [hasPrice, profits.Max(p => p.ExpectedProfit)],
            "Best ROI" => double.IsNegativeInfinity(roi) ? [0, roi, roi] : [1, roi * (1 - BestRisk / 100d), roi],
            "Best Plat/Trace" => [PlatPerTrace.HasValue ? 1 : 0, PlatPerTrace is null or 0 ? double.NegativeInfinity : PlatPerTrace.Value],
            "Cheapest" => [hasPrice, known.Length > 0 ? -known.Min(p => p.TotalCost) : double.NegativeInfinity],
            "Best Ducat Farming" => [DucatInfo.DucatsPerPlat.HasValue ? 1 : 0, DucatInfo.DucatsPerPlat ?? double.NegativeInfinity],
            _ => [CategoryRank(OverallCategory), profits.Max(p => p.WorstCaseProfit), profits.Max(p => p.ExpectedProfit)]
        };
    }
    public static int CategoryRank(string category) => category switch { "green" => 3, "yellow" => 2, "red" => 1, _ => 0 };
    public static IEnumerable<RelicRow> Rank(IEnumerable<RelicRow> rows, string mode, string scope) =>
        rows.OrderByDescending(r => r.RankKey(mode, scope), Comparer<double[]>.Create((a, b) =>
        {
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++) { var cmp = a[i].CompareTo(b[i]); if (cmp != 0) return cmp; }
            return a.Length.CompareTo(b.Length);
        }));
}
