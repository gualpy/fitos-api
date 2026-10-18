using FitOS.Application.Common;
using FitOS.Application.Dashboard;

namespace FitOS.UnitTests.Dashboard;

public sealed class DashboardServiceTests
{
    private static readonly TimeZoneInfo Ecuador = TimeZoneInfo.CreateCustomTimeZone("EC", TimeSpan.FromHours(-5), "EC", "EC");

    private static readonly DateOnly Today = new(2026, 10, 18);

    private sealed class FakeClock : ITenantClock
    {
        public Task<TimeZoneInfo> ZoneAsync(CancellationToken cancellationToken) => Task.FromResult(Ecuador);

        public Task<DateOnly> TodayAsync(CancellationToken cancellationToken) => Task.FromResult(Today);
    }

    private sealed class FakeRepository : IDashboardRepository
    {
        public DashboardWindow? Window { get; private set; }

        public int SalesCalls { get; private set; }

        public int ExpiringDaysAsked { get; private set; }

        public Task<int> CountCustomersAsync(CancellationToken cancellationToken) => Task.FromResult(5);

        public Task<int> CountActiveMembersAsync(DateOnly today, CancellationToken cancellationToken) => Task.FromResult(3);

        public Task<int> CountExpiringAsync(DateOnly today, int days, CancellationToken cancellationToken)
        {
            ExpiringDaysAsked = days;
            return Task.FromResult(1);
        }

        public Task<int> CountInsideAsync(CancellationToken cancellationToken) => Task.FromResult(2);

        public Task<IReadOnlyList<GenderCount>> GenderBreakdownAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GenderCount>>([new GenderCount("F", 2), new GenderCount(null, 1)]);

        public Task<IReadOnlyList<DailyCount>> AttendancePerDayAsync(DashboardWindow window, CancellationToken cancellationToken)
        {
            Window = window;
            return Task.FromResult<IReadOnlyList<DailyCount>>(window.Days.Select((d, i) => new DailyCount(d, i)).ToList());
        }

        public Task<IReadOnlyList<DailyAmount>> SalesPerDayAsync(DashboardWindow window, CancellationToken cancellationToken)
        {
            SalesCalls++;
            return Task.FromResult<IReadOnlyList<DailyAmount>>(window.Days.Select(d => new DailyAmount(d, d == Today ? 12.5m : 1m)).ToList());
        }
    }

    [Fact]
    public async Task The_window_is_the_last_seven_gym_days_oldest_first_with_today_last()
    {
        var repository = new FakeRepository();

        await new DashboardService(repository, new FakeClock()).GetAsync(includeMoney: false, CancellationToken.None);

        var window = repository.Window!;
        Assert.Equal(7, window.Days.Count);
        Assert.Equal(Today.AddDays(-6), window.Days[0]);
        Assert.Equal(Today, window.Days[^1]);
        Assert.Equal(window.Days.Count, window.StartsUtc.Count);
    }

    [Fact]
    public async Task Each_day_runs_from_local_midnight_to_the_next_one_expressed_in_utc()
    {
        var repository = new FakeRepository();

        await new DashboardService(repository, new FakeClock()).GetAsync(false, CancellationToken.None);

        var window = repository.Window!;
        // Ecuador is UTC-5: local midnight is 05:00 UTC.
        Assert.Equal(new DateTime(2026, 10, 18, 5, 0, 0, DateTimeKind.Utc), window.StartsUtc[^1]);
        Assert.Equal(new DateTime(2026, 10, 19, 5, 0, 0, DateTimeKind.Utc), window.EndsUtc[^1]);
        for (var i = 0; i < window.Days.Count; i++)
        {
            Assert.Equal(TimeSpan.FromHours(24), window.EndsUtc[i] - window.StartsUtc[i]);
        }
    }

    [Fact]
    public async Task Attendance_today_is_the_count_of_the_last_window_day()
    {
        var summary = await new DashboardService(new FakeRepository(), new FakeClock()).GetAsync(false, CancellationToken.None);

        Assert.Equal(6, summary.AttendanceToday); // the fake returns the day index
        Assert.Equal(Today, summary.Today);
        Assert.Equal(5, summary.TotalCustomers);
        Assert.Equal(3, summary.ActiveMembers);
        Assert.Equal(2, summary.InsideNow);
    }

    [Fact]
    public async Task Without_money_rights_revenue_is_not_even_queried()
    {
        var repository = new FakeRepository();

        var summary = await new DashboardService(repository, new FakeClock()).GetAsync(includeMoney: false, CancellationToken.None);

        Assert.Null(summary.Sales);
        Assert.Equal(0, repository.SalesCalls);
    }

    [Fact]
    public async Task With_money_rights_revenue_splits_today_from_the_week()
    {
        var summary = await new DashboardService(new FakeRepository(), new FakeClock()).GetAsync(includeMoney: true, CancellationToken.None);

        Assert.NotNull(summary.Sales);
        Assert.Equal(12.5m, summary.Sales!.Today);
        Assert.Equal(12.5m + 6m, summary.Sales.Last7Days);
        Assert.Equal(7, summary.Sales.Daily.Count);
    }

    [Fact]
    public async Task Expiring_soon_uses_the_documented_horizon()
    {
        var repository = new FakeRepository();

        var summary = await new DashboardService(repository, new FakeClock()).GetAsync(false, CancellationToken.None);

        Assert.Equal(DashboardService.ExpiringDays, repository.ExpiringDaysAsked);
        Assert.Equal(DashboardService.ExpiringDays, summary.ExpiringWithinDays);
        Assert.Equal(1, summary.ExpiringSoon);
    }
}
