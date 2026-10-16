using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;

namespace FitOS.IntegrationTests;

/// <summary>Secrets for the in-memory API under test: random per test run, never stored anywhere.</summary>
internal static class TestSettings
{
    private static readonly string JwtKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    private static readonly string EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static IWebHostBuilder UseTestSecrets(this IWebHostBuilder host)
    {
        host.UseSetting("Jwt:SigningKey", JwtKey);
        host.UseSetting("Secrets:EncryptionKey", EncryptionKey);
        return host;
    }
}
