namespace SecureChat.App.Views;

/// <summary>
/// Строка галереи медиа пространства: либо заголовок-дата, либо само вложение.
/// </summary>
public sealed class SpaceMediaItemVm
{
    private SpaceMediaItemVm(bool isHeader, string title, SpaceMessageVm? message)
    {
        IsHeader = isHeader;
        Title = title;
        Message = message;
    }

    /// <summary>Заголовок группы (дата).</summary>
    public bool IsHeader { get; }

    /// <summary>Обычное вложение.</summary>
    public bool IsItem => !IsHeader;

    /// <summary>Текст заголовка или имя файла.</summary>
    public string Title { get; }

    /// <summary>Сообщение с вложением (для обычных строк).</summary>
    public SpaceMessageVm? Message { get; }

    /// <summary>Кто прислал (для вложения).</summary>
    public string Author => Message?.AuthorText ?? string.Empty;

    /// <summary>Время отправки (для вложения).</summary>
    public string Time => Message?.TimeText ?? string.Empty;

    public static SpaceMediaItemVm Header(string title) => new(true, title, null);

    public static SpaceMediaItemVm Item(SpaceMessageVm message) => new(false, message.FileName, message);
}
