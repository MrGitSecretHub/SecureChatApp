namespace SecureChat.Core.Contracts;

/// <summary>
/// Отложенный пост пространства: уже зашифрованное сообщение, которое должно
/// уйти на сервер в указанное время. Хранится только на устройстве автора —
/// сервер не знает о существовании отложенных постов.
/// </summary>
public sealed class ScheduledSpacePost
{
    /// <summary>Идентификатор сообщения (совпадает с id в шифротексте).</summary>
    public required string Id { get; set; }

    /// <summary>Пространство, куда отправлять.</summary>
    public required string SpaceId { get; set; }

    /// <summary>Тема форума (если пост в тему).</summary>
    public string? TopicId { get; set; }

    /// <summary>Время отправки (Unix мс, локальное время устройства).</summary>
    public long SendAtUnixMs { get; set; }

    /// <summary>Зашифрованное сообщение (готово к отправке).</summary>
    public required SpaceCipher Cipher { get; set; }

    /// <summary>Краткое превью текста для списка отложенных (не шифруется, только на устройстве).</summary>
    public string Preview { get; set; } = string.Empty;
}
