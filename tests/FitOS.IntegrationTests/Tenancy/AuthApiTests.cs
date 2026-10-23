using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FitOS.Api.Tenancy;
using FitOS.Infrastructure.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FitOS.IntegrationTests.Tenancy;

/// <summary>Login end to end: real HTTP pipeline, real PostgreSQL, real RLS and SECURITY DEFINER functions.</summary>
public sealed class AuthApiTests(PostgresFixture db) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Password = "Correct-Horse-9!";

    private readonly List<WebApplicationFactory<Program>> _factories = [];

    private static readonly IdentityPasswordService Hasher = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }

        return Task.CompletedTask;
    }

    private HttpClient Client(int loginsPerMinute = 1000)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseTestSecrets();
            host.UseSetting("ConnectionStrings:Default", db.AppConnection);
            host.UseSetting("Auth:LoginRequestsPerMinute", loginsPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
            host.UseSetting("Auth:MaxFailedAttempts", "5");
        });
        _factories.Add(factory);
        return factory.CreateClient();
    }

    private async Task<Guid> NewTenant(string status = "active")
    {
        var id = Guid.NewGuid();
        await db.ScalarAsync(
            "INSERT INTO tenants (id, name, slug, status) VALUES (@id, 'T', @slug, @status) RETURNING 1",
            ("id", id), ("slug", "t" + id.ToString("N")[..12]), ("status", status));
        return id;
    }

    private async Task<string> Slug(Guid tenantId) =>
        (string)(await db.ScalarAsync("SELECT slug::text FROM tenants WHERE id = @id", ("id", tenantId)))!;

    private async Task<Guid> NewUser(Guid? tenantId, string username, string password = Password, string role = "admin", bool active = true)
    {
        var id = Guid.NewGuid();
        await db.ScalarAsync(
            "INSERT INTO users (id, tenant_id, username, password_hash, display_name, role, is_active) VALUES (@id, @tenant, @user, @hash, @name, @role, @active) RETURNING 1",
            ("id", id), ("tenant", tenantId), ("user", username), ("hash", Hasher.Hash(password)), ("name", "Name " + username), ("role", role), ("active", active));
        return id;
    }

    private static Task<HttpResponseMessage> Login(HttpClient client, string? slug, string username, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = JsonContent.Create(new { username, password }),
        };
        request.Headers.Add(TenantHostResolver.TenantHeader, slug ?? "platform");
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [SkippableFact]
    public async Task Valid_login_returns_a_token_that_works_and_carries_the_tenant()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var tenant = await NewTenant();
        var userId = await NewUser(tenant, "ana");
        var client = Client();

        var response = await Login(client, await Slug(tenant), "ana", Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Body(response);
        var token = body.GetProperty("accessToken").GetString()!;
        Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());
        Assert.Equal(tenant, body.GetProperty("user").GetProperty("tenantId").GetGuid());
        Assert.Equal("admin", body.GetProperty("user").GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("user").GetProperty("customerId").ValueKind); // staff have no customer record
        Assert.DoesNotContain("passwordHash", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(tenant.ToString(), new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.Single(c => c.Type == "tenant_id").Value);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var me = await client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var meBody = await Body(me);
        Assert.Equal(userId.ToString(), meBody.GetProperty("id").GetString());
        Assert.Equal(tenant.ToString(), meBody.GetProperty("tenantId").GetString());
        Assert.NotNull(await db.ScalarAsync("SELECT last_login_at FROM users WHERE id = @id AND last_login_at IS NOT NULL", ("id", userId)));
    }

    [SkippableFact]
    public async Task Wrong_password_and_unknown_user_are_indistinguishable()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var tenant = await NewTenant();
        await NewUser(tenant, "bea");
        var slug = await Slug(tenant);
        var client = Client();

        var wrong = await Login(client, slug, "bea", "not-the-password");
        var unknown = await Login(client, slug, "nobody", "not-the-password");

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task Same_username_in_two_tenants_never_crosses_over()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var tenantA = await NewTenant();
        var tenantB = await NewTenant();
        await NewUser(tenantA, "juan", "Password-of-A-1!");
        await NewUser(tenantB, "juan", "Password-of-B-1!");
        var client = Client();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, await Slug(tenantA), "juan", "Password-of-B-1!")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, await Slug(tenantB), "juan", "Password-of-A-1!")).StatusCode);

        var ok = await Login(client, await Slug(tenantB), "juan", "Password-of-B-1!");
        Assert.Equal(tenantB, (await Body(ok)).GetProperty("user").GetProperty("tenantId").GetGuid());
    }

    [SkippableFact]
    public async Task Platform_admin_logs_in_without_a_tenant_and_tenant_users_cannot_use_the_platform_door()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var tenant = await NewTenant();
        await NewUser(null, "root-1", role: "platform_admin");
        await NewUser(tenant, "tenant-user");
        var client = Client();

        var platform = await Login(client, null, "root-1", Password);
        Assert.Equal(HttpStatusCode.OK, platform.StatusCode);
        var token = (await Body(platform)).GetProperty("accessToken").GetString()!;
        Assert.DoesNotContain(new JwtSecurityTokenHandler().ReadJwtToken(token).Claims, c => c.Type == "tenant_id");

        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, null, "tenant-user", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, await Slug(tenant), "root-1", Password)).StatusCode);
    }

    [SkippableFact]
    public async Task Five_failures_lock_the_account_even_for_the_right_password_until_the_lock_expires()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var tenant = await NewTenant();
        var userId = await NewUser(tenant, "locky");
        var slug = await Slug(tenant);
        var client = Client();

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, slug, "locky", "wrong-password")).StatusCode);
        }

        Assert.NotNull(await db.ScalarAsync("SELECT locked_until FROM users WHERE id = @id AND locked_until IS NOT NULL", ("id", userId)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, slug, "locky", Password)).StatusCode);

        await db.ScalarAsync("UPDATE users SET locked_until = now() - interval '1 minute' WHERE id = @id RETURNING 1", ("id", userId));
        Assert.Equal(HttpStatusCode.OK, (await Login(client, slug, "locky", Password)).StatusCode);
        Assert.Equal(0, await db.ScalarAsync("SELECT failed_login_count FROM users WHERE id = @id", ("id", userId)));
    }

    [SkippableFact]
    public async Task Inactive_user_and_suspended_tenant_cannot_log_in()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var active = await NewTenant();
        var suspended = await NewTenant("suspended");
        await NewUser(active, "fired", active: false);
        await NewUser(suspended, "nice");
        var client = Client();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, await Slug(active), "fired", Password)).StatusCode);

        // A wrong password on a suspended gym looks like any other failure; only the right one learns why it cannot enter.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, await Slug(suspended), "nice", "Wrong-Password-1!")).StatusCode);
        var blocked = await Login(client, await Slug(suspended), "nice", Password);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("tenant_inactive", (await Body(blocked)).GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Login_attempts_are_rate_limited_per_client()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var tenant = await NewTenant();
        var slug = await Slug(tenant);
        var client = Client(loginsPerMinute: 3);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, slug, "nobody", "x")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(client, slug, "nobody", "x")).StatusCode);
    }

    [SkippableFact]
    public async Task Invalid_host_and_empty_fields_are_rejected_before_touching_credentials()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var client = Client();

        Assert.Equal(HttpStatusCode.BadRequest, (await Login(client, "Bad Slug!", "a", "b")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Login(client, "titan", "", "b")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Login(client, "titan", "a", new string('x', 201))).StatusCode);
    }

    [SkippableFact]
    public async Task Protected_endpoint_rejects_missing_and_tampered_tokens()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var tenant = await NewTenant();
        await NewUser(tenant, "tam");
        var client = Client();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);

        var token = (await Body(await Login(client, await Slug(tenant), "tam", Password))).GetProperty("accessToken").GetString()!;
        var parts = token.Split('.');
        var tampered = $"{parts[0]}.{parts[1]}.{new string(parts[2].Reverse().ToArray())}";
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tampered);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
    }

    [SkippableFact]
    public async Task The_app_role_cannot_read_users_directly_only_through_the_login_functions()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var tenant = await NewTenant();
        await NewUser(tenant, "hidden");
        await using var conn = new Npgsql.NpgsqlConnection(db.AppConnection);
        await conn.OpenAsync();

        await using (var direct = new Npgsql.NpgsqlCommand("SELECT count(*) FROM users", conn))
        {
            Assert.Equal(0L, await direct.ExecuteScalarAsync()); // RLS: no tenant set => nothing
        }

        await using var viaFunction = new Npgsql.NpgsqlCommand("SELECT count(*) FROM auth_find_user(@slug, 'hidden')", conn);
        viaFunction.Parameters.AddWithValue("slug", await Slug(tenant));
        Assert.Equal(1L, await viaFunction.ExecuteScalarAsync());
    }
}
