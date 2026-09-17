using System.Globalization;
using System.Text.Json;

namespace RelicFrame.Core;

public sealed record ArcaneSalesActivity(DateTimeOffset CheckedAt, double? SalesPerDay, int ReportingHours);
public sealed record ArcaneLiquidCollection(string Name, double SalesPerDay, double DirectSellEv, int Known, int Total);

// Closed-order statistics are reported marketplace activity, not guaranteed trades
// or a prediction of how quickly an individual listing will sell.
public sealed class ArcaneLiquidity
{
    private readonly MarketHttp http;
    private readonly string path;
    private Dictionary<string, ArcaneSalesActivity> cache;
    public ArcaneLiquidity(MarketHttp http, string path)
    {
        this.http = http; this.path = path;
        try { cache = File.Exists(path) ? Json.Read<Dictionary<string, ArcaneSalesActivity>>(path) : new(); }
        catch (Exception error) when (error is IOException or JsonException) { cache = new(); }
    }

    public async Task<IReadOnlyDictionary<string, ArcaneSalesActivity>> RefreshAsync(ArcaneEconomyResult result, CancellationToken ct)
    {
        foreach (var quote in result.Quotes)
        {
            if (cache.TryGetValue(quote.Name, out var previous) && DateTimeOffset.UtcNow - previous.CheckedAt
                < (previous.SalesPerDay.HasValue ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(15))) continue;
            try
            {
                using var json = await http.GetJsonAsync("https://api.warframe.market/v1/items/" + Uri.EscapeDataString(quote.Slug) + "/statistics", ct, lowPriority: true);
                var rows = json.RootElement.Get("payload").Get("statistics_closed").Get("48hours");
                if (rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Missing closed-order statistics.");
                cache[quote.Name] = Parse(rows, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Console.WriteLine($"[arcane-liquidity] {quote.Slug}: {error.GetType().Name}; activity unknown");
                // Do not mislabel failed or stale statistics as zero sales.
                cache[quote.Name] = new(DateTimeOffset.UtcNow, null, 0);
            }
        }
        Json.WriteAtomic(path, cache);
        return new Dictionary<string, ArcaneSalesActivity>(cache, StringComparer.OrdinalIgnoreCase);
    }

    public static ArcaneSalesActivity Parse(JsonElement rows, DateTimeOffset now)
    {
        var hours = rows.Rows().Where(row => row.Get("mod_rank").Number() == 0)
            .Where(row => DateTimeOffset.TryParse(row.Get("datetime").Text(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                && at >= now.AddHours(-48) && at <= now)
            .GroupBy(row => row.Get("datetime").Text(), StringComparer.Ordinal).Select(group => group.First()).ToArray();
        // Use the whole 48h window, including hours with no reported sales.
        return new(now, hours.Sum(row => Math.Max(0, row.Get("volume").Number() ?? 0)) / 2, hours.Length);
    }

    public static IReadOnlyList<ArcaneLiquidCollection> Rank(ArcaneEconomyResult result, IReadOnlyDictionary<string, ArcaneSalesActivity> activity)
        => result.Packs.Select(pack =>
        {
            var members = ArcaneEconomy.CollectionMembers(pack.Name);
            var known = members.Where(member => activity.TryGetValue(member.Name, out var sales) && sales.SalesPerDay.HasValue).ToArray();
            return new ArcaneLiquidCollection(pack.Name,
                known.Sum(member => member.Chance * activity[member.Name].SalesPerDay!.Value),
                pack.DirectSellEv, known.Length, members.Count);
        }).OrderByDescending(row => row.Known == row.Total)
            .ThenByDescending(row => row.SalesPerDay).ThenByDescending(row => row.DirectSellEv)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToArray();
}
