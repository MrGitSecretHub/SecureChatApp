using SecureChat.Core.Crypto;

namespace SecureChat.Core.Tests;

public class PublicKeyValidationTests
{
    [Fact]
    public void Valid_public_keys_are_accepted()
    {
        var identity = IdentityKeys.Create();

        Assert.True(PublicKeyValidator.IsValidEncryptionKey(identity.EncryptionPublicKey));
        Assert.True(PublicKeyValidator.IsValidSigningKey(identity.SigningPublicKey));
    }

    [Fact]
    public void Garbage_and_wrong_curve_keys_are_rejected()
    {
        Assert.False(PublicKeyValidator.IsValidEncryptionKey("not-base64"));
        Assert.False(PublicKeyValidator.IsValidSigningKey(Convert.ToBase64String(new byte[32])));
    }
}
