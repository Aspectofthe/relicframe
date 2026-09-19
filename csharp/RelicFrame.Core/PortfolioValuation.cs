namespace RelicFrame.Core;

public sealed record PortfolioPosition(string Name, int Quantity, double? UnitAsk, bool Fresh);
public sealed record PortfolioSummary(double EstimatedAskValue, int PricedUnits, int TotalUnits, int PricedItems, int TotalItems);

public static class PortfolioValuation
{
    public static PortfolioSummary Calculate(IEnumerable<PortfolioPosition> positions)
    {
        double value = 0;
        var pricedUnits = 0;
        var totalUnits = 0;
        var pricedItems = 0;
        var totalItems = 0;
        foreach (var position in positions)
        {
            if (position.Quantity <= 0) continue;
            totalItems++;
            totalUnits += position.Quantity;
            if (!position.Fresh || position.UnitAsk is not { } price || !double.IsFinite(price) || price <= 0) continue;
            value += position.Quantity * price;
            pricedUnits += position.Quantity;
            pricedItems++;
        }
        return new(value, pricedUnits, totalUnits, pricedItems, totalItems);
    }
}
