using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FitOS.Api.Tenancy;
using FitOS.Application.Email;
using FitOS.Domain.Identification;
using FitOS.Infrastructure.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FitOS.IntegrationTests.Tenancy;

/// <summary>Mail settings kept in the database: real pipeline, real PostgreSQL, real encryption; only SMTP itself is faked.</summary>
public sealed class EmailSettingsApiTests(PostgresFixture db) : IClassFixture<PostgresFixture>, IAsyncLifetime
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

    private sealed class RecordingSmtp : ISmtpDelivery
    {
        public List<(SmtpSettings Settings, EmailMessage Message)> Sent { get; } = [];

        public string? FailWithReason { get; set; }

        public Task SendAsync(SmtpSettings settings, EmailMessage message, CancellationToken cancellationToken)
        {
            if (FailWithReason is not null)
            {
                throw new EmailDeliveryException("refused", FailWithReason);
            }

            Sent.Add((settings, message));
            return Task.CompletedTask;
        }
    }

    private (HttpClient Client, WebApplicationFactory<Program> App) Boot(RecordingSmtp smtp, bool skipConfirmation = true)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseTestSecrets();
            host.UseSetting("ConnectionStrings:Default", db.AppConnection);
            host.UseSetting("Auth:LoginRequestsPerMinute", "1000");
            host.UseSetting("Auth:SkipPlatformConfirmation", skipConfirmation ? "true" : "false");
            host.ConfigureTestServices(services => services.AddSingleton<ISmtpDelivery>(smtp));
        });
        _factories.Add(factory);
        return (factory.CreateClient(), factory);
    }

    private static async Task<HttpResponseMessage> Login(HttpClient client, string slug, string username)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login") { Content = JsonContent.Create(new { username, password = Password }) };
        request.Headers.Add(TenantHostResolver.TenantHeader, slug);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private async Task<HttpClient> Platform(RecordingSmtp smtp)
    {
        var (client, _) = Boot(smtp);
        var username = "root-" + Guid.NewGuid().ToString("N")[..8];
        await db.ScalarAsync(
            "INSERT INTO users (tenant_id, username, password_hash, display_name, role) VALUES (NULL, @u, @h, 'Root', 'platform_admin') RETURNING 1",
            ("u", username), ("h", Hasher.Hash(Password)));
        var token = (await Json(await Login(client, "platform", username))).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static object Settings(string? password = "clave-del-buzon") => new
    {
        host = "smtp.hostinger.com",
        port = 465,
        security = "ssl_on_connect",
        username = "no-reply@support-me.net",
        fromAddress = "no-reply@support-me.net",
        password,
    };

    [SkippableFact]
    public async Task Settings_are_saved_encrypted_and_the_password_never_comes_back()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        await db.ScalarAsync("DELETE FROM email_settings RETURNING 1");
        var client = await Platform(new RecordingSmtp());

        var empty = await Json(await client.GetAsync("/platform/email-settings"));
        Assert.False(empty.GetProperty("configured").GetBoolean());

        var saved = await client.PutAsJsonAsync("/platform/email-settings", Settings());
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var text = await saved.Content.ReadAsStringAsync();
        Assert.DoesNotContain("clave-del-buzon", text);
        var view = JsonDocument.Parse(text).RootElement;
        Assert.True(view.GetProperty("configured").GetBoolean());
        Assert.Equal("••••uzon", view.GetProperty("passwordHint").GetString());
        Assert.Equal("ssl_on_connect", view.GetProperty("security").GetString());

        var stored = (byte[])(await db.ScalarAsync("SELECT password_enc FROM email_settings WHERE id = 1"))!;
        Assert.DoesNotContain("clave-del-buzon", System.Text.Encoding.UTF8.GetString(stored));

        // Saving again without a password keeps the one already stored.
        var again = await client.PutAsJsonAsync("/platform/email-settings", Settings(password: null));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(stored, (byte[])(await db.ScalarAsync("SELECT password_enc FROM email_settings WHERE id = 1"))!);
        Assert.True((long)(await db.ScalarAsync("SELECT count(*) FROM security_events WHERE event = 'email_settings_changed'"))! >= 2);
    }

    [SkippableFact]
    public async Task Invalid_settings_are_rejected_with_field_errors()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        await db.ScalarAsync("DELETE FROM email_settings RETURNING 1");
        var client = await Platform(new RecordingSmtp());

        var response = await client.PutAsJsonAsync("/platform/email-settings", new { host = "bad host", port = 0, security = "plain", fromAddress = "x", password = "" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var details = (await Json(response)).GetProperty("error").GetProperty("details");
        Assert.Equal("invalid", details.GetProperty("host").GetString());
        Assert.Equal("invalid", details.GetProperty("port").GetString());
        Assert.Equal("invalid", details.GetProperty("security").GetString());
        Assert.Equal("invalid", details.GetProperty("fromAddress").GetString());
        Assert.Equal("required", details.GetProperty("password").GetString());
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM email_settings"));
    }

    [SkippableFact]
    public async Task The_test_button_uses_the_saved_settings_and_reports_why_it_failed()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        await db.ScalarAsync("DELETE FROM email_settings RETURNING 1");
        var smtp = new RecordingSmtp();
        var client = await Platform(smtp);

        var notYet = await client.PostAsJsonAsync("/platform/email-settings/test", new { to = "yo@example.test" });
        Assert.Equal(HttpStatusCode.Conflict, notYet.StatusCode);
        Assert.Equal("email_not_configured", (await Json(notYet)).GetProperty("error").GetProperty("code").GetString());

        await client.PutAsJsonAsync("/platform/email-settings", Settings());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/platform/email-settings/test", new { to = "yo@example.test" })).StatusCode);
        var (used, message) = Assert.Single(smtp.Sent);
        Assert.Equal("clave-del-buzon", used.Password); // decrypted from the database, only in memory
        Assert.Equal("yo@example.test", message.To);

        smtp.FailWithReason = "auth_failed";
        var failed = await client.PostAsJsonAsync("/platform/email-settings/test", new { to = "yo@example.test" });
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Equal("auth_failed", (await Json(failed)).GetProperty("error").GetProperty("reason").GetString());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/platform/email-settings/test", new { to = "nope" })).StatusCode);
    }

    [SkippableFact]
    public async Task Only_platform_administrators_touch_the_mail_settings_and_the_api_role_cannot_read_the_table()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (anonymous, _) = Boot(new RecordingSmtp());

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/platform/email-settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/platform/email-settings", Settings())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/platform/email-settings/test", new { to = "yo@example.test" })).StatusCode);

        await using var connection = new NpgsqlConnection(db.AppConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM email_settings", connection);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
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

    [SkippableFact]
    public async Task The_welcome_mail_goes_through_the_database_settings_in_the_name_of_the_gym_and_its_page_shows_the_gym()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        await db.ScalarAsync("DELETE FROM email_settings RETURNING 1");
        var smtp = new RecordingSmtp();
        var client = await Platform(smtp);
        await client.PutAsJsonAsync("/platform/email-settings", Settings());
        var slug = "g" + Guid.NewGuid().ToString("N")[..10];

        var created = await client.PostAsJsonAsync("/platform/tenants", new
        {
            slug,
            name = "Gimnasio Titan Norte",
            ruc = NewRuc(),
            legalName = "Titan S.A.",
            headOfficeAddress = "Av. Amazonas N24",
            regime = "general",
            accountingRequired = false,
            providerCompanyId = "12",
            providerApiKey = "sk-live-ABCDEFGH1234",
            ownerUsername = "dueno.titan",
            ownerDisplayName = "Dueño Titan",
            ownerEmail = "dueno@example.test",
            environment = "test",
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True((await Json(created)).GetProperty("welcomeEmailSent").GetBoolean());
        var (settings, message) = Assert.Single(smtp.Sent);
        Assert.Equal("smtp.hostinger.com", settings.Host);
        Assert.Equal("no-reply@support-me.net", settings.FromAddress);
        Assert.Equal("Gimnasio Titan Norte", message.FromName); // the gym, not "FitOS"
        Assert.Equal("Bienvenido a Gimnasio Titan Norte: crea tu contraseña", message.Subject);
        Assert.DoesNotContain("FitOS", message.Body.Split("--\n")[0]); // only the footer names the platform

        // The page behind the link names the gym and does not spend the link.
        var token = message.Body.Split("#setup=")[1].Split('\n')[0].Trim();
        for (var i = 0; i < 2; i++)
        {
            var info = await Json(await client.PostAsJsonAsync("/auth/setup-info", new { token }));
            Assert.Equal("Gimnasio Titan Norte", info.GetProperty("gymName").GetString());
            Assert.Equal("Dueño Titan", info.GetProperty("displayName").GetString());
        }

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/auth/set-password", new { token, password = Password })).StatusCode);
        var spent = await client.PostAsJsonAsync("/auth/setup-info", new { token });
        Assert.Equal(HttpStatusCode.BadRequest, spent.StatusCode);
        Assert.Equal("invalid_link", (await Json(spent)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/setup-info", new { token = "no-such-token" })).StatusCode);
    }

    [SkippableFact]
    public async Task A_platform_login_whose_confirmation_mail_cannot_be_sent_says_so_instead_of_failing()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        await db.ScalarAsync("DELETE FROM email_settings RETURNING 1");
        var smtp = new RecordingSmtp { FailWithReason = "connection_failed" };
        var (client, _) = Boot(smtp, skipConfirmation: false);
        var username = "root-" + Guid.NewGuid().ToString("N")[..8];
        await db.ScalarAsync(
            "INSERT INTO users (tenant_id, username, password_hash, display_name, role, email) VALUES (NULL, @u, @h, 'Root', 'platform_admin', 'root@example.test') RETURNING 1",
            ("u", username), ("h", Hasher.Hash(Password)));

        // Save settings directly so the sender tries (and fails on) them; there is no environment fallback in this test.
        await using (var connection = new NpgsqlConnection(db.OwnerConnection))
        {
            await connection.OpenAsync();
            await using var insert = new NpgsqlCommand(
                "INSERT INTO email_settings (id, host, port, security, username, from_address, password_enc) VALUES (1, 'smtp.invalid', 465, 'ssl_on_connect', 'u', 'no-reply@support-me.net', @enc)",
                connection);
            using var scope = _factories[^1].Services.CreateScope();
            insert.Parameters.AddWithValue("enc", scope.ServiceProvider.GetRequiredService<FitOS.Application.Secrets.ISecretProtector>().Protect("x", Guid.Empty));
            await insert.ExecuteNonQueryAsync();
        }

        var response = await Login(client, "platform", username);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("email_unavailable", (await Json(response)).GetProperty("error").GetProperty("code").GetString());
    }
}
