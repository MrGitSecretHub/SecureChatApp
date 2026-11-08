namespace SecureChat.App.Services;

/// <summary>Цель глубокой ссылки securechat://</summary>
public sealed class DeepLinkTarget
{
    /// <summary>"11" — личный чат, "space" — пространство.</summary>
    public required string ChatKind { get; init; }
    public required string ChatId { get; init; }
    public string? TopicId { get; init; }
    public string? MessageId { get; init; }
}

/// <summary>
/// Сборка и разбор глубоких ссылок securechat://.
/// Форматы:
///   securechat://11/<username>                     — открыть личный чат
///   securechat://11/<username>/<messageId>         — открыть чат и прокрутить к сообщению
///   securechat://space/<spaceId>                   — открыть пространство
///   securechat://space/<spaceId>/<topicId>         — открыть тему форума
///   securechat://space/<spaceId>/<topicId>/<msgId> — открыть тему и прокрутить к сообщению
/// </summary>
public static class DeepLink
{
    private const string Scheme = "securechat";

    public static string BuildMessageLink(string chatKind, string chatId, string messageId,
        string? topicId = null)
    {
        var path = chatKind switch
        {
            "11" => $"11/{Uri.EscapeDataString(chatId)}/{Uri.EscapeDataString(messageId)}",
            "space" => topicId is not null
                ? $"space/{Uri.EscapeDataString(chatId)}/{Uri.EscapeDataString(topicId)}/{Uri.EscapeDataString(messageId)}"
                : $"space/{Uri.EscapeDataString(chatId)}/{Uri.EscapeDataString(messageId)}",
            _ => throw new ArgumentOutOfRangeException(nameof(chatKind), chatKind, null)
        };

        return $"{Scheme}://{path}";
    }

    public static string BuildChatLink(string chatKind, string chatId, string? topicId = null)
    {
        var path = chatKind switch
        {
            "11" => $"11/{Uri.EscapeDataString(chatId)}",
            "space" => topicId is not null
                ? $"space/{Uri.EscapeDataString(chatId)}/{Uri.EscapeDataString(topicId)}"
                : $"space/{Uri.EscapeDataString(chatId)}",
            _ => throw new ArgumentOutOfRangeException(nameof(chatKind), chatKind, null)
        };

        return $"{Scheme}://{path}";
    }

    public static bool TryParse(Uri? uri, out DeepLinkTarget? target)
    {
        target = null;
        if (uri is null || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.Segments
            .Where(s => s.Length > 1)
            .Select(s => s[1..].TrimEnd('/'))
            .Where(s => s.Length > 0)
            .Select(Uri.UnescapeDataString)
            .ToList();

        if (segments.Count < 2)
        {
            return false;
        }

        var chatKind = segments[0];
        var chatId = segments[1];

        if (chatKind != "11" && chatKind != "space")
        {
            return false;
        }

        // space/<id>/<msgId> без topicId: третий сегмент — messageId, а не topicId.
        string? topicId = null;
        string? messageId = null;
        if (segments.Count >= 3)
        {
            if (chatKind == "space" && segments.Count == 3)
            {
                messageId = segments[2];
            }
            else
            {
                topicId = segments[2];
                if (segments.Count >= 4)
                {
                    messageId = segments[3];
                }
            }
        }

        target = new DeepLinkTarget
        {
            ChatKind = chatKind,
            ChatId = chatId,
            TopicId = topicId,
            MessageId = messageId
        };

        return true;
    }
}
