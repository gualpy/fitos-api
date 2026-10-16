namespace FitOS.Application.Fiscal;

/// <summary>
/// Port to the electronic-invoicing provider (SICOFAC today; other countries/providers later).
/// Implementations must be safe to call again with the same <c>idempotencyKey</c>.
/// </summary>
public interface IFiscalProvider
{
    Task<FiscalInvoiceState> IssueInvoiceAsync(
        FiscalAccount account,
        FiscalInvoiceRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <returns><c>null</c> when the provider has no invoice for that reference.</returns>
    Task<FiscalInvoiceState?> GetByReferenceAsync(
        FiscalAccount account,
        string externalReference,
        CancellationToken cancellationToken);
}
