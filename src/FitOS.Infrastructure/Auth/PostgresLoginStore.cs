using System.ComponentModel.DataAnnotations.Schema;
using FitOS.Application.Auth;
using FitOS.Domain.Tenancy;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitOS.Infrastructure.Auth;

/// <summary>Talks to the SECURITY DEFINER functions of migration 0002; the app role cannot read `users` directly here.</summary>
public sealed class PostgresLoginStore(FitOsDbContext db) : ILoginStore
{
    public async Task<LoginRecord?> FindAsync(string? tenantSlug, string username, CancellationToken cancellationToken)
    {
        var rows = await db.Database
            .SqlQuery<LoginRow>($"SELECT * FROM auth_find_user({tenantSlug}, {username})")
            .ToListAsync(cancellationToken);

        // (tenant, username) is unique, so at most one row; anything else is treated as "not found".
        if (rows is not [var row])
        {
            return null;
        }

        return new LoginRecord(
            row.UserId,
            row.TenantId,
            row.PasswordHash,
            UserRoleWire.Parse(row.Role),
            row.DisplayName,
            row.CustomerId,
            row.IsActive,
            row.IsLocked,
            row.TenantStatus,
            row.MustChangePassword);
    }

    public async Task<bool> RegisterFailureAsync(Guid userId, int maxFailures, TimeSpan lockout, CancellationToken cancellationToken)
    {
        var minutes = (int)Math.Ceiling(lockout.TotalMinutes);
        var result = await db.Database
            .SqlQuery<bool>($"SELECT auth_register_failure({userId}, {maxFailures}, {minutes}) AS \"Value\"")
            .ToListAsync(cancellationToken);
        return result.FirstOrDefault();
    }

    public async Task UpgradePasswordHashAsync(Guid userId, string newHash, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT auth_rehash_password({userId}, {newHash})", cancellationToken);

    public async Task RegisterSuccessAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT auth_register_success({userId})", cancellationToken);

    private sealed class LoginRow
    {
        [Column("user_id")]
        public Guid UserId { get; set; }

        [Column("tenant_id")]
        public Guid? TenantId { get; set; }

        [Column("password_hash")]
        public string PasswordHash { get; set; } = string.Empty;

        [Column("role")]
        public string Role { get; set; } = string.Empty;

        [Column("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        [Column("customer_id")]
        public Guid? CustomerId { get; set; }

        [Column("is_active")]
        public bool IsActive { get; set; }

        [Column("is_locked")]
        public bool IsLocked { get; set; }

        [Column("tenant_status")]
        public string? TenantStatus { get; set; }

        [Column("must_change_password")]
        public bool MustChangePassword { get; set; }
    }
}
