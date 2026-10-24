using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FitOS.Api.Tenancy;
using FitOS.Application.Auth;
using FitOS.Application.Email;
using FitOS.Infrastructure.Auth;
using FitOS.Migrator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FitOS.IntegrationTests.Tenancy;

/// <summary>The platform administrators' second step end to end: real pipeline, real PostgreSQL.</summary>
public sealed class PlatformLoginConfirmationTests(PostgresFixture db) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Password = "Correct-Horse-9!";

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

    private sealed class CapturingEmail : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private HttpClient Client(CapturingEmail email)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseTestSecrets();
            host.UseSetting("ConnectionStrings:Default", db.AppConnection);
            host.UseSetting("Auth:LoginRequestsPerMinute", "1000");
            host.UseSetting("Auth:SkipPlatformConfirmation", "false");
            host.ConfigureTestServices(services => services.AddSingleton<IEmailSender>(email));
        });
        _factories.Add(factory);
        return factory.CreateClient();
    }

    private async Task<string> NewAdmin(string? email)
    {
        var username = "root-" + Guid.NewGuid().ToString("N")[..8];
        if (email is not null)
        {
            // An e-mail belongs to one platform administrator (it is also a way to log in): free it from earlier test admins.
            await db.ScalarAsync("UPDATE users SET email = NULL WHERE tenant_id IS NULL AND email = @e::citext RETURNING 1", ("e", email));
        }

        Assert.True(await PlatformAdminBootstrap.CreateAsync(db.OwnerConnection, username, "Root", Password, Hasher, email));
        await db.ScalarAsync("UPDATE users SET must_change_password = false WHERE tenant_id IS NULL AND username = @u RETURNING 1", ("u", username));
        return username;
    }

    private static async Task<HttpResponseMessage> Login(HttpClient client, string username, string password = Password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login") { Content = JsonContent.Create(new { username, password }) };
        request.Headers.Add(TenantHostResolver.TenantHeader, "platform");
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static string LinkIn(CapturingEmail email) => email.Sent[^1].Body.Split("#confirm=")[1].Split('\n')[0].Trim();

    [SkippableFact]
    public async Task The_password_alone_gives_no_token_and_the_link_with_the_browsers_secret_does()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var email = new CapturingEmail();
        var client = Client(email);
        var username = await NewAdmin("Admin@Platform.Test");

        var first = await Login(client, username);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var body = await Json(first);
        Assert.Equal("confirmation_required", body.GetProperty("status").GetString());
        Assert.False(body.TryGetProperty("accessToken", out _));
        Assert.False(body.TryGetProperty("refreshToken", out _));
        var secret = body.GetProperty("challengeSecret").GetString()!;
        Assert.Equal("Admin@Platform.Test", Assert.Single(email.Sent).To);
        Assert.DoesNotContain(secret, email.Sent[0].Body);

        var done = await client.PostAsJsonAsync("/auth/confirm-login", new { token = LinkIn(email), secret });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var session = await Json(done);
        Assert.Equal("platform_admin", session.GetProperty("user").GetProperty("role").GetString());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/platform/tenants")).StatusCode);

        var replay = await client.PostAsJsonAsync("/auth/confirm-login", new { token = LinkIn(email), secret });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("invalid_link", (await Json(replay)).GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Resetting_a_lost_password_replaces_it_ends_sessions_lifts_lockout_and_burns_pending_links()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var email = new CapturingEmail();
        var client = Client(email);
        var username = await NewAdmin("admin@platform.test");
        var secret = (await Json(await Login(client, username))).GetProperty("challengeSecret").GetString()!;
        var pendingLink = LinkIn(email);
        await db.ScalarAsync(
            "INSERT INTO auth_sessions (user_id, expires_at) SELECT id, now() + interval '1 day' FROM users WHERE tenant_id IS NULL AND username = @u RETURNING 1",
            ("u", username));
        await db.ScalarAsync("UPDATE users SET failed_login_count = 5, locked_until = now() + interval '1 hour' WHERE tenant_id IS NULL AND username = @u RETURNING 1", ("u", username));

        Assert.True(await PlatformAdminBootstrap.ResetPasswordAsync(db.OwnerConnection, username, "Brand-New-Pass-7!", Hasher));

        Assert.Equal(true, await db.ScalarAsync("SELECT must_change_password FROM users WHERE tenant_id IS NULL AND username = @u", ("u", username)));
        Assert.Equal(0, await db.ScalarAsync("SELECT failed_login_count FROM users WHERE tenant_id IS NULL AND username = @u", ("u", username)));
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM auth_sessions s JOIN users u ON u.id = s.user_id WHERE u.username = @u AND s.revoked_at IS NULL", ("u", username)));
        Assert.Equal(1L, await db.ScalarAsync("SELECT count(*) FROM security_events e JOIN users u ON u.id = e.user_id WHERE u.username = @u AND e.event = 'platform_password_reset'", ("u", username)));

        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, username, Password)).StatusCode); // the old one is gone
        Assert.Equal(HttpStatusCode.Accepted, (await Login(client, username, "Brand-New-Pass-7!")).StatusCode); // the new one reaches the e-mail step
        var burned = await client.PostAsJsonAsync("/auth/confirm-login", new { token = pendingLink, secret });
        Assert.Equal(HttpStatusCode.BadRequest, burned.StatusCode);
    }

    [SkippableFact]
    public async Task Resetting_a_password_touches_only_platform_administrators()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");

        Assert.False(await PlatformAdminBootstrap.ResetPasswordAsync(db.OwnerConnection, "no-such-admin", "Brand-New-Pass-7!", Hasher));
    }

    [SkippableFact]
    public async Task A_platform_administrator_can_log_in_with_the_e_mail_or_the_username()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var email = new CapturingEmail();
        var client = Client(email);
        var username = await NewAdmin("Boss@Platform.Test");

        var byEmail = await Login(client, "boss@platform.test");
        var byUsername = await Login(client, username);
        var wrong = await Login(client, "someone-else@platform.test");

        Assert.Equal(HttpStatusCode.Accepted, byEmail.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, byUsername.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.All(email.Sent, m => Assert.Equal("Boss@Platform.Test", m.To));
    }

    [SkippableFact]
    public async Task The_link_opened_elsewhere_without_the_secret_does_not_log_in_and_is_burned()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var email = new CapturingEmail();
        var client = Client(email);
        var username = await NewAdmin("admin@platform.test");
        var secret = (await Json(await Login(client, username))).GetProperty("challengeSecret").GetString()!;
        var link = LinkIn(email);

        var stranger = await client.PostAsJsonAsync("/auth/confirm-login", new { token = link, secret = "not-the-browsers-secret" });
        var legitimate = await client.PostAsJsonAsync("/auth/confirm-login", new { token = link, secret });

        Assert.Equal(HttpStatusCode.BadRequest, stranger.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, legitimate.StatusCode); // burned: sign in again
        Assert.Equal(HttpStatusCode.Accepted, (await Login(client, username)).StatusCode);
    }

    [SkippableFact]
    public async Task A_new_login_voids_the_previous_link()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var email = new CapturingEmail();
        var client = Client(email);
        var username = await NewAdmin("admin@platform.test");
        var firstSecret = (await Json(await Login(client, username))).GetProperty("challengeSecret").GetString()!;
        var firstLink = LinkIn(email);
        var secondSecret = (await Json(await Login(client, username))).GetProperty("challengeSecret").GetString()!;

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/confirm-login", new { token = firstLink, secret = firstSecret })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/auth/confirm-login", new { token = LinkIn(email), secret = secondSecret })).StatusCode);
    }

    [SkippableFact]
    public async Task An_expired_link_says_so_only_to_the_browser_that_holds_the_secret()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var email = new CapturingEmail();
        var client = Client(email);
        var username = await NewAdmin("admin@platform.test");
        var secret = (await Json(await Login(client, username))).GetProperty("challengeSecret").GetString()!;
        await db.ScalarAsync("UPDATE login_challenges SET expires_at = now() - interval '1 minute' WHERE used_at IS NULL AND user_id = (SELECT id FROM users WHERE tenant_id IS NULL AND username = @u) RETURNING 1", ("u", username));

        var response = await client.PostAsJsonAsync("/auth/confirm-login", new { token = LinkIn(email), secret });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("link_expired", (await Json(response)).GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task A_wrong_password_sends_nothing_and_an_account_without_email_is_told_so()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var email = new CapturingEmail();
        var client = Client(email);
        var withMail = await NewAdmin("admin@platform.test");
        var withoutMail = await NewAdmin(null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, withMail, "Wrong-Password-1!")).StatusCode);
        Assert.Empty(email.Sent);

        var blocked = await Login(client, withoutMail);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("confirmation_unavailable", (await Json(blocked)).GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(email.Sent);
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM login_challenges WHERE user_id = (SELECT id FROM users WHERE tenant_id IS NULL AND username = @u)", ("u", withoutMail)));
    }

    [SkippableFact]
    public async Task Garbage_and_missing_fields_are_rejected()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var client = Client(new CapturingEmail());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/confirm-login", new { token = "nope", secret = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/confirm-login", new { token = "", secret = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/confirm-login", new { token = "x" })).StatusCode);
    }

    [SkippableFact]
    public async Task The_email_of_a_platform_admin_can_be_changed_with_the_migrator_and_the_challenge_table_is_off_limits_to_the_api_role()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var username = await NewAdmin(null);

        Assert.True(await PlatformAdminBootstrap.SetEmailAsync(db.OwnerConnection, username, "new@platform.test"));
        Assert.False(await PlatformAdminBootstrap.SetEmailAsync(db.OwnerConnection, "no-such-admin", "x@platform.test"));
        Assert.Equal("new@platform.test", await db.ScalarAsync("SELECT email::text FROM users WHERE tenant_id IS NULL AND username = @u", ("u", username)));

        await using var connection = new Npgsql.NpgsqlConnection(db.AppConnection);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("SELECT count(*) FROM login_challenges", connection);
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => command.ExecuteScalarAsync());
        Assert.Equal(Npgsql.PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Theory]
    [InlineData("Development", true, false)]
    [InlineData("Development", false, true)]
    [InlineData("Production", true, true)] // only Development may switch the step off
    [InlineData("Production", false, true)]
    public void Only_development_may_skip_the_confirmation(string environment, bool skip, bool required)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment(environment);
            host.UseTestSecrets();
            host.UseProductionMailSettings();
            host.UseSetting("ConnectionStrings:Default", "Host=127.0.0.1;Database=x;Username=x;Password=x");
            host.UseSetting("Auth:SkipPlatformConfirmation", skip ? "true" : "false");
        });

        using var scope = factory.Services.CreateScope();

        Assert.Equal(required, scope.ServiceProvider.GetRequiredService<LoginChallengeService>().Required);
    }
}
