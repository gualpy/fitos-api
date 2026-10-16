using System.ComponentModel.DataAnnotations.Schema;
using FitOS.Application.Fiscal;
using FitOS.Application.Secrets;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitOS.Infrastructure.Onboarding;

/// <summary>
/// Reads a tenant's SICOFAC credentials. A platform administrator has no tenant of their own, so this pins the
/// requested tenant for the rest of the request scope (switching to a different one afterwards is refused).
/// </summary>
public sealed class PostgresTenantFiscalAccountStore(FitOsDbContext db, TenantContext tenant, ISecretProtector secrets) : ITenantFiscalAccountStore
{
    public async Task<FiscalAccount?> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        tenant.Set(tenantId);

        var rows = await db.Database
            .SqlQuery<AccountRow>($"SELECT provider_company_id, provider_api_key_enc FROM tenant_fiscal_config WHERE tenant_id = {tenantId}")
            .ToListAsync(cancellationToken);

        return rows is [{ CompanyId: { Length: > 0 } companyId, EncryptedKey: { Length: > 0 } encrypted }]
            ? new FiscalAccount(companyId, secrets.Unprotect(encrypted, tenantId))
            : null;
    }

    private sealed class AccountRow
    {
        [Column("provider_company_id")]
        public string? CompanyId { get; set; }

        [Column("provider_api_key_enc")]
        public byte[]? EncryptedKey { get; set; }
    }
}
