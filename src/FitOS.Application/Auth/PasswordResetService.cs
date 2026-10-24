using FitOS.Application.Common;
using FitOS.Domain.Tenancy;

namespace FitOS.Application.Auth;

/// <summary>
/// Runs work after the response has gone, in its own scope. Used where the time a task takes must not tell the caller
/// anything (an e-mail that is sent only for accounts that exist).
/// </summary>
public interface IBackgroundWork
{
    void Enqueue<TService>(Func<TService, CancellationToken, Task> work)
        where TService : notnull;
}

/// <summary>
/// "I forgot my password". The answer is always the same, whether the account exists or not, and the e-mail (when there is
/// one to send) goes out in the background, so neither the reply nor its timing reveals which accounts exist.
/// </summary>
public sealed class PasswordResetService(IPasswordResetStore store, IBackgroundWork background, ISecurityLog log)
{
    public async Task RequestAsync(string? tenantSlug, string identifier, ClientInfo client, CancellationToken cancellationToken)
    {
        // Platform administrators are not reset by e-mail (their login already depends on it); the Migrator does that.
        var target = tenantSlug is null ? null : await store.FindAsync(tenantSlug, identifier, cancellationToken);

        var eligible = target is { IsActive: true, Email: not null, Role: not UserRole.PlatformAdmin }
            && target.TenantStatus is "active" or "trial";
        if (!eligible || target is null)
        {
            // The typed identifier is deliberately not logged: people sometimes type their password in that field.
            await log.WriteAsync(new SecurityEvent("password_reset_ignored", Client: client), cancellationToken);
            return;
        }

        await log.WriteAsync(new SecurityEvent("password_reset_requested", target.TenantId, target.UserId, client), cancellationToken);
        background.Enqueue<PasswordSetupService>((setup, ct) => setup.SendResetAsync(target, ct));
    }
}
