namespace FitOS.Application.Secrets;

/// <summary>
/// Encrypts secrets stored in the database (e.g. a tenant's SICOFAC API key). The ciphertext is bound
/// to the tenant, so a value copied into another tenant's row cannot be decrypted there.
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(string plaintext, Guid tenantId);

    /// <exception cref="System.Security.Cryptography.CryptographicException">Wrong tenant, wrong key or tampered data.</exception>
    string Unprotect(byte[] protectedData, Guid tenantId);
}
