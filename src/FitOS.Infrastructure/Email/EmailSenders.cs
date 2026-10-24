using System.ComponentModel.DataAnnotations;
using System.Text;
using FitOS.Application.Auth;
using FitOS.Application.Email;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace FitOS.Infrastructure.Email;

/// <summary>
/// FALLBACK mail server settings (environment/user-secrets), used only when the database has none or they fail.
/// The password is a secret: user-secrets in development, an environment variable in production.
/// </summary>
public sealed class EmailOptions
{
    public const string Section = "Email";

    public string Host { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; init; } = 587;

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    /// <summary>Address the messages come from (it must be the mailbox itself with most providers).</summary>
    public string From { get; init; } = string.Empty;

    public string FromName { get; init; } = EmailSettingsService.DefaultFromName;

    /// <summary><c>ssl_on_connect</c> (port 465) or <c>start_tls</c> (port 587).</summary>
    public string Security { get; init; } = "start_tls";

    [Range(1, 120)]
    public int TimeoutSeconds { get; init; } = 20;

    public bool IsConfigured => Host.Length > 0 && From.Length > 0;
}

/// <summary>Speaks SMTP with MailKit: TLS from the first byte (465) or STARTTLS (587), never plain text.</summary>
public sealed class MailKitDelivery(IOptions<EmailOptions> options) : ISmtpDelivery
{
    public async Task SendAsync(SmtpSettings settings, EmailMessage message, CancellationToken cancellationToken)
    {
        var mime = Build(settings, message);

        using var client = new SmtpClient { Timeout = options.Value.TimeoutSeconds * 1000 };
        try
        {
            var tls = settings.Security == SmtpSecurity.SslOnConnect ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
            await client.ConnectAsync(settings.Host, settings.Port, tls, cancellationToken);
            if (settings.Username.Length > 0)
            {
                await client.AuthenticateAsync(settings.Username, settings.Password, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(true, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EmailDeliveryException("The mail server did not accept the message.", ReasonOf(ex), ex);
        }
    }

    /// <exception cref="EmailDeliveryException">The recipient address cannot be parsed.</exception>
    internal static MimeMessage Build(SmtpSettings settings, EmailMessage message)
    {
        try
        {
            var mime = new MimeMessage();
            mime.From.Add(new MailboxAddress(CleanName(message.FromName ?? settings.FromName), settings.FromAddress));
            mime.To.Add(MailboxAddress.Parse(message.To));
            mime.Subject = CleanLine(message.Subject);
            mime.Body = new BodyBuilder { TextBody = message.Body, HtmlBody = message.Html }.ToMessageBody();
            return mime;
        }
        catch (Exception ex) when (ex is ParseException or FormatException or ArgumentException)
        {
            throw new EmailDeliveryException("The address is not valid.", "invalid_address", ex);
        }
    }

    private static string ReasonOf(Exception ex) => ex switch
    {
        MailKit.Security.AuthenticationException => "auth_failed",
        SslHandshakeException or System.Security.Authentication.AuthenticationException => "tls_failed",
        SmtpCommandException { ErrorCode: SmtpErrorCode.SenderNotAccepted or SmtpErrorCode.RecipientNotAccepted or SmtpErrorCode.MessageNotAccepted } => "rejected",
        SmtpProtocolException or SmtpCommandException or ServiceNotConnectedException or ServiceNotAuthenticatedException => "failed",
        _ when ex is IOException or TimeoutException or OperationCanceledException or System.Net.Sockets.SocketException => "connection_failed",
        _ => "failed",
    };

    /// <summary>A display name is free text (a gym's name): no line breaks (header injection), no control characters, bounded.</summary>
    internal static string CleanName(string? name)
    {
        var cleaned = CleanLine(name ?? string.Empty);
        return cleaned.Length == 0 ? EmailSettingsService.DefaultFromName : cleaned.Length <= 80 ? cleaned : cleaned[..80];
    }

    private static string CleanLine(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(char.IsControl(c) ? ' ' : c);
        }

        return sb.ToString().Trim();
    }
}

/// <summary>
/// The only <see cref="IEmailSender"/>: settings saved in the database first; if there are none (or they fail) the
/// environment's. In Development with neither, the message goes to the log so the flow can be tried.
/// </summary>
public sealed partial class ConfiguredEmailSender(
    IEmailSettingsStore store,
    EmailSettingsService settings,
    ISmtpDelivery delivery,
    IOptions<EmailOptions> options,
    ISecurityLog securityLog,
    ILogger<ConfiguredEmailSender> logger,
    bool logInsteadOfSending) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var fallback = FromEnvironment(options.Value);
        var stored = await store.GetAsync(cancellationToken);
        var fromDatabase = stored is null ? null : settings.Decrypt(stored, EmailSettingsService.DefaultFromName);

        if (fromDatabase is not null)
        {
            try
            {
                await delivery.SendAsync(fromDatabase, message, cancellationToken);
                return;
            }
            catch (EmailDeliveryException ex) when (fallback is not null)
            {
                // A wrong password saved in the panel must never lock the platform administrator out of their own login.
                await securityLog.WriteAsync(
                    new SecurityEvent("email_settings_fallback", Detail: new Dictionary<string, object?> { ["reason"] = ex.Reason }),
                    cancellationToken);
            }
        }
        else if (stored is not null)
        {
            LogUnreadable(logger);
        }

        if (fallback is not null)
        {
            await delivery.SendAsync(fallback, message, cancellationToken);
            return;
        }

        if (logInsteadOfSending && fromDatabase is null)
        {
            LogMessage(logger, message.To, message.Subject, message.Body);
            return;
        }

        throw new EmailDeliveryException("No mail server is configured.", "not_configured");
    }

    private static SmtpSettings? FromEnvironment(EmailOptions o) =>
        o.IsConfigured
            ? new SmtpSettings(o.Host, o.Port, SmtpSecurityWire.TryParse(o.Security, out var security) ? security : SmtpSecurity.StartTls, o.Username, o.Password, o.From, o.FromName)
            : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "E-mail NOT sent (no mail server configured). To: {To}. Subject: {Subject}.\n{Body}")]
    private static partial void LogMessage(ILogger logger, string to, string subject, string body);

    [LoggerMessage(Level = LogLevel.Error, Message = "The mail settings saved in the database cannot be decrypted (was Secrets:EncryptionKey changed?). Save them again from the panel.")]
    private static partial void LogUnreadable(ILogger logger);
}
