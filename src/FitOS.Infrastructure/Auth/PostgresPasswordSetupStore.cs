using System.ComponentModel.DataAnnotations.Schema;
using FitOS.Application.Auth;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitOS.Infrastructure.Auth;

public sealed class PostgresPasswordSetupStore(FitOsDbContext db) : IPasswordSetupStore
{
    public async Task IssueAsync(Guid userId, byte[] tokenHash, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT auth_setup_token_issue({userId}, {tokenHash}, {expiresAt})", cancellationToken);

    public async Task<SetupRedeem> RedeemAsync(byte[] tokenHash, string passwordHash, CancellationToken cancellationToken)
    {
        var row = (await db.Database
            .SqlQuery<RedeemRow>($"SELECT outcome, user_id, tenant_id FROM auth_setup_token_redeem({tokenHash}, {passwordHash})")
            .ToListAsync(cancellationToken)).FirstOrDefault();

        var status = row?.Outcome switch
        {
            "ok" => SetupRedeemStatus.Ok,
            "expired" => SetupRedeemStatus.Expired,
            _ => SetupRedeemStatus.Invalid,
        };
        return new SetupRedeem(status, row?.UserId, row?.TenantId);
    }

    public async Task<SetupInfo?> InfoAsync(byte[] tokenHash, CancellationToken cancellationToken)
    {
        var row = (await db.Database
            .SqlQuery<InfoRow>($"SELECT display_name, gym_name, gym_slug FROM auth_setup_token_info({tokenHash})")
            .ToListAsync(cancellationToken)).FirstOrDefault();
        return row is null ? null : new SetupInfo(row.DisplayName, row.GymName, row.GymSlug);
    }

    private sealed class InfoRow
    {
        [Column("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        [Column("gym_name")]
        public string? GymName { get; set; }

        [Column("gym_slug")]
        public string? GymSlug { get; set; }
    }

    private sealed class RedeemRow
    {
        [Column("outcome")]
        public string Outcome { get; set; } = string.Empty;

        [Column("user_id")]
        public Guid? UserId { get; set; }

        [Column("tenant_id")]
        public Guid? TenantId { get; set; }
    }
}
