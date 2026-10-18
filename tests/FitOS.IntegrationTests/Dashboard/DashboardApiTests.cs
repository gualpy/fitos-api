using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FitOS.Api.Tenancy;
using FitOS.Infrastructure.Auth;
using FitOS.IntegrationTests.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FitOS.IntegrationTests.Dashboard;

/// <summary>The dashboard numbers end to end: HTTP pipeline, roles, PostgreSQL aggregates and row-level security.</summary>
public sealed class DashboardApiTests(PostgresFixture db) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Password = "Correct-Horse-9!";

    private static readonly IdentityPasswordService Hasher = new();

    private static readonly TimeSpan Ecuador = TimeSpan.FromHours(-5);

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

    private async Task<Gym> NewGym()
    {
        var id = Guid.NewGuid();
        var slug = "d" + id.ToString("N")[..10];
        var branch = Guid.NewGuid();
        await db.ScalarAsync("INSERT INTO tenants (id, name, slug) VALUES (@id, 'Gym', @slug) RETURNING 1", ("id", id), ("slug", slug));
        await db.ScalarAsync("INSERT INTO tenant_settings (tenant_id) VALUES (@id) RETURNING 1", ("id", id));
        await db.ScalarAsync("INSERT INTO branches (id, tenant_id, name, address, sri_establishment) VALUES (@b, @id, 'Matriz', 'Quito', '001') RETURNING 1", ("b", branch), ("id", id));
        return new Gym(id, slug, branch);
    }

    private async Task SeedUser(Gym gym, string username, string role, Guid? customerId = null) =>
        await db.ScalarAsync(
            "INSERT INTO users (tenant_id, username, password_hash, display_name, role, customer_id) VALUES (@t, @u, @h, 'N', @r, @c) RETURNING 1",
            ("t", gym.TenantId), ("u", username), ("h", Hasher.Hash(Password)), ("r", role), ("c", customerId));

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

    private static DateOnly TodayEc() => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(Ecuador).DateTime);

    /// <summary>Noon of a gym day (gym zone), as the UTC instant PostgreSQL wants.</summary>
    private static DateTimeOffset Noon(DateOnly day) => new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), Ecuador).ToUniversalTime();

    private async Task<Guid> Customer(Gym gym, string name, string? gender = null, string status = "active")
    {
        var id = Guid.NewGuid();
        await db.ScalarAsync(
            "INSERT INTO customers (id, tenant_id, name, gender, status, client_code) VALUES (@i, @t, @n, @g, @s, (SELECT coalesce(max(client_code), 0) + 1 FROM customers WHERE tenant_id = @t)) RETURNING 1",
            ("i", id), ("t", gym.TenantId), ("n", name), ("g", gender), ("s", status));
        return id;
    }

    private async Task<Guid> Plan(Gym gym)
    {
        var id = Guid.NewGuid();
        await db.ScalarAsync("INSERT INTO plans (id, tenant_id, name, price, duration_months) VALUES (@i, @t, 'Mensual', 40, 1) RETURNING 1", ("i", id), ("t", gym.TenantId));
        return id;
    }

    private Task<object?> Membership(Gym gym, Guid customer, Guid plan, DateOnly from, DateOnly to) =>
        db.ScalarAsync(
            "INSERT INTO memberships (tenant_id, customer_id, plan_id, starts_on, ends_on) VALUES (@t, @c, @p, @f, @e) RETURNING 1",
            ("t", gym.TenantId), ("c", customer), ("p", plan), ("f", from), ("e", to));

    private Task<object?> Entry(Gym gym, Guid customer, DateTimeOffset checkIn, bool open, string result = "granted") =>
        db.ScalarAsync(
            "INSERT INTO attendance (tenant_id, branch_id, customer_id, check_in, check_out, result, deny_reason) VALUES (@t, @b, @c, @i, @o, @r, @d) RETURNING 1",
            ("t", gym.TenantId), ("b", gym.BranchId), ("c", customer), ("i", checkIn), ("o", open ? null : checkIn.AddHours(1)), ("r", result), ("d", result == "denied" ? "closed" : null));

    private Task<object?> Sale(Gym gym, decimal subtotal, decimal tax, DateTimeOffset at, string status = "paid") =>
        db.ScalarAsync(
            "INSERT INTO sales (tenant_id, branch_id, subtotal, tax_total, total, status, created_at) VALUES (@t, @b, @s, @x, @s + @x, @st, @a) RETURNING 1",
            ("t", gym.TenantId), ("b", gym.BranchId), ("s", subtotal), ("x", tax), ("st", status), ("a", at));

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>Gym with a known mix: 5 customers, 3 covered, 1 about to expire, entries today and earlier, sales.</summary>
    private async Task<Gym> Populated()
    {
        var gym = await NewGym();
        var today = TodayEc();
        var plan = await Plan(gym);

        var ana = await Customer(gym, "Ana", "F");
        var bob = await Customer(gym, "Bob", "M");
        var cris = await Customer(gym, "Cris", "M");
        var dora = await Customer(gym, "Dora");
        var eva = await Customer(gym, "Eva", "F", status: "inactive");

        await Membership(gym, ana, plan, today.AddDays(-5), today.AddDays(25));
        await Membership(gym, bob, plan, today.AddDays(-27), today.AddDays(3)); // ends soon, nothing after
        await Membership(gym, cris, plan, today.AddDays(-27), today.AddDays(3)); // ends soon but renewal is booked
        await Membership(gym, cris, plan, today.AddDays(4), today.AddDays(34));
        await Membership(gym, eva, plan, today.AddDays(-5), today.AddDays(25)); // inactive customer: not an active member

        await Entry(gym, ana, Noon(today).AddHours(-3), open: false);
        await Entry(gym, ana, Noon(today), open: false); // same person twice counts once
        await Entry(gym, bob, Noon(today), open: true); // still inside
        await Entry(gym, dora, Noon(today), open: false, result: "denied"); // denied never counts
        await Entry(gym, ana, Noon(today.AddDays(-2)), open: false);
        await Entry(gym, ana, Noon(today.AddDays(-9)), open: false); // outside the window

        await Sale(gym, 10m, 1.5m, Noon(today));
        await Sale(gym, 20m, 3m, Noon(today));
        await Sale(gym, 500m, 75m, Noon(today), status: "void"); // voided: ignored
        await Sale(gym, 10m, 1.5m, Noon(today.AddDays(-3)));
        await Sale(gym, 99m, 0m, Noon(today.AddDays(-8))); // outside the window

        await SeedUser(gym, "owner", "owner");
        await SeedUser(gym, "recep", "reception");
        return gym;
    }

    [SkippableFact]
    public async Task The_summary_counts_what_is_really_in_the_gym()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var gym = await Populated();
        var owner = await Login(gym.Slug, "owner");

        var response = await owner.GetAsync("/dashboard/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(TodayEc().ToString("yyyy-MM-dd"), body.GetProperty("today").GetString());
        Assert.Equal(5, body.GetProperty("totalCustomers").GetInt32());
        Assert.Equal(3, body.GetProperty("activeMembers").GetInt32()); // ana, bob, cris; eva is inactive
        Assert.Equal(1, body.GetProperty("expiringSoon").GetInt32()); // bob; cris already renewed
        Assert.Equal(7, body.GetProperty("expiringWithinDays").GetInt32());
        Assert.Equal(2, body.GetProperty("attendanceToday").GetInt32()); // ana (once) and bob
        Assert.Equal(1, body.GetProperty("insideNow").GetInt32()); // bob
    }

    [SkippableFact]
    public async Task Gender_breakdown_covers_active_members_only_and_keeps_the_unknown()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var gym = await Populated();
        var owner = await Login(gym.Slug, "owner");

        var genders = (await Json(await owner.GetAsync("/dashboard/summary"))).GetProperty("genders").EnumerateArray()
            .ToDictionary(g => g.GetProperty("gender").GetString() ?? "unknown", g => g.GetProperty("count").GetInt32());

        Assert.Equal(new Dictionary<string, int> { ["F"] = 1, ["M"] = 2, ["unknown"] = 1 }, genders);
    }

    [SkippableFact]
    public async Task Attendance_per_day_has_seven_days_with_zeros_where_nobody_came()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var gym = await Populated();
        var owner = await Login(gym.Slug, "owner");
        var today = TodayEc();

        var days = (await Json(await owner.GetAsync("/dashboard/summary"))).GetProperty("attendance").EnumerateArray()
            .ToDictionary(d => d.GetProperty("date").GetString()!, d => d.GetProperty("count").GetInt32());

        Assert.Equal(7, days.Count);
        Assert.Equal(2, days[today.ToString("yyyy-MM-dd")]);
        Assert.Equal(1, days[today.AddDays(-2).ToString("yyyy-MM-dd")]);
        Assert.Equal(0, days[today.AddDays(-1).ToString("yyyy-MM-dd")]);
        Assert.DoesNotContain(today.AddDays(-9).ToString("yyyy-MM-dd"), days.Keys);
    }

    [SkippableFact]
    public async Task Owners_see_paid_sales_by_day_and_voided_or_old_ones_do_not_count()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var gym = await Populated();
        var owner = await Login(gym.Slug, "owner");
        var today = TodayEc();

        var sales = (await Json(await owner.GetAsync("/dashboard/summary"))).GetProperty("sales");

        Assert.Equal(34.5m, sales.GetProperty("today").GetDecimal()); // 11.5 + 23.0
        Assert.Equal(46.0m, sales.GetProperty("last7Days").GetDecimal()); // + 11.5 three days ago
        var daily = sales.GetProperty("daily").EnumerateArray().ToDictionary(d => d.GetProperty("date").GetString()!, d => d.GetProperty("amount").GetDecimal());
        Assert.Equal(7, daily.Count);
        Assert.Equal(11.5m, daily[today.AddDays(-3).ToString("yyyy-MM-dd")]);
        Assert.Equal(0m, daily[today.AddDays(-1).ToString("yyyy-MM-dd")]);
    }

    [SkippableFact]
    public async Task Reception_gets_the_counts_but_never_the_money()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var gym = await Populated();
        var reception = await Login(gym.Slug, "recep");

        var body = await Json(await reception.GetAsync("/dashboard/summary"));

        Assert.Equal(3, body.GetProperty("activeMembers").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("sales").ValueKind);
    }

    [SkippableFact]
    public async Task A_customer_or_an_anonymous_caller_cannot_read_the_dashboard()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var gym = await Populated();
        var member = await Customer(gym, "Socio");
        await SeedUser(gym, "socio", "customer", member);

        Assert.Equal(HttpStatusCode.Forbidden, (await (await Login(gym.Slug, "socio")).GetAsync("/dashboard/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await App().CreateClient().GetAsync("/dashboard/summary")).StatusCode);
    }

    [SkippableFact]
    public async Task One_gym_never_sees_the_numbers_of_another()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var busy = await Populated();
        var quiet = await NewGym();
        await SeedUser(quiet, "owner", "owner");

        var body = await Json(await (await Login(quiet.Slug, "owner")).GetAsync("/dashboard/summary"));

        Assert.Equal(0, body.GetProperty("totalCustomers").GetInt32());
        Assert.Equal(0, body.GetProperty("activeMembers").GetInt32());
        Assert.Equal(0, body.GetProperty("attendanceToday").GetInt32());
        Assert.Equal(0m, body.GetProperty("sales").GetProperty("last7Days").GetDecimal());
        Assert.Empty(body.GetProperty("genders").EnumerateArray());
        Assert.NotEqual(busy.TenantId, quiet.TenantId);
    }

    // ------------------------------------------------------------------------------------------ insights

    private sealed class FakeInsights(bool configured) : FitOS.Application.Dashboard.IInsightsProvider
    {
        public bool IsConfigured { get; } = configured;

        public int Calls { get; private set; }

        public string? LastPrompt { get; private set; }

        public Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken)
        {
            Calls++;
            LastPrompt = prompt;
            return Task.FromResult("1. Retén a quienes vencen.");
        }
    }

    private static async Task<HttpClient> LoginTo(WebApplicationFactory<Program> app, string slug, string username)
    {
        var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login") { Content = JsonContent.Create(new { username, password = Password }) };
        request.Headers.Add(TenantHostResolver.TenantHeader, slug);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        return client;
    }

    private WebApplicationFactory<Program> WithInsights(FakeInsights provider)
    {
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseTestSecrets();
            host.UseSetting("ConnectionStrings:Default", db.AppConnection);
            host.UseSetting("Auth:LoginRequestsPerMinute", "100000");
            host.ConfigureTestServices(services => services.AddSingleton<FitOS.Application.Dashboard.IInsightsProvider>(provider));
        });
        _factories.Add(app);
        return app;
    }

    [SkippableFact]
    public async Task Without_a_provider_key_the_insights_answer_503_with_a_stable_code()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var gym = await Populated();

        var response = await (await Login(gym.Slug, "owner")).GetAsync("/dashboard/insights");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("insights_unavailable", (await Json(response)).GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Owners_get_insights_built_from_aggregates_and_the_second_call_is_cached()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var gym = await Populated();
        var provider = new FakeInsights(configured: true);
        var owner = await LoginTo(WithInsights(provider), gym.Slug, "owner");

        var first = await Json(await owner.GetAsync("/dashboard/insights"));
        var second = await Json(await owner.GetAsync("/dashboard/insights"));

        Assert.Equal("1. Retén a quienes vencen.", first.GetProperty("text").GetString());
        Assert.Equal(first.GetProperty("generatedAt").GetString(), second.GetProperty("generatedAt").GetString());
        Assert.Equal(1, provider.Calls);
        Assert.Contains("Socios registrados: 5", provider.LastPrompt);
        foreach (var name in new[] { "Ana", "Bob", "Cris", "Dora", "Eva" })
        {
            Assert.DoesNotMatch($@"\b{name}\b", provider.LastPrompt!);
        }
    }

    [SkippableFact]
    public async Task Reception_and_customers_cannot_ask_for_insights()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var gym = await Populated();
        var provider = new FakeInsights(configured: true);
        var app = WithInsights(provider);

        Assert.Equal(HttpStatusCode.Forbidden, (await (await LoginTo(app, gym.Slug, "recep")).GetAsync("/dashboard/insights")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/dashboard/insights")).StatusCode);
        Assert.Equal(0, provider.Calls);
    }
}
