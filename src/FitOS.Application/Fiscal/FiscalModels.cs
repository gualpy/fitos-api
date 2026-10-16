using System.Text;

namespace FitOS.Application.Fiscal;

/// <summary>Credentials of one tenant's company in the fiscal provider. The API key never prints.</summary>
public sealed record FiscalAccount(string ProviderCompanyId, string ApiKey)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("ProviderCompanyId = ").Append(ProviderCompanyId).Append(", ApiKey = ***");
        return true;
    }
}

/// <summary>Buyer of an invoice. A <c>null</c> buyer means consumidor final.</summary>
public sealed record FiscalBuyer(
    string IdentificationType,
    string IdentificationNumber,
    string Name,
    string? Email = null,
    string? Phone = null,
    string? Address = null);

/// <param name="UnitPrice">Price WITHOUT IVA.</param>
/// <param name="TaxRate">IVA percentage (15, 5, 0).</param>
public sealed record FiscalItem(
    string Code,
    string Name,
    decimal Quantity,
    decimal UnitPrice,
    decimal TaxRate,
    decimal Discount = 0m);

/// <param name="Method">SICOFAC payment method (see <see cref="FiscalPaymentMethods"/>).</param>
public sealed record FiscalPayment(string Method, decimal Value);

public sealed record FiscalTotals(decimal Subtotal, decimal Tax, decimal Total);

public sealed record FiscalInvoiceRequest(
    string ExternalReference,
    IReadOnlyList<FiscalItem> Items,
    IReadOnlyList<FiscalPayment> Payments,
    FiscalBuyer? Buyer = null,
    FiscalTotals? ExpectedTotals = null,
    string DocumentCode = "01",
    string? EstablishmentCode = null,
    string? EmissionPoint = null);

/// <summary>Provider-side status, already collapsed to the API v1 states.</summary>
public enum FiscalRemoteStatus
{
    Draft,
    Processing,
    Authorized,
    Rejected,
    Failed,
}

public sealed record FiscalInvoiceState(
    long Id,
    string ExternalReference,
    FiscalRemoteStatus Status,
    string DocumentCode,
    string EstablishmentCode,
    string EmissionPoint,
    long? Sequential,
    string? AccessKey,
    string? AuthorizationNumber,
    DateTimeOffset? AuthorizedAt,
    FiscalTotals Totals,
    string? RideUrl,
    string? ErrorDetails,
    bool Replayed);
