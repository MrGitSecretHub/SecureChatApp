using System.Security.Cryptography;

namespace SecureChat.Core.Crypto;

/// <summary>
/// Хэш кода-пароля приложения (PBKDF2-SHA256). На устройстве хранятся только соль и хэш.
/// </summary>
public static class PasscodeHasher
{
    public const int Iterations = 150_000;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static (string Salt, string Hash) Create(string passcode)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(passcode, salt, HashBytes);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool Verify(string passcode, string saltBase64, string hashBase64)
    {
        var salt = Convert.FromBase64String(saltBase64);
        var expected = Convert.FromBase64String(hashBase64);
        var actual = Derive(passcode, salt, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(string passcode, byte[] salt, int length) =>
        Rfc2898DeriveBytes.Pbkdf2(passcode, salt, Iterations, HashAlgorithmName.SHA256, length);
}
