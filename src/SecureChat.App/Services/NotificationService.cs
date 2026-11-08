namespace SecureChat.App.Services;

/// <summary>
/// Системные уведомления о новых сообщениях. На Windows используются
/// AppNotification (Windows App SDK), на остальных платформах — заглушка.
/// </summary>
public sealed partial class NotificationService
{
    private string? _pendingUsername;

    public event Action<string>? Activated;

    public partial void Initialize();

    /// <summary>Запрашивает разрешение на уведомления (Android 13+).</summary>
    public partial void EnsurePermission();

    /// <returns>true, если системное уведомление удалось показать.</returns>
    public partial bool ShowNotification(
        string title,
        string body,
        string username,
        int unreadCount,
        bool silent);

    public partial void ActivateWindow();

    /// <summary>Уведомление нажато: запоминаем чат и сообщаем подписчикам.</summary>
    public void ActivateFromNotification(string username) => RaiseActivated(username);

    /// <summary>Забирает чат из уведомления, которое пришло до готовности UI.</summary>
    public string? ConsumePendingUsername()
    {
        var username = _pendingUsername;
        _pendingUsername = null;
        return username;
    }

    public void RememberPendingUsername(string username) => _pendingUsername = username;

    private void RaiseActivated(string username)
    {
        if (Activated is null)
        {
            // Приложение ещё не построило экран (холодный старт) — откроем чат позже.
            _pendingUsername = username;
            return;
        }

        Activated.Invoke(username);
    }
}
