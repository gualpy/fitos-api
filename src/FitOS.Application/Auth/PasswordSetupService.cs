using System.Security.Cryptography;
using FitOS.Application.Email;

namespace FitOS.Application.Auth;

/// <summary>Where the front lives and how long a "create your password" link works.</summary>
public sealed record SetupLinkPolicy(string PublicUrl, TimeSpan Validity);

public enum SetupRedeemStatus
{
    Ok,
    Invalid,
    Expired,
}

public sealed record SetupRedeem(SetupRedeemStatus Status, Guid? UserId = null, Guid? TenantId = null);

public interface IPasswordSetupStore
{
    /// <summary>Stores the hash of a new link and voids the user's earlier unused links.</summary>
    Task IssueAsync(Guid userId, byte[] tokenHash, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>Marks the link as used and sets the password in one step; also closes the user's open sessions.</summary>
    Task<SetupRedeem> RedeemAsync(byte[] tokenHash, string passwordHash, CancellationToken cancellationToken);
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
        await store.IssueAsync(to.UserId, Hash(token), clock.GetUtcNow().Add(policy.Validity), cancellationToken);

        var link = $"{policy.PublicUrl.TrimEnd('/')}/#setup={token}";
        var hours = (int)policy.Validity.TotalHours;
        await email.SendAsync(
            new EmailMessage(
                to.Email,
                $"Bienvenido a FitOS: crea tu contraseña ({to.GymName})",
                $"""
                Hola {to.DisplayName},

                Tu gimnasio "{to.GymName}" ya está listo en FitOS.

                Gimnasio (para iniciar sesión): {to.GymSlug}
                Usuario: {to.Username}

                Crea tu contraseña con este enlace (sirve una sola vez y vence en {hours} horas):
                {link}

                Si no esperabas este correo, ignóralo.
                """),
            cancellationToken);
    }

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
