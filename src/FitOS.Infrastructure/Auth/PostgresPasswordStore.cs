using FitOS.Application.Auth;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitOS.Infrastructure.Auth;

public sealed class PostgresPasswordStore(FitOsDbContext db) : IPasswordStore
{
    public async Task<string?> GetHashAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await db.Database
            .SqlQuery<string?>($"SELECT auth_get_password_hash({userId}) AS \"Value\"")
            .ToListAsync(cancellationToken);
        return rows.FirstOrDefault();
    }

    public async Task SetAsync(Guid userId, string newHash, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT auth_set_password({userId}, {newHash})", cancellationToken);
}
