using FitOS.Api.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace FitOS.IntegrationTests.Tenancy;

public sealed class TenantHostResolverTests
{
    private static TenantTarget Resolve(string host, string? baseDomain = "fitos.com", bool allowHeader = false, string? header = null, bool allowLoginHeader = false, string path = TenantHostResolver.LoginPath)
    {
        var resolver = new TenantHostResolver(Options.Create(new TenancyOptions
        {
            BaseDomain = baseDomain,
            AllowTenantHeader = allowHeader,
            AllowLoginTenantHeader = allowLoginHeader,
        }));
        var request = new DefaultHttpContext().Request;
        request.Host = new HostString(host);
        request.Path = path;
        if (header is not null)
        {
            request.Headers[TenantHostResolver.TenantHeader] = header;
        }

        return resolver.Resolve(request);
    }

    [Theory]
    [InlineData("titan.fitos.com", "titan")]
    [InlineData("TITAN.FITOS.COM", "titan")]
    [InlineData("gym-2.fitos.com:443", "gym-2")]
    public void Subdomain_is_the_tenant_slug(string host, string slug)
    {
        var target = Resolve(host);

        Assert.Equal(TenantTargetKind.Tenant, target.Kind);
        Assert.Equal(slug, target.Slug);
    }

    [Theory]
    [InlineData("fitos.com")]
    [InlineData("admin.fitos.com")]
    public void Apex_and_admin_host_are_the_platform(string host) =>
        Assert.Equal(TenantTargetKind.Platform, Resolve(host).Kind);

    [Theory]
    [InlineData("evil.com")]
    [InlineData("titan.fitos.com.evil.com")]
    [InlineData("notfitos.com")]
    [InlineData("a.b.fitos.com")]
    [InlineData("-bad.fitos.com")]
    [InlineData("www.fitos.com")]
    [InlineData("api.fitos.com")]
    public void Foreign_nested_malformed_or_reserved_hosts_are_invalid(string host) =>
        Assert.Equal(TenantTargetKind.Invalid, Resolve(host).Kind);

    [Fact]
    public void Without_a_base_domain_nothing_resolves()
    {
        Assert.Equal(TenantTargetKind.Invalid, Resolve("titan.fitos.com", baseDomain: null).Kind);
    }

    [Fact]
    public void Header_is_ignored_unless_explicitly_allowed()
    {
        var target = Resolve("evil.com", allowHeader: false, header: "titan");

        Assert.Equal(TenantTargetKind.Invalid, target.Kind);
    }

    [Theory]
    [InlineData("titan", TenantTargetKind.Tenant)]
    [InlineData("platform", TenantTargetKind.Platform)]
    [InlineData("www", TenantTargetKind.Invalid)]
    [InlineData("Bad Slug!", TenantTargetKind.Invalid)]
    [InlineData("", TenantTargetKind.Invalid)]
    public void Allowed_header_selects_the_tenant(string header, TenantTargetKind kind) =>
        Assert.Equal(kind, Resolve("localhost", allowHeader: true, header: header).Kind);

    [Fact]
    public void Login_header_selects_the_tenant_on_a_single_domain()
    {
        var target = Resolve("fitos.com", allowLoginHeader: true, header: " Titan ");

        Assert.Equal(TenantTargetKind.Tenant, target.Kind);
        Assert.Equal("titan", target.Slug);
    }

    [Fact]
    public void Login_header_is_ignored_when_the_option_is_off() =>
        Assert.Equal(TenantTargetKind.Platform, Resolve("fitos.com", allowLoginHeader: false, header: "titan").Kind);

    [Theory]
    [InlineData("/customers")]
    [InlineData("/auth/refresh")]
    [InlineData("/auth/login/extra")]
    public void Login_header_is_ignored_on_other_endpoints(string path) =>
        Assert.Equal(TenantTargetKind.Platform, Resolve("fitos.com", allowLoginHeader: true, header: "titan", path: path).Kind);

    [Theory]
    [InlineData("www")]
    [InlineData("api")]
    [InlineData("Bad Slug!")]
    [InlineData("a.b")]
    [InlineData("")]
    public void Login_header_with_a_reserved_or_malformed_slug_is_rejected(string header) =>
        Assert.Equal(TenantTargetKind.Invalid, Resolve("fitos.com", allowLoginHeader: true, header: header).Kind);

    [Fact]
    public void Login_without_header_still_resolves_by_host_when_the_option_is_on()
    {
        Assert.Equal(TenantTargetKind.Platform, Resolve("fitos.com", allowLoginHeader: true).Kind);
        Assert.Equal("titan", Resolve("titan.fitos.com", allowLoginHeader: true).Slug);
    }

    [Fact]
    public void Both_options_on_resolve_the_header_everywhere_without_conflict()
    {
        Assert.Equal("titan", Resolve("localhost", allowHeader: true, allowLoginHeader: true, header: "titan", path: "/customers").Slug);
        Assert.Equal("titan", Resolve("localhost", allowHeader: true, allowLoginHeader: true, header: "titan").Slug);
    }
}
