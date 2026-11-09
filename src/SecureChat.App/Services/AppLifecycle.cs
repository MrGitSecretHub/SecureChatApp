namespace SecureChat.App.Services;

/// <summary>
/// Приложение сейчас на переднем плане. На Android в фоне показываем
/// системное уведомление (оно само играет звук), в foreground — только звук.
///
/// Смена состояния порождает события, на которые подписаны, например,
/// BackgroundKeepAlive (не даёт Android засыпать сеть в фоне) и ChatManager
/// (мгновенное переподключение при возврате на передний план).
/// </summary>
public static class AppLifecycle
{
    private static bool _isForeground = true;

    public static bool IsForeground
    {
        get => _isForeground;
        set
        {
            if (_isForeground == value)
            {
                return;
            }

            _isForeground = value;

            if (value)
            {
                EnteredForeground?.Invoke();
                BackgroundKeepAlive.Release();
            }
            else
            {
                EnteredBackground?.Invoke();
                BackgroundKeepAlive.Acquire();
            }
        }
    }

    /// <summary>Приложение снова на переднем плане.</summary>
    public static event Action? EnteredForeground;

    /// <summary>Приложение ушло в фон (Android: минимизация/сворачивание).</summary>
    public static event Action? EnteredBackground;
}
