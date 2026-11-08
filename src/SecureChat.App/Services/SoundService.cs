namespace SecureChat.App.Services;

/// <summary>
/// Короткий звук уведомления о новом сообщении. Используется системный звук
/// платформы, поэтому приложение не тащит собственные аудиофайлы.
/// </summary>
public sealed partial class SoundService
{
    /// <summary>Не чаще одного звука в секунду: пачка сообщений после
    /// переподключения не должна превращаться в очередь сигналов.</summary>
    private const long MinIntervalMs = 1000;

    private long _lastPlayedMs;

    public void PlayNotification()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastPlayedMs);
        if (now - last < MinIntervalMs)
        {
            return;
        }

        Interlocked.Exchange(ref _lastPlayedMs, now);
        PlayNotificationCore();
    }

    private partial void PlayNotificationCore();
}
