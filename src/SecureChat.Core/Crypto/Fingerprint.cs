using System.Security.Cryptography;
using System.Text;

namespace SecureChat.Core.Crypto;

/// <summary>
/// Человекочитаемый отпечаток ключа подписи. Используется для сверки ключей
/// собеседников вне чата (например, голосом или через другой канал).
/// </summary>
public static class Fingerprint
{
    public static string Compute(string signingPublicKeyBase64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingPublicKeyBase64);

        var keyBytes = Convert.FromBase64String(signingPublicKeyBase64);
        var hash = SHA256.HashData(keyBytes);
        return Format(hash, groups: 8);
    }

    public static string Format(ReadOnlySpan<byte> hash, int groups = 8)
    {
        var builder = new StringBuilder(groups * 5);
        for (var i = 0; i < groups && i * 2 + 1 < hash.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(hash[i * 2].ToString("X2"));
            builder.Append(hash[i * 2 + 1].ToString("X2"));
        }

        return builder.ToString();
    }
}
