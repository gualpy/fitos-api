using System.Security.Cryptography;
using FitOS.Application.Email;

namespace FitOS.Application.Auth;

/// <param name="Required">False only in Development (Auth:SkipPlatformConfirmation); every other environment ignores that switch.</param>
public sealed record LoginChallengePolicy(string PublicUrl, TimeSpan Validity, bool Required);

/// <param name="Secret">Stays in the browser that logged in; the e-mailed link is useless without it.</param>
public sealed record LoginChallenge(string Secret, DateTimeOffset ExpiresAt);

public enum ChallengeRedeemStatus
{
    Ok,
    Invalid,
    Expired,
}

public sealed record ChallengeRedeem(ChallengeRedeemStatus Status, Guid? UserId = null);

public interface ILoginChallengeStore
{
    /// <returns>The e-mail to send the link to, or <c>null</c> when the account has none (nothing was stored).</returns>
    Task<string?> CreateAsync(Guid userId, byte[] linkHash, byte[] secretHash, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    Task<ChallengeRedeem> RedeemAsync(byte[] linkHash, byte[] secretHash, CancellationToken cancellationToken);
}

/// <summary>
/// Second step for platform administrators: after the right password a one-time link goes to their e-mail. Finishing the
/// login needs BOTH the link (e-mail) and the secret returned to the browser that asked, so neither alone is enough.
/// </summary>
public sealed class LoginChallengeService(ILoginChallengeStore store, IEmailSender email, ISecurityLog log, LoginChallengePolicy policy, TimeProvider clock)
{
    public bool Required => policy.Required;

    /// <returns>The challenge for the browser, or <c>null</c> when the account has no e-mail on file.</returns>
    public async Task<LoginChallenge?> BeginAsync(LoginRecord user, ClientInfo client, CancellationToken cancellationToken)
    {
        var link = Token();
        var secret = Token();
        var expiresAt = clock.GetUtcNow().Add(policy.Validity);

        var to = await store.CreateAsync(user.UserId, Hash(link), Hash(secret), expiresAt, cancellationToken);
        if (to is null)
        {
            await log.WriteAsync(new SecurityEvent("login_confirmation_unavailable", user.TenantId, user.UserId, client), cancellationToken);
            return null;
        }

        var minutes = (int)policy.Validity.TotalMinutes;
        var card = new EmailCard(
            "FitOS",
            "Confirma que eres tú",
            [$"Hola {user.DisplayName},", "Alguien escribió tu usuario y contraseña de administrador de FitOS."],
            "Mensaje de seguridad de FitOS.",
            [new EmailDetail("Dirección IP", client.Ip ?? "desconocida"), new EmailDetail("Navegador", Shorten(client.UserAgent))],
            "Confirmar inicio de sesión",
            $"{policy.PublicUrl.TrimEnd('/')}/#confirm={link}",
            Warning: $"Abre el enlace **en el mismo navegador** donde iniciaste sesión. Sirve una sola vez y vence en {minutes} minutos.",
            Note: "Si no fuiste tú, no abras el enlace y cambia tu contraseña.");
        await email.SendAsync(card.ToMessage(to, "FitOS: confirma tu inicio de sesión"), cancellationToken);

        await log.WriteAsync(new SecurityEvent("login_confirmation_sent", user.TenantId, user.UserId, client), cancellationToken);
        return new LoginChallenge(secret, expiresAt);
    }

    public async Task<ChallengeRedeem> RedeemAsync(string link, string secret, ClientInfo client, CancellationToken cancellationToken)
    {
        var redeem = await store.RedeemAsync(Hash(link), Hash(secret), cancellationToken);
        if (redeem.Status != ChallengeRedeemStatus.Ok)
        {
            await log.WriteAsync(new SecurityEvent("login_confirmation_failed", Client: client), cancellationToken);
        }

        return redeem;
    }

    private static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Hash(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    private static string Shorten(string? userAgent) =>
        string.IsNullOrWhiteSpace(userAgent) ? "desconocido" : userAgent.Length <= 120 ? userAgent : userAgent[..120];
}
