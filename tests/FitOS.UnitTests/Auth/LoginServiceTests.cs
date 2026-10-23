using System.Text.Json;
using FitOS.Application.Auth;
using FitOS.Domain.Tenancy;

namespace FitOS.UnitTests.Auth;

public sealed class LoginServiceTests
{
    private static readonly LoginPolicy Policy = new(5, TimeSpan.FromMinutes(15));

    private static readonly ClientInfo Client = new("203.0.113.5", "Mozilla/5.0 test");

    private static LoginRecord User(
        bool active = true,
        bool locked = false,
        string? tenantStatus = "active",
        Guid? tenantId = null,
        string hash = "hash:right") =>
        new(Guid.NewGuid(), tenantId ?? Guid.NewGuid(), hash, UserRole.Admin, "Ana", null, active, locked, tenantStatus);

    private sealed class FakeStore(LoginRecord? user, bool locksNow = false) : ILoginStore
    {
        public List<string> Calls { get; } = [];

        public string? UpgradedHash { get; private set; }

        public Task<LoginRecord?> FindAsync(string? tenantSlug, string username, CancellationToken cancellationToken)
        {
            Calls.Add($"find:{tenantSlug}:{username}");
            return Task.FromResult(user);
        }

        public Task<LoginRecord?> FindByIdAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(user);

        public Task<bool> RegisterFailureAsync(Guid userId, int maxFailures, TimeSpan lockout, CancellationToken cancellationToken)
        {
            Calls.Add($"failure:{maxFailures}:{lockout.TotalMinutes}");
            return Task.FromResult(locksNow);
        }

        public Task RegisterSuccessAsync(Guid userId, CancellationToken cancellationToken)
        {
            Calls.Add("success");
            return Task.CompletedTask;
        }

        public Task UpgradePasswordHashAsync(Guid userId, string newHash, CancellationToken cancellationToken)
        {
            Calls.Add("rehash");
            UpgradedHash = newHash;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePasswords(bool rehashNeeded = false) : IPasswordService
    {
        public int Verifications { get; private set; }

        public int DummyVerifications { get; private set; }

        public string Hash(string password) => "hash:" + password;

        public bool Verify(string hash, string password) => Check(hash, password) != PasswordCheck.Failed;

        public PasswordCheck Check(string hash, string password)
        {
            Verifications++;
            return hash != "hash:" + password ? PasswordCheck.Failed : rehashNeeded ? PasswordCheck.SuccessRehashNeeded : PasswordCheck.Success;
        }

        public void VerifyDummy(string password) => DummyVerifications++;
    }

    private sealed class FakeIssuer : IAccessTokenIssuer
    {
        public AccessToken Issue(LoginRecord user, Guid sessionId) => new("token-for-" + user.UserId + "-" + sessionId, DateTimeOffset.UnixEpoch);
    }

    private sealed class FakeSessions : ISessionStore
    {
        public List<(Guid UserId, string? UserAgent)> Created { get; } = [];

        public Task<(Guid SessionId, RefreshGrant Refresh)> CreateAsync(Guid userId, string? userAgent, CancellationToken cancellationToken)
        {
            Created.Add((userId, userAgent));
            return Task.FromResult((Guid.NewGuid(), new RefreshGrant("refresh-" + userId, DateTimeOffset.UnixEpoch)));
        }

        public Task<RefreshOutcome> RotateAsync(string presentedRefreshToken, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RevokeAsync(Guid sessionId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeLog : ISecurityLog
    {
        public List<SecurityEvent> Events { get; } = [];

        public Task WriteAsync(SecurityEvent securityEvent, CancellationToken cancellationToken)
        {
            Events.Add(securityEvent);
            return Task.CompletedTask;
        }
    }

    private sealed record Rig(LoginService Service, FakeStore Store, FakePasswords Passwords, FakeSessions Sessions, FakeLog Log, FakeEmail Email, FakeChallengeStore Challenges, FixedTime Time);

    private static Rig Build(LoginRecord? user, bool locksNow = false, bool rehashNeeded = false, bool confirmPlatform = true, string? adminEmail = "admin@platform.test")
    {
        var store = new FakeStore(user, locksNow);
        var passwords = new FakePasswords(rehashNeeded);
        var sessions = new FakeSessions();
        var log = new FakeLog();
        var time = new FixedTime(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        var email = new FakeEmail();
        var challengeStore = new FakeChallengeStore { Email = adminEmail, Now = time.Now };
        var challenges = new LoginChallengeService(challengeStore, email, log, new LoginChallengePolicy("https://app.test/", TimeSpan.FromMinutes(10), confirmPlatform), time);
        return new Rig(new LoginService(store, passwords, new FakeIssuer(), sessions, log, Policy, challenges), store, passwords, sessions, log, email, challengeStore, time);
    }

    private static Task<LoginResult> Login(Rig rig, string password = "right", string username = "ana") =>
        rig.Service.LoginAsync("titan", username, password, Client, CancellationToken.None);

    [Fact]
    public async Task Correct_credentials_issue_a_token_and_register_success()
    {
        var user = User();
        var rig = Build(user);

        var result = await Login(rig);

        Assert.True(result.Succeeded);
        Assert.StartsWith("token-for-" + user.UserId, result.Token!.Value, StringComparison.Ordinal);
        Assert.Equal(user.TenantId, result.User!.TenantId);
        Assert.Equal(["find:titan:ana", "success"], rig.Store.Calls);
    }

    [Fact]
    public async Task A_successful_login_starts_a_session_and_returns_its_renewal_token()
    {
        var user = User();
        var rig = Build(user);

        var result = await Login(rig);

        Assert.Equal("refresh-" + user.UserId, result.Refresh!.Token);
        Assert.Equal([(user.UserId, "Mozilla/5.0 test")], rig.Sessions.Created);
        Assert.Contains(rig.Sessions.Created[0].UserId.ToString(), result.Token!.Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("locked")]
    [InlineData("inactive")]
    [InlineData("unknown")]
    public async Task A_failed_login_never_starts_a_session(string why)
    {
        var record = why switch
        {
            "locked" => User(locked: true),
            "inactive" => User(active: false),
            "unknown" => null,
            _ => User(),
        };
        var rig = Build(record);

        var result = await Login(rig, why == "wrong" ? "bad" : "right");

        Assert.False(result.Succeeded);
        Assert.Null(result.Refresh);
        Assert.Empty(rig.Sessions.Created);
    }

    [Fact]
    public async Task Wrong_password_fails_and_counts_toward_the_lockout()
    {
        var rig = Build(User());

        var result = await Login(rig, "wrong");

        Assert.False(result.Succeeded);
        Assert.Null(result.Token);
        Assert.Equal(["find:titan:ana", "failure:5:15"], rig.Store.Calls);
    }

    [Fact]
    public async Task Unknown_user_fails_after_the_same_hashing_work()
    {
        var rig = Build(null);

        var result = await Login(rig, "whatever", "ghost");

        Assert.False(result.Succeeded);
        Assert.Equal(1, rig.Passwords.DummyVerifications); // not distinguishable by timing
        Assert.Equal(0, rig.Passwords.Verifications);
        Assert.Equal(["find:titan:ghost"], rig.Store.Calls);
    }

    [Fact]
    public async Task Locked_account_is_never_password_checked_even_with_the_right_password()
    {
        var rig = Build(User(locked: true));

        var result = await Login(rig);

        Assert.False(result.Succeeded);
        Assert.Equal(0, rig.Passwords.Verifications);
        Assert.Equal(1, rig.Passwords.DummyVerifications);
        Assert.DoesNotContain("failure:5:15", rig.Store.Calls); // attempts during a lock do not extend it
    }

    [Fact]
    public async Task Inactive_user_cannot_log_in_and_is_not_counted_as_a_guess()
    {
        var rig = Build(User(active: false));

        var result = await Login(rig);

        Assert.False(result.Succeeded);
        Assert.Equal(["find:titan:ana"], rig.Store.Calls);
    }

    [Theory]
    [InlineData("suspended")]
    [InlineData("cancelled")]
    [InlineData(null)]
    public async Task Users_of_a_non_active_tenant_cannot_log_in(string? status)
    {
        var rig = Build(User(tenantStatus: status));

        Assert.False((await Login(rig)).Succeeded);
    }

    [Fact]
    public async Task Trial_tenants_can_log_in()
    {
        var rig = Build(User(tenantStatus: "trial"));

        Assert.True((await Login(rig)).Succeeded);
    }

    [Fact]
    public async Task A_customer_login_carries_the_customer_id_so_clients_can_load_their_own_record()
    {
        var customerId = Guid.NewGuid();
        var customer = new LoginRecord(Guid.NewGuid(), Guid.NewGuid(), "hash:right", UserRole.Customer, "Juan", customerId, true, false, "active");
        var rig = Build(customer);

        var result = await Login(rig);

        Assert.Equal(customerId, result.User!.CustomerId);
    }

    [Fact]
    public async Task Platform_admin_has_no_tenant_status_and_can_log_in()
    {
        var admin = new LoginRecord(Guid.NewGuid(), null, "hash:right", UserRole.PlatformAdmin, "Root", null, true, false, null);
        var rig = Build(admin, confirmPlatform: false); // the e-mail step has its own tests

        var result = await rig.Service.LoginAsync(null, "root", "right", Client, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.User!.TenantId);
    }

    // ---------------------------------------------------------------------------------------- security log

    [Fact]
    public async Task A_good_login_is_logged_with_who_and_from_where()
    {
        var user = User();
        var rig = Build(user);

        await Login(rig);

        var logged = Assert.Single(rig.Log.Events);
        Assert.Equal("login_success", logged.Name);
        Assert.Equal(user.UserId, logged.UserId);
        Assert.Equal(user.TenantId, logged.TenantId);
        Assert.Equal(Client, logged.Client);
    }

    [Theory]
    [InlineData("wrong", "bad_password")]
    [InlineData("locked", "locked")]
    [InlineData("inactive", "inactive")]
    [InlineData("tenant", "tenant_inactive")]
    public async Task Each_kind_of_failure_is_logged_with_its_reason_against_the_user(string why, string reason)
    {
        var user = why switch
        {
            "locked" => User(locked: true),
            "inactive" => User(active: false),
            "tenant" => User(tenantStatus: "suspended"),
            _ => User(),
        };
        var rig = Build(user);

        await Login(rig, why == "wrong" ? "bad" : "right");

        var logged = Assert.Single(rig.Log.Events);
        Assert.Equal("login_failed", logged.Name);
        Assert.Equal(reason, logged.Detail!["reason"]);
        Assert.Equal(user.UserId, logged.UserId);
    }

    [Fact]
    public async Task An_unknown_user_is_logged_without_the_typed_name_or_password()
    {
        var rig = Build(null);

        await Login(rig, password: "my-secret-pass-1", username: "hunter2-typed-in-the-wrong-box");

        var logged = Assert.Single(rig.Log.Events);
        Assert.Equal("login_failed", logged.Name);
        Assert.Null(logged.UserId);
        var everything = JsonSerializer.Serialize(logged);
        Assert.DoesNotContain("hunter2", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("my-secret-pass-1", everything, StringComparison.Ordinal);
        Assert.Equal("unknown_user", logged.Detail!["reason"]);
        Assert.Equal("titan", logged.Detail["tenantSlug"]); // the gym is not a secret and helps spot probing
    }

    [Fact]
    public async Task Never_does_a_log_entry_contain_the_password_that_was_tried()
    {
        var rig = Build(User(), locksNow: true);

        await Login(rig, "tried-this-password-9");

        Assert.DoesNotContain("tried-this-password-9", JsonSerializer.Serialize(rig.Log.Events), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_failure_that_locks_the_account_also_logs_account_locked_once()
    {
        var user = User();
        var rig = Build(user, locksNow: true);

        await Login(rig, "wrong");

        Assert.Equal(["login_failed", "account_locked"], rig.Log.Events.Select(e => e.Name).ToArray());
        Assert.Equal(15, rig.Log.Events[1].Detail!["minutes"]);
        Assert.Equal(user.UserId, rig.Log.Events[1].UserId);
    }

    [Fact]
    public async Task A_failure_that_does_not_lock_logs_no_lock_event()
    {
        var rig = Build(User(), locksNow: false);

        await Login(rig, "wrong");

        Assert.Equal(["login_failed"], rig.Log.Events.Select(e => e.Name).ToArray());
    }

    // ---------------------------------------------------------------------------------------- stronger hashes

    [Fact]
    public async Task A_correct_login_with_an_old_hash_stores_a_fresh_stronger_one()
    {
        var rig = Build(User(), rehashNeeded: true);

        var result = await Login(rig);

        Assert.True(result.Succeeded);
        Assert.Equal("hash:right", rig.Store.UpgradedHash); // hashed from the password just proven correct
        Assert.Contains("rehash", rig.Store.Calls);
    }

    [Fact]
    public async Task An_up_to_date_hash_is_left_alone()
    {
        var rig = Build(User(), rehashNeeded: false);

        await Login(rig);

        Assert.Null(rig.Store.UpgradedHash);
        Assert.DoesNotContain("rehash", rig.Store.Calls);
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("locked")]
    [InlineData("inactive")]
    public async Task No_hash_is_ever_replaced_unless_the_login_succeeds(string why)
    {
        var rig = Build(why switch { "locked" => User(locked: true), "inactive" => User(active: false), _ => User() }, rehashNeeded: true);

        await Login(rig, why == "wrong" ? "bad" : "right");

        Assert.Null(rig.Store.UpgradedHash);
    }

    private static LoginRecord PlatformAdmin(bool active = true) =>
        new(Guid.NewGuid(), null, "hash:right", UserRole.PlatformAdmin, "Root", null, active, false, null);

    private static string LinkIn(FakeEmail email) => email.Sent[^1].Body.Split("#confirm=")[1].Split('\n')[0].Trim();

    [Fact]
    public async Task A_platform_admin_with_the_right_password_gets_no_session_only_an_emailed_link()
    {
        var rig = Build(PlatformAdmin());

        var result = await rig.Service.LoginAsync(null, "root", "right", Client, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Challenge);
        Assert.Empty(rig.Sessions.Created);
        Assert.DoesNotContain("success", rig.Store.Calls);
        var message = Assert.Single(rig.Email.Sent);
        Assert.Equal("admin@platform.test", message.To);
        Assert.Contains("https://app.test/#confirm=", message.Body);
        Assert.Contains("203.0.113.5", message.Body);
        Assert.DoesNotContain(result.Challenge!.Secret, message.Body); // the secret never travels by e-mail
        Assert.Contains("login_confirmation_sent", rig.Log.Events.Select(e => e.Name));
    }

    [Fact]
    public async Task The_link_with_the_browsers_secret_finishes_the_login_once()
    {
        var rig = Build(PlatformAdmin());
        var challenge = (await rig.Service.LoginAsync(null, "root", "right", Client, CancellationToken.None)).Challenge!;

        var done = await rig.Service.ConfirmAsync(LinkIn(rig.Email), challenge.Secret, Client, CancellationToken.None);
        var again = await rig.Service.ConfirmAsync(LinkIn(rig.Email), challenge.Secret, Client, CancellationToken.None);

        Assert.True(done.Succeeded);
        Assert.Single(rig.Sessions.Created);
        Assert.False(again.Succeeded);
    }

    [Fact]
    public async Task The_link_alone_or_with_another_secret_does_not_log_in_and_burns_the_link()
    {
        var rig = Build(PlatformAdmin());
        var challenge = (await rig.Service.LoginAsync(null, "root", "right", Client, CancellationToken.None)).Challenge!;

        var other = await rig.Service.ConfirmAsync(LinkIn(rig.Email), "some-other-secret", Client, CancellationToken.None);
        var late = await rig.Service.ConfirmAsync(LinkIn(rig.Email), challenge.Secret, Client, CancellationToken.None);

        Assert.False(other.Succeeded);
        Assert.False(late.Succeeded); // the legitimate browser must sign in again
        Assert.Empty(rig.Sessions.Created);
        Assert.Contains("login_confirmation_failed", rig.Log.Events.Select(e => e.Name));
    }

    [Fact]
    public async Task An_old_link_is_refused_as_expired()
    {
        var rig = Build(PlatformAdmin());
        var challenge = (await rig.Service.LoginAsync(null, "root", "right", Client, CancellationToken.None)).Challenge!;
        rig.Challenges.Now = rig.Time.Now.AddMinutes(11);

        var result = await rig.Service.ConfirmAsync(LinkIn(rig.Email), challenge.Secret, Client, CancellationToken.None);

        Assert.True(result.ConfirmationExpired);
        Assert.Empty(rig.Sessions.Created);
    }

    [Fact]
    public async Task A_wrong_password_never_sends_an_email()
    {
        var rig = Build(PlatformAdmin());

        var result = await rig.Service.LoginAsync(null, "root", "wrong", Client, CancellationToken.None);

        Assert.Null(result.Challenge);
        Assert.Empty(rig.Email.Sent);
    }

    [Fact]
    public async Task A_platform_admin_without_email_cannot_get_in()
    {
        var rig = Build(PlatformAdmin(), adminEmail: null);

        var result = await rig.Service.LoginAsync(null, "root", "right", Client, CancellationToken.None);

        Assert.True(result.ConfirmationUnavailable);
        Assert.False(result.Succeeded);
        Assert.Empty(rig.Sessions.Created);
        Assert.Contains("login_confirmation_unavailable", rig.Log.Events.Select(e => e.Name));
    }

    [Fact]
    public async Task An_account_deactivated_while_the_link_was_out_cannot_use_it()
    {
        var rig = Build(PlatformAdmin(active: true));
        var challenge = (await rig.Service.LoginAsync(null, "root", "right", Client, CancellationToken.None)).Challenge!;
        var disabled = Build(PlatformAdmin(active: false));
        // Same challenge store state, but the store now reports the user as inactive.
        var service = new LoginService(
            new FakeStore(PlatformAdmin(active: false)), new FakePasswords(), new FakeIssuer(), disabled.Sessions, disabled.Log, Policy,
            new LoginChallengeService(rig.Challenges, rig.Email, disabled.Log, new LoginChallengePolicy("https://app.test", TimeSpan.FromMinutes(10), true), rig.Time));

        var result = await service.ConfirmAsync(LinkIn(rig.Email), challenge.Secret, Client, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(disabled.Sessions.Created);
    }

    [Fact]
    public async Task With_the_confirmation_switched_off_a_platform_admin_gets_a_session_straight_away()
    {
        var rig = Build(PlatformAdmin(), confirmPlatform: false);

        var result = await rig.Service.LoginAsync(null, "root", "right", Client, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(rig.Email.Sent);
    }

    [Fact]
    public async Task Gym_users_are_never_asked_for_a_confirmation()
    {
        var rig = Build(User());

        var result = await Login(rig);

        Assert.True(result.Succeeded);
        Assert.Empty(rig.Email.Sent);
    }
}
