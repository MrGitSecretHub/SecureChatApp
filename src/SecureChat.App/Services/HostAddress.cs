namespace SecureChat.App.Services;

/// <summary>
/// Подсказки по адресу сервера для подключения с телефона или эмулятора.
/// По умолчанию предлагается HTTPS (порт 5443) — сервер поднимает его с LAN-
/// сертификатом, доверенным приложению через network_security_config.xml.
/// HTTP (порт 5080) остаётся только для локальной отладки на эмуляторе.
/// </summary>
public sealed partial class HostAddress
{
    /// <summary>Порт HTTPS-сервера чата.</summary>
    public const int HttpsPort = 5443;

    /// <summary>Порт TCP-транспорта (для «Избранного» и отладки).</summary>
    public const int TcpPort = 5444;

    /// <summary>Возвращает IP-адрес хоста в локальной сети или null, если определить не удалось.</summary>
    public static partial string? TryGetHostAddress();

    /// <summary>Адреса сервера, доступные с телефона или эмулятора (HTTPS).</summary>
    public static string[] Candidates(int port = HttpsPort)
    {
        var scheme = port == 5080 ? "http" : "https";
        var result = new List<string>
        {
            $"{scheme}://10.0.2.2:{port}",       // Android Studio Emulator (AVD)
            $"{scheme}://192.168.56.1:{port}",    // LDPlayer, MEmu и другие эмуляторы в NAT-режиме
            $"{scheme}://10.0.3.2:{port}"        // Genymotion
        };

        var gateway = TryGetHostAddress();
        if (!string.IsNullOrWhiteSpace(gateway) && !result.Any(item => item.Contains(gateway)))
        {
            result.Add($"{scheme}://{gateway}:{port}");
        }

        return result.ToArray();
    }
}
