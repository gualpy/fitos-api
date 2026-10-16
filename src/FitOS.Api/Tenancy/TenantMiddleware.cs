using FitOS.Infrastructure.Persistence;

namespace FitOS.Api.Tenancy;

/// <summary>
/// Copies the tenant from the validated JWT (<c>tenant_id</c> claim) into the scoped
/// <see cref="TenantContext"/>. The tenant is NEVER read from a header, route or body.
/// A token without the claim (platform admin) leaves the context empty, so tenant tables return no rows.
/// </summary>
public sealed class TenantMiddleware(RequestDelegate next)
{
    public const string ClaimType = "tenant_id";

    public Task InvokeAsync(HttpContext context, TenantContext tenant)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && Guid.TryParse(context.User.FindFirst(ClaimType)?.Value, out var tenantId))
        {
            tenant.Set(tenantId);
        }

        return next(context);
    }
}
