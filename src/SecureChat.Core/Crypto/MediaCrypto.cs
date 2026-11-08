using System.Security.Cryptography;
using System.Text;

namespace SecureChat.Core.Crypto;

/// <summary>
/// Шифрование вложений. Файл шифруется случайным одноразовым ключом
/// AES-256-GCM, а сам ключ передаётся только внутри E2E-конверта сообщения.
/// Сервер хранит лишь шифротекст вложения.
/// </summary>
public static class MediaCrypto
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static MediaEncryptionResult Encrypt(ReadOnlySpan<byte> plaintext, string mediaId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaId);

        var key = RandomNumberGenerator.GetBytes(KeySize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var ciphertext = new byte[plaintext.Length];
        var aad = BuildAad(mediaId);

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        }

        return new MediaEncryptionResult
        {
            Ciphertext = ciphertext,
            Key = Convert.ToBase64String(key),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag)
        };
    }

    public static byte[] Decrypt(
        string mediaId,
        ReadOnlySpan<byte> ciphertext,
        string keyBase64,
        string nonceBase64,
        string tagBase64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyBase64);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonceBase64);
        ArgumentException.ThrowIfNullOrWhiteSpace(tagBase64);

        var key = Convert.FromBase64String(keyBase64);
        var nonce = Convert.FromBase64String(nonceBase64);
        var tag = Convert.FromBase64String(tagBase64);

        if (key.Length != KeySize || nonce.Length != NonceSize || tag.Length != TagSize)
        {
            throw new CryptographicException("Некорректные параметры шифрования вложения.");
        }

        var plaintext = new byte[ciphertext.Length];
        var aad = BuildAad(mediaId);

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        }

        return plaintext;
    }

    private static byte[] BuildAad(string mediaId) =>
        Encoding.UTF8.GetBytes($"SecureChat/v1/media|{mediaId}");
}

public sealed class MediaEncryptionResult
{
    public required byte[] Ciphertext { get; init; }
    public required string Key { get; init; }
    public required string Nonce { get; init; }
    public required string Tag { get; init; }
}
