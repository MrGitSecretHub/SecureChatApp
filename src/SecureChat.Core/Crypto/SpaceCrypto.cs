using System.Security.Cryptography;
using System.Text;
using SecureChat.Core.Contracts;

namespace SecureChat.Core.Crypto;

/// <summary>
/// Сквозное шифрование сообщений в пространствах (группы, каналы, форумы).
///
/// Устройство общего ключа пространства:
/// 1. При создании пространства генерируется случайный ключ AES-256.
/// 2. Для каждого участника ключ шифруется по его долговременному ECDH-ключу
///    (одноразовый ECDH + HKDF-SHA256 + AES-256-GCM) — «конверт».
/// 3. Сервер хранит только «конверты» и зашифрованные сообщения, ключ у него
///    отсутствует и восстановить невозможно.
/// 4. Сообщения шифруются общим ключом пространства (AES-256-GCM) и
///    подписываются долговременным ECDSA-ключом отправителя, поэтому участник
///    не может подделать сообщение другого.
/// </summary>
public static class SpaceCrypto
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    /// <summary>Создаёт общий ключ пространства.</summary>
    public static byte[] CreateSpaceKey() => RandomNumberGenerator.GetBytes(KeySize);

    /// <summary>Шифрует общий ключ пространства для конкретного участника.</summary>
    public static SpaceKeyEnvelope EncryptSpaceKey(
        byte[] spaceKey,
        string spaceId,
        string recipientUserId,
        string recipientEncryptionPublicKey)
    {
        ArgumentNullException.ThrowIfNull(spaceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientEncryptionPublicKey);

        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var recipient = ECDiffieHellman.Create();
        recipient.ImportSubjectPublicKeyInfo(
            Convert.FromBase64String(recipientEncryptionPublicKey),
            out _);

        var ephemeralPublic = ephemeral.ExportSubjectPublicKeyInfo();
        var recipientPublic = recipient.ExportSubjectPublicKeyInfo();
        var shared = ephemeral.DeriveRawSecretAgreement(recipient.PublicKey);
        var salt = SHA256.HashData(Concat(ephemeralPublic, recipientPublic));
        var info = Encoding.UTF8.GetBytes(
            $"SecureChat/space-key/v1|{spaceId}|{recipientUserId}");
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, KeySize, salt, info);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];

        // Копия: исходный ключ пространства нельзя обнулять — он нужен владельцу.
        var plaintext = spaceKey.ToArray();
        var ciphertext = new byte[plaintext.Length];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(
                nonce,
                plaintext,
                ciphertext,
                tag,
                Encoding.UTF8.GetBytes($"{spaceId}|{recipientUserId}"));
        }

        CryptographicOperations.ZeroMemory(plaintext);

        return new SpaceKeyEnvelope
        {
            EphemeralPublicKey = Convert.ToBase64String(ephemeralPublic),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Ciphertext = Convert.ToBase64String(ciphertext)
        };
    }

    /// <summary>Расшифровывает общий ключ пространства приватным ключом участника.</summary>
    public static byte[] DecryptSpaceKey(
        SpaceKeyEnvelope envelope,
        string spaceId,
        string userId,
        IdentityKeys identity)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(identity);

        using var ephemeral = ECDiffieHellman.Create();
        ephemeral.ImportSubjectPublicKeyInfo(
            Convert.FromBase64String(envelope.EphemeralPublicKey),
            out _);

        using var own = identity.CreateEncryptionKey();
        var ownPublic = own.ExportSubjectPublicKeyInfo();
        var shared = own.DeriveRawSecretAgreement(ephemeral.PublicKey);
        var salt = SHA256.HashData(Concat(ephemeral.ExportSubjectPublicKeyInfo(), ownPublic));
        var info = Encoding.UTF8.GetBytes($"SecureChat/space-key/v1|{spaceId}|{userId}");
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, KeySize, salt, info);

        var nonce = Convert.FromBase64String(envelope.Nonce);
        var tag = Convert.FromBase64String(envelope.Tag);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        var plaintext = new byte[ciphertext.Length];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Decrypt(
                nonce,
                ciphertext,
                tag,
                plaintext,
                Encoding.UTF8.GetBytes($"{spaceId}|{userId}"));
        }

        CryptographicOperations.ZeroMemory(key);
        return plaintext;
    }

    /// <summary>Шифрует сообщение общим ключом пространства и подписывает его.</summary>
    public static SpaceCipher EncryptMessage(
        byte[] spaceKey,
        string spaceId,
        string messageId,
        string senderId,
        string text,
        ECDsa senderSigningKey)
    {
        ArgumentNullException.ThrowIfNull(spaceKey);
        ArgumentNullException.ThrowIfNull(senderSigningKey);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var plaintext = Encoding.UTF8.GetBytes(text);
        var ciphertext = new byte[plaintext.Length];

        using (var aes = new AesGcm(spaceKey, TagSize))
        {
            aes.Encrypt(
                nonce,
                plaintext,
                ciphertext,
                tag,
                BuildAad(spaceId, messageId, senderId));
        }

        var signature = senderSigningKey.SignData(
            BuildSignatureData(spaceId, messageId, senderId, nonce, ciphertext, tag),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return new SpaceCipher
        {
            MessageId = messageId,
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Ciphertext = Convert.ToBase64String(ciphertext),
            Signature = Convert.ToBase64String(signature)
        };
    }

    /// <summary>Расшифровывает сообщение пространства.</summary>
    public static string DecryptMessage(
        byte[] spaceKey,
        string spaceId,
        string messageId,
        string senderId,
        SpaceCipher cipher)
    {
        ArgumentNullException.ThrowIfNull(spaceKey);
        ArgumentNullException.ThrowIfNull(cipher);

        var nonce = Convert.FromBase64String(cipher.Nonce);
        var tag = Convert.FromBase64String(cipher.Tag);
        var ciphertext = Convert.FromBase64String(cipher.Ciphertext);
        var plaintext = new byte[ciphertext.Length];

        using (var aes = new AesGcm(spaceKey, TagSize))
        {
            aes.Decrypt(
                nonce,
                ciphertext,
                tag,
                plaintext,
                BuildAad(spaceId, messageId, senderId));
        }

        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>Проверяет подпись отправителя сообщения пространства.</summary>
    public static bool VerifyMessage(
        string spaceId,
        string messageId,
        string senderId,
        SpaceCipher cipher,
        string senderSigningPublicKey)
    {
        try
        {
            using var signing = ECDsa.Create();
            signing.ImportSubjectPublicKeyInfo(
                Convert.FromBase64String(senderSigningPublicKey),
                out _);

            return signing.VerifyData(
                BuildSignatureData(
                    spaceId,
                    messageId,
                    senderId,
                    Convert.FromBase64String(cipher.Nonce),
                    Convert.FromBase64String(cipher.Ciphertext),
                    Convert.FromBase64String(cipher.Tag)),
                Convert.FromBase64String(cipher.Signature),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] BuildAad(string spaceId, string messageId, string senderId) =>
        Encoding.UTF8.GetBytes($"{spaceId}|{messageId}|{senderId}");

    private static byte[] BuildSignatureData(
        string spaceId,
        string messageId,
        string senderId,
        byte[] nonce,
        byte[] ciphertext,
        byte[] tag)
    {
        var header = Encoding.UTF8.GetBytes($"{spaceId}|{messageId}|{senderId}|");
        var result = new byte[header.Length + nonce.Length + ciphertext.Length + tag.Length];
        var offset = 0;

        header.CopyTo(result, offset);
        offset += header.Length;
        nonce.CopyTo(result, offset);
        offset += nonce.Length;
        ciphertext.CopyTo(result, offset);
        offset += ciphertext.Length;
        tag.CopyTo(result, offset);

        return result;
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }
}
