namespace RelicFrame.Core;

public sealed record RelicPanelOptions(string Refinement = "Radiant", string Sort = "Best Overall",
    string Scope = "Both (best of either)", string Vault = "All", double? MinRoi = null,
    double? MaxCost = null, double? MinReward = null, bool GuaranteedOnly = false);

public static class RelicPanel
{
    public static RelicRow[] Select(IReadOnlyDictionary<string, Relic> relics, MarketSnapshot snapshot, RelicPanelOptions options, int limit = 16)
    {
        if (!Enum.TryParse<Refinement>(options.Refinement, true, out var tier)) tier = Refinement.Radiant;
        var rows = relics.Values.Select(r => RelicRow.Compute(r, tier, snapshot.Prices,
            snapshot.RelicPrices.GetValueOrDefault(r.RelicName) ?? new(), snapshot.Ducats, scope: options.Scope))
            .Where(r => r.Passes(options.Scope, options.Vault, options.MinRoi, options.MaxCost, options.MinReward, options.GuaranteedOnly));
        return RelicRow.Rank(rows, options.Sort, options.Scope).Take(Math.Clamp(limit, 1, 40)).ToArray();
    }
}
