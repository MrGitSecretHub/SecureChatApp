using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace SecureChat.App.Services;

/// <summary>
/// Pinning TLS-сертификата сервера по отпечатку SHA-1.
/// Самоподписанный LAN-сертификат и сертификат публичного туннеля не всегда
/// попадают в доверенные корни Android/Windows — вместо этого клиент явно
/// пропускает только ожидаемый сертификат. Остальные проверяются обычным образом.
/// </summary>
public static class ServerCertificatePin
{
    /// <summary>Отпечаток текущего LAN-сертификата (CN=SecureChat LAN server).</summary>
    public const string Thumbprint = "D51699FADDAE05F1C013BB489BC87529545DADE2";

    public static bool Validate(
        object? sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (certificate is null)
        {
            return errors == SslPolicyErrors.None;
        }

        // Наш сертификат — принимаем всегда (в том числе при несовпадении
        // имени узла или отсутствии в системных корнях).
        if (string.Equals(certificate.GetCertHashString(), Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Все остальные сертификаты — обычная проверка системы.
        return errors == SslPolicyErrors.None;
    }
}
