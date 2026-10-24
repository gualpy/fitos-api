using System.ComponentModel.DataAnnotations.Schema;
using FitOS.Application.Platform;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitOS.Infrastructure.Platform;

/// <summary>Everything goes through SECURITY DEFINER functions: the API role cannot read other gyms' rows directly.</summary>
public sealed class PostgresPlatformStore(FitOsDbContext db) : IPlatformStore
{
    public async Task<IReadOnlyList<TenantSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Database
            .SqlQuery<TenantRow>($"SELECT id, name, slug, status, effective_status, subscription_ends_on, days_left, created_at, customers, owner_state FROM platform_list_tenants()")
            .ToListAsync(cancellationToken);
        return rows
            .Select(r => new TenantSummary(r.Id, r.Name, r.Slug, r.Status, r.EffectiveStatus, DateOnly.FromDateTime(r.SubscriptionEndsOn), r.DaysLeft, r.CreatedAt, r.Customers, r.OwnerState))
            .ToList();
    }

    public async Task<DateOnly?> ExtendAsync(Guid tenantId, int months, decimal amount, Guid actorId, CancellationToken cancellationToken)
    {
        var ends = (await db.Database
            .SqlQuery<DateTime?>($"SELECT platform_extend_subscription({tenantId}, {months}, {amount}, {actorId}) AS \"Value\"")
            .ToListAsync(cancellationToken)).FirstOrDefault();
        return ends is null ? null : DateOnly.FromDateTime(ends.Value);
    }

    public async Task<bool> SetStatusAsync(Guid tenantId, string status, Guid actorId, CancellationToken cancellationToken) =>
        (await db.Database
            .SqlQuery<bool>($"SELECT platform_set_tenant_status({tenantId}, {status}, {actorId}) AS \"Value\"")
            .ToListAsync(cancellationToken)).FirstOrDefault();

    public async Task<IReadOnlyList<MonthlyRevenue>> RevenueAsync(int months, CancellationToken cancellationToken)
    {
        var rows = await db.Database
            .SqlQuery<RevenueRow>($"SELECT month, total, payments FROM platform_revenue_by_month({months})")
            .ToListAsync(cancellationToken);
        return rows.Select(r => new MonthlyRevenue(DateOnly.FromDateTime(r.Month), r.Total, r.Payments)).ToList();
    }

    public async Task<TenantOwner?> FindOwnerAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await db.Database
            .SqlQuery<OwnerRow>($"SELECT user_id, username, display_name, email, must_change_password, gym_name, gym_slug FROM platform_find_owner({tenantId})")
            .ToListAsync(cancellationToken))
        .Select(r => new TenantOwner(r.UserId, r.Username, r.DisplayName, r.Email, r.MustChangePassword, r.GymName, r.GymSlug))
        .FirstOrDefault();

    private sealed class TenantRow
    {
        [Column("id")]
        public Guid Id { get; set; }

        [Column("name")]
        public string Name { get; set; } = string.Empty;

        [Column("slug")]
        public string Slug { get; set; } = string.Empty;

        [Column("status")]
        public string Status { get; set; } = string.Empty;

        [Column("effective_status")]
        public string EffectiveStatus { get; set; } = string.Empty;

        [Column("subscription_ends_on")]
        public DateTime SubscriptionEndsOn { get; set; }

        [Column("days_left")]
        public int DaysLeft { get; set; }

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; }

        [Column("customers")]
        public long Customers { get; set; }

        [Column("owner_state")]
        public string OwnerState { get; set; } = string.Empty;
    }

    private sealed class RevenueRow
    {
        [Column("month")]
        public DateTime Month { get; set; }

        [Column("total")]
        public decimal Total { get; set; }

        [Column("payments")]
        public long Payments { get; set; }
    }

    private sealed class OwnerRow
    {
        [Column("user_id")]
        public Guid UserId { get; set; }

        [Column("username")]
        public string Username { get; set; } = string.Empty;

        [Column("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        [Column("email")]
        public string? Email { get; set; }

        [Column("must_change_password")]
        public bool MustChangePassword { get; set; }

        [Column("gym_name")]
        public string GymName { get; set; } = string.Empty;

        [Column("gym_slug")]
        public string GymSlug { get; set; } = string.Empty;
    }
}
