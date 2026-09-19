using System.Globalization;
using System.Text.Json;

namespace RelicFrame.Core;

public sealed record VendorOffer(string Name, int Cost, int Credits = 0);
public sealed record VendorRotation(DateTimeOffset StartsAt, DateTimeOffset EndsAt, IReadOnlyList<VendorOffer> Offers);
public sealed record UpcomingResurgence(DateTimeOffset StartsAt, DateTimeOffset EndsAt, string FeaturedPackage);
public sealed record PrimeVendorSnapshot(DateTimeOffset SourceTime, VendorRotation? Aya, VendorRotation? Baro,
    IReadOnlyList<string> ReturningPrimes, DateTimeOffset? NextBaroAt, UpcomingResurgence? NextAya);
public sealed record HistoricalSaleSummary(double? MedianR0, double SalesPerDay, int ReportingDays);
public sealed record AyaValueRow(string RelicName, int AyaCost, double? DirectSellAsk, double? PrimePartEv,
    string? BestPrimePart, double? BestPrimePartAsk, double? RareChance);

public static class AyaProfit
{
    public const string Overall = "overall";
    public const string PrimeParts = "prime-parts";
    public const string RelicSale = "relic-sale";

    public static IReadOnlyList<AyaValueRow> Sort(IEnumerable<AyaValueRow> rows, string mode)
    {
        static double? Basis(AyaValueRow row, string selected) => selected switch
        {
            PrimeParts => row.PrimePartEv,
            RelicSale => row.DirectSellAsk,
            _ => row.PrimePartEv.HasValue || row.DirectSellAsk.HasValue
                ? Math.Max(row.PrimePartEv ?? 0, row.DirectSellAsk ?? 0) : null
        };
        return rows.OrderByDescending(row => Basis(row, mode).HasValue)
            .ThenByDescending(row => Basis(row, mode) / Math.Max(1, row.AyaCost))
            .ThenBy(row => row.RelicName, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

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
        UpcomingResurgence? nextAya = null;
        if (aya is not null)
        {
            var schedule = official.Get("PrimeVaultTraders").Rows()
                .Where(row => Date(row.Get("Activation")) == aya.StartsAt)
                .SelectMany(row => row.Get("ScheduleInfo").Rows())
                .Where(row => Date(row.Get("Expiry")) is { } expiry && expiry > aya.EndsAt)
                .OrderBy(row => Date(row.Get("Expiry"))).FirstOrDefault();
            if (schedule.ValueKind == JsonValueKind.Object && Date(schedule.Get("Expiry")) is { } end &&
                Date(schedule.Get("PreviewHiddenUntil")) is { } reveal && reveal <= source &&
                schedule.Get("FeaturedItem").Text() is { Length: > 0 } featured)
                nextAya = new(aya.EndsAt, end, featured);
        }
        return new(source, aya, baro, returning, nextBaro == default ? null : nextBaro, nextAya);
    }

    public static IReadOnlyList<string> FeaturedPrimeSets(string package, IReadOnlyList<string> knownPrimeSetNames)
    {
        var key = package.Split('/').LastOrDefault() ?? "";
        if (!key.StartsWith("MPV", StringComparison.Ordinal) || !key.EndsWith("Pack", StringComparison.Ordinal)) return [];
        var dual = key.EndsWith("PrimeDualPack", StringComparison.Ordinal);
        var single = key.EndsWith("PrimeSinglePack", StringComparison.Ordinal);
        if (!dual && !single) return [];
        var payload = key[3..^(dual ? "PrimeDualPack".Length : "PrimeSinglePack".Length)];
        var names = knownPrimeSetNames.Where(name => name.EndsWith(" Prime Set", StringComparison.OrdinalIgnoreCase))
            .Select(name => (Name: name, Token: name[..^" Prime Set".Length].Replace(" ", "", StringComparison.Ordinal)))
            .ToArray();
        if (single) return names.Where(item => item.Token.Equals(payload, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray() is { Length: 1 } one ? one : [];
        var matches = (from left in names from right in names
            where (left.Token + right.Token).Equals(payload, StringComparison.OrdinalIgnoreCase)
            select new[] { left.Name, right.Name }).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : [];
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
