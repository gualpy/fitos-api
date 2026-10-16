using System.Security.Cryptography;
using System.Text;
using FitOS.Application.Auth;
using FitOS.Application.Fiscal;
using FitOS.Infrastructure.Secrets;
using Microsoft.Extensions.Options;

namespace FitOS.UnitTests.Onboarding;

public sealed class AesGcmSecretProtectorTests
{
    private static AesGcmSecretProtector Create(byte? fill = 7) =>
        new(Options.Create(new SecretsOptions { EncryptionKey = Convert.ToBase64String(Enumerable.Repeat(fill ?? (byte)7, 32).ToArray()) }));

    [Fact]
    public void Round_trip_returns_the_original_and_hides_the_plaintext()
    {
        var tenant = Guid.NewGuid();
        var protector = Create();

        var data = protector.Protect("sk-live-SECRET-123", tenant);

        Assert.DoesNotContain("SECRET", Encoding.UTF8.GetString(data));
        Assert.Equal("sk-live-SECRET-123", protector.Unprotect(data, tenant));
    }

    [Fact]
    public void Same_value_encrypts_differently_every_time()
    {
        var tenant = Guid.NewGuid();
        var protector = Create();

        Assert.NotEqual(protector.Protect("same", tenant), protector.Protect("same", tenant));
    }

    [Fact]
    public void Ciphertext_cannot_be_decrypted_for_another_tenant()
    {
        var protector = Create();
        var data = protector.Protect("key", Guid.NewGuid());

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(data, Guid.NewGuid()));
    }

    [Fact]
    public void Tampered_data_and_a_different_key_are_detected()
    {
        var tenant = Guid.NewGuid();
        var data = Create().Protect("key", tenant);
        var tampered = (byte[])data.Clone();
        tampered[^1] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => Create().Unprotect(tampered, tenant));
        Assert.ThrowsAny<CryptographicException>(() => Create(fill: 9).Unprotect(data, tenant));
        Assert.ThrowsAny<CryptographicException>(() => Create().Unprotect([1, 2, 3], tenant));
    }

    [Theory]
    [InlineData("not base64!!")]
    [InlineData("AAAA")]
    [InlineData("")]
    public void A_bad_encryption_key_is_refused_at_startup(string key) =>
        Assert.Throws<InvalidOperationException>(() => new AesGcmSecretProtector(Options.Create(new SecretsOptions { EncryptionKey = key })));
}

public sealed class ChangePasswordServiceTests
{
    private sealed class Store(string? hash) : IPasswordStore
    {
        public string? Saved { get; private set; }

        public Task<string?> GetHashAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(hash);

        public Task SetAsync(Guid userId, string newHash, CancellationToken cancellationToken)
        {
            Saved = newHash;
            return Task.CompletedTask;
        }
    }

    private sealed class Passwords : IPasswordService
    {
        public string Hash(string password) => "h:" + password;

        public bool Verify(string hash, string password) => hash == "h:" + password;

        public void VerifyDummy(string password)
        {
        }
    }

    private static (ChangePasswordService Service, Store Store) Create(string? hash = "h:Temp-Password-1")
    {
        var store = new Store(hash);
        return (new ChangePasswordService(store, new Passwords()), store);
    }

    [Fact]
    public async Task Correct_current_and_acceptable_new_password_is_stored_hashed()
    {
        var (service, store) = Create();

        var outcome = await service.ChangeAsync(Guid.NewGuid(), "Temp-Password-1", "Brand-New-Pass-9", CancellationToken.None);

        Assert.Equal(ChangePasswordOutcome.Changed, outcome);
        Assert.Equal("h:Brand-New-Pass-9", store.Saved);
    }

    [Fact]
    public async Task Wrong_current_password_changes_nothing()
    {
        var (service, store) = Create();

        Assert.Equal(ChangePasswordOutcome.WrongCurrentPassword, await service.ChangeAsync(Guid.NewGuid(), "nope", "Brand-New-Pass-9", CancellationToken.None));
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task Unknown_user_looks_like_a_wrong_password()
    {
        var (service, store) = Create(hash: null);

        Assert.Equal(ChangePasswordOutcome.WrongCurrentPassword, await service.ChangeAsync(Guid.NewGuid(), "x", "Brand-New-Pass-9", CancellationToken.None));
        Assert.Null(store.Saved);
    }

    [Theory]
    [InlineData("short1")]
    [InlineData("onlylettersonlyletters")]
    [InlineData("12345678901234")]
    [InlineData("Temp-Password-1")] // same as current
    public async Task Weak_or_unchanged_new_password_is_refused(string newPassword)
    {
        var (service, store) = Create();

        Assert.Equal(ChangePasswordOutcome.WeakPassword, await service.ChangeAsync(Guid.NewGuid(), "Temp-Password-1", newPassword, CancellationToken.None));
        Assert.Null(store.Saved);
    }

    [Fact]
    public void Password_policy_has_a_maximum_to_avoid_hashing_huge_inputs() =>
        Assert.False(ChangePasswordService.IsAcceptable(new string('a', 150) + new string('1', 60)));
}

public sealed class FiscalConnectionServiceTests
{
    private sealed class Accounts(FiscalAccount? account) : ITenantFiscalAccountStore
    {
        public Task<FiscalAccount?> GetAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(account);
    }

    private sealed class Provider(Func<FiscalInvoiceState?> respond) : IFiscalProvider
    {
        public string? Reference { get; private set; }

        public FiscalAccount? UsedAccount { get; private set; }

        public Task<FiscalInvoiceState> IssueInvoiceAsync(FiscalAccount account, FiscalInvoiceRequest request, string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException("a connection test must never create an invoice");

        public Task<FiscalInvoiceState?> GetByReferenceAsync(FiscalAccount account, string externalReference, CancellationToken cancellationToken)
        {
            Reference = externalReference;
            UsedAccount = account;
            return Task.FromResult(respond());
        }
    }

    private static FiscalConnectionService Create(FiscalAccount? account, Func<FiscalInvoiceState?> respond, out Provider provider)
    {
        provider = new Provider(respond);
        return new FiscalConnectionService(new Accounts(account), provider);
    }

    private static readonly FiscalAccount Account = new("12", "key");

    [Fact]
    public async Task Not_found_reference_means_the_key_works_and_nothing_was_created()
    {
        var service = Create(Account, () => null, out var provider);

        var result = await service.TestAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(FiscalConnectionStatus.Ok, result.Status);
        Assert.Equal(FiscalConnectionService.ProbeReference, provider.Reference);
        Assert.Equal(Account, provider.UsedAccount);
    }

    [Fact]
    public async Task No_configured_account_is_reported_without_calling_the_provider()
    {
        var service = Create(null, () => throw new InvalidOperationException("must not be called"), out _);

        Assert.Equal(FiscalConnectionStatus.NotConfigured, (await service.TestAsync(Guid.NewGuid(), CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData(401, "unauthenticated", false, FiscalConnectionStatus.InvalidApiKey)]
    [InlineData(403, "forbidden", false, FiscalConnectionStatus.WrongCompany)]
    [InlineData(404, "company_not_found", false, FiscalConnectionStatus.CompanyNotFound)]
    [InlineData(503, "unavailable", true, FiscalConnectionStatus.Unreachable)]
    [InlineData(422, "validation_failed", false, FiscalConnectionStatus.Unexpected)]
    public async Task Provider_errors_map_to_a_diagnosis(int http, string code, bool retryable, FiscalConnectionStatus expected)
    {
        var service = Create(Account, () => throw new FiscalProviderException(code, "x", http, retryable), out _);

        var result = await service.TestAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(expected, result.Status);
        Assert.Equal(code, result.Detail);
    }

    [Fact]
    public async Task Network_failure_is_unreachable()
    {
        var service = Create(Account, () => throw new FiscalProviderException("network_error", "x", null, isRetryable: true), out _);

        Assert.Equal(FiscalConnectionStatus.Unreachable, (await service.TestAsync(Guid.NewGuid(), CancellationToken.None)).Status);
    }
}
