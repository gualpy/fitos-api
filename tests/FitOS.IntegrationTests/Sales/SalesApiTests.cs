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

/// <summary>Catalogue, stock and counter sales end to end: roles, money, transactions and isolation between gyms.</summary>
public sealed class SalesApiTests(PostgresFixture db) : IClassFixture<PostgresFixture>, IAsyncLifetime
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

    private async Task<Gym> NewGym(bool withBranch = true)
    {
        var id = Guid.NewGuid();
        var slug = "v" + id.ToString("N")[..10];
        var branch = Guid.NewGuid();
        await db.ScalarAsync("INSERT INTO tenants (id, name, slug) VALUES (@id, 'Gym', @slug) RETURNING 1", ("id", id), ("slug", slug));
        await db.ScalarAsync("INSERT INTO tenant_settings (tenant_id) VALUES (@id) RETURNING 1", ("id", id));
        if (withBranch)
        {
            await db.ScalarAsync("INSERT INTO branches (id, tenant_id, name, address, sri_establishment) VALUES (@b, @id, 'Matriz', 'Quito', '001') RETURNING 1", ("b", branch), ("id", id));
        }

        return new Gym(id, slug, branch);
    }

    private async Task<Guid> SeedUser(Gym gym, string username, string role, Guid? customerId = null)
    {
        var id = Guid.NewGuid();
        await db.ScalarAsync(
            "INSERT INTO users (id, tenant_id, username, password_hash, display_name, role, customer_id) VALUES (@i, @t, @u, @h, 'N', @r, @c) RETURNING 1",
            ("i", id), ("t", gym.TenantId), ("u", username), ("h", Hasher.Hash(Password)), ("r", role), ("c", customerId));
        return id;
    }

    private async Task<Guid> Customer(Gym gym)
    {
        var id = Guid.NewGuid();
        await db.ScalarAsync(
            "INSERT INTO customers (id, tenant_id, name, client_code) VALUES (@i, @t, 'Socio', (SELECT coalesce(max(client_code), 0) + 1 FROM customers WHERE tenant_id = @t)) RETURNING 1",
            ("i", id), ("t", gym.TenantId));
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

    private static async Task<Guid> CreateProduct(HttpClient client, string name, decimal price, int stock, decimal iva = 15m)
    {
        var response = await client.PostAsJsonAsync("/products", new { name, price, ivaRate = iva, stock, category = "Test" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<(Gym Gym, HttpClient Owner, HttpClient Desk)> Shop()
    {
        var gym = await NewGym();
        await SeedUser(gym, "boss", "owner");
        await SeedUser(gym, "desk", "reception");
        return (gym, await Login(gym.Slug, "boss"), await Login(gym.Slug, "desk"));
    }

    private static object Pay(string method, decimal amount) => new { method, amount };

    private static object Line(Guid product, int quantity) => new { productId = product, quantity };

    private Task<object?> Stock(Guid product) => db.ScalarAsync("SELECT stock FROM products WHERE id = @p", ("p", product));

    // ---- catalogue ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_manager_creates_lists_edits_and_deactivates_products()
    {
        var (_, owner, desk) = await Shop();

        var created = await owner.PostAsJsonAsync("/products", new { sku = "W-1", barcode = "7501", name = "Whey", category = "Suplementos", price = 35, ivaRate = 15, stock = 12 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await Json(created)).GetProperty("id").GetGuid();

        var updated = await owner.PutAsJsonAsync($"/products/{id}", new { sku = "W-1", barcode = "7501", name = "Whey 2kg", category = "Suplementos", price = 60, ivaRate = 15, stock = 999 });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var body = await Json(updated);
        Assert.Equal("Whey 2kg", body.GetProperty("name").GetString());
        Assert.Equal(12, body.GetProperty("stock").GetInt32()); // stock is not editable through the product form

        var seen = await Json(await desk.GetAsync("/products"));
        Assert.Equal("Whey 2kg", Assert.Single(seen.EnumerateArray()).GetProperty("name").GetString());

        await owner.PutAsJsonAsync($"/products/{id}/active", new { isActive = false });
        Assert.Empty((await Json(await desk.GetAsync("/products"))).EnumerateArray());
        Assert.Single((await Json(await desk.GetAsync("/products?includeInactive=true"))).EnumerateArray());
    }

    [Fact]
    public async Task Opening_stock_leaves_a_movement_that_explains_it()
    {
        var (gym, owner, _) = await Shop();

        var id = await CreateProduct(owner, "Agua", 1.5m, 40, 0m);

        Assert.Equal(40, await db.ScalarAsync("SELECT delta FROM stock_movements WHERE product_id = @p", ("p", id)));
        Assert.Equal("adjustment", await db.ScalarAsync("SELECT reason FROM stock_movements WHERE product_id = @p", ("p", id)));
        Assert.Equal(1L, await db.ScalarAsync("SELECT count(*) FROM products WHERE tenant_id = @t", ("t", gym.TenantId)));
    }

    [Fact]
    public async Task Names_skus_and_barcodes_cannot_repeat_inside_a_gym_but_can_across_gyms()
    {
        var (_, owner, _) = await Shop();
        await owner.PostAsJsonAsync("/products", new { sku = "S1", barcode = "B1", name = "Whey", price = 1 });

        var sameName = await owner.PostAsJsonAsync("/products", new { name = "  WHEY ", price = 1 });
        var sameSku = await owner.PostAsJsonAsync("/products", new { sku = "S1", name = "Otro", price = 1 });
        var sameBarcode = await owner.PostAsJsonAsync("/products", new { barcode = "B1", name = "Otro 2", price = 1 });

        Assert.Equal(HttpStatusCode.Conflict, sameName.StatusCode);
        Assert.Equal("product_name_taken", (await Json(sameName)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("sku_taken", (await Json(sameSku)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("barcode_taken", (await Json(sameBarcode)).GetProperty("error").GetProperty("code").GetString());

        var (_, other, _) = await Shop();
        Assert.Equal(HttpStatusCode.Created, (await other.PostAsJsonAsync("/products", new { sku = "S1", barcode = "B1", name = "Whey", price = 1 })).StatusCode);
    }

    [Fact]
    public async Task A_failed_create_does_not_leave_stock_movements_behind()
    {
        var (gym, owner, _) = await Shop();
        await CreateProduct(owner, "Whey", 1, 5);

        var again = await owner.PostAsJsonAsync("/products", new { name = "Whey", price = 1, stock = 9 });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(1L, await db.ScalarAsync("SELECT count(*) FROM stock_movements WHERE tenant_id = @t", ("t", gym.TenantId)));
    }

    [Fact]
    public async Task Invalid_products_are_rejected_with_the_field_names()
    {
        var (_, owner, _) = await Shop();

        var response = await owner.PostAsJsonAsync("/products", new { name = "", price = -5, ivaRate = 12, stock = -1 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var details = (await Json(response)).GetProperty("error").GetProperty("details");
        Assert.Equal("required", details.GetProperty("name").GetString());
        Assert.Equal("invalid", details.GetProperty("price").GetString());
        Assert.Equal("invalid", details.GetProperty("ivaRate").GetString());
        Assert.Equal("invalid", details.GetProperty("stock").GetString());
    }

    [Fact]
    public async Task Search_matches_name_sku_barcode_and_category_and_treats_wildcards_literally()
    {
        var (_, owner, _) = await Shop();
        await owner.PostAsJsonAsync("/products", new { sku = "PRO-9", barcode = "123456", name = "Proteína", category = "Suplementos", price = 1 });
        await owner.PostAsJsonAsync("/products", new { name = "Agua", category = "Bebidas", price = 1 });

        async Task<int> Count(string q) => (await Json(await owner.GetAsync($"/products?q={Uri.EscapeDataString(q)}"))).GetArrayLength();

        Assert.Equal(1, await Count("prote"));
        Assert.Equal(1, await Count("pro-9"));
        Assert.Equal(1, await Count("3456"));
        Assert.Equal(1, await Count("bebi"));
        Assert.Equal(0, await Count("%"));
        Assert.Equal(0, await Count("_"));
    }

    [Fact]
    public async Task Stock_is_adjusted_with_a_movement_and_cannot_go_negative()
    {
        var (_, owner, _) = await Shop();
        var id = await CreateProduct(owner, "Agua", 1, 5);

        var up = await owner.PostAsJsonAsync($"/products/{id}/stock", new { delta = 10, reason = "purchase" });
        var down = await owner.PostAsJsonAsync($"/products/{id}/stock", new { delta = -3, reason = "adjustment" });
        var tooMuch = await owner.PostAsJsonAsync($"/products/{id}/stock", new { delta = -100, reason = "adjustment" });

        Assert.Equal(15, (await Json(up)).GetProperty("stock").GetInt32());
        Assert.Equal(12, (await Json(down)).GetProperty("stock").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);
        Assert.Equal(12, await Stock(id));
        Assert.Equal(3L, await db.ScalarAsync("SELECT count(*) FROM stock_movements WHERE product_id = @p", ("p", id)));
        Assert.Equal(12L, await db.ScalarAsync("SELECT sum(delta) FROM stock_movements WHERE product_id = @p", ("p", id)));
    }

    [Fact]
    public async Task Only_managers_change_the_catalogue_and_customers_see_nothing()
    {
        var (gym, owner, desk) = await Shop();
        var id = await CreateProduct(owner, "Agua", 1, 5);
        await SeedUser(gym, "member", "customer", await Customer(gym));
        var member = await Login(gym.Slug, "member");

        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsJsonAsync("/products", new { name = "X", price = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PutAsJsonAsync($"/products/{id}", new { name = "X", price = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PutAsJsonAsync($"/products/{id}/active", new { isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsJsonAsync($"/products/{id}/stock", new { delta = 1, reason = "purchase" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await App().CreateClient().GetAsync("/products")).StatusCode);
    }

    [Fact]
    public async Task One_gym_never_sees_or_touches_the_products_of_another()
    {
        var (_, ownerA, _) = await Shop();
        var (_, ownerB, _) = await Shop();
        var id = await CreateProduct(ownerA, "Secreto", 9, 5);

        Assert.Empty((await Json(await ownerB.GetAsync("/products"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.GetAsync($"/products/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.PutAsJsonAsync($"/products/{id}", new { name = "Robado", price = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.PostAsJsonAsync($"/products/{id}/stock", new { delta = -1, reason = "adjustment" })).StatusCode);
        Assert.Equal(5, await Stock(id));
    }

    // ---- sales ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_sale_is_priced_by_the_server_takes_stock_out_and_records_everything()
    {
        var (gym, owner, desk) = await Shop();
        var whey = await CreateProduct(owner, "Whey", 35m, 10);
        var water = await CreateProduct(owner, "Agua", 1.5m, 100, 0m);
        var customer = await Customer(gym);

        var response = await desk.PostAsJsonAsync("/sales", new
        {
            customerId = customer,
            lines = new[] { Line(whey, 2), Line(water, 4) },
            payments = new[] { Pay("cash", 86.5m) },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(76m, body.GetProperty("subtotal").GetDecimal());
        Assert.Equal(10.5m, body.GetProperty("taxTotal").GetDecimal());
        Assert.Equal(86.5m, body.GetProperty("total").GetDecimal());
        var id = body.GetProperty("id").GetGuid();

        Assert.Equal(8, await Stock(whey));
        Assert.Equal(96, await Stock(water));
        Assert.Equal(2L, await db.ScalarAsync("SELECT count(*) FROM sale_lines WHERE sale_id = @s", ("s", id)));
        Assert.Equal(2L, await db.ScalarAsync("SELECT count(*) FROM stock_movements WHERE sale_id = @s AND reason = 'sale' AND delta < 0", ("s", id)));
        Assert.Equal("01", await db.ScalarAsync("SELECT sri_code FROM sale_payments WHERE sale_id = @s", ("s", id)));
        Assert.Equal("identified", await db.ScalarAsync("SELECT buyer_kind FROM sales WHERE id = @s", ("s", id)));
        Assert.Equal("Whey", await db.ScalarAsync("SELECT description FROM sale_lines WHERE sale_id = @s AND iva_rate = 15", ("s", id)));
        Assert.Equal(gym.BranchId, await db.ScalarAsync("SELECT branch_id FROM sales WHERE id = @s", ("s", id)));
    }

    [Fact]
    public async Task The_client_cannot_choose_prices_or_rates()
    {
        var (_, owner, desk) = await Shop();
        var whey = await CreateProduct(owner, "Whey", 35m, 10);

        var response = await desk.PostAsJsonAsync("/sales", new
        {
            lines = new[] { new { productId = whey, quantity = 1, unitPrice = 0.01m, ivaRate = 0 } },
            payments = new[] { Pay("cash", 0.01m) },
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("payment_mismatch", (await Json(response)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(10, await Stock(whey));
    }

    [Fact]
    public async Task A_sale_without_a_customer_is_a_final_consumer_sale_and_the_cashier_is_recorded()
    {
        var (gym, owner, desk) = await Shop();
        var water = await CreateProduct(owner, "Agua", 2m, 10, 0m);

        var response = await desk.PostAsJsonAsync("/sales", new { lines = new[] { Line(water, 1) }, payments = new[] { Pay("transfer", 2m) } });

        var id = (await Json(response)).GetProperty("id").GetGuid();
        Assert.Equal("final_consumer", await db.ScalarAsync("SELECT buyer_kind FROM sales WHERE id = @s", ("s", id)));
        Assert.Equal("20", await db.ScalarAsync("SELECT sri_code FROM sale_payments WHERE sale_id = @s", ("s", id)));
        Assert.NotNull(await db.ScalarAsync("SELECT cashier_id FROM sales WHERE id = @s AND tenant_id = @t", ("s", id), ("t", gym.TenantId)));
    }

    [Fact]
    public async Task Selling_more_than_the_stock_changes_nothing_at_all()
    {
        var (gym, owner, desk) = await Shop();
        var plenty = await CreateProduct(owner, "Agua", 1m, 50, 0m);
        var scarce = await CreateProduct(owner, "Whey", 10m, 1, 0m);

        var response = await desk.PostAsJsonAsync("/sales", new
        {
            lines = new[] { Line(plenty, 5), Line(scarce, 2) },
            payments = new[] { Pay("cash", 25m) },
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("insufficient_stock", (await Json(response)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(50, await Stock(plenty)); // the first product's stock was rolled back
        Assert.Equal(1, await Stock(scarce));
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM sales WHERE tenant_id = @t", ("t", gym.TenantId)));
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM sale_lines WHERE tenant_id = @t", ("t", gym.TenantId)));
    }

    [Fact]
    public async Task Two_cashiers_cannot_both_sell_the_last_unit()
    {
        var (gym, owner, desk) = await Shop();
        var last = await CreateProduct(owner, "Ultimo", 5m, 1, 0m);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            (Task<HttpResponseMessage>)desk.PostAsJsonAsync("/sales", new { lines = new[] { Line(last, 1) }, payments = new[] { Pay("cash", 5m) } })));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(0, await Stock(last));
        Assert.Equal(1L, await db.ScalarAsync("SELECT count(*) FROM sales WHERE tenant_id = @t", ("t", gym.TenantId)));
    }

    [Fact]
    public async Task Stock_movements_always_add_up_to_the_stock()
    {
        var (_, owner, desk) = await Shop();
        var id = await CreateProduct(owner, "Agua", 1m, 30, 0m);
        await desk.PostAsJsonAsync("/sales", new { lines = new[] { Line(id, 7) }, payments = new[] { Pay("cash", 7m) } });
        await owner.PostAsJsonAsync($"/products/{id}/stock", new { delta = 4, reason = "purchase" });
        await desk.PostAsJsonAsync("/sales", new { lines = new[] { Line(id, 2) }, payments = new[] { Pay("cash", 2m) } });

        Assert.Equal(25, await Stock(id));
        Assert.Equal(25L, await db.ScalarAsync("SELECT sum(delta) FROM stock_movements WHERE product_id = @p", ("p", id)));
    }

    [Fact]
    public async Task Inactive_unknown_and_foreign_products_cannot_be_sold()
    {
        var (_, owner, desk) = await Shop();
        var (_, otherOwner, _) = await Shop();
        var off = await CreateProduct(owner, "Descontinuado", 1m, 5, 0m);
        await owner.PutAsJsonAsync($"/products/{off}/active", new { isActive = false });
        var foreign = await CreateProduct(otherOwner, "Ajeno", 1m, 5, 0m);

        var inactive = await desk.PostAsJsonAsync("/sales", new { lines = new[] { Line(off, 1) }, payments = new[] { Pay("cash", 1m) } });
        var unknown = await desk.PostAsJsonAsync("/sales", new { lines = new[] { Line(Guid.NewGuid(), 1) }, payments = new[] { Pay("cash", 1m) } });
        var other = await desk.PostAsJsonAsync("/sales", new { lines = new[] { Line(foreign, 1) }, payments = new[] { Pay("cash", 1m) } });

        Assert.Equal(HttpStatusCode.Conflict, inactive.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(5, await Stock(foreign));
    }

    [Fact]
    public async Task A_customer_of_another_gym_cannot_be_put_on_a_sale()
    {
        var (_, owner, desk) = await Shop();
        var other = await NewGym();
        var stranger = await Customer(other);
        var water = await CreateProduct(owner, "Agua", 1m, 5, 0m);

        var response = await desk.PostAsJsonAsync("/sales", new { customerId = stranger, lines = new[] { Line(water, 1) }, payments = new[] { Pay("cash", 1m) } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(5, await Stock(water));
    }

    [Fact]
    public async Task Invalid_sales_are_rejected_before_touching_anything()
    {
        var (_, owner, desk) = await Shop();
        var water = await CreateProduct(owner, "Agua", 1m, 5, 0m);

        var empty = await desk.PostAsJsonAsync("/sales", new { lines = Array.Empty<object>(), payments = new[] { Pay("cash", 1m) } });
        var badMethod = await desk.PostAsJsonAsync("/sales", new { lines = new[] { Line(water, 1) }, payments = new[] { Pay("bitcoin", 1m) } });
        var zero = await desk.PostAsJsonAsync("/sales", new { lines = new[] { Line(water, 0) }, payments = new[] { Pay("cash", 1m) } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badMethod.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, zero.StatusCode);
        Assert.Equal(5, await Stock(water));
    }

    [Fact]
    public async Task A_gym_without_a_branch_cannot_sell_and_stock_is_untouched()
    {
        var gym = await NewGym(withBranch: false);
        await SeedUser(gym, "boss", "owner");
        var owner = await Login(gym.Slug, "boss");
        var water = await CreateProduct(owner, "Agua", 1m, 5, 0m);

        var response = await owner.PostAsJsonAsync("/sales", new { lines = new[] { Line(water, 1) }, payments = new[] { Pay("cash", 1m) } });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("no_branch", (await Json(response)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(5, await Stock(water));
    }

    [Fact]
    public async Task Customers_and_anonymous_users_cannot_sell()
    {
        var (gym, owner, _) = await Shop();
        var water = await CreateProduct(owner, "Agua", 1m, 5, 0m);
        await SeedUser(gym, "member", "customer", await Customer(gym));
        var member = await Login(gym.Slug, "member");
        var payload = new { lines = new[] { Line(water, 1) }, payments = new[] { Pay("cash", 1m) } };

        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/sales", payload)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await App().CreateClient().PostAsJsonAsync("/sales", payload)).StatusCode);
        Assert.Equal(5, await Stock(water));
    }

    [Fact]
    public async Task Sales_feed_the_dashboard_revenue()
    {
        var (_, owner, desk) = await Shop();
        var whey = await CreateProduct(owner, "Whey", 35m, 10);
        await desk.PostAsJsonAsync("/sales", new { lines = new[] { Line(whey, 1) }, payments = new[] { Pay("cash", 40.25m) } });

        var summary = await Json(await owner.GetAsync("/dashboard/summary"));

        Assert.Equal(40.25m, summary.GetProperty("sales").GetProperty("today").GetDecimal());
    }
}
