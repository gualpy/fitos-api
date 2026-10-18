using FitOS.Application.Common;
using FitOS.Application.Dashboard;
using Microsoft.AspNetCore.Diagnostics;

namespace FitOS.Api;

/// <summary>Turns the application's business exceptions into the API's error format; everything else stays a 500.</summary>
public sealed class ApplicationExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, error) = exception switch
        {
            NotFoundException e => (StatusCodes.Status404NotFound, (object)new { code = "not_found", message = $"{e.Entity} not found.", entity = e.Entity }),
            ConflictException e => (StatusCodes.Status409Conflict, new { code = e.Code, message = "The request conflicts with existing data." }),
            ValidationFailedException e => (StatusCodes.Status422UnprocessableEntity, new { code = "validation_failed", message = "Invalid data.", details = e.Errors }),
            InsightsUnavailableException => (StatusCodes.Status503ServiceUnavailable, new { code = "insights_unavailable", message = "Insights are not configured on this server." }),
            InsightsFailedException => (StatusCodes.Status502BadGateway, new { code = "insights_failed", message = "The insights provider failed. Try again later." }),
            _ => (0, null!),
        };

        if (status == 0)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(new { error }, cancellationToken);
        return true;
    }
}
