using FitOS.Domain.Tenancy;

namespace FitOS.Application.Auth;

/// <summary>
/// Username + password login. Unknown user, wrong password, inactive user, suspended tenant (told apart only after a right password) and locked
/// account all return <see cref="LoginResult.Failed"/> after comparable work, so the response never
/// reveals which accounts exist. A locked account is not even password-checked, so a lock cannot be
/// brute-forced. Every outcome is recorded in the security log (without secrets).
/// </summary>
public sealed class LoginService(
    ILoginStore store,
    IPasswordService passwords,
    IAccessTokenIssuer tokens,
    ISessionStore sessions,
    ISecurityLog log,
    LoginPolicy policy,
    LoginChallengeService challenges)
{
    public async Task<LoginResult> LoginAsync(string? tenantSlug, string username, string password, ClientInfo client, CancellationToken cancellationToken)
    {
        var user = await store.FindAsync(tenantSlug, username, cancellationToken);
        if (user is null)
        {
            passwords.VerifyDummy(password);
            // The typed user name is deliberately NOT recorded: people sometimes type their password in that field.
            await FailedAsync(null, "unknown_user", client, cancellationToken, tenantSlug);
            return LoginResult.Failed;
        }

        if (user.IsLocked)
        {
            passwords.VerifyDummy(password);
            await FailedAsync(user, "locked", client, cancellationToken);
            return LoginResult.Failed;
        }

        var check = passwords.Check(user.PasswordHash, password);
        if (check == PasswordCheck.Failed)
        {
            var lockedNow = await store.RegisterFailureAsync(user.UserId, policy.MaxFailures, policy.Lockout, cancellationToken);
            await FailedAsync(user, "bad_password", client, cancellationToken);
            if (lockedNow)
            {
                await log.WriteAsync(
                    new SecurityEvent("account_locked", user.TenantId, user.UserId, client, new Dictionary<string, object?> { ["minutes"] = (int)policy.Lockout.TotalMinutes }),
                    cancellationToken);
            }

            return LoginResult.Failed;
        }

        // Correct password, but the account may not log in. Not counted as a guessing attempt.
        if (!user.IsActive)
        {
            await FailedAsync(user, "inactive", client, cancellationToken);
            return LoginResult.Failed;
        }

        if (user.TenantId is not null && user.TenantStatus is not ("active" or "trial"))
        {
            await FailedAsync(user, "tenant_inactive", client, cancellationToken);
            return LoginResult.GymSuspended;
        }

        if (check == PasswordCheck.SuccessRehashNeeded)
        {
            // We hold the right password right now: the one moment a stronger hash can be made.
            await store.UpgradePasswordHashAsync(user.UserId, passwords.Hash(password), cancellationToken);
        }

        // Platform administrators can do anything: a password alone does not get them in. The link goes to their e-mail.
        if (user.Role == UserRole.PlatformAdmin && challenges.Required)
        {
            return await challenges.BeginAsync(user, client, cancellationToken) is { } challenge
                ? LoginResult.ConfirmationRequired(challenge)
                : LoginResult.NoConfirmationEmail;
        }

        return await StartSessionAsync(user, client, cancellationToken);
    }

    /// <summary>Second step for platform administrators: the e-mailed link plus the secret the browser kept.</summary>
    public async Task<LoginResult> ConfirmAsync(string link, string secret, ClientInfo client, CancellationToken cancellationToken)
    {
        var redeem = await challenges.RedeemAsync(link, secret, client, cancellationToken);
        if (redeem.Status != ChallengeRedeemStatus.Ok || redeem.UserId is not { } userId)
        {
            return redeem.Status == ChallengeRedeemStatus.Expired ? LoginResult.LinkExpired : LoginResult.Failed;
        }

        // The account may have been deactivated or locked while the link was out.
        var user = await store.FindByIdAsync(userId, cancellationToken);
        if (user is null || !user.IsActive || user.IsLocked || user.Role != UserRole.PlatformAdmin)
        {
            await FailedAsync(user, "confirmation_refused", client, cancellationToken);
            return LoginResult.Failed;
        }

        return await StartSessionAsync(user, client, cancellationToken);
    }

    private async Task<LoginResult> StartSessionAsync(LoginRecord user, ClientInfo client, CancellationToken cancellationToken)
    {
        await store.RegisterSuccessAsync(user.UserId, cancellationToken);
        var (sessionId, refresh) = await sessions.CreateAsync(user.UserId, client.UserAgent, cancellationToken);
        await log.WriteAsync(new SecurityEvent("login_success", user.TenantId, user.UserId, client), cancellationToken);
        return new LoginResult(tokens.Issue(user, sessionId), LoginUser.From(user), refresh);
    }

    private Task FailedAsync(LoginRecord? user, string reason, ClientInfo client, CancellationToken cancellationToken, string? tenantSlug = null)
    {
        var detail = new Dictionary<string, object?> { ["reason"] = reason };
        if (tenantSlug is not null)
        {
            detail["tenantSlug"] = tenantSlug;
        }

        return log.WriteAsync(new SecurityEvent("login_failed", user?.TenantId, user?.UserId, client, detail), cancellationToken);
    }
}
