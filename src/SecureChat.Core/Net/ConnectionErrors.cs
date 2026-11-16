namespace SecureChat.Core.Net;

/// <summary>
/// Распознавание ошибок подключения. Для них приложение не показывает
/// модальное окно: связь часто отваливается на несколько секунд, и десяток
/// всплывающих окон подряд только раздражает. Вместо этого в интерфейсе
/// остаётся постоянный индикатор офлайн-режима.
/// </summary>
public static class ConnectionErrors
{
    private static readonly string[] Markers =
    {
        "Нет подключения",
        "нет подключения",
        "Нет соединения",
        "нет соединения",
        "соединение прервано",
        "Соединение прервано",
        "подключение не удалось",
        "Подключение не удалось",
        "WebSocket недоступен",
        "WebSocket",
        "сервер недоступен",
        "Сервер недоступен",
        "Не удалось подключиться",
        "не удалось подключиться",
        "Connection refused",
        "connection refused",
        "timeout",
        "Timeout",
        "таймаут",
        "прервано"
    };

    /// <summary>Похоже ли сообщение на проблему с подключением.</summary>
    public static bool IsConnectionProblem(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return Markers.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Короткий текст для тихого показа вместо окна.</summary>
    public const string OfflineHint =
        "⚡ Нет связи с сервером — сообщения сохранятся и уйдут после подключения.";
}
