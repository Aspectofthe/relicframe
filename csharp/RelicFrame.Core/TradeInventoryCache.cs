namespace RelicFrame.Core;

public sealed record ObservedInventoryDecrease(int Quantity, DateTimeOffset ObservedAt);

public static class TradeInventoryCache
{
    private static readonly TimeSpan MaximumTradeFeedLag = TimeSpan.FromHours(2);

    // A first connection may happen while AlecaFrame's inventory file is older
    // than a completed trade. Only those newer trades need a local deduction.
    public static DateTimeOffset FirstTradeWatermark(DateTimeOffset inventoryUpdatedAt, DateTimeOffset now)
        => inventoryUpdatedAt < now - MaximumTradeFeedLag ? now - MaximumTradeFeedLag
            : inventoryUpdatedAt > now ? now : inventoryUpdatedAt;

    public static void ForgetOldDecreases(IDictionary<string, ObservedInventoryDecrease> decreases, DateTimeOffset now)
    {
        foreach (var entry in decreases.Where(entry => now - entry.Value.ObservedAt > MaximumTradeFeedLag).ToArray())
            decreases.Remove(entry.Key);
    }

    // The inventory file may update before AlecaFrame's public trade feed. In
    // that order, subtracting the trade again would hide stock still owned.
    public static int QuantityStillInCache(string itemId, int soldQuantity, DateTimeOffset tradedAt,
        IDictionary<string, ObservedInventoryDecrease> decreases, IReadOnlyList<PrimeSetDefinition> definitions)
    {
        if (soldQuantity <= 0) return 0;
        var set = definitions.FirstOrDefault(definition => definition.SetItemId == itemId);
        var components = set is null
            ? new[] { (ItemId: itemId, Required: 1) }
            : set.Components.Select(component => (component.ItemId, Required: Math.Max(1, component.Quantity))).ToArray();
        if (components.Length == 0) return soldQuantity;
        var reflected = soldQuantity;
        foreach (var component in components)
        {
            if (!decreases.TryGetValue(component.ItemId, out var observed) ||
                observed.ObservedAt < tradedAt - TimeSpan.FromMinutes(2) ||
                observed.ObservedAt - tradedAt > MaximumTradeFeedLag)
                return soldQuantity;
            reflected = Math.Min(reflected, observed.Quantity / component.Required);
        }
        foreach (var component in components)
        {
            var observed = decreases[component.ItemId];
            var left = observed.Quantity - reflected * component.Required;
            if (left == 0) decreases.Remove(component.ItemId);
            else decreases[component.ItemId] = observed with { Quantity = left };
        }
        return soldQuantity - reflected;
    }
}
