using System.Security.Claims;
using FitOS.Api.Auth;
using FitOS.Application.Dashboard;

namespace FitOS.Api.Dashboard;

/// <summary>Headline numbers of the caller's gym. The gym comes from the token, never from the URL.</summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboard(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dashboard/summary", Summary).RequireAuthorization(AuthPolicies.Staff);
        app.MapGet("/dashboard/insights", GetInsights).RequireAuthorization(AuthPolicies.Manager);
        return app;
    }

    /// <summary>AI tips from aggregate figures. The provider key stays on the server; answers are cached per gym.</summary>
    private static async Task<IResult> GetInsights(InsightsService service, CancellationToken ct)
    {
        var insights = await service.GetAsync(ct);
        return Results.Ok(new { insights.Text, insights.GeneratedAt });
    }

    private static async Task<IResult> Summary(ClaimsPrincipal user, DashboardService service, CancellationToken ct)
    {
        var includeMoney = user.IsInRole("owner") || user.IsInRole("admin");
        var s = await service.GetAsync(includeMoney, ct);
        return Results.Ok(new
        {
            s.Today,
            s.TotalCustomers,
            s.ActiveMembers,
            s.ExpiringSoon,
            s.ExpiringWithinDays,
            s.AttendanceToday,
            s.InsideNow,
            Genders = s.Genders.Select(g => new { g.Gender, g.Count }),
            Attendance = s.Attendance.Select(a => new { Date = a.Day, a.Count }),
            Sales = s.Sales is null ? null : new
            {
                s.Sales.Today,
                s.Sales.Last7Days,
                Daily = s.Sales.Daily.Select(d => new { Date = d.Day, d.Amount }),
            },
        });
    }
}
