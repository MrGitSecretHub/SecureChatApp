using SecureChat.Core.Crypto;
using Xunit;

namespace SecureChat.Core.Tests;

public sealed class PasscodeHasherTests
{
    [Fact]
    public void Verify_AcceptsTheSamePasscode()
    {
        var (salt, hash) = PasscodeHasher.Create("2580");

        Assert.True(PasscodeHasher.Verify("2580", salt, hash));
    }

    [Fact]
    public void Verify_RejectsAnotherPasscode()
    {
        var (salt, hash) = PasscodeHasher.Create("2580");

        Assert.False(PasscodeHasher.Verify("2581", salt, hash));
    }

    [Fact]
    public void Create_UsesFreshSaltEveryTime()
    {
        var first = PasscodeHasher.Create("2580");
        var second = PasscodeHasher.Create("2580");

        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Hash, second.Hash);
    }
}
