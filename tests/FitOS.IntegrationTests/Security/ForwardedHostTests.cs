using System.Net;
using System.Net.Http.Json;
using FitOS.Infrastructure.Auth;
using FitOS.IntegrationTests.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FitOS.IntegrationTests.Security;

/// <summary>The gym is chosen by host name: a proxy header must not let a stranger pick someone else's gym.</summary>
public sealed class ForwardedHostTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private const string ProxyIp = "203.0.113.9";

    private async Task<string> GymWithUser()
    {
        var id = Guid.NewGuid();
        var slug = "f" + id.ToString("N")[..10];
        await db.ScalarAsync("INSERT INTO tenants (id, name, slug) VALUES (@id, 'Gym', @slug) RETURNING 1", ("id", id), ("slug", slug));
        await db.ScalarAsync(
            "INSERT INTO users (tenant_id, username, password_hash, display_name, role) VALUES (@t, 'ana', @h, 'Ana', 'admin') RETURNING 1",
            ("t", id), ("h", new IdentityPasswordService(1000).Hash("Correct-Horse-9!")));
        return slug;
    }

    private HttpClient Client(bool trustProxy)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Production"); // the tenant header is a development-only shortcut
            host.UseTestSecrets();
            host.UseSetting("ConnectionStrings:Default", db.AppConnection);
            host.UseSetting("Tenancy:BaseDomain", "fitos.test");
            if (trustProxy)
            {
                host.UseSetting("Proxy:KnownProxies:0", ProxyIp);
            }

            host.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new RemoteIp(IPAddress.Parse(ProxyIp))));
        });
        return factory.CreateClient();
    }

    private sealed class RemoteIp(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, pipeline) =>
            {
                context.Connection.RemoteIpAddress = address;
                return pipeline(context);
            });
            next(app);
        };
    }

    private static async Task<HttpResponseMessage> Login(HttpClient client, string host, string? forwardedHost)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login") { Content = JsonContent.Create(new { username = "ana", password = "Correct-Horse-9!" }) };
        request.Headers.Host = host;
        if (forwardedHost is not null)
        {
            request.Headers.Add("X-Forwarded-Host", forwardedHost);
        }

        return await client.SendAsync(request);
    }

    [SkippableFact]
    public async Task A_forged_forwarded_host_cannot_select_a_gym_unless_it_comes_from_the_trusted_proxy()
    {
        Skip.IfNot(PostgresFixture.Available, "FITOS_TEST_PG not set");
        var slug = await GymWithUser();

        using var open = Client(trustProxy: false);
        var forged = await Login(open, "evil.example", $"{slug}.fitos.test");
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode); // "evil.example" is not under our domain; the header is ignored

        using var behindProxy = Client(trustProxy: true);
        var genuine = await Login(behindProxy, "api.internal", $"{slug}.fitos.test");
        Assert.Equal(HttpStatusCode.OK, genuine.StatusCode); // the trusted proxy says which gym was asked for

        // And a plain request to the gym's own host works either way.
        Assert.Equal(HttpStatusCode.OK, (await Login(open, $"{slug}.fitos.test", null)).StatusCode);
    }
}
