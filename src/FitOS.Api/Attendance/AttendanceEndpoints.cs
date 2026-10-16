using System.Security.Claims;
using FitOS.Api.Auth;
using FitOS.Api.Members;
using FitOS.Application.Attendance;
using FitOS.Application.Common;
using FitOS.Domain.Attendance;

namespace FitOS.Api.Attendance;

/// <summary>Entries and exits of the caller's gym. The gym comes from the token, never from the URL.</summary>
public static class AttendanceEndpoints
{
    public static IEndpointRouteBuilder MapAttendance(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/attendance");
        group.MapPost("/check-in", CheckIn).RequireAuthorization(AuthPolicies.Staff);
        group.MapPost("/check-out", CheckOut).RequireAuthorization(AuthPolicies.Staff);
        group.MapGet("/", List).RequireAuthorization(AuthPolicies.Staff);

        app.MapGet("/customers/{id:guid}/attendance", ListForCustomer).RequireAuthorization(AuthPolicies.TenantMember);
        return app;
    }

    private static async Task<IResult> CheckIn(CheckInRequest body, AttendanceService service, CancellationToken ct)
    {
        // 201 for both outcomes: a denied scan is an event that was recorded, not a failed request.
        var item = await service.CheckInAsync(new CheckInInput(new IdentifyCustomer(body.CustomerId, body.ClientCode, body.RfidCard), body.BranchId), ct);
        return Results.Created($"/attendance/{item.Id}", ToResponse(item));
    }

    private static async Task<IResult> CheckOut(CheckOutRequest body, AttendanceService service, CancellationToken ct) =>
        Results.Ok(ToResponse(await service.CheckOutAsync(new IdentifyCustomer(body.CustomerId, body.ClientCode, body.RfidCard), ct)));

    private static async Task<IResult> List(
        DateOnly? from, DateOnly? to, Guid? customerId, string? result, int? page, int? pageSize, AttendanceService service, CancellationToken ct) =>
        Results.Ok(ToPage(await service.ListAsync(new AttendanceFilter(from, to, customerId, ParseResult(result), page ?? 1, pageSize ?? 25), ct)));

    private static async Task<IResult> ListForCustomer(
        Guid id, DateOnly? from, DateOnly? to, int? page, int? pageSize, ClaimsPrincipal user, AttendanceService service, CancellationToken ct)
    {
        CustomerAccess.EnsureCanSee(user, id);
        return Results.Ok(ToPage(await service.ListForCustomerAsync(id, new AttendanceFilter(from, to, id, null, page ?? 1, pageSize ?? 25), ct)));
    }

    private static AttendanceResult? ParseResult(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Enum.TryParse<AttendanceResult>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ValidationFailedException("result", "invalid");
    }

    private static object ToPage(PagedResult<AttendanceItem> page) => new
    {
        items = page.Items.Select(ToResponse),
        page.Page,
        page.PageSize,
        page.Total,
    };

    private static object ToResponse(AttendanceItem i) => new
    {
        i.Id,
        i.CustomerId,
        i.CustomerName,
        i.ClientCode,
        i.BranchId,
        i.CheckIn,
        i.CheckOut,
        i.Date,
        i.CheckInTime,
        i.CheckOutTime,
        i.Minutes,
        i.Result,
        i.DenyReason,
        i.AutoClosed,
    };

    public sealed record CheckInRequest(Guid? CustomerId, long? ClientCode, string? RfidCard, Guid? BranchId);

    public sealed record CheckOutRequest(Guid? CustomerId, long? ClientCode, string? RfidCard);
}
