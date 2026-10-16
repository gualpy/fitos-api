using System.ComponentModel.DataAnnotations.Schema;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FitOS.Application.Auth;
using FitOS.Domain.Tenancy;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FitOS.Infrastructure.Auth;

/// <summary>
/// Renewal tokens: 256 random bits, handed to the client once. Only the SHA-256 reaches the database, and only through
/// SECURITY DEFINER functions (migration 0006): the API role cannot read the token tables.
/// </summary>
public sealed partial class PostgresSessionStore(FitOsDbContext db, IOptions<AuthOptions> options) : ISessionStore
{
    private TimeSpan Idle => TimeSpan.FromMinutes(options.Value.IdleMinutes);

    private TimeSpan Max => TimeSpan.FromHours(options.Value.SessionHours);

    public async Task<(Guid SessionId, RefreshGrant Refresh)> CreateAsync(Guid userId, string? userAgent, CancellationToken cancellationToken)
    {
        var token = NewToken();
        var hash = Hash(token);
        var rows = await db.Database
            .SqlQuery<CreateRow>($"SELECT session_id, refresh_expires_at FROM auth_session_create({userId}, {hash}, {Idle}, {Max}, {userAgent})")
            .ToListAsync(cancellationToken);
        var row = rows.Single();
        return (row.SessionId, new RefreshGrant(token, row.RefreshExpiresAt));
    }

    public async Task<RefreshOutcome> RotateAsync(string presentedRefreshToken, CancellationToken cancellationToken)
    {
        // Anything that cannot be one of our tokens never reaches the database.
        if (!TokenShape().IsMatch(presentedRefreshToken))
        {
            return new RefreshOutcome(RefreshStatus.Invalid);
        }

        var next = NewToken();
        var oldHash = Hash(presentedRefreshToken);
        var newHash = Hash(next);
        var rows = await db.Database
            .SqlQuery<RotateRow>($"SELECT * FROM auth_session_refresh({oldHash}, {newHash}, {Idle})")
            .ToListAsync(cancellationToken);
        var row = rows.SingleOrDefault();

        var status = row?.Outcome switch
        {
            "ok" => RefreshStatus.Ok,
            "expired" => RefreshStatus.Expired,
            "reuse" => RefreshStatus.Reuse,
            "concurrent" => RefreshStatus.Concurrent,
            "inactive" => RefreshStatus.Inactive,
            _ => RefreshStatus.Invalid,
        };
        if (status != RefreshStatus.Ok || row is null)
        {
            return new RefreshOutcome(status, UserId: row?.UserId); // for "reuse" the owner is known, so the theft can be logged against them
        }

        var user = new LoginRecord(
            row.UserId!.Value,
            row.TenantId,
            string.Empty, // the password hash is never needed (or loaded) to renew a session
            UserRoleWire.Parse(row.Role!),
            row.DisplayName!,
            row.CustomerId,
            IsActive: true,
            IsLocked: false,
            row.TenantStatus,
            row.MustChangePassword ?? false);
        return new RefreshOutcome(RefreshStatus.Ok, user, row.SessionId, new RefreshGrant(next, row.RefreshExpiresAt!.Value));
    }

    public async Task RevokeAsync(Guid sessionId, string reason, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT auth_session_revoke({sessionId}, {reason})", cancellationToken);

    private static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex TokenShape();

    private sealed class CreateRow
    {
        [Column("session_id")]
        public Guid SessionId { get; set; }

        [Column("refresh_expires_at")]
        public DateTimeOffset RefreshExpiresAt { get; set; }
    }

    private sealed class RotateRow
    {
        [Column("outcome")]
        public string Outcome { get; set; } = string.Empty;

        [Column("session_id")]
        public Guid? SessionId { get; set; }

        [Column("refresh_expires_at")]
        public DateTimeOffset? RefreshExpiresAt { get; set; }

        [Column("user_id")]
        public Guid? UserId { get; set; }

        [Column("tenant_id")]
        public Guid? TenantId { get; set; }

        [Column("role")]
        public string? Role { get; set; }

        [Column("display_name")]
        public string? DisplayName { get; set; }

        [Column("customer_id")]
        public Guid? CustomerId { get; set; }

        [Column("must_change_password")]
        public bool? MustChangePassword { get; set; }

        [Column("tenant_status")]
        public string? TenantStatus { get; set; }
    }
}
