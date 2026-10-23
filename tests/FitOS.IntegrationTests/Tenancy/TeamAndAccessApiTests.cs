using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FitOS.Api.Tenancy;
using FitOS.Application.Email;
using FitOS.Infrastructure.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FitOS.IntegrationTests.Tenancy;

/// <summary>Staff, customer logins and "forgot my password": real pipeline, real PostgreSQL, real row-level security.</summary>
public sealed class TeamAndAccessApiTests(PostgresFixture db) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Password = "Correct-Horse-9!";

    private const string NewPassword = "Brand-New-Pass-77";

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

    private sealed class Mailbox : ISmtpDelivery
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(SmtpSettings settings, EmailMessage message, CancellationToken cancellationToken)
        {
            lock (Sent)
            {
                Sent.Add(message);
            }

            return Task.CompletedTask;
        }

        public async Task<EmailMessage> NextAsync(string to)
        {
            for (var i = 0; i < 100; i++)
            {
                lock (Sent)
                {
                    var found = Sent.LastOrDefault(m => m.To.Equals(to, StringComparison.OrdinalIgnoreCase));
                    if (found is not null)
                    {
                        return found;
                    }
                }

                await Task.Delay(50);
            }

            throw new TimeoutException($"No e-mail arrived for {to}.");
        }

        public int CountFor(string to)
        {
            lock (Sent)
            {
                return Sent.Count(m => m.To.Equals(to, StringComparison.OrdinalIgnoreCase));
            }
        }

        public static string TokenIn(EmailMessage message) => message.Body.Split("#setup=")[1].Split('\n')[0].Trim();
    }

    private HttpClient Client(Mailbox mailbox, int resetGapSeconds = 0)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseTestSecrets();
            host.UseSetting("ConnectionStrings:Default", db.AppConnection);
            host.UseSetting("Auth:LoginRequestsPerMinute", "1000");
            host.UseSetting("Email:Host", "smtp.test"); // any host: the fake delivery below catches every message
            host.UseSetting("Email:From", "no-reply@test.invalid");
            host.UseSetting("Auth:ResetMinGapSeconds", resetGapSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            host.ConfigureTestServices(services => services.AddSingleton<ISmtpDelivery>(mailbox));
        });
        _factories.Add(factory);
        return factory.CreateClient();
    }

    private async Task<(Guid Id, string Slug)> NewGym(string name = "Gimnasio Titan", string status = "active")
    {
        var id = Guid.NewGuid();
        var slug = "t" + id.ToString("N")[..12];
        await db.ScalarAsync("INSERT INTO tenants (id, name, slug, status) VALUES (@id, @name, @slug, @status) RETURNING 1", ("id", id), ("name", name), ("slug", slug), ("status", status));
        await db.ScalarAsync("INSERT INTO tenant_settings (tenant_id) VALUES (@id) RETURNING 1", ("id", id));
        return (id, slug);
    }

    private async Task<Guid> NewUser(Guid tenant, string username, string role, string? email = null, bool active = true, Guid? customerId = null)
    {
        var id = Guid.NewGuid();
        await db.ScalarAsync(
            "INSERT INTO users (id, tenant_id, username, password_hash, display_name, role, email, is_active, customer_id) VALUES (@id, @t, @u, @h, @n, @r, @e, @a, @c) RETURNING 1",
            ("id", id), ("t", tenant), ("u", username), ("h", Hasher.Hash(Password)), ("n", "Nombre " + username), ("r", role), ("e", email), ("a", active), ("c", customerId));
        return id;
    }

    private async Task<Guid> NewCustomer(Guid tenant, string name = "Ana Socia", string? email = "ana@example.test", string status = "active")
    {
        var id = Guid.NewGuid();
        await db.ScalarAsync(
            "INSERT INTO customers (id, tenant_id, client_code, name, email, status) VALUES (@id, @t, (SELECT coalesce(max(client_code), 0) + 1 FROM customers WHERE tenant_id = @t), @n, @e, @s) RETURNING 1",
            ("id", id), ("t", tenant), ("n", name), ("e", email), ("s", status));
        return id;
    }

    private static async Task<HttpResponseMessage> Login(HttpClient client, string slug, string who, string password = Password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login") { Content = JsonContent.Create(new { username = who, password }) };
        request.Headers.Add(TenantHostResolver.TenantHeader, slug);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private async Task<HttpClient> SignedIn(Mailbox mailbox, string slug, string who)
    {
        var client = Client(mailbox);
        var token = (await Json(await Login(client, slug, who))).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ---- login by e-mail or username -----------------------------------------------------------------------------

    [SkippableFact]
    public async Task Login_works_with_the_email_or_the_username_in_any_case_and_only_inside_that_gym()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewGym();
        var (otherTenant, otherSlug) = await NewGym();
        await NewUser(tenant, "carla", "reception", "Carla@Example.test");
        await NewUser(otherTenant, "pedro", "reception", "pedro@example.test");
        var client = Client(new Mailbox());

        Assert.Equal(HttpStatusCode.OK, (await Login(client, slug, "carla")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(client, slug, "CARLA")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(client, slug, "carla@example.test")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(client, slug, " Carla@Example.TEST ".Trim())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, otherSlug, "carla@example.test")).StatusCode); // another gym
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, slug, "pedro@example.test")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, slug, "carla@example.test", "Wrong-Password-1!")).StatusCode);
    }

    // ---- staff -----------------------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task The_owner_invites_staff_who_sets_a_password_from_the_mail_and_logs_in_with_the_email()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewGym("Gimnasio Titan Norte");
        await NewUser(tenant, "dueno", "owner", "dueno@example.test");
        var mailbox = new Mailbox();
        var owner = await SignedIn(mailbox, slug, "dueno");

        var created = await owner.PostAsJsonAsync("/users", new { name = "Carla Pérez", email = "carla@example.test", role = "reception" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True((await Json(created)).GetProperty("emailSent").GetBoolean());
        var mail = await mailbox.NextAsync("carla@example.test");
        Assert.Equal("Gimnasio Titan Norte", mail.FromName);
        Assert.Equal("Bienvenido a Gimnasio Titan Norte: crea tu contraseña", mail.Subject);
        var token = Mailbox.TokenIn(mail);

        var info = await Json(await owner.PostAsJsonAsync("/auth/setup-info", new { token }));
        Assert.Equal("Gimnasio Titan Norte", info.GetProperty("gymName").GetString());
        Assert.Equal("welcome", info.GetProperty("purpose").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(owner, slug, "carla@example.test", "Whatever-123456")).StatusCode); // no password yet
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync("/auth/set-password", new { token, password = NewPassword })).StatusCode);
        var login = await Json(await Login(owner, slug, "carla@example.test", NewPassword));
        Assert.Equal("reception", login.GetProperty("user").GetProperty("role").GetString());
        Assert.False(login.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());

        var list = (await Json(await owner.GetAsync("/users"))).EnumerateArray().ToList();
        Assert.Contains(list, m => m.GetProperty("email").GetString() == "carla@example.test" && m.GetProperty("passwordSet").GetBoolean());
    }

    [SkippableFact]
    public async Task Who_may_call_what_and_a_repeated_email_is_a_conflict()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewGym();
        await NewUser(tenant, "dueno", "owner", "dueno@example.test");
        await NewUser(tenant, "admin1", "admin", "admin1@example.test");
        await NewUser(tenant, "recep", "reception", "recep@example.test");
        var mailbox = new Mailbox();
        var owner = await SignedIn(mailbox, slug, "dueno");
        var admin = await SignedIn(mailbox, slug, "admin1");
        var reception = await SignedIn(mailbox, slug, "recep");
        var anonymous = Client(mailbox);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync("/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.PostAsJsonAsync("/users", new { name = "X", email = "x@example.test", role = "reception" })).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/users", new { name = "Recep Dos", email = "dos@example.test", role = "reception" })).StatusCode);
        var adminMakesAdmin = await admin.PostAsJsonAsync("/users", new { name = "Admin Dos", email = "admin2@example.test", role = "admin" });
        Assert.Equal(HttpStatusCode.Forbidden, adminMakesAdmin.StatusCode);
        Assert.Equal("role_not_allowed", (await Json(adminMakesAdmin)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/users", new { name = "Admin Tres", email = "admin3@example.test", role = "admin" })).StatusCode);

        var duplicate = await owner.PostAsJsonAsync("/users", new { name = "Otra", email = "DOS@example.test", role = "reception" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("email_taken", (await Json(duplicate)).GetProperty("error").GetProperty("code").GetString());

        var invalid = await owner.PostAsJsonAsync("/users", new { name = "", email = "no", role = "dios" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var details = (await Json(invalid)).GetProperty("error").GetProperty("details");
        Assert.Equal("required", details.GetProperty("name").GetString());
        Assert.Equal("invalid_format", details.GetProperty("email").GetString());
        Assert.Equal("invalid", details.GetProperty("role").GetString());
    }

    [SkippableFact]
    public async Task Deactivating_staff_stops_their_login_and_their_renewals_and_the_last_owner_is_protected()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewGym();
        var ownerId = await NewUser(tenant, "dueno", "owner", "dueno@example.test");
        var receptionId = await NewUser(tenant, "recep", "reception", "recep@example.test");
        var mailbox = new Mailbox();
        var owner = await SignedIn(mailbox, slug, "dueno");
        var refresh = (await Json(await Login(owner, slug, "recep"))).GetProperty("refreshToken").GetString();

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PatchAsJsonAsync($"/users/{receptionId}", new { isActive = false })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(owner, slug, "recep")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.PostAsJsonAsync("/auth/refresh", new { refreshToken = refresh })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PatchAsJsonAsync($"/users/{receptionId}", new { isActive = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(owner, slug, "recep")).StatusCode);

        var self = await owner.PatchAsJsonAsync($"/users/{ownerId}", new { isActive = false });
        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
        Assert.Equal("cannot_deactivate_self", (await Json(self)).GetProperty("error").GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PatchAsJsonAsync($"/users/{receptionId}", new { role = "admin" })).StatusCode);
        Assert.Equal("admin", await db.ScalarAsync("SELECT role FROM users WHERE id = @id", ("id", receptionId)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PatchAsJsonAsync($"/users/{receptionId}", new { })).StatusCode);
    }

    [SkippableFact]
    public async Task One_gym_never_sees_or_touches_the_staff_of_another()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenantA, slugA) = await NewGym();
        var (tenantB, _) = await NewGym();
        await NewUser(tenantA, "dueno", "owner", "dueno@example.test");
        var foreign = await NewUser(tenantB, "recep", "reception", "recep@example.test");
        var owner = await SignedIn(new Mailbox(), slugA, "dueno");

        var list = (await Json(await owner.GetAsync("/users"))).EnumerateArray().ToList();

        Assert.DoesNotContain(list, m => m.GetProperty("id").GetGuid() == foreign);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PatchAsJsonAsync($"/users/{foreign}", new { isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/users/{foreign}/resend-invite", null)).StatusCode);
        Assert.Equal(true, await db.ScalarAsync("SELECT is_active FROM users WHERE id = @id", ("id", foreign)));
    }

    [SkippableFact]
    public async Task A_pending_invitation_can_be_sent_again_and_the_old_link_stops_working()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewGym();
        await NewUser(tenant, "dueno", "owner", "dueno@example.test");
        var mailbox = new Mailbox();
        var owner = await SignedIn(mailbox, slug, "dueno");
        var id = (await Json(await owner.PostAsJsonAsync("/users", new { name = "Carla", email = "carla@example.test", role = "reception" }))).GetProperty("id").GetGuid();
        var first = Mailbox.TokenIn(await mailbox.NextAsync("carla@example.test"));

        var resent = await owner.PostAsync($"/users/{id}/resend-invite", null);
        Assert.Equal(HttpStatusCode.OK, resent.StatusCode);
        var second = Mailbox.TokenIn(mailbox.Sent.Last(m => m.To == "carla@example.test"));

        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/auth/set-password", new { token = first, password = NewPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync("/auth/set-password", new { token = second, password = NewPassword })).StatusCode);
        var again = await owner.PostAsync($"/users/{id}/resend-invite", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("already_active", (await Json(again)).GetProperty("error").GetProperty("code").GetString());
    }

    // ---- customers' access -----------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Reception_gives_a_customer_access_and_the_customer_logs_in_with_their_email_seeing_only_their_own()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewGym("Gimnasio Titan Norte");
        await NewUser(tenant, "recep", "reception", "recep@example.test");
        var customer = await NewCustomer(tenant, "Ana Socia", "ana@example.test");
        var other = await NewCustomer(tenant, "Otro Socio", "otro@example.test");
        var mailbox = new Mailbox();
        var reception = await SignedIn(mailbox, slug, "recep");

        Assert.False((await Json(await reception.GetAsync($"/customers/{customer}/access"))).GetProperty("hasAccess").GetBoolean());
        var granted = await reception.PostAsJsonAsync($"/customers/{customer}/access", new { });

        Assert.Equal(HttpStatusCode.Created, granted.StatusCode); // the customer's own e-mail is the default
        var mail = await mailbox.NextAsync("ana@example.test");
        Assert.Equal("Gimnasio Titan Norte", mail.FromName);
        var status = await Json(await reception.GetAsync($"/customers/{customer}/access"));
        Assert.True(status.GetProperty("hasAccess").GetBoolean());
        Assert.False(status.GetProperty("passwordSet").GetBoolean());

        var again = await reception.PostAsJsonAsync($"/customers/{customer}/access", new { });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("access_already_granted", (await Json(again)).GetProperty("error").GetProperty("code").GetString());

        var token = Mailbox.TokenIn(mail);
        Assert.Equal(HttpStatusCode.NoContent, (await reception.PostAsJsonAsync("/auth/set-password", new { token, password = NewPassword })).StatusCode);
        var login = await Json(await Login(reception, slug, "ana@example.test", NewPassword));
        Assert.Equal("customer", login.GetProperty("user").GetProperty("role").GetString());
        Assert.Equal(customer, login.GetProperty("user").GetProperty("customerId").GetGuid());

        using var asCustomer = Client(mailbox);
        asCustomer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await asCustomer.GetAsync($"/customers/{customer}/measurements")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await asCustomer.GetAsync($"/customers/{other}/measurements")).StatusCode); // somebody else's
        Assert.Equal(HttpStatusCode.Forbidden, (await asCustomer.PostAsJsonAsync($"/customers/{other}/access", new { email = "x@example.test" })).StatusCode);
    }

    [SkippableFact]
    public async Task Access_needs_an_existing_active_customer_of_this_gym_with_a_valid_email()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewGym();
        var (otherTenant, _) = await NewGym();
        await NewUser(tenant, "recep", "reception", "recep@example.test");
        var noEmail = await NewCustomer(tenant, "Sin Correo", null);
        var inactive = await NewCustomer(tenant, "Inactivo", "inactivo@example.test", "inactive");
        var foreign = await NewCustomer(otherTenant, "Ajeno", "ajeno@example.test");
        var reception = await SignedIn(new Mailbox(), slug, "recep");

        Assert.Equal(HttpStatusCode.NotFound, (await reception.PostAsJsonAsync($"/customers/{foreign}/access", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reception.GetAsync($"/customers/{foreign}/access")).StatusCode);
        var inactiveResult = await reception.PostAsJsonAsync($"/customers/{inactive}/access", new { });
        Assert.Equal("customer_inactive", (await Json(inactiveResult)).GetProperty("error").GetProperty("code").GetString());

        var missing = await reception.PostAsJsonAsync($"/customers/{noEmail}/access", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await reception.PostAsJsonAsync($"/customers/{noEmail}/access", new { email = "no-es-correo" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await reception.PostAsJsonAsync($"/customers/{noEmail}/access", new { email = "nuevo@example.test" })).StatusCode);
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM users WHERE customer_id = ANY(@ids)", ("ids", new[] { foreign, inactive })));
    }

    // ---- forgot my password ----------------------------------------------------------------------------------------

    private static async Task<HttpResponseMessage> Forgot(HttpClient client, string slug, string identifier)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/forgot-password") { Content = JsonContent.Create(new { identifier }) };
        request.Headers.Add(TenantHostResolver.TenantHeader, slug);
        return await client.SendAsync(request);
    }

    [SkippableFact]
    public async Task Forgot_password_sends_a_link_in_the_gyms_name_and_the_new_password_replaces_the_old_everywhere()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewGym("Gimnasio Titan Norte");
        await NewUser(tenant, "carla", "reception", "carla@example.test");
        var mailbox = new Mailbox();
        var client = Client(mailbox);
        var refresh = (await Json(await Login(client, slug, "carla"))).GetProperty("refreshToken").GetString();

        var asked = await Forgot(client, slug, "Carla@Example.test");

        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        var mail = await mailbox.NextAsync("carla@example.test");
        Assert.Equal("Gimnasio Titan Norte", mail.FromName);
        Assert.Equal("Restablece tu contraseña en Gimnasio Titan Norte", mail.Subject);
        var token = Mailbox.TokenIn(mail);
        Assert.Equal("reset", (await Json(await client.PostAsJsonAsync("/auth/setup-info", new { token }))).GetProperty("purpose").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/auth/set-password", new { token, password = NewPassword })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, slug, "carla")).StatusCode); // the old one is gone
        Assert.Equal(HttpStatusCode.OK, (await Login(client, slug, "carla@example.test", NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = refresh })).StatusCode); // old sessions closed
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/set-password", new { token, password = "Another-Pass-9!" })).StatusCode); // once only
    }

    [SkippableFact]
    public async Task Forgot_password_answers_the_same_for_everything_and_sends_nothing_when_there_is_nothing_to_send()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewGym();
        var (suspended, suspendedSlug) = await NewGym(status: "suspended");
        await NewUser(tenant, "inactivo", "reception", "inactivo@example.test", active: false);
        await NewUser(tenant, "sincorreo", "reception", null);
        await NewUser(suspended, "carla", "reception", "carla@example.test");
        var mailbox = new Mailbox();
        var client = Client(mailbox);

        foreach (var (gym, who) in new[] { (slug, "nadie@example.test"), (slug, "inactivo@example.test"), (slug, "sincorreo"), ("no-such-gym", "carla@example.test"), (suspendedSlug, "carla@example.test"), ("platform", "root") })
        {
            var response = await Forgot(client, gym, who);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal("accepted", (await Json(response)).GetProperty("status").GetString());
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await Forgot(client, slug, "")).StatusCode);
        await Task.Delay(300);
        Assert.Empty(mailbox.Sent);
    }

    [SkippableFact]
    public async Task Asking_twice_in_a_row_sends_only_one_mail()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewGym();
        await NewUser(tenant, "carla", "reception", "carla@example.test");
        var mailbox = new Mailbox();
        var client = Client(mailbox, resetGapSeconds: 3600);

        await Forgot(client, slug, "carla@example.test");
        await mailbox.NextAsync("carla@example.test");
        await Forgot(client, slug, "carla");
        await Task.Delay(400);

        Assert.Equal(1, mailbox.CountFor("carla@example.test"));
    }

    [SkippableFact]
    public async Task The_database_keeps_one_email_per_gym_but_the_same_email_may_exist_in_two_gyms()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenantA, _) = await NewGym();
        var (tenantB, _) = await NewGym();
        await NewUser(tenantA, "uno", "reception", "mismo@example.test");

        await NewUser(tenantB, "uno", "reception", "mismo@example.test"); // another gym: fine
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => NewUser(tenantA, "dos", "reception", "MISMO@example.test"));

        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }
}
