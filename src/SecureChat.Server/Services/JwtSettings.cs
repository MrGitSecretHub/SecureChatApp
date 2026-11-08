using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace SecureChat.Server.Services;

public sealed class JwtSettings
{
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public required byte[] KeyBytes { get; init; }

    public static JwtSettings Load(IConfiguration configuration, string dataDirectory)
    {
        var issuer = configuration["Jwt:Issuer"] ?? "SecureChat";
        var audience = configuration["Jwt:Audience"] ?? "SecureChat";
        var key = configuration["Jwt:Key"];

        if (string.IsNullOrWhiteSpace(key))
        {
            var keyPath = Path.Combine(dataDirectory, "jwt.key");

            if (File.Exists(keyPath))
            {
                key = File.ReadAllText(keyPath).Trim();
            }
            else
            {
                Directory.CreateDirectory(dataDirectory);
                key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
                File.WriteAllText(keyPath, key);
            }
        }

        return new JwtSettings
        {
            Issuer = issuer,
            Audience = audience,
            KeyBytes = Encoding.UTF8.GetBytes(key)
        };
    }
}
