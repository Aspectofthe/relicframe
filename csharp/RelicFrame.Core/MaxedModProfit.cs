namespace RelicFrame.Core;

public sealed record ModUpgradeCost(int Endo, int Credits);
public sealed record ModProfitRow(string Name, double BuyR0, double SellMax, double SalesPerDay,
    ModUpgradeCost Cost, double Uplift, double? NetProfit, DateTimeOffset CheckedAt)
{
    public double ReturnPer1000Endo => (NetProfit ?? Uplift) / Cost.Endo * 1000;
    public double LiquidityWeightedReturn => (NetProfit ?? Uplift) * Math.Min(1, SalesPerDay / 5);
}

public static class MaxedModProfit
{
    // Fusion doubles each rank. R0 -> R10 legendary: 40,920 Endo, 1,976,436 Credits.
    // https://wiki.warframe.com/w/Fusion
    public static ModUpgradeCost? UpgradeCost(IReadOnlyList<string> tags, int rank)
    {
        if (rank is < 1 or > 10) return null;
        var rarities = new[] { "common", "uncommon", "rare", "legendary" };
        var matches = rarities.Select((name, index) => (name, multiplier: index + 1))
            .Where(item => tags.Contains(item.name, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) return null;
        var steps = (1 << rank) - 1;
        return new(10 * matches[0].multiplier * steps, 483 * matches[0].multiplier * steps);
    }

    public static ModProfitRow? Evaluate(string name, double? buyR0, double? maxAsk,
        HistoricalSaleSummary maxSales, ModUpgradeCost cost, DateTimeOffset checkedAt,
        double? platinumPer1000Endo, double? platinumPer100kCredits)
    {
        if (buyR0 is not > 0 || maxAsk is not > 0 || maxSales.MedianR0 is not > 0 ||
            maxSales.ReportingDays < 3 || maxSales.SalesPerDay < 1 || cost.Endo <= 0 ||
            !double.IsFinite(buyR0.Value) || !double.IsFinite(maxAsk.Value) || !double.IsFinite(maxSales.MedianR0.Value)) return null;
        var sell = Math.Min(maxAsk.Value, maxSales.MedianR0.Value);
        var uplift = sell - buyR0.Value;
        double? net = platinumPer1000Endo is { } endo && platinumPer100kCredits is { } credits &&
            double.IsFinite(endo) && double.IsFinite(credits) && endo >= 0 && credits >= 0
            ? uplift - cost.Endo / 1000d * endo - cost.Credits / 100000d * credits : null;
        return new(name, buyR0.Value, sell, maxSales.SalesPerDay, cost, uplift, net, checkedAt);
    }

    public static IReadOnlyList<ModProfitRow> Sort(IEnumerable<ModProfitRow> rows, string mode)
        => rows.OrderByDescending(row => mode switch
        {
            "endo" => row.ReturnPer1000Endo,
            "sales" => row.SalesPerDay,
            _ => row.LiquidityWeightedReturn
        }).ThenByDescending(row => row.NetProfit ?? row.Uplift)
          .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToArray();
}
