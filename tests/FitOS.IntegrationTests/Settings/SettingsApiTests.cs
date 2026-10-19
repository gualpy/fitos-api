using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FitOS.Api.Tenancy;
using FitOS.Infrastructure.Auth;
using FitOS.IntegrationTests.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace FitOS.IntegrationTests.Settings;

/// <summary>Gym settings end to end: roles, validation, persistence and isolation between gyms.</summary>
public sealed class SettingsApiTests(PostgresFixture db) : IClassFixture<PostgresFixture>, IAsyncLifetime
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

    private sealed record Gym(Guid TenantId, string Slug, Guid BranchId);

    private WebApplicationFactory<Program> App()
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

        return _app;
    }

    private async Task<Gym> NewGym(bool withSettings = true)
    {
        var id = Guid.NewGuid();
        var slug = "s" + id.ToString("N")[..10];
        var branch = Guid.NewGuid();
        await db.ScalarAsync("INSERT INTO tenants (id, name, slug) VALUES (@id, 'Gym', @slug) RETURNING 1", ("id", id), ("slug", slug));
        if (withSettings)
        {
            await db.ScalarAsync("INSERT INTO tenant_settings (tenant_id) VALUES (@id) RETURNING 1", ("id", id));
        }

        await db.ScalarAsync("INSERT INTO branches (id, tenant_id, name, address, sri_establishment) VALUES (@b, @id, 'Matriz', 'Quito', '001') RETURNING 1", ("b", branch), ("id", id));
        return new Gym(id, slug, branch);
    }

    private async Task SeedUser(Gym gym, string username, string role, Guid? customerId = null) =>
        await db.ScalarAsync(
            "INSERT INTO users (tenant_id, username, password_hash, display_name, role, customer_id) VALUES (@t, @u, @h, 'N', @r, @c) RETURNING 1",
            ("t", gym.TenantId), ("u", username), ("h", Hasher.Hash(Password)), ("r", role), ("c", customerId));

    private async Task<Guid> Customer(Gym gym, int code = 1)
    {
        var id = Guid.NewGuid();
        await db.ScalarAsync(
            "INSERT INTO customers (id, tenant_id, name, client_code) VALUES (@i, @t, 'Socio', @c) RETURNING 1", ("i", id), ("t", gym.TenantId), ("c", code));
        return id;
    }

    private async Task<HttpClient> Login(string slug, string username)
    {
        var client = App().CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login") { Content = JsonContent.Create(new { username, password = Password }) };
        request.Headers.Add(TenantHostResolver.TenantHeader, slug);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static object[] Week(Func<int, object> day) => Enumerable.Range(1, 7).Select(day).ToArray();

    private static object Day(int weekday, bool open, string from = "06:00", string to = "22:00") =>
        new { weekday, isOpen = open, openTime = from, closeTime = to };

    [Fact]
    public async Task A_new_gym_has_no_hours_configured_and_does_not_allow_entry_without_a_plan()
    {
        var gym = await NewGym();
        await SeedUser(gym, "boss", "owner");
        var client = await Login(gym.Slug, "boss");

        var body = await Json(await client.GetAsync("/settings"));

        Assert.False(body.GetProperty("allowWithoutPlan").GetBoolean());
        Assert.Equal("Matriz", body.GetProperty("branch").GetProperty("name").GetString());
        var hours = body.GetProperty("hours").EnumerateArray().ToList();
        Assert.Equal(7, hours.Count);
        Assert.All(hours, h => Assert.False(h.GetProperty("configured").GetBoolean()));
    }

    [Fact]
    public async Task An_owner_saves_the_switch_and_the_week_and_reads_it_back()
    {
        var gym = await NewGym();
        await SeedUser(gym, "boss", "owner");
        var client = await Login(gym.Slug, "boss");

        var saved = await client.PutAsJsonAsync("/settings", new
        {
            allowWithoutPlan = true,
            hours = Week(d => d == 7 ? Day(d, false, "00:00", "00:00") : d == 6 ? Day(d, true, "08:00", "18:00") : Day(d, true)),
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var body = await Json(await client.GetAsync("/settings"));
        Assert.True(body.GetProperty("allowWithoutPlan").GetBoolean());
        var hours = body.GetProperty("hours").EnumerateArray().ToList();
        Assert.All(hours, h => Assert.True(h.GetProperty("configured").GetBoolean()));
        Assert.Equal("08:00", hours[5].GetProperty("openTime").GetString());
        Assert.Equal("18:00", hours[5].GetProperty("closeTime").GetString());
        Assert.False(hours[6].GetProperty("isOpen").GetBoolean());
        Assert.Equal(7L, await db.ScalarAsync("SELECT count(*) FROM business_hours WHERE tenant_id = @t", ("t", gym.TenantId)));
    }

    [Fact]
    public async Task Saving_twice_updates_in_place()
    {
        var gym = await NewGym();
        await SeedUser(gym, "boss", "owner");
        var client = await Login(gym.Slug, "boss");

        await client.PutAsJsonAsync("/settings", new { allowWithoutPlan = true, hours = Week(d => Day(d, true)) });
        var second = await client.PutAsJsonAsync("/settings", new { allowWithoutPlan = false, hours = Week(d => Day(d, true, "07:00", "21:00")) });

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await Json(second);
        Assert.False(body.GetProperty("allowWithoutPlan").GetBoolean());
        Assert.Equal("07:00", body.GetProperty("hours")[0].GetProperty("openTime").GetString());
        Assert.Equal(7L, await db.ScalarAsync("SELECT count(*) FROM business_hours WHERE tenant_id = @t", ("t", gym.TenantId)));
    }

    [Fact]
    public async Task The_switch_can_be_saved_without_touching_the_hours()
    {
        var gym = await NewGym();
        await SeedUser(gym, "boss", "owner");
        var client = await Login(gym.Slug, "boss");
        await client.PutAsJsonAsync("/settings", new { allowWithoutPlan = false, hours = Week(d => Day(d, true)) });

        var response = await client.PutAsJsonAsync("/settings", new { allowWithoutPlan = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(7L, await db.ScalarAsync("SELECT count(*) FROM business_hours WHERE tenant_id = @t", ("t", gym.TenantId)));
    }

    [Fact]
    public async Task A_gym_without_a_settings_row_gets_one_on_the_first_save()
    {
        var gym = await NewGym(withSettings: false);
        await SeedUser(gym, "boss", "owner");
        var client = await Login(gym.Slug, "boss");

        var response = await client.PutAsJsonAsync("/settings", new { allowWithoutPlan = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(true, await db.ScalarAsync("SELECT allow_without_plan FROM tenant_settings WHERE tenant_id = @t", ("t", gym.TenantId)));
    }

    [Theory]
    [InlineData("missing_switch")]
    [InlineData("six_days")]
    [InlineData("repeated_day")]
    [InlineData("bad_weekday")]
    [InlineData("bad_time")]
    [InlineData("closes_before_opening")]
    public async Task Invalid_settings_are_rejected_and_nothing_is_saved(string case_)
    {
        var gym = await NewGym();
        await SeedUser(gym, "boss", "owner");
        var client = await Login(gym.Slug, "boss");

        object payload = case_ switch
        {
            "missing_switch" => new { hours = Week(d => Day(d, true)) },
            "six_days" => new { allowWithoutPlan = true, hours = Enumerable.Range(1, 6).Select(d => Day(d, true)).ToArray() },
            "repeated_day" => new { allowWithoutPlan = true, hours = Week(d => Day(d == 7 ? 1 : d, true)) },
            "bad_weekday" => new { allowWithoutPlan = true, hours = Week(d => Day(d == 7 ? 8 : d, true)) },
            "bad_time" => new { allowWithoutPlan = true, hours = Week(d => Day(d, true, "6am", "22:00")) },
            _ => new { allowWithoutPlan = true, hours = Week(d => Day(d, true, "22:00", "06:00")) },
        };

        var response = await client.PutAsJsonAsync("/settings", payload);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM business_hours WHERE tenant_id = @t", ("t", gym.TenantId)));
        Assert.Equal(false, await db.ScalarAsync("SELECT allow_without_plan FROM tenant_settings WHERE tenant_id = @t", ("t", gym.TenantId)));
    }

    [Fact]
    public async Task A_closed_day_may_have_any_times()
    {
        var gym = await NewGym();
        await SeedUser(gym, "boss", "owner");
        var client = await Login(gym.Slug, "boss");

        var response = await client.PutAsJsonAsync("/settings", new { allowWithoutPlan = false, hours = Week(d => Day(d, d != 7, d == 7 ? "10:00" : "06:00", d == 7 ? "09:00" : "22:00")) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Reception_can_read_but_not_change_and_customers_and_anonymous_are_refused()
    {
        var gym = await NewGym();
        await SeedUser(gym, "desk", "reception");
        await SeedUser(gym, "member", "customer", await Customer(gym));
        var desk = await Login(gym.Slug, "desk");
        var member = await Login(gym.Slug, "member");

        Assert.Equal(HttpStatusCode.OK, (await desk.GetAsync("/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PutAsJsonAsync("/settings", new { allowWithoutPlan = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await App().CreateClient().GetAsync("/settings")).StatusCode);
    }

    [Fact]
    public async Task One_gym_never_sees_or_changes_the_settings_of_another()
    {
        var a = await NewGym();
        var b = await NewGym();
        await SeedUser(a, "boss", "owner");
        await SeedUser(b, "boss", "owner");
        var clientA = await Login(a.Slug, "boss");
        var clientB = await Login(b.Slug, "boss");

        await clientA.PutAsJsonAsync("/settings", new { allowWithoutPlan = true, hours = Week(d => Day(d, true, "05:00", "23:00")) });

        var other = await Json(await clientB.GetAsync("/settings"));
        Assert.False(other.GetProperty("allowWithoutPlan").GetBoolean());
        Assert.All(other.GetProperty("hours").EnumerateArray(), h => Assert.False(h.GetProperty("configured").GetBoolean()));
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM business_hours WHERE tenant_id = @t", ("t", b.TenantId)));
    }

    [Fact]
    public async Task Hours_saved_through_the_api_restrict_the_turnstile()
    {
        var gym = await NewGym();
        await SeedUser(gym, "boss", "owner");
        var client = await Login(gym.Slug, "boss");
        await client.PutAsJsonAsync("/settings", new { allowWithoutPlan = true, hours = Week(d => Day(d, false, "00:00", "00:00")) });
        var customer = await Customer(gym);

        var response = await client.PostAsJsonAsync("/attendance/check-in", new { customerId = customer });

        var body = await Json(response);
        Assert.Contains("closed", body.GetRawText());
    }
}
