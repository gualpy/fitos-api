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
        string? email = null,
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
            INSERT INTO users (tenant_id, username, password_hash, display_name, role, must_change_password, email)
            VALUES (NULL, @username, @hash, @name, 'platform_admin', true, @email)
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue("username", username);
        insert.Parameters.AddWithValue("hash", passwords.Hash(temporaryPassword));
        insert.Parameters.AddWithValue("name", displayName);
        insert.Parameters.AddWithValue("email", (object?)email ?? DBNull.Value);

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

    /// <summary>Sets the e-mail the confirmation link of a platform administrator is sent to.</summary>
    /// <returns><c>false</c> if there is no such platform administrator.</returns>
    public static async Task<bool> SetEmailAsync(string connectionString, string username, string email, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var role = new NpgsqlCommand("SET LOCAL ROLE fitos_platform", connection, transaction))
        {
            await role.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var update = new NpgsqlCommand(
            "UPDATE users SET email = @email WHERE tenant_id IS NULL AND role = 'platform_admin' AND username = @username",
            connection,
            transaction);
        update.Parameters.AddWithValue("email", email);
        update.Parameters.AddWithValue("username", username);

        var changed = await update.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    /// <summary>
    /// Deletes the mail settings saved from the panel, so the environment's (Email__*) take over. For when a wrong
    /// configuration has to be undone without the panel.
    /// </summary>
    /// <returns><c>true</c> if there was something to delete.</returns>
    public static async Task<bool> ClearEmailSettingsAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var role = new NpgsqlCommand("SET LOCAL ROLE fitos_platform", connection, transaction))
        {
            await role.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var delete = new NpgsqlCommand("DELETE FROM email_settings", connection, transaction);
        var removed = await delete.ExecuteNonQueryAsync(cancellationToken) > 0;
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }
}
