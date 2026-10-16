using System.Security.Claims;
using FitOS.Api.Tenancy;
using FitOS.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using FitOS.Domain.Tenancy;

namespace FitOS.Api.Auth;

public static class AuthEndpoints
{
    public const string LoginRateLimitPolicy = "login";

    private const int MaxFieldLength = 200;

    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth");
        group.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting(LoginRateLimitPolicy);
        group.MapGet("/me", Me).RequireAuthorization(AuthPolicies.Authenticated);
        group.MapPost("/change-password", ChangePasswordAsync)
            .RequireAuthorization(AuthPolicies.Authenticated)
            .RequireRateLimiting(LoginRateLimitPolicy);
        return app;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext http,
        TenantHostResolver resolver,
        LoginService login,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password)
            || request.Username.Length > MaxFieldLength || request.Password.Length > MaxFieldLength)
        {
            return Error(StatusCodes.Status400BadRequest, "validation_failed", "Username and password are required.");
        }

        var target = resolver.Resolve(http.Request);
        if (target.Kind == TenantTargetKind.Invalid)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_host", "Unknown tenant host.");
        }

        var result = await login.LoginAsync(
            target.Kind == TenantTargetKind.Tenant ? target.Slug : null,
            request.Username.Trim(),
            request.Password,
            cancellationToken);

        if (!result.Succeeded)
        {
            return Error(StatusCodes.Status401Unauthorized, "invalid_credentials", "Invalid username or password.");
        }

        return Results.Ok(new LoginResponse(
            result.Token!.Value,
            "Bearer",
            result.Token.ExpiresAt,
            new UserSummary(result.User!.Id, result.User.TenantId, result.User.DisplayName, result.User.Role.ToWire(), result.User.MustChangePassword, result.User.CustomerId)));
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        ClaimsPrincipal user,
        ChangePasswordService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId)
            || string.IsNullOrEmpty(request.CurrentPassword) || string.IsNullOrEmpty(request.NewPassword)
            || request.CurrentPassword.Length > MaxFieldLength || request.NewPassword.Length > MaxFieldLength)
        {
            return Error(StatusCodes.Status400BadRequest, "validation_failed", "Current and new password are required.");
        }

        return await service.ChangeAsync(userId, request.CurrentPassword, request.NewPassword, cancellationToken) switch
        {
            ChangePasswordOutcome.Changed => Results.NoContent(), // existing tokens stay valid until they expire; log in again
            ChangePasswordOutcome.WrongCurrentPassword => Error(StatusCodes.Status400BadRequest, "invalid_current_password", "The current password is incorrect."),
            _ => Error(
                StatusCodes.Status422UnprocessableEntity,
                "weak_password",
                $"The new password needs {ChangePasswordService.MinLength}+ characters, a letter and a non-letter, and must differ from the current one."),
        };
    }

    private static IResult Me(ClaimsPrincipal user) => Results.Ok(new
    {
        id = user.FindFirstValue("sub"),
        tenantId = user.FindFirstValue("tenant_id"),
        role = user.FindFirstValue("role"),
        customerId = user.FindFirstValue("customer_id"),
        mustChangePassword = user.HasClaim("pwd_change", "true"),
        name = user.FindFirstValue("name"),
    });

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: status);

    public sealed record LoginRequest(string? Username, string? Password);

    public sealed record UserSummary(Guid Id, Guid? TenantId, string DisplayName, string Role, bool MustChangePassword, Guid? CustomerId);

    public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

    public sealed record LoginResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt, UserSummary User);
}
