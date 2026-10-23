using FitOS.Application.Auth;

namespace FitOS.UnitTests.Onboarding;

public sealed class PasswordSetupServiceTests
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

    private static readonly DateTimeOffset Start = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static (PasswordSetupService Service, FakeSetupStore Store, FakeEmail Email, FakeLog Log, FixedTime Time) Create()
    {
        var time = new FixedTime(Start);
        var store = new FakeSetupStore { Now = Start };
        var email = new FakeEmail();
        var log = new FakeLog();
        return (new PasswordSetupService(store, new FakePasswords(), email, log, new SetupLinkPolicy("https://app.test", TimeSpan.FromHours(48), TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(60)), time), store, email, log, time);
    }

    private static string TokenIn(FakeEmail email) => email.Sent[^1].Body.Split("#setup=")[1].Split('\n')[0].Trim();

    private static readonly WelcomeRecipient Owner = new(Guid.NewGuid(), "o@t.test", "Dueño", "dueno", "Titan", "titan");

    [Fact]
    public async Task A_link_sets_the_password_once_and_only_once()
    {
        var (service, store, email, log, _) = Create();
        await service.SendWelcomeAsync(Owner, CancellationToken.None);

        var first = await service.SetPasswordAsync(TokenIn(email), "una-clave-segura-1", new ClientInfo("1.2.3.4", "ua"), CancellationToken.None);
        var second = await service.SetPasswordAsync(TokenIn(email), "otra-clave-segura-2", ClientInfo.Unknown, CancellationToken.None);

        Assert.Equal(SetPasswordOutcome.Done, first);
        Assert.Equal("h:una-clave-segura-1", store.PasswordSet);
        Assert.Equal(SetPasswordOutcome.InvalidLink, second);
        Assert.Equal(["password_set", "password_setup_failed"], log.Events.Select(e => e.Name).ToArray());
    }

    [Fact]
    public async Task An_expired_link_is_refused_and_does_not_set_the_password()
    {
        var (service, store, email, _, time) = Create();
        await service.SendWelcomeAsync(Owner, CancellationToken.None);
        time.Now = Start.AddHours(49);
        store.Now = time.Now;

        var outcome = await service.SetPasswordAsync(TokenIn(email), "una-clave-segura-1", ClientInfo.Unknown, CancellationToken.None);

        Assert.Equal(SetPasswordOutcome.ExpiredLink, outcome);
        Assert.Null(store.PasswordSet);
    }

    [Fact]
    public async Task A_new_link_voids_the_previous_one()
    {
        var (service, _, email, _, _) = Create();
        await service.SendWelcomeAsync(Owner, CancellationToken.None);
        var old = TokenIn(email);
        await service.SendWelcomeAsync(Owner, CancellationToken.None);

        Assert.Equal(SetPasswordOutcome.InvalidLink, await service.SetPasswordAsync(old, "una-clave-segura-1", ClientInfo.Unknown, CancellationToken.None));
        Assert.Equal(SetPasswordOutcome.Done, await service.SetPasswordAsync(TokenIn(email), "una-clave-segura-1", ClientInfo.Unknown, CancellationToken.None));
    }

    [Theory]
    [InlineData("corta1")]
    [InlineData("sololetrasyletras")]
    [InlineData("12345678901234")]
    public async Task A_weak_password_is_refused_and_the_link_stays_usable(string weak)
    {
        var (service, store, email, _, _) = Create();
        await service.SendWelcomeAsync(Owner, CancellationToken.None);

        Assert.Equal(SetPasswordOutcome.WeakPassword, await service.SetPasswordAsync(TokenIn(email), weak, ClientInfo.Unknown, CancellationToken.None));
        Assert.Null(store.PasswordSet);
        Assert.Equal(SetPasswordOutcome.Done, await service.SetPasswordAsync(TokenIn(email), "una-clave-segura-1", ClientInfo.Unknown, CancellationToken.None));
    }

    [Fact]
    public async Task Garbage_tokens_are_invalid()
    {
        var (service, _, _, _, _) = Create();

        Assert.Equal(SetPasswordOutcome.InvalidLink, await service.SetPasswordAsync("no-existe", "una-clave-segura-1", ClientInfo.Unknown, CancellationToken.None));
    }
}
