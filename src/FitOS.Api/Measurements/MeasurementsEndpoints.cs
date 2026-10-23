using System.Security.Claims;
using FitOS.Api.Auth;
using FitOS.Api.Members;
using FitOS.Api.Products;
using FitOS.Application.Measurements;

namespace FitOS.Api.Measurements;

/// <summary>
/// Weight and height history of a customer. A customer reads and records only their own; staff do it for any customer of the gym.
/// </summary>
public static class MeasurementsEndpoints
{
    public static IEndpointRouteBuilder MapMeasurements(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/customers/{id:guid}/measurements").RequireAuthorization(AuthPolicies.TenantMember);
        group.MapGet("/", List);
        group.MapPost("/", Record);
        group.MapDelete("/{measurementId:guid}", Delete);
        return app;
    }

    private static async Task<IResult> List(Guid id, ClaimsPrincipal user, MeasurementService service, CancellationToken ct)
    {
        CustomerAccess.EnsureCanSee(user, id);
        return Results.Ok((await service.ListAsync(id, ct)).Select(ToResponse));
    }

    private static async Task<IResult> Record(Guid id, MeasurementRequest body, ClaimsPrincipal user, MeasurementService service, CancellationToken ct)
    {
        CustomerAccess.EnsureCanSee(user, id);
        var saved = await service.RecordAsync(id, new MeasurementInput(body.MeasuredOn, body.WeightKg, body.HeightCm), ProductsEndpoints.UserId(user), ct);
        return Results.Ok(ToResponse(saved));
    }

    private static async Task<IResult> Delete(Guid id, Guid measurementId, ClaimsPrincipal user, MeasurementService service, CancellationToken ct)
    {
        CustomerAccess.EnsureCanSee(user, id);
        await service.DeleteAsync(id, measurementId, ct);
        return Results.NoContent();
    }

    private static object ToResponse(BodyMeasurement m) => new { m.Id, m.MeasuredOn, m.WeightKg, m.HeightCm };

    public sealed record MeasurementRequest(DateOnly? MeasuredOn, decimal? WeightKg, decimal? HeightCm);
}
