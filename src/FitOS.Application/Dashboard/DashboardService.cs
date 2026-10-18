using FitOS.Application.Common;

namespace FitOS.Application.Dashboard;

/// <summary>One calendar day of the gym (its own time zone) and a count of something that happened that day.</summary>
public sealed record DailyCount(DateOnly Day, int Count);

public sealed record DailyAmount(DateOnly Day, decimal Amount);

/// <param name="Gender">M, F, O, or null for members who never gave one.</param>
public sealed record GenderCount(string? Gender, int Count);

public sealed record SalesSummary(decimal Today, decimal Last7Days, IReadOnlyList<DailyAmount> Daily);

/// <param name="Days">The last <see cref="DashboardService.WindowDays"/> calendar days, oldest first, today last.</param>
public sealed record DashboardWindow(DateOnly Today, IReadOnlyList<DateOnly> Days, IReadOnlyList<DateTime> StartsUtc, IReadOnlyList<DateTime> EndsUtc, int ExpiringDays);

public sealed record DashboardSummary(
    DateOnly Today,
    int TotalCustomers,
    int ActiveMembers,
    int ExpiringSoon,
    int ExpiringWithinDays,
    int AttendanceToday,
    int InsideNow,
    IReadOnlyList<GenderCount> Genders,
    IReadOnlyList<DailyCount> Attendance,
    SalesSummary? Sales);

public interface IDashboardRepository
{
    Task<int> CountCustomersAsync(CancellationToken cancellationToken);

    /// <summary>Active customers with a membership covering <paramref name="today"/>.</summary>
    Task<int> CountActiveMembersAsync(DateOnly today, CancellationToken cancellationToken);

    /// <summary>Customers whose current membership ends within <paramref name="days"/> days and has no renewal scheduled.</summary>
    Task<int> CountExpiringAsync(DateOnly today, int days, CancellationToken cancellationToken);

    Task<int> CountInsideAsync(CancellationToken cancellationToken);

    /// <summary>Active customers grouped by gender.</summary>
    Task<IReadOnlyList<GenderCount>> GenderBreakdownAsync(CancellationToken cancellationToken);

    /// <summary>Distinct customers let in per window day (granted entries only); one entry per window day.</summary>
    Task<IReadOnlyList<DailyCount>> AttendancePerDayAsync(DashboardWindow window, CancellationToken cancellationToken);

    /// <summary>Paid sales per window day; one entry per window day.</summary>
    Task<IReadOnlyList<DailyAmount>> SalesPerDayAsync(DashboardWindow window, CancellationToken cancellationToken);
}

public sealed class DashboardService(IDashboardRepository repository, ITenantClock clock)
{
    public const int WindowDays = 7;

    /// <summary>Members whose plan ends within this many days are shown as "about to expire".</summary>
    public const int ExpiringDays = 7;

    /// <param name="includeMoney">Revenue is for owners and admins only; reception gets null.</param>
    public async Task<DashboardSummary> GetAsync(bool includeMoney, CancellationToken cancellationToken)
    {
        var window = await WindowAsync(cancellationToken);
        var today = window.Today;

        // Same scoped DbContext underneath: one query at a time.
        var total = await repository.CountCustomersAsync(cancellationToken);
        var active = await repository.CountActiveMembersAsync(today, cancellationToken);
        var expiring = await repository.CountExpiringAsync(today, ExpiringDays, cancellationToken);
        var inside = await repository.CountInsideAsync(cancellationToken);
        var genders = await repository.GenderBreakdownAsync(cancellationToken);
        var attendance = await repository.AttendancePerDayAsync(window, cancellationToken);

        SalesSummary? sales = null;
        if (includeMoney)
        {
            var daily = await repository.SalesPerDayAsync(window, cancellationToken);
            sales = new SalesSummary(daily.Where(d => d.Day == today).Sum(d => d.Amount), daily.Sum(d => d.Amount), daily);
        }

        return new DashboardSummary(
            today,
            total,
            active,
            expiring,
            ExpiringDays,
            attendance.FirstOrDefault(a => a.Day == today)?.Count ?? 0,
            inside,
            genders,
            attendance,
            sales);
    }

    private async Task<DashboardWindow> WindowAsync(CancellationToken cancellationToken)
    {
        var zone = await clock.ZoneAsync(cancellationToken);
        var today = await clock.TodayAsync(cancellationToken);

        var days = Enumerable.Range(0, WindowDays).Select(i => today.AddDays(i - (WindowDays - 1))).ToList();
        return new DashboardWindow(today, days, days.Select(d => StartOf(d, zone)).ToList(), days.Select(d => StartOf(d.AddDays(1), zone)).ToList(), ExpiringDays);
    }

    private static DateTime StartOf(DateOnly day, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), zone);
}
