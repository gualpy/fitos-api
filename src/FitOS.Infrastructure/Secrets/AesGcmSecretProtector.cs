using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using FitOS.Application.Secrets;
using Microsoft.Extensions.Options;

namespace FitOS.Infrastructure.Secrets;

public sealed class SecretsOptions
{
    public const string Section = "Secrets";

    /// <summary>Base64 of 32 random bytes (<c>openssl rand -base64 32</c>). From secrets/environment, never the repo.</summary>
    [Required]
    public string EncryptionKey { get; init; } = string.Empty;
}

/// <summary>AES-256-GCM. Layout: version(1) | nonce(12) | tag(16) | ciphertext. The tenant id is authenticated as AAD.</summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const byte Version = 1;

    private const int NonceSize = 12;

    private const int TagSize = 16;

    private readonly byte[] _key;

    public AesGcmSecretProtector(IOptions<SecretsOptions> options)
    {
        try
        {
            _key = Convert.FromBase64String(options.Value.EncryptionKey);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Secrets:EncryptionKey must be base64.", ex);
        }

        if (_key.Length != 32)
        {
            throw new InvalidOperationException("Secrets:EncryptionKey must decode to exactly 32 bytes.");
        }
    }

    public byte[] Protect(string plaintext, Guid tenantId)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[1 + NonceSize + TagSize + plain.Length];
        output[0] = Version;
        var nonce = output.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(1 + NonceSize + TagSize), output.AsSpan(1 + NonceSize, TagSize), tenantId.ToByteArray());
        return output;
    }

    public string Unprotect(byte[] protectedData, Guid tenantId)
    {
        if (protectedData.Length < 1 + NonceSize + TagSize || protectedData[0] != Version)
        {
            throw new CryptographicException("Unrecognised secret format.");
        }

        var cipher = protectedData.AsSpan(1 + NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(
            protectedData.AsSpan(1, NonceSize),
            cipher,
            protectedData.AsSpan(1 + NonceSize, TagSize),
            plain,
            tenantId.ToByteArray());
        return Encoding.UTF8.GetString(plain);
    }
}
