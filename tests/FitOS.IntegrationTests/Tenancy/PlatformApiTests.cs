using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FitOS.Api.Tenancy;
using FitOS.Application.Fiscal;
using FitOS.Application.Onboarding;
using FitOS.Domain.Identification;
using FitOS.Infrastructure.Auth;
using FitOS.Migrator;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FitOS.IntegrationTests.Tenancy;

/// <summary>Gym onboarding end to end: real HTTP pipeline, real PostgreSQL, real row-level security.</summary>
public sealed class PlatformApiTests(PostgresFixture db) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Password = "Correct-Horse-9!";

    private const string ProviderKey = "sk-live-ABCDEFGH1234";

    private static readonly IdentityPasswordService Hasher = new();

    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }

        return Task.CompletedTask;
    }

    private sealed class FakeFiscalProvider(Func<FiscalAccount, FiscalInvoiceState?> respond) : IFiscalProvider
    {
        public List<FiscalAccount> Accounts { get; } = [];

        public Task<FiscalInvoiceState> IssueInvoiceAsync(FiscalAccount account, FiscalInvoiceRequest request, string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<FiscalInvoiceState?> GetByReferenceAsync(FiscalAccount account, string externalReference, CancellationToken cancellationToken)
        {
            Accounts.Add(account);
            return Task.FromResult(respond(account));
        }
    }

    private (HttpClient Client, WebApplicationFactory<Program> App) Boot(IFiscalProvider? provider = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseTestSecrets();
            host.UseSetting("ConnectionStrings:Default", db.AppConnection);
            host.UseSetting("Auth:LoginRequestsPerMinute", "1000");
            if (provider is not null)
            {
                host.ConfigureTestServices(services => services.AddSingleton(provider));
            }
        });
        _factories.Add(factory);
        return (factory.CreateClient(), factory);
    }

    private static string NewRuc()
    {
        int[] coefficients = [4, 3, 2, 7, 6, 5, 4, 3, 2];
        while (true)
        {
            var body = "179" + Random.Shared.Next(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            var sum = body.Select((c, i) => (c - '0') * coefficients[i]).Sum();
            var check = sum % 11 == 0 ? 0 : 11 - (sum % 11);
            if (check != 10)
            {
                var ruc = body + check + "001";
                Assert.True(EcuadorId.IsValidRuc(ruc));
                return ruc;
            }
        }
    }

    private static string NewSlug() => "g" + Guid.NewGuid().ToString("N")[..10];

    private static object Body(string slug, string ruc, string owner = "dueno", string apiKey = ProviderKey) => new
    {
        slug,
        name = "Gimnasio " + slug,
        ruc,
        legalName = "Gym S.A.",
        tradeName = "Gym",
        headOfficeAddress = "Av. Amazonas N24",
        regime = "general",
        accountingRequired = true,
        providerCompanyId = "12",
        providerApiKey = apiKey,
        ownerUsername = owner,
        ownerDisplayName = "Dueño",
        environment = "test",
    };

    private async Task<Guid> SeedUser(Guid? tenantId, string username, string role, bool mustChange = false)
    {
        var id = Guid.NewGuid();
        await db.ScalarAsync(
            "INSERT INTO users (id, tenant_id, username, password_hash, display_name, role, must_change_password) VALUES (@id, @tenant, @user, @hash, 'Name', @role, @must) RETURNING 1",
            ("id", id), ("tenant", tenantId), ("user", username), ("hash", Hasher.Hash(Password)), ("role", role), ("must", mustChange));
        return id;
    }

    private static async Task<HttpResponseMessage> Login(HttpClient client, string slug, string username, string password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login") { Content = JsonContent.Create(new { username, password }) };
        request.Headers.Add(TenantHostResolver.TenantHeader, slug);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private async Task<(HttpClient Client, Guid AdminId)> PlatformClient(WebApplicationFactory<Program>? app = null, HttpClient? existing = null)
    {
        var client = existing ?? Boot().Client;
        var username = "root-" + Guid.NewGuid().ToString("N")[..8];
        var adminId = await SeedUser(null, username, "platform_admin");
        var token = (await Json(await Login(client, "platform", username, Password))).GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, adminId);
    }

    [SkippableFact]
    public async Task Bootstrapped_admin_must_change_the_temporary_password_before_doing_anything_else()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var client = Boot().Client;
        var username = "boot-" + Guid.NewGuid().ToString("N")[..8];

        Assert.True(await PlatformAdminBootstrap.CreateAsync(db.OwnerConnection, username, "Root", "Temp-Passw0rd!", Hasher));
        Assert.False(await PlatformAdminBootstrap.CreateAsync(db.OwnerConnection, username, "Root", "Temp-Passw0rd!", Hasher)); // already exists

        var login = await Json(await Login(client, "platform", username, "Temp-Passw0rd!"));
        Assert.True(login.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("accessToken").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/platform/tenants", Body(NewSlug(), NewRuc()))).StatusCode);
        var me = await Json(await client.GetAsync("/auth/me"));
        Assert.True(me.GetProperty("mustChangePassword").GetBoolean());

        var wrong = await client.PostAsJsonAsync("/auth/change-password", new { currentPassword = "nope", newPassword = "Brand-New-Pass-9" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var weak = await client.PostAsJsonAsync("/auth/change-password", new { currentPassword = "Temp-Passw0rd!", newPassword = "short" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, weak.StatusCode);
        var changed = await client.PostAsJsonAsync("/auth/change-password", new { currentPassword = "Temp-Passw0rd!", newPassword = "Brand-New-Pass-9" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, "platform", username, "Temp-Passw0rd!")).StatusCode);
        var again = await Json(await Login(client, "platform", username, "Brand-New-Pass-9"));
        Assert.False(again.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", again.GetProperty("accessToken").GetString());
        var allowed = await client.PostAsJsonAsync("/platform/tenants", new { }); // authorised now: only the body is wrong
        Assert.Equal(HttpStatusCode.UnprocessableEntity, allowed.StatusCode);
    }

    [SkippableFact]
    public async Task Onboarding_creates_the_whole_gym_and_the_owner_can_log_in()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (app, factory) = Boot();
        var (client, adminId) = await PlatformClient(existing: app);
        var slug = NewSlug();
        var ruc = NewRuc();

        var response = await client.PostAsJsonAsync("/platform/tenants", Body(slug, ruc, owner: "Dueno.Titan"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        var created = await Json(response);
        var tenantId = created.GetProperty("tenantId").GetGuid();
        var temporaryPassword = created.GetProperty("temporaryPassword").GetString()!;
        Assert.Equal("dueno.titan", created.GetProperty("ownerUsername").GetString());

        Assert.Equal("active", await db.ScalarAsync("SELECT status FROM tenants WHERE id = @id", ("id", tenantId)));
        Assert.Equal(1L, await db.ScalarAsync("SELECT count(*) FROM tenant_settings WHERE tenant_id = @id", ("id", tenantId)));
        Assert.Equal(ruc, await db.ScalarAsync("SELECT ruc FROM tenant_fiscal_config WHERE tenant_id = @id", ("id", tenantId)));
        Assert.Equal("001", await db.ScalarAsync("SELECT sri_establishment FROM branches WHERE tenant_id = @id AND name = 'Matriz'", ("id", tenantId)));
        Assert.Equal(1L, await db.ScalarAsync("SELECT count(*) FROM emission_points WHERE tenant_id = @id AND code = '001'", ("id", tenantId)));
        Assert.Equal(true, await db.ScalarAsync("SELECT must_change_password FROM users WHERE tenant_id = @id AND role = 'owner'", ("id", tenantId)));

        // The API key is stored encrypted, bound to the tenant, and decrypts back through the real store.
        var encrypted = (byte[])(await db.ScalarAsync("SELECT provider_api_key_enc FROM tenant_fiscal_config WHERE tenant_id = @id", ("id", tenantId)))!;
        Assert.DoesNotContain("ABCDEFGH1234", Encoding.UTF8.GetString(encrypted));
        Assert.Equal("1234", await db.ScalarAsync("SELECT provider_api_key_hint FROM tenant_fiscal_config WHERE tenant_id = @id", ("id", tenantId)));
        using (var scope = factory.Services.CreateScope())
        {
            var account = await scope.ServiceProvider.GetRequiredService<ITenantFiscalAccountStore>().GetAsync(tenantId, CancellationToken.None);
            Assert.Equal(new FiscalAccount("12", ProviderKey), account);
        }

        // Audit: who did it, and no secrets anywhere in it.
        var audit = (string)(await db.ScalarAsync("SELECT after::text FROM audit_log WHERE tenant_id = @id AND action = 'tenant.onboarded' AND user_id = @actor", ("id", tenantId), ("actor", adminId)))!;
        Assert.Contains(slug, audit);
        Assert.DoesNotContain(ProviderKey, audit);
        Assert.DoesNotContain(temporaryPassword, audit);

        // The owner logs in with the temporary password, must change it, and has no platform powers.
        var login = await Json(await Login(client, slug, "dueno.titan", temporaryPassword));
        Assert.Equal("owner", login.GetProperty("user").GetProperty("role").GetString());
        Assert.Equal(tenantId, login.GetProperty("user").GetProperty("tenantId").GetGuid());
        Assert.True(login.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());

        using var ownerClient = factory.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerClient.PostAsJsonAsync("/platform/tenants", Body(NewSlug(), NewRuc()))).StatusCode);
    }

    [SkippableFact]
    public async Task Duplicate_slug_or_ruc_is_a_conflict_and_a_failed_onboarding_leaves_nothing_behind()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (client, _) = await PlatformClient();
        var slug = NewSlug();
        var ruc = NewRuc();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/platform/tenants", Body(slug, ruc))).StatusCode);

        var sameSlug = await client.PostAsJsonAsync("/platform/tenants", Body(slug, NewRuc()));
        Assert.Equal(HttpStatusCode.Conflict, sameSlug.StatusCode);
        Assert.Equal("slug_taken", (await Json(sameSlug)).GetProperty("error").GetProperty("code").GetString());

        // The slug is inserted BEFORE the RUC clash is detected: it must be rolled back with everything else.
        var otherSlug = NewSlug();
        var sameRuc = await client.PostAsJsonAsync("/platform/tenants", Body(otherSlug, ruc, owner: "otro.dueno"));
        Assert.Equal(HttpStatusCode.Conflict, sameRuc.StatusCode);
        Assert.Equal("ruc_taken", (await Json(sameRuc)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM tenants WHERE slug = @slug", ("slug", otherSlug)));
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM users WHERE username = 'otro.dueno'"));
    }

    [SkippableFact]
    public async Task Invalid_data_is_rejected_with_field_level_errors_and_writes_nothing()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (client, _) = await PlatformClient();
        var before = await db.ScalarAsync("SELECT count(*) FROM tenants");

        var response = await client.PostAsJsonAsync("/platform/tenants", Body("admin", "1234567890123", apiKey: " "));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var details = (await Json(response)).GetProperty("error").GetProperty("details");
        Assert.Equal("reserved", details.GetProperty("slug").GetString());
        Assert.Equal("invalid", details.GetProperty("ruc").GetString());
        Assert.Equal("required", details.GetProperty("providerApiKey").GetString());
        Assert.Equal(before, await db.ScalarAsync("SELECT count(*) FROM tenants"));
    }

    [SkippableFact]
    public async Task Only_platform_administrators_can_onboard()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (client, _) = Boot();
        var body = Body(NewSlug(), NewRuc());

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/platform/tenants", body)).StatusCode);

        var tenantId = Guid.NewGuid();
        await db.ScalarAsync("INSERT INTO tenants (id, name, slug) VALUES (@id, 'T', @slug) RETURNING 1", ("id", tenantId), ("slug", NewSlug()));
        var slug = (string)(await db.ScalarAsync("SELECT slug::text FROM tenants WHERE id = @id", ("id", tenantId)))!;
        await SeedUser(tenantId, "admin-user", "admin");
        var token = (await Json(await Login(client, slug, "admin-user", Password))).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/platform/tenants", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/platform/tenants/{tenantId}/fiscal/test-connection", null)).StatusCode);
    }

    [SkippableFact]
    public async Task Test_connection_uses_the_decrypted_key_of_that_tenant_and_reports_a_diagnosis()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var mode = "ok";
        var fake = new FakeFiscalProvider(_ => mode == "ok" ? null : throw new FiscalProviderException("unauthenticated", "x", 401, isRetryable: false));
        var (app, _) = Boot(fake);
        var (client, _) = await PlatformClient(existing: app);
        var created = await Json(await client.PostAsJsonAsync("/platform/tenants", Body(NewSlug(), NewRuc())));
        var tenantId = created.GetProperty("tenantId").GetGuid();
        var url = $"/platform/tenants/{tenantId}/fiscal/test-connection";

        var ok = await Json(await client.PostAsync(url, null));
        Assert.Equal("ok", ok.GetProperty("status").GetString());
        Assert.Equal(new FiscalAccount("12", ProviderKey), fake.Accounts.Single());

        mode = "bad";
        Assert.Equal("invalid_api_key", (await Json(await client.PostAsync(url, null))).GetProperty("status").GetString());

        // A tenant that does not exist is indistinguishable from one without credentials.
        var unknown = await Json(await client.PostAsync($"/platform/tenants/{Guid.NewGuid()}/fiscal/test-connection", null));
        Assert.Equal("not_configured", unknown.GetProperty("status").GetString());
    }

    [SkippableFact]
    public async Task The_database_itself_refuses_reserved_slugs_and_malformed_rucs()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");

        var reserved = await Assert.ThrowsAsync<PostgresException>(() =>
            db.ScalarAsync("INSERT INTO tenants (name, slug) VALUES ('X', 'admin') RETURNING 1"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, reserved.SqlState);

        var tenantId = Guid.NewGuid();
        await db.ScalarAsync("INSERT INTO tenants (id, name, slug) VALUES (@id, 'T', @slug) RETURNING 1", ("id", tenantId), ("slug", NewSlug()));
        var badRuc = await Assert.ThrowsAsync<PostgresException>(() => db.ScalarAsync(
            "INSERT INTO tenant_fiscal_config (tenant_id, ruc, legal_name, head_office_address) VALUES (@id, 'abc', 'L', 'A') RETURNING 1",
            ("id", tenantId)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, badRuc.SqlState);
    }
}
