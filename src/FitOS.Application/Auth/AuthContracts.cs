using FitOS.Domain.Tenancy;

namespace FitOS.Application.Auth;

public sealed record LoginRecord(
    Guid UserId,
    Guid? TenantId,
    string PasswordHash,
    UserRole Role,
    string DisplayName,
    Guid? CustomerId,
    bool IsActive,
    bool IsLocked,
    string? TenantStatus,
    bool MustChangePassword = false);

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

public sealed record LoginPolicy(int MaxFailures, TimeSpan Lockout);

public sealed record LoginUser(Guid Id, Guid? TenantId, string DisplayName, UserRole Role, bool MustChangePassword = false, Guid? CustomerId = null);

/// <summary>Deliberately has no reason: every failure looks the same to the caller.</summary>
public sealed record LoginResult(AccessToken? Token, LoginUser? User)
{
    public bool Succeeded => Token is not null;

    public static LoginResult Failed { get; } = new(null, null);
}

public interface ILoginStore
{
    /// <param name="tenantSlug"><c>null</c> = platform administrators.</param>
    Task<LoginRecord?> FindAsync(string? tenantSlug, string username, CancellationToken cancellationToken);

    Task RegisterFailureAsync(Guid userId, int maxFailures, TimeSpan lockout, CancellationToken cancellationToken);

    Task RegisterSuccessAsync(Guid userId, CancellationToken cancellationToken);
}

public interface IPasswordService
{
    string Hash(string password);

    bool Verify(string hash, string password);

    /// <summary>Burns the same CPU as a real check so unknown users are not distinguishable by timing.</summary>
    void VerifyDummy(string password);
}

public interface IAccessTokenIssuer
{
    AccessToken Issue(LoginRecord user);
}

public interface IPasswordStore
{
    Task<string?> GetHashAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Stores the new hash, clears the must-change flag and any lock.</summary>
    Task SetAsync(Guid userId, string newHash, CancellationToken cancellationToken);
}
