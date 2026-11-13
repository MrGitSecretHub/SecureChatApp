using SecureChat.App.Services;

namespace SecureChat.App.Views;

/// <summary>
/// Строка вкладки «Медиа и файлы»: либо заголовок-дата, либо само вложение.
/// </summary>
public sealed class MediaListItemVm
{
    private MediaListItemVm(bool isHeader, string title, MessageVm? message)
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
    public MessageVm? Message { get; }

    public static MediaListItemVm Header(string title) => new(true, title, null);

    public static MediaListItemVm Item(MessageVm message) => new(false, message.FileName, message);
}
