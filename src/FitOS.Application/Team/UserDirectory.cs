namespace FitOS.Application.Team;

public sealed record TeamMember(Guid Id, string DisplayName, string? Email, string Role, bool IsActive, bool PasswordSet, DateTimeOffset? LastLoginAt);

public sealed record NewUser(string Username, string PasswordHash, string DisplayName, string Email, string Role, Guid? CustomerId);

/// <summary>The gym an e-mail speaks for: its name is the sender's display name.</summary>
public sealed record GymIdentity(string Name, string Slug);

public sealed record CustomerForAccess(Guid Id, string Name, string? Email, bool IsActive);

public sealed record CustomerUser(Guid UserId, string? Email, bool IsActive, bool PasswordSet);

/// <summary>Users of the CURRENT gym (row-level security does the scoping).</summary>
public interface IUserStore
{
    /// <summary>Owner, admin and reception only: customers' accounts are not "the team".</summary>
    Task<IReadOnlyList<TeamMember>> ListStaffAsync(CancellationToken cancellationToken);

    Task<TeamMember?> FindStaffAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> UsernameExistsAsync(string username, CancellationToken cancellationToken);

    /// <exception cref="FitOS.Application.Common.ConflictException"><c>email_taken</c>: another account of this gym already uses it.</exception>
    Task<Guid> CreateAsync(NewUser user, CancellationToken cancellationToken);

    Task SetActiveAsync(Guid id, bool active, CancellationToken cancellationToken);

    Task SetRoleAsync(Guid id, string role, CancellationToken cancellationToken);

    Task<int> CountActiveOwnersAsync(CancellationToken cancellationToken);

    Task RevokeSessionsAsync(Guid userId, string reason, CancellationToken cancellationToken);

    Task<GymIdentity> GymAsync(CancellationToken cancellationToken);

    Task<CustomerForAccess?> FindCustomerAsync(Guid customerId, CancellationToken cancellationToken);

    Task<CustomerUser?> FindCustomerUserAsync(Guid customerId, CancellationToken cancellationToken);
}

/// <summary>Login names nobody has to know: derived from the e-mail, made unique inside the gym.</summary>
public static class UsernameGenerator
{
    public static async Task<string> FromEmailAsync(string email, Func<string, Task<bool>> exists)
    {
        var local = email.Split('@')[0].ToLowerInvariant();
        var cleaned = new string(local.Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-').ToArray()).TrimStart('.', '_', '-');
        if (cleaned.Length < 3)
        {
            cleaned = (cleaned + "user").Length >= 3 ? cleaned + "user" : "user";
        }

        var baseName = cleaned.Length > 40 ? cleaned[..40] : cleaned;
        var candidate = baseName;
        for (var n = 2; n < 1000; n++)
        {
            if (!await exists(candidate))
            {
                return candidate;
            }

            candidate = baseName + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return baseName + Guid.NewGuid().ToString("N")[..8];
    }
}
