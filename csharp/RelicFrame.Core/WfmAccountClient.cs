using System.Text.Json;

namespace RelicFrame.Core;

public sealed record WfmOwnOrder(string Id, string ItemId, string Type, int Platinum, int Quantity, bool Visible);

public sealed class WfmAccountClient(MarketHttp http, string tokenPath)
{
    private string Token()
    {
        var token = Environment.GetEnvironmentVariable("RELICFRAME_WFM_TOKEN")?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            if (!File.Exists(tokenPath))
                throw new FileNotFoundException("Warframe.market token was not found. Set RELICFRAME_WFM_TOKEN or RELICFRAME_WFM_TOKEN_FILE.", tokenPath);
            token = File.ReadAllText(tokenPath).Trim();
        }
        if (token.Length < 100 || token.Count(character => character == '.') != 2)
            throw new InvalidDataException("Warframe.market token does not have the expected JWT format.");
        return token;
    }

    public async Task<IReadOnlyList<WfmOwnOrder>> OrdersAsync(CancellationToken ct)
    {
        using var document = await http.SendJsonAsync(HttpMethod.Get, "https://api.warframe.market/v2/orders/my", null, Token(), ct, lowPriority: false);
        return document.RootElement.Get("data").Rows().Select(Parse).Where(order => order.Id.Length > 0 && order.ItemId.Length > 0).ToArray();
    }

    public async Task<WfmOwnOrder> CreateSellAsync(string itemId, int platinum, int quantity, CancellationToken ct)
    {
        using var document = await http.SendJsonAsync(HttpMethod.Post, "https://api.warframe.market/v2/order",
            new { itemId, type = "sell", platinum, quantity, visible = true }, Token(), ct, lowPriority: false);
        return Parse(document.RootElement.Get("data"));
    }

    public async Task<WfmOwnOrder> UpdateSellAsync(string orderId, int platinum, int quantity, bool visible, CancellationToken ct)
    {
        using var document = await http.SendJsonAsync(HttpMethod.Patch, "https://api.warframe.market/v2/order/" + Uri.EscapeDataString(orderId),
            new { platinum, quantity, visible }, Token(), ct, lowPriority: false);
        return Parse(document.RootElement.Get("data"));
    }

    public async Task<WfmOwnOrder> DeleteOrderAsync(string orderId, CancellationToken ct)
    {
        using var document = await http.SendJsonAsync(HttpMethod.Delete, "https://api.warframe.market/v2/order/" + Uri.EscapeDataString(orderId),
            null, Token(), ct, lowPriority: false);
        return Parse(document.RootElement.Get("data"));
    }

    public async Task CloseOrderAsync(string orderId, int quantity, CancellationToken ct)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        using var _ = await http.SendJsonAsync(HttpMethod.Post,
            "https://api.warframe.market/v2/order/" + Uri.EscapeDataString(orderId) + "/close",
            new { quantity }, Token(), ct, lowPriority: false);
    }

    private static WfmOwnOrder Parse(JsonElement row) => new(row.Get("id").Text(), row.Get("itemId").Text(), row.Get("type").Text(),
        (int)(row.Get("platinum").Number() ?? 0), (int)(row.Get("quantity").Number() ?? 0), row.Get("visible").Bool());
}
