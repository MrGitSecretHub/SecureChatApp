using System.Text.Json;
using System.Text.RegularExpressions;

namespace SecureChat.Core.Contracts;

/// <summary>Вложение пространства: файл шифруется своим ключом, а ключ лежит внутри зашифрованного содержимого.</summary>
public sealed class SpaceMediaAttachment
{
    public required string Id { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public required string Key { get; set; }
    public required string Nonce { get; set; }
    public required string Tag { get; set; }
}

/// <summary>Содержимое сообщения пространства. Шифруется общим ключом пространства целиком.</summary>
public sealed class SpaceMessagePayload
{
    /// <summary>"text", "image" или "file".</summary>
    public string Kind { get; set; } = "text";
    public string Text { get; set; } = string.Empty;
    public string? ReplyToMessageId { get; set; }
    public string? ReplyToText { get; set; }
    public bool IsForwarded { get; set; }
    public SpaceMediaAttachment? Media { get; set; }

    /// <summary>
    /// Логины упомянутых участников (@username). Хранится внутри зашифрованного
    /// содержимого — сервер не знает, кого упомянули.
    /// </summary>
    public List<string> Mentions { get; set; } = new();

    /// <summary>
    /// Идентификатор поста, к которому относится комментарий. Заполнено только у
    /// комментариев к постам каналов — такие сообщения не показываются в ленте,
    /// а собираются в ветку обсуждения под постом.
    /// </summary>
    public string? CommentToMessageId { get; set; }
}

/// <summary>Разбор упоминаний @username внутри текста сообщения пространства.</summary>
public static class SpaceMentions
{
    /// <summary>Максимальное число упоминаний в одном сообщении.</summary>
    public const int MaxMentions = 16;

    // Токен @username: только латиница, цифры, подчёркивание; перед @ — начало
    // текста, пробел или знак, не входящий в имя.
    private static readonly Regex Token = new(
        @"(?<![A-Za-z0-9_@])@([A-Za-z0-9_]{1,32})",
        RegexOptions.Compiled);

    /// <summary>Имена участников реально упомянуты в тексте: порядок текста, без дубликатов.</summary>
    public static List<string> Parse(string? text, IEnumerable<string> knownUsernames)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        var known = new HashSet<string>(knownUsernames ?? [], StringComparer.OrdinalIgnoreCase);
        if (known.Count == 0)
        {
            return result;
        }

        foreach (Match match in Token.Matches(text))
        {
            var typed = match.Groups[1].Value;
            var name = known.FirstOrDefault(item =>
                string.Equals(item, typed, StringComparison.OrdinalIgnoreCase));

            if (name is null)
            {
                continue;
            }

            if (!result.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(name);
                if (result.Count >= MaxMentions)
                {
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>Все @username-токены в тексте (порядок текста, без дубликатов).
    /// Нужно для личных чатов: там нет списка участников, подсвечиваем любой логин после «@» — как в Telegram.</summary>
    public static List<string> AllTokens(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        foreach (Match match in Token.Matches(text))
        {
            var name = match.Groups[1].Value;
            if (!result.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(name);
                if (result.Count >= MaxMentions)
                {
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>Упомянули ли конкретного участника (учитывается логин участника, регистронезависимо).</summary>
    public static bool HasMentionFor(SpaceMessagePayload? payload, string username)
    {
        if (payload is null || string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        return payload.Mentions
            .Any(name => string.Equals(name, username, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Разбивает текст на куски: обычные или упоминания (для подсветки в интерфейсе).</summary>
    public static List<(string Text, string? Mention)> Split(string? text, IReadOnlyCollection<string> mentions)
    {
        var segments = new List<(string, string?)>();
        if (string.IsNullOrEmpty(text))
        {
            return segments;
        }

        var mentioned = mentions is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(mentions, StringComparer.OrdinalIgnoreCase);

        var position = 0;
        foreach (Match match in Token.Matches(text))
        {
            if (match.Index <= position)
            {
                continue;
            }

            segments.Add((text[position..match.Index], null));
            var name = match.Groups[1].Value;
            segments.Add((match.Value, mentioned.Contains(name) ? name : null));
            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            segments.Add((text[position..], null));
        }

        return segments;
    }
}

/// <summary>Кодирование содержимого. Старые сообщения были обычным текстом без префикса.</summary>
public static class SpacePayloadCodec
{
    private const string Prefix = "\u0001sp1:";

    public static string Encode(SpaceMessagePayload payload) =>
        Prefix + JsonSerializer.Serialize(payload);

    public static SpaceMessagePayload Decode(string plaintext)
    {
        if (plaintext.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return JsonSerializer.Deserialize<SpaceMessagePayload>(plaintext[Prefix.Length..])
                ?? new SpaceMessagePayload();
        }

        return new SpaceMessagePayload { Text = plaintext };
    }
}
