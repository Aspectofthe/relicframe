using System.Globalization;

namespace RelicFrame.Core;

public sealed record RelicBuyMessage(string Whisper, int Quantity, double UnitPrice, double TotalPrice);

public static class RelicBuyWhisper
{
    public static RelicBuyMessage Build(string seller, string relicName, string refinement, double unitPrice, double? available, bool buyAll)
    {
        if (!double.IsFinite(unitPrice) || unitPrice <= 0) throw new ArgumentOutOfRangeException(nameof(unitPrice));
        var quantity = buyAll && available is { } count && double.IsFinite(count) && count >= 1
            && count <= int.MaxValue && count == Math.Floor(count) ? (int)count : 1;
        var safeSeller = seller.Replace('\r', ' ').Replace('\n', ' ').Replace('`', '\'').Trim();
        var item = $"{relicName} Relic ({refinement})";
        var price = unitPrice.ToString("0.##", CultureInfo.InvariantCulture);
        var total = quantity * unitPrice;
        var whisper = quantity == 1
            ? $"/w {safeSeller} Hi! I want to buy: {item} for {price} platinum. (warframe.market)"
            : $"/w {safeSeller} Hi! I want to buy: {quantity} x {item} for {total.ToString("0.##", CultureInfo.InvariantCulture)} platinum total ({price} each). (warframe.market)";
        return new(whisper, quantity, unitPrice, total);
    }
}
