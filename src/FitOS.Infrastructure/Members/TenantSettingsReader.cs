using System.ComponentModel.DataAnnotations.Schema;
using FitOS.Application.Common;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitOS.Infrastructure.Members;

/// <summary>Reads tenant_settings once per request (the row of the current tenant, thanks to row-level security).</summary>
public sealed class TenantSettingsReader(FitOsDbContext db)
{
    private const string DefaultZone = "America/Guayaquil";

    // Mainland Ecuador is UTC-5 all year (no DST): a safe fallback where the OS has no tz database (minimal containers).
    private static readonly TimeZoneInfo Ecuador = TimeZoneInfo.CreateCustomTimeZone("EC", TimeSpan.FromHours(-5), "Ecuador", "Ecuador");

    private (TimeZoneInfo Zone, bool AllowWithoutPlan)? _loaded;

    public async Task<TimeZoneInfo> ZoneAsync(CancellationToken cancellationToken) => (await LoadAsync(cancellationToken)).Zone;

    public async Task<bool> AllowWithoutPlanAsync(CancellationToken cancellationToken) => (await LoadAsync(cancellationToken)).AllowWithoutPlan;

    private async Task<(TimeZoneInfo Zone, bool AllowWithoutPlan)> LoadAsync(CancellationToken cancellationToken)
    {
        if (_loaded is { } cached)
        {
            return cached;
        }

        var rows = await db.Database
            .SqlQuery<SettingsRow>($"SELECT timezone, allow_without_plan FROM tenant_settings")
            .ToListAsync(cancellationToken);
        var row = rows.FirstOrDefault();

        var zone = Find(row?.Timezone ?? DefaultZone) ?? Find(DefaultZone) ?? Ecuador;
        _loaded = (zone, row?.AllowWithoutPlan ?? false);
        return _loaded.Value;
    }

    private static TimeZoneInfo? Find(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    private sealed class SettingsRow
    {
        [Column("timezone")]
        public string? Timezone { get; set; }

        [Column("allow_without_plan")]
        public bool AllowWithoutPlan { get; set; }
    }
}

public sealed class TenantClock(TenantSettingsReader settings, TimeProvider time) : ITenantClock
{
    public Task<TimeZoneInfo> ZoneAsync(CancellationToken cancellationToken) => settings.ZoneAsync(cancellationToken);

    public async Task<DateOnly> TodayAsync(CancellationToken cancellationToken) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), await settings.ZoneAsync(cancellationToken)).DateTime);
}

public sealed class TenantSettings(TenantSettingsReader settings) : ITenantSettings
{
    public Task<bool> AllowWithoutPlanAsync(CancellationToken cancellationToken) => settings.AllowWithoutPlanAsync(cancellationToken);
}
