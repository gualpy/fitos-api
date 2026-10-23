namespace FitOS.Application.Email;

/// <summary>A plain-text message. The body may hold one-time links: it is never logged outside Development.</summary>
/// <param name="FromName">The sender's display name (a gym's name, say). The address always stays the configured mailbox.</param>
public sealed record EmailMessage(string To, string Subject, string Body, string? FromName = null);

public interface IEmailSender
{
    /// <exception cref="EmailDeliveryException">The message could not be handed to the mail server.</exception>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <param name="Reason">Stable code: auth_failed, tls_failed, connection_failed, rejected, invalid_address, not_configured or failed.</param>
public sealed class EmailDeliveryException(string message, string reason = "failed", Exception? inner = null) : Exception(message, inner)
{
    public string Reason { get; } = reason;
}
