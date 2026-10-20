using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FitOS.Api.Tenancy;
using FitOS.Infrastructure.Auth;
using FitOS.IntegrationTests.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace FitOS.IntegrationTests.Measurements;

/// <summary>Weight and height history: who may read and write it, validation, same-day replacement and isolation.</summary>
public sealed class MeasurementsApiTests(PostgresFixture db) : IClassFixture<PostgresFixture>, IAsyncLifetime
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

    private static string Path(Guid customer) => $"/customers/{customer}/measurements";

    [Fact]
    public async Task A_customer_records_and_reads_their_own_measurements_oldest_first()
    {
        var gym = await NewGym();
        var customer = await Customer(gym);
        await SeedUser(gym, "member", "customer", customer);
        var member = await Login(gym.Slug, "member");

        Assert.Equal(HttpStatusCode.OK, (await member.PostAsJsonAsync(Path(customer), new { measuredOn = "2026-03-10", weightKg = 82.46, heightCm = 175 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.PostAsJsonAsync(Path(customer), new { measuredOn = "2026-01-05", weightKg = 85, heightCm = 175 })).StatusCode);

        var list = await Json(await member.GetAsync(Path(customer)));
        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal("2026-01-05", list[0].GetProperty("measuredOn").GetString());
        Assert.Equal(82.5m, list[1].GetProperty("weightKg").GetDecimal());
    }

    [Fact]
    public async Task Recording_twice_the_same_day_replaces_the_first()
    {
        var gym = await NewGym();
        var customer = await Customer(gym);
        await SeedUser(gym, "desk", "reception");
        var desk = await Login(gym.Slug, "desk");

        await desk.PostAsJsonAsync(Path(customer), new { measuredOn = "2026-03-10", weightKg = 80, heightCm = 170 });
        await desk.PostAsJsonAsync(Path(customer), new { measuredOn = "2026-03-10", weightKg = 79, heightCm = 170 });

        var list = await Json(await desk.GetAsync(Path(customer)));
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal(79m, list[0].GetProperty("weightKg").GetDecimal());
    }

    [Fact]
    public async Task Staff_record_for_any_customer_and_a_customer_cannot_touch_another()
    {
        var gym = await NewGym();
        var mine = await Customer(gym, 1);
        var other = await Customer(gym, 2);
        await SeedUser(gym, "member", "customer", mine);
        await SeedUser(gym, "desk", "reception");
        var member = await Login(gym.Slug, "member");
        var desk = await Login(gym.Slug, "desk");

        var saved = await desk.PostAsJsonAsync(Path(other), new { weightKg = 70, heightCm = 165 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var id = (await Json(saved)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync(Path(other))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.PostAsJsonAsync(Path(other), new { weightKg = 70, heightCm = 165 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.DeleteAsync($"{Path(other)}/{id}")).StatusCode);
        Assert.Equal(1, (await Json(await desk.GetAsync(Path(other)))).GetArrayLength());
    }

    [Fact]
    public async Task Invalid_values_are_rejected_with_a_code_per_field()
    {
        var gym = await NewGym();
        var customer = await Customer(gym);
        await SeedUser(gym, "desk", "reception");
        var desk = await Login(gym.Slug, "desk");

        var empty = await desk.PostAsJsonAsync(Path(customer), new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        var details = (await Json(empty)).GetProperty("error").GetProperty("details");
        Assert.Equal("required", details.GetProperty("weightKg").GetString());
        Assert.Equal("required", details.GetProperty("heightCm").GetString());

        var bad = await Json(await desk.PostAsJsonAsync(Path(customer), new { weightKg = 5, heightCm = 400, measuredOn = "2999-01-01" }));
        var errors = bad.GetProperty("error").GetProperty("details");
        Assert.Equal("invalid", errors.GetProperty("weightKg").GetString());
        Assert.Equal("invalid", errors.GetProperty("heightCm").GetString());
        Assert.Equal("in_the_future", errors.GetProperty("measuredOn").GetString());
    }

    [Fact]
    public async Task A_measurement_can_be_deleted_and_an_unknown_one_is_not_found()
    {
        var gym = await NewGym();
        var customer = await Customer(gym);
        await SeedUser(gym, "member", "customer", customer);
        var member = await Login(gym.Slug, "member");

        var id = (await Json(await member.PostAsJsonAsync(Path(customer), new { weightKg = 90, heightCm = 180 }))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync($"{Path(customer)}/{id}")).StatusCode);
        Assert.Equal(0, (await Json(await member.GetAsync(Path(customer)))).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await member.DeleteAsync($"{Path(customer)}/{id}")).StatusCode);
    }

    [Fact]
    public async Task One_gym_never_sees_the_measurements_of_another()
    {
        var gymA = await NewGym();
        var gymB = await NewGym();
        var customerA = await Customer(gymA);
        await SeedUser(gymA, "deska", "reception");
        await SeedUser(gymB, "deskb", "reception");
        var a = await Login(gymA.Slug, "deska");
        var b = await Login(gymB.Slug, "deskb");

        await a.PostAsJsonAsync(Path(customerA), new { weightKg = 70, heightCm = 170 });

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync(Path(customerA))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync(Path(customerA), new { weightKg = 60, heightCm = 160 })).StatusCode);
        Assert.Equal(1, (await Json(await a.GetAsync(Path(customerA)))).GetArrayLength());
    }
}
