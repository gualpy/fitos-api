using FitOS.Application.Abstractions;
using FitOS.Application.Common;
using FitOS.Application.Customers;
using FitOS.Application.Products;
using FitOS.Domain.Products;
using FitOS.Domain.Sales;

namespace FitOS.Application.Sales;

public sealed record SaleLineInput(Guid? ProductId, int? Quantity);

public sealed record PaymentInput(string? Method, decimal? Amount);

public sealed record SaleInput(Guid? CustomerId, IReadOnlyList<SaleLineInput>? Lines, IReadOnlyList<PaymentInput>? Payments);

public sealed record SalePayment(string Method, string SriCode, decimal Amount);

/// <summary>What the repository writes in one transaction. Prices and totals were computed on the server.</summary>
public sealed record SaleDraft(
    Guid Id,
    Guid TenantId,
    Guid? CustomerId,
    Guid? CashierId,
    DateTimeOffset CreatedAt,
    SaleTotals Totals,
    IReadOnlyList<PricedLine> Lines,
    IReadOnlyList<SalePayment> Payments);

public sealed record Sale(
    Guid Id,
    Guid? CustomerId,
    DateTimeOffset CreatedAt,
    SaleTotals Totals,
    IReadOnlyList<PricedLine> Lines,
    IReadOnlyList<SalePayment> Payments);

/// <summary>SRI code of each payment method. It lives in configuration because the accountant, not the code, decides it.</summary>
public interface IPaymentCodes
{
    string? CodeFor(string method);
}

public interface ISaleRepository
{
    /// <summary>Products by id, not tracked. Ids of other gyms simply do not come back.</summary>
    Task<IReadOnlyList<Product>> FindProductsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the sale, its lines and payments and takes the stock out, all or nothing.
    /// </summary>
    /// <exception cref="ConflictException"><c>insufficient_stock</c> or <c>no_branch</c></exception>
    Task CreateAsync(SaleDraft sale, CancellationToken cancellationToken);
}

public sealed class SaleService(
    ISaleRepository sales,
    ICustomerRepository customers,
    IPaymentCodes paymentCodes,
    ITenantContext tenant,
    TimeProvider time)
{
    public const int MaxLines = 50;

    public const int MaxQuantity = 10_000;

    public async Task<Sale> CreateAsync(SaleInput input, Guid? cashierId, CancellationToken cancellationToken)
    {
        var errors = new ErrorBag();
        var wanted = ParseLines(input.Lines, errors);
        var payments = ParsePayments(input.Payments, errors);
        errors.ThrowIfAny();

        if (input.CustomerId is { } customerId && await customers.GetAsync(customerId, cancellationToken) is null)
        {
            throw new NotFoundException("customer");
        }

        var found = (await sales.FindProductsAsync([.. wanted.Keys], cancellationToken)).ToDictionary(p => p.Id);
        if (found.Count != wanted.Count)
        {
            throw new NotFoundException("product");
        }

        if (found.Values.Any(p => !p.IsActive))
        {
            throw new ConflictException("product_inactive");
        }

        // The server decides every price and rate: the client only says what and how many.
        var lines = wanted
            .Select(w => (Product: found[w.Key], Quantity: w.Value))
            .Select(x => new PricedLine(x.Product.Id, x.Product.Name, x.Quantity, x.Product.Price, x.Product.IvaRate, SaleCalculator.LineTotal(x.Product.Price, x.Quantity)))
            .ToList();
        var totals = SaleCalculator.Totals(lines);

        if (payments.Sum(p => p.Amount) != totals.Total)
        {
            throw new ConflictException("payment_mismatch");
        }

        var resolved = new List<SalePayment>();
        foreach (var payment in payments)
        {
            var code = paymentCodes.CodeFor(payment.Method) ?? throw new InvalidOperationException($"No SRI code configured for '{payment.Method}'.");
            resolved.Add(new SalePayment(payment.Method, code, payment.Amount));
        }

        var draft = new SaleDraft(
            Guid.NewGuid(),
            tenant.TenantId ?? throw new InvalidOperationException("No tenant in scope."),
            input.CustomerId,
            cashierId,
            time.GetUtcNow(),
            totals,
            lines,
            resolved);

        await sales.CreateAsync(draft, cancellationToken);
        return new Sale(draft.Id, draft.CustomerId, draft.CreatedAt, totals, lines, resolved);
    }

    /// <summary>The same product twice in the cart is one line with the sum.</summary>
    private static Dictionary<Guid, int> ParseLines(IReadOnlyList<SaleLineInput>? lines, ErrorBag errors)
    {
        var wanted = new Dictionary<Guid, int>();
        if (lines is null or { Count: 0 })
        {
            errors.Add("lines", "required");
            return wanted;
        }

        if (lines.Count > MaxLines)
        {
            errors.Add("lines", "too_many");
            return wanted;
        }

        foreach (var line in lines)
        {
            if (line.ProductId is not { } productId || line.Quantity is not (>= 1 and <= MaxQuantity))
            {
                errors.Add("lines", "invalid");
                return wanted;
            }

            wanted[productId] = wanted.GetValueOrDefault(productId) + line.Quantity.Value;
            if (wanted[productId] > MaxQuantity)
            {
                errors.Add("lines", "invalid");
                return wanted;
            }
        }

        return wanted;
    }

    private static List<(string Method, decimal Amount)> ParsePayments(IReadOnlyList<PaymentInput>? payments, ErrorBag errors)
    {
        var parsed = new List<(string, decimal)>();
        if (payments is null or { Count: 0 })
        {
            errors.Add("payments", "required");
            return parsed;
        }

        if (payments.Count > 5)
        {
            errors.Add("payments", "too_many");
            return parsed;
        }

        foreach (var payment in payments)
        {
            var method = payment.Method?.Trim().ToLowerInvariant();
            if (method is null || !PaymentMethods.All.Contains(method)
                || payment.Amount is not { } amount || amount <= 0 || amount > 1_000_000 || decimal.Round(amount, 2) != amount)
            {
                errors.Add("payments", "invalid");
                return parsed;
            }

            parsed.Add((method, amount));
        }

        return parsed;
    }
}
