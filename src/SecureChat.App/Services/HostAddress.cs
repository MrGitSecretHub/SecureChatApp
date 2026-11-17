namespace SecureChat.App.Services;

/// <summary>
/// Подсказки адреса сервера для телефона, эмулятора и публичных туннелей.
/// HTTPS — основной транспорт; HTTP и TCP остаются для локальной отладки
/// и для туннелей, которые пробрасывают эти порты наружу.
/// </summary>
public sealed partial class HostAddress
{
    /// <summary>Локальный HTTPS-порт сервера чата.</summary>
    public const int HttpsPort = 5443;

    /// <summary>Локальный HTTP-порт сервера чата (отладка).</summary>
    public const int HttpPort = 5080;

    /// <summary>Публичный HTTPS-порт (стандартный, используется туннелями).</summary>
    public const int PublicHttpsPort = 443;

    /// <summary>Публичный HTTP-порт (стандартный, используется туннелями).</summary>
    public const int PublicHttpPort = 80;

    /// <summary>Локальный TCP-порт сервера чата.</summary>
    public const int TcpPort = 5444;

    /// <summary>Публичный TCP-порт (Playit и подобные туннели).</summary>
    public const int PublicTcpPort = 4444;

    /// <summary>Все поддерживаемые порты сервера.</summary>
    public static readonly int[] SupportedPorts =
    {
        PublicHttpsPort, PublicHttpPort, PublicTcpPort,
        HttpsPort, HttpPort, TcpPort
    };

    /// <summary>Возвращает IP-адрес хоста в локальной сети или null, если определить не удалось.</summary>
    public static partial string? TryGetHostAddress();

    /// <summary>
    /// Возвращает схему для порта: HTTPS для защищённых портов, HTTP для
    /// открытых. Порт 4444 — TCP, для HTTP-адреса не используется.
    /// </summary>
    public static string SchemeForPort(int port) =>
        port is PublicHttpPort or HttpPort ? "http" : "https";

    /// <summary>
    /// Приводит адрес сервера к каноническому виду: добавляет схему по порту
    /// и убирает стандартные порты 80/443 (они уже подразумеваются схемой).
    /// </summary>
    public static string Normalize(string value, int defaultPort = HttpsPort)
    {
        var text = value.Trim().TrimEnd('/');
        if (text.Length == 0)
        {
            return text;
        }

        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return StripDefaultPort(text);
        }

        var host = text;
        var port = defaultPort;
        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var parsed) && SupportedPorts.Contains(parsed))
        {
            host = text[..colon];
            port = parsed;
        }

        // TCP-порт 4444 не является HTTP-адресом — оставляем как есть.
        if (port == PublicTcpPort)
        {
            return text;
        }

        return StripDefaultPort($"{SchemeForPort(port)}://{host}:{port}");
    }

    private static string StripDefaultPort(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            ((uri.Scheme == Uri.UriSchemeHttps && uri.Port == PublicHttpsPort) ||
             (uri.Scheme == Uri.UriSchemeHttp && uri.Port == PublicHttpPort)))
        {
            return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath.TrimEnd('/')}";
        }

        return url;
    }

    /// <summary>Адреса сервера, доступные с телефона или эмулятора.</summary>
    public static string[] Candidates(int port = HttpsPort)
    {
        var scheme = SchemeForPort(port);
        var suffix = port is PublicHttpsPort or PublicHttpPort ? string.Empty : $":{port}";

        var result = new List<string>
        {
            $"{scheme}://10.0.2.2{suffix}",       // Android Studio Emulator (AVD)
            $"{scheme}://192.168.56.1{suffix}",    // LDPlayer, MEmu и другие эмуляторы в NAT-режиме
            $"{scheme}://10.0.3.2{suffix}"        // Genymotion
        };

        var gateway = TryGetHostAddress();
        if (!string.IsNullOrWhiteSpace(gateway) && !result.Any(item => item.Contains(gateway)))
        {
            result.Add($"{scheme}://{gateway}{suffix}");
        }

        return result.ToArray();
    }
}
