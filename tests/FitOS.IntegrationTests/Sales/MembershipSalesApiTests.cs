using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FitOS.Api.Tenancy;
using FitOS.Infrastructure.Auth;
using FitOS.IntegrationTests.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace FitOS.IntegrationTests.Sales;

/// <summary>Charging a plan end to end: one transaction for sale, payment and membership.</summary>
public sealed class MembershipSalesApiTests(PostgresFixture db) : IClassFixture<PostgresFixture>, IAsyncLifetime
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

    private sealed record Gym(Guid TenantId, string Slug);

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
        var slug = "m" + id.ToString("N")[..10];
        await db.ScalarAsync("INSERT INTO tenants (id, name, slug) VALUES (@id, 'Gym', @slug) RETURNING 1", ("id", id), ("slug", slug));
        await db.ScalarAsync("INSERT INTO tenant_settings (tenant_id) VALUES (@id) RETURNING 1", ("id", id));
        await db.ScalarAsync("INSERT INTO branches (tenant_id, name, address, sri_establishment) VALUES (@id, 'Matriz', 'Quito', '001') RETURNING 1", ("id", id));
        return new Gym(id, slug);
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

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private async Task<(Gym Gym, HttpClient Owner, HttpClient Desk)> Shop()
    {
        var gym = await NewGym();
        await SeedUser(gym, "boss", "owner");
        await SeedUser(gym, "desk", "reception");
        return (gym, await Login(gym.Slug, "boss"), await Login(gym.Slug, "desk"));
    }

    private static async Task<Guid> Plan(HttpClient owner, decimal price = 40m, decimal iva = 15m, int months = 1)
    {
        var response = await owner.PostAsJsonAsync("/plans", new { name = "Plan " + Guid.NewGuid().ToString("N")[..6], price, ivaRate = iva, durationMonths = months });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> Customer(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/customers", new { name = "Socio " + Guid.NewGuid().ToString("N")[..6] });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private static List<object> Pay(decimal amount, string method = "cash") => [new { method, amount }];

    private static string Url(Guid customer) => $"/customers/{customer}/memberships";

    private Task<object?> Count(string table, Guid tenant) => db.ScalarAsync($"SELECT count(*) FROM {table} WHERE tenant_id = @t", ("t", tenant));

    [Fact]
    public async Task Reception_charges_a_plan_and_sale_payment_and_membership_are_linked()
    {
        var (gym, owner, desk) = await Shop();
        var plan = await Plan(owner);
        var customer = await Customer(desk);

        var response = await desk.PostAsJsonAsync(Url(customer), new { planId = plan, payments = Pay(46m, "transfer") });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(46m, body.GetProperty("total").GetDecimal());
        Assert.Equal(6m, body.GetProperty("taxTotal").GetDecimal());
        Assert.Equal("current", body.GetProperty("state").GetString());
        var saleId = body.GetProperty("saleId").GetGuid();

        Assert.Equal(saleId, await db.ScalarAsync("SELECT sale_id FROM memberships WHERE id = @m", ("m", body.GetProperty("id").GetGuid())));
        Assert.Equal(plan, await db.ScalarAsync("SELECT plan_id FROM sale_lines WHERE sale_id = @s", ("s", saleId)));
        Assert.Equal("20", await db.ScalarAsync("SELECT sri_code FROM sale_payments WHERE sale_id = @s", ("s", saleId)));
        Assert.Equal("identified", await db.ScalarAsync("SELECT buyer_kind FROM sales WHERE id = @s", ("s", saleId)));
        Assert.Equal(customer, await db.ScalarAsync("SELECT customer_id FROM sales WHERE id = @s", ("s", saleId)));
        Assert.NotNull(await db.ScalarAsync("SELECT cashier_id FROM sales WHERE id = @s AND tenant_id = @t", ("s", saleId), ("t", gym.TenantId)));
    }

    [Fact]
    public async Task The_price_comes_from_the_plan_not_from_the_request()
    {
        var (_, owner, desk) = await Shop();
        var plan = await Plan(owner, price: 40m, iva: 0m);
        var customer = await Customer(desk);

        var cheat = await desk.PostAsJsonAsync(Url(customer), new { planId = plan, price = 1m, ivaRate = 15, payments = Pay(1m) });
        var honest = await desk.PostAsJsonAsync(Url(customer), new { planId = plan, price = 1m, payments = Pay(40m) });

        Assert.Equal(HttpStatusCode.Conflict, cheat.StatusCode);
        Assert.Equal("payment_mismatch", (await Json(cheat)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, honest.StatusCode);
        Assert.Equal(40m, (await Json(honest)).GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task A_wrong_payment_writes_neither_sale_nor_membership()
    {
        var (gym, owner, desk) = await Shop();
        var plan = await Plan(owner);
        var customer = await Customer(desk);

        var response = await desk.PostAsJsonAsync(Url(customer), new { planId = plan, payments = Pay(40m) }); // forgot the IVA

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0L, await Count("sales", gym.TenantId));
        Assert.Equal(0L, await Count("memberships", gym.TenantId));
    }

    [Fact]
    public async Task Only_managers_can_give_a_plan_without_charging()
    {
        var (gym, owner, desk) = await Shop();
        var plan = await Plan(owner);
        var customer = await Customer(desk);

        var refused = await desk.PostAsJsonAsync(Url(customer), new { planId = plan });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(0L, await Count("memberships", gym.TenantId));

        var courtesy = await owner.PostAsJsonAsync(Url(customer), new { planId = plan });
        Assert.Equal(HttpStatusCode.Created, courtesy.StatusCode);
        Assert.Equal(1L, await Count("memberships", gym.TenantId));
        Assert.Equal(0L, await Count("sales", gym.TenantId));
        Assert.Equal(DBNull.Value, await db.ScalarAsync("SELECT sale_id FROM memberships WHERE tenant_id = @t", ("t", gym.TenantId)));
    }

    [Fact]
    public async Task An_empty_payments_list_is_invalid_not_a_courtesy()
    {
        var (gym, owner, desk) = await Shop();
        var plan = await Plan(owner);
        var customer = await Customer(desk);

        var response = await owner.PostAsJsonAsync(Url(customer), new { planId = plan, payments = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0L, await Count("memberships", gym.TenantId));
    }

    [Fact]
    public async Task A_second_charged_plan_queues_after_the_first_and_each_has_its_own_sale()
    {
        var (gym, owner, desk) = await Shop();
        var plan = await Plan(owner);
        var customer = await Customer(desk);

        var first = await Json(await desk.PostAsJsonAsync(Url(customer), new { planId = plan, payments = Pay(46m) }));
        var second = await Json(await desk.PostAsJsonAsync(Url(customer), new { planId = plan, payments = Pay(46m) }));

        Assert.Equal("scheduled", second.GetProperty("state").GetString());
        Assert.NotEqual(first.GetProperty("saleId").GetGuid(), second.GetProperty("saleId").GetGuid());
        Assert.Equal(2L, await Count("sales", gym.TenantId));
        Assert.Equal(2L, await Count("memberships", gym.TenantId));
    }

    [Fact]
    public async Task Simultaneous_charges_never_leave_a_sale_without_its_membership()
    {
        var (gym, owner, desk) = await Shop();
        var plan = await Plan(owner);
        var customer = await Customer(desk);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            desk.PostAsJsonAsync(Url(customer), new { planId = plan, payments = Pay(46m) })));

        var created = results.Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.True(created >= 1);
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal((long)created, await Count("sales", gym.TenantId));
        Assert.Equal((long)created, await Count("memberships", gym.TenantId));
        Assert.Equal(0L, await db.ScalarAsync(
            "SELECT count(*) FROM sales s WHERE s.tenant_id = @t AND NOT EXISTS (SELECT 1 FROM memberships m WHERE m.sale_id = s.id)", ("t", gym.TenantId)));
    }

    [Fact]
    public async Task Inactive_plan_inactive_customer_and_past_dates_charge_nothing()
    {
        var (gym, owner, desk) = await Shop();
        var plan = await Plan(owner);
        var off = await Plan(owner);
        await owner.PutAsJsonAsync($"/plans/{off}/active", new { isActive = false });
        var customer = await Customer(desk);
        var gone = await Customer(desk);
        await owner.PutAsJsonAsync($"/customers/{gone}/status", new { status = "inactive" });

        var inactivePlan = await desk.PostAsJsonAsync(Url(customer), new { planId = off, payments = Pay(46m) });
        var inactiveCustomer = await desk.PostAsJsonAsync(Url(gone), new { planId = plan, payments = Pay(46m) });
        var past = await desk.PostAsJsonAsync(Url(customer), new { planId = plan, startsOn = "2020-01-01", payments = Pay(46m) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, inactivePlan.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inactiveCustomer.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, past.StatusCode);
        Assert.Equal(0L, await Count("sales", gym.TenantId));
    }

    [Fact]
    public async Task Customers_of_other_gyms_and_other_gyms_plans_cannot_be_charged()
    {
        var (_, ownerA, deskA) = await Shop();
        var (gymB, ownerB, deskB) = await Shop();
        var planA = await Plan(ownerA);
        var planB = await Plan(ownerB);
        var customerA = await Customer(deskA);

        var foreignCustomer = await deskB.PostAsJsonAsync(Url(customerA), new { planId = planB, payments = Pay(46m) });
        var foreignPlan = await deskB.PostAsJsonAsync(Url(await Customer(deskB)), new { planId = planA, payments = Pay(46m) });

        Assert.Equal(HttpStatusCode.NotFound, foreignCustomer.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignPlan.StatusCode);
        Assert.Equal(0L, await Count("sales", gymB.TenantId));
    }

    [Fact]
    public async Task Customers_themselves_cannot_buy_through_this_endpoint()
    {
        var (gym, owner, desk) = await Shop();
        var plan = await Plan(owner);
        var customer = await Customer(desk);
        await SeedUser(gym, "member", "customer", customer);
        var member = await Login(gym.Slug, "member");

        var response = await member.PostAsJsonAsync(Url(customer), new { planId = plan, payments = Pay(46m) });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0L, await Count("sales", gym.TenantId));
    }

    [Fact]
    public async Task Charged_plans_count_in_the_dashboard_revenue_and_active_members()
    {
        var (_, owner, desk) = await Shop();
        var plan = await Plan(owner);
        var customer = await Customer(desk);
        await desk.PostAsJsonAsync(Url(customer), new { planId = plan, payments = Pay(46m) });

        var summary = await Json(await owner.GetAsync("/dashboard/summary"));

        Assert.Equal(46m, summary.GetProperty("sales").GetProperty("today").GetDecimal());
        Assert.Equal(1, summary.GetProperty("activeMembers").GetInt32());
    }
}
