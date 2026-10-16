using FitOS.Infrastructure.Persistence;
using FitOS.Migrator;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace FitOS.IntegrationTests.Tenancy;

/// <summary>
/// Runs the real SQL schema (RLS, triggers) in a throwaway database. Needs a PostgreSQL server:
/// set FITOS_TEST_PG to a superuser connection string, e.g.
/// "Host=127.0.0.1;Port=5432;Username=postgres;Password=...;Database=postgres". Skipped otherwise.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private static readonly string? AdminConnection = Environment.GetEnvironmentVariable("FITOS_TEST_PG");

    // Roles are cluster-wide: two fixtures migrating at once race on CREATE ROLE. Prepare one at a time.
    private static readonly SemaphoreSlim PrepareLock = new(1, 1);

    private string _database = string.Empty;

    public static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-00000000000a");

    public static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    public static bool Available => AdminConnection is not null;

    public string AppConnection { get; private set; } = string.Empty;

    /// <summary>Superuser connection to the throwaway database (bypasses RLS): for seeding and assertions.</summary>
    public string OwnerConnection { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (AdminConnection is null)
        {
            return;
        }

        await PrepareLock.WaitAsync();
        try
        {
            await PrepareAsync();
        }
        finally
        {
            PrepareLock.Release();
        }
    }

    private async Task PrepareAsync()
    {
        _database = $"fitos_test_{Guid.NewGuid():N}";
        await Exec(AdminConnection!, $"CREATE DATABASE \"{_database}\"");

        var admin = new NpgsqlConnectionStringBuilder(AdminConnection!) { Database = _database }.ConnectionString;
        Assert.True(MigrationRunner.Run(admin), "migrations failed");

        OwnerConnection = admin;
        await Exec(admin, "ALTER ROLE fitos_app LOGIN PASSWORD 'test'");
        await Exec(admin, $"""
            INSERT INTO tenants (id, name, slug) VALUES ('{TenantA}', 'A', 'a'), ('{TenantB}', 'B', 'b');
            INSERT INTO customers (tenant_id, name) VALUES ('{TenantA}', 'Ana'), ('{TenantA}', 'Beto'), ('{TenantB}', 'Zoe');
            """);

        // Pool of ONE physical connection: any session state that leaked would be seen by the next user.
        AppConnection = new NpgsqlConnectionStringBuilder(admin)
        {
            Username = "fitos_app",
            Password = "test",
            MaxPoolSize = 1,
        }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (AdminConnection is null || _database.Length == 0)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await Exec(AdminConnection, $"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");
    }

    public FitOsDbContext Context(Guid? tenant)
    {
        var tenantContext = new TenantContext();
        if (tenant is { } id)
        {
            tenantContext.Set(id);
        }

        var options = new DbContextOptionsBuilder<FitOsDbContext>()
            .UseNpgsql(AppConnection)
            .AddInterceptors(new TenantConnectionInterceptor(tenantContext))
            .Options;
        return new FitOsDbContext(options);
    }

    public async Task<object?> ScalarAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var conn = new NpgsqlConnection(OwnerConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return await cmd.ExecuteScalarAsync();
    }

    public async Task<List<object?[]>> RowsAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var conn = new NpgsqlConnection(OwnerConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var rows = new List<object?[]>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static async Task Exec(string connection, string sql)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

public sealed class TenantIsolationTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private static Task<int> CountCustomers(FitOsDbContext context) =>
        context.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM customers").SingleAsync();

    [SkippableFact]
    public async Task Each_tenant_sees_only_its_own_rows()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");

        await using (var a = db.Context(PostgresFixture.TenantA))
        {
            Assert.Equal(2, await CountCustomers(a));
        }

        await using var b = db.Context(PostgresFixture.TenantB);
        Assert.Equal(1, await CountCustomers(b));
    }

    [SkippableFact]
    public async Task No_tenant_means_no_rows()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");

        await using var none = db.Context(null);

        Assert.Equal(0, await CountCustomers(none));
    }

    [SkippableFact]
    public async Task Cannot_write_rows_for_another_tenant()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");

        await using var a = db.Context(PostgresFixture.TenantA);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => a.Database.ExecuteSqlAsync(
            $"INSERT INTO customers (tenant_id, name) VALUES ({PostgresFixture.TenantB}, 'Intruso')"));
        Assert.Equal("42501", ex.SqlState); // row-level security violation
    }

    [SkippableFact]
    public async Task Pooled_connection_does_not_leak_tenant()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");

        await using (var a = db.Context(PostgresFixture.TenantA))
        {
            Assert.Equal(2, await CountCustomers(a)); // leaves tenant A pinned on the (only) pooled connection
        }

        // Same pool, no interceptor: raw code (workers, scripts) must NOT inherit tenant A.
        await using var raw = new NpgsqlConnection(db.AppConnection);
        await raw.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM customers", raw);
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }

    [SkippableFact]
    public async Task Client_codes_are_sequential_per_tenant_and_rollback_does_not_burn_one()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");

        await using (var a = db.Context(PostgresFixture.TenantA))
        {
            Assert.Equal([1L, 2L], await Codes(a));
        }

        await using (var b = db.Context(PostgresFixture.TenantB))
        {
            Assert.Equal([1L], await Codes(b));
        }

        // The next code continues the tenant's own sequence...
        await using var ctx = db.Context(PostgresFixture.TenantA);
        Assert.Equal(3L, await InsertAndRollback(ctx));

        // ...and since the counter increment rolled back with the insert, the code is reused, not skipped.
        Assert.Equal(3L, await InsertAndRollback(ctx));
    }

    private static Task<List<long>> Codes(FitOsDbContext context) =>
        context.Database.SqlQueryRaw<long>("SELECT client_code AS \"Value\" FROM customers ORDER BY client_code").ToListAsync();

    private static async Task<long> InsertAndRollback(FitOsDbContext context)
    {
        await using var transaction = await context.Database.BeginTransactionAsync();
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await using var command = new NpgsqlCommand(
            "INSERT INTO customers (tenant_id, name) VALUES (current_setting('app.tenant_id')::uuid, 'Carla') RETURNING client_code",
            connection,
            (NpgsqlTransaction)transaction.GetDbTransaction());
        var code = (long)(await command.ExecuteScalarAsync())!;
        await transaction.RollbackAsync();
        return code;
    }
}
