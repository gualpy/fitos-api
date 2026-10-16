using System.Text.RegularExpressions;

namespace FitOS.Domain.Tenancy;

/// <summary>A gym's sub-domain label (<c>titan</c> in titan.fitos.com). Mirrors the CHECK on tenants.slug.</summary>
public static partial class TenantSlug
{
    /// <summary>Labels that can never be a gym: platform administration and infrastructure.</summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
    {
        "www", "admin", "api", "platform",
    };

    public static bool IsWellFormed(string? value) => value is not null && Pattern().IsMatch(value);

    public static bool IsAvailableForTenant(string? value) => IsWellFormed(value) && !Reserved.Contains(value!);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial Regex Pattern();
}
