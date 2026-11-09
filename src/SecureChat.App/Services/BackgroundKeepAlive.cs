namespace SecureChat.App.Services;

/// <summary>
/// Не даёт ОС усыплять приложение, когда оно в фоне. На Android удерживает
/// partial wake lock и Wi-Fi lock: без них Android через несколько секунд
/// приостанавливает сеть, SignalR-соединение рвётся и уведомления
/// прекращаются. На Windows — заглушка ( мониторы не засыпают, пока работает
/// процесс).
/// </summary>
public sealed partial class BackgroundKeepAlive
{
    public static BackgroundKeepAlive Instance { get; } = new();

    private BackgroundKeepAlive()
    {
    }

    public static void Acquire() => AcquireCore();

    public static void Release() => ReleaseCore();

    private static partial void AcquireCore();

    private static partial void ReleaseCore();
}
