using System.Security.Cryptography;
using SecureChat.Core.Contracts;
using SecureChat.Core.Crypto;

namespace SecureChat.Core.Tests;

public class CryptoTests
{
    private static EncryptedEnvelope CreateEnvelope(
        IdentityKeys sender,
        IdentityKeys recipient,
        string text = "Привет, это секретное сообщение!")
    {
        using var signingKey = sender.CreateSigningKey();
        return MessageCrypto.Encrypt(
            Guid.NewGuid().ToString("N"),
            "sender-id",
            "alice",
            "recipient-id",
            "bob",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            text,
            recipient.EncryptionPublicKey,
            signingKey);
    }

    [Fact]
    public void Identity_keys_are_valid_and_have_stable_fingerprints()
    {
        var identity = IdentityKeys.Create();
        identity.Validate();

        Assert.False(string.IsNullOrWhiteSpace(identity.EncryptionPublicKey));
        Assert.False(string.IsNullOrWhiteSpace(identity.SigningPublicKey));
        Assert.Equal(identity.Fingerprint, identity.Fingerprint);
        Assert.Equal(32, identity.Fingerprint.Count(c => c != ' '));
    }

    [Fact]
    public void Message_roundtrip_preserves_plaintext()
    {
        var alice = IdentityKeys.Create();
        var bob = IdentityKeys.Create();
        var envelope = CreateEnvelope(alice, bob);

        Assert.True(MessageCrypto.VerifyEnvelope(envelope, alice.SigningPublicKey));

        var decrypted = MessageCrypto.Decrypt(
            envelope,
            bob.EncryptionPrivateKey,
            alice.SigningPublicKey);

        Assert.Equal("Привет, это секретное сообщение!", decrypted);
        Assert.NotEqual(envelope.Ciphertext, decrypted);
        Assert.DoesNotContain("секретное", envelope.Ciphertext);
    }

    [Fact]
    public void Tampered_ciphertext_is_rejected()
    {
        var alice = IdentityKeys.Create();
        var bob = IdentityKeys.Create();
        var envelope = CreateEnvelope(alice, bob);

        var bytes = Convert.FromBase64String(envelope.Ciphertext);
        bytes[0] ^= 0x42;
        envelope.Ciphertext = Convert.ToBase64String(bytes);

        Assert.False(MessageCrypto.VerifyEnvelope(envelope, alice.SigningPublicKey));
        Assert.Throws<CryptographicException>(() =>
            MessageCrypto.Decrypt(envelope, bob.EncryptionPrivateKey, alice.SigningPublicKey));
    }

    [Fact]
    public void Tampered_header_is_rejected()
    {
        var alice = IdentityKeys.Create();
        var bob = IdentityKeys.Create();
        var envelope = CreateEnvelope(alice, bob);

        envelope.SenderUsername = "mallory";

        Assert.False(MessageCrypto.VerifyEnvelope(envelope, alice.SigningPublicKey));
        Assert.Throws<CryptographicException>(() =>
            MessageCrypto.Decrypt(envelope, bob.EncryptionPrivateKey, alice.SigningPublicKey));
    }

    [Fact]
    public void Third_party_cannot_decrypt_message()
    {
        var alice = IdentityKeys.Create();
        var bob = IdentityKeys.Create();
        var carol = IdentityKeys.Create();
        var envelope = CreateEnvelope(alice, bob);

        Assert.True(MessageCrypto.VerifyEnvelope(envelope, alice.SigningPublicKey));
        Assert.ThrowsAny<CryptographicException>(() =>
            MessageCrypto.Decrypt(envelope, carol.EncryptionPrivateKey, alice.SigningPublicKey));
    }

    [Fact]
    public void Signature_from_wrong_sender_is_rejected()
    {
        var alice = IdentityKeys.Create();
        var bob = IdentityKeys.Create();
        var mallory = IdentityKeys.Create();
        var envelope = CreateEnvelope(alice, bob);

        Assert.False(MessageCrypto.VerifyEnvelope(envelope, mallory.SigningPublicKey));
    }

    [Fact]
    public void Auth_secret_is_deterministic_and_bound_to_username()
    {
        var first = AuthSecret.Derive("correct horse battery staple", "Alice");
        var second = AuthSecret.Derive("correct horse battery staple", " alice ");
        var otherPassword = AuthSecret.Derive("different password", "Alice");
        var otherUser = AuthSecret.Derive("correct horse battery staple", "Bob");

        Assert.Equal(first, second);
        Assert.NotEqual(first, otherPassword);
        Assert.NotEqual(first, otherUser);
        Assert.True(Convert.FromBase64String(first).Length >= 32);
    }
}
