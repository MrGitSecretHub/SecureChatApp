using System.Security.Cryptography;
using System.Text.Json;
using SecureChat.Core.Crypto;
using SecureChat.Core.Serialization;

namespace SecureChat.Core.Tests;

public class KeyBackupTests
{
    private sealed class Payload
    {
        public string Username { get; set; } = string.Empty;
        public IdentityKeys Identity { get; set; } = null!;
    }

    [Fact]
    public void RoundTrip_RestoresIdentity()
    {
        var identity = IdentityKeys.Create();
        var json = ChatJson.Serialize(new Payload { Username = "alice", Identity = identity });
        var backup = KeyBackupCrypto.Encrypt(json, "correct horse battery staple");

        Assert.StartsWith("SCKEY1:", backup);
        var restored = ChatJson.Deserialize<Payload>(KeyBackupCrypto.Decrypt(backup, "correct horse battery staple"));
        Assert.Equal("alice", restored.Username);
        Assert.Equal(identity.SigningPublicKey, restored.Identity.SigningPublicKey);
        Assert.Equal(identity.EncryptionPrivateKey, restored.Identity.EncryptionPrivateKey);
        restored.Identity.Validate();
    }

    [Fact]
    public void WrongPassword_Throws()
    {
        var backup = KeyBackupCrypto.Encrypt("{}", "one");
        Assert.Throws<AuthenticationTagMismatchException>(() => KeyBackupCrypto.Decrypt(backup, "two"));
    }

    [Fact]
    public void TamperedBackup_Throws()
    {
        var backup = KeyBackupCrypto.Encrypt("{}", "pass");
        var bytes = Convert.FromBase64String(backup["SCKEY1:".Length..]);
        bytes[^1] ^= 0xFF;
        var tampered = "SCKEY1:" + Convert.ToBase64String(bytes);
        Assert.ThrowsAny<CryptographicException>(() => KeyBackupCrypto.Decrypt(tampered, "pass"));
    }

    [Fact]
    public void InvalidFormat_Throws()
    {
        Assert.Throws<CryptographicException>(() => KeyBackupCrypto.Decrypt("garbage", "pass"));
    }
}
