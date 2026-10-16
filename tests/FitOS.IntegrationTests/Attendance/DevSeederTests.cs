using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FitOS.Api.Tenancy;
using FitOS.Infrastructure.Auth;
using FitOS.IntegrationTests.Tenancy;
using FitOS.Migrator;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FitOS.IntegrationTests.Attendance;

/// <summary>The development seed writes real rows (user, membership, attendance) and the API serves them.</summary>
public sealed class DevSeederTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private const string SeedPassword = "Seeded-Pass-1";

    private static readonly IdentityPasswordService Hasher = new();

    private static readonly TimeZoneInfo Ecuador = TimeZoneInfo.FindSystemTimeZoneById("America/Guayaquil");

    private static DateOnly TodayEc() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Ecuador).DateTime);

    private async Task<(Guid TenantId, string Slug)> NewGym()
    {
        var id = Guid.NewGuid();
        var slug = "s" + id.ToString("N")[..10];
        await db.ScalarAsync("INSERT INTO tenants (id, name, slug) VALUES (@id, 'Gym', @slug) RETURNING 1", ("id", id), ("slug", slug));
        await db.ScalarAsync("INSERT INTO tenant_settings (tenant_id) VALUES (@id) RETURNING 1", ("id", id));
        await db.ScalarAsync("INSERT INTO branches (tenant_id, name, address, sri_establishment) VALUES (@id, 'Matriz', 'Quito', '001') RETURNING 1", ("id", id));
        return (id, slug);
    }

    private Task<SeedResult> Seed(string slug, string username = "juan", int days = 20) =>
        DevSeeder.SeedAsync(db.OwnerConnection, new SeedOptions(slug, username, "Juan Pérez", SeedPassword, days), Hasher, TimeProvider.System);

    private async Task<Guid> CustomerOf(Guid tenantId, string username) =>
        (Guid)(await db.ScalarAsync("SELECT customer_id FROM users WHERE tenant_id = @t AND username = @u", ("t", tenantId), ("u", username)))!;

    [SkippableFact]
    public async Task Seeds_a_customer_with_a_login_continuous_memberships_and_attendance_on_every_open_day()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewGym();

        var result = await Seed(slug);

        Assert.True(result.CustomerCreated);
        Assert.True(result.MembershipsCreated >= 1);
        var customer = await CustomerOf(tenant, "juan");
        Assert.Equal(false, await db.ScalarAsync("SELECT must_change_password FROM users WHERE tenant_id = @t AND username = 'juan'", ("t", tenant)));
        Assert.Equal(7L, await db.ScalarAsync("SELECT count(*) FROM business_hours WHERE tenant_id = @t", ("t", tenant)));
        Assert.Equal(false, await db.ScalarAsync("SELECT is_open FROM business_hours WHERE tenant_id = @t AND weekday = 7", ("t", tenant))); // Sunday closed

        // Memberships: back to back, no gap, no overlap, covering the whole window up to today.
        var today = TodayEc();
        var memberships = (await db.RowsAsync("SELECT starts_on, ends_on FROM memberships WHERE customer_id = @c AND status = 'active' ORDER BY starts_on", ("c", customer)))
            .Select(r => (Start: DateOnly.FromDateTime((DateTime)r[0]!), End: DateOnly.FromDateTime((DateTime)r[1]!))).ToList();
        Assert.Equal(today.AddDays(-19), memberships[0].Start);
        Assert.True(memberships[^1].End >= today);
        for (var i = 1; i < memberships.Count; i++)
        {
            Assert.Equal(memberships[i - 1].End.AddDays(1), memberships[i].Start);
        }

        // Attendance: every day of the window except Sundays had exactly one visit (today only if it has started).
        var perDay = (await db.RowsAsync(
                "SELECT (check_in AT TIME ZONE 'America/Guayaquil')::date, count(*), bool_and(result = 'granted') FROM attendance WHERE customer_id = @c GROUP BY 1",
                ("c", customer)))
            .ToDictionary(r => DateOnly.FromDateTime((DateTime)r[0]!), r => ((long)r[1]!, (bool)r[2]!));
        for (var back = 1; back < 20; back++)
        {
            var day = today.AddDays(-back);
            if (day.DayOfWeek == DayOfWeek.Sunday)
            {
                Assert.DoesNotContain(day, perDay.Keys);
            }
            else
            {
                Assert.Equal((1L, true), perDay[day]);
            }
        }

        Assert.True(perDay.GetValueOrDefault(today).Item1 <= 1);
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM attendance WHERE customer_id = @c AND check_out IS NOT NULL AND check_out < check_in", ("c", customer)));
        Assert.True(await db.ScalarAsync("SELECT count(*) FROM attendance WHERE customer_id = @c AND check_out IS NULL", ("c", customer)) is 0L or 1L); // at most today's open visit
    }

    [SkippableFact]
    public async Task Running_the_seed_again_adds_nothing()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewGym();
        await Seed(slug);
        var customer = await CustomerOf(tenant, "juan");
        var before = (long)(await db.ScalarAsync("SELECT count(*) FROM attendance WHERE customer_id = @c", ("c", customer)))!;

        var again = await Seed(slug);

        Assert.False(again.CustomerCreated);
        Assert.Equal(0, again.MembershipsCreated);
        Assert.Equal(0, again.AttendanceCreated);
        Assert.Equal(before, await db.ScalarAsync("SELECT count(*) FROM attendance WHERE customer_id = @c", ("c", customer)));
        Assert.Equal(1L, await db.ScalarAsync("SELECT count(*) FROM customers WHERE tenant_id = @t", ("t", tenant)));
    }

    [SkippableFact]
    public async Task It_respects_hours_that_the_gym_already_configured()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewGym();
        var branch = await db.ScalarAsync("SELECT id FROM branches WHERE tenant_id = @t", ("t", tenant));
        // Only Wednesdays (ISO 3) are open, 10:00-12:00.
        await db.ScalarAsync(
            "INSERT INTO business_hours (tenant_id, branch_id, weekday, is_open, open_time, close_time) SELECT @t, @b, d, d = 3, '10:00', '12:00' FROM generate_series(1, 7) d RETURNING 1",
            ("t", tenant), ("b", branch));

        await Seed(slug, days: 30);

        var customer = await CustomerOf(tenant, "juan");
        var weekdays = (await db.RowsAsync("SELECT DISTINCT extract(isodow FROM check_in AT TIME ZONE 'America/Guayaquil')::int FROM attendance WHERE customer_id = @c", ("c", customer)))
            .Select(r => (int)r[0]!).ToList();
        Assert.Equal([3], weekdays);
        var hours = await db.RowsAsync("SELECT min((check_in AT TIME ZONE 'America/Guayaquil')::time), max((check_in AT TIME ZONE 'America/Guayaquil')::time) FROM attendance WHERE customer_id = @c", ("c", customer));
        Assert.True((TimeSpan)hours[0][0]! >= TimeSpan.FromHours(10));
    }

    [SkippableFact]
    public async Task The_seeded_user_logs_in_and_the_api_serves_exactly_their_attendance()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewGym();
        await Seed(slug, "juan");
        await Seed(slug, "maria");
        var juan = await CustomerOf(tenant, "juan");
        var maria = await CustomerOf(tenant, "maria");
        var expected = (long)(await db.ScalarAsync("SELECT count(*) FROM attendance WHERE customer_id = @c", ("c", juan)))!;

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseTestSecrets();
            host.UseSetting("ConnectionStrings:Default", db.AppConnection);
        });
        using var client = factory.CreateClient();
        using var login = new HttpRequestMessage(HttpMethod.Post, "/auth/login") { Content = JsonContent.Create(new { username = "juan", password = SeedPassword }) };
        login.Headers.Add(TenantHostResolver.TenantHeader, slug);
        var response = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(juan, body.GetProperty("user").GetProperty("customerId").GetGuid());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());

        var page = await client.GetFromJsonAsync<JsonElement>($"/customers/{juan}/attendance?pageSize=100");
        Assert.Equal(expected, page.GetProperty("total").GetInt64());
        Assert.All(page.GetProperty("items").EnumerateArray(), i => Assert.Equal(juan, i.GetProperty("customerId").GetGuid()));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/customers/{maria}/attendance")).StatusCode);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/customers/{juan}");
        Assert.Equal("current", detail.GetProperty("membership").GetProperty("state").GetString());
    }

    [SkippableFact]
    public async Task It_refuses_unknown_gyms_clashing_usernames_and_silly_windows()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (tenant, slug) = await NewGym();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Seed("no-such-gym"));
        await db.ScalarAsync(
            "INSERT INTO users (tenant_id, username, password_hash, display_name, role) VALUES (@t, 'owner', 'x', 'O', 'owner') RETURNING 1", ("t", tenant));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Seed(slug, "owner"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Seed(slug, days: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Seed(slug, days: 5000));
    }
}
