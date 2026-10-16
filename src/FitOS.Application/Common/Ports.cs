namespace FitOS.Application.Common;

/// <summary>The tenant's own calendar: a gym in Guayaquil is not on UTC.</summary>
public interface ITenantClock
{
    Task<TimeZoneInfo> ZoneAsync(CancellationToken cancellationToken);

    /// <summary>Today's date in the tenant's time zone.</summary>
    Task<DateOnly> TodayAsync(CancellationToken cancellationToken);
}

/// <summary>Per-gym switches stored in tenant_settings.</summary>
public interface ITenantSettings
{
    /// <summary>When true, customers without a current membership may still come in.</summary>
    Task<bool> AllowWithoutPlanAsync(CancellationToken cancellationToken);
}
