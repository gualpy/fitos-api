using FitOS.Application.Auth;
using FitOS.Domain.Memberships;
using Npgsql;

namespace FitOS.Migrator;

public sealed record SeedOptions(
    string TenantSlug,
    string Username,
    string DisplayName,
    string Password,
    int Days = 60,
    string PlanName = "Plan Mensual");

public sealed record SeedResult(bool CustomerCreated, int MembershipsCreated, int AttendanceCreated);

/// <summary>
/// DEVELOPMENT ONLY. Gives one customer a real login, a membership covering the last <c>Days</c> days and one attendance per day the
/// gym was open (according to its business hours in the database). Everything is written to the database, nothing lives in app code.
/// Safe to re-run: it only adds what is missing.
/// Runs as the schema owner with <c>SET LOCAL ROLE fitos_platform</c> (it must see every tenant before choosing one).
/// </summary>
public static class DevSeeder
{
    // Default weekly hours written when the branch has none: Mon-Fri 06:00-22:00, Sat 08:00-18:00, Sun closed.
    private const string DefaultHoursSql = """
        INSERT INTO business_hours (tenant_id, branch_id, weekday, is_open, open_time, close_time)
        SELECT @t, @b, d, d <> 7,
               CASE WHEN d = 7 THEN '00:00'::time WHEN d = 6 THEN '08:00'::time ELSE '06:00'::time END,
               CASE WHEN d = 7 THEN '00:00'::time WHEN d = 6 THEN '18:00'::time ELSE '22:00'::time END
        FROM generate_series(1, 7) AS d
        ON CONFLICT (tenant_id, branch_id, weekday) DO NOTHING
        """;

    // One scan per open day. Times vary a little per day of the year but are deterministic, so re-runs agree.
    private const string AttendanceSql = """
        WITH days AS (
          SELECT d::date AS day FROM generate_series(@from::date, @to::date, interval '1 day') AS d
        ), planned AS (
          SELECT days.day,
                 (days.day + bh.open_time + make_interval(mins => ((extract(doy FROM days.day)::int * 7) % 8) * 15)) AT TIME ZONE @tz AS check_in,
                 (days.day + bh.open_time + make_interval(mins => ((extract(doy FROM days.day)::int * 7) % 8) * 15 + 60 + ((extract(doy FROM days.day)::int * 5) % 4) * 15)) AT TIME ZONE @tz AS check_out
          FROM days
          JOIN business_hours bh ON bh.tenant_id = @t AND bh.branch_id = @b AND bh.is_open
                                AND bh.weekday = extract(isodow FROM days.day)::int
        )
        INSERT INTO attendance (tenant_id, branch_id, customer_id, check_in, check_out, result)
        SELECT @t, @b, @c, p.check_in, CASE WHEN p.check_out <= now() THEN p.check_out END, 'granted'
        FROM planned p
        WHERE p.check_in <= now()
          AND NOT EXISTS (SELECT 1 FROM attendance a
                          WHERE a.tenant_id = @t AND a.customer_id = @c AND (a.check_in AT TIME ZONE @tz)::date = p.day)
        """;

    public static async Task<SeedResult> SeedAsync(
        string connectionString,
        SeedOptions options,
        IPasswordService passwords,
        TimeProvider time,
        CancellationToken cancellationToken = default)
    {
        if (options.Days is < 1 or > 730)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Days must be between 1 and 730.");
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        async Task<T?> Scalar<T>(string sql, params (string Name, object? Value)[] args)
        {
            await using var command = new NpgsqlCommand(sql, connection, tx);
            foreach (var (name, value) in args)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is null or DBNull ? default : (T)result;
        }

        async Task<int> Run(string sql, params (string Name, object? Value)[] args)
        {
            await using var command = new NpgsqlCommand(sql, connection, tx);
            foreach (var (name, value) in args)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await Run("SET LOCAL ROLE fitos_platform");

        var tenantId = await Scalar<Guid?>("SELECT id FROM tenants WHERE slug = @slug", ("slug", options.TenantSlug))
            ?? throw new InvalidOperationException($"No gym with slug '{options.TenantSlug}'. Create it first (POST /platform/tenants).");
        var zoneId = await Scalar<string>("SELECT timezone FROM tenant_settings WHERE tenant_id = @t", ("t", tenantId)) ?? "America/Guayaquil";
        var zone = FindZone(zoneId);
        var branchId = await Scalar<Guid?>("SELECT id FROM branches WHERE tenant_id = @t AND is_active ORDER BY name, id LIMIT 1", ("t", tenantId))
            ?? throw new InvalidOperationException("The gym has no active branch.");

        await Run(DefaultHoursSql, ("t", tenantId), ("b", branchId));

        var planId = await Scalar<Guid?>("SELECT id FROM plans WHERE tenant_id = @t AND lower(name) = lower(@n)", ("t", tenantId), ("n", options.PlanName));
        var planMonths = 1;
        if (planId is null)
        {
            planId = await Scalar<Guid>(
                "INSERT INTO plans (tenant_id, name, price, iva_rate, duration_months) VALUES (@t, @n, 40, 15, 1) RETURNING id",
                ("t", tenantId), ("n", options.PlanName));
        }
        else
        {
            planMonths = Convert.ToInt32(await Scalar<object>("SELECT duration_months FROM plans WHERE id = @p", ("p", planId))); // smallint column
        }

        var customerCreated = false;
        var existing = await Scalar<Guid?>("SELECT customer_id FROM users WHERE tenant_id = @t AND username = @u AND role = 'customer'", ("t", tenantId), ("u", options.Username));
        if (existing is null)
        {
            if (await Scalar<Guid?>("SELECT id FROM users WHERE tenant_id = @t AND username = @u", ("t", tenantId), ("u", options.Username)) is not null)
            {
                throw new InvalidOperationException($"'{options.Username}' already exists in that gym with a role other than customer.");
            }

            existing = await Scalar<Guid>("INSERT INTO customers (tenant_id, name) VALUES (@t, @n) RETURNING id", ("t", tenantId), ("n", options.DisplayName));
            await Run(
                "INSERT INTO users (tenant_id, username, password_hash, display_name, role, customer_id) VALUES (@t, @u, @h, @n, 'customer', @c)",
                ("t", tenantId), ("u", options.Username), ("h", passwords.Hash(options.Password)), ("n", options.DisplayName), ("c", existing));
            customerCreated = true;
        }

        var customerId = existing.Value;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);
        var windowStart = today.AddDays(1 - options.Days);

        // Back-to-back memberships from the start of the window until today is covered (the database forbids overlaps).
        // ADO.NET hands a SQL date back as DateTime.
        var latestRaw = await Scalar<DateTime?>(
            "SELECT max(ends_on) FROM memberships WHERE tenant_id = @t AND customer_id = @c AND status <> 'cancelled'", ("t", tenantId), ("c", customerId));
        DateOnly? latest = latestRaw is { } raw ? DateOnly.FromDateTime(raw) : null;
        var memberships = 0;
        while (latest is null || latest < today)
        {
            var (start, end) = MembershipRules.Schedule(windowStart, null, latest, planMonths);
            await Run(
                "INSERT INTO memberships (tenant_id, customer_id, plan_id, starts_on, ends_on) VALUES (@t, @c, @p, @s, @e)",
                ("t", tenantId), ("c", customerId), ("p", planId), ("s", start), ("e", end));
            latest = end;
            memberships++;
        }

        var attendance = await Run(
            AttendanceSql,
            ("t", tenantId), ("b", branchId), ("c", customerId), ("tz", zone.Id), ("from", windowStart), ("to", today));

        await tx.CommitAsync(cancellationToken);
        return new SeedResult(customerCreated, memberships, attendance);
    }

    private static TimeZoneInfo FindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException($"Unknown time zone '{id}' in tenant_settings.", ex);
        }
    }
}
