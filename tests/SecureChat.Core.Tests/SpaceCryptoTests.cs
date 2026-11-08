using System.Security.Cryptography;
using System.Text;
using SecureChat.Core.Contracts;
using SecureChat.Core.Crypto;

namespace SecureChat.Core.Tests;

public class SpaceCryptoTests
{
    private const string SpaceId = "space-1";

    [Fact]
    public void Space_key_envelope_is_readable_only_by_the_recipient()
    {
        var author = IdentityKeys.Create();
        var member = IdentityKeys.Create();
        var spaceKey = SpaceCrypto.CreateSpaceKey();

        var envelope = SpaceCrypto.EncryptSpaceKey(
            spaceKey,
            SpaceId,
            "member-id",
            member.EncryptionPublicKey);

        var recovered = SpaceCrypto.DecryptSpaceKey(
            envelope,
            SpaceId,
            "member-id",
            member);

        Assert.Equal(spaceKey, recovered);

        // Посторонний участник не может расшифровать «конверт».
        var stranger = IdentityKeys.Create();
        Assert.Throws<AuthenticationTagMismatchException>(() =>
            SpaceCrypto.DecryptSpaceKey(envelope, SpaceId, "member-id", stranger));
    }

    [Fact]
    public void Space_message_roundtrip_preserves_plaintext()
    {
        var author = IdentityKeys.Create();
        var spaceKey = SpaceCrypto.CreateSpaceKey();
        const string text = "Секретное сообщение группы";

        using var signingKey = author.CreateSigningKey();
        var cipher = SpaceCrypto.EncryptMessage(
            spaceKey,
            SpaceId,
            "message-1",
            "author",
            text,
            signingKey);

        // В конверте не должно быть открытого текста.
        Assert.DoesNotContain(text, cipher.Ciphertext, StringComparison.Ordinal);

        var recovered = SpaceCrypto.DecryptMessage(
            spaceKey,
            SpaceId,
            "message-1",
            "author",
            cipher);

        Assert.Equal(text, recovered);
    }

    [Fact]
    public void Space_message_signature_is_verified_and_tampering_is_detected()
    {
        var author = IdentityKeys.Create();
        var spaceKey = SpaceCrypto.CreateSpaceKey();

        using var signingKey = author.CreateSigningKey();
        var cipher = SpaceCrypto.EncryptMessage(
            spaceKey,
            SpaceId,
            "message-1",
            "author",
            "Оригинал",
            signingKey);

        Assert.True(SpaceCrypto.VerifyMessage(
            SpaceId,
            "message-1",
            "author",
            cipher,
            author.SigningPublicKey));

        // Подмена шифротекста ломает подпись.
        var tampered = new SpaceCipher
        {
            MessageId = cipher.MessageId,
            Nonce = cipher.Nonce,
            Tag = cipher.Tag,
            Ciphertext = Convert.ToBase64String(
                Encoding.UTF8.GetBytes("подмена")),
            Signature = cipher.Signature
        };

        Assert.False(SpaceCrypto.VerifyMessage(
            SpaceId,
            "message-1",
            "author",
            tampered,
            author.SigningPublicKey));

        // Чужой отправитель не проходит проверку.
        var stranger = IdentityKeys.Create();
        Assert.False(SpaceCrypto.VerifyMessage(
            SpaceId,
            "message-1",
            "author",
            cipher,
            stranger.SigningPublicKey));
    }

    [Fact]
    public void Space_message_cannot_be_decrypted_with_another_space_key()
    {
        var author = IdentityKeys.Create();
        var firstKey = SpaceCrypto.CreateSpaceKey();
        var secondKey = SpaceCrypto.CreateSpaceKey();

        using var signingKey = author.CreateSigningKey();
        var cipher = SpaceCrypto.EncryptMessage(
            firstKey,
            SpaceId,
            "message-1",
            "author",
            "Текст",
            signingKey);

        Assert.Throws<AuthenticationTagMismatchException>(() =>
            SpaceCrypto.DecryptMessage(
                secondKey,
                SpaceId,
                "message-1",
                "author",
                cipher));
    }

    [Fact]
    public void Space_message_cannot_be_decrypted_in_another_space()
    {
        var author = IdentityKeys.Create();
        var spaceKey = SpaceCrypto.CreateSpaceKey();

        using var signingKey = author.CreateSigningKey();
        var cipher = SpaceCrypto.EncryptMessage(
            spaceKey,
            SpaceId,
            "message-1",
            "author",
            "Текст",
            signingKey);

        Assert.Throws<AuthenticationTagMismatchException>(() =>
            SpaceCrypto.DecryptMessage(
                spaceKey,
                "other-space",
                "message-1",
                "author",
                cipher));
    }
}
