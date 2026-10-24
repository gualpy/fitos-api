using FitOS.Application.Auth;
using FitOS.Domain.Tenancy;

namespace FitOS.UnitTests.Team;

public sealed class PasswordResetTests
{
    private sealed class FakePasswords : IPasswordService
    {
        public string Hash(string password) => password;

        public bool Verify(string hash, string password) => true;

        public PasswordCheck Check(string hash, string password) => PasswordCheck.Success;

        public void VerifyDummy(string password)
        {
        }
    }

    private static readonly ClientInfo Client = new("203.0.113.9", "ua");

    private static ResetTarget Target(
        bool active = true, string? email = "ana@example.test", UserRole role = UserRole.Reception, string? status = "active") =>
        new(Guid.NewGuid(), role == UserRole.PlatformAdmin ? null : Guid.NewGuid(), "Ana", email, role, active, status, "Gimnasio Titan", "titan");

    private static (PasswordResetService Service, FakeResetStore Store, FakeEmail Email, FakeLog Log, FakeSetupStore Tokens, FixedTime Time, ImmediateBackground Background) Build()
    {
        var time = new FixedTime(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        var tokens = new FakeSetupStore { Now = time.Now };
        var email = new FakeEmail();
        var log = new FakeLog();
        var setup = new PasswordSetupService(
            tokens, new FakePasswords(), email, log,
            new SetupLinkPolicy("https://app.test", TimeSpan.FromHours(48), TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(60)), time);
        var store = new FakeResetStore();
        var background = new ImmediateBackground(_ => setup);
        return (new PasswordResetService(store, background, log), store, email, log, tokens, time, background);
    }

    [Fact]
    public async Task A_real_account_gets_a_reset_link_in_the_gyms_name()
    {
        var (service, store, email, log, tokens, _, _) = Build();
        store.Target = Target();

        await service.RequestAsync("titan", false, "ana@example.test", Client, CancellationToken.None);

        var message = Assert.Single(email.Sent);
        Assert.Equal("ana@example.test", message.To);
        Assert.Equal("Gimnasio Titan", message.FromName);
        Assert.Equal("Restablece tu contraseña en Gimnasio Titan", message.Subject);
        Assert.Contains("https://app.test/#setup=", message.Body);
        Assert.Contains("60 minutos", message.Body);
        Assert.Equal("reset", Assert.Single(tokens.Tokens.Values).Purpose);
        Assert.Equal(["password_reset_requested", "password_reset_sent"], log.Events.Select(e => e.Name).ToArray());
        Assert.Equal("ana@example.test", store.AskedIdentifier);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("inactive")]
    [InlineData("no_email")]
    [InlineData("platform")]
    [InlineData("suspended_gym")]
    [InlineData("expired_gym")]
    public async Task Nothing_is_sent_for_accounts_that_cannot_reset_and_the_typed_text_is_never_logged(string kind)
    {
        var (service, store, email, log, tokens, _, background) = Build();
        store.Target = kind switch
        {
            "unknown" => null,
            "inactive" => Target(active: false),
            "no_email" => Target(email: null),
            "platform" => Target(role: UserRole.PlatformAdmin),
            "suspended_gym" => Target(status: "suspended"),
            _ => Target(status: "expired"),
        };

        await service.RequestAsync("titan", false, "mi-clave-secreta-1", Client, CancellationToken.None);

        Assert.Empty(email.Sent);
        Assert.Empty(tokens.Tokens);
        Assert.Equal(0, background.Enqueued);
        Assert.Equal(["password_reset_ignored"], log.Events.Select(e => e.Name).ToArray());
        Assert.DoesNotContain("mi-clave-secreta-1", System.Text.Json.JsonSerializer.Serialize(log.Events));
    }

    [Fact]
    public async Task Without_a_gym_nothing_is_even_looked_up()
    {
        var (service, store, email, _, _, _, _) = Build();
        store.Target = Target();

        await service.RequestAsync(null, false, "ana@example.test", Client, CancellationToken.None);

        Assert.Null(store.AskedSlug);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task Asking_again_within_the_minimum_gap_sends_nothing_new_and_after_it_a_fresh_link_voids_the_old_one()
    {
        var (service, store, email, _, tokens, time, _) = Build();
        store.Target = Target();

        await service.RequestAsync("titan", false, "ana@example.test", Client, CancellationToken.None);
        await service.RequestAsync("titan", false, "ana@example.test", Client, CancellationToken.None);
        Assert.Single(email.Sent);

        time.Now = time.Now.AddSeconds(61);
        tokens.Now = time.Now;
        await service.RequestAsync("titan", false, "ana@example.test", Client, CancellationToken.None);
        Assert.Equal(2, email.Sent.Count);
        Assert.Single(tokens.Tokens); // the first link is no longer valid
    }

    [Fact]
    public async Task A_failing_mail_server_leaves_a_trace_but_never_an_error()
    {
        var (service, store, _, log, _, _, _) = Build();
        store.Target = Target();
        var failing = new FakeEmail { Fail = true };
        var setup = new PasswordSetupService(
            new FakeSetupStore(), new FakePasswords(), failing, log,
            new SetupLinkPolicy("https://app.test", TimeSpan.FromHours(48), TimeSpan.FromMinutes(60), TimeSpan.Zero), new FixedTime(DateTimeOffset.UtcNow));
        var failingService = new PasswordResetService(store, new ImmediateBackground(_ => setup), log);

        await failingService.RequestAsync("titan", false, "ana@example.test", Client, CancellationToken.None);

        Assert.Contains("password_reset_email_failed", log.Events.Select(e => e.Name));
    }

    [Fact]
    public async Task A_platform_administrator_gets_a_short_lived_link_in_fitos_name()
    {
        var (service, store, email, log, tokens, _, _) = Build();
        store.Target = Target(role: UserRole.PlatformAdmin, status: null) with { GymName = "FitOS", GymSlug = "platform" };

        await service.RequestAsync(null, true, "root@example.test", Client, CancellationToken.None);

        Assert.Null(store.AskedSlug); // the lookup is among platform administrators
        var message = Assert.Single(email.Sent);
        Assert.Equal("FitOS: restablece tu contraseña de administrador", message.Subject);
        Assert.Null(message.FromName);
        Assert.Contains("15 minutos", message.Body);
        Assert.DoesNotContain("60 minutos", message.Body);
        Assert.Contains("https://app.test/#setup=", message.Body);
        Assert.Equal("reset", Assert.Single(tokens.Tokens.Values).Purpose);
        Assert.Equal(["password_reset_requested", "password_reset_sent"], log.Events.Select(e => e.Name).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_platform_request_ignores_gym_accounts_and_inactive_or_mailless_administrators(bool gymAccount)
    {
        var (service, store, email, log, _, _, _) = Build();
        store.Target = gymAccount
            ? Target()
            : Target(role: UserRole.PlatformAdmin, email: null, status: null);

        await service.RequestAsync(null, true, "someone", Client, CancellationToken.None);

        Assert.Empty(email.Sent);
        Assert.Equal(["password_reset_ignored"], log.Events.Select(e => e.Name).ToArray());
    }
}
