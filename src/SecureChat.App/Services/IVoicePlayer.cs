namespace SecureChat.App.Services;

/// <summary>
/// Инлайн-плеер голосовых сообщений: подготовка файла, воспроизведение,
/// пауза, перемотка и уведомление о завершении. Позиции — в секундах.
/// </summary>
public interface IVoicePlayer
{
    bool IsPlaying { get; }
    double PositionSeconds { get; }
    double DurationSeconds { get; }

    /// <summary>Срабатывает по завершении воспроизведения (или ошибке) — вне UI-потока.</summary>
    event EventHandler? Completed;

    /// <summary>Открывает файл и готовит к воспроизведению.</summary>
    Task<bool> OpenAsync(string path);

    void Play();
    void Pause();
    void SeekSeconds(double seconds);
    void Release();
}
