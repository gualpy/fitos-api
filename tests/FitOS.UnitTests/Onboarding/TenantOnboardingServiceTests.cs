using FitOS.Application.Auth;
using FitOS.Application.Onboarding;
using FitOS.Application.Secrets;

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
            " 12 ", " sk-live-ABCDEFGH1234 ", "Owner.Titan", " Dueño Titan ");
        return change?.Invoke(command) ?? command;
    }

    private static TenantOnboardingService Create(FakeStore store) => new(store, new FakePasswords(), new FakeSecrets());

    [Fact]
    public async Task Onboarding_stores_hashed_password_and_encrypted_key_never_the_plain_values()
    {
        var store = new FakeStore();
        var actor = Guid.NewGuid();

        var result = await Create(store).OnboardAsync(Valid(), actor, CancellationToken.None);

        var saved = store.Saved!;
        Assert.Equal(actor, store.Actor);
        Assert.Equal(result.TenantId, saved.TenantId);
        Assert.Equal("hashed(16)", saved.OwnerPasswordHash);
        Assert.DoesNotContain(result.TemporaryPassword, saved.OwnerPasswordHash);
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
    public async Task Temporary_password_is_random_and_acceptable_under_the_password_policy()
    {
        var first = await Create(new FakeStore()).OnboardAsync(Valid(), Guid.NewGuid(), CancellationToken.None);
        var second = await Create(new FakeStore()).OnboardAsync(Valid(), Guid.NewGuid(), CancellationToken.None);

        Assert.NotEqual(first.TemporaryPassword, second.TemporaryPassword);
        Assert.Equal(16, first.TemporaryPassword.Length);
        Assert.True(ChangePasswordService.IsAcceptable(first.TemporaryPassword));
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
