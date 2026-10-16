using System.Text.Json;
using FitOS.Application.Auth;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FitOS.Infrastructure.Auth;

/// <summary>
/// Writes security events through a SECURITY DEFINER function: the API can add events but cannot read or erase them.
/// Best effort by design: if the database refuses, the failure is logged and the user-facing action goes on.
/// </summary>
public sealed partial class PostgresSecurityLog(FitOsDbContext db, ILogger<PostgresSecurityLog> logger) : ISecurityLog
{
    public async Task WriteAsync(SecurityEvent securityEvent, CancellationToken cancellationToken)
    {
        try
        {
            var detail = securityEvent.Detail is { Count: > 0 } ? JsonSerializer.Serialize(securityEvent.Detail) : null;
            await db.Database.ExecuteSqlAsync(
                $"SELECT security_event_log({securityEvent.Name}, {securityEvent.TenantId}, {securityEvent.UserId}, {securityEvent.Client?.Ip}, {securityEvent.Client?.UserAgent}, {detail}::jsonb)",
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never include the event's contents here: this line goes to ordinary application logs.
            LogWriteFailed(logger, ex, securityEvent.Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record security event {Event}")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception, string @event);
}
