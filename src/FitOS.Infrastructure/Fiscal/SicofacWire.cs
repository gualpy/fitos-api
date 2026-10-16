using System.Text.Json;

namespace FitOS.Infrastructure.Fiscal;

// Wire shapes of the SICOFAC API v1 (snake_case JSON). Kept internal: the rest of the
// code only sees the types in FitOS.Application.Fiscal.

internal sealed record IssueBody(
    string ExternalReference,
    string DocumentCode,
    string? EstablishmentCode,
    string? EmissionPoint,
    BuyerBody? Buyer,
    IReadOnlyList<ItemBody> Items,
    IReadOnlyList<PaymentBody> PaymentMethods,
    TotalsBody? ExpectedTotals);

internal sealed record BuyerBody(string IdentificationType, string IdentificationNumber, string Name, string? Email, string? Phone, string? Address);

internal sealed record ItemBody(string Code, string Name, decimal Quantity, decimal UnitPrice, decimal Discount, decimal TaxRate);

internal sealed record PaymentBody(string Method, decimal Value);

internal sealed record TotalsBody(decimal Subtotal, decimal Tax, decimal Total);

internal sealed record DataEnvelope(InvoiceBody? Data);

internal sealed record InvoiceBody(
    long Id,
    string? ExternalReference,
    string? Status,
    string? DocumentCode,
    string? EstablishmentCode,
    string? EmissionPoint,
    JsonElement? Sequential,
    string? AccessKey,
    string? AuthorizationNumber,
    DateTimeOffset? AuthorizedAt,
    TotalsBody? Totals,
    string? RideUrl,
    JsonElement? Error);

internal sealed record ErrorEnvelope(ErrorBody? Error);

internal sealed record ErrorBody(string? Code, string? Message, JsonElement? Details);
