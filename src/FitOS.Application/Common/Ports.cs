namespace FitOS.Application.Common;

/// <summary>Today's date in the TENANT's time zone (a gym in Guayaquil is not on UTC).</summary>
public interface ITenantClock
{
    Task<DateOnly> TodayAsync(CancellationToken cancellationToken);
}
