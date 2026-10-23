using System.Security.Cryptography;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FitOS.IntegrationTests;

/// <summary>Secrets for the in-memory API under test: random per test run, never stored anywhere.</summary>
internal static class TestSettings
{
    internal static readonly string JwtKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    private static readonly string EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static IWebHostBuilder UseTestSecrets(this IWebHostBuilder host)
    {
        host.UseSetting("Jwt:SigningKey", JwtKey);
        host.UseSetting("Secrets:EncryptionKey", EncryptionKey);
        // Cheap hashes keep the suite fast; production uses 210 000 (the default).
        host.UseSetting("Auth:PasswordIterations", "1000");
        host.UseSetting("Customers:ValidateIdentification", "true"); // the development file turns it off
        host.UseSetting("Auth:SkipPlatformConfirmation", "true"); // most tests just need a platform session; the confirmation has its own tests
        return host;
    }

    /// <summary>What a Production-like host must have to start (welcome links go by e-mail): never actually used to send anything.</summary>
    public static IWebHostBuilder UseProductionMailSettings(this IWebHostBuilder host)
    {
        host.UseSetting("Email:Host", "smtp.invalid");
        host.UseSetting("Email:From", "fitos@test.invalid");
        host.UseSetting("App:PublicUrl", "https://app.test");
        return host;
    }

    /// <summary>Makes the in-memory server see every request as coming from this address.</summary>
    public static IWebHostBuilder UseRemoteIp(this IWebHostBuilder host, string address)
    {
        host.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(IPAddress.Parse(address))));
        return host;
    }

    private sealed class RemoteIpStartupFilter(IPAddress address) : IStartupFilter
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
}
