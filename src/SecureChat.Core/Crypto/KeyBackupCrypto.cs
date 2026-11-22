using System.Security.Cryptography;
using System.Text;

namespace SecureChat.Core.Crypto;

/// <summary>Шифрование резервной копии ключей по паролю (PBKDF2 + AES-GCM).</summary>
public static class KeyBackupCrypto
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int Iterations = 210_000;

    public static string Encrypt(string plainJson, string password)
    {
        if (string.IsNullOrWhiteSpace(password)) throw new ArgumentException("Пароль пуст.", nameof(password));
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        var plain = Encoding.UTF8.GetBytes(plainJson);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        var result = new byte[SaltSize + NonceSize + tag.Length + cipher.Length];
        Buffer.BlockCopy(salt, 0, result, 0, SaltSize);
        Buffer.BlockCopy(nonce, 0, result, SaltSize, NonceSize);
        Buffer.BlockCopy(tag, 0, result, SaltSize + NonceSize, tag.Length);
        Buffer.BlockCopy(cipher, 0, result, SaltSize + NonceSize + tag.Length, cipher.Length);
        return "SCKEY1:" + Convert.ToBase64String(result);
    }

    public static string Decrypt(string payload, string password)
    {
        if (string.IsNullOrWhiteSpace(password)) throw new ArgumentException("Пароль пуст.", nameof(password));
        if (!payload.StartsWith("SCKEY1:", StringComparison.Ordinal))
            throw new CryptographicException("Неизвестный формат бэкапа.");
        var data = Convert.FromBase64String(payload["SCKEY1:".Length..]);
        if (data.Length < SaltSize + NonceSize + 16)
            throw new CryptographicException("Бэкап повреждён.");
        var salt = data.AsSpan(0, SaltSize);
        var nonce = data.AsSpan(SaltSize, NonceSize);
        var tag = data.AsSpan(SaltSize + NonceSize, 16);
        var cipher = data.AsSpan(SaltSize + NonceSize + 16);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
