namespace SecureChat.App.Services;

/// <summary>
/// Приложение сейчас на переднем плане. На Android в фоне показываем
/// системное уведомление (оно само играет звук), в foreground — только звук.
/// </summary>
public static class AppLifecycle
{
    public static bool IsForeground { get; set; } = true;
}
