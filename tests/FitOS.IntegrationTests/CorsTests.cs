using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FitOS.IntegrationTests;

/// <summary>CORS is configuration-driven: only listed origins may call the API from a browser. None of this needs a database.</summary>
public sealed class CorsTests
{
    private const string Allowed = "https://app.example.test";

    private static WebApplicationFactory<Program> Factory(params string[] origins) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Production"); // so appsettings.Development.json (which lists localhost:3000) is not loaded
            host.UseProductionMailSettings();
            host.UseTestSecrets();
            for (var i = 0; i < origins.Length; i++)
            {
                host.UseSetting($"Cors:AllowedOrigins:{i}", origins[i]);
            }
        });

    private static HttpRequestMessage Preflight(string origin, string headers = "content-type,x-tenant-slug")
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/auth/login");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", headers);
        return request;
    }

    [Fact]
    public async Task A_listed_origin_gets_its_preflight_approved_with_the_tenant_header()
    {
        using var factory = Factory(Allowed);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight(Allowed));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(Allowed, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("x-tenant-slug", string.Join(',', response.Headers.GetValues("Access-Control-Allow-Headers")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Another_origin_is_not_approved()
    {
        using var factory = Factory(Allowed);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("https://evil.example.test"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Headers_outside_the_allow_list_are_refused()
    {
        using var factory = Factory(Allowed);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight(Allowed, headers: "x-something-else"));

        // The browser blocks the call unless every requested header appears in the allowed list.
        var allowedHeaders = response.Headers.TryGetValues("Access-Control-Allow-Headers", out var values) ? string.Join(',', values) : string.Empty;
        Assert.DoesNotContain("x-something-else", allowedHeaders, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Without_configuration_no_origin_is_ever_approved()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("http://localhost:3000"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Real_responses_to_a_listed_origin_carry_the_header_too()
    {
        using var factory = Factory(Allowed);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", Allowed);

        var response = await client.SendAsync(request);

        Assert.Equal(Allowed, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Theory]
    [InlineData("*")]
    [InlineData("not a url")]
    [InlineData("localhost:3000")]
    [InlineData("https://app.example.test/")]
    [InlineData("https://app.example.test/path")]
    [InlineData("ftp://app.example.test")]
    [InlineData("https://*.example.test")]
    public void A_wildcard_or_malformed_origin_stops_the_app_from_starting(string origin)
    {
        using var factory = Factory(origin);

        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }
}
