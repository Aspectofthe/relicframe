using System.Text.Json;

namespace RelicFrame.Core;

public sealed record SyndicateOffer(string Syndicate, MarketCatalogItem Item, int Standing, string RequiredRank);
public sealed record SyndicateProfitRow(SyndicateOffer Offer, double Price, double SalesPerDay, DateTimeOffset CheckedAt)
{
    public double Per25000Standing => Price / Offer.Standing * 25000;
}
public static class SyndicateProfit
{
    public static IReadOnlyList<string> Names { get; } = ["Steel Meridian", "Arbiters of Hexis", "Cephalon Suda", "The Perrin Sequence", "Red Veil", "New Loka"];

    public static IReadOnlyList<SyndicateOffer> Parse(JsonElement data, Func<string, MarketCatalogItem?> resolve)
    {
        var result = new List<SyndicateOffer>();
        foreach (var syndicate in Names)
        foreach (var row in data.Get("syndicates").Get(syndicate).Rows())
        {
            if (row.Get("standing").Number() is not { } standing || standing < 1 || standing > 1000000 || standing != Math.Floor(standing)) continue;
            var name = row.Get("item").Text().Trim();
            var item = resolve(name);
            // Wiki vendor rows append the target weapon/frame to augment names.
            // Strip that suffix only if the resulting exact catalog match is a mod.
            if (item is null && name.EndsWith(')') && name.LastIndexOf(" (", StringComparison.Ordinal) is var suffix && suffix > 0)
            {
                var mod = resolve(name[..suffix]);
                if (mod?.Tags.Contains("mod", StringComparer.OrdinalIgnoreCase) == true) item = mod;
            }
            if (item is null || !item.Tags.Any(tag => tag.Equals("mod", StringComparison.OrdinalIgnoreCase) ||
                tag.Equals("weapon", StringComparison.OrdinalIgnoreCase) || tag.Equals("component", StringComparison.OrdinalIgnoreCase))) continue;
            var place = row.Get("place").Text();
            var rank = place.StartsWith(syndicate + ",", StringComparison.OrdinalIgnoreCase) ? place[(syndicate.Length + 1)..].Trim() : "verify in vendor";
            result.Add(new(syndicate, item, (int)standing, rank));
        }
        return result.OrderBy(row => row.Standing).DistinctBy(row => (row.Syndicate, row.Item.Id)).ToArray();
    }

    public static SyndicateProfitRow? Evaluate(SyndicateOffer offer, double? ask, HistoricalSaleSummary sales, DateTimeOffset at)
        => ask is > 0 && double.IsFinite(ask.Value) && sales.MedianR0 is > 0 && double.IsFinite(sales.MedianR0.Value) &&
           sales.ReportingDays >= 3 && sales.SalesPerDay >= 1 && offer.Standing > 0
            ? new(offer, Math.Min(ask.Value, sales.MedianR0.Value), sales.SalesPerDay, at) : null;

    public static IReadOnlyList<SyndicateProfitRow> Sort(IEnumerable<SyndicateProfitRow> rows, string mode)
        => rows.OrderByDescending(row => mode switch
        {
            "value" => row.Per25000Standing,
            "sales" => row.SalesPerDay,
            _ => row.Per25000Standing * Math.Min(1, row.SalesPerDay / 5)
        }).ThenByDescending(row => row.Per25000Standing).ThenBy(row => row.Offer.Item.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public static (int Quantity, int Remaining, double Value) Convert(SyndicateProfitRow row, int standing)
    {
        var budget = Math.Max(0, standing);
        var quantity = budget / row.Offer.Standing;
        return (quantity, budget % row.Offer.Standing, quantity * row.Price);
    }
}
