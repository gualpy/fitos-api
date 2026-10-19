using System.Globalization;
using FitOS.Application.Common;

namespace FitOS.Application.Settings;

/// <summary>One weekday of the branch's opening hours. <see cref="Configured"/> is false when the gym never set that day.</summary>
public sealed record HoursDay(int Weekday, bool Configured, bool IsOpen, TimeOnly Open, TimeOnly Close);

public sealed record GymSettings(bool AllowWithoutPlan, string Timezone, Guid? BranchId, string? BranchName, IReadOnlyList<HoursDay> Hours);

public sealed record HoursInput(int? Weekday, bool? IsOpen, string? OpenTime, string? CloseTime);

public sealed record SettingsInput(bool? AllowWithoutPlan, IReadOnlyList<HoursInput>? Hours);

public interface ISettingsRepository
{
    /// <summary>Settings of the current gym with the weekly hours of its first active branch (days never set come back unconfigured).</summary>
    Task<GymSettings> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Writes the switch and, when given, the seven days of the branch in one transaction.</summary>
    Task SaveAsync(bool allowWithoutPlan, IReadOnlyList<HoursDay>? hours, CancellationToken cancellationToken);
}

public sealed class SettingsService(ISettingsRepository settings)
{
    public Task<GymSettings> GetAsync(CancellationToken cancellationToken) => settings.LoadAsync(cancellationToken);

    public async Task<GymSettings> UpdateAsync(SettingsInput input, CancellationToken cancellationToken)
    {
        var errors = new ErrorBag();
        if (input.AllowWithoutPlan is null)
        {
            errors.Add("allowWithoutPlan", "required");
        }

        var days = input.Hours is null ? null : ParseHours(input.Hours, errors);
        errors.ThrowIfAny();

        await settings.SaveAsync(input.AllowWithoutPlan!.Value, days, cancellationToken);
        return await settings.LoadAsync(cancellationToken);
    }

    private static List<HoursDay>? ParseHours(IReadOnlyList<HoursInput> hours, ErrorBag errors)
    {
        var parsed = new List<HoursDay>();
        var seen = new HashSet<int>();

        foreach (var item in hours)
        {
            if (item.Weekday is not (>= 1 and <= 7) || !seen.Add(item.Weekday.Value))
            {
                errors.Add("hours", "invalid");
                return null;
            }

            var isOpen = item.IsOpen ?? false;
            var open = ParseTime(item.OpenTime);
            var close = ParseTime(item.CloseTime);
            if (open is null || close is null)
            {
                errors.Add("hours", "invalid");
                return null;
            }

            // [open, close): a day that opens must close later, or nobody could ever come in.
            if (isOpen && close <= open)
            {
                errors.Add("hours", "close_before_open");
                return null;
            }

            parsed.Add(new HoursDay(item.Weekday.Value, true, isOpen, open.Value, close.Value));
        }

        if (parsed.Count != 7)
        {
            errors.Add("hours", "seven_days_required");
            return null;
        }

        return parsed;
    }

    private static TimeOnly? ParseTime(string? text) =>
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;
}
