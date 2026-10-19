using System.ComponentModel.DataAnnotations.Schema;
using FitOS.Application.Abstractions;
using FitOS.Application.Settings;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitOS.Infrastructure.Settings;

public sealed class SettingsRepository(FitOsDbContext db, ITenantContext tenant) : ISettingsRepository
{
    public async Task<GymSettings> LoadAsync(CancellationToken cancellationToken)
    {
        var settings = (await db.Database
            .SqlQuery<SettingsRow>($"SELECT allow_without_plan, timezone FROM tenant_settings")
            .ToListAsync(cancellationToken)).FirstOrDefault();

        var branch = (await db.Database
            .SqlQuery<BranchRow>($"SELECT id, name FROM branches WHERE is_active ORDER BY name, id LIMIT 1")
            .ToListAsync(cancellationToken)).FirstOrDefault();

        var stored = branch is null
            ? []
            : await db.Database
                .SqlQuery<HoursRow>($"SELECT weekday, is_open, open_time, close_time FROM business_hours WHERE branch_id = {branch.Id}")
                .ToListAsync(cancellationToken);

        // Hours never set do not restrict access (see AccessPolicy), so they are shown as open all day.
        var days = Enumerable.Range(1, 7)
            .Select(weekday => stored.FirstOrDefault(h => h.Weekday == weekday) is { } row
                ? new HoursDay(weekday, true, row.IsOpen, row.OpenTime, row.CloseTime)
                : new HoursDay(weekday, false, true, TimeOnly.MinValue, new TimeOnly(23, 59)))
            .ToList();

        return new GymSettings(settings?.AllowWithoutPlan ?? false, settings?.Timezone ?? string.Empty, branch?.Id, branch?.Name, days);
    }

    public async Task SaveAsync(bool allowWithoutPlan, IReadOnlyList<HoursDay>? hours, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("No tenant in scope.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO tenant_settings (tenant_id, allow_without_plan) VALUES ({tenantId}, {allowWithoutPlan}) ON CONFLICT (tenant_id) DO UPDATE SET allow_without_plan = EXCLUDED.allow_without_plan",
            cancellationToken);

        if (hours is not null)
        {
            var branchId = (await db.Database
                .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM branches WHERE is_active ORDER BY name, id LIMIT 1")
                .ToListAsync(cancellationToken)).FirstOrDefault();

            if (branchId != Guid.Empty)
            {
                foreach (var day in hours)
                {
                    await db.Database.ExecuteSqlAsync(
                        $"""
                        INSERT INTO business_hours (tenant_id, branch_id, weekday, is_open, open_time, close_time)
                        VALUES ({tenantId}, {branchId}, {day.Weekday}, {day.IsOpen}, {day.Open}, {day.Close})
                        ON CONFLICT (tenant_id, branch_id, weekday)
                        DO UPDATE SET is_open = EXCLUDED.is_open, open_time = EXCLUDED.open_time, close_time = EXCLUDED.close_time
                        """,
                        cancellationToken);
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private sealed class SettingsRow
    {
        [Column("allow_without_plan")]
        public bool AllowWithoutPlan { get; set; }

        [Column("timezone")]
        public string Timezone { get; set; } = string.Empty;
    }

    private sealed class BranchRow
    {
        [Column("id")]
        public Guid Id { get; set; }

        [Column("name")]
        public string Name { get; set; } = string.Empty;
    }

    private sealed class HoursRow
    {
        [Column("weekday")]
        public short Weekday { get; set; }

        [Column("is_open")]
        public bool IsOpen { get; set; }

        [Column("open_time")]
        public TimeOnly OpenTime { get; set; }

        [Column("close_time")]
        public TimeOnly CloseTime { get; set; }
    }
}
