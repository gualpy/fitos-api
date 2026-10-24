using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FitOS.Application.Auth;
using FitOS.Application.Common;
using FitOS.Application.Secrets;

namespace FitOS.Application.Email;

public enum SmtpSecurity
{
    /// <summary>TLS from the first byte (port 465).</summary>
    SslOnConnect,

    /// <summary>Plain greeting, then upgrade to TLS (port 587).</summary>
    StartTls,
}

public static class SmtpSecurityWire
{
    public static string ToWire(this SmtpSecurity security) => security == SmtpSecurity.SslOnConnect ? "ssl_on_connect" : "start_tls";

    public static bool TryParse(string? wire, out SmtpSecurity security)
    {
        security = wire switch { "ssl_on_connect" => SmtpSecurity.SslOnConnect, _ => SmtpSecurity.StartTls };
        return wire is "ssl_on_connect" or "start_tls";
    }
}

/// <summary>Everything needed to talk to a mail server, password in clear (it only exists in memory, for one delivery).</summary>
public sealed record SmtpSettings(string Host, int Port, SmtpSecurity Security, string Username, string Password, string FromAddress, string FromName);

public sealed record StoredEmailSettings(string Host, int Port, SmtpSecurity Security, string Username, string FromAddress, byte[]? PasswordEnc, string? PasswordHint, DateTimeOffset UpdatedAt);

public interface IEmailSettingsStore
{
    Task<StoredEmailSettings?> GetAsync(CancellationToken cancellationToken);

    /// <param name="passwordEnc"><c>null</c> keeps the stored password.</param>
    Task SaveAsync(string host, int port, SmtpSecurity security, string username, string fromAddress, byte[]? passwordEnc, string? passwordHint, Guid actorId, CancellationToken cancellationToken);
}

/// <summary>The one place that speaks SMTP.</summary>
public interface ISmtpDelivery
{
    /// <exception cref="EmailDeliveryException">The server refused or could not be reached.</exception>
    Task SendAsync(SmtpSettings settings, EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>What the panel shows: never the password, only its last characters.</summary>
public sealed record EmailSettingsView(bool Configured, string Host, int Port, string Security, string Username, string FromAddress, string? PasswordHint, DateTimeOffset? UpdatedAt);

public sealed record EmailSettingsInput(string? Host, int? Port, string? Security, string? Username, string? FromAddress, string? Password);

/// <summary>Mail server settings kept in the database. Platform administrators only (enforced at the API).</summary>
public sealed partial class EmailSettingsService(
    IEmailSettingsStore store,
    ISecretProtector secrets,
    ISmtpDelivery delivery,
    ISecurityLog log)
{
    /// <summary>The password is bound to this fixed scope instead of a gym: the settings belong to the whole system.</summary>
    public static readonly Guid Scope = Guid.Empty;

    public const string DefaultFromName = "FitOS";

    public async Task<EmailSettingsView> GetAsync(CancellationToken cancellationToken)
    {
        var s = await store.GetAsync(cancellationToken);
        return s is null
            ? new EmailSettingsView(false, string.Empty, 587, SmtpSecurity.StartTls.ToWire(), string.Empty, string.Empty, null, null)
            : new EmailSettingsView(true, s.Host, s.Port, s.Security.ToWire(), s.Username, s.FromAddress, MaskOf(s.PasswordHint), s.UpdatedAt);
    }

    public async Task SaveAsync(EmailSettingsInput input, Guid actorId, CancellationToken cancellationToken)
    {
        var current = await store.GetAsync(cancellationToken);
        var errors = new ErrorBag();
        var host = (input.Host ?? string.Empty).Trim();
        var from = (input.FromAddress ?? string.Empty).Trim();
        var username = (input.Username ?? string.Empty).Trim();

        if (host.Length == 0)
        {
            errors.Add("host", "required");
        }
        else if (host.Length > 255 || !HostPattern().IsMatch(host))
        {
            errors.Add("host", "invalid");
        }

        if (input.Port is null or < 1 or > 65535)
        {
            errors.Add("port", "invalid");
        }

        if (!SmtpSecurityWire.TryParse(input.Security, out var security))
        {
            errors.Add("security", "invalid");
        }

        if (username.Length > 254)
        {
            errors.Add("username", "too_long");
        }

        if (from.Length == 0)
        {
            errors.Add("fromAddress", "required");
        }
        else if (from.Length > 254 || !EmailPattern().IsMatch(from))
        {
            errors.Add("fromAddress", "invalid");
        }

        var password = input.Password ?? string.Empty;
        if (password.Length == 0 && current?.PasswordEnc is null)
        {
            errors.Add("password", "required");
        }
        else if (password.Length > 200)
        {
            errors.Add("password", "too_long");
        }

        errors.ThrowIfAny();

        byte[]? encrypted = null;
        string? hint = null;
        if (password.Length > 0)
        {
            encrypted = secrets.Protect(password, Scope);
            hint = password.Length <= 4 ? new string('•', password.Length) : password[^4..];
        }

        await store.SaveAsync(host, input.Port!.Value, security, username, from, encrypted, hint, actorId, cancellationToken);
        await log.WriteAsync(new SecurityEvent("email_settings_changed", UserId: actorId), cancellationToken);
    }

    /// <summary>Sends through the SAVED settings (never the fallback), so the operator learns whether they work.</summary>
    public async Task SendTestAsync(string? to, Guid actorId, CancellationToken cancellationToken)
    {
        var address = (to ?? string.Empty).Trim();
        if (!EmailPattern().IsMatch(address) || address.Length > 254)
        {
            throw new ValidationFailedException("to", "invalid");
        }

        var stored = await store.GetAsync(cancellationToken) ?? throw new ConflictException("email_not_configured");
        var settings = Decrypt(stored, DefaultFromName) ?? throw new ConflictException("email_password_unreadable");

        await delivery.SendAsync(
            settings,
            new EmailCard(
                "FitOS",
                "La configuración de correo funciona",
                ["Si lees este mensaje, FitOS puede enviar correos con la configuración guardada en el panel."],
                "Mensaje de prueba. No hace falta responder.",
                [new EmailDetail("Servidor", settings.Host), new EmailDetail("Puerto", settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)), new EmailDetail("Remitente", settings.FromAddress)])
                .ToMessage(address, "FitOS: prueba de correo"),
            cancellationToken);
        await log.WriteAsync(new SecurityEvent("email_test_sent", UserId: actorId), cancellationToken);
    }

    /// <summary>Opens the stored password; <c>null</c> when it cannot be read (key rotated, data damaged).</summary>
    public SmtpSettings? Decrypt(StoredEmailSettings stored, string fromName)
    {
        if (stored.PasswordEnc is null)
        {
            return null;
        }

        try
        {
            return new SmtpSettings(stored.Host, stored.Port, stored.Security, stored.Username, secrets.Unprotect(stored.PasswordEnc, Scope), stored.FromAddress, fromName);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static string? MaskOf(string? hint) => hint is null ? null : "••••" + hint;

    [GeneratedRegex("^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$")]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}
