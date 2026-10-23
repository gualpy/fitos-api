namespace FitOS.Application.Email;

/// <summary>A plain-text message. The body may hold one-time links: it is never logged outside Development.</summary>
public sealed record EmailMessage(string To, string Subject, string Body);

public interface IEmailSender
{
    /// <exception cref="EmailDeliveryException">The message could not be handed to the mail server.</exception>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

public sealed class EmailDeliveryException(string message, Exception? inner = null) : Exception(message, inner);
