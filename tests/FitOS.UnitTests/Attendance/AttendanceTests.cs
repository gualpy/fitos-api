using FitOS.Application.Attendance;
using FitOS.Application.Common;
using FitOS.Application.Customers;
using FitOS.Application.Memberships;
using FitOS.Domain.Attendance;
using FitOS.Domain.Customers;
using FitOS.Domain.Memberships;
using FitOS.UnitTests.Members;

namespace FitOS.UnitTests.Attendance;

public sealed class AccessPolicyTests
{
    private static readonly BusinessDay Open = new(true, new TimeOnly(6, 0), new TimeOnly(22, 0));

    private static AccessDecision Decide(bool active = true, bool membership = true, bool allowWithoutPlan = false, BusinessDay? day = null, int hour = 9, int minute = 0) =>
        AccessPolicy.Decide(active, membership, allowWithoutPlan, day, new TimeOnly(hour, minute));

    [Fact]
    public void An_active_member_with_a_current_membership_comes_in() => Assert.True(Decide().Granted);

    [Fact]
    public void An_inactive_customer_is_denied_even_with_everything_else_in_order() =>
        Assert.Equal("customer_inactive", Decide(active: false, day: Open).Reason);

    [Fact]
    public void No_membership_is_denied_unless_the_gym_allows_entry_without_a_plan()
    {
        Assert.Equal("no_active_membership", Decide(membership: false).Reason);
        Assert.True(Decide(membership: false, allowWithoutPlan: true).Granted);
    }

    [Fact]
    public void A_closed_day_denies_everyone_with_a_plan_included() =>
        Assert.Equal("closed", Decide(day: new BusinessDay(false, default, default)).Reason);

    [Theory]
    [InlineData(5, 59, false)]
    [InlineData(6, 0, true)] // opening time itself is inside
    [InlineData(21, 59, true)]
    [InlineData(22, 0, false)] // at closing time the door is shut
    [InlineData(23, 30, false)]
    public void Opening_hours_are_half_open_open_inclusive_close_exclusive(int hour, int minute, bool granted)
    {
        var decision = Decide(day: Open, hour: hour, minute: minute);

        Assert.Equal(granted, decision.Granted);
        if (!granted)
        {
            Assert.Equal("outside_hours", decision.Reason);
        }
    }

    [Fact]
    public void Without_configured_hours_the_time_of_day_does_not_restrict() =>
        Assert.True(Decide(day: null, hour: 3).Granted);

    [Fact]
    public void Hours_come_before_the_membership_check_so_the_reason_is_the_door_not_the_plan() =>
        Assert.Equal("closed", Decide(membership: false, day: new BusinessDay(false, default, default)).Reason);

    [Theory]
    [InlineData("2026-10-12", 1)] // Monday
    [InlineData("2026-10-16", 5)] // Friday
    [InlineData("2026-10-17", 6)]
    [InlineData("2026-10-18", 7)] // Sunday is 7, not 0
    public void Weekdays_follow_the_iso_convention_of_the_business_hours_table(string date, int expected) =>
        Assert.Equal(expected, TenantTime.IsoWeekday(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void A_local_day_starts_at_the_right_utc_instant_and_dates_follow_the_zone()
    {
        var ecuador = TimeZoneInfo.CreateCustomTimeZone("EC", TimeSpan.FromHours(-5), "EC", "EC");

        Assert.Equal(new DateTimeOffset(2026, 10, 16, 5, 0, 0, TimeSpan.Zero), TenantTime.StartOfDayUtc(ecuador, new DateOnly(2026, 10, 16)));
        // 03:00 UTC on Oct 17 is still 22:00 on Oct 16 in Ecuador.
        var instant = new DateTimeOffset(2026, 10, 17, 3, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 10, 16), TenantTime.LocalDate(ecuador, instant));
        Assert.Equal(new TimeOnly(22, 0), TenantTime.LocalTime(ecuador, instant));
    }
}

public sealed class AttendanceServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 14, 0, 0, TimeSpan.Zero); // Friday 09:00 in Ecuador

    private static readonly TimeZoneInfo Ecuador = TimeZoneInfo.CreateCustomTimeZone("EC", TimeSpan.FromHours(-5), "EC", "EC");

    private sealed class Clock(TimeZoneInfo zone) : ITenantClock
    {
        public Task<TimeZoneInfo> ZoneAsync(CancellationToken cancellationToken) => Task.FromResult(zone);

        public Task<DateOnly> TodayAsync(CancellationToken cancellationToken) => Task.FromResult(TenantTime.LocalDate(zone, Now));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Settings(bool allowWithoutPlan) : ITenantSettings
    {
        public Task<bool> AllowWithoutPlanAsync(CancellationToken cancellationToken) => Task.FromResult(allowWithoutPlan);
    }

    private sealed class Branches(params Guid[] ids) : IBranchRepository
    {
        public Task<IReadOnlyList<Guid>> ActiveIdsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>(ids);
    }

    private sealed class Hours(BusinessDay? day) : IBusinessHoursRepository
    {
        public int? AskedWeekday { get; private set; }

        public Task<BusinessDay?> GetAsync(Guid branchId, int isoWeekday, CancellationToken cancellationToken)
        {
            AskedWeekday = isoWeekday;
            return Task.FromResult(day);
        }
    }

    private sealed class Attendances : IAttendanceRepository
    {
        public List<AttendanceEntry> Items { get; } = [];

        public int Saves { get; private set; }

        public AttendanceQuery? LastQuery { get; private set; }

        public Task<AttendanceEntry?> GetOpenAsync(Guid customerId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.FirstOrDefault(a => a.CustomerId == customerId && a.CheckOut is null && a.Result == AttendanceResult.Granted));

        public Task<PagedResult<AttendanceView>> SearchAsync(AttendanceQuery query, CancellationToken cancellationToken)
        {
            LastQuery = query;
            return Task.FromResult(new PagedResult<AttendanceView>(
                Items.Select(i => new AttendanceView(i, "Ana", 7)).ToList(), query.Page, query.PageSize, Items.Count));
        }

        public void Add(AttendanceEntry entry) => Items.Add(entry);

        public Task SaveAsync(CancellationToken cancellationToken)
        {
            Saves++;
            return Task.CompletedTask;
        }
    }

    private readonly Guid _tenant = Guid.NewGuid();

    private readonly Guid _branch = Guid.NewGuid();

    private readonly Customer _customer = new() { Name = "Ana Pérez", ClientCode = 7, RfidCard = "CARD-7" };

    private readonly MemoryCustomers _customers = new();

    private readonly MemoryMemberships _memberships = new();

    private readonly Attendances _attendance = new();

    private readonly FixedTime _time = new(Now);

    public AttendanceServiceTests()
    {
        _customer.TenantId = _tenant;
        _customers.Items.Add(_customer);
        _memberships.Items.Add(new Membership { TenantId = _tenant, CustomerId = _customer.Id, PlanId = Guid.NewGuid(), StartsOn = new DateOnly(2026, 10, 1), EndsOn = new DateOnly(2026, 10, 31) });
    }

    private AttendanceService Service(BusinessDay? day = null, bool allowWithoutPlan = false, Guid[]? branches = null, Hours? hours = null) => new(
        _attendance,
        _customers,
        _memberships,
        new Branches(branches ?? [_branch]),
        hours ?? new Hours(day),
        new Settings(allowWithoutPlan),
        new Clock(Ecuador),
        new FixedTenant(_tenant),
        _time);

    private IdentifyCustomer ById => new(_customer.Id, null, null);

    private Task<AttendanceItem> CheckIn(AttendanceService? service = null, Guid? branch = null) =>
        (service ?? Service()).CheckInAsync(new CheckInInput(ById, branch), CancellationToken.None);

    [Fact]
    public async Task A_member_with_a_current_membership_is_let_in_and_the_entry_is_recorded()
    {
        var item = await CheckIn();

        Assert.Equal("granted", item.Result);
        Assert.Null(item.DenyReason);
        Assert.Equal(new DateOnly(2026, 10, 16), item.Date);
        Assert.Equal(Now, item.CheckIn);
        Assert.Null(item.CheckOut);
        Assert.Equal("Ana Pérez", item.CustomerName);
        var stored = Assert.Single(_attendance.Items);
        Assert.Equal(_tenant, stored.TenantId);
        Assert.Equal(_branch, stored.BranchId);
    }

    [Fact]
    public async Task Hours_are_checked_against_the_gyms_local_clock_not_utc()
    {
        var hours = new Hours(new BusinessDay(true, new TimeOnly(6, 0), new TimeOnly(10, 0)));

        // 14:00 UTC is 09:00 in Ecuador: inside 06-10. In UTC terms it would look like closing time was long past.
        Assert.Equal("granted", (await CheckIn(Service(hours: hours))).Result);
        Assert.Equal(5, hours.AskedWeekday); // Friday
    }

    [Theory]
    [InlineData("closed", false)]
    [InlineData("outside_hours", true)] // it is 09:00 local and the gym opens at 18:00
    public async Task Denied_scans_are_recorded_with_their_reason(string reason, bool isOpenDay)
    {
        BusinessDay day = isOpenDay ? new BusinessDay(true, new TimeOnly(18, 0), new TimeOnly(22, 0)) : new BusinessDay(false, default, default);

        var item = await CheckIn(Service(day));

        Assert.Equal("denied", item.Result);
        Assert.Equal(reason, item.DenyReason);
        Assert.Equal(AttendanceResult.Denied, Assert.Single(_attendance.Items).Result);
    }

    [Fact]
    public async Task A_customer_without_a_membership_is_denied_unless_the_gym_allows_it()
    {
        _memberships.Items.Clear();

        Assert.Equal("no_active_membership", (await CheckIn()).DenyReason);
        Assert.Equal("granted", (await CheckIn(Service(allowWithoutPlan: true))).Result);
    }

    [Fact]
    public async Task An_inactive_customer_is_denied_and_the_attempt_is_still_recorded()
    {
        _customer.Status = CustomerStatus.Inactive;

        var item = await CheckIn();

        Assert.Equal("customer_inactive", item.DenyReason);
        Assert.Single(_attendance.Items);
    }

    [Fact]
    public async Task Being_inside_already_refuses_a_second_entry_until_the_exit_is_registered()
    {
        await CheckIn();

        var ex = await Assert.ThrowsAsync<ConflictException>(() => CheckIn());
        Assert.Equal("already_inside", ex.Code);
        Assert.Single(_attendance.Items);

        await Service().CheckOutAsync(ById, CancellationToken.None);
        Assert.Equal("granted", (await CheckIn()).Result);
    }

    [Fact]
    public async Task A_denied_attempt_does_not_count_as_being_inside()
    {
        _customer.Status = CustomerStatus.Inactive;
        await CheckIn();
        await CheckIn(); // a second denied attempt is fine: nobody is inside

        Assert.Equal(2, _attendance.Items.Count);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Service().CheckOutAsync(ById, CancellationToken.None));
        Assert.Equal("no_open_entry", ex.Code);
    }

    [Fact]
    public async Task A_forgotten_entry_is_closed_visibly_so_it_does_not_block_the_next_visit()
    {
        var forgotten = new AttendanceEntry { TenantId = _tenant, BranchId = _branch, CustomerId = _customer.Id, CheckIn = Now.AddHours(-30) };
        _attendance.Items.Add(forgotten);

        var item = await CheckIn();

        Assert.Equal("granted", item.Result);
        Assert.Equal(forgotten.CheckIn, forgotten.CheckOut); // the real exit is unknown: no invented hours
        Assert.Equal(Now, forgotten.AutoClosedAt);
        Assert.Equal(2, _attendance.Saves); // closed first, then the new entry (the database allows one open entry only)
        Assert.Equal(2, _attendance.Items.Count);
    }

    [Fact]
    public async Task An_entry_open_for_less_than_a_day_is_a_real_session()
    {
        _attendance.Items.Add(new AttendanceEntry { TenantId = _tenant, BranchId = _branch, CustomerId = _customer.Id, CheckIn = Now.AddHours(-23) });

        await Assert.ThrowsAsync<ConflictException>(() => CheckIn());
    }

    [Fact]
    public async Task Check_out_records_the_time_and_the_length_of_the_stay()
    {
        await CheckIn();
        _time.Now = Now.AddMinutes(95);

        var item = await Service().CheckOutAsync(ById, CancellationToken.None);

        Assert.Equal(Now.AddMinutes(95), item.CheckOut);
        Assert.Equal(95, item.Minutes);
    }

    [Fact]
    public async Task Check_out_without_an_open_entry_is_a_conflict()
    {
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Service().CheckOutAsync(ById, CancellationToken.None));

        Assert.Equal("no_open_entry", ex.Code);
    }

    [Fact]
    public async Task The_customer_can_be_identified_by_id_client_code_or_card_but_by_exactly_one()
    {
        var service = Service();

        Assert.Equal(_customer.Id, (await service.CheckInAsync(new CheckInInput(new IdentifyCustomer(_customer.Id, null, null), null), CancellationToken.None)).CustomerId);
        await service.CheckOutAsync(new IdentifyCustomer(null, 7, null), CancellationToken.None);
        Assert.Equal(_customer.Id, (await service.CheckInAsync(new CheckInInput(new IdentifyCustomer(null, null, "  CARD-7 "), null), CancellationToken.None)).CustomerId);

        foreach (var who in new[] { new IdentifyCustomer(null, null, null), new IdentifyCustomer(_customer.Id, 7, null), new IdentifyCustomer(null, null, "   ") })
        {
            var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => service.CheckInAsync(new CheckInInput(who, null), CancellationToken.None));
            Assert.Equal("exactly_one_required", ex.Errors["identifier"]);
        }

        await Assert.ThrowsAsync<NotFoundException>(() => service.CheckInAsync(new CheckInInput(new IdentifyCustomer(null, 999, null), null), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => service.CheckInAsync(new CheckInInput(new IdentifyCustomer(null, null, "NOPE"), null), CancellationToken.None));
    }

    [Fact]
    public async Task The_branch_is_inferred_when_there_is_one_and_required_when_there_are_several()
    {
        var other = Guid.NewGuid();

        Assert.Equal(_branch, (await CheckIn(Service(branches: [_branch]))).BranchId);
        await Service().CheckOutAsync(ById, CancellationToken.None); // leave, so the next scans are not "already inside"

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => CheckIn(Service(branches: [_branch, other])));
        Assert.Equal("required", ex.Errors["branchId"]);
        Assert.Equal(other, (await CheckIn(Service(branches: [_branch, other]), other)).BranchId);
    }

    [Fact]
    public async Task An_unknown_or_missing_branch_is_rejected()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => CheckIn(Service(), Guid.NewGuid()));

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => CheckIn(Service(branches: [])));
        Assert.Equal("no_active_branch", ex.Errors["branchId"]);
    }

    [Fact]
    public async Task Date_filters_become_utc_bounds_of_the_gyms_local_days_and_include_the_whole_last_day()
    {
        await Service().ListAsync(new AttendanceFilter(new DateOnly(2026, 10, 16), new DateOnly(2026, 10, 16), null, null, 1, 25), CancellationToken.None);

        Assert.Equal(new DateTimeOffset(2026, 10, 16, 5, 0, 0, TimeSpan.Zero), _attendance.LastQuery!.FromUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 5, 0, 0, TimeSpan.Zero), _attendance.LastQuery.ToUtcExclusive);
    }

    [Fact]
    public async Task An_inverted_range_is_rejected_and_paging_is_bounded()
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(
            () => Service().ListAsync(new AttendanceFilter(new DateOnly(2026, 10, 16), new DateOnly(2026, 10, 15), null, null, 1, 25), CancellationToken.None));
        Assert.Equal("before_from", ex.Errors["to"]);

        await Service().ListAsync(new AttendanceFilter(null, null, null, null, -3, 100_000), CancellationToken.None);
        Assert.Equal(1, _attendance.LastQuery!.Page);
        Assert.Equal(AttendanceService.MaxPageSize, _attendance.LastQuery.PageSize);
        Assert.Null(_attendance.LastQuery.FromUtc);
    }

    [Fact]
    public async Task Listing_one_customers_history_filters_by_them_and_needs_them_to_exist()
    {
        await Service().ListForCustomerAsync(_customer.Id, new AttendanceFilter(null, null, null, null, 1, 25), CancellationToken.None);

        Assert.Equal(_customer.Id, _attendance.LastQuery!.CustomerId);
        await Assert.ThrowsAsync<NotFoundException>(
            () => Service().ListForCustomerAsync(Guid.NewGuid(), new AttendanceFilter(null, null, null, null, 1, 25), CancellationToken.None));
    }

    [Fact]
    public async Task Local_dates_in_the_history_follow_the_gyms_zone_even_late_at_night()
    {
        // 03:00 UTC on Oct 17 is 22:00 on Oct 16 in Ecuador: the visit belongs to Oct 16.
        _attendance.Items.Add(new AttendanceEntry { TenantId = _tenant, BranchId = _branch, CustomerId = _customer.Id, CheckIn = new DateTimeOffset(2026, 10, 17, 3, 0, 0, TimeSpan.Zero), CheckOut = new DateTimeOffset(2026, 10, 17, 4, 15, 0, TimeSpan.Zero) });

        var page = await Service().ListAsync(new AttendanceFilter(null, null, null, null, 1, 25), CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(new DateOnly(2026, 10, 16), item.Date);
        Assert.Equal(new TimeOnly(22, 0), item.CheckInTime); // the gym's own clock, not UTC
        Assert.Equal(new TimeOnly(23, 15), item.CheckOutTime);
        Assert.Equal(75, item.Minutes);
    }
}
