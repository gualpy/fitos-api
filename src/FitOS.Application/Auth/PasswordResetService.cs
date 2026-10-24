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
    /// <param name="tenantSlug">The gym the person typed.</param>
    /// <param name="platform">The request is for the platform (no gym): only platform administrators match.</param>
    public async Task RequestAsync(string? tenantSlug, bool platform, string identifier, ClientInfo client, CancellationToken cancellationToken)
    {
        var target = platform || tenantSlug is not null
            ? await store.FindAsync(platform ? null : tenantSlug, identifier, cancellationToken)
            : null;

        // A gym request never matches a platform administrator (the lookup is by gym), and a platform request matches only them.
        var eligible = target is { IsActive: true, Email: not null }
            && (platform
                ? target.Role == UserRole.PlatformAdmin
                : target.Role != UserRole.PlatformAdmin && target.TenantStatus is "active" or "trial");
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
