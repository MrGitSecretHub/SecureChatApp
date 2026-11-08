namespace SecureChat.App.Services;

/// <summary>
/// Подсказки по адресу сервера для подключения с телефона или эмулятора.
/// </summary>
public sealed partial class HostAddress
{
    /// <summary>Возвращает IP-адрес хоста в локальной сети или null, если определить не удалось.</summary>
    public static partial string? TryGetHostAddress();

    /// <summary>Типичные адреса хоста, доступные с эмуляторов Android.</summary>
    public static string[] Candidates(int port = 5444)
    {
        var result = new List<string>
        {
            $"10.0.2.2:{port}",       // Android Studio Emulator (AVD)
            $"192.168.56.1:{port}",    // LDPlayer, MEmu и другие эмуляторы в NAT-режиме
            $"10.0.3.2:{port}"        // Genymotion
        };

        var gateway = TryGetHostAddress();
        if (!string.IsNullOrWhiteSpace(gateway) && !result.Any(item => item.Contains(gateway)))
        {
            result.Add($"{gateway}:{port}");
        }

        return result.ToArray();
    }
}
