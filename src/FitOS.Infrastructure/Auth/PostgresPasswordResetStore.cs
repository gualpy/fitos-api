using System.ComponentModel.DataAnnotations.Schema;
using FitOS.Application.Auth;
using FitOS.Domain.Tenancy;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitOS.Infrastructure.Auth;

public sealed class PostgresPasswordResetStore(FitOsDbContext db) : IPasswordResetStore
{
    public async Task<ResetTarget?> FindAsync(string tenantSlug, string identifier, CancellationToken cancellationToken)
    {
        var rows = await db.Database
            .SqlQuery<Row>($"SELECT user_id, tenant_id, display_name, email, role, is_active, tenant_status, gym_name, gym_slug FROM auth_find_user_for_reset({tenantSlug}, {identifier})")
            .ToListAsync(cancellationToken);

        // (gym, username) and (gym, e-mail) are unique, so at most one row; anything else counts as "not found".
        return rows is [var r]
            ? new ResetTarget(r.UserId, r.TenantId, r.DisplayName, r.Email, UserRoleWire.Parse(r.Role), r.IsActive, r.TenantStatus, r.GymName, r.GymSlug)
            : null;
    }

    private sealed class Row
    {
        [Column("user_id")]
        public Guid UserId { get; set; }

        [Column("tenant_id")]
        public Guid TenantId { get; set; }

        [Column("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        [Column("email")]
        public string? Email { get; set; }

        [Column("role")]
        public string Role { get; set; } = string.Empty;

        [Column("is_active")]
        public bool IsActive { get; set; }

        [Column("tenant_status")]
        public string? TenantStatus { get; set; }

        [Column("gym_name")]
        public string GymName { get; set; } = string.Empty;

        [Column("gym_slug")]
        public string GymSlug { get; set; } = string.Empty;
    }
}
