using System.Threading;
using System.Threading.Tasks;

namespace SecureChat.App.Services;

/// <summary>Запись аудио для голосовых сообщений.</summary>
public interface IAudioRecorder
{
    /// <summary>Записывает аудио в временный файл и возвращает путь к нему.</summary>
    Task<string?> RecordAsync(CancellationToken cancellationToken = default);

    /// <summary>Прерывает запись и удаляет временный файл.</summary>
    Task CancelAsync();
}
