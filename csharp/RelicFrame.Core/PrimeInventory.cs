using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RelicFrame.Core;

public sealed record PrimeInventoryEntry(string GameRef, int Quantity, string ItemName = "");
public sealed record PersonalMarketQuote(string ItemName, string GameRef, int Quantity, int LowestAsk, int DraftPrice, string LowestSeller);

public static class PrimeInventory
{
    public static bool ShouldDeleteManagedListing(bool owned, bool hasEligibleQuote, bool priceBookFresh)
        => !owned || (!hasEligibleQuote && priceBookFresh);

    public static IReadOnlyList<PrimeInventoryEntry> SubtractPendingSales(IEnumerable<PrimeInventoryEntry> inventory,
        IReadOnlyDictionary<string, int> pendingSoldByItemId, Func<PrimeInventoryEntry, string?> itemIdForEntry)
        => inventory.Select(row =>
        {
            var itemId = itemIdForEntry(row);
            var sold = itemId is null ? 0 : Math.Max(0, pendingSoldByItemId.GetValueOrDefault(itemId));
            return row with { Quantity = Math.Max(0, row.Quantity - sold) };
        }).Where(row => row.Quantity > 0).ToArray();

    private static bool TryProperty(JsonElement row, string name, out JsonElement value)
    {
        if (row.ValueKind == JsonValueKind.Object)
            foreach (var property in row.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default; return false;
    }

    public static IReadOnlyList<PrimeInventoryEntry> Load(string path)
    {
        using var outer = JsonDocument.Parse(ReadPayload(path));
        JsonDocument? nested = null;
        try
        {
            var root = outer.RootElement;
            if (TryProperty(root, "InventoryJson", out var encoded) && encoded.ValueKind == JsonValueKind.String)
            {
                nested = JsonDocument.Parse(encoded.GetString() ?? "{}"); root = nested.RootElement;
            }
            var counts = new Dictionary<string, (int Quantity, string Name)>(StringComparer.OrdinalIgnoreCase);
            foreach (var collection in new[] { "Recipes", "MiscItems" })
            {
                if (!TryProperty(root, collection, out var rows) || rows.ValueKind != JsonValueKind.Array) continue;
                AddRows(rows, counts);
            }
            // Also accept a small user-maintained array: [{"itemType":"/Lotus/...","itemCount":2}].
            if (root.ValueKind == JsonValueKind.Array) AddRows(root, counts);
            return counts.Where(pair => pair.Value.Quantity > 0).Select(pair => new PrimeInventoryEntry(pair.Key, pair.Value.Quantity, pair.Value.Name ?? "")).ToArray();
        }
        finally { nested?.Dispose(); }
    }

    private static byte[] ReadPayload(string path)
    {
        var payload = File.ReadAllBytes(path);
        if (!Path.GetExtension(path).Equals(".dat", StringComparison.OrdinalIgnoreCase)) return payload;
        // AlecaFrame's local inventory cache is AES-CBC wrapped. This reads inventory only;
        // callers never inspect WFMarketToken.tk or any other account/session file.
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
        aes.Key = Encoding.ASCII.GetBytes("LEO-ALEC\tEO-ALEC");
        aes.IV = [49, 50, 70, 71, 66, 51, 54, 45, 76, 69, 51, 45, 113, 61, 57, 0];
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(payload, 0, payload.Length);
    }

    private static void AddRows(JsonElement rows, Dictionary<string, (int Quantity, string Name)> counts)
    {
        foreach (var row in rows.EnumerateArray())
        {
            var gameRef = TryProperty(row, "ItemType", out var type) ? type.GetString()?.Trim() ?? "" : "";
            var itemName = TryProperty(row, "ItemName", out var name) ? name.GetString()?.Trim() ?? "" : "";
            if (gameRef.Length == 0 && itemName.Length == 0) continue;
            var key = gameRef.Length > 0 ? gameRef : "name:" + itemName;
            var quantity = TryProperty(row, "ItemCount", out var count) && count.TryGetInt32(out var parsed) ? parsed : 1;
            if (TryProperty(row, "Quantity", out var simpleCount) && simpleCount.TryGetInt32(out var simpleParsed)) quantity = simpleParsed;
            if (quantity > 0)
            {
                var previous = counts.GetValueOrDefault(key);
                counts[key] = (previous.Quantity + quantity, itemName.Length > 0 ? itemName : previous.Name ?? "");
            }
        }
    }

    public static PersonalMarketQuote? Quote(PrimeInventoryEntry inventory, string? itemName, OrderMatch asks, int minimumPrice, int undercut)
    {
        var best = asks.Entries.Where(row => !row.IsOutlier).MinBy(row => row.Price);
        return Quote(inventory, itemName, best?.Price, best?.Seller ?? "", minimumPrice, undercut);
    }

    public static PersonalMarketQuote? Quote(PrimeInventoryEntry inventory, string? itemName, double? referencePrice,
        string referenceSeller, int minimumPrice, int undercut)
    {
        if (string.IsNullOrWhiteSpace(itemName) || inventory.Quantity < 1 || !referencePrice.HasValue ||
            !(itemName.Contains(" Prime ", StringComparison.OrdinalIgnoreCase) || itemName.EndsWith(" Prime", StringComparison.OrdinalIgnoreCase)) ||
            !double.IsFinite(referencePrice.Value) || referencePrice.Value < minimumPrice) return null;
        var lowest = Math.Max(1, (int)Math.Floor(referencePrice.Value));
        var draft = Math.Max(minimumPrice, lowest - Math.Clamp(undercut, 1, 3));
        return new(itemName, inventory.GameRef, inventory.Quantity, lowest, draft, referenceSeller);
    }
}
