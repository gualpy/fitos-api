using System.Globalization;
using System.Text;
using FitOS.Application.Abstractions;
using FitOS.Application.Common;

namespace FitOS.Application.Dashboard;

public sealed record Insights(string Text, DateTimeOffset GeneratedAt);

/// <summary>No AI provider is configured on this server (no key). Not an error of the caller.</summary>
public sealed class InsightsUnavailableException() : Exception("Insights provider is not configured.");

/// <summary>The AI provider was configured but did not answer usefully. The message never carries provider details.</summary>
public sealed class InsightsFailedException(string message, Exception? inner = null) : Exception(message, inner);

public interface IInsightsProvider
{
    /// <summary>False when no key is configured; checked before anything is read or sent.</summary>
    bool IsConfigured { get; }

    /// <exception cref="InsightsFailedException"/>
    Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken);
}

/// <summary>Answers already generated, per gym, so a click costs one provider call per window, not one per visit.</summary>
public interface IInsightsCache
{
    Insights? Find(Guid tenantId);

    void Store(Guid tenantId, Insights insights);
}

public static class InsightsPrompt
{
    /// <summary>
    /// The text sent to the provider. Aggregates only: counts and totals, never a name, identification,
    /// e-mail or any other row-level data.
    /// </summary>
    public static string Build(DashboardSummary s)
    {
        var text = new StringBuilder();
        text.AppendLine("Analiza los siguientes datos agregados de un gimnasio y dame 3 consejos estratégicos cortos para mejorar el negocio.");
        text.AppendLine("Responde en español, con una lista numerada y sin introducción.");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Socios registrados: {s.TotalCustomers}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Socios con plan vigente: {s.ActiveMembers}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Planes que vencen en los próximos {s.ExpiringWithinDays} días (sin renovación): {s.ExpiringSoon}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Asistencia hoy: {s.AttendanceToday} socios distintos");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Asistencia de los últimos {s.Attendance.Count} días (más antiguo primero): {string.Join(", ", s.Attendance.Select(a => a.Count))}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Socios activos por género: {string.Join(", ", s.Genders.Select(g => $"{g.Gender ?? "sin indicar"}={g.Count}"))}");
        if (s.Sales is { } sales)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Ventas pagadas hoy: {sales.Today:0.00} USD; últimos 7 días: {sales.Last7Days:0.00} USD");
        }

        return text.ToString();
    }
}

public sealed class InsightsService(
    DashboardService dashboard,
    IInsightsProvider provider,
    IInsightsCache cache,
    ITenantContext tenant,
    TimeProvider time)
{
    /// <exception cref="InsightsUnavailableException"/>
    /// <exception cref="InsightsFailedException"/>
    public async Task<Insights> GetAsync(CancellationToken cancellationToken)
    {
        if (!provider.IsConfigured)
        {
            throw new InsightsUnavailableException();
        }

        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("No tenant in scope.");
        if (cache.Find(tenantId) is { } cached)
        {
            return cached;
        }

        // Owners and admins only reach this: they are the ones allowed to see the money figures.
        var summary = await dashboard.GetAsync(includeMoney: true, cancellationToken);
        var answer = (await provider.GenerateAsync(InsightsPrompt.Build(summary), cancellationToken)).Trim();
        if (answer.Length == 0)
        {
            throw new InsightsFailedException("The provider returned no text.");
        }

        var insights = new Insights(answer, time.GetUtcNow());
        cache.Store(tenantId, insights);
        return insights;
    }
}
