using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Mail;
using FitOS.Application.Email;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FitOS.Infrastructure.Email;

/// <summary>
/// Mail server settings. The password is a secret: user-secrets in development, environment variables in production.
/// Leave <see cref="Host"/> empty only in Development, where messages are written to the log instead.
/// </summary>
public sealed class EmailOptions
{
    public const string Section = "Email";

    public string Host { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; init; } = 587;

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    /// <summary>Address the messages come from (with Gmail it must be the account itself).</summary>
    public string From { get; init; } = string.Empty;

    public string FromName { get; init; } = "FitOS";

    /// <summary>STARTTLS (587) or implicit TLS (465) is always used; never plain text.</summary>
    public bool EnableSsl { get; init; } = true;

    [Range(1, 120)]
    public int TimeoutSeconds { get; init; } = 20;

    public bool IsConfigured => Host.Length > 0 && From.Length > 0;
}

public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        try
        {
            using var mail = new MailMessage
            {
                From = new MailAddress(settings.From, settings.FromName),
                Subject = message.Subject,
                Body = message.Body,
                IsBodyHtml = false,
            };
            mail.To.Add(message.To);

            using var client = new SmtpClient(settings.Host, settings.Port)
            {
                EnableSsl = settings.EnableSsl,
                Timeout = settings.TimeoutSeconds * 1000,
                Credentials = settings.Username.Length > 0 ? new NetworkCredential(settings.Username, settings.Password) : null,
            };
            await client.SendMailAsync(mail, cancellationToken);
        }
        catch (Exception ex) when (ex is SmtpException or FormatException or InvalidOperationException or IOException)
        {
            throw new EmailDeliveryException("The mail server did not accept the message.", ex);
        }
    }
}

/// <summary>DEVELOPMENT ONLY: nothing is sent; the message (links included) goes to the log so the flow can be tested.</summary>
public sealed partial class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        LogMessage(logger, message.To, message.Subject, message.Body);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "E-mail NOT sent (no mail server configured). To: {To}. Subject: {Subject}.\n{Body}")]
    private static partial void LogMessage(ILogger logger, string to, string subject, string body);
}
