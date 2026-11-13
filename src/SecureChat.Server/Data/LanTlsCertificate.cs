using System.Security.Cryptography.X509Certificates;

namespace SecureChat.Server.Data;

/// <summary>
/// HTTPS-сертификат для подключения клиентов по LAN. Файлы лежат в App_Data (папка не попадает в git):
/// lan-tls.pfx — сертификат с адресами сервера в SAN, lan-tls.password.txt — пароль к PFX.
/// Выписывается скриптом tools/New-LanTlsCertificate.ps1.
/// </summary>
public static class LanTlsCertificate
{
    public const string PfxFileName = "lan-tls.pfx";
    public const string PasswordFileName = "lan-tls.password.txt";

    /// <summary>Загружает сертификат, если он выписан; иначе null — Kestrel использует dev-сертификат localhost.</summary>
    public static X509Certificate2? TryLoad(string dataDirectory)
    {
        var pfxPath = Path.Combine(dataDirectory, PfxFileName);
        var passwordPath = Path.Combine(dataDirectory, PasswordFileName);
        if (!File.Exists(pfxPath) || !File.Exists(passwordPath))
        {
            return null;
        }

        var password = File.ReadAllText(passwordPath).Trim();
        return new X509Certificate2(pfxPath, password);
    }
}
