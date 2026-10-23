using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FitOS.IntegrationTests.Tenancy;

/// <summary>Outside Development the API refuses to start without an https front address (welcome links point at it).</summary>
public sealed class StartupConfigTests
{
    private static void Start(Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder> configure)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseTestSecrets();
            host.UseSetting("ConnectionStrings:Default", "Host=127.0.0.1;Database=x;Username=x;Password=x");
            host.UseEnvironment("Production");
            configure(host);
        });
        _ = factory.Server; // forces the host to build
    }

    [Fact]
    public void Production_without_environment_mail_settings_still_starts_because_they_may_live_in_the_database()
    {
        Start(host => host.UseSetting("App:PublicUrl", "https://app.test"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://app.test")]
    [InlineData("not a url")]
    public void Production_needs_an_https_public_url(string url)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Start(host =>
        {
            host.UseSetting("Email:Host", "smtp.invalid");
            host.UseSetting("Email:From", "fitos@test.invalid");
            host.UseSetting("App:PublicUrl", url);
        }));

        Assert.Contains("App:PublicUrl", ex.Message);
    }
}
