namespace FitOS.Domain.Sales;

/// <summary>One line of a sale: a product OR a plan (exactly one of the two ids is set).</summary>
public sealed record PricedLine(Guid? ProductId, Guid? PlanId, string Description, int Quantity, decimal UnitPrice, decimal IvaRate, decimal LineTotal);

public sealed record SaleTotals(decimal Subtotal, decimal TaxTotal, decimal Total);

public static class PaymentMethods
{
    public static readonly IReadOnlyList<string> All = ["cash", "credit_card", "debit_card", "transfer", "other"];
}

/// <summary>Money maths of a sale, pure so rounding can be tested without a database.</summary>
public static class SaleCalculator
{
    public static decimal LineTotal(decimal unitPrice, int quantity) => Round(unitPrice * quantity);

    /// <summary>Like an SRI invoice: IVA is computed on the sum of each rate, not line by line.</summary>
    public static SaleTotals Totals(IEnumerable<PricedLine> lines)
    {
        var all = lines.ToList();
        var subtotal = all.Sum(l => l.LineTotal);
        var tax = all.GroupBy(l => l.IvaRate).Sum(g => Round(g.Sum(l => l.LineTotal) * g.Key / 100m));
        return new SaleTotals(subtotal, tax, subtotal + tax);
    }

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
