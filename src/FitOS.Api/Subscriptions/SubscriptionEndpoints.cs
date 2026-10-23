using FitOS.Api.Auth;
using FitOS.Application.Subscriptions;

namespace FitOS.Api.Subscriptions;

/// <summary>The caller's own gym: when its service ends, so the panel can remind the owner in time. The gym comes from the token.</summary>
public static class SubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapSubscription(this IEndpointRouteBuilder app)
    {
        app.MapGet("/subscription", Get).RequireAuthorization(AuthPolicies.Staff);
        return app;
    }

    private static async Task<IResult> Get(SubscriptionService service, CancellationToken ct)
    {
        var status = await service.GetAsync(ct);
        return Results.Ok(new { endsOn = status.EndsOn.ToString("yyyy-MM-dd"), status.DaysLeft, status.ShowReminder });
    }
}
