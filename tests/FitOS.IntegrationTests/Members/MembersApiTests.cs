using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FitOS.Api.Tenancy;
using FitOS.Infrastructure.Auth;
using FitOS.Infrastructure.Members;
using FitOS.IntegrationTests.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace FitOS.IntegrationTests.Members;

/// <summary>Plans, customers and memberships end to end: HTTP pipeline, roles, PostgreSQL, row-level security.</summary>
public sealed class MembersApiTests(PostgresFixture db) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Password = "Correct-Horse-9!";

    private const string Cedula = "1710034065";

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

    private WebApplicationFactory<Program> App() => _app ??= Track(new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
    {
        host.UseTestSecrets();
        host.UseSetting("ConnectionStrings:Default", db.AppConnection);
        host.UseSetting("Auth:LoginRequestsPerMinute", "100000");
    }));

    private WebApplicationFactory<Program> Track(WebApplicationFactory<Program> factory)
    {
        _factories.Add(factory);
        return factory;
    }

    /// <summary>The "today" the API computes: the gym's time zone defaults to Guayaquil (UTC-5, no DST).</summary>
    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));

    private async Task<Gym> NewGym(string timezone = "America/Guayaquil")
    {
        var id = Guid.NewGuid();
        var slug = "m" + id.ToString("N")[..10];
        await db.ScalarAsync("INSERT INTO tenants (id, name, slug) VALUES (@id, 'Gym', @slug) RETURNING 1", ("id", id), ("slug", slug));
        await db.ScalarAsync("INSERT INTO tenant_settings (tenant_id, timezone) VALUES (@id, @tz) RETURNING 1", ("id", id), ("tz", timezone));
        return new Gym(id, slug);
    }

    private async Task<Guid> SeedUser(Gym gym, string username, string role, Guid? customerId = null, bool mustChange = false)
    {
        var id = Guid.NewGuid();
        await db.ScalarAsync(
            "INSERT INTO users (id, tenant_id, username, password_hash, display_name, role, customer_id, must_change_password) VALUES (@id, @t, @u, @h, 'N', @r, @c, @m) RETURNING 1",
            ("id", id), ("t", gym.TenantId), ("u", username), ("h", Hasher.Hash(Password)), ("r", role), ("c", customerId), ("m", mustChange));
        return id;
    }

    private async Task<HttpClient> Login(string slug, string username, string password = Password)
    {
        var client = App().CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login") { Content = JsonContent.Create(new { username, password }) };
        request.Headers.Add(TenantHostResolver.TenantHeader, slug);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await Json(response)).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>A gym with an owner, a reception user and their clients.</summary>
    private async Task<(Gym Gym, HttpClient Owner, HttpClient Reception)> Staffed()
    {
        var gym = await NewGym();
        await SeedUser(gym, "owner", "owner");
        await SeedUser(gym, "recep", "reception");
        return (gym, await Login(gym.Slug, "owner"), await Login(gym.Slug, "recep"));
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<Guid> CreatePlan(HttpClient owner, string name = "Mensual", int months = 1)
    {
        var response = await owner.PostAsJsonAsync("/plans", new { name, price = 40.00m, ivaRate = 15, durationMonths = months });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateCustomer(HttpClient staff, string name = "Ana Pérez", string? identification = null, string? rfid = null)
    {
        var response = await staff.PostAsJsonAsync("/customers", new { name, identificationType = identification is null ? null : "05", identification, rfidCard = rfid });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private static string ErrorCode(JsonElement body) => body.GetProperty("error").GetProperty("code").GetString()!;

    // ------------------------------------------------------------------------------------------------ plans

    [SkippableFact]
    public async Task Plans_are_managed_by_owners_read_by_everyone_and_hidden_from_customers_once_inactive()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (gym, owner, reception) = await Staffed();
        var customerId = await CreateCustomer(owner);
        await SeedUser(gym, "cust", "customer", customerId);
        var customer = await Login(gym.Slug, "cust");

        var planId = await CreatePlan(owner);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.PostAsJsonAsync("/plans", new { name = "X", price = 1, durationMonths = 1 })).StatusCode);

        Assert.Single((await Json(await reception.GetAsync("/plans"))).EnumerateArray());
        Assert.Single((await Json(await customer.GetAsync("/plans"))).EnumerateArray());

        var update = await owner.PutAsJsonAsync($"/plans/{planId}", new { name = "Mensual Plus", price = 45.50m, ivaRate = 15, durationMonths = 1, isPromotion = true });
        Assert.Equal(45.50m, (await Json(update)).GetProperty("price").GetDecimal());

        var off = await owner.PutAsJsonAsync($"/plans/{planId}/active", new { isActive = false });
        Assert.False((await Json(off)).GetProperty("isActive").GetBoolean());

        Assert.Empty((await Json(await customer.GetAsync("/plans?includeInactive=true"))).EnumerateArray()); // customers cannot ask for inactive ones
        Assert.Equal(HttpStatusCode.NotFound, (await customer.GetAsync($"/plans/{planId}")).StatusCode);
        Assert.Single((await Json(await owner.GetAsync("/plans?includeInactive=true"))).EnumerateArray());
        Assert.Empty((await Json(await owner.GetAsync("/plans"))).EnumerateArray());
    }

    [SkippableFact]
    public async Task Plan_names_are_unique_per_gym_ignoring_case_and_bad_data_is_explained()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, owner, _) = await Staffed();
        await CreatePlan(owner, "Mensual");

        var duplicate = await owner.PostAsJsonAsync("/plans", new { name = "mensual", price = 40, durationMonths = 1 });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("plan_name_taken", ErrorCode(await Json(duplicate)));

        var bad = await owner.PostAsJsonAsync("/plans", new { name = "", price = -5, ivaRate = 12, durationMonths = 0 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        var details = (await Json(bad)).GetProperty("error").GetProperty("details");
        Assert.Equal("required", details.GetProperty("name").GetString());
        Assert.Equal("invalid", details.GetProperty("price").GetString());
        Assert.Equal("invalid", details.GetProperty("ivaRate").GetString());
        Assert.Equal("invalid", details.GetProperty("durationMonths").GetString());
    }

    // -------------------------------------------------------------------------------------------- customers

    [SkippableFact]
    public async Task Customers_get_sequential_codes_and_duplicates_are_rejected()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, _, reception) = await Staffed();

        var first = await reception.PostAsJsonAsync("/customers", new { name = "  Ana Pérez ", identificationType = "05", identification = Cedula, email = "ana@example.com", gender = "f", rfidCard = "CARD-1" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var body = await Json(first);
        Assert.Equal(1, body.GetProperty("clientCode").GetInt32());
        Assert.Equal("Ana Pérez", body.GetProperty("name").GetString());
        Assert.Equal("F", body.GetProperty("gender").GetString());
        Assert.Equal("active", body.GetProperty("status").GetString());
        Assert.Equal(Today().ToString("yyyy-MM-dd"), body.GetProperty("joinedAt").GetString());

        var second = await reception.PostAsJsonAsync("/customers", new { name = "Beto" });
        Assert.Equal(2, (await Json(second)).GetProperty("clientCode").GetInt32());

        var sameId = await reception.PostAsJsonAsync("/customers", new { name = "Otra", identificationType = "05", identification = Cedula });
        Assert.Equal(HttpStatusCode.Conflict, sameId.StatusCode);
        Assert.Equal("identification_taken", ErrorCode(await Json(sameId)));

        var sameCard = await reception.PostAsJsonAsync("/customers", new { name = "Otra", rfidCard = "CARD-1" });
        Assert.Equal("rfid_card_taken", ErrorCode(await Json(sameCard)));

        var invalid = await reception.PostAsJsonAsync("/customers", new { name = "X", identificationType = "05", identification = "1710034066", email = "mal" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var details = (await Json(invalid)).GetProperty("error").GetProperty("details");
        Assert.Equal("invalid", details.GetProperty("identification").GetString());
        Assert.Equal("invalid", details.GetProperty("email").GetString());
    }

    [SkippableFact]
    public async Task Search_filters_pages_and_treats_wildcards_literally()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, owner, reception) = await Staffed();
        await CreateCustomer(reception, "Ana Pérez", Cedula);
        var betoId = await CreateCustomer(reception, "Beto Ruiz");
        await CreateCustomer(reception, "Carla 100% Fit");

        async Task<JsonElement> Search(string query) => await Json(await reception.GetAsync("/customers" + query));

        Assert.Equal(1, (await Search("?q=ana")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Search($"?q={Cedula[..6]}")).GetProperty("total").GetInt32()); // by identification
        Assert.Equal("Beto Ruiz", (await Search("?q=2")).GetProperty("items")[0].GetProperty("name").GetString()); // by client code
        Assert.Equal(1, (await Search("?q=100%25")).GetProperty("total").GetInt32()); // '%' is literal, not "match everything"
        Assert.Equal(0, (await Search("?q=_")).GetProperty("total").GetInt32());
        Assert.Equal(3, (await Search("")).GetProperty("total").GetInt32());

        var page2 = await Search("?pageSize=2&page=2");
        Assert.Equal(1, page2.GetProperty("items").GetArrayLength());
        Assert.Equal(3, page2.GetProperty("total").GetInt32());
        Assert.Equal(100, (await Search("?pageSize=100000")).GetProperty("pageSize").GetInt32());

        Assert.Equal(HttpStatusCode.Forbidden, (await reception.PutAsJsonAsync($"/customers/{betoId}/status", new { status = "inactive" })).StatusCode);
        var off = await owner.PutAsJsonAsync($"/customers/{betoId}/status", new { status = "inactive" });
        Assert.Equal("inactive", (await Json(off)).GetProperty("status").GetString());
        Assert.Equal(1, (await Search("?status=inactive")).GetProperty("total").GetInt32());
        Assert.Equal(2, (await Search("?status=active")).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await reception.GetAsync("/customers?status=weird")).StatusCode);
    }

    [SkippableFact]
    public async Task Customers_can_be_edited()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, _, reception) = await Staffed();
        var id = await CreateCustomer(reception, "Ana");

        var updated = await reception.PutAsJsonAsync($"/customers/{id}", new { name = "Ana María", phone = "+593 99 123 4567", birthDate = "1990-05-01" });

        var body = await Json(updated);
        Assert.Equal("Ana María", body.GetProperty("name").GetString());
        Assert.Equal("1990-05-01", body.GetProperty("birthDate").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await reception.PutAsJsonAsync($"/customers/{Guid.NewGuid()}", new { name = "X" })).StatusCode);
    }

    // ------------------------------------------------------------------------------------------- memberships

    [SkippableFact]
    public async Task Memberships_queue_instead_of_overlapping_and_show_what_is_current()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, owner, reception) = await Staffed();
        var plan = await CreatePlan(owner);
        var customer = await CreateCustomer(reception, "Ana", Cedula);
        var url = $"/customers/{customer}/memberships";

        var first = await owner.PostAsJsonAsync(url, new { planId = plan });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await Json(first);
        var firstEnd = DateOnly.Parse(firstBody.GetProperty("endsOn").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("current", firstBody.GetProperty("state").GetString());
        Assert.Equal(Today().ToString("yyyy-MM-dd"), firstBody.GetProperty("startsOn").GetString());
        Assert.Equal("Mensual", firstBody.GetProperty("planName").GetString());

        // Renewing while covered queues the new one right after, even when a date inside the covered period is asked for.
        var second = await Json(await owner.PostAsJsonAsync(url, new { planId = plan, startsOn = Today().AddDays(3) }));
        Assert.Equal(firstEnd.AddDays(1).ToString("yyyy-MM-dd"), second.GetProperty("startsOn").GetString());
        Assert.Equal("scheduled", second.GetProperty("state").GetString());

        var list = (await Json(await reception.GetAsync(url))).EnumerateArray().ToList();
        Assert.Equal(["scheduled", "current"], list.Select(m => m.GetProperty("state").GetString()!).ToArray()); // newest first

        var detail = await Json(await reception.GetAsync($"/customers/{customer}"));
        Assert.Equal("current", detail.GetProperty("membership").GetProperty("state").GetString());
        Assert.Equal(firstEnd.ToString("yyyy-MM-dd"), detail.GetProperty("customer").GetProperty("activeUntil").GetString());

        var inList = (await Json(await reception.GetAsync("/customers"))).GetProperty("items")[0];
        Assert.Equal(firstEnd.ToString("yyyy-MM-dd"), inList.GetProperty("activeUntil").GetString());

        // Only managers cancel; the cancelled one stops counting.
        var scheduledId = second.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.PostAsync($"/memberships/{scheduledId}/cancel", null)).StatusCode);
        var cancelled = await owner.PostAsync($"/memberships/{scheduledId}/cancel", null);
        Assert.Equal("cancelled", (await Json(cancelled)).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/memberships/{scheduledId}/cancel", null)).StatusCode); // idempotent
    }

    [SkippableFact]
    public async Task Memberships_are_refused_for_inactive_customers_inactive_plans_past_dates_and_unknown_ids()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, owner, reception) = await Staffed();
        var plan = await CreatePlan(owner);
        var customer = await CreateCustomer(reception, "Ana");
        var url = $"/customers/{customer}/memberships";

        var past = await owner.PostAsJsonAsync(url, new { planId = plan, startsOn = Today().AddDays(-1) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, past.StatusCode);
        Assert.Equal("in_the_past", (await Json(past)).GetProperty("error").GetProperty("details").GetProperty("startsOn").GetString());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsJsonAsync(url, new { })).StatusCode); // no plan
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync(url, new { planId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/customers/{Guid.NewGuid()}/memberships", new { planId = plan })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/memberships/{Guid.NewGuid()}/cancel", null)).StatusCode);

        await owner.PutAsJsonAsync($"/plans/{plan}/active", new { isActive = false });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsJsonAsync(url, new { planId = plan })).StatusCode);
        await owner.PutAsJsonAsync($"/plans/{plan}/active", new { isActive = true });

        await owner.PutAsJsonAsync($"/customers/{customer}/status", new { status = "inactive" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsJsonAsync(url, new { planId = plan })).StatusCode);
    }

    [SkippableFact]
    public async Task Simultaneous_assignments_never_overlap()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (gym, owner, reception) = await Staffed();
        var plan = await CreatePlan(owner);
        var customer = await CreateCustomer(reception, "Ana");
        var url = $"/customers/{customer}/memberships";

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => owner.PostAsJsonAsync(url, new { planId = plan })));

        // Either queued behind each other (201) or the database refused a race (409): never an overlap.
        Assert.All(results, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, r.StatusCode.ToString()));
        Assert.Contains(results, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(
            0L,
            await db.ScalarAsync(
                """
                SELECT count(*) FROM memberships a JOIN memberships b
                  ON a.customer_id = b.customer_id AND a.id < b.id
                 AND daterange(a.starts_on, a.ends_on, '[]') && daterange(b.starts_on, b.ends_on, '[]')
                WHERE a.tenant_id = @t AND a.status = 'active' AND b.status = 'active'
                """,
                ("t", gym.TenantId)));
    }

    // ------------------------------------------------------------------------------------- access and isolation

    [SkippableFact]
    public async Task One_gym_can_never_see_or_change_another_gyms_data()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, ownerA, receptionA) = await Staffed();
        var (_, ownerB, receptionB) = await Staffed();

        var planB = await CreatePlan(ownerB, "Plan de B");
        var customerB = await CreateCustomer(receptionB, "Cliente de B", Cedula);
        var membershipB = (await Json(await ownerB.PostAsJsonAsync($"/customers/{customerB}/memberships", new { planId = planB }))).GetProperty("id").GetGuid();
        var planA = await CreatePlan(ownerA, "Plan de A");
        var customerA = await CreateCustomer(receptionA, "Cliente de A");

        // Reading and writing B's records with A's token looks exactly like "does not exist".
        Assert.Equal(HttpStatusCode.NotFound, (await receptionA.GetAsync($"/customers/{customerB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await receptionA.PutAsJsonAsync($"/customers/{customerB}", new { name = "Hackeado" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerA.PutAsJsonAsync($"/customers/{customerB}/status", new { status = "inactive" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await receptionA.GetAsync($"/customers/{customerB}/memberships")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerA.PutAsJsonAsync($"/plans/{planB}", new { name = "X", price = 1, durationMonths = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerA.PostAsync($"/memberships/{membershipB}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerA.PostAsJsonAsync($"/customers/{customerA}/memberships", new { planId = planB })).StatusCode); // B's plan
        Assert.Equal(HttpStatusCode.NotFound, (await ownerA.PostAsJsonAsync($"/customers/{customerB}/memberships", new { planId = planA })).StatusCode); // B's customer

        // Listings never leak, and B's data is untouched.
        Assert.Equal(1, (await Json(await receptionA.GetAsync("/customers"))).GetProperty("total").GetInt32());
        Assert.DoesNotContain("Plan de B", await (await ownerA.GetAsync("/plans")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("Cliente de B", (await Json(await receptionB.GetAsync($"/customers/{customerB}"))).GetProperty("customer").GetProperty("name").GetString());

        // Uniqueness is per gym: the same cédula is fine in another gym.
        Assert.Equal(HttpStatusCode.Created, (await receptionA.PostAsJsonAsync("/customers", new { name = "Mismo documento", identificationType = "05", identification = Cedula })).StatusCode);
    }

    [SkippableFact]
    public async Task A_customer_only_sees_their_own_record_and_cannot_manage_anything()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (gym, owner, reception) = await Staffed();
        var plan = await CreatePlan(owner);
        var mine = await CreateCustomer(reception, "Yo");
        var other = await CreateCustomer(reception, "Otro");
        await owner.PostAsJsonAsync($"/customers/{mine}/memberships", new { planId = plan });
        await SeedUser(gym, "yo", "customer", mine);
        var me = await Login(gym.Slug, "yo");

        Assert.Equal(HttpStatusCode.OK, (await me.GetAsync($"/customers/{mine}")).StatusCode);
        Assert.Single((await Json(await me.GetAsync($"/customers/{mine}/memberships"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await me.GetAsync($"/customers/{other}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await me.GetAsync($"/customers/{other}/memberships")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await me.GetAsync("/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await me.PostAsJsonAsync("/customers", new { name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await me.PostAsJsonAsync($"/customers/{mine}/memberships", new { planId = plan })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await me.PostAsync($"/memberships/{Guid.NewGuid()}/cancel", null)).StatusCode);
    }

    [SkippableFact]
    public async Task Anonymous_platform_and_password_pending_users_are_all_kept_out()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var gym = await NewGym();
        await SeedUser(gym, "pending", "owner", mustChange: true);
        var pending = await Login(gym.Slug, "pending");
        var anonymous = App().CreateClient();

        var platformName = "root-" + Guid.NewGuid().ToString("N")[..8];
        await db.ScalarAsync(
            "INSERT INTO users (tenant_id, username, password_hash, display_name, role) VALUES (NULL, @u, @h, 'Root', 'platform_admin') RETURNING 1",
            ("u", platformName), ("h", Hasher.Hash(Password)));
        var platform = await Login("platform", platformName);

        foreach (var url in new[] { "/customers", "/plans" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await pending.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await platform.GetAsync(url)).StatusCode);
        }
    }

    // ----------------------------------------------------------------------------------- database guarantees

    [SkippableFact]
    public async Task The_database_refuses_overlapping_active_memberships_even_if_the_app_did_not()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (gym, owner, reception) = await Staffed();
        var plan = await CreatePlan(owner);
        var customer = await CreateCustomer(reception, "Ana");

        Task<object?> Insert(string status, string from, string to) => db.ScalarAsync(
            "INSERT INTO memberships (tenant_id, customer_id, plan_id, starts_on, ends_on, status) VALUES (@t, @c, @p, @f::date, @e::date, @s) RETURNING 1",
            ("t", gym.TenantId), ("c", customer), ("p", plan), ("f", from), ("e", to), ("s", status));

        await Insert("active", "2030-01-01", "2030-01-31");
        var overlap = await Assert.ThrowsAsync<PostgresException>(() => Insert("active", "2030-01-31", "2030-02-28")); // shares Jan 31
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, overlap.SqlState);
        await Insert("active", "2030-02-01", "2030-02-28"); // adjacent is fine
        await Insert("cancelled", "2030-01-10", "2030-01-20"); // cancelled ones do not count
    }

    [SkippableFact]
    public async Task The_tenant_clock_uses_the_gyms_own_time_zone_and_survives_a_bad_one()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var instant = new DateTimeOffset(2026, 10, 17, 3, 0, 0, TimeSpan.Zero); // 22:00 on Oct 16 in Guayaquil, 17:00 on Oct 17 in Kiritimati

        async Task<DateOnly> TodayFor(string timezone)
        {
            var gym = await NewGym(timezone);
            await using var context = db.Context(gym.TenantId);
            return await new TenantClock(new TenantSettingsReader(context), new FixedTime(instant)).TodayAsync(CancellationToken.None);
        }

        Assert.Equal(new DateOnly(2026, 10, 16), await TodayFor("America/Guayaquil"));
        Assert.Equal(new DateOnly(2026, 10, 17), await TodayFor("Pacific/Kiritimati"));
        Assert.Equal(new DateOnly(2026, 10, 16), await TodayFor("Mars/Phobos")); // unknown zone falls back to Ecuador
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
