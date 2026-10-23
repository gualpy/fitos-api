using FitOS.Application.Common;

namespace FitOS.Application.Subscriptions;

/// <param name="ReminderDays">From this many days before the last day, the gym is reminded to renew.</param>
public sealed record SubscriptionPolicy(int TrialDays, int ReminderDays);

/// <param name="EndsOn">Last day with service (inclusive).</param>
public sealed record SubscriptionStatus(DateOnly EndsOn, int DaysLeft, bool ShowReminder);

public interface ISubscriptionReader
{
    Task<DateOnly?> EndsOnAsync(CancellationToken cancellationToken);
}

/// <summary>The caller's own gym: when its service ends and whether to remind it. Days are counted on the gym's calendar.</summary>
public sealed class SubscriptionService(ISubscriptionReader reader, ITenantClock clock, SubscriptionPolicy policy)
{
    public async Task<SubscriptionStatus> GetAsync(CancellationToken cancellationToken)
    {
        var endsOn = await reader.EndsOnAsync(cancellationToken) ?? throw new NotFoundException("subscription");
        var today = await clock.TodayAsync(cancellationToken);
        var daysLeft = endsOn.DayNumber - today.DayNumber;
        return new SubscriptionStatus(endsOn, daysLeft, daysLeft <= policy.ReminderDays);
    }
}
