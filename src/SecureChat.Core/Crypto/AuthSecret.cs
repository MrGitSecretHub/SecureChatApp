using System.Security.Cryptography;
using System.Text;

namespace SecureChat.Core.Crypto;

/// <summary>
/// Клиентская предварительная обработка пароля. Сервер никогда не получает
/// исходный пароль: по сети передаётся только производная «auth secret».
/// На сервере она дополнительно хешируется случайной солью.
/// </summary>
public static class AuthSecret
{
    private const int Iterations = 210_000;
    private const int OutputLength = 32;

    public static string Derive(string password, string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        var salt = Encoding.UTF8.GetBytes(
            "SecureChat.Auth.v1|" + username.Trim().ToLowerInvariant());

        var secret = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            OutputLength);

        return Convert.ToBase64String(secret);
    }
}
