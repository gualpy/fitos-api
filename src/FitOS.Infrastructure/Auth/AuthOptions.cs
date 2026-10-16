using System.ComponentModel.DataAnnotations;

namespace FitOS.Infrastructure.Auth;

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    [Required]
    public string Issuer { get; init; } = string.Empty;

    [Required]
    public string Audience { get; init; } = string.Empty;

    /// <summary>HMAC key, at least 32 characters. Comes from secrets/environment, never from the repo.</summary>
    [Required, MinLength(32)]
    public string SigningKey { get; init; } = string.Empty;

    /// <summary>Access-token lifetime. Short on purpose: it cannot be revoked, so this is how long a stolen one could work.</summary>
    [Range(1, 120)]
    public int AccessTokenMinutes { get; init; } = 15;
}

public sealed class AuthOptions
{
    public const string Section = "Auth";

    [Range(1, 100)]
    public int MaxFailedAttempts { get; init; } = 5;

    [Range(1, 1440)]
    public int LockoutMinutes { get; init; } = 15;

    /// <summary>Inactivity after which a session can no longer be renewed (the renewal token's lifetime, renewed on every use).</summary>
    [Range(5, 43200)]
    public int IdleMinutes { get; init; } = 480;

    /// <summary>Absolute lifetime of a session, however active: after this the person signs in again.</summary>
    [Range(1, 720)]
    public int SessionHours { get; init; } = 12;

    /// <summary>PBKDF2 iterations for NEW password hashes. Older hashes keep verifying and are upgraded at the next login.</summary>
    [Range(1000, 5_000_000)]
    public int PasswordIterations { get; init; } = 210_000;

    /// <summary>Login requests per minute per client IP.</summary>
    [Range(1, 100000)]
    public int LoginRequestsPerMinute { get; init; } = 10;
}
