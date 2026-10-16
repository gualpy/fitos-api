using FitOS.Application.Auth;
using FitOS.Domain.Tenancy;

namespace FitOS.UnitTests.Auth;

public sealed class LoginServiceTests
{
    private static readonly LoginPolicy Policy = new(5, TimeSpan.FromMinutes(15));

    private static LoginRecord User(
        bool active = true,
        bool locked = false,
        string? tenantStatus = "active",
        Guid? tenantId = null,
        string hash = "hash:right") =>
        new(Guid.NewGuid(), tenantId ?? Guid.NewGuid(), hash, UserRole.Admin, "Ana", null, active, locked, tenantStatus);

    private sealed class FakeStore(LoginRecord? user) : ILoginStore
    {
        public List<string> Calls { get; } = [];

        public Task<LoginRecord?> FindAsync(string? tenantSlug, string username, CancellationToken cancellationToken)
        {
            Calls.Add($"find:{tenantSlug}:{username}");
            return Task.FromResult(user);
        }

        public Task RegisterFailureAsync(Guid userId, int maxFailures, TimeSpan lockout, CancellationToken cancellationToken)
        {
            Calls.Add($"failure:{maxFailures}:{lockout.TotalMinutes}");
            return Task.CompletedTask;
        }

        public Task RegisterSuccessAsync(Guid userId, CancellationToken cancellationToken)
        {
            Calls.Add("success");
            return Task.CompletedTask;
        }
    }

    private sealed class FakePasswords : IPasswordService
    {
        public int Verifications { get; private set; }

        public int DummyVerifications { get; private set; }

        public string Hash(string password) => "hash:" + password;

        public bool Verify(string hash, string password)
        {
            Verifications++;
            return hash == "hash:" + password;
        }

        public void VerifyDummy(string password) => DummyVerifications++;
    }

    private sealed class FakeIssuer : IAccessTokenIssuer
    {
        public AccessToken Issue(LoginRecord user) => new("token-for-" + user.UserId, DateTimeOffset.UnixEpoch);
    }

    private static (LoginService Service, FakeStore Store, FakePasswords Passwords) Create(LoginRecord? user)
    {
        var store = new FakeStore(user);
        var passwords = new FakePasswords();
        return (new LoginService(store, passwords, new FakeIssuer(), Policy), store, passwords);
    }

    [Fact]
    public async Task Correct_credentials_issue_a_token_and_register_success()
    {
        var user = User();
        var (service, store, _) = Create(user);

        var result = await service.LoginAsync("titan", "ana", "right", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("token-for-" + user.UserId, result.Token!.Value);
        Assert.Equal(user.TenantId, result.User!.TenantId);
        Assert.Equal(["find:titan:ana", "success"], store.Calls);
    }

    [Fact]
    public async Task Wrong_password_fails_and_counts_toward_the_lockout()
    {
        var (service, store, _) = Create(User());

        var result = await service.LoginAsync("titan", "ana", "wrong", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Token);
        Assert.Equal(["find:titan:ana", "failure:5:15"], store.Calls);
    }

    [Fact]
    public async Task Unknown_user_fails_after_the_same_hashing_work()
    {
        var (service, store, passwords) = Create(null);

        var result = await service.LoginAsync("titan", "ghost", "whatever", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(1, passwords.DummyVerifications); // not distinguishable by timing
        Assert.Equal(0, passwords.Verifications);
        Assert.Equal(["find:titan:ghost"], store.Calls);
    }

    [Fact]
    public async Task Locked_account_is_never_password_checked_even_with_the_right_password()
    {
        var (service, store, passwords) = Create(User(locked: true));

        var result = await service.LoginAsync("titan", "ana", "right", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, passwords.Verifications);
        Assert.Equal(1, passwords.DummyVerifications);
        Assert.DoesNotContain("failure:5:15", store.Calls); // attempts during a lock do not extend it
    }

    [Fact]
    public async Task Inactive_user_cannot_log_in_and_is_not_counted_as_a_guess()
    {
        var (service, store, _) = Create(User(active: false));

        var result = await service.LoginAsync("titan", "ana", "right", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(["find:titan:ana"], store.Calls);
    }

    [Theory]
    [InlineData("suspended")]
    [InlineData("cancelled")]
    [InlineData(null)]
    public async Task Users_of_a_non_active_tenant_cannot_log_in(string? status)
    {
        var (service, _, _) = Create(User(tenantStatus: status));

        Assert.False((await service.LoginAsync("titan", "ana", "right", CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task Trial_tenants_can_log_in()
    {
        var (service, _, _) = Create(User(tenantStatus: "trial"));

        Assert.True((await service.LoginAsync("titan", "ana", "right", CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task Platform_admin_has_no_tenant_status_and_can_log_in()
    {
        var admin = new LoginRecord(Guid.NewGuid(), null, "hash:right", UserRole.PlatformAdmin, "Root", null, true, false, null);
        var (service, _, _) = Create(admin);

        var result = await service.LoginAsync(null, "root", "right", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.User!.TenantId);
    }
}
