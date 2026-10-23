using System.Security.Claims;
using FitOS.Api.Auth;
using FitOS.Application.Fiscal;
using FitOS.Application.Email;
using FitOS.Application.Onboarding;
using FitOS.Application.Platform;

namespace FitOS.Api.Platform;

/// <summary>Operations of the platform administrator (not of any single gym).</summary>
public static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatform(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/platform").RequireAuthorization(AuthPolicies.PlatformAdmin);
        group.MapPost("/tenants", OnboardAsync);
        group.MapGet("/tenants", ListAsync);
        group.MapPost("/tenants/{tenantId:guid}/subscription", ExtendAsync);
        group.MapPost("/tenants/{tenantId:guid}/suspend", (Guid tenantId, ClaimsPrincipal user, PlatformService service, CancellationToken ct) => SetSuspendedAsync(tenantId, true, user, service, ct));
        group.MapPost("/tenants/{tenantId:guid}/reactivate", (Guid tenantId, ClaimsPrincipal user, PlatformService service, CancellationToken ct) => SetSuspendedAsync(tenantId, false, user, service, ct));
        group.MapPost("/tenants/{tenantId:guid}/resend-welcome", ResendWelcomeAsync);
        group.MapGet("/revenue", RevenueAsync);
        group.MapGet("/email-settings", GetEmailSettingsAsync);
        group.MapPut("/email-settings", SaveEmailSettingsAsync);
        group.MapPost("/email-settings/test", TestEmailAsync);
        group.MapPost("/tenants/{tenantId:guid}/fiscal/test-connection", TestConnectionAsync);
        return group;
    }

    private static async Task<IResult> OnboardAsync(
        OnboardRequest request,
        ClaimsPrincipal user,
        HttpContext http,
        TenantOnboardingService onboarding,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(user.FindFirstValue("sub"), out var actorId))
        {
            return Results.Unauthorized();
        }

        var command = new OnboardTenantCommand(
            (request.Slug ?? string.Empty).Trim().ToLowerInvariant(),
            request.Name ?? string.Empty,
            (request.Ruc ?? string.Empty).Trim(),
            request.LegalName ?? string.Empty,
            request.TradeName,
            request.HeadOfficeAddress ?? string.Empty,
            request.Regime ?? "general",
            request.AccountingRequired ?? false,
            request.ProviderCompanyId ?? string.Empty,
            request.ProviderApiKey ?? string.Empty,
            request.OwnerUsername ?? string.Empty,
            request.OwnerDisplayName ?? string.Empty,
            request.OwnerEmail ?? string.Empty,
            request.Environment ?? "test",
            request.SriEstablishment ?? "001",
            request.EmissionPointCode ?? "001");

        try
        {
            var result = await onboarding.OnboardAsync(command, actorId, cancellationToken);

            return Results.Created($"/platform/tenants/{result.TenantId}", new
            {
                result.TenantId,
                result.Slug,
                result.OwnerUserId,
                result.OwnerUsername,
                result.WelcomeEmailSent,
            });
        }
        catch (OnboardingValidationException ex)
        {
            return Results.Json(
                new { error = new { code = "validation_failed", message = "Invalid onboarding data.", details = ex.Errors } },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (OnboardingConflictException ex)
        {
            return Results.Json(
                new { error = new { code = ex.Field + "_taken", message = $"The {ex.Field} is already registered." } },
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> ListAsync(PlatformService service, CancellationToken ct) =>
        Results.Ok((await service.ListAsync(ct)).Select(t => new
        {
            t.Id,
            t.Name,
            t.Slug,
            t.Status,
            t.EffectiveStatus,
            subscriptionEndsOn = t.SubscriptionEndsOn.ToString("yyyy-MM-dd"),
            t.DaysLeft,
            t.CreatedAt,
            t.Customers,
        }));

    private static async Task<IResult> ExtendAsync(Guid tenantId, ExtendRequest body, ClaimsPrincipal user, PlatformService service, CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirstValue("sub"), out var actorId))
        {
            return Results.Unauthorized();
        }

        var ends = await service.ExtendAsync(tenantId, body.Months, body.Amount, actorId, ct);
        return Results.Ok(new { subscriptionEndsOn = ends.ToString("yyyy-MM-dd") });
    }

    private static async Task<IResult> SetSuspendedAsync(Guid tenantId, bool suspended, ClaimsPrincipal user, PlatformService service, CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirstValue("sub"), out var actorId))
        {
            return Results.Unauthorized();
        }

        await service.SetSuspendedAsync(tenantId, suspended, actorId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ResendWelcomeAsync(Guid tenantId, PlatformService service, CancellationToken ct)
    {
        try
        {
            await service.ResendWelcomeAsync(tenantId, ct);
            return Results.NoContent();
        }
        catch (FitOS.Application.Email.EmailDeliveryException)
        {
            return Results.Json(
                new { error = new { code = "email_failed", message = "The mail server did not accept the message." } },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> RevenueAsync(PlatformService service, CancellationToken ct) =>
        Results.Ok((await service.RevenueAsync(ct)).Select(r => new { month = r.Month.ToString("yyyy-MM"), r.Total, r.Payments }));

    private static async Task<IResult> GetEmailSettingsAsync(EmailSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.GetAsync(ct));

    private static async Task<IResult> SaveEmailSettingsAsync(EmailSettingsRequest body, ClaimsPrincipal user, EmailSettingsService service, CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirstValue("sub"), out var actorId))
        {
            return Results.Unauthorized();
        }

        await service.SaveAsync(new EmailSettingsInput(body.Host, body.Port, body.Security, body.Username, body.FromAddress, body.Password), actorId, ct);
        return Results.Ok(await service.GetAsync(ct));
    }

    private static async Task<IResult> TestEmailAsync(TestEmailRequest body, ClaimsPrincipal user, EmailSettingsService service, CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirstValue("sub"), out var actorId))
        {
            return Results.Unauthorized();
        }

        try
        {
            await service.SendTestAsync(body.To, actorId, ct);
            return Results.NoContent();
        }
        catch (EmailDeliveryException ex)
        {
            return Results.Json(
                new { error = new { code = "email_failed", message = "The mail server did not accept the message.", reason = ex.Reason } },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> TestConnectionAsync(Guid tenantId, FiscalConnectionService service, CancellationToken cancellationToken)
    {
        var result = await service.TestAsync(tenantId, cancellationToken);
        return Results.Ok(new { status = Wire(result.Status), detail = result.Detail });
    }

    private static string Wire(FiscalConnectionStatus status) => status switch
    {
        FiscalConnectionStatus.Ok => "ok",
        FiscalConnectionStatus.NotConfigured => "not_configured",
        FiscalConnectionStatus.InvalidApiKey => "invalid_api_key",
        FiscalConnectionStatus.WrongCompany => "wrong_company",
        FiscalConnectionStatus.CompanyNotFound => "company_not_found",
        FiscalConnectionStatus.Unreachable => "unreachable",
        _ => "unexpected",
    };

    public sealed record OnboardRequest(
        string? Slug,
        string? Name,
        string? Ruc,
        string? LegalName,
        string? TradeName,
        string? HeadOfficeAddress,
        string? Regime,
        bool? AccountingRequired,
        string? ProviderCompanyId,
        string? ProviderApiKey,
        string? OwnerUsername,
        string? OwnerDisplayName,
        string? OwnerEmail,
        string? Environment,
        string? SriEstablishment,
        string? EmissionPointCode);

    public sealed record ExtendRequest(int? Months, decimal? Amount);

    public sealed record EmailSettingsRequest(string? Host, int? Port, string? Security, string? Username, string? FromAddress, string? Password);

    public sealed record TestEmailRequest(string? To);
}
