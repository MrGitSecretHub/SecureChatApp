namespace SecureChat.App.Services;

/// <summary>
/// Короткий звук уведомления о новом сообщении: выбранный встроенный рингтон
/// (оригинальные синтезированные тоны) или системный звук платформы.
/// Рингтон задаётся настройками (AppSession.RingtoneId, PrivacyStore).
/// </summary>
public sealed partial class SoundService
{
    /// <summary>Не чаще одного звука в секунду: пачка сообщений после
    /// переподключения не должна превращаться в очередь сигналов.</summary>
    private const long MinIntervalMs = 1000;

    private long _lastPlayedMs;

    private static string EffectiveRingtoneId =>
        AppServices.Get<AppSession>().RingtoneId is { Length: > 0 } id
            ? id
            : Ringtones.DefaultId;

    public void PlayNotification()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastPlayedMs);
        if (now - last < MinIntervalMs)
        {
            return;
        }

        Interlocked.Exchange(ref _lastPlayedMs, now);
        PlayCore(EffectiveRingtoneId);
    }

    /// <summary>Прослушать конкретный рингтон (например, из настроек),
    /// без ограничения частоты.</summary>
    public void Play(string ringtoneId) =>
        PlayCore(Ringtones.Normalize(ringtoneId));

    private partial void PlayCore(string ringtoneId);
}
