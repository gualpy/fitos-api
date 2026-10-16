using FitOS.Application.Auth;
using Microsoft.AspNetCore.Identity;

namespace FitOS.Infrastructure.Auth;

/// <summary>PBKDF2-HMAC-SHA512 with per-hash salt and a versioned format (ASP.NET Core Identity hasher).</summary>
public sealed class IdentityPasswordService : IPasswordService
{
    private static readonly object NoUser = new();

    private readonly PasswordHasher<object> _hasher = new();

    private readonly string _dummyHash;

    public IdentityPasswordService()
    {
        _dummyHash = _hasher.HashPassword(NoUser, Guid.NewGuid().ToString("N"));
    }

    public string Hash(string password) => _hasher.HashPassword(NoUser, password);

    public bool Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword(NoUser, hash, password) != PasswordVerificationResult.Failed;

    public void VerifyDummy(string password) => _hasher.VerifyHashedPassword(NoUser, _dummyHash, password);
}
