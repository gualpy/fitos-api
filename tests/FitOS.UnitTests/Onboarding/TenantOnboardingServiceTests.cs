using FitOS.Application.Auth;
using FitOS.Application.Onboarding;
using FitOS.Application.Secrets;
using FitOS.Application.Subscriptions;

namespace FitOS.UnitTests.Onboarding;

public sealed class TenantOnboardingServiceTests
{
    private sealed class FakeStore : ITenantOnboardingStore
    {
        public NewTenant? Saved { get; private set; }

        public Guid? Actor { get; private set; }

        public string? ConflictOn { get; init; }

        public Task<Guid> CreateAsync(NewTenant tenant, Guid actorId, CancellationToken cancellationToken)
        {
            if (ConflictOn is not null)
            {
                throw new OnboardingConflictException(ConflictOn);
            }

            Saved = tenant;
            Actor = actorId;
            return Task.FromResult(Guid.NewGuid());
        }
    }

    private sealed class FakePasswords : IPasswordService
    {
        public string Hash(string password) => "hashed(" + password.Length + ")";

        public bool Verify(string hash, string password) => true;

        public PasswordCheck Check(string hash, string password) => PasswordCheck.Success;

        public void VerifyDummy(string password)
        {
        }
    }

    private sealed class FakeSecrets : ISecretProtector
    {
        public byte[] Protect(string plaintext, Guid tenantId) => System.Text.Encoding.UTF8.GetBytes($"enc:{tenantId}:{new string(plaintext.Reverse().ToArray())}");

        public string Unprotect(byte[] protectedData, Guid tenantId) => throw new NotSupportedException();
    }

    private static OnboardTenantCommand Valid(Func<OnboardTenantCommand, OnboardTenantCommand>? change = null)
    {
        var command = new OnboardTenantCommand(
            "titan", " Gimnasio Titan ", "1790016919001", " Titan S.A. ", null, " Av. Amazonas N24 ", "general", true,
            " 12 ", " sk-live-ABCDEFGH1234 ", "Owner.Titan", " Dueño Titan ", " owner@titan.test ");
        return change?.Invoke(command) ?? command;
    }

    private static readonly DateTimeOffset Today = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static TenantOnboardingService Create(FakeStore store, FakeEmail? email = null, FakeSetupStore? setupStore = null)
    {
        var time = new FixedTime(Today);
        var setup = new PasswordSetupService(
            setupStore ?? new FakeSetupStore(), new FakePasswords(), email ?? new FakeEmail(), new FakeLog(),
            new SetupLinkPolicy("https://app.test/", TimeSpan.FromHours(48)), time);
        return new(store, new FakePasswords(), new FakeSecrets(), setup, new SubscriptionPolicy(30, 3), time);
    }

    [Fact]
    public async Task Onboarding_stores_hashed_password_and_encrypted_key_never_the_plain_values()
    {
        var store = new FakeStore();
        var actor = Guid.NewGuid();

        var result = await Create(store).OnboardAsync(Valid(), actor, CancellationToken.None);

        var saved = store.Saved!;
        Assert.Equal(actor, store.Actor);
        Assert.Equal(result.TenantId, saved.TenantId);
        Assert.Equal("hashed(32)", saved.OwnerPasswordHash); // a random value nobody sees: the owner sets theirs from the link
        Assert.DoesNotContain("sk-live-ABCDEFGH1234", System.Text.Encoding.UTF8.GetString(saved.EncryptedApiKey));
        Assert.Contains(result.TenantId.ToString(), System.Text.Encoding.UTF8.GetString(saved.EncryptedApiKey)); // bound to the tenant
        Assert.Equal("1234", saved.ApiKeyHint);
    }

    [Fact]
    public async Task Text_fields_are_trimmed_and_defaults_are_test_environment_and_establishment_001()
    {
        var store = new FakeStore();

        await Create(store).OnboardAsync(Valid(), Guid.NewGuid(), CancellationToken.None);

        var saved = store.Saved!;
        Assert.Equal("Gimnasio Titan", saved.Name);
        Assert.Equal("Titan S.A.", saved.LegalName);
        Assert.Equal("12", saved.ProviderCompanyId);
        Assert.Equal("Dueño Titan", saved.OwnerDisplayName);
        Assert.Equal("test", saved.Environment);
        Assert.Equal("001", saved.SriEstablishment);
        Assert.Equal("001", saved.EmissionPointCode);
    }

    [Fact]
    public async Task The_gym_starts_with_the_trial_period_and_the_owner_email_is_kept()
    {
        var store = new FakeStore();

        await Create(store).OnboardAsync(Valid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 11, 22), store.Saved!.SubscriptionEndsOn);
        Assert.Equal("owner@titan.test", store.Saved.OwnerEmail);
    }

    [Fact]
    public async Task The_owner_gets_a_one_time_link_and_no_password_by_email()
    {
        var email = new FakeEmail();
        var setupStore = new FakeSetupStore();

        var result = await Create(new FakeStore(), email, setupStore).OnboardAsync(Valid(), Guid.NewGuid(), CancellationToken.None);

        var message = Assert.Single(email.Sent);
        Assert.True(result.WelcomeEmailSent);
        Assert.Equal("owner@titan.test", message.To);
        Assert.Contains("https://app.test/#setup=", message.Body); // fragment: never reaches a server log
        Assert.Contains("Usuario: owner.titan", message.Body);
        Assert.Single(setupStore.Tokens);
        var token = message.Body.Split("#setup=")[1].Split('\n')[0].Trim();
        Assert.DoesNotContain(token, string.Join(',', setupStore.Tokens.Keys)); // only a hash is stored
    }

    [Fact]
    public async Task A_failing_mail_server_does_not_undo_the_gym()
    {
        var store = new FakeStore();

        var result = await Create(store, new FakeEmail { Fail = true }).OnboardAsync(Valid(), Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.WelcomeEmailSent);
        Assert.NotNull(store.Saved);
    }

    [Theory]
    [InlineData("slug", "Titan", "invalid_format")]
    [InlineData("slug", "admin", "reserved")]
    [InlineData("slug", "platform", "reserved")]
    [InlineData("ruc", "1790016918001", "invalid")]
    [InlineData("regime", "inventado", "invalid")]
    [InlineData("environment", "staging", "invalid")]
    [InlineData("sriEstablishment", "1", "invalid_format")]
    [InlineData("emissionPointCode", "ab1", "invalid_format")]
    [InlineData("ownerUsername", "A b", "invalid_format")]
    [InlineData("ownerUsername", "ab", "invalid_format")]
    [InlineData("providerApiKey", "  ", "required")]
    [InlineData("providerCompanyId", "", "required")]
    [InlineData("legalName", "", "required")]
    [InlineData("name", "", "required")]
    [InlineData("ownerEmail", "", "required")]
    [InlineData("ownerEmail", "sin-arroba", "invalid_format")]
    [InlineData("ownerEmail", "a b@c.d", "invalid_format")]
    public async Task Invalid_input_is_rejected_before_touching_the_store(string field, string value, string code)
    {
        var store = new FakeStore();
        var command = Valid(c => field switch
        {
            "slug" => c with { Slug = value },
            "ruc" => c with { Ruc = value },
            "regime" => c with { Regime = value },
            "environment" => c with { Environment = value },
            "sriEstablishment" => c with { SriEstablishment = value },
            "emissionPointCode" => c with { EmissionPointCode = value },
            "ownerUsername" => c with { OwnerUsername = value },
            "providerApiKey" => c with { ProviderApiKey = value },
            "providerCompanyId" => c with { ProviderCompanyId = value },
            "legalName" => c with { LegalName = value },
            "ownerEmail" => c with { OwnerEmail = value },
            _ => c with { Name = value },
        });

        var ex = await Assert.ThrowsAsync<OnboardingValidationException>(() => Create(store).OnboardAsync(command, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(code, ex.Errors[field]);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task Several_errors_are_reported_together()
    {
        var command = Valid(c => c with { Slug = "ADMIN", Ruc = "1", Regime = "x" });

        var ex = await Assert.ThrowsAsync<OnboardingValidationException>(() => Create(new FakeStore()).OnboardAsync(command, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(["regime", "ruc", "slug"], ex.Errors.Keys.Order().ToArray());
    }

    [Theory]
    [InlineData("slug")]
    [InlineData("ruc")]
    public async Task Conflicts_from_the_store_surface_with_their_field(string field)
    {
        var ex = await Assert.ThrowsAsync<OnboardingConflictException>(
            () => Create(new FakeStore { ConflictOn = field }).OnboardAsync(Valid(), Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(field, ex.Field);
    }
}
