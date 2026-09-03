using System.IO.Compression;
using System.Text.Json;

namespace RelicFrame.Core;

public sealed record OrderEntry(double Price, double? Quantity, bool IsOutlier = false);
public sealed record OrderMatch(IReadOnlyList<OrderEntry> Entries, bool SubtypeMatched)
{
    public OrderEntry? Best => Entries.MinBy(e => e.Price);
}
public sealed record PurchaseOrder(double Price, double QuantityUsed);
public sealed record Purchase(double TotalCost, double UnitsFilled, bool FullyFilled, double? AvgPricePerUnit, IReadOnlyList<PurchaseOrder> OrdersUsed);

public static class OrderMath
{
    public static double? UnitPrice(JsonElement row)
    {
        var plat = row.Get("platinum").Number(); var count = row.Get("perTrade").Number();
        return count > 0 ? plat / count : plat;
    }
    public static bool Online(JsonElement row) => row.Get("user").Get("status").Text() is "online" or "ingame";
    public static List<OrderEntry> Entries(IEnumerable<JsonElement> rows) => rows.Select(r => new { P = UnitPrice(r), Q = r.Get("quantity").Number() })
        .Where(r => r.P.HasValue && r.Q != 0).Select(r => new OrderEntry(r.P!.Value, r.Q)).ToList();
    public static IReadOnlyList<OrderEntry> Outliers(IReadOnlyList<OrderEntry> rows)
    {
        if (rows.Count == 0) return [];
        var sorted = rows.Select(r => r.Price).Order().ToArray();
        var median = (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
        return rows.Select(r => r with { IsOutlier = rows.Count == 1 || r.Price > median * 3 || r.Price < median / 3 }).ToArray();
    }
    public static OrderMatch Matching(IEnumerable<JsonElement> rows, string? subtype)
    {
        var all = rows.ToArray();
        if (!string.IsNullOrEmpty(subtype))
        {
            var exact = Entries(all.Where(r => string.Equals(r.Get("subtype").Text(), subtype, StringComparison.OrdinalIgnoreCase)));
            if (exact.Count > 0) return new(Outliers(exact), true);
        }
        var fallback = Entries(all);
        return new(Outliers(fallback), fallback.Count > 0 && string.IsNullOrEmpty(subtype));
    }
    public static Purchase Cheapest(IEnumerable<OrderEntry> rows, int n)
    {
        double total = 0, filled = 0; var used = new List<PurchaseOrder>();
        foreach (var row in rows.OrderBy(r => r.Price))
        {
            if (filled >= n) break;
            var take = Math.Min(row.Quantity.HasValue ? Math.Max(0, row.Quantity.Value) : 1, n - filled);
            if (take <= 0) continue;
            total += take * row.Price; filled += take; used.Add(new(row.Price, take));
        }
        return new(total, filled, filled >= n, filled > 0 ? total / filled : null, used);
    }
}

// Only compressed UTF-8 bytes survive between requests. A reader owns its decoded document.
// Atomic reference replacement permits background reconciliation while commands read old snapshots.
public sealed class OrderBook
{
    private sealed record State(byte[] Packed, DateTimeOffset? FetchedAt);
    private State state = new(Pack("[]"u8), null);
    public DateTimeOffset? FetchedAt => Volatile.Read(ref state).FetchedAt;
    private static byte[] Pack(ReadOnlySpan<byte> bytes)
    {
        using var output = new MemoryStream();
        using (var zipper = new ZLibStream(output, CompressionLevel.Fastest, true)) zipper.Write(bytes);
        return output.ToArray();
    }
    public void Replace(JsonElement rows)
    {
        if (rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Order book must be an array");
        var packed = Pack(JsonSerializer.SerializeToUtf8Bytes(rows));
        Interlocked.Exchange(ref state, new(packed, DateTimeOffset.UtcNow));
    }
    public void ApplyCreated(JsonElement order)
    {
        if (order.ValueKind != JsonValueKind.Object || order.Get("id").Text().Length == 0) return;
        var prior = Volatile.Read(ref state); using var current = Read(); using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray(); var id = order.Get("id").Text();
            foreach (var row in current.RootElement.Rows()) if (!row.Get("id").Text().Equals(id, StringComparison.Ordinal)) row.WriteTo(writer);
            order.WriteTo(writer); writer.WriteEndArray();
        }
        Interlocked.Exchange(ref state, new(Pack(buffer.ToArray()), prior.FetchedAt));
    }
    public JsonDocument Read()
    {
        using var input = new MemoryStream(Volatile.Read(ref state).Packed, false);
        using var zipper = new ZLibStream(input, CompressionMode.Decompress);
        return JsonDocument.Parse(zipper);
    }
    public OrderMatch Match(string? subtype, bool onlineOnly, ISet<string>? blacklist = null)
    {
        using var doc = Read();
        return OrderMath.Matching(doc.RootElement.Rows().Where(r => r.Get("type").Text() == "sell"
            && (!onlineOnly || OrderMath.Online(r))
            && !(blacklist?.Contains(r.Get("user").Get("ingameName").Text().Trim().ToLowerInvariant()) ?? false)), subtype);
    }
}
