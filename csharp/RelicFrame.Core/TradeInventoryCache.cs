namespace RelicFrame.Core;

public sealed record ObservedInventoryDecrease(int Quantity, DateTimeOffset ObservedAt);
public sealed record MissingManagedOrderHold(string OrderId, int Quantity, DateTimeOffset ObservedAt);

public static class TradeInventoryCache
{
    private static readonly TimeSpan MaximumTradeFeedLag = TimeSpan.FromHours(2);

    // A vanished market order is not a confirmed sale, but it must not be
    // recreated from an unchanged, potentially stale inventory snapshot.
    // Keep that uncertainty separate from confirmed-sale deductions.
    public static Dictionary<string, int> SuppressedStock(
        IReadOnlyDictionary<string, int> confirmedSales,
        IReadOnlyDictionary<string, MissingManagedOrderHold> missingOrders,
        IReadOnlyList<PrimeSetDefinition> definitions)
    {
        var suppressed = new Dictionary<string, int>(confirmedSales, StringComparer.Ordinal);
        foreach (var held in missingOrders)
        {
            var set = definitions.FirstOrDefault(definition => definition.SetItemId == held.Key);
            if (set is null) Add(held.Key, held.Value.Quantity);
            else foreach (var component in set.Components)
                Add(component.ItemId, checked(held.Value.Quantity * component.Quantity));
        }
        return suppressed;

        void Add(string itemId, int quantity)
        {
            if (quantity <= 0) return;
            suppressed[itemId] = checked(suppressed.GetValueOrDefault(itemId) + quantity);
        }
    }

    public static int ConsumeMissingOrderHold(IDictionary<string, MissingManagedOrderHold> missingOrders,
        string itemId, int confirmedQuantity)
    {
        if (confirmedQuantity <= 0 || !missingOrders.TryGetValue(itemId, out var hold)) return 0;
        // Once the disappearance is explained by a real trade, the confirmed
        // sale deduction replaces the whole conservative hold. Remaining raw
        // copies are then eligible again instead of being hidden twice.
        missingOrders.Remove(itemId);
        return hold.Quantity;
    }

    public static int AdditionalConfirmedSaleDeduction(int stillInInventory, int notAlreadySuppressedByOrder)
        => Math.Min(Math.Max(0, stillInInventory), Math.Max(0, notAlreadySuppressedByOrder));

    // The game log and AlecaFrame may report the same completed trade at
    // different times. Match only the same item near the actual trade time.
    public static int ConsumeMatchingTradeCredit(IDictionary<string, ObservedInventoryDecrease> credits,
        string itemId, int quantity, DateTimeOffset tradeTime)
    {
        if (quantity <= 0 || !credits.TryGetValue(itemId, out var credit) ||
            Math.Abs((credit.ObservedAt - tradeTime).TotalMinutes) > 5) return 0;
        var matched = Math.Min(quantity, Math.Max(0, credit.Quantity));
        if (matched >= credit.Quantity) credits.Remove(itemId);
        else credits[itemId] = credit with { Quantity = credit.Quantity - matched };
        return matched;
    }

    public static void RememberTradeCredit(IDictionary<string, ObservedInventoryDecrease> credits,
        string itemId, int quantity, DateTimeOffset tradeTime)
    {
        if (quantity <= 0) return;
        credits.TryGetValue(itemId, out var prior);
        var total = prior is not null && Math.Abs((prior.ObservedAt - tradeTime).TotalMinutes) <= 5
            ? prior.Quantity + quantity : quantity;
        credits[itemId] = new(total, tradeTime);
    }

    public static bool FreshSnapshotResolvesHold(MissingManagedOrderHold hold,
        int remainingStock, DateTimeOffset snapshotUpdatedAt)
        => snapshotUpdatedAt > hold.ObservedAt && remainingStock < hold.Quantity;

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
