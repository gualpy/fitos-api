using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FitOS.Api.Tenancy;
using FitOS.Infrastructure.Auth;
using FitOS.IntegrationTests.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace FitOS.IntegrationTests.Attendance;

/// <summary>Check-in/out and history end to end: HTTP pipeline, roles, PostgreSQL, row-level security.</summary>
public sealed class AttendanceApiTests(PostgresFixture db) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Password = "Correct-Horse-9!";

    private static readonly IdentityPasswordService Hasher = new();

    private static readonly TimeZoneInfo Ecuador = TimeZoneInfo.FindSystemTimeZoneById("America/Guayaquil");

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
        var slug = "a" + id.ToString("N")[..10];
        var branch = Guid.NewGuid();
        await db.ScalarAsync("INSERT INTO tenants (id, name, slug) VALUES (@id, 'Gym', @slug) RETURNING 1", ("id", id), ("slug", slug));
        await db.ScalarAsync("INSERT INTO tenant_settings (tenant_id) VALUES (@id) RETURNING 1", ("id", id));
        await db.ScalarAsync("INSERT INTO branches (id, tenant_id, name, address, sri_establishment) VALUES (@b, @id, 'Matriz', 'Quito', '001') RETURNING 1", ("b", branch), ("id", id));
        return new Gym(id, slug, branch);
    }

    private async Task SeedUser(Gym gym, string username, string role, Guid? customerId = null)
    {
        await db.ScalarAsync(
            "INSERT INTO users (tenant_id, username, password_hash, display_name, role, customer_id) VALUES (@t, @u, @h, 'N', @r, @c) RETURNING 1",
            ("t", gym.TenantId), ("u", username), ("h", Hasher.Hash(Password)), ("r", role), ("c", customerId));
    }

    private async Task<HttpClient> Login(string slug, string username)
    {
        var client = App().CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login") { Content = JsonContent.Create(new { username, password = Password }) };
        request.Headers.Add(TenantHostResolver.TenantHeader, slug);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await Json(response)).GetProperty("accessToken").GetString());
        return client;
    }

    private async Task<(Gym Gym, HttpClient Owner, HttpClient Reception)> Staffed()
    {
        var gym = await NewGym();
        await SeedUser(gym, "owner", "owner");
        await SeedUser(gym, "recep", "reception");
        return (gym, await Login(gym.Slug, "owner"), await Login(gym.Slug, "recep"));
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static string ErrorCode(JsonElement body) => body.GetProperty("error").GetProperty("code").GetString()!;

    private static async Task<Guid> CreateCustomer(HttpClient staff, string name = "Ana Pérez", string? rfid = null)
    {
        var response = await staff.PostAsJsonAsync("/customers", new { name, rfidCard = rfid });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    /// <summary>A customer with a current membership, so the door opens for them.</summary>
    private static async Task<Guid> Member(HttpClient owner, HttpClient staff, string name = "Ana Pérez", string? rfid = null)
    {
        var plan = (await Json(await owner.PostAsJsonAsync("/plans", new { name = "Plan " + Guid.NewGuid().ToString("N")[..6], price = 40, durationMonths = 1 }))).GetProperty("id").GetGuid();
        var customer = await CreateCustomer(staff, name, rfid);
        Assert.Equal(HttpStatusCode.Created, (await staff.PostAsJsonAsync($"/customers/{customer}/memberships", new { planId = plan })).StatusCode);
        return customer;
    }

    private static DateOnly TodayEc() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Ecuador).DateTime);

    private Task<object?> InsertEntry(Gym gym, Guid customer, DateTimeOffset checkIn, DateTimeOffset? checkOut, string result = "granted", string? reason = null) =>
        db.ScalarAsync(
            "INSERT INTO attendance (tenant_id, branch_id, customer_id, check_in, check_out, result, deny_reason) VALUES (@t, @b, @c, @i, @o, @r, @d) RETURNING 1",
            ("t", gym.TenantId), ("b", gym.BranchId), ("c", customer), ("i", checkIn), ("o", checkOut), ("r", result), ("d", reason));

    // ------------------------------------------------------------------------------------------ check in / out

    [SkippableFact]
    public async Task A_member_checks_in_and_out_and_can_come_back()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, owner, reception) = await Staffed();
        var customer = await Member(owner, reception);

        var entry = await reception.PostAsJsonAsync("/attendance/check-in", new { customerId = customer });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
        var body = await Json(entry);
        Assert.Equal("granted", body.GetProperty("result").GetString());
        Assert.Equal(TodayEc().ToString("yyyy-MM-dd"), body.GetProperty("date").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("checkOut").ValueKind);

        var again = await reception.PostAsJsonAsync("/attendance/check-in", new { customerId = customer });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("already_inside", ErrorCode(await Json(again)));

        var exit = await reception.PostAsJsonAsync("/attendance/check-out", new { customerId = customer });
        Assert.Equal(HttpStatusCode.OK, exit.StatusCode);
        var exitBody = await Json(exit);
        Assert.NotEqual(JsonValueKind.Null, exitBody.GetProperty("checkOut").ValueKind);
        Assert.True(exitBody.GetProperty("minutes").GetInt32() >= 0);

        var second = await reception.PostAsJsonAsync("/attendance/check-out", new { customerId = customer });
        Assert.Equal("no_open_entry", ErrorCode(await Json(second)));

        Assert.Equal(HttpStatusCode.Created, (await reception.PostAsJsonAsync("/attendance/check-in", new { customerId = customer })).StatusCode);
    }

    [SkippableFact]
    public async Task The_customer_can_be_identified_by_client_code_or_card_but_by_exactly_one()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, owner, reception) = await Staffed();
        var customer = await Member(owner, reception, rfid: "CARD-77");
        var code = (await Json(await reception.GetAsync($"/customers/{customer}"))).GetProperty("customer").GetProperty("clientCode").GetInt64();

        Assert.Equal(HttpStatusCode.Created, (await reception.PostAsJsonAsync("/attendance/check-in", new { clientCode = code })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reception.PostAsJsonAsync("/attendance/check-out", new { rfidCard = "CARD-77" })).StatusCode);

        var both = await reception.PostAsJsonAsync("/attendance/check-in", new { customerId = customer, clientCode = code });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, both.StatusCode);
        Assert.Equal("exactly_one_required", (await Json(both)).GetProperty("error").GetProperty("details").GetProperty("identifier").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await reception.PostAsJsonAsync("/attendance/check-in", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reception.PostAsJsonAsync("/attendance/check-in", new { clientCode = 999999 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reception.PostAsJsonAsync("/attendance/check-in", new { rfidCard = "NOPE" })).StatusCode);
    }

    [SkippableFact]
    public async Task A_denied_scan_is_recorded_listed_and_never_blocks_the_next_one()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, _, reception) = await Staffed();
        var customer = await CreateCustomer(reception, "Sin Plan");

        var first = await reception.PostAsJsonAsync("/attendance/check-in", new { customerId = customer });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode); // a recorded event, not a failed request
        var body = await Json(first);
        Assert.Equal("denied", body.GetProperty("result").GetString());
        Assert.Equal("no_active_membership", body.GetProperty("denyReason").GetString());
        Assert.Equal("denied", (await Json(await reception.PostAsJsonAsync("/attendance/check-in", new { customerId = customer }))).GetProperty("result").GetString());

        Assert.Equal(2, (await Json(await reception.GetAsync("/attendance?result=denied"))).GetProperty("total").GetInt32());
        Assert.Equal(0, (await Json(await reception.GetAsync("/attendance?result=granted"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await reception.GetAsync("/attendance?result=maybe")).StatusCode);
        Assert.Equal("no_open_entry", ErrorCode(await Json(await reception.PostAsJsonAsync("/attendance/check-out", new { customerId = customer }))));
    }

    [SkippableFact]
    public async Task The_gym_can_allow_entry_without_a_plan_or_close_its_doors()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (gym, _, reception) = await Staffed();
        var customer = await CreateCustomer(reception, "Sin Plan");

        await db.ScalarAsync("UPDATE tenant_settings SET allow_without_plan = true WHERE tenant_id = @t RETURNING 1", ("t", gym.TenantId));
        var open = await Json(await reception.PostAsJsonAsync("/attendance/check-in", new { customerId = customer }));
        Assert.Equal("granted", open.GetProperty("result").GetString());
        await reception.PostAsJsonAsync("/attendance/check-out", new { customerId = customer });

        await db.ScalarAsync(
            "INSERT INTO business_hours (tenant_id, branch_id, weekday, is_open) SELECT @t, @b, d, false FROM generate_series(1, 7) d RETURNING 1",
            ("t", gym.TenantId), ("b", gym.BranchId));
        var closed = await Json(await reception.PostAsJsonAsync("/attendance/check-in", new { customerId = customer }));
        Assert.Equal("denied", closed.GetProperty("result").GetString());
        Assert.Equal("closed", closed.GetProperty("denyReason").GetString());
    }

    [SkippableFact]
    public async Task A_forgotten_entry_is_closed_visibly_and_does_not_block_the_next_visit()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (gym, owner, reception) = await Staffed();
        var customer = await Member(owner, reception);
        await InsertEntry(gym, customer, DateTimeOffset.UtcNow.AddHours(-30), checkOut: null);

        var response = await reception.PostAsJsonAsync("/attendance/check-in", new { customerId = customer });

        Assert.Equal("granted", (await Json(response)).GetProperty("result").GetString());
        var old = (await db.RowsAsync("SELECT auto_closed_at IS NOT NULL, check_out = check_in FROM attendance WHERE customer_id = @c AND auto_closed_at IS NOT NULL", ("c", customer))).Single();
        Assert.Equal(true, old[0]);
        Assert.Equal(true, old[1]);
        Assert.Equal(1L, await db.ScalarAsync("SELECT count(*) FROM attendance WHERE customer_id = @c AND check_out IS NULL AND result = 'granted'", ("c", customer)));
    }

    [SkippableFact]
    public async Task Simultaneous_scans_let_exactly_one_entry_in()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, owner, reception) = await Staffed();
        var customer = await Member(owner, reception);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => reception.PostAsJsonAsync("/attendance/check-in", new { customerId = customer })));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1L, await db.ScalarAsync("SELECT count(*) FROM attendance WHERE customer_id = @c", ("c", customer)));
    }

    // ------------------------------------------------------------------------------------------ history

    [SkippableFact]
    public async Task History_filters_by_the_gyms_local_days_pages_and_sorts_newest_first()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (gym, _, reception) = await Staffed();
        var ana = await CreateCustomer(reception, "Ana");
        var beto = await CreateCustomer(reception, "Beto");
        DateTimeOffset Local(int day, int hour) => new(TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 9, day, hour, 0, 0, DateTimeKind.Unspecified), Ecuador), TimeSpan.Zero);

        await InsertEntry(gym, ana, Local(10, 7), Local(10, 8));
        await InsertEntry(gym, ana, Local(11, 22), Local(11, 23)); // 22:00 local on the 11th is already the 12th in UTC
        await InsertEntry(gym, ana, Local(12, 7), Local(12, 8));
        await InsertEntry(gym, beto, Local(11, 6), Local(11, 7));

        async Task<JsonElement> Get(string query) => await Json(await reception.GetAsync("/attendance" + query));

        var all = await Get("?pageSize=100");
        Assert.Equal(4, all.GetProperty("total").GetInt32());
        var dates = all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("date").GetString()).ToArray();
        Assert.Equal(["2026-09-12", "2026-09-11", "2026-09-11", "2026-09-10"], dates);

        var day11 = await Get("?from=2026-09-11&to=2026-09-11"); // whole local day, including 22:00-23:00
        Assert.Equal(2, day11.GetProperty("total").GetInt32());
        Assert.Equal(2, (await Get($"?customerId={ana}&from=2026-09-11")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await Get("?from=2026-09-13")).GetProperty("total").GetInt32());

        var page2 = await Get("?pageSize=3&page=2");
        Assert.Equal(1, page2.GetProperty("items").GetArrayLength());
        Assert.Equal(60, all.GetProperty("items")[0].GetProperty("minutes").GetInt32());
        // Times come in the gym's own clock: 22:00 local on the 11th, not 03:00 UTC on the 12th.
        var lateNight = all.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("checkInTime").GetString() == "22:00:00");
        Assert.Equal("2026-09-11", lateNight.GetProperty("date").GetString());
        Assert.Equal("23:00:00", lateNight.GetProperty("checkOutTime").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await reception.GetAsync("/attendance?from=2026-09-12&to=2026-09-10")).StatusCode);
    }

    // ------------------------------------------------------------------------------------------ access and isolation

    [SkippableFact]
    public async Task A_customers_own_history_can_be_filtered_by_result()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (gym, owner, reception) = await Staffed();
        var mine = await Member(owner, reception, "Yo");
        var now = DateTimeOffset.UtcNow;
        await InsertEntry(gym, mine, now.AddHours(-3), now.AddHours(-2));
        await InsertEntry(gym, mine, now.AddHours(-1), null, result: "denied", reason: "closed");
        await SeedUser(gym, "yo", "customer", mine);
        var me = await Login(gym.Slug, "yo");

        Assert.Equal(2, (await Json(await me.GetAsync($"/customers/{mine}/attendance"))).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Json(await me.GetAsync($"/customers/{mine}/attendance?result=granted"))).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Json(await me.GetAsync($"/customers/{mine}/attendance?result=denied"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await me.GetAsync($"/customers/{mine}/attendance?result=maybe")).StatusCode);
    }

    [SkippableFact]
    public async Task A_customer_sees_only_their_own_history_and_cannot_scan_or_list_everyone()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (gym, owner, reception) = await Staffed();
        var mine = await Member(owner, reception, "Yo");
        var other = await Member(owner, reception, "Otro");
        await reception.PostAsJsonAsync("/attendance/check-in", new { customerId = mine });
        await reception.PostAsJsonAsync("/attendance/check-in", new { customerId = other });
        await SeedUser(gym, "yo", "customer", mine);
        var me = await Login(gym.Slug, "yo");

        var own = await Json(await me.GetAsync($"/customers/{mine}/attendance"));
        Assert.Equal(1, own.GetProperty("total").GetInt32());
        Assert.Equal(mine, own.GetProperty("items")[0].GetProperty("customerId").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await me.GetAsync($"/customers/{other}/attendance")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await me.GetAsync("/attendance")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await me.PostAsJsonAsync("/attendance/check-in", new { customerId = mine })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await me.PostAsJsonAsync("/attendance/check-out", new { customerId = mine })).StatusCode);
    }

    [SkippableFact]
    public async Task One_gym_never_sees_or_scans_another_gyms_customers()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (_, ownerA, receptionA) = await Staffed();
        var (_, ownerB, receptionB) = await Staffed();
        var customerA = await Member(ownerA, receptionA, "De A", rfid: "SHARED-CARD");
        var customerB = await Member(ownerB, receptionB, "De B"); // the card number is only unique per gym
        await receptionB.PostAsJsonAsync("/attendance/check-in", new { customerId = customerB });
        await receptionA.PostAsJsonAsync("/attendance/check-in", new { customerId = customerA });

        Assert.Equal(HttpStatusCode.NotFound, (await receptionA.PostAsJsonAsync("/attendance/check-in", new { customerId = customerB })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await receptionA.PostAsJsonAsync("/attendance/check-out", new { customerId = customerB })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await receptionA.GetAsync($"/customers/{customerB}/attendance")).StatusCode);

        var listA = await Json(await receptionA.GetAsync("/attendance"));
        Assert.Equal(1, listA.GetProperty("total").GetInt32());
        Assert.Equal(customerA, listA.GetProperty("items")[0].GetProperty("customerId").GetGuid());
        Assert.Equal(0, (await Json(await receptionA.GetAsync($"/attendance?customerId={customerB}"))).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Json(await receptionB.GetAsync("/attendance"))).GetProperty("total").GetInt32()); // B's entry is untouched
    }

    [SkippableFact]
    public async Task Anonymous_and_platform_users_are_kept_out()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var anonymous = App().CreateClient();
        var platformName = "root-" + Guid.NewGuid().ToString("N")[..8];
        await db.ScalarAsync(
            "INSERT INTO users (tenant_id, username, password_hash, display_name, role) VALUES (NULL, @u, @h, 'Root', 'platform_admin') RETURNING 1",
            ("u", platformName), ("h", Hasher.Hash(Password)));
        var platform = await Login("platform", platformName);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/attendance")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/attendance/check-in", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await platform.GetAsync("/attendance")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await platform.PostAsJsonAsync("/attendance/check-in", new { })).StatusCode);
    }

    // ------------------------------------------------------------------------------------------ database rules

    [SkippableFact]
    public async Task The_database_itself_refuses_two_open_entries_orphan_reasons_and_backwards_exits()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var (gym, _, reception) = await Staffed();
        var customer = await CreateCustomer(reception);
        var now = DateTimeOffset.UtcNow;

        await InsertEntry(gym, customer, now, checkOut: null);
        var twoOpen = await Assert.ThrowsAsync<PostgresException>(() => InsertEntry(gym, customer, now.AddMinutes(1), checkOut: null));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, twoOpen.SqlState);

        await InsertEntry(gym, customer, now.AddMinutes(2), checkOut: null, result: "denied", reason: "closed"); // denied ones never count as open
        var reasonOnGranted = await Assert.ThrowsAsync<PostgresException>(() => InsertEntry(gym, customer, now.AddMinutes(3), now.AddMinutes(4), reason: "closed"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, reasonOnGranted.SqlState);
        var backwards = await Assert.ThrowsAsync<PostgresException>(() => InsertEntry(gym, customer, now.AddMinutes(10), now.AddMinutes(5)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, backwards.SqlState);
    }
}
