using System.Security.Cryptography;

namespace SecureChat.Core.Crypto;

/// <summary>Проверка публичных ключей, полученных от клиента или сервера.</summary>
public static class PublicKeyValidator
{
    public static bool IsValidEncryptionKey(string base64)
    {
        try
        {
            using var key = ECDiffieHellman.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(base64), out var read);
            return read > 0 && read == Convert.FromBase64String(base64).Length;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    public static bool IsValidSigningKey(string base64)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(base64), out var read);
            return read > 0 && read == Convert.FromBase64String(base64).Length;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
