namespace Sluice.Core.Profiles;

/// <summary>
/// Protects secrets with the current OS account (Windows DPAPI, current-user scope), for Proxifier's "account" password
/// mode. Reached through <see cref="Platform.IPlatformServices.SecretProtector"/>; null where the OS has none
/// (docs/architecture/platforms.md §6.3).
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] data, string? description = null);

    /// <summary>Throws <see cref="System.Security.Cryptography.CryptographicException"/> if the data can't be unprotected.</summary>
    byte[] Unprotect(byte[] data);
}
