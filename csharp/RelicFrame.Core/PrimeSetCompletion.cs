using System.Text.Json;

namespace RelicFrame.Core;

public sealed record PrimeSetComponent(string ItemId, string ItemName, int Quantity);
public sealed record PrimeSetDefinition(string SetItemId, string SetName, IReadOnlyList<PrimeSetComponent> Components);
public sealed record PrimeSetOpportunity(string SetName, string MissingItem, int MissingQuantity,
    int MissingCost, int SetValue, int CompletionProfit, int OwnedComponentUnits, int RequiredComponentUnits);
public sealed record PrimeCompletionRelic(string Name, Refinement Refinement, double Price, double DropChance);

public sealed class PrimeSetCompletion
{
    public static PrimeCompletionRelic? CheapestSourceRelic(IEnumerable<Relic> relics, string rewardName,
        Func<string, Refinement, OrderMatch> asks)
    {
        var choices = new List<PrimeCompletionRelic>();
        foreach (var relic in relics)
        {
            var reward = relic.Rewards.FirstOrDefault(row => row.RewardName.Equals(rewardName, StringComparison.OrdinalIgnoreCase));
            if (reward is null) continue;
            foreach (var tier in Enum.GetValues<Refinement>())
            {
                var match = asks(relic.RelicName, tier);
                // Never use the order matcher’s all-refinement fallback as an exact quote.
                if (!match.SubtypeMatched) continue;
                var best = match.Entries.Where(row => double.IsFinite(row.Price) && row.Price > 0 && row.Quantity != 0).MinBy(row => row.Price);
                if (best is not null) choices.Add(new(relic.RelicName, tier, best.Price, Relic.Chance(tier, reward.Rarity)));
            }
        }
        return choices.OrderBy(row => row.Price).ThenByDescending(row => row.DropChance)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Refinement).FirstOrDefault();
    }
    private sealed record CacheState
    {
        public Dictionary<string, PrimeSetDefinition> Sets { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Quantities { get; init; } = new(StringComparer.Ordinal);
    }

    private readonly LiveMarket market;
    private readonly MarketHttp http;
    private readonly string cachePath;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CacheState cache;

    public PrimeSetCompletion(LiveMarket market, MarketHttp http, string cachePath)
    {
        this.market = market; this.http = http; this.cachePath = cachePath;
        try { cache = File.Exists(cachePath) ? Json.Read<CacheState>(cachePath) : new(); }
        catch (Exception error) when (error is IOException or JsonException) { cache = new(); }
    }

    public async Task<IReadOnlyList<PrimeSetOpportunity>> AnalyzeAsync(IEnumerable<PrimeInventoryEntry> inventory,
        string? excludedSellerSlug, IProgress<(int Current, int Total)>? progress, CancellationToken ct, bool requireComplete = false)
    {
        var owned = inventory.Select(row => new
            {
                Name = !string.IsNullOrWhiteSpace(row.ItemName) ? row.ItemName.Trim() : market.NameForGameRef(row.GameRef),
                row.Quantity
            })
            .Where(row => !string.IsNullOrWhiteSpace(row.Name) && row.Quantity > 0)
            .GroupBy(row => row.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Quantity), StringComparer.OrdinalIgnoreCase);
        var candidates = market.PrimeSetNames.Where(setName =>
        {
            var family = setName.EndsWith(" Set", StringComparison.OrdinalIgnoreCase) ? setName[..^4] : setName;
            return owned.Keys.Any(itemName => itemName.Equals(family, StringComparison.OrdinalIgnoreCase)
                || itemName.StartsWith(family + " ", StringComparison.OrdinalIgnoreCase));
        }).ToArray();
        var definitions = new List<PrimeSetDefinition>();
        for (var index = 0; index < candidates.Length; index++)
        {
            ct.ThrowIfCancellationRequested(); progress?.Report((index, candidates.Length));
            try
            {
                var definition = await DefinitionAsync(candidates[index], ct);
                if (definition is not null) definitions.Add(definition);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { if (requireComplete) throw; Console.WriteLine($"[prime-set-metadata] {candidates[index]}: {error.GetType().Name}; remaining families continue"); }
        }
        progress?.Report((candidates.Length, candidates.Length));

        var oneAway = definitions.Select(definition =>
        {
            var missing = definition.Components.Select(component => new
                {
                    Component = component,
                    Missing = Math.Max(0, component.Quantity - owned.GetValueOrDefault(component.ItemName))
                }).Where(row => row.Missing > 0).ToArray();
            var ownedUnits = definition.Components.Sum(component => Math.Min(component.Quantity, owned.GetValueOrDefault(component.ItemName)));
            return new { Definition = definition, Missing = missing, OwnedUnits = ownedUnits };
        }).Where(row => row.Missing.Length == 1 && row.OwnedUnits > 0).ToArray();

        var opportunities = new List<PrimeSetOpportunity>();
        foreach (var row in oneAway)
        {
            ct.ThrowIfCancellationRequested();
            var missing = row.Missing[0];
            if (!await market.EnsureBookAsync(row.Definition.SetName, ct)
                || !await market.EnsureBookAsync(missing.Component.ItemName, ct))
            {
                if (requireComplete) throw new InvalidDataException("Completion prices unavailable; top-10 protection cannot be determined safely.");
                continue;
            }
            var setPrice = market.RewardEstimate(row.Definition.SetName, excludedSellerSlug).Price;
            var asks = market.MatchPersonalMarket(missing.Component.ItemName, excludedSellerSlug).Entries.Where(entry => !entry.IsOutlier).ToArray();
            if (asks.Length == 0) asks = market.MatchPersonalMarket(missing.Component.ItemName, excludedSellerSlug).Entries.ToArray();
            var purchase = OrderMath.Cheapest(asks, missing.Missing);
            if (!setPrice.HasValue || !purchase.FullyFilled) continue;
            var setValue = Math.Max(1, (int)Math.Floor(setPrice.Value));
            var missingCost = Math.Max(1, (int)Math.Ceiling(purchase.TotalCost));
            opportunities.Add(new(row.Definition.SetName, missing.Component.ItemName, missing.Missing,
                missingCost, setValue, setValue - missingCost, row.OwnedUnits, row.Definition.Components.Sum(component => component.Quantity)));
        }
        return opportunities.OrderByDescending(row => row.CompletionProfit).ThenByDescending(row => row.SetValue)
            .ThenBy(row => row.SetName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<PrimeSetDefinition?> DefinitionAsync(string setName, CancellationToken ct)
    {
        var setId = market.ItemIdForName(setName); var slug = setId is null ? null : market.SlugForItemId(setId);
        if (setId is null || slug is null) return null;
        await gate.WaitAsync(ct);
        try
        {
            if (cache.Sets.TryGetValue(setId, out var existing)) return existing;
            using var root = await http.GetJsonAsync("https://api.warframe.market/v2/items/" + Uri.EscapeDataString(slug), ct, lowPriority: true);
            var partIds = root.RootElement.Get("data").Get("setParts").Rows().Select(row => row.Text())
                .Where(id => id.Length > 0 && id != setId).Distinct(StringComparer.Ordinal).ToArray();
            var components = new List<PrimeSetComponent>();
            foreach (var partId in partIds)
            {
                var item = market.CatalogItem(partId); if (item is null || item.Tags.Contains("set", StringComparer.OrdinalIgnoreCase)) continue;
                if (!cache.Quantities.TryGetValue(partId, out var quantity))
                {
                    using var part = await http.GetJsonAsync("https://api.warframe.market/v2/items/" + Uri.EscapeDataString(item.Slug), ct, lowPriority: true);
                    quantity = Math.Max(1, (int)(part.RootElement.Get("data").Get("quantityInSet").Number() ?? 1));
                    cache.Quantities[partId] = quantity;
                }
                components.Add(new(partId, item.Name, quantity));
            }
            if (components.Count == 0) return null;
            var definition = new PrimeSetDefinition(setId, setName, components);
            cache.Sets[setId] = definition; Json.WriteAtomic(cachePath, cache);
            return definition;
        }
        finally { gate.Release(); }
    }
}
