namespace SecureChat.Core.Contracts;

/// <summary>
/// Зашифрованный конверт сообщения. Сервер видит только эти поля и не может
/// прочитать текст. Подпись покрывает заголовок, nonce, ciphertext и tag.
/// </summary>
public sealed class EncryptedEnvelope
{
    public required string Id { get; set; }
    public required string SenderId { get; set; }
    public required string SenderUsername { get; set; }
    public required string RecipientId { get; set; }
    public required string RecipientUsername { get; set; }
    public long SentAtUnixMs { get; set; }
    public required string SenderEphemeralPublicKey { get; set; }
    public required string Nonce { get; set; }
    public required string Ciphertext { get; set; }
    public required string Tag { get; set; }
    public required string Signature { get; set; }

    /// <summary>Время истечения сообщения (Unix мс). Если истекло — сервер не должен доставлять/хранить.</summary>
    public long? ExpireAtUnixMs { get; set; }
}

/// <summary>Полезная нагрузка, которая находится внутри ciphertext.</summary>
public sealed class MessagePayload
{
    public string Text { get; set; } = string.Empty;
    public long SentAtUnixMs { get; set; }
    public string Kind { get; set; } = MessageKinds.Text;
    public MediaAttachment? Media { get; set; }

    /// <summary>Сообщение закреплено в чате.</summary>
    public bool IsPinned { get; set; }

    /// <summary>Реакции на сообщение (эмодзи + автор).</summary>
    public List<Reaction> Reactions { get; set; } = new();

    /// <summary>Переслано ли сообщение.</summary>
    public bool IsForwarded { get; set; }

    /// <summary>Ответ на сообщение: идентификатор исходного сообщения.</summary>
    public string? ReplyToMessageId { get; set; }

    /// <summary>Текст цитаты: текст исходного сообщения или превью медиа.</summary>
    public string? ReplyToText { get; set; }

    /// <summary>Имя вложения исходного сообщения, если отвечают на медиа.</summary>
    public string? ReplyToFileName { get; set; }
}

public static class MessageKinds
{
    public const string Text = "text";
    public const string Image = "image";
    public const string File = "file";
    public const string Voice = "voice";
    public const string VideoCircle = "videocircle";

    /// <summary>Стикер: текст сообщения — эмодзи, отображается крупно без пузыря.</summary>
    public const string Sticker = "sticker";

}

/// <summary>
/// Параметры вложения. Находится внутри зашифрованного конверта, поэтому
/// сервер не видит ни имя файла, ни ключ, ни содержимое.
/// </summary>
public sealed class MediaAttachment
{
    public required string Id { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public required string Key { get; set; }
    public required string Nonce { get; set; }
    public required string Tag { get; set; }
}

public static class MediaLimits
{
    /// <summary>Максимальный размер исходного файла до шифрования (150 МБ).</summary>
    public const int MaxBytes = 150 * 1024 * 1024;

    /// <summary>
    /// Порог для встроенного видео-плеера в пузыре: рендер через data-URI съедает
    /// память пропорционально размеру (base64 ~133% от файла). Большие видео
    /// открываются нативным плеером из кэш-файла — иначе OOM на телефоне.
    /// </summary>
    public const int InlineVideoMaxBytes = 24 * 1024 * 1024;
}
