namespace FitOS.Application.Fiscal;

public enum FiscalConnectionStatus
{
    Ok,
    NotConfigured,
    InvalidApiKey,
    WrongCompany,
    CompanyNotFound,
    Unreachable,
    Unexpected,
}

public sealed record FiscalConnectionResult(FiscalConnectionStatus Status, string? Detail = null);

/// <summary>Loads a tenant's SICOFAC credentials (decrypted). <c>null</c> when none are configured.</summary>
public interface ITenantFiscalAccountStore
{
    Task<FiscalAccount?> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// "Test connection": asks SICOFAC for a reference that cannot exist. A 404 "not found" proves the API key is
/// valid for that company without creating anything; 401/403 mean the key is bad or belongs to another company.
/// </summary>
public sealed class FiscalConnectionService(ITenantFiscalAccountStore accounts, IFiscalProvider provider)
{
    public const string ProbeReference = "fitos:connection-check";

    public async Task<FiscalConnectionResult> TestAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAsync(tenantId, cancellationToken);
        if (account is null)
        {
            return new FiscalConnectionResult(FiscalConnectionStatus.NotConfigured);
        }

        try
        {
            await provider.GetByReferenceAsync(account, ProbeReference, cancellationToken);
            return new FiscalConnectionResult(FiscalConnectionStatus.Ok);
        }
        catch (FiscalProviderException ex)
        {
            var status = ex.HttpStatus switch
            {
                401 => FiscalConnectionStatus.InvalidApiKey,
                403 => FiscalConnectionStatus.WrongCompany,
                404 => FiscalConnectionStatus.CompanyNotFound,
                _ when ex.Code == "network_error" || ex.IsRetryable => FiscalConnectionStatus.Unreachable,
                _ => FiscalConnectionStatus.Unexpected,
            };
            return new FiscalConnectionResult(status, ex.Code);
        }
    }
}
