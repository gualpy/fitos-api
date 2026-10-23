using FitOS.Application.Auth;
using FitOS.Application.Common;
using FitOS.Application.Platform;
using FitOS.Application.Subscriptions;

namespace FitOS.UnitTests.Platform;

public sealed class PlatformServicesTests
{
    private sealed class FakePasswords : IPasswordService
    {
        public string Hash(string password) => "h:" + password;

        public bool Verify(string hash, string password) => true;

        public PasswordCheck Check(string hash, string password) => PasswordCheck.Success;

        public void VerifyDummy(string password)
        {
        }
    }

    private sealed class FakePlatformStore : IPlatformStore
    {
        public TenantOwner? Owner { get; init; }

        public (int Months, decimal Amount)? Extended { get; private set; }

        public string? Status { get; private set; }

        public bool Exists { get; init; } = true;

        public Task<IReadOnlyList<TenantSummary>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TenantSummary>>([]);

        public Task<DateOnly?> ExtendAsync(Guid tenantId, int months, decimal amount, Guid actorId, CancellationToken cancellationToken)
        {
            Extended = (months, amount);
            return Task.FromResult<DateOnly?>(Exists ? new DateOnly(2027, 1, 1) : null);
        }

        public Task<bool> SetStatusAsync(Guid tenantId, string status, Guid actorId, CancellationToken cancellationToken)
        {
            Status = status;
            return Task.FromResult(Exists);
        }

        public Task<IReadOnlyList<MonthlyRevenue>> RevenueAsync(int months, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MonthlyRevenue>>([]);

        public Task<TenantOwner?> FindOwnerAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(Owner);
    }

    private static PlatformService Service(FakePlatformStore store, FakeEmail? email = null) =>
        new(store, new PasswordSetupService(
            new FakeSetupStore(), new FakePasswords(), email ?? new FakeEmail(), new FakeLog(),
            new SetupLinkPolicy("https://app.test", TimeSpan.FromHours(48)), new FixedTime(DateTimeOffset.UtcNow)));

    [Theory]
    [InlineData(0, 20, "months")]
    [InlineData(25, 20, "months")]
    [InlineData(null, 20, "months")]
    [InlineData(1, -1, "amount")]
    [InlineData(1, 100001, "amount")]
    [InlineData(1, 10.555, "amount")]
    public async Task Extending_with_invalid_numbers_is_rejected_and_nothing_is_stored(int? months, double amount, string field)
    {
        var store = new FakePlatformStore();

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(
            () => Service(store).ExtendAsync(Guid.NewGuid(), months, (decimal)amount, Guid.NewGuid(), CancellationToken.None));

        Assert.Contains(field, ex.Errors.Keys);
        Assert.Null(store.Extended);
    }

    [Fact]
    public async Task A_missing_amount_is_required_not_free()
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(
            () => Service(new FakePlatformStore()).ExtendAsync(Guid.NewGuid(), 1, null, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal("required", ex.Errors["amount"]);
    }

    [Fact]
    public async Task A_valid_extension_goes_to_the_store_and_a_zero_amount_is_allowed()
    {
        var store = new FakePlatformStore();

        var ends = await Service(store).ExtendAsync(Guid.NewGuid(), 3, 0m, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(new DateOnly(2027, 1, 1), ends);
        Assert.Equal((3, 0m), store.Extended);
    }

    [Fact]
    public async Task Unknown_gyms_are_not_found()
    {
        var store = new FakePlatformStore { Exists = false };

        await Assert.ThrowsAsync<NotFoundException>(() => Service(store).ExtendAsync(Guid.NewGuid(), 1, 20m, Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => Service(store).SetSuspendedAsync(Guid.NewGuid(), true, Guid.NewGuid(), CancellationToken.None));
    }

    [Theory]
    [InlineData(true, "suspended")]
    [InlineData(false, "active")]
    public async Task Suspend_and_reactivate_map_to_statuses(bool suspend, string status)
    {
        var store = new FakePlatformStore();

        await Service(store).SetSuspendedAsync(Guid.NewGuid(), suspend, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(status, store.Status);
    }

    private static TenantOwner Owner(bool pending, string? email = "o@t.test") =>
        new(Guid.NewGuid(), "dueno", "Dueño", email, pending, "Titan", "titan");

    [Fact]
    public async Task Resending_the_welcome_sends_a_link_to_an_owner_who_never_set_a_password()
    {
        var mail = new FakeEmail();

        await Service(new FakePlatformStore { Owner = Owner(pending: true) }, mail).ResendWelcomeAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Single(mail.Sent);
    }

    [Fact]
    public async Task Resending_is_refused_when_the_owner_already_has_a_password_or_no_email()
    {
        var active = await Assert.ThrowsAsync<ConflictException>(
            () => Service(new FakePlatformStore { Owner = Owner(pending: false) }).ResendWelcomeAsync(Guid.NewGuid(), CancellationToken.None));
        var noEmail = await Assert.ThrowsAsync<ConflictException>(
            () => Service(new FakePlatformStore { Owner = Owner(pending: true, email: null) }).ResendWelcomeAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal("owner_already_active", active.Code);
        Assert.Equal("owner_without_email", noEmail.Code);
    }

    private sealed class FakeReader(DateOnly? endsOn) : ISubscriptionReader
    {
        public Task<DateOnly?> EndsOnAsync(CancellationToken cancellationToken) => Task.FromResult(endsOn);
    }

    private sealed class FakeClock(DateOnly today) : ITenantClock
    {
        public Task<TimeZoneInfo> ZoneAsync(CancellationToken cancellationToken) => Task.FromResult(TimeZoneInfo.Utc);

        public Task<DateOnly> TodayAsync(CancellationToken cancellationToken) => Task.FromResult(today);
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(4, false)]
    [InlineData(3, true)]
    [InlineData(1, true)]
    [InlineData(0, true)]
    public async Task The_reminder_shows_from_three_days_before_the_last_day(int daysLeft, bool reminder)
    {
        var today = new DateOnly(2026, 10, 23);
        var service = new SubscriptionService(new FakeReader(today.AddDays(daysLeft)), new FakeClock(today), new SubscriptionPolicy(30, 3));

        var status = await service.GetAsync(CancellationToken.None);

        Assert.Equal(daysLeft, status.DaysLeft);
        Assert.Equal(reminder, status.ShowReminder);
    }

    [Fact]
    public async Task A_gym_without_a_subscription_row_is_not_found()
    {
        var service = new SubscriptionService(new FakeReader(null), new FakeClock(new DateOnly(2026, 10, 23)), new SubscriptionPolicy(30, 3));

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(CancellationToken.None));
    }
}
