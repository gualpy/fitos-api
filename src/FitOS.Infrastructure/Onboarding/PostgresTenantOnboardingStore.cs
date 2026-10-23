using System.Text.Json;
using FitOS.Application.Onboarding;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace FitOS.Infrastructure.Onboarding;

/// <summary>
/// Creates a whole gym in ONE transaction. Row-level security stays on: the transaction pins
/// <c>app.tenant_id</c> to the NEW tenant (transaction-local), so every insert is accepted for that tenant
/// and for no other. Any failure rolls everything back.
/// </summary>
public sealed class PostgresTenantOnboardingStore(FitOsDbContext db) : ITenantOnboardingStore
{
    public async Task<Guid> CreateAsync(NewTenant tenant, Guid actorId, CancellationToken cancellationToken)
    {
        var t = tenant;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var npgsqlTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

        async Task<object?> Run(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var command = new NpgsqlCommand(sql, connection, npgsqlTransaction);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            return await command.ExecuteScalarAsync(cancellationToken);
        }

        try
        {
            await Run("SELECT set_config('app.tenant_id', @t, true)", ("t", t.TenantId.ToString()));

            await Run(
                "INSERT INTO tenants (id, name, slug, status, subscription_ends_on) VALUES (@id, @name, @slug, 'trial', @ends)",
                ("id", t.TenantId), ("name", t.Name), ("slug", t.Slug), ("ends", t.SubscriptionEndsOn));

            await Run("INSERT INTO tenant_settings (tenant_id) VALUES (@id)", ("id", t.TenantId));

            await Run(
                """
                INSERT INTO tenant_fiscal_config
                  (tenant_id, ruc, legal_name, trade_name, head_office_address, regime, accounting_required,
                   environment, provider, provider_company_id, provider_api_key_enc, provider_api_key_hint)
                VALUES (@id, @ruc, @legal, @trade, @address, @regime, @accounting, @env, 'sicofac', @company, @enc, @hint)
                """,
                ("id", t.TenantId), ("ruc", t.Ruc), ("legal", t.LegalName), ("trade", t.TradeName),
                ("address", t.HeadOfficeAddress), ("regime", t.Regime), ("accounting", t.AccountingRequired),
                ("env", t.Environment), ("company", t.ProviderCompanyId), ("enc", t.EncryptedApiKey), ("hint", t.ApiKeyHint));

            var branchId = (Guid)(await Run(
                "INSERT INTO branches (tenant_id, name, address, sri_establishment) VALUES (@id, 'Matriz', @address, @est) RETURNING id",
                ("id", t.TenantId), ("address", t.HeadOfficeAddress), ("est", t.SriEstablishment)))!;

            await Run(
                "INSERT INTO emission_points (tenant_id, branch_id, code) VALUES (@id, @branch, @code)",
                ("id", t.TenantId), ("branch", branchId), ("code", t.EmissionPointCode));

            var ownerId = (Guid)(await Run(
                """
                INSERT INTO users (tenant_id, username, password_hash, display_name, role, must_change_password, email)
                VALUES (@id, @user, @hash, @name, 'owner', true, @email) RETURNING id
                """,
                ("id", t.TenantId), ("user", t.OwnerUsername), ("hash", t.OwnerPasswordHash), ("name", t.OwnerDisplayName), ("email", t.OwnerEmail)))!;

            // The audit entry never contains secrets: no API key, no password, no hash.
            var after = JsonSerializer.Serialize(new
            {
                t.Slug,
                t.Ruc,
                t.Environment,
                t.ProviderCompanyId,
                OwnerUsername = t.OwnerUsername,
            });
            var audit = new NpgsqlCommand(
                "INSERT INTO audit_log (tenant_id, user_id, action, entity, entity_id, after) VALUES (@id, @actor, 'tenant.onboarded', 'tenant', @entity, @after)",
                connection,
                npgsqlTransaction);
            await using (audit)
            {
                audit.Parameters.AddWithValue("id", t.TenantId);
                audit.Parameters.AddWithValue("actor", actorId);
                audit.Parameters.AddWithValue("entity", t.TenantId.ToString());
                audit.Parameters.Add(new NpgsqlParameter("after", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = after });
                await audit.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return ownerId;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw ex.ConstraintName switch
            {
                "tenants_slug_key" => new OnboardingConflictException("slug"),
                "tenant_fiscal_config_ruc_key" => new OnboardingConflictException("ruc"),
                _ => ex,
            };
        }
    }
}
