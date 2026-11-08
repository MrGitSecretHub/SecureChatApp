using SecureChat.App.Services;
using System.Globalization;

namespace SecureChat.App.Views;

/// <summary>Строка результата глобального поиска по сообщениям.</summary>
public sealed class SearchResultVm
{
    public required string Username { get; init; }
    public required string MessageId { get; init; }
    public required string Title { get; init; }
    public required string Preview { get; init; }
    public required string TimeText { get; init; }

    public static SearchResultVm From(StoredMessageSearchResult source) => new()
    {
        Username = source.Username,
        MessageId = source.MessageId,
        Title = source.Username,
        Preview = (source.IsMine ? "Вы: " : string.Empty) + MessagePreview.Build(source.Text, 160),
        TimeText = DateTimeOffset
            .FromUnixTimeMilliseconds(source.SentAtUnixMs)
            .ToLocalTime()
            .ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
    };
}
