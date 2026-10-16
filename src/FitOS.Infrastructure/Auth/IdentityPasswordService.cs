using FitOS.Application.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace FitOS.Infrastructure.Auth;

/// <summary>
/// PBKDF2-HMAC-SHA512 with a per-hash salt (ASP.NET Core Identity hasher). The iteration count travels inside every hash,
/// so raising it never breaks existing passwords: an older hash still verifies and is replaced by a stronger one at the
/// next successful login (<see cref="PasswordCheck.SuccessRehashNeeded"/>).
/// </summary>
public sealed class IdentityPasswordService : IPasswordService
{
    /// <summary>OWASP's current floor for PBKDF2-HMAC-SHA512 (the framework default, 100 000, is below it).</summary>
    public const int DefaultIterations = 210_000;

    private static readonly object NoUser = new();

    private readonly PasswordHasher<object> _hasher;

    private readonly string _dummyHash;

    public IdentityPasswordService(int iterations = DefaultIterations)
    {
        _hasher = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions { IterationCount = iterations }));
        _dummyHash = _hasher.HashPassword(NoUser, Guid.NewGuid().ToString("N"));
    }

    public string Hash(string password) => _hasher.HashPassword(NoUser, password);

    public bool Verify(string hash, string password) => Check(hash, password) != PasswordCheck.Failed;

    public PasswordCheck Check(string hash, string password)
    {
        try
        {
            return _hasher.VerifyHashedPassword(NoUser, hash, password) switch
            {
                PasswordVerificationResult.Success => PasswordCheck.Success,
                PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
                _ => PasswordCheck.Failed,
            };
        }
        catch (FormatException)
        {
            return PasswordCheck.Failed; // a corrupted stored hash must reject the login, not crash it
        }
    }

    public void VerifyDummy(string password) => _hasher.VerifyHashedPassword(NoUser, _dummyHash, password);
}
