namespace FitOS.Application.Abstractions;

/// <summary>
/// Tenant of the current request or background job. <c>null</c> means "no tenant":
/// the database then returns zero rows (row-level security fails closed).
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
}
