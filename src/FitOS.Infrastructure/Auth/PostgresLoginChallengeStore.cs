using System.ComponentModel.DataAnnotations.Schema;
using FitOS.Application.Auth;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitOS.Infrastructure.Auth;

public sealed class PostgresLoginChallengeStore(FitOsDbContext db) : ILoginChallengeStore
{
    public async Task<string?> CreateAsync(Guid userId, byte[] linkHash, byte[] secretHash, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        (await db.Database
            .SqlQuery<string?>($"SELECT auth_login_challenge_create({userId}, {linkHash}, {secretHash}, {expiresAt}) AS \"Value\"")
            .ToListAsync(cancellationToken)).FirstOrDefault();

    public async Task<ChallengeRedeem> RedeemAsync(byte[] linkHash, byte[] secretHash, CancellationToken cancellationToken)
    {
        var row = (await db.Database
            .SqlQuery<RedeemRow>($"SELECT outcome, user_id FROM auth_login_challenge_redeem({linkHash}, {secretHash})")
            .ToListAsync(cancellationToken)).FirstOrDefault();

        var status = row?.Outcome switch
        {
            "ok" => ChallengeRedeemStatus.Ok,
            "expired" => ChallengeRedeemStatus.Expired,
            _ => ChallengeRedeemStatus.Invalid,
        };
        return new ChallengeRedeem(status, row?.UserId);
    }

    private sealed class RedeemRow
    {
        [Column("outcome")]
        public string Outcome { get; set; } = string.Empty;

        [Column("user_id")]
        public Guid? UserId { get; set; }
    }
}
