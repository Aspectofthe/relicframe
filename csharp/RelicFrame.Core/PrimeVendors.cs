using System.Globalization;
using System.Text.Json;

namespace RelicFrame.Core;

public sealed record VendorOffer(string Name, int Cost, int Credits = 0);
public sealed record VendorRotation(DateTimeOffset StartsAt, DateTimeOffset EndsAt, IReadOnlyList<VendorOffer> Offers);
public sealed record PrimeVendorSnapshot(DateTimeOffset SourceTime, VendorRotation? Aya, VendorRotation? Baro,
    IReadOnlyList<string> ReturningPrimes, DateTimeOffset? NextBaroAt);
public sealed record HistoricalSaleSummary(double? MedianR0, double SalesPerDay, int ReportingDays);

public static class PrimeVendors
{
    public static string NormalizeGameRef(string gameRef)
    {
        var normalized = gameRef.Replace("/Lotus/StoreItems/", "/Lotus/", StringComparison.OrdinalIgnoreCase);
        if (normalized.Contains("/Projections/", StringComparison.OrdinalIgnoreCase) &&
            normalized.EndsWith("Bronze", StringComparison.OrdinalIgnoreCase)) normalized = normalized[..^6];
        return normalized;
    }

    private static DateTimeOffset? Date(JsonElement value)
    {
        if (value.Get("$date").Get("$numberLong").Number() is { } ms && ms > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
        return WorldRender.Time(value);
    }

    private static VendorRotation? Rotation(JsonElement rows, DateTimeOffset now, Func<string, string?> nameForGameRef,
        bool aya, out IReadOnlyList<string> returning)
    {
        returning = [];
        var active = rows.Rows().Where(row => Date(row.Get("Activation")) is { } start && start <= now &&
            Date(row.Get("Expiry")) is { } end && now < end).OrderByDescending(row => Date(row.Get("Activation"))).FirstOrDefault();
        if (active.ValueKind != JsonValueKind.Object) return null;
        var offered = new List<VendorOffer>(); var primes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in active.Get("Manifest").Rows())
        {
            var rawRef = entry.Get("ItemType").Text();
            var name = nameForGameRef(NormalizeGameRef(rawRef));
            if (name is null) continue;
            if (aya)
            {
                if (rawRef.Contains("/Projections/", StringComparison.OrdinalIgnoreCase)
                    && entry.Get("RegularPrice").Number() is { } ayaCost && ayaCost >= 1 && ayaCost <= 10)
                    offered.Add(new(name, (int)ayaCost));
                else if (entry.Get("PrimePrice").Number() is > 0 &&
                    name.Contains(" Prime", StringComparison.OrdinalIgnoreCase)) primes.Add(name);
            }
            else if (entry.Get("PrimePrice").Number() is { } ducats && ducats > 0 &&
                entry.Get("RegularPrice").Number() is { } credits && credits >= 0)
                offered.Add(new(name, (int)ducats, (int)credits));
        }
        returning = primes.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(Date(active.Get("Activation"))!.Value, Date(active.Get("Expiry"))!.Value,
            offered.DistinctBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static PrimeVendorSnapshot Parse(JsonElement official, Func<string, string?> nameForGameRef, DateTimeOffset now)
    {
        var source = WorldRender.Time(official.Get("Time")) ?? throw new InvalidDataException("Official world state has no timestamp.");
        if (source > now.AddMinutes(5) || now - source > TimeSpan.FromMinutes(20))
            throw new InvalidDataException("Official world state is stale.");
        var aya = Rotation(official.Get("PrimeVaultTraders"), now, nameForGameRef, true, out var returning);
        var baro = Rotation(official.Get("VoidTraders"), now, nameForGameRef, false, out _);
        var nextBaro = official.Get("VoidTraders").Rows().Select(row => Date(row.Get("Activation")))
            .OfType<DateTimeOffset>().Where(at => at > now).Order().FirstOrDefault();
        return new(source, aya, baro, returning, nextBaro == default ? null : nextBaro);
    }

    // WFM closed-order daily statistics are reported activity, not a guarantee of
    // execution. R0 filtering is crucial: a maxed Primed mod costs Endo/Credits.
    public static HistoricalSaleSummary Sales(JsonElement rows, DateTimeOffset now, bool ranked)
        => SalesBetween(rows, now.AddDays(-30), now, ranked);

    public static HistoricalSaleSummary SalesBetween(JsonElement rows, DateTimeOffset from, DateTimeOffset through, bool ranked)
    {
        var daily = rows.Rows().Where(row => !ranked || row.Get("mod_rank").Number() == 0)
            .Select(row => new { At = DateTimeOffset.TryParse(row.Get("datetime").Text(), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var at) ? at : DateTimeOffset.MinValue,
                Volume = Math.Max(0, row.Get("volume").Number() ?? 0), Price = row.Get("median").Number() })
            .Where(row => row.At >= from && row.At <= through && row.Volume > 0 && row.Price is > 0)
            .ToArray();
        if (daily.Length == 0) return new(null, 0, 0);
        var ordered = daily.OrderBy(row => row.Price).ToArray();
        var midpoint = daily.Sum(row => row.Volume) / 2;
        double cumulative = 0; double median = ordered[^1].Price!.Value;
        foreach (var row in ordered) { cumulative += row.Volume; if (cumulative >= midpoint) { median = row.Price!.Value; break; } }
        return new(median, daily.Sum(row => row.Volume) / Math.Max(1, (through - from).TotalDays),
            daily.Select(row => row.At.UtcDateTime.Date).Distinct().Count());
    }
}
