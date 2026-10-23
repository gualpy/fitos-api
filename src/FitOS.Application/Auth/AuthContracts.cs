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

public sealed record LoginUser(Guid Id, Guid? TenantId, string DisplayName, UserRole Role, bool MustChangePassword = false, Guid? CustomerId = null)
{
    public static LoginUser From(LoginRecord user) =>
        new(user.UserId, user.TenantId, user.DisplayName, user.Role, user.MustChangePassword, user.CustomerId);
}

/// <summary>Opaque renewal token (shown once, only its hash is stored) and when it stops being valid.</summary>
public sealed record RefreshGrant(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Deliberately has no reason: every failure looks the same to the caller. The one exception is
/// <see cref="GymInactive"/>, reported only AFTER the password was proven right, so it reveals nothing to a guesser.
/// </summary>
public sealed record LoginResult(AccessToken? Token, LoginUser? User, RefreshGrant? Refresh = null, bool GymInactive = false)
{
    public bool Succeeded => Token is not null;

    public static LoginResult Failed { get; } = new(null, null);

    /// <summary>Right credentials, but the gym's subscription ended or it was suspended.</summary>
    public static LoginResult GymSuspended { get; } = new(null, null, null, true);
}

public enum RefreshStatus
{
    Ok,
    Invalid,
    Expired,

    /// <summary>A token that was already used came back: it was copied. The whole session was revoked.</summary>
    Reuse,

    /// <summary>The same token arrived twice within seconds (one client, two requests). Refused, session kept.</summary>
    Concurrent,

    /// <summary>The user was deactivated or the gym suspended since the last refresh.</summary>
    Inactive,
}

public sealed record RefreshOutcome(RefreshStatus Status, LoginRecord? User = null, Guid? SessionId = null, RefreshGrant? Refresh = null, Guid? UserId = null);

public interface ISessionStore
{
    /// <summary>Starts a session and returns its first renewal token.</summary>
    Task<(Guid SessionId, RefreshGrant Refresh)> CreateAsync(Guid userId, string? userAgent, CancellationToken cancellationToken);

    /// <summary>Exchanges a renewal token for a new one (rotation). The presented value is never stored.</summary>
    Task<RefreshOutcome> RotateAsync(string presentedRefreshToken, CancellationToken cancellationToken);

    Task RevokeAsync(Guid sessionId, string reason, CancellationToken cancellationToken);
}

public interface ILoginStore
{
    /// <param name="tenantSlug"><c>null</c> = platform administrators.</param>
    Task<LoginRecord?> FindAsync(string? tenantSlug, string username, CancellationToken cancellationToken);

    /// <returns><c>true</c> when THIS failure is the one that locked the account.</returns>
    Task<bool> RegisterFailureAsync(Guid userId, int maxFailures, TimeSpan lockout, CancellationToken cancellationToken);

    Task RegisterSuccessAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Replaces the stored hash with a stronger one. Does not touch sessions or any other field.</summary>
    Task UpgradePasswordHashAsync(Guid userId, string newHash, CancellationToken cancellationToken);
}

public enum PasswordCheck
{
    Failed,
    Success,

    /// <summary>Correct, but the stored hash uses fewer iterations than today's setting: store a fresh one.</summary>
    SuccessRehashNeeded,
}

/// <summary>Where a request came from. Used only to write the security log; never to decide access.</summary>
public sealed record ClientInfo(string? Ip, string? UserAgent)
{
    public static ClientInfo Unknown { get; } = new(null, null);
}

/// <summary>What happened, not the secrets involved: never a password, never the user name that was typed.</summary>
public sealed record SecurityEvent(
    string Name,
    Guid? TenantId = null,
    Guid? UserId = null,
    ClientInfo? Client = null,
    IReadOnlyDictionary<string, object?>? Detail = null);

public interface ISecurityLog
{
    /// <summary>Best effort: failing to record an event must never stop a login, a logout or a password change.</summary>
    Task WriteAsync(SecurityEvent securityEvent, CancellationToken cancellationToken);
}

public interface IPasswordService
{
    string Hash(string password);

    bool Verify(string hash, string password);

    PasswordCheck Check(string hash, string password);

    /// <summary>Burns the same CPU as a real check so unknown users are not distinguishable by timing.</summary>
    void VerifyDummy(string password);
}

public interface IAccessTokenIssuer
{
    /// <param name="sessionId">Goes into the token (<c>sid</c>) so logout can revoke exactly this session.</param>
    AccessToken Issue(LoginRecord user, Guid sessionId);
}

public interface IPasswordStore
{
    Task<string?> GetHashAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Stores the new hash, clears the must-change flag and any lock.</summary>
    Task SetAsync(Guid userId, string newHash, CancellationToken cancellationToken);
}
