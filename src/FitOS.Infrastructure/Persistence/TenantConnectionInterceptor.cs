using System.Data.Common;
using FitOS.Application.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FitOS.Infrastructure.Persistence;

/// <summary>
/// Pins <c>app.tenant_id</c> on every connection EF opens so PostgreSQL row-level security applies.
/// With no tenant it sets an empty value, which makes every tenant table return zero rows.
/// The setting is session-scoped; Npgsql resets session state when a connection returns to the pool
/// (verified by TenantIsolationTests.Pooled_connection_does_not_leak_tenant).
/// </summary>
public sealed class TenantConnectionInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    private const string Sql = "SELECT set_config('app.tenant_id', @tenant, false)";

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = Build(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = Build(connection);
        command.ExecuteNonQuery();
    }

    private DbCommand Build(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = Sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenant";
        parameter.Value = tenant.TenantId?.ToString() ?? string.Empty;
        command.Parameters.Add(parameter);
        return command;
    }
}
