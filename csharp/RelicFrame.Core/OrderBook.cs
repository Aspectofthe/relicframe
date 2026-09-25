using System.IO.Compression;
using System.Buffers.Binary;
using System.Text.Json;

namespace RelicFrame.Core;

public sealed record OrderEntry(double Price, double? Quantity, bool IsOutlier = false, string Seller = "", string SellerStatus = "", string SellerSlug = "", int? Rank = null, string OrderId = "");
public sealed record OrderMatch(IReadOnlyList<OrderEntry> Entries, bool SubtypeMatched)
{
    public OrderEntry? Best => Entries.MinBy(e => e.Price);
}
public sealed record PurchaseOrder(double Price, double QuantityUsed, string Seller = "");
public sealed record Purchase(double TotalCost, double UnitsFilled, bool FullyFilled, double? AvgPricePerUnit, IReadOnlyList<PurchaseOrder> OrdersUsed);

public static class OrderMath
{
    public static double? UnitPrice(JsonElement row)
    {
        var plat = row.Get("platinum").Number(); var count = row.Get("perTrade").Number();
        return count > 0 ? plat / count : plat;
    }
    public static bool Online(JsonElement row) => row.Get("user").Get("status").Text() is "online" or "ingame";
    public static List<OrderEntry> Entries(IEnumerable<JsonElement> rows) => rows.Select(r => new { P = UnitPrice(r), Q = r.Get("quantity").Number(),
            Seller = r.Get("user").Get("ingameName").Text(r.Get("user").Get("ingame_name").Text()), Status = r.Get("user").Get("status").Text(), Slug = r.Get("user").Get("slug").Text(),
            Rank = r.Get("rank").Number() is { } rank ? (int?)rank : null, Id = r.Get("id").Text() })
        .Where(r => r.P.HasValue && r.Q != 0).Select(r => new OrderEntry(r.P!.Value, r.Q, Seller: r.Seller, SellerStatus: r.Status, SellerSlug: r.Slug, Rank: r.Rank, OrderId: r.Id)).ToList();
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
        foreach (var row in rows.OrderBy(r => r.Price).ThenBy(r => r.Seller, StringComparer.OrdinalIgnoreCase))
        {
            if (filled >= n) break;
            var take = Math.Min(row.Quantity.HasValue ? Math.Max(0, row.Quantity.Value) : 1, n - filled);
            if (take <= 0) continue;
            total += take * row.Price; filled += take; used.Add(new(row.Price, take, row.Seller));
        }
        return new(total, filled, filled >= n, filled > 0 ? total / filled : null, used);
    }
    public static double? StableUnvaultedRewardPrice(IEnumerable<OrderEntry> visibleSellOrders, double? onlineFloor)
    {
        var recentMedian = RecentVisibleLowerMedian(visibleSellOrders);
        if (!recentMedian.HasValue) return onlineFloor;
        if (!onlineFloor.HasValue || !double.IsFinite(onlineFloor.Value) || onlineFloor <= 0) return Math.Round(recentMedian.Value, MidpointRounding.AwayFromZero);
        // Online supply still matters, but one remaining seller cannot pull a common current item
        // above 150% of the broader recent baseline merely because everyone else went offline.
        var blended = (onlineFloor.Value + recentMedian.Value) / 2;
        var stabilized = Math.Min(blended, recentMedian.Value * 1.5);
        return Math.Max(1, Math.Round(stabilized, MidpointRounding.AwayFromZero));
    }
    public static double? RecentVisibleLowerMedian(IEnumerable<OrderEntry> visibleSellOrders)
    {
        var rows = visibleSellOrders.Where(row => double.IsFinite(row.Price) && row.Price > 0).OrderBy(row => row.Price).ToArray();
        if (rows.Length == 0) return null;
        // Ignore a flagged extreme only when enough ordinary evidence remains. Then use the
        // median of the cheapest five recent visible asks rather than the median of an inflated tail.
        var ordinary = rows.Where(row => !row.IsOutlier).ToArray();
        if (rows.Length >= 5 && ordinary.Length >= 3) rows = ordinary;
        var lowerBook = rows.Take(5).Select(row => row.Price).Order().ToArray();
        return (lowerBook[(lowerBook.Length - 1) / 2] + lowerBook[lowerBook.Length / 2]) / 2;
    }
}

// Only compressed UTF-8 bytes survive between requests. A reader owns its decoded document.
// Atomic reference replacement permits background reconciliation while commands read old snapshots.
public sealed class OrderBook
{
    private sealed record State(byte[] Packed, DateTimeOffset? FetchedAt);
    private readonly object updateGate = new();
    private State state = new(Pack("[]"u8), null);
    public DateTimeOffset? FetchedAt => Volatile.Read(ref state).FetchedAt;
    public bool TryRestoreCache(string path, TimeSpan maximumAge)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var bytes = File.ReadAllBytes(path); if (bytes.Length <= sizeof(long)) return false;
            var fetched = DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(0, sizeof(long))));
            if (DateTimeOffset.UtcNow - fetched > maximumAge || fetched > DateTimeOffset.UtcNow.AddMinutes(5)) return false;
            var restored = new State(bytes[sizeof(long)..], fetched);
            using var input = new MemoryStream(restored.Packed, false);
            using var zipper = new ZLibStream(input, CompressionMode.Decompress);
            using var validation = JsonDocument.Parse(zipper);
            if (validation.RootElement.ValueKind != JsonValueKind.Array) return false;
            lock (updateGate) Interlocked.Exchange(ref state, restored);
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentOutOfRangeException) { return false; }
    }
    public void SaveCache(string path)
    {
        var snapshot = Volatile.Read(ref state); if (!snapshot.FetchedAt.HasValue) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var bytes = new byte[sizeof(long) + snapshot.Packed.Length];
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0, sizeof(long)), snapshot.FetchedAt.Value.ToUnixTimeMilliseconds());
        snapshot.Packed.CopyTo(bytes, sizeof(long));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
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
        lock (updateGate) Interlocked.Exchange(ref state, new(packed, DateTimeOffset.UtcNow));
    }
    public void ApplyCreated(JsonElement order)
    {
        if (order.ValueKind != JsonValueKind.Object || order.Get("id").Text().Length == 0) return;
        lock (updateGate)
        {
            var prior = Volatile.Read(ref state); using var current = Read(); using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray(); var id = order.Get("id").Text();
                foreach (var row in current.RootElement.Rows()) if (!row.Get("id").Text().Equals(id, StringComparison.Ordinal)) row.WriteTo(writer);
                order.WriteTo(writer); writer.WriteEndArray();
            }
            Interlocked.Exchange(ref state, new(Pack(buffer.ToArray()), prior.FetchedAt));
        }
    }
    public JsonDocument Read()
    {
        using var input = new MemoryStream(Volatile.Read(ref state).Packed, false);
        using var zipper = new ZLibStream(input, CompressionMode.Decompress);
        return JsonDocument.Parse(zipper);
    }
    public OrderMatch Match(string? subtype, bool onlineOnly, ISet<string>? blacklist = null, string? excludedSellerSlug = null, string? sellerStatus = null, int? rank = null, IReadOnlySet<string>? excludedOrderIds = null)
    {
        using var doc = Read();
        return OrderMath.Matching(doc.RootElement.Rows().Where(r => r.Get("type").Text() == "sell"
            && (!onlineOnly || OrderMath.Online(r))
            && (!rank.HasValue || r.Get("rank").Number() is { } orderRank && (int)orderRank == rank.Value)
            && (string.IsNullOrWhiteSpace(sellerStatus) || r.Get("user").Get("status").Text().Equals(sellerStatus, StringComparison.OrdinalIgnoreCase))
            && !(blacklist?.Contains(r.Get("user").Get("ingameName").Text().Trim().ToLowerInvariant()) ?? false)
            && (string.IsNullOrWhiteSpace(excludedSellerSlug) || !r.Get("user").Get("slug").Text().Equals(excludedSellerSlug, StringComparison.OrdinalIgnoreCase))
            && (excludedOrderIds is null || !excludedOrderIds.Contains(r.Get("id").Text()))), subtype);
    }
}
