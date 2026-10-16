namespace FitOS.Domain.Attendance;

/// <summary>Opening hours of a branch on one weekday.</summary>
public sealed record BusinessDay(bool IsOpen, TimeOnly Open, TimeOnly Close);

public sealed record AccessDecision(bool Granted, string? Reason)
{
    public static AccessDecision Allow { get; } = new(true, null);

    public static AccessDecision Deny(string reason) => new(false, reason);
}

/// <summary>Who may come in. Pure so the rules can be tested without a database or a clock.</summary>
public static class AccessPolicy
{
    /// <param name="day">Hours of that weekday; <c>null</c> when the gym has not configured hours (then hours do not restrict).</param>
    /// <param name="localTime">Time of day in the GYM's time zone.</param>
    public static AccessDecision Decide(bool customerActive, bool hasCurrentMembership, bool allowWithoutPlan, BusinessDay? day, TimeOnly localTime)
    {
        if (!customerActive)
        {
            return AccessDecision.Deny("customer_inactive");
        }

        if (day is { } hours)
        {
            if (!hours.IsOpen)
            {
                return AccessDecision.Deny("closed");
            }

            // [open, close): at closing time the door is already shut.
            if (localTime < hours.Open || localTime >= hours.Close)
            {
                return AccessDecision.Deny("outside_hours");
            }
        }

        return hasCurrentMembership || allowWithoutPlan ? AccessDecision.Allow : AccessDecision.Deny("no_active_membership");
    }
}

public static class TenantTime
{
    /// <summary>ISO weekday: Monday = 1 ... Sunday = 7 (the convention of business_hours.weekday).</summary>
    public static int IsoWeekday(DateOnly date) => date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;

    /// <summary>The instant a local calendar day starts, in UTC.</summary>
    public static DateTimeOffset StartOfDayUtc(TimeZoneInfo zone, DateOnly date) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone), TimeSpan.Zero);

    public static DateOnly LocalDate(TimeZoneInfo zone, DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    public static TimeOnly LocalTime(TimeZoneInfo zone, DateTimeOffset instant) =>
        TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
}
