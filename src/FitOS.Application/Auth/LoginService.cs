namespace FitOS.Application.Auth;

/// <summary>
/// Username + password login. Unknown user, wrong password, inactive user, suspended tenant and locked
/// account all return <see cref="LoginResult.Failed"/> after comparable work, so the response never
/// reveals which accounts exist. A locked account is not even password-checked, so a lock cannot be
/// brute-forced.
/// </summary>
public sealed class LoginService(ILoginStore store, IPasswordService passwords, IAccessTokenIssuer tokens, LoginPolicy policy)
{
    public async Task<LoginResult> LoginAsync(string? tenantSlug, string username, string password, CancellationToken cancellationToken)
    {
        var user = await store.FindAsync(tenantSlug, username, cancellationToken);
        if (user is null)
        {
            passwords.VerifyDummy(password);
            return LoginResult.Failed;
        }

        if (user.IsLocked)
        {
            passwords.VerifyDummy(password);
            return LoginResult.Failed;
        }

        if (!passwords.Verify(user.PasswordHash, password))
        {
            await store.RegisterFailureAsync(user.UserId, policy.MaxFailures, policy.Lockout, cancellationToken);
            return LoginResult.Failed;
        }

        // Correct password, but the account may not log in. Not counted as a guessing attempt.
        if (!user.IsActive || (user.TenantId is not null && user.TenantStatus is not ("active" or "trial")))
        {
            return LoginResult.Failed;
        }

        await store.RegisterSuccessAsync(user.UserId, cancellationToken);
        return new LoginResult(tokens.Issue(user), new LoginUser(user.UserId, user.TenantId, user.DisplayName, user.Role, user.MustChangePassword, user.CustomerId));
    }
}
