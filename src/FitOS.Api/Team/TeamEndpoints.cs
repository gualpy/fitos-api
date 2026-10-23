using System.Security.Claims;
using FitOS.Api.Auth;
using FitOS.Application.Team;
using FitOS.Domain.Tenancy;
using FitOS.Infrastructure.Auth;

namespace FitOS.Api.Team;

/// <summary>The gym's staff (owner and administrators manage it) and logins for customers (reception can grant them).</summary>
public static class TeamEndpoints
{
    public static IEndpointRouteBuilder MapTeam(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/users").RequireAuthorization(AuthPolicies.Manager);
        users.MapGet("/", List);
        users.MapPost("/", Invite);
        users.MapPatch("/{id:guid}", Update);
        users.MapPost("/{id:guid}/resend-invite", Resend);

        var access = app.MapGroup("/customers/{customerId:guid}/access").RequireAuthorization(AuthPolicies.Staff);
        access.MapGet("/", Status);
        access.MapPost("/", Grant);
        access.MapPost("/resend", ResendAccess);
        return app;
    }

    private static Actor? ActorOf(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue("sub"), out var id) && user.FindFirstValue(JwtAccessTokenIssuer.RoleClaim) is { } role
            ? new Actor(id, UserRoleWire.Parse(role))
            : null;

    private static async Task<IResult> List(TeamService service, CancellationToken ct) =>
        Results.Ok((await service.ListAsync(ct)).Select(m => new
        {
            m.Id,
            name = m.DisplayName,
            m.Email,
            m.Role,
            m.IsActive,
            m.PasswordSet,
            m.LastLoginAt,
        }));

    private static async Task<IResult> Invite(InviteRequest body, ClaimsPrincipal user, TeamService service, CancellationToken ct)
    {
        if (ActorOf(user) is not { } actor)
        {
            return Results.Unauthorized();
        }

        var result = await service.InviteAsync(actor, new InviteInput(body.Name, body.Email, body.Role), ct);
        return Results.Created($"/users/{result.Id}", new { result.Id, result.EmailSent });
    }

    private static async Task<IResult> Update(Guid id, UpdateRequest body, ClaimsPrincipal user, TeamService service, CancellationToken ct)
    {
        if (ActorOf(user) is not { } actor)
        {
            return Results.Unauthorized();
        }

        if (body.IsActive is null && body.Role is null)
        {
            return Results.Json(
                new { error = new { code = "validation_failed", message = "Nothing to change.", details = new Dictionary<string, string> { ["isActive"] = "required" } } },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        if (body.Role is not null)
        {
            await service.ChangeRoleAsync(actor, id, body.Role, ct);
        }

        if (body.IsActive is { } active)
        {
            await service.SetActiveAsync(actor, id, active, ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> Resend(Guid id, ClaimsPrincipal user, TeamService service, CancellationToken ct) =>
        ActorOf(user) is { } actor
            ? Results.Ok(new { emailSent = await service.ResendInviteAsync(actor, id, ct) })
            : Results.Unauthorized();

    private static async Task<IResult> Status(Guid customerId, MemberAccessService service, CancellationToken ct)
    {
        var status = await service.StatusAsync(customerId, ct);
        return Results.Ok(new { status.HasAccess, status.Email, status.IsActive, status.PasswordSet });
    }

    private static async Task<IResult> Grant(Guid customerId, GrantRequest body, MemberAccessService service, CancellationToken ct)
    {
        var result = await service.GrantAsync(customerId, body.Email, ct);
        return Results.Created($"/customers/{customerId}/access", new { result.Id, result.EmailSent });
    }

    private static async Task<IResult> ResendAccess(Guid customerId, MemberAccessService service, CancellationToken ct) =>
        Results.Ok(new { emailSent = await service.ResendAsync(customerId, ct) });

    private sealed record InviteRequest(string? Name, string? Email, string? Role);

    private sealed record UpdateRequest(bool? IsActive, string? Role);

    private sealed record GrantRequest(string? Email);
}
