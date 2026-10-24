using System.ComponentModel.DataAnnotations.Schema;
using FitOS.Application.Abstractions;
using FitOS.Application.Common;
using FitOS.Application.Team;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FitOS.Infrastructure.Team;

/// <summary>Plain SQL under the request's tenant: row-level security already limits every statement to the caller's gym.</summary>
public sealed class PostgresUserStore(FitOsDbContext db, ITenantContext tenant) : IUserStore
{
    private Guid TenantId => tenant.TenantId ?? throw new InvalidOperationException("No tenant in scope.");

    public async Task<IReadOnlyList<TeamMember>> ListStaffAsync(CancellationToken cancellationToken) =>
        (await db.Database
            .SqlQuery<MemberRow>($"SELECT id, display_name, email::text AS email, role, is_active, NOT must_change_password AS password_set, last_login_at FROM users WHERE role IN ('owner','admin','reception') ORDER BY created_at, id")
            .ToListAsync(cancellationToken)).Select(ToMember).ToList();

    public async Task<TeamMember?> FindStaffAsync(Guid id, CancellationToken cancellationToken) =>
        (await db.Database
            .SqlQuery<MemberRow>($"SELECT id, display_name, email::text AS email, role, is_active, NOT must_change_password AS password_set, last_login_at FROM users WHERE id = {id} AND role IN ('owner','admin','reception')")
            .ToListAsync(cancellationToken)).Select(ToMember).FirstOrDefault();

    public async Task<bool> UsernameExistsAsync(string username, CancellationToken cancellationToken) =>
        (await db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM users WHERE username = {username}::citext")
            .ToListAsync(cancellationToken)).FirstOrDefault() > 0;

    public async Task<Guid> CreateAsync(NewUser user, CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        try
        {
            return (await db.Database
                .SqlQuery<Guid>($"""
                    INSERT INTO users (tenant_id, username, password_hash, display_name, role, email, customer_id, must_change_password)
                    VALUES ({tenantId}, {user.Username}, {user.PasswordHash}, {user.DisplayName}, {user.Role}, {user.Email}, {user.CustomerId}, true)
                    RETURNING id AS "Value"
                    """)
                .ToListAsync(cancellationToken)).Single();
        }
        catch (Exception ex) when (UniqueViolation(ex, out var constraint))
        {
            throw new ConflictException(constraint switch
            {
                "users_email_uq" => "email_taken",
                _ => "username_taken",
            });
        }
    }

    public async Task SetActiveAsync(Guid id, bool active, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"UPDATE users SET is_active = {active} WHERE id = {id}", cancellationToken);

    public async Task SetRoleAsync(Guid id, string role, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"UPDATE users SET role = {role} WHERE id = {id}", cancellationToken);

    public async Task<int> CountActiveOwnersAsync(CancellationToken cancellationToken) =>
        (await db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM users WHERE role = 'owner' AND is_active")
            .ToListAsync(cancellationToken)).FirstOrDefault();

    public async Task RevokeSessionsAsync(Guid userId, string reason, CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        await db.Database.ExecuteSqlAsync($"SELECT auth_user_sessions_revoke({tenantId}, {userId}, {reason})", cancellationToken);
    }

    public async Task<GymIdentity> GymAsync(CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        var row = (await db.Database
            .SqlQuery<GymRow>($"SELECT name, slug::text AS slug FROM tenants WHERE id = {tenantId}")
            .ToListAsync(cancellationToken)).Single();
        return new GymIdentity(row.Name, row.Slug);
    }

    public async Task<CustomerForAccess?> FindCustomerAsync(Guid customerId, CancellationToken cancellationToken) =>
        (await db.Database
            .SqlQuery<CustomerRow>($"SELECT id, name, email::text AS email, (status = 'active') AS is_active FROM customers WHERE id = {customerId}")
            .ToListAsync(cancellationToken)).Select(r => new CustomerForAccess(r.Id, r.Name, r.Email, r.IsActive)).FirstOrDefault();

    public async Task<CustomerUser?> FindCustomerUserAsync(Guid customerId, CancellationToken cancellationToken) =>
        (await db.Database
            .SqlQuery<CustomerUserRow>($"SELECT id AS user_id, email::text AS email, is_active, NOT must_change_password AS password_set FROM users WHERE customer_id = {customerId} AND role = 'customer'")
            .ToListAsync(cancellationToken)).Select(r => new CustomerUser(r.UserId, r.Email, r.IsActive, r.PasswordSet)).FirstOrDefault();

    private static TeamMember ToMember(MemberRow r) => new(r.Id, r.DisplayName, r.Email, r.Role, r.IsActive, r.PasswordSet, r.LastLoginAt);

    private static bool UniqueViolation(Exception ex, out string constraint)
    {
        var pg = ex as PostgresException ?? ex.InnerException as PostgresException;
        constraint = pg?.ConstraintName ?? string.Empty;
        return pg is { SqlState: PostgresErrorCodes.UniqueViolation };
    }

    private sealed class MemberRow
    {
        [Column("id")]
        public Guid Id { get; set; }

        [Column("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        [Column("email")]
        public string? Email { get; set; }

        [Column("role")]
        public string Role { get; set; } = string.Empty;

        [Column("is_active")]
        public bool IsActive { get; set; }

        [Column("password_set")]
        public bool PasswordSet { get; set; }

        [Column("last_login_at")]
        public DateTimeOffset? LastLoginAt { get; set; }
    }

    private sealed class GymRow
    {
        [Column("name")]
        public string Name { get; set; } = string.Empty;

        [Column("slug")]
        public string Slug { get; set; } = string.Empty;
    }

    private sealed class CustomerRow
    {
        [Column("id")]
        public Guid Id { get; set; }

        [Column("name")]
        public string Name { get; set; } = string.Empty;

        [Column("email")]
        public string? Email { get; set; }

        [Column("is_active")]
        public bool IsActive { get; set; }
    }

    private sealed class CustomerUserRow
    {
        [Column("user_id")]
        public Guid UserId { get; set; }

        [Column("email")]
        public string? Email { get; set; }

        [Column("is_active")]
        public bool IsActive { get; set; }

        [Column("password_set")]
        public bool PasswordSet { get; set; }
    }
}
