using System.Security.Claims;
using FitOS.Api.Auth;
using FitOS.Application.Fiscal;
using FitOS.Application.Onboarding;

namespace FitOS.Api.Platform;

/// <summary>Operations of the platform administrator (not of any single gym).</summary>
public static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatform(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/platform").RequireAuthorization(AuthPolicies.PlatformAdmin);
        group.MapPost("/tenants", OnboardAsync);
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
            request.Environment ?? "test",
            request.SriEstablishment ?? "001",
            request.EmissionPointCode ?? "001");

        try
        {
            var result = await onboarding.OnboardAsync(command, actorId, cancellationToken);

            // The response carries the temporary password: it must never be cached or stored by intermediaries.
            http.Response.Headers.CacheControl = "no-store";
            return Results.Created($"/platform/tenants/{result.TenantId}", new
            {
                result.TenantId,
                result.Slug,
                result.OwnerUserId,
                result.OwnerUsername,
                result.TemporaryPassword,
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
        string? Environment,
        string? SriEstablishment,
        string? EmissionPointCode);
}
