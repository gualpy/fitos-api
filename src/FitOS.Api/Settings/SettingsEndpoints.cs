using FitOS.Api.Auth;
using FitOS.Application.Settings;

namespace FitOS.Api.Settings;

/// <summary>Rules of the caller's gym: access without a plan and weekly opening hours. The gym comes from the token.</summary>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettings(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/settings");
        group.MapGet("/", Get).RequireAuthorization(AuthPolicies.Staff);
        group.MapPut("/", Update).RequireAuthorization(AuthPolicies.Manager);
        return app;
    }

    private static async Task<IResult> Get(SettingsService service, CancellationToken ct) =>
        Results.Ok(ToResponse(await service.GetAsync(ct)));

    private static async Task<IResult> Update(SettingsRequest body, SettingsService service, CancellationToken ct)
    {
        var hours = body.Hours?.Select(h => new HoursInput(h.Weekday, h.IsOpen, h.OpenTime, h.CloseTime)).ToList();
        return Results.Ok(ToResponse(await service.UpdateAsync(new SettingsInput(body.AllowWithoutPlan, hours), ct)));
    }

    private static object ToResponse(GymSettings s) => new
    {
        s.AllowWithoutPlan,
        s.Timezone,
        branch = s.BranchId is null ? null : new { id = s.BranchId, name = s.BranchName },
        hours = s.Hours.Select(h => new
        {
            h.Weekday,
            h.Configured,
            h.IsOpen,
            openTime = h.Open.ToString("HH:mm"),
            closeTime = h.Close.ToString("HH:mm"),
        }),
    };

    private sealed record SettingsRequest(bool? AllowWithoutPlan, List<HoursRequest>? Hours);

    private sealed record HoursRequest(int? Weekday, bool? IsOpen, string? OpenTime, string? CloseTime);
}
