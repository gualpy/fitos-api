using System.Security.Cryptography;
using FitOS.Application.Email;
using FitOS.Domain.Tenancy;

namespace FitOS.Application.Auth;

/// <summary>Where the front lives and how long a "create your password" link works.</summary>
/// <param name="Validity">How long a welcome link works.</param>
/// <param name="ResetValidity">How long a "forgot my password" link works (shorter: it is asked for on the spot).</param>
/// <param name="ResetMinGap">The least time between two reset e-mails for the same account.</param>
public sealed record SetupLinkPolicy(string PublicUrl, TimeSpan Validity, TimeSpan ResetValidity, TimeSpan ResetMinGap);

public enum SetupRedeemStatus
{
    Ok,
    Invalid,
    Expired,
}

public sealed record SetupRedeem(SetupRedeemStatus Status, Guid? UserId = null, Guid? TenantId = null);

public interface IPasswordSetupStore
{
    /// <summary>
    /// Stores the hash of a new link and voids the user's earlier unused links. <paramref name="purpose"/> is
    /// <c>welcome</c> or <c>reset</c>; with a <paramref name="minGap"/> no new link is issued while a recent one exists.
    /// </summary>
    /// <returns><c>false</c> when nothing was issued because of the minimum gap.</returns>
    Task<bool> IssueAsync(Guid userId, byte[] tokenHash, DateTimeOffset expiresAt, string purpose, TimeSpan minGap, CancellationToken cancellationToken);

    /// <summary>Marks the link as used and sets the password in one step; also closes the user's open sessions.</summary>
    Task<SetupRedeem> RedeemAsync(byte[] tokenHash, string passwordHash, CancellationToken cancellationToken);

    /// <summary>Who the link is for and which gym, without spending it. <c>null</c> = invalid, used or expired.</summary>
    Task<SetupInfo?> InfoAsync(byte[] tokenHash, CancellationToken cancellationToken);
}

/// <param name="GymName"><c>null</c> for accounts that belong to no gym.</param>
/// <param name="Purpose"><c>welcome</c> (first password) or <c>reset</c> (forgotten password).</param>
public sealed record SetupInfo(string DisplayName, string? GymName, string? GymSlug, string Purpose);

/// <summary>An account that asked to restore its password.</summary>
public sealed record ResetTarget(Guid UserId, Guid TenantId, string DisplayName, string? Email, UserRole Role, bool IsActive, string? TenantStatus, string GymName, string GymSlug);

public interface IPasswordResetStore
{
    /// <param name="identifier">E-mail (has a <c>@</c>) or username.</param>
    Task<ResetTarget?> FindAsync(string tenantSlug, string identifier, CancellationToken cancellationToken);
}

public sealed record WelcomeRecipient(Guid UserId, string Email, string DisplayName, string Username, string GymName, string GymSlug);

public enum SetPasswordOutcome
{
    Done,
    InvalidLink,
    ExpiredLink,
    WeakPassword,
}

/// <summary>
/// One-time links to create a password (a new gym's owner). The token travels in the URL fragment, so it never reaches a
/// server log; only its hash is stored, and a link works once.
/// </summary>
public sealed class PasswordSetupService(
    IPasswordSetupStore store,
    IPasswordService passwords,
    IEmailSender email,
    ISecurityLog log,
    SetupLinkPolicy policy,
    TimeProvider clock)
{
    public async Task SendWelcomeAsync(WelcomeRecipient to, CancellationToken cancellationToken)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        await store.IssueAsync(to.UserId, Hash(token), clock.GetUtcNow().Add(policy.Validity), "welcome", TimeSpan.Zero, cancellationToken);

        var link = $"{policy.PublicUrl.TrimEnd('/')}/#setup={token}";
        var hours = (int)policy.Validity.TotalHours;
        await email.SendAsync(
            new EmailMessage(
                to.Email,
                $"Bienvenido a {to.GymName}: crea tu contraseña",
                $"""
                Hola {to.DisplayName},

                Tu cuenta en {to.GymName} ya está lista.

                Gimnasio (para iniciar sesión): {to.GymSlug}
                Usuario: {to.Username}

                Crea tu contraseña con este enlace (sirve una sola vez y vence en {hours} horas):
                {link}

                Si no esperabas este correo, ignóralo.
                """,
                to.GymName),
            cancellationToken);
    }

    /// <summary>
    /// "I forgot my password": a one-time link, valid <see cref="SetupLinkPolicy.ResetValidity"/>, in the gym's name.
    /// Runs in the background (nothing about the outcome ever reaches the person who asked).
    /// </summary>
    public async Task SendResetAsync(ResetTarget target, CancellationToken cancellationToken)
    {
        if (target.Email is null)
        {
            return;
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var issued = await store.IssueAsync(target.UserId, Hash(token), clock.GetUtcNow().Add(policy.ResetValidity), "reset", policy.ResetMinGap, cancellationToken);
        if (!issued)
        {
            return; // asked again within seconds: the first e-mail is on its way
        }

        var link = $"{policy.PublicUrl.TrimEnd('/')}/#setup={token}";
        var minutes = (int)policy.ResetValidity.TotalMinutes;
        try
        {
            await email.SendAsync(
                new EmailMessage(
                    target.Email,
                    $"Restablece tu contraseña en {target.GymName}",
                    $"""
                    Hola {target.DisplayName},

                    Alguien pidió restablecer la contraseña de tu cuenta en {target.GymName}.

                    Elige una nueva con este enlace (sirve una sola vez y vence en {minutes} minutos):
                    {link}

                    Si no fuiste tú, ignora este correo: tu contraseña actual sigue funcionando.
                    """,
                    target.GymName),
                cancellationToken);
            await log.WriteAsync(new SecurityEvent("password_reset_sent", target.TenantId, target.UserId), cancellationToken);
        }
        catch (EmailDeliveryException)
        {
            await log.WriteAsync(new SecurityEvent("password_reset_email_failed", target.TenantId, target.UserId), cancellationToken);
        }
    }

    /// <summary>For the page that opens from the link: whose account and which gym. Spends nothing.</summary>
    public Task<SetupInfo?> InfoAsync(string token, CancellationToken cancellationToken) => store.InfoAsync(Hash(token), cancellationToken);

    public async Task<SetPasswordOutcome> SetPasswordAsync(string token, string newPassword, ClientInfo client, CancellationToken cancellationToken)
    {
        if (!ChangePasswordService.IsAcceptable(newPassword))
        {
            return SetPasswordOutcome.WeakPassword;
        }

        var redeem = await store.RedeemAsync(Hash(token), passwords.Hash(newPassword), cancellationToken);
        if (redeem.Status != SetupRedeemStatus.Ok)
        {
            await log.WriteAsync(new SecurityEvent("password_setup_failed", Client: client), cancellationToken);
            return redeem.Status == SetupRedeemStatus.Expired ? SetPasswordOutcome.ExpiredLink : SetPasswordOutcome.InvalidLink;
        }

        await log.WriteAsync(new SecurityEvent("password_set", redeem.TenantId, redeem.UserId, client), cancellationToken);
        return SetPasswordOutcome.Done;
    }

    private static byte[] Hash(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
