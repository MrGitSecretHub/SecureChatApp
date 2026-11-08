using System.Security.Cryptography;
using System.Text;
using SecureChat.Core.Contracts;
using SecureChat.Core.Serialization;

namespace SecureChat.Core.Crypto;

/// <summary>
/// Сквозное шифрование сообщений (E2E).
///
/// Схема: одноразовый ECDH P-256 ключ отправителя + долговременный ECDH-ключ
/// получателя → HKDF-SHA256 → AES-256-GCM. Каждое сообщение подписывается
/// долговременным ECDSA-ключом отправителя. Сервер ретранслирует только
/// шифротекст и не имеет ключей для его расшифровки.
/// </summary>
public static class MessageCrypto
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    public static EncryptedEnvelope Encrypt(
        string id,
        string senderId,
        string senderUsername,
        string recipientId,
        string recipientUsername,
        long sentAtUnixMs,
        string plaintext,
        string recipientEncryptionPublicKey,
        ECDsa senderSigningKey)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        return EncryptPayload(
            id,
            senderId,
            senderUsername,
            recipientId,
            recipientUsername,
            sentAtUnixMs,
            new MessagePayload
            {
                Text = plaintext,
                SentAtUnixMs = sentAtUnixMs
            },
            recipientEncryptionPublicKey,
            senderSigningKey);
    }

    public static EncryptedEnvelope EncryptPayload(
        string id,
        string senderId,
        string senderUsername,
        string recipientId,
        string recipientUsername,
        long sentAtUnixMs,
        MessagePayload payload,
        string recipientEncryptionPublicKey,
        ECDsa senderSigningKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderUsername);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientUsername);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientEncryptionPublicKey);
        ArgumentNullException.ThrowIfNull(senderSigningKey);

        payload.SentAtUnixMs = sentAtUnixMs;
        payload.Text ??= string.Empty;
        payload.Kind = string.IsNullOrWhiteSpace(payload.Kind)
            ? MessageKinds.Text
            : payload.Kind;

        using var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var recipientKey = ECDiffieHellman.Create();
        recipientKey.ImportSubjectPublicKeyInfo(
            Convert.FromBase64String(recipientEncryptionPublicKey), out _);

        var recipientPublicKeyBytes = recipientKey.ExportSubjectPublicKeyInfo();
        var ephemeralPublicKeyBytes = ephemeralKey.ExportSubjectPublicKeyInfo();
        var sharedSecret = ephemeralKey.DeriveRawSecretAgreement(recipientKey.PublicKey);

        var header = BuildHeader(
            id, senderId, senderUsername, recipientId, recipientUsername,
            sentAtUnixMs, Convert.ToBase64String(ephemeralPublicKeyBytes));

        var aad = Encoding.UTF8.GetBytes(header);
        var salt = SHA256.HashData(Concat(ephemeralPublicKeyBytes, recipientPublicKeyBytes));
        var info = Encoding.UTF8.GetBytes($"SecureChat/v1/message|{senderId}|{recipientId}");
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, KeySize, salt, info);

        var payloadJson = ChatJson.Serialize(payload);
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[payloadBytes.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, payloadBytes, ciphertext, tag, aad);
        }

        var nonceBase64 = Convert.ToBase64String(nonce);
        var ciphertextBase64 = Convert.ToBase64String(ciphertext);
        var tagBase64 = Convert.ToBase64String(tag);

        var signatureInput = BuildSignatureInput(header, nonce, ciphertext, tag);
        var signature = senderSigningKey.SignData(
            signatureInput,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return new EncryptedEnvelope
        {
            Id = id,
            SenderId = senderId,
            SenderUsername = senderUsername,
            RecipientId = recipientId,
            RecipientUsername = recipientUsername,
            SentAtUnixMs = sentAtUnixMs,
            SenderEphemeralPublicKey = Convert.ToBase64String(ephemeralPublicKeyBytes),
            Nonce = nonceBase64,
            Ciphertext = ciphertextBase64,
            Tag = tagBase64,
            Signature = Convert.ToBase64String(signature)
        };
    }

    public static string Decrypt(
        EncryptedEnvelope envelope,
        string recipientEncryptionPrivateKey,
        string senderSigningPublicKey)
    {
        return DecryptPayload(
            envelope,
            recipientEncryptionPrivateKey,
            senderSigningPublicKey).Text;
    }

    public static MessagePayload DecryptPayload(
        EncryptedEnvelope envelope,
        string recipientEncryptionPrivateKey,
        string senderSigningPublicKey)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientEncryptionPrivateKey);

        if (!VerifyEnvelope(envelope, senderSigningPublicKey))
        {
            throw new CryptographicException(
                "Подпись сообщения недействительна. Сообщение могло быть изменено.");
        }

        using var recipientKey = ECDiffieHellman.Create();
        recipientKey.ImportPkcs8PrivateKey(
            Convert.FromBase64String(recipientEncryptionPrivateKey), out _);

        using var ephemeralKey = ECDiffieHellman.Create();
        ephemeralKey.ImportSubjectPublicKeyInfo(
            Convert.FromBase64String(envelope.SenderEphemeralPublicKey), out _);

        var recipientPublicKeyBytes = recipientKey.ExportSubjectPublicKeyInfo();
        var ephemeralPublicKeyBytes = ephemeralKey.ExportSubjectPublicKeyInfo();
        var sharedSecret = recipientKey.DeriveRawSecretAgreement(ephemeralKey.PublicKey);

        var header = BuildHeader(
            envelope.Id,
            envelope.SenderId,
            envelope.SenderUsername,
            envelope.RecipientId,
            envelope.RecipientUsername,
            envelope.SentAtUnixMs,
            envelope.SenderEphemeralPublicKey);

        var aad = Encoding.UTF8.GetBytes(header);
        var salt = SHA256.HashData(Concat(ephemeralPublicKeyBytes, recipientPublicKeyBytes));
        var info = Encoding.UTF8.GetBytes(
            $"SecureChat/v1/message|{envelope.SenderId}|{envelope.RecipientId}");
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, KeySize, salt, info);

        var nonce = Convert.FromBase64String(envelope.Nonce);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        var tag = Convert.FromBase64String(envelope.Tag);

        if (nonce.Length != NonceSize || tag.Length != TagSize)
        {
            throw new CryptographicException("Некорректные параметры шифрования.");
        }

        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        }

        var payload = ChatJson.Deserialize<MessagePayload>(
            Encoding.UTF8.GetString(plaintext));
        payload.SentAtUnixMs = envelope.SentAtUnixMs;
        payload.Text ??= string.Empty;
        payload.Kind = string.IsNullOrWhiteSpace(payload.Kind)
            ? MessageKinds.Text
            : payload.Kind;
        return payload;
    }

    public static bool VerifyEnvelope(
        EncryptedEnvelope envelope,
        string senderSigningPublicKeyBase64)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (string.IsNullOrWhiteSpace(senderSigningPublicKeyBase64))
        {
            return false;
        }

        try
        {
            var nonce = Convert.FromBase64String(envelope.Nonce);
            var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
            var tag = Convert.FromBase64String(envelope.Tag);
            var signature = Convert.FromBase64String(envelope.Signature);

            if (nonce.Length != NonceSize || tag.Length != TagSize)
            {
                return false;
            }

            var header = BuildHeader(
                envelope.Id,
                envelope.SenderId,
                envelope.SenderUsername,
                envelope.RecipientId,
                envelope.RecipientUsername,
                envelope.SentAtUnixMs,
                envelope.SenderEphemeralPublicKey);

            var signatureInput = BuildSignatureInput(header, nonce, ciphertext, tag);

            using var signingKey = ECDsa.Create();
            signingKey.ImportSubjectPublicKeyInfo(
                Convert.FromBase64String(senderSigningPublicKeyBase64), out _);

            return signingKey.VerifyData(
                signatureInput,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    public static string BuildHeader(
        string id,
        string senderId,
        string senderUsername,
        string recipientId,
        string recipientUsername,
        long sentAtUnixMs,
        string ephemeralPublicKey)
    {
        return string.Join(
            '\n',
            id,
            senderId,
            senderUsername,
            recipientId,
            recipientUsername,
            sentAtUnixMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ephemeralPublicKey);
    }

    private static byte[] BuildSignatureInput(
        string header,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag)
    {
        using var stream = new MemoryStream();
        WriteField(stream, Encoding.UTF8.GetBytes(header));
        WriteField(stream, nonce);
        WriteField(stream, ciphertext);
        WriteField(stream, tag);
        return stream.ToArray();
    }

    private static void WriteField(Stream stream, ReadOnlySpan<byte> value)
    {
        stream.Write(value);
        stream.WriteByte(0);
    }

    private static byte[] Concat(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var result = new byte[left.Length + right.Length];
        left.CopyTo(result);
        right.CopyTo(result.AsSpan(left.Length));
        return result;
    }
}
