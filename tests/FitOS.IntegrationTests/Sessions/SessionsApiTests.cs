using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FitOS.Api.Tenancy;
using FitOS.Infrastructure.Auth;
using FitOS.IntegrationTests.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace FitOS.IntegrationTests.Sessions;

/// <summary>Revocable sessions end to end: short access tokens, rotating renewal tokens, theft detection, logout.</summary>
public sealed class SessionsApiTests(PostgresFixture db) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Password = "Correct-Horse-9!";

    private static readonly IdentityPasswordService Hasher = new();

    private readonly List<WebApplicationFactory<Program>> _factories = [];

    private WebApplicationFactory<Program>? _app;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }

        return Task.CompletedTask;
    }

    private sealed record Tokens(string Access, string Refresh, JsonElement Body);

    private HttpClient Client()
    {
        if (_app is null)
        {
            _app = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            {
                host.UseTestSecrets();
                host.UseSetting("ConnectionStrings:Default", db.AppConnection);
                host.UseSetting("Auth:LoginRequestsPerMinute", "100000");
            });
            _factories.Add(_app);
        }

        return _app.CreateClient();
    }

    private async Task<(Guid TenantId, string Slug)> NewTenant(string status = "active")
    {
        var id = Guid.NewGuid();
        var slug = "x" + id.ToString("N")[..10];
        await db.ScalarAsync("INSERT INTO tenants (id, name, slug, status) VALUES (@id, 'T', @s, @st) RETURNING 1", ("id", id), ("s", slug), ("st", status));
        return (id, slug);
    }

    private async Task<Guid> NewUser(Guid tenantId, string username, bool active = true, bool mustChange = false)
    {
        var id = Guid.NewGuid();
        await db.ScalarAsync(
            "INSERT INTO users (id, tenant_id, username, password_hash, display_name, role, is_active, must_change_password) VALUES (@id, @t, @u, @h, 'N', 'admin', @a, @m) RETURNING 1",
            ("id", id), ("t", tenantId), ("u", username), ("h", Hasher.Hash(Password)), ("a", active), ("m", mustChange));
        return id;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<Tokens> Login(HttpClient client, string slug, string username, string password = Password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login") { Content = JsonContent.Create(new { username, password }) };
        request.Headers.Add(TenantHostResolver.TenantHeader, slug);
        request.Headers.UserAgent.ParseAdd("SessionsTests/1.0");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        return new Tokens(body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!, body);
    }

    private static Task<HttpResponseMessage> Refresh(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync("/auth/refresh", new { refreshToken });

    private static byte[] Sha256(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Code(JsonElement body) => body.GetProperty("error").GetProperty("code").GetString()!;

    private async Task<(Guid TenantId, string Slug, Guid UserId, HttpClient Client, Tokens Tokens)> Session(string username = "ana")
    {
        var (tenant, slug) = await NewTenant();
        var user = await NewUser(tenant, username);
        var client = Client();
        return (tenant, slug, user, client, await Login(client, slug, username));
    }

    [SkippableFact]
    public async Task Login_returns_a_short_access_token_and_a_renewal_token_of_which_only_the_hash_is_stored()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, _, user, _, tokens) = await Session();

        Assert.Equal(43, tokens.Refresh.Length); // 256 random bits, base64url
        var expiresAt = tokens.Body.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.InRange((expiresAt - DateTimeOffset.UtcNow).TotalMinutes, 13, 15.5); // short: it cannot be revoked
        Assert.True(tokens.Body.GetProperty("refreshExpiresAt").GetDateTimeOffset() > expiresAt);

        var stored = (byte[])(await db.ScalarAsync(
            "SELECT rt.token_hash FROM refresh_tokens rt JOIN auth_sessions s ON s.id = rt.session_id WHERE s.user_id = @u", ("u", user)))!;
        Assert.Equal(Sha256(tokens.Refresh), stored);
        Assert.NotEqual(Encoding.UTF8.GetBytes(tokens.Refresh), stored);
        Assert.Equal("SessionsTests/1.0", await db.ScalarAsync("SELECT user_agent FROM auth_sessions WHERE user_id = @u", ("u", user)));

        var sid = new JwtSecurityTokenHandler().ReadJwtToken(tokens.Access).Claims.Single(c => c.Type == "sid").Value;
        Assert.Equal(sid, (await db.ScalarAsync("SELECT id::text FROM auth_sessions WHERE user_id = @u", ("u", user))));
    }

    [SkippableFact]
    public async Task Renewing_rotates_the_token_and_the_new_access_token_works()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, _, user, client, first) = await Session();

        var response = await Refresh(client, first.Refresh);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        var next = body.GetProperty("refreshToken").GetString()!;
        Assert.NotEqual(first.Refresh, next);
        Assert.NotEqual(first.Access, body.GetProperty("accessToken").GetString());
        Assert.Equal(user, body.GetProperty("user").GetProperty("id").GetGuid());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
        Assert.NotNull(await db.ScalarAsync("SELECT used_at FROM refresh_tokens WHERE token_hash = @h AND used_at IS NOT NULL", ("h", Sha256(first.Refresh))));
        Assert.Equal(1L, await db.ScalarAsync("SELECT count(*) FROM auth_sessions WHERE user_id = @u", ("u", user))); // same session, new token

        // The new token renews again, and so on.
        Assert.Equal(HttpStatusCode.OK, (await Refresh(client, next)).StatusCode);
    }

    [SkippableFact]
    public async Task A_used_token_that_comes_back_later_means_theft_and_kills_the_whole_session()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, _, user, client, first) = await Session();
        var rotated = await Json(await Refresh(client, first.Refresh));
        var legitimateNext = rotated.GetProperty("refreshToken").GetString()!;
        await db.ScalarAsync("UPDATE refresh_tokens SET used_at = now() - interval '1 minute' WHERE token_hash = @h RETURNING 1", ("h", Sha256(first.Refresh)));

        var thief = await Refresh(client, first.Refresh); // the old token, presented again after the grace window

        Assert.Equal(HttpStatusCode.Unauthorized, thief.StatusCode);
        Assert.Equal("invalid_refresh_token", Code(await Json(thief)));
        Assert.Equal("refresh_reuse", await db.ScalarAsync("SELECT revoked_reason FROM auth_sessions WHERE user_id = @u", ("u", user)));
        // The legitimate client's newest token is dead too: both must sign in again.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(client, legitimateNext)).StatusCode);
    }

    [SkippableFact]
    public async Task Two_simultaneous_renewals_of_the_same_token_do_not_kill_the_session()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, _, user, client, first) = await Session();
        var winner = await Json(await Refresh(client, first.Refresh));

        var loser = await Refresh(client, first.Refresh); // arrives right after: same client, two requests

        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Equal("refresh_in_progress", Code(await Json(loser)));
        Assert.Equal(DBNull.Value, await db.ScalarAsync("SELECT revoked_at FROM auth_sessions WHERE user_id = @u", ("u", user))); // still alive
        Assert.Equal(HttpStatusCode.OK, (await Refresh(client, winner.GetProperty("refreshToken").GetString()!)).StatusCode);
    }

    [SkippableFact]
    public async Task Idle_and_absolute_expiry_end_the_session()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, _, user, client, idle) = await Session("idle");
        await db.ScalarAsync("UPDATE refresh_tokens SET expires_at = now() - interval '1 second' WHERE token_hash = @h RETURNING 1", ("h", Sha256(idle.Refresh)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(client, idle.Refresh)).StatusCode);

        var (_, _, _, client2, absolute) = await Session("absolute");
        await db.ScalarAsync("UPDATE auth_sessions SET expires_at = now() - interval '1 second' WHERE user_id = (SELECT user_id FROM auth_sessions s JOIN refresh_tokens r ON r.session_id = s.id WHERE r.token_hash = @h) RETURNING 1", ("h", Sha256(absolute.Refresh)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(client2, absolute.Refresh)).StatusCode);
        _ = user;
    }

    [SkippableFact]
    public async Task The_renewal_token_never_outlives_the_sessions_absolute_limit()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, _, user, client, first) = await Session();
        // The session ends in 1 minute although the idle window is 8 hours.
        await db.ScalarAsync("UPDATE auth_sessions SET expires_at = now() + interval '1 minute' WHERE user_id = @u RETURNING 1", ("u", user));

        var body = await Json(await Refresh(client, first.Refresh));

        var tokenExpiry = body.GetProperty("refreshExpiresAt").GetDateTimeOffset();
        Assert.True(tokenExpiry <= DateTimeOffset.UtcNow.AddMinutes(1.1), tokenExpiry.ToString("O"));
    }

    [SkippableFact]
    public async Task Logout_revokes_only_that_session()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, slug, user, client, phone) = await Session();
        var laptop = await Login(client, slug, "ana"); // a second session of the same user

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
        logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", phone.Access);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(logout)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(client, phone.Refresh)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Refresh(client, laptop.Refresh)).StatusCode);
        Assert.Equal("logout", await db.ScalarAsync("SELECT revoked_reason FROM auth_sessions WHERE revoked_at IS NOT NULL AND user_id = @u", ("u", user)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/auth/logout", null)).StatusCode); // needs a token
    }

    [SkippableFact]
    public async Task Changing_the_password_closes_every_session_of_that_user()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, slug, user, client, first) = await Session();
        var other = await Login(client, slug, "ana");
        var changer = Client();
        changer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.Access);

        var changed = await changer.PostAsJsonAsync("/auth/change-password", new { currentPassword = Password, newPassword = "Brand-New-Pass-9" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(client, first.Refresh)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(client, other.Refresh)).StatusCode);
        Assert.Equal(2L, await db.ScalarAsync("SELECT count(*) FROM auth_sessions WHERE user_id = @u AND revoked_reason = 'password_changed'", ("u", user)));
        _ = await Login(client, slug, "ana", "Brand-New-Pass-9"); // and the new password opens a fresh session
    }

    [SkippableFact]
    public async Task A_deactivated_user_or_a_suspended_gym_cannot_renew()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, _, user, client, first) = await Session("fired");
        await db.ScalarAsync("UPDATE users SET is_active = false WHERE id = @u RETURNING 1", ("u", user));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(client, first.Refresh)).StatusCode);
        Assert.Equal("inactive", await db.ScalarAsync("SELECT revoked_reason FROM auth_sessions WHERE user_id = @u", ("u", user)));

        var (tenant, _, otherUser, client2, second) = await Session("suspended-gym");
        await db.ScalarAsync("UPDATE tenants SET status = 'suspended' WHERE id = @t RETURNING 1", ("t", tenant));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(client2, second.Refresh)).StatusCode);
        Assert.Equal("inactive", await db.ScalarAsync("SELECT revoked_reason FROM auth_sessions WHERE user_id = @u", ("u", otherUser)));
    }

    [SkippableFact]
    public async Task A_pending_password_change_survives_renewal()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewTenant();
        await NewUser(tenant, "temp", mustChange: true);
        var client = Client();
        var first = await Login(client, slug, "temp");

        var body = await Json(await Refresh(client, first.Refresh));

        Assert.True(body.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        var claims = new JwtSecurityTokenHandler().ReadJwtToken(body.GetProperty("accessToken").GetString()).Claims;
        Assert.Contains(claims, c => c is { Type: "pwd_change", Value: "true" });
    }

    [SkippableFact]
    public async Task Garbage_missing_or_oversized_tokens_are_refused_without_touching_state()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, _, _, client, _) = await Session();
        var before = await db.ScalarAsync("SELECT count(*) FROM refresh_tokens");

        foreach (var bad in new[] { "garbage", new string('A', 43), "has spaces and symbols !!!!!!!!!!!!!!!!!!!!!!!!!", new string('B', 44) })
        {
            var response = await Refresh(client, bad);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("invalid_refresh_token", Code(await Json(response)));
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/refresh", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Refresh(client, new string('A', 500))).StatusCode);
        Assert.Equal(before, await db.ScalarAsync("SELECT count(*) FROM refresh_tokens"));
    }

    [SkippableFact]
    public async Task The_api_database_role_cannot_read_or_write_the_token_tables_directly()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        await Session();
        await using var conn = new NpgsqlConnection(db.AppConnection);
        await conn.OpenAsync();

        foreach (var sql in new[]
                 {
                     "SELECT count(*) FROM refresh_tokens",
                     "SELECT count(*) FROM auth_sessions",
                     "DELETE FROM auth_sessions",
                     "UPDATE refresh_tokens SET used_at = NULL",
                 })
        {
            await using var command = new NpgsqlCommand(sql, conn);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }
}
