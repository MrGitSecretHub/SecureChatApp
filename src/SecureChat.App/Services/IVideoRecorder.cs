using System.Threading;
using System.Threading.Tasks;

namespace SecureChat.App.Services;

/// <summary>Запись видеокружка для чата.</summary>
public interface IVideoRecorder
{
    /// <summary>Записывает короткое видео в временный файл и возвращает путь к нему.</summary>
    Task<string?> RecordAsync(CancellationToken cancellationToken = default);

    /// <summary>Прерывает запись и удаляет временный файл.</summary>
    Task CancelAsync();
}
