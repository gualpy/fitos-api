using FitOS.Domain.Memberships;

namespace FitOS.UnitTests.Members;

public sealed class MembershipRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 16);

    [Fact]
    public void Without_history_it_starts_today_and_the_end_is_inclusive()
    {
        var (start, end) = MembershipRules.Schedule(Today, null, null, 1);

        Assert.Equal(Today, start);
        Assert.Equal(new DateOnly(2026, 11, 15), end);
    }

    [Fact]
    public void A_requested_future_start_is_respected()
    {
        var (start, end) = MembershipRules.Schedule(Today, new DateOnly(2026, 11, 1), null, 2);

        Assert.Equal(new DateOnly(2026, 11, 1), start);
        Assert.Equal(new DateOnly(2026, 12, 31), end);
    }

    [Fact]
    public void A_customer_still_covered_gets_the_new_membership_queued_right_after()
    {
        var (start, end) = MembershipRules.Schedule(Today, null, new DateOnly(2026, 10, 30), 1);

        Assert.Equal(new DateOnly(2026, 10, 31), start);
        Assert.Equal(new DateOnly(2026, 11, 29), end); // Oct 31 + 1 month clamps to Nov 30; the inclusive last day is Nov 29
    }

    [Fact]
    public void A_requested_start_inside_the_covered_period_is_pushed_after_it_so_nothing_overlaps()
    {
        var (start, _) = MembershipRules.Schedule(Today, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 30), 1);

        Assert.Equal(new DateOnly(2026, 10, 31), start);
    }

    [Fact]
    public void Coverage_that_ends_the_day_before_does_not_delay_the_start()
    {
        var (start, _) = MembershipRules.Schedule(Today, null, Today.AddDays(-1), 1);

        Assert.Equal(Today, start);
    }

    [Fact]
    public void Coverage_ending_today_pushes_the_new_one_to_tomorrow()
    {
        var (start, _) = MembershipRules.Schedule(Today, null, Today, 1);

        Assert.Equal(Today.AddDays(1), start);
    }

    [Theory]
    [InlineData(2026, 1, 31, 1, 2026, 2, 27)] // month-end clamps: Jan 31 + 1 month = Feb 28, minus a day
    [InlineData(2028, 1, 31, 1, 2028, 2, 28)] // leap year
    [InlineData(2026, 1, 1, 12, 2026, 12, 31)]
    [InlineData(2026, 3, 15, 3, 2026, 6, 14)]
    public void End_date_counts_calendar_months(int y, int m, int d, int months, int ey, int em, int ed)
    {
        var (_, end) = MembershipRules.Schedule(new DateOnly(y, m, d), null, null, months);

        Assert.Equal(new DateOnly(ey, em, ed), end);
    }

    [Theory]
    [InlineData(MembershipStatus.Active, "2026-10-01", "2026-10-31", MembershipState.Current)]
    [InlineData(MembershipStatus.Active, "2026-10-16", "2026-10-16", MembershipState.Current)] // one-day membership, today
    [InlineData(MembershipStatus.Active, "2026-10-01", "2026-10-16", MembershipState.Current)] // last day still counts
    [InlineData(MembershipStatus.Active, "2026-10-01", "2026-10-15", MembershipState.Expired)]
    [InlineData(MembershipStatus.Active, "2026-10-17", "2026-11-16", MembershipState.Scheduled)]
    [InlineData(MembershipStatus.Cancelled, "2026-10-01", "2026-10-31", MembershipState.Cancelled)]
    [InlineData(MembershipStatus.Cancelled, "2026-09-01", "2026-09-30", MembershipState.Cancelled)] // cancelled wins over expired
    [InlineData(MembershipStatus.Expired, "2026-10-01", "2026-10-31", MembershipState.Expired)]
    public void State_is_derived_from_status_and_dates(MembershipStatus status, string from, string to, MembershipState expected) =>
        Assert.Equal(expected, MembershipRules.StateOf(status, DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture), Today));
}
