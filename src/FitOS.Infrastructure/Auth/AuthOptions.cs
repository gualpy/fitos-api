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

    /// <summary>Access-token lifetime. There is no refresh token yet, so this is also the session length.</summary>
    [Range(1, 1440)]
    public int AccessTokenMinutes { get; init; } = 60;
}

public sealed class AuthOptions
{
    public const string Section = "Auth";

    [Range(1, 100)]
    public int MaxFailedAttempts { get; init; } = 5;

    [Range(1, 1440)]
    public int LockoutMinutes { get; init; } = 15;

    /// <summary>Login requests per minute per client IP.</summary>
    [Range(1, 100000)]
    public int LoginRequestsPerMinute { get; init; } = 10;
}
