using System.Security.Cryptography;

namespace SecureChat.Core.Crypto;

/// <summary>
/// Долговременная идентичность устройства: пара ключей ECDH P-256 для
/// согласования ключей сообщений и пара ECDSA P-256 для подписи сообщений.
/// Приватные ключи никогда не покидают устройство.
/// </summary>
public sealed class IdentityKeys
{
    public required string EncryptionPublicKey { get; init; }
    public required string EncryptionPrivateKey { get; init; }
    public required string SigningPublicKey { get; init; }
    public required string SigningPrivateKey { get; init; }

    public string Fingerprint => SecureChat.Core.Crypto.Fingerprint.Compute(SigningPublicKey);

    public static IdentityKeys Create()
    {
        using var encryptionKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        return new IdentityKeys
        {
            EncryptionPublicKey = Convert.ToBase64String(
                encryptionKey.ExportSubjectPublicKeyInfo()),
            EncryptionPrivateKey = Convert.ToBase64String(
                encryptionKey.ExportPkcs8PrivateKey()),
            SigningPublicKey = Convert.ToBase64String(
                signingKey.ExportSubjectPublicKeyInfo()),
            SigningPrivateKey = Convert.ToBase64String(
                signingKey.ExportPkcs8PrivateKey())
        };
    }

    public ECDsa CreateSigningKey()
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(SigningPrivateKey), out _);
        return key;
    }

    public ECDiffieHellman CreateEncryptionKey()
    {
        var key = ECDiffieHellman.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(EncryptionPrivateKey), out _);
        return key;
    }

    /// <summary>Проверяет, что строки содержат корректные ключи P-256.</summary>
    public void Validate()
    {
        using var encryption = CreateEncryptionKey();
        using var signing = CreateSigningKey();

        if (!string.Equals(
                Convert.ToBase64String(encryption.ExportSubjectPublicKeyInfo()),
                EncryptionPublicKey,
                StringComparison.Ordinal))
        {
            throw new CryptographicException("Пара ключей ECDH не согласована.");
        }

        if (!string.Equals(
                Convert.ToBase64String(signing.ExportSubjectPublicKeyInfo()),
                SigningPublicKey,
                StringComparison.Ordinal))
        {
            throw new CryptographicException("Пара ключей ECDSA не согласована.");
        }
    }
}
