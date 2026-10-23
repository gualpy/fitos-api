using System.Security.Claims;
using FitOS.Api.Tenancy;
using FitOS.Application.Auth;
using FitOS.Infrastructure.Auth;
using Microsoft.AspNetCore.Authorization;
using FitOS.Domain.Tenancy;

namespace FitOS.Api.Auth;

public static class AuthEndpoints
{
    public const string LoginRateLimitPolicy = "login";

    public const string GroupPath = "/auth";

    public const string LoginRoute = "/login";

    public const string ForgotPasswordRoute = "/forgot-password";

    private const int MaxFieldLength = 200;

    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(GroupPath);
        group.MapPost(LoginRoute, LoginAsync).AllowAnonymous().RequireRateLimiting(LoginRateLimitPolicy);
        group.MapPost("/refresh", RefreshAsync).AllowAnonymous().RequireRateLimiting(LoginRateLimitPolicy);
        group.MapPost("/confirm-login", ConfirmLoginAsync).AllowAnonymous().RequireRateLimiting(LoginRateLimitPolicy);
        group.MapPost(ForgotPasswordRoute, ForgotPasswordAsync).AllowAnonymous().RequireRateLimiting(LoginRateLimitPolicy);
        group.MapPost("/setup-info", SetupInfoAsync).AllowAnonymous().RequireRateLimiting(LoginRateLimitPolicy);
        group.MapPost("/set-password", SetPasswordAsync).AllowAnonymous().RequireRateLimiting(LoginRateLimitPolicy);
        group.MapPost("/logout", LogoutAsync).RequireAuthorization(AuthPolicies.Authenticated);
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
            ClientOf(http),
            cancellationToken);

        if (result.Challenge is { } challenge)
        {
            // No session yet: the browser keeps this secret and the link in the e-mail completes the login.
            return Results.Json(
                new { status = "confirmation_required", challengeSecret = challenge.Secret, expiresAt = challenge.ExpiresAt },
                statusCode: StatusCodes.Status202Accepted);
        }

        if (result.ConfirmationEmailFailed)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "email_unavailable", "The confirmation e-mail could not be sent. Try again in a moment.");
        }

        if (result.ConfirmationUnavailable)
        {
            return Error(StatusCodes.Status403Forbidden, "confirmation_unavailable", "This account has no e-mail to confirm the login. Set one with the Migrator (set-platform-email).");
        }

        if (result.GymInactive)
        {
            return Error(StatusCodes.Status403Forbidden, "tenant_inactive", "The gym's subscription ended or it was suspended. Contact the platform administrator.");
        }

        if (!result.Succeeded)
        {
            return Error(StatusCodes.Status401Unauthorized, "invalid_credentials", "Invalid username or password.");
        }

        return Results.Ok(ToResponse(result));
    }

    private static async Task<IResult> ConfirmLoginAsync(ConfirmLoginRequest request, HttpContext http, LoginService login, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.Secret)
            || request.Token.Length > MaxFieldLength || request.Secret.Length > MaxFieldLength)
        {
            return Error(StatusCodes.Status400BadRequest, "validation_failed", "token and secret are required.");
        }

        var result = await login.ConfirmAsync(request.Token.Trim(), request.Secret.Trim(), ClientOf(http), cancellationToken);
        if (result.Succeeded)
        {
            return Results.Ok(ToResponse(result));
        }

        return result.ConfirmationExpired
            ? Error(StatusCodes.Status400BadRequest, "link_expired", "The link expired. Sign in again.")
            : Error(StatusCodes.Status400BadRequest, "invalid_link", "The link is not valid, was already used, or was opened in another browser. Sign in again.");
    }

    /// <summary>The reply is always 202, whatever happens: it never says whether the gym or the account exists.</summary>
    private static async Task<IResult> ForgotPasswordAsync(ForgotPasswordRequest request, HttpContext http, TenantHostResolver resolver, PasswordResetService service, CancellationToken cancellationToken)
    {
        var identifier = request.Identifier?.Trim() ?? string.Empty;
        if (identifier.Length == 0 || identifier.Length > MaxFieldLength)
        {
            return Error(StatusCodes.Status400BadRequest, "validation_failed", "identifier is required.");
        }

        var target = resolver.Resolve(http.Request);
        await service.RequestAsync(target.Kind == TenantTargetKind.Tenant ? target.Slug : null, identifier, ClientOf(http), cancellationToken);
        return Results.Json(new { status = "accepted" }, statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> SetupInfoAsync(SetupInfoRequest request, PasswordSetupService service, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token) || request.Token.Length > MaxFieldLength)
        {
            return Error(StatusCodes.Status400BadRequest, "validation_failed", "token is required.");
        }

        return await service.InfoAsync(request.Token.Trim(), cancellationToken) is { } info
            ? Results.Ok(new { info.DisplayName, info.GymName, info.Purpose })
            : Error(StatusCodes.Status400BadRequest, "invalid_link", "The link is not valid, was already used or expired.");
    }

    private static async Task<IResult> SetPasswordAsync(SetPasswordRequest request, HttpContext http, PasswordSetupService service, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrEmpty(request.Password)
            || request.Token.Length > MaxFieldLength || request.Password.Length > MaxFieldLength)
        {
            return Error(StatusCodes.Status400BadRequest, "validation_failed", "token and password are required.");
        }

        return await service.SetPasswordAsync(request.Token.Trim(), request.Password, ClientOf(http), cancellationToken) switch
        {
            SetPasswordOutcome.Done => Results.NoContent(),
            SetPasswordOutcome.WeakPassword => Error(
                StatusCodes.Status422UnprocessableEntity,
                "weak_password",
                $"The password needs {ChangePasswordService.MinLength}+ characters, a letter and a non-letter."),
            SetPasswordOutcome.ExpiredLink => Error(StatusCodes.Status400BadRequest, "link_expired", "The link expired. Ask for a new one."),
            _ => Error(StatusCodes.Status400BadRequest, "invalid_link", "The link is not valid or was already used."),
        };
    }

    private static async Task<IResult> RefreshAsync(RefreshRequest request, HttpContext http, SessionService sessions, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken) || request.RefreshToken.Length > 200)
        {
            return Error(StatusCodes.Status400BadRequest, "validation_failed", "refreshToken is required.");
        }

        var result = await sessions.RefreshAsync(request.RefreshToken, ClientOf(http), cancellationToken);
        if (result.Succeeded)
        {
            return Results.Ok(ToResponse(result.Login!));
        }

        // Two requests at once from the same client: ask it to retry with the token it just received.
        // Every other failure looks the same: it never reveals whether the token existed, expired or was stolen.
        return result.Status == RefreshStatus.Concurrent
            ? Error(StatusCodes.Status409Conflict, "refresh_in_progress", "The session is being renewed; retry in a moment.")
            : Error(StatusCodes.Status401Unauthorized, "invalid_refresh_token", "The session is no longer valid. Sign in again.");
    }

    private static async Task<IResult> LogoutAsync(ClaimsPrincipal user, HttpContext http, SessionService sessions, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(user.FindFirstValue(JwtAccessTokenIssuer.SessionClaim), out var sessionId))
        {
            Guid? userId = Guid.TryParse(user.FindFirstValue("sub"), out var parsed) ? parsed : null;
            await sessions.LogoutAsync(sessionId, userId, ClientOf(http), cancellationToken);
        }

        return Results.NoContent();
    }

    private static LoginResponse ToResponse(LoginResult result) => new(
        result.Token!.Value,
        "Bearer",
        result.Token.ExpiresAt,
        result.Refresh!.Token,
        result.Refresh.ExpiresAt,
        new UserSummary(result.User!.Id, result.User.TenantId, result.User.DisplayName, result.User.Role.ToWire(), result.User.MustChangePassword, result.User.CustomerId));

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        ClaimsPrincipal user,
        HttpContext http,
        ChangePasswordService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId)
            || string.IsNullOrEmpty(request.CurrentPassword) || string.IsNullOrEmpty(request.NewPassword)
            || request.CurrentPassword.Length > MaxFieldLength || request.NewPassword.Length > MaxFieldLength)
        {
            return Error(StatusCodes.Status400BadRequest, "validation_failed", "Current and new password are required.");
        }

        return await service.ChangeAsync(userId, request.CurrentPassword, request.NewPassword, ClientOf(http), cancellationToken) switch
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

    /// <summary>The real client address (after trusted-proxy processing) and browser, for the security log only.</summary>
    private static ClientInfo ClientOf(HttpContext http) =>
        new(http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: status);

    public sealed record LoginRequest(string? Username, string? Password);

    public sealed record UserSummary(Guid Id, Guid? TenantId, string DisplayName, string Role, bool MustChangePassword, Guid? CustomerId);

    public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

    public sealed record LoginResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt, string RefreshToken, DateTimeOffset RefreshExpiresAt, UserSummary User);

    public sealed record RefreshRequest(string? RefreshToken);

    public sealed record ConfirmLoginRequest(string? Token, string? Secret);

    public sealed record ForgotPasswordRequest(string? Identifier);

    public sealed record SetupInfoRequest(string? Token);

    public sealed record SetPasswordRequest(string? Token, string? Password);
}
