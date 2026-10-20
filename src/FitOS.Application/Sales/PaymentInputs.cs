using FitOS.Application.Common;
using FitOS.Domain.Sales;

namespace FitOS.Application.Sales;

/// <summary>Reading and checking the payments of a sale; shared by every kind of sale.</summary>
public static class PaymentInputs
{
    public const int MaxPayments = 5;

    public static List<(string Method, decimal Amount)> Parse(IReadOnlyList<PaymentInput>? payments, ErrorBag errors)
    {
        var parsed = new List<(string, decimal)>();
        if (payments is null or { Count: 0 })
        {
            errors.Add("payments", "required");
            return parsed;
        }

        if (payments.Count > MaxPayments)
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

    /// <summary>The payments must add up to the total to the cent; each gets its SRI code from configuration.</summary>
    /// <exception cref="ConflictException"><c>payment_mismatch</c></exception>
    public static List<SalePayment> Resolve(IReadOnlyList<(string Method, decimal Amount)> payments, decimal total, IPaymentCodes codes)
    {
        if (payments.Sum(p => p.Amount) != total)
        {
            throw new ConflictException("payment_mismatch");
        }

        return payments
            .Select(p => new SalePayment(
                p.Method,
                codes.CodeFor(p.Method) ?? throw new InvalidOperationException($"No SRI code configured for '{p.Method}'."),
                p.Amount))
            .ToList();
    }
}
