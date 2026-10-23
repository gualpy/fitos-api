using FitOS.Application.Abstractions;
using FitOS.Application.Subscriptions;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitOS.Infrastructure.Subscriptions;

/// <summary>Reads the caller's own gym row (row-level security shows only that one).</summary>
public sealed class SubscriptionReader(FitOsDbContext db, ITenantContext tenant) : ISubscriptionReader
{
    public async Task<DateOnly?> EndsOnAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("No tenant in scope.");
        var ends = (await db.Database
            .SqlQuery<DateTime>($"SELECT subscription_ends_on AS \"Value\" FROM tenants WHERE id = {tenantId}")
            .ToListAsync(cancellationToken)).Select(d => (DateTime?)d).FirstOrDefault();
        return ends is null ? null : DateOnly.FromDateTime(ends.Value);
    }
}
