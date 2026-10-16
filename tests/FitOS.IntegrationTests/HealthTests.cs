using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FitOS.IntegrationTests;

public sealed class HealthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_is_public_and_the_app_boots_with_valid_configuration()
    {
        using var client = factory.WithWebHostBuilder(host => host.UseTestSecrets()).CreateClient();
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
