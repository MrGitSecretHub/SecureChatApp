using System.Security.Cryptography;

namespace SecureChat.Server.Services;

/// <summary>
/// Серверное хеширование auth-secret (PBKDF2-SHA256, случайная соль).
/// Исходный пароль на сервер не передаётся: клиент присылает уже производную
/// величину, а сервер хранит только её хеш.
/// </summary>
public sealed class PasswordHasher
{
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    private static readonly byte[] DummySalt = RandomNumberGenerator.GetBytes(SaltSize);
    private static readonly byte[] DummyHash = Hash("dummy-password-for-timing", DummySalt);

    public (byte[] Hash, byte[] Salt) Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        return (Hash(secret, salt), salt);
    }

    public bool Verify(string secret, byte[] expectedHash, byte[] salt)
    {
        var actual = Hash(secret, salt);
        return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }

    /// <summary>Выравнивает время ответа, когда пользователь не найден.</summary>
    public void VerifyDummy(string secret) => _ = Verify(secret, DummyHash, DummySalt);

    private static byte[] Hash(string secret, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            secret,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);
}
