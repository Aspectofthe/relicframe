namespace RelicFrame.Core;

public sealed record PrimeSetStock(string ItemId, string ItemName, int Quantity);
public sealed record PrimeSetListingPlan(IReadOnlyList<PrimeSetStock> Sellable,
    IReadOnlyDictionary<string, int> ControlledQuantities)
{
    public static PrimeSetListingPlan Build(IEnumerable<PrimeSetStock> inventory,
        IEnumerable<PrimeSetDefinition> definitions, IReadOnlySet<string> protectedSetNames,
        bool sellCompleteSets, bool protectIncompleteSets)
    {
        var rows = inventory.Where(row => row.Quantity > 0 && row.ItemId.Length > 0).ToArray();
        var counts = rows.GroupBy(row => row.ItemId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Quantity), StringComparer.Ordinal);
        var names = rows.GroupBy(row => row.ItemId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().ItemName, StringComparer.Ordinal);
        var sets = new List<PrimeSetStock>();
        var controlled = new Dictionary<string, int>(StringComparer.Ordinal);
        var knownSets = definitions.Where(row => row.Components.Count > 0)
            .OrderBy(row => row.SetName, StringComparer.OrdinalIgnoreCase).ToArray();
        var setIds = knownSets.Select(row => row.SetItemId).ToHashSet(StringComparer.Ordinal);

        foreach (var definition in knownSets)
        {
            var quantity = sellCompleteSets
                ? definition.Components.Min(part => counts.GetValueOrDefault(part.ItemId) / Math.Max(1, part.Quantity))
                : 0;
            // Some imports include a virtual set row alongside its components.
            // Prefer the verified component count; trust the set row only when
            // there are no component rows at all, never add the representations.
            var hasComponentRows = definition.Components.Any(part => counts.GetValueOrDefault(part.ItemId) > 0);
            var listedQuantity = sellCompleteSets
                ? hasComponentRows ? quantity : counts.GetValueOrDefault(definition.SetItemId)
                : 0;
            controlled[definition.SetItemId] = listedQuantity;
            if (quantity > 0)
            {
                foreach (var part in definition.Components)
                {
                    counts[part.ItemId] -= quantity * Math.Max(1, part.Quantity);
                    controlled[part.ItemId] = counts[part.ItemId];
                }
            }
            if (listedQuantity > 0) sets.Add(new(definition.SetItemId, definition.SetName, listedQuantity));
        }

        if (protectIncompleteSets)
        {
            foreach (var definition in knownSets.Where(row => protectedSetNames.Contains(row.SetName)))
            {
                // "Close to completion" means exactly one required component type
                // is still missing after complete sets have been allocated. Do not
                // lock parts inside sets that are missing two or more component types.
                var missingTypes = definition.Components.Count(part =>
                    counts.GetValueOrDefault(part.ItemId) < Math.Max(1, part.Quantity));
                var ownedUnits = definition.Components.Sum(part =>
                    Math.Min(counts.GetValueOrDefault(part.ItemId), Math.Max(1, part.Quantity)));
                if (missingTypes != 1 || ownedUnits == 0) continue;

                // Reserve one near-complete set's owned components, not all copies.
                foreach (var part in definition.Components)
                {
                    var owned = counts.GetValueOrDefault(part.ItemId);
                    var reserve = Math.Min(owned, Math.Max(1, part.Quantity));
                    if (reserve == 0) continue;
                    counts[part.ItemId] = owned - reserve;
                    controlled[part.ItemId] = counts[part.ItemId];
                }
            }
        }

        var sellableParts = rows.GroupBy(row => row.ItemId, StringComparer.Ordinal)
            .Where(group => !setIds.Contains(group.Key))
            .Select(group => new PrimeSetStock(group.Key, names[group.Key], counts.GetValueOrDefault(group.Key)))
            .Where(row => row.Quantity > 0);
        return new(sellableParts.Concat(sets).ToArray(), controlled);
    }
}
