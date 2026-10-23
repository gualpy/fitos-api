using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FitOS.Api.Tenancy;
using FitOS.Infrastructure.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace FitOS.IntegrationTests.Tenancy;

/// <summary>Subscriptions end to end: automatic expiry, manual suspension, renewals, revenue and the reminder.</summary>
public sealed class SubscriptionApiTests(PostgresFixture db) : IClassFixture<PostgresFixture>, IAsyncLifetime
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

    private HttpClient Client()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseTestSecrets();
            host.UseSetting("ConnectionStrings:Default", db.AppConnection);
            host.UseSetting("Auth:LoginRequestsPerMinute", "1000");
        });
        _factories.Add(factory);
        return factory.CreateClient();
    }

    private async Task<(Guid Id, string Slug)> NewTenant(int daysLeft, string status = "active")
    {
        var id = Guid.NewGuid();
        var slug = "s" + id.ToString("N")[..12];
        await db.ScalarAsync(
            "INSERT INTO tenants (id, name, slug, status, subscription_ends_on) VALUES (@id, 'Gym ' || @slug, @slug, @status, current_date + @days) RETURNING 1",
            ("id", id), ("slug", slug), ("status", status), ("days", daysLeft));
        await db.ScalarAsync("INSERT INTO tenant_settings (tenant_id, timezone) VALUES (@id, 'UTC') RETURNING 1", ("id", id));
        return (id, slug);
    }

    private async Task NewUser(Guid? tenantId, string username, string role)
    {
        await db.ScalarAsync(
            "INSERT INTO users (tenant_id, username, password_hash, display_name, role) VALUES (@tenant, @user, @hash, 'Name', @role) RETURNING 1",
            ("tenant", tenantId), ("user", username), ("hash", Hasher.Hash(Password)), ("role", role));
    }

    private static async Task<HttpResponseMessage> Login(HttpClient client, string slug, string username)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login") { Content = JsonContent.Create(new { username, password = Password }) };
        request.Headers.Add(TenantHostResolver.TenantHeader, slug);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private async Task<HttpClient> SignedIn(string slug, string username)
    {
        var client = Client();
        var token = (await Json(await Login(client, slug, username))).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<HttpClient> Platform()
    {
        var username = "root-" + Guid.NewGuid().ToString("N")[..8];
        await NewUser(null, username, "platform_admin");
        return await SignedIn("platform", username);
    }

    [SkippableFact]
    public async Task A_gym_stops_getting_in_the_day_after_its_last_day_and_returns_when_renewed()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (lastDay, lastDaySlug) = await NewTenant(daysLeft: 0);
        var (expired, expiredSlug) = await NewTenant(daysLeft: -1);
        await NewUser(lastDay, "dueno", "owner");
        await NewUser(expired, "dueno", "owner");
        var client = Client();

        Assert.Equal(HttpStatusCode.OK, (await Login(client, lastDaySlug, "dueno")).StatusCode); // the last day still counts
        var blocked = await Login(client, expiredSlug, "dueno");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("tenant_inactive", (await Json(blocked)).GetProperty("error").GetProperty("code").GetString());

        var platform = await Platform();
        var renewed = await platform.PostAsJsonAsync($"/platform/tenants/{expired}/subscription", new { months = 1, amount = 20 });
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(client, expiredSlug, "dueno")).StatusCode); // no job needed: it just works again
    }

    [SkippableFact]
    public async Task Extending_adds_months_from_the_end_date_or_from_today_when_already_expired_and_records_the_payment()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (running, _) = await NewTenant(daysLeft: 10, status: "trial");
        var (lapsed, _) = await NewTenant(daysLeft: -40);
        var platform = await Platform();

        var first = await Json(await platform.PostAsJsonAsync($"/platform/tenants/{running}/subscription", new { months = 2, amount = 40 }));
        var second = await Json(await platform.PostAsJsonAsync($"/platform/tenants/{lapsed}/subscription", new { months = 1, amount = 20 }));

        var today = DateTime.UtcNow.Date;
        Assert.Equal(today.AddDays(10).AddMonths(2).ToString("yyyy-MM-dd"), first.GetProperty("subscriptionEndsOn").GetString());
        Assert.Equal(today.AddMonths(1).ToString("yyyy-MM-dd"), second.GetProperty("subscriptionEndsOn").GetString());
        Assert.Equal("active", await db.ScalarAsync("SELECT status FROM tenants WHERE id = @id", ("id", running))); // paying ends the trial
        Assert.Equal(40m, await db.ScalarAsync("SELECT amount FROM subscription_payments WHERE tenant_id = @id", ("id", running)));
        Assert.Equal(1L, await db.ScalarAsync("SELECT count(*) FROM audit_log WHERE tenant_id = @id AND action = 'subscription.extended'", ("id", running)));
    }

    [SkippableFact]
    public async Task The_monthly_revenue_adds_up_what_the_gyms_paid()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        await db.ScalarAsync("DELETE FROM subscription_payments RETURNING 1");
        var platform = await Platform();
        for (var i = 0; i < 3; i++)
        {
            var (id, _) = await NewTenant(daysLeft: 5);
            Assert.Equal(HttpStatusCode.OK, (await platform.PostAsJsonAsync($"/platform/tenants/{id}/subscription", new { months = 1, amount = 20 })).StatusCode);
        }

        var revenue = await Json(await platform.GetAsync("/platform/revenue"));

        var current = revenue.EnumerateArray().Single();
        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM"), current.GetProperty("month").GetString());
        Assert.Equal(60m, current.GetProperty("total").GetDecimal());
        Assert.Equal(3, current.GetProperty("payments").GetInt32());
    }

    [SkippableFact]
    public async Task Suspending_blocks_login_and_refresh_and_reactivating_restores_them()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (id, slug) = await NewTenant(daysLeft: 20);
        await NewUser(id, "dueno", "owner");
        var client = Client();
        var session = await Json(await Login(client, slug, "dueno"));
        var refresh = session.GetProperty("refreshToken").GetString();
        var platform = await Platform();

        Assert.Equal(HttpStatusCode.NoContent, (await platform.PostAsync($"/platform/tenants/{id}/suspend", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await Login(client, slug, "dueno")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = refresh })).StatusCode);
        var list = (await Json(await platform.GetAsync("/platform/tenants"))).EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == id);
        Assert.Equal("suspended", list.GetProperty("effectiveStatus").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await platform.PostAsync($"/platform/tenants/{id}/reactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(client, slug, "dueno")).StatusCode);
    }

    [SkippableFact]
    public async Task The_list_shows_each_gym_with_its_days_left_and_effective_status()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (soon, _) = await NewTenant(daysLeft: 2);
        var (late, _) = await NewTenant(daysLeft: -3);
        var platform = await Platform();

        var rows = (await Json(await platform.GetAsync("/platform/tenants"))).EnumerateArray().ToList();

        var soonRow = rows.Single(t => t.GetProperty("id").GetGuid() == soon);
        var lateRow = rows.Single(t => t.GetProperty("id").GetGuid() == late);
        Assert.Equal(2, soonRow.GetProperty("daysLeft").GetInt32());
        Assert.Equal("active", soonRow.GetProperty("effectiveStatus").GetString());
        Assert.Equal(-3, lateRow.GetProperty("daysLeft").GetInt32());
        Assert.Equal("expired", lateRow.GetProperty("effectiveStatus").GetString());
        Assert.Equal("active", lateRow.GetProperty("status").GetString()); // stored status untouched: expiry is computed
    }

    [SkippableFact]
    public async Task Platform_endpoints_refuse_gym_staff_and_anonymous_callers_and_validate_input()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (id, slug) = await NewTenant(daysLeft: 20);
        await NewUser(id, "dueno", "owner");
        var owner = await SignedIn(slug, "dueno");
        var anonymous = Client();

        foreach (var client in new[] { owner, anonymous })
        {
            var denied = client == owner ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
            Assert.Equal(denied, (await client.GetAsync("/platform/tenants")).StatusCode);
            Assert.Equal(denied, (await client.GetAsync("/platform/revenue")).StatusCode);
            Assert.Equal(denied, (await client.PostAsJsonAsync($"/platform/tenants/{id}/subscription", new { months = 1, amount = 20 })).StatusCode);
            Assert.Equal(denied, (await client.PostAsync($"/platform/tenants/{id}/suspend", null)).StatusCode);
        }

        var platform = await Platform();
        var invalid = await platform.PostAsJsonAsync($"/platform/tenants/{id}/subscription", new { months = 0, amount = -5 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.PostAsJsonAsync($"/platform/tenants/{Guid.NewGuid()}/subscription", new { months = 1, amount = 20 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.PostAsync($"/platform/tenants/{Guid.NewGuid()}/suspend", null)).StatusCode);
    }

    [SkippableTheory]
    [InlineData(10, false)]
    [InlineData(4, false)]
    [InlineData(3, true)]
    [InlineData(0, true)]
    public async Task The_owner_is_reminded_from_three_days_before_the_end(int daysLeft, bool reminder)
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (id, slug) = await NewTenant(daysLeft);
        await NewUser(id, "dueno", "owner");
        var owner = await SignedIn(slug, "dueno");

        var status = await Json(await owner.GetAsync("/subscription"));

        Assert.Equal(daysLeft, status.GetProperty("daysLeft").GetInt32());
        Assert.Equal(reminder, status.GetProperty("showReminder").GetBoolean());
        Assert.Equal(DateTime.UtcNow.Date.AddDays(daysLeft).ToString("yyyy-MM-dd"), status.GetProperty("endsOn").GetString());
    }

    [SkippableFact]
    public async Task A_gym_cannot_read_the_payments_table_or_call_around_the_platform_checks_at_row_level()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        await using var connection = new NpgsqlConnection(db.AppConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM subscription_payments", connection);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [SkippableFact]
    public async Task Garbage_links_and_missing_fields_are_rejected_without_revealing_anything()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var client = Client();

        var unknown = await client.PostAsJsonAsync("/auth/set-password", new { token = "no-such-token", password = Password });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("invalid_link", (await Json(unknown)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/set-password", new { token = "", password = Password })).StatusCode);
    }
}
