using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RelicFrame.Core;

public sealed record AlecaTradeItem(string Name, string DisplayName, int Quantity, int Rank);
public sealed record AlecaCompletedTrade(string Id, DateTimeOffset Timestamp, int Type, int TotalPlatinum,
    IReadOnlyList<AlecaTradeItem> Sent, IReadOnlyList<AlecaTradeItem> Received)
{
    public bool IsSale => Type == 0;
}

public static class AlecaTradeHistory
{
    public static IReadOnlyList<AlecaCompletedTrade> Parse(JsonElement root)
    {
        if (!TryProperty(root, "trades", out var trades) || trades.ValueKind != JsonValueKind.Array) return [];
        var parsed = new List<AlecaCompletedTrade>();
        foreach (var trade in trades.EnumerateArray())
        {
            if (!TryProperty(trade, "ts", out var timestampValue) ||
                timestampValue.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(timestampValue.GetString(), out var timestamp)) continue;
            var type = Integer(trade, "type");
            var totalPlatinum = Integer(trade, "totalPlat");
            var sent = Items(trade, "tx");
            var received = Items(trade, "rx");
            var canonical = new StringBuilder()
                .Append(timestamp.ToUniversalTime().ToString("O")).Append('|').Append(type).Append('|').Append(totalPlatinum)
                .Append('|').Append(String(trade, "user")).Append("|tx");
            Append(canonical, sent); canonical.Append("|rx"); Append(canonical, received);
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
            parsed.Add(new(id, timestamp, type, totalPlatinum, sent, received));
        }
        return parsed.OrderBy(row => row.Timestamp).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
    }

    private static AlecaTradeItem[] Items(JsonElement trade, string property)
    {
        if (!TryProperty(trade, property, out var items) || items.ValueKind != JsonValueKind.Array) return [];
        return items.EnumerateArray().Select(item => new AlecaTradeItem(
            String(item, "name"), String(item, "displayName"), Math.Max(0, Integer(item, "cnt")), Math.Max(0, Integer(item, "rank"))))
            .Where(item => item.Quantity > 0 && (item.Name.Length > 0 || item.DisplayName.Length > 0)).ToArray();
    }

    private static void Append(StringBuilder target, IEnumerable<AlecaTradeItem> items)
    {
        foreach (var item in items.OrderBy(row => row.DisplayName, StringComparer.Ordinal).ThenBy(row => row.Name, StringComparer.Ordinal)
                     .ThenBy(row => row.Rank).ThenBy(row => row.Quantity))
            target.Append('|').Append(item.Name).Append('\u001f').Append(item.DisplayName).Append('\u001f')
                .Append(item.Quantity).Append('\u001f').Append(item.Rank);
    }

    private static int Integer(JsonElement row, string name)
        => TryProperty(row, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : 0;

    private static string String(JsonElement row, string name)
        => TryProperty(row, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";

    private static bool TryProperty(JsonElement row, string name, out JsonElement value)
    {
        if (row.ValueKind == JsonValueKind.Object)
            foreach (var property in row.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default; return false;
    }
}
