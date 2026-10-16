using System.Security.Claims;
using FitOS.Api.Tenancy;
using FitOS.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;

namespace FitOS.IntegrationTests.Tenancy;

public sealed class TenantMiddlewareTests
{
    private static async Task<TenantContext> Run(ClaimsPrincipal user)
    {
        var tenant = new TenantContext();
        var middleware = new TenantMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(new DefaultHttpContext { User = user }, tenant);
        return tenant;
    }

    private static ClaimsPrincipal Authenticated(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "test"));

    [Fact]
    public async Task Sets_tenant_from_the_validated_claim()
    {
        var id = Guid.NewGuid();

        var tenant = await Run(Authenticated(new Claim(TenantMiddleware.ClaimType, id.ToString())));

        Assert.Equal(id, tenant.TenantId);
    }

    [Fact]
    public async Task Token_without_the_claim_leaves_no_tenant()
    {
        Assert.Null((await Run(Authenticated(new Claim("sub", "platform-admin")))).TenantId);
    }

    [Fact]
    public async Task Malformed_claim_leaves_no_tenant()
    {
        Assert.Null((await Run(Authenticated(new Claim(TenantMiddleware.ClaimType, "not-a-guid")))).TenantId);
    }

    [Fact]
    public async Task Unauthenticated_request_never_gets_a_tenant_even_with_the_claim()
    {
        var spoofed = new ClaimsPrincipal(new ClaimsIdentity([new Claim(TenantMiddleware.ClaimType, Guid.NewGuid().ToString())]));

        Assert.Null((await Run(spoofed)).TenantId);
    }
}
