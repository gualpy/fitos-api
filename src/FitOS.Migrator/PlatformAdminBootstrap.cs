using FitOS.Application.Auth;
using Npgsql;

namespace FitOS.Migrator;

public static class PlatformAdminBootstrap
{
    /// <summary>
    /// Creates the first (or another) platform administrator with a temporary password they must change at first login.
    /// Runs as the schema owner; <c>SET LOCAL ROLE fitos_platform</c> (BYPASSRLS) is needed because platform users
    /// have no tenant and the row-level policy would otherwise refuse the insert.
    /// </summary>
    /// <returns><c>false</c> if the username already exists.</returns>
    public static async Task<bool> CreateAsync(
        string connectionString,
        string username,
        string displayName,
        string temporaryPassword,
        IPasswordService passwords,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var role = new NpgsqlCommand("SET LOCAL ROLE fitos_platform", connection, transaction))
        {
            await role.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO users (tenant_id, username, password_hash, display_name, role, must_change_password)
            VALUES (NULL, @username, @hash, @name, 'platform_admin', true)
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue("username", username);
        insert.Parameters.AddWithValue("hash", passwords.Hash(temporaryPassword));
        insert.Parameters.AddWithValue("name", displayName);

        try
        {
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
