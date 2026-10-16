using FitOS.Application.Abstractions;

namespace FitOS.Infrastructure.Persistence;

/// <summary>Scoped holder set by the HTTP middleware or by a background job before touching the database.</summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }

    /// <summary>Pins the tenant. Re-pinning the same tenant is harmless; switching tenants inside one scope is a bug.</summary>
    public void Set(Guid tenantId)
    {
        if (TenantId is { } current && current != tenantId)
        {
            throw new InvalidOperationException("The tenant of this scope is already set to a different tenant.");
        }

        TenantId = tenantId;
    }
}
