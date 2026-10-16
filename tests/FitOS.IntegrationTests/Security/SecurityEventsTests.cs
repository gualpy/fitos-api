using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FitOS.Api.Tenancy;
using FitOS.Infrastructure.Auth;
using FitOS.IntegrationTests.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace FitOS.IntegrationTests.Security;

/// <summary>The security log and password hardening, end to end against real PostgreSQL.</summary>
public sealed class SecurityEventsTests(PostgresFixture db) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Password = "Correct-Horse-9!";

    private const string ClientIp = "198.51.100.7";

    private static readonly IdentityPasswordService CheapHasher = new(1000);

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

    private HttpClient Client(int iterations = 1000, int maxFailures = 5)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseTestSecrets();
            host.UseSetting("ConnectionStrings:Default", db.AppConnection);
            host.UseSetting("Auth:LoginRequestsPerMinute", "100000");
            host.UseSetting("Auth:PasswordIterations", iterations.ToString(System.Globalization.CultureInfo.InvariantCulture));
            host.UseSetting("Auth:MaxFailedAttempts", maxFailures.ToString(System.Globalization.CultureInfo.InvariantCulture));
            host.UseRemoteIp(ClientIp);
        });
        _factories.Add(factory);
        return factory.CreateClient();
    }

    private async Task<(Guid TenantId, string Slug)> NewTenant()
    {
        var id = Guid.NewGuid();
        var slug = "e" + id.ToString("N")[..10];
        await db.ScalarAsync("INSERT INTO tenants (id, name, slug) VALUES (@id, 'T', @s) RETURNING 1", ("id", id), ("s", slug));
        return (id, slug);
    }

    private async Task<Guid> NewUser(Guid tenantId, string username, string? hash = null)
    {
        var id = Guid.NewGuid();
        await db.ScalarAsync(
            "INSERT INTO users (id, tenant_id, username, password_hash, display_name, role) VALUES (@id, @t, @u, @h, 'N', 'admin') RETURNING 1",
            ("id", id), ("t", tenantId), ("u", username), ("h", hash ?? CheapHasher.Hash(Password)));
        return id;
    }

    private static Task<HttpResponseMessage> Login(HttpClient client, string slug, string username, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login") { Content = JsonContent.Create(new { username, password }) };
        request.Headers.Add(TenantHostResolver.TenantHeader, slug);
        request.Headers.UserAgent.ParseAdd("SecurityTests/1.0");
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private async Task<List<string>> Events(Guid userId) =>
        (await db.RowsAsync("SELECT event FROM security_events WHERE user_id = @u ORDER BY id", ("u", userId))).Select(r => (string)r[0]!).ToList();

    private static int IterationsOf(string hash)
    {
        var bytes = Convert.FromBase64String(hash);
        return (bytes[5] << 24) | (bytes[6] << 16) | (bytes[7] << 8) | bytes[8];
    }

    [SkippableFact]
    public async Task Logins_are_recorded_with_who_where_and_why_for_known_users()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewTenant();
        var user = await NewUser(tenant, "ana");
        var client = Client();

        await Login(client, slug, "ana", "wrong-password-1");
        await Login(client, slug, "ana", Password);

        var rows = await db.RowsAsync(
            "SELECT event, tenant_id, host(ip), user_agent, detail->>'reason' FROM security_events WHERE user_id = @u ORDER BY id", ("u", user));
        Assert.Equal(["login_failed", "login_success"], rows.Select(r => (string)r[0]!).ToArray());
        Assert.All(rows, r =>
        {
            Assert.Equal(tenant, r[1]);
            Assert.Equal(ClientIp, r[2]);
            Assert.Equal("SecurityTests/1.0", r[3]);
        });
        Assert.Equal("bad_password", rows[0][4]);
    }

    [SkippableFact]
    public async Task An_unknown_user_is_recorded_but_never_the_name_or_password_that_was_typed()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, slug) = await NewTenant();
        var client = Client();

        await Login(client, slug, "typed-in-the-wrong-box-77", "my-typed-secret-pass-9");

        var rows = await db.RowsAsync("SELECT user_id, detail->>'reason', detail->>'tenantSlug', host(ip) FROM security_events WHERE detail->>'tenantSlug' = @s", ("s", slug));
        var row = Assert.Single(rows);
        Assert.Null(row[0]);
        Assert.Equal("unknown_user", row[1]);
        Assert.Equal(ClientIp, row[3]);
        var everything = await db.ScalarAsync("SELECT string_agg(row_to_json(e)::text, ' ') FROM security_events e WHERE detail->>'tenantSlug' = @s", ("s", slug));
        Assert.DoesNotContain("typed-in-the-wrong-box-77", (string)everything!, StringComparison.Ordinal);
        Assert.DoesNotContain("my-typed-secret-pass-9", (string)everything!, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task The_failure_that_locks_the_account_is_logged_once_and_later_attempts_only_say_locked()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewTenant();
        var user = await NewUser(tenant, "locky");
        var client = Client(maxFailures: 3);

        for (var i = 0; i < 3; i++)
        {
            await Login(client, slug, "locky", "wrong-password-1");
        }

        await Login(client, slug, "locky", "wrong-password-1");
        await Login(client, slug, "locky", Password); // right password, but the account is locked

        var events = await Events(user);
        Assert.Equal(1, events.Count(e => e == "account_locked"));
        Assert.Equal(["login_failed", "login_failed", "login_failed", "account_locked", "login_failed", "login_failed"], events);
        var reasons = (await db.RowsAsync("SELECT detail->>'reason' FROM security_events WHERE user_id = @u AND event = 'login_failed' ORDER BY id", ("u", user))).Select(r => (string)r[0]!).ToArray();
        Assert.Equal(["bad_password", "bad_password", "bad_password", "locked", "locked"], reasons);
    }

    [SkippableFact]
    public async Task Password_changes_logouts_and_token_replays_are_recorded()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewTenant();
        var user = await NewUser(tenant, "ana");
        var client = Client();

        var first = await Json(await Login(client, slug, "ana", Password));
        var second = await Json(await Login(client, slug, "ana", Password));

        // theft: rotate the first token, then replay the old one after the grace window
        var oldRefresh = first.GetProperty("refreshToken").GetString()!;
        await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = oldRefresh });
        await db.ScalarAsync("UPDATE refresh_tokens SET used_at = now() - interval '1 minute' WHERE used_at IS NOT NULL RETURNING 1");
        await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = oldRefresh });

        // logout of the second session
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
        logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", second.GetProperty("accessToken").GetString());
        await client.SendAsync(logout);

        // a stolen token guessing the current password, then a real change
        using var wrong = new HttpRequestMessage(HttpMethod.Post, "/auth/change-password") { Content = JsonContent.Create(new { currentPassword = "guess", newPassword = "Brand-New-Pass-9" }) };
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", second.GetProperty("accessToken").GetString());
        await client.SendAsync(wrong);
        using var change = new HttpRequestMessage(HttpMethod.Post, "/auth/change-password") { Content = JsonContent.Create(new { currentPassword = Password, newPassword = "Brand-New-Pass-9" }) };
        change.Headers.Authorization = new AuthenticationHeaderValue("Bearer", second.GetProperty("accessToken").GetString());
        await client.SendAsync(change);

        var events = await Events(user);
        Assert.Contains("refresh_token_reuse", events);
        Assert.Contains("logout", events);
        Assert.Contains("password_change_failed", events);
        Assert.Contains("password_changed", events);
        Assert.Equal(ClientIp, await db.ScalarAsync("SELECT host(ip) FROM security_events WHERE event = 'refresh_token_reuse' AND user_id = @u", ("u", user)));
        Assert.DoesNotContain("Brand-New-Pass-9", (string)(await db.ScalarAsync("SELECT string_agg(row_to_json(e)::text, ' ') FROM security_events e WHERE user_id = @u", ("u", user)))!, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task The_api_role_can_add_events_but_can_neither_read_nor_erase_them()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewTenant();
        await NewUser(tenant, "ana");
        await Login(Client(), slug, "ana", Password); // the API wrote an event through the function

        Assert.True((long)(await db.ScalarAsync("SELECT count(*) FROM security_events"))! > 0);
        await using var conn = new NpgsqlConnection(db.AppConnection);
        await conn.OpenAsync();
        foreach (var sql in new[]
                 {
                     "SELECT count(*) FROM security_events",
                     "DELETE FROM security_events",
                     "UPDATE security_events SET event = 'nothing_happened'",
                     "INSERT INTO security_events (event) VALUES ('forged_event')",
                     "TRUNCATE security_events",
                 })
        {
            await using var command = new NpgsqlCommand(sql, conn);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    [SkippableFact]
    public async Task A_failing_security_log_never_breaks_a_login()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewTenant();
        var user = await NewUser(tenant, "ana");
        var client = Client();
        const string function = "security_event_log(text, uuid, uuid, text, text, jsonb)";

        await db.ScalarAsync($"REVOKE EXECUTE ON FUNCTION {function} FROM fitos_app; SELECT 1");
        try
        {
            var response = await Login(client, slug, "ana", Password);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode); // the log is best effort: access is not hostage to it
            Assert.Empty(await Events(user));
        }
        finally
        {
            await db.ScalarAsync($"GRANT EXECUTE ON FUNCTION {function} TO fitos_app; SELECT 1");
        }
    }

    [SkippableFact]
    public async Task An_old_hash_is_upgraded_at_login_without_closing_any_session_and_only_once()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewTenant();
        var user = await NewUser(tenant, "ana"); // hashed with 1 000 iterations
        var client = Client(iterations: 5000);
        Assert.Equal(1000, IterationsOf((string)(await db.ScalarAsync("SELECT password_hash FROM users WHERE id = @u", ("u", user)))!));

        await Login(client, slug, "ana", "wrong-password-1");
        Assert.Equal(1000, IterationsOf((string)(await db.ScalarAsync("SELECT password_hash FROM users WHERE id = @u", ("u", user)))!)); // a failed attempt upgrades nothing

        var alreadyOpen = await Json(await Login(client, slug, "ana", Password)); // upgrades 1 000 -> 5 000
        var upgraded = (string)(await db.ScalarAsync("SELECT password_hash FROM users WHERE id = @u", ("u", user)))!;
        Assert.Equal(5000, IterationsOf(upgraded));

        Assert.Equal(HttpStatusCode.OK, (await Login(client, slug, "ana", Password)).StatusCode); // the new hash works
        Assert.Equal(upgraded, await db.ScalarAsync("SELECT password_hash FROM users WHERE id = @u", ("u", user))); // and is not rewritten again

        // Unlike a password change, an upgrade must not log anyone out.
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM auth_sessions WHERE user_id = @u AND revoked_at IS NOT NULL", ("u", user)));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = alreadyOpen.GetProperty("refreshToken").GetString() })).StatusCode);
    }

    [SkippableFact]
    public async Task A_corrupted_stored_hash_rejects_the_login_instead_of_crashing()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewTenant();
        await NewUser(tenant, "broken", hash: "this-is-not-a-hash");

        var response = await Login(Client(), slug, "broken", Password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
