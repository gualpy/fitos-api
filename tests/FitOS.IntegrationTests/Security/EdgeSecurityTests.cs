using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FitOS.Application.Auth;
using FitOS.Domain.Tenancy;
using FitOS.Infrastructure.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FitOS.IntegrationTests.Security;

/// <summary>The API's outer shell: proxy trust, defensive headers, size and rate limits. None of this needs a database.</summary>
public sealed class EdgeSecurityTests : IDisposable
{
    private const string ProxyIp = "203.0.113.9";

    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    private sealed class RemoteIpFilter(IPAddress address) : IStartupFilter
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

    /// <summary>Production-like host (so appsettings.Development.json is not loaded) with chosen settings and a fixed peer address.</summary>
    private HttpClient Client(Dictionary<string, string?>? settings = null, string remoteIp = ProxyIp)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Production");
            host.UseProductionMailSettings();
            host.UseTestSecrets();
            foreach (var (key, value) in settings ?? [])
            {
                host.UseSetting(key, value);
            }

            host.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new RemoteIpFilter(IPAddress.Parse(remoteIp))));
        });
        _factories.Add(factory);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }); // the redirect itself is what we assert on
        client.DefaultRequestHeaders.Host = "api.example.test"; // not "localhost": HSTS skips loopback names
        return client;
    }

    private static string Token(Guid? user = null)
    {
        var record = new LoginRecord(user ?? Guid.NewGuid(), Guid.NewGuid(), string.Empty, UserRole.Admin, "N", null, true, false, "active");
        var issuer = new JwtAccessTokenIssuer(
            Options.Create(new JwtOptions { Issuer = "fitos", Audience = "fitos-api", SigningKey = TestSettings.JwtKey, AccessTokenMinutes = 15 }),
            TimeProvider.System);
        return issuer.Issue(record, Guid.NewGuid()).Value;
    }

    private static HttpRequestMessage Get(string path, string? token = null, string? forwardedFor = null, string? forwardedProto = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        if (forwardedProto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        }

        return request;
    }

    private static Dictionary<string, string?> Limit(int anonymous, int authenticated = 600) => new()
    {
        ["Security:AnonymousRequestsPerMinute"] = anonymous.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Security:AuthenticatedRequestsPerMinute"] = authenticated.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    // -------------------------------------------------------------------------------------------- headers

    [Fact]
    public async Task Every_response_carries_the_defensive_headers_even_errors()
    {
        var client = Client();

        foreach (var path in new[] { "/health", "/auth/me" })
        {
            var response = await client.GetAsync(path);
            var headers = response.Headers;

            Assert.Equal("nosniff", headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("DENY", headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("no-referrer", headers.GetValues("Referrer-Policy").Single());
            Assert.Equal("default-src 'none'; frame-ancestors 'none'", headers.GetValues("Content-Security-Policy").Single());
            Assert.Contains("camera=()", headers.GetValues("Permissions-Policy").Single(), StringComparison.Ordinal);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal); // health checks add "no-cache" too
            Assert.False(headers.Contains("Server"));
            Assert.False(headers.Contains("X-Powered-By"));
        }
    }

    [Fact]
    public async Task Hsts_is_sent_only_over_https()
    {
        var plain = Client();
        var behindProxy = Client(new() { ["Proxy:KnownProxies:0"] = ProxyIp });

        Assert.False((await plain.GetAsync("/health")).Headers.Contains("Strict-Transport-Security"));

        var secure = await behindProxy.SendAsync(Get("/health", forwardedProto: "https"));
        var hsts = secure.Headers.GetValues("Strict-Transport-Security").Single();
        Assert.Contains("max-age=31536000", hsts, StringComparison.Ordinal);
        Assert.Contains("includeSubDomains", hsts, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------- https redirect

    [Fact]
    public async Task Plain_http_is_redirected_when_https_is_required_and_the_scheme_cannot_be_faked()
    {
        var client = Client(new() { ["Security:RequireHttps"] = "true" });

        var redirect = await client.GetAsync("/health?x=1");
        Assert.Equal(HttpStatusCode.PermanentRedirect, redirect.StatusCode);
        Assert.Equal("https://api.example.test/health?x=1", redirect.Headers.Location?.ToString());

        // The proxy is not trusted, so a forged X-Forwarded-Proto changes nothing.
        Assert.Equal(HttpStatusCode.PermanentRedirect, (await client.SendAsync(Get("/health", forwardedProto: "https"))).StatusCode);
    }

    [Fact]
    public async Task A_trusted_proxy_that_speaks_https_is_not_redirected_in_a_loop()
    {
        var client = Client(new() { ["Security:RequireHttps"] = "true", ["Proxy:KnownProxies:0"] = ProxyIp });

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/health", forwardedProto: "https"))).StatusCode);
        Assert.Equal(HttpStatusCode.PermanentRedirect, (await client.SendAsync(Get("/health", forwardedProto: "http"))).StatusCode);
    }

    // -------------------------------------------------------------------------------------------- body size

    [Fact]
    public async Task Oversized_bodies_are_refused_before_being_read()
    {
        var client = Client(new() { ["Security:MaxBodyBytes"] = "1024" });
        var huge = new StringContent($"{{\"username\":\"{new string('a', 5000)}\",\"password\":\"x\"}}", Encoding.UTF8, "application/json");
        var small = new StringContent("{\"username\":\"\",\"password\":\"\"}", Encoding.UTF8, "application/json");

        var refused = await client.PostAsync("/auth/login", huge);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
        Assert.Equal("payload_too_large", JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/auth/login", small)).StatusCode); // reaches the endpoint
    }

    // -------------------------------------------------------------------------------------------- rate limits

    [Fact]
    public async Task Anonymous_callers_are_throttled_per_ip_but_health_checks_never_are()
    {
        var client = Client(Limit(anonymous: 3));

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
        }

        var limited = await client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.Equal("rate_limited", JsonDocument.Parse(await limited.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("code").GetString());

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        }
    }

    [Fact]
    public async Task Signed_in_users_have_their_own_budget_independent_of_the_ip_and_of_each_other()
    {
        var client = Client(Limit(anonymous: 2, authenticated: 5));
        var ana = Token();
        var beto = Token();

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/auth/me", ana))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Get("/auth/me", ana))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/auth/me", beto))).StatusCode); // same IP, other person
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode); // anonymous budget untouched by Ana
    }

    // -------------------------------------------------------------------------------------------- forwarded headers

    [Fact]
    public async Task A_forged_x_forwarded_for_from_an_untrusted_peer_cannot_dodge_the_rate_limit()
    {
        var client = Client(Limit(anonymous: 3));

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/auth/me", forwardedFor: $"198.51.100.{i + 1}"))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Get("/auth/me", forwardedFor: "198.51.100.200"))).StatusCode);
    }

    [Theory]
    [InlineData("Proxy:KnownProxies:0")]
    [InlineData("Proxy:KnownNetworks:0")]
    public async Task A_trusted_proxy_lets_the_real_client_ip_be_used_for_the_limit(string setting)
    {
        var client = Client(Limit(anonymous: 3).Concat(new Dictionary<string, string?> { [setting] = setting.Contains("Networks", StringComparison.Ordinal) ? "203.0.113.0/24" : ProxyIp }).ToDictionary());

        // Six different visitors behind the proxy: each has their own budget.
        for (var i = 1; i <= 6; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/auth/me", forwardedFor: $"198.51.100.{i}"))).StatusCode);
        }

        // One visitor hammering: limited as an individual.
        for (var i = 0; i < 3; i++)
        {
            await client.SendAsync(Get("/auth/me", forwardedFor: "198.51.100.77"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Get("/auth/me", forwardedFor: "198.51.100.77"))).StatusCode);
    }

    [Fact]
    public async Task A_peer_outside_the_trusted_network_is_not_believed()
    {
        var client = Client(Limit(anonymous: 3).Concat(new Dictionary<string, string?> { ["Proxy:KnownNetworks:0"] = "10.0.0.0/8" }).ToDictionary(), remoteIp: "192.0.2.50");

        for (var i = 0; i < 3; i++)
        {
            await client.SendAsync(Get("/auth/me", forwardedFor: $"198.51.100.{i + 1}"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Get("/auth/me", forwardedFor: "198.51.100.99"))).StatusCode);
    }

    // -------------------------------------------------------------------------------------------- bad configuration stops the app

    [Theory]
    [InlineData("Proxy:KnownProxies:0", "not-an-ip")]
    [InlineData("Proxy:KnownNetworks:0", "10.0.0.0")]
    [InlineData("Proxy:KnownNetworks:0", "10.0.0.0/99")]
    [InlineData("Proxy:KnownNetworks:0", "garbage/8")]
    [InlineData("Security:MaxBodyBytes", "10")]
    [InlineData("Security:AnonymousRequestsPerMinute", "0")]
    [InlineData("Security:AuthenticatedRequestsPerMinute", "-1")]
    public void Invalid_edge_settings_stop_the_app_from_starting(string key, string value)
    {
        var client = () => Client(new() { [key] = value });

        Assert.Throws<InvalidOperationException>(() => client().GetAsync("/health").GetAwaiter().GetResult());
    }
}
