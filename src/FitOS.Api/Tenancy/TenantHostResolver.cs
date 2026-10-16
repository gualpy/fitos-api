using FitOS.Domain.Tenancy;
using Microsoft.Extensions.Options;

namespace FitOS.Api.Tenancy;

public sealed class TenancyOptions
{
    public const string Section = "Tenancy";

    /// <summary>Apex domain, e.g. "fitos.com": gym "titan" lives at titan.fitos.com.</summary>
    public string? BaseDomain { get; init; }

    /// <summary>DEVELOPMENT ONLY: take the tenant from the X-Tenant-Slug header. Refused to start outside Development.</summary>
    public bool AllowTenantHeader { get; init; }
}

public enum TenantTargetKind
{
    Invalid,
    Platform,
    Tenant,
}

public sealed record TenantTarget(TenantTargetKind Kind, string? Slug = null)
{
    public static readonly TenantTarget Invalid = new(TenantTargetKind.Invalid);

    public static readonly TenantTarget Platform = new(TenantTargetKind.Platform);
}

/// <summary>
/// Decides which tenant a PRE-authentication request (login) is for, from the host name only.
/// After login the tenant comes from the JWT claim, never from here.
/// </summary>
public sealed class TenantHostResolver(IOptions<TenancyOptions> options)
{
    public const string TenantHeader = "X-Tenant-Slug";

    public TenantTarget Resolve(HttpRequest request)
    {
        var settings = options.Value;

        if (settings.AllowTenantHeader && request.Headers.TryGetValue(TenantHeader, out var header))
        {
            return FromLabel(header.ToString().Trim().ToLowerInvariant());
        }

        var baseDomain = settings.BaseDomain?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(baseDomain))
        {
            return TenantTarget.Invalid;
        }

        var host = request.Host.Host.ToLowerInvariant();
        if (host == baseDomain)
        {
            return TenantTarget.Platform;
        }

        return host.EndsWith("." + baseDomain, StringComparison.Ordinal)
            ? FromLabel(host[..^(baseDomain.Length + 1)])
            : TenantTarget.Invalid;
    }

    private static TenantTarget FromLabel(string label)
    {
        if (!TenantSlug.IsWellFormed(label))
        {
            return TenantTarget.Invalid;
        }

        return label is "admin" or "platform"
            ? TenantTarget.Platform
            : TenantSlug.Reserved.Contains(label) ? TenantTarget.Invalid : new TenantTarget(TenantTargetKind.Tenant, label);
    }
}
