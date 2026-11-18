using System.Text.RegularExpressions;
using SecureChat.Core.Contracts;
using SecureChat.Core.Text;

namespace SecureChat.App.Views;

/// <summary>Чип со скрытым адресом markdown-ссылки [текст](url): показывает, куда ведёт ссылка.</summary>
public sealed record LinkChip(string Display, string Href);

/// <summary>
/// Строит FormattedString для текста сообщения: кликабельные ссылки (URL, e-mail,
/// телефон, markdown-метки), @упоминания, #хештеги и лёгкая разметка
/// (**жирный**, *курсив*, `код`, ~~зачёркнутый~~).
/// </summary>
internal static class InlineTextBuilder
{
    private static readonly Regex Markup = new(
        @"\*\*(?<bold>.+?)\*\*|~~(?<strike>.+?)~~|`(?<code>[^`\n]+)`|" +
        @"(?<italic>\*[^*\n]+\*|_[^_\n]+_)|(?<tag>#[^\s#,.!?()\[\]]+)",
        RegexOptions.Compiled);

    public static FormattedString? Build(
        string? text,
        IReadOnlyCollection<string> mentionNames,
        Color linkColor,
        Color mentionColor,
        Action<string>? onLink,
        Action<string>? onMention,
        Color? hashtagColor = null,
        Action<string>? onHashtag = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var formatted = new FormattedString();
        var styled = false;

        foreach (var segment in TextLinks.Segment(text))
        {
            if (segment.Href is null)
            {
                foreach (var (chunk, mention) in SpaceMentions.Split(segment.Display, mentionNames))
                {
                    if (mention is not null)
                    {
                        var span = new Span { Text = chunk };
                        span.TextColor = mentionColor;
                        span.TextDecorations = TextDecorations.Underline;
                        span.FontAttributes = FontAttributes.Bold;
                        AttachTap(span, () => onMention?.Invoke(mention));
                        formatted.Spans.Add(span);
                        styled = true;
                    }
                    else
                    {
                        styled |= AppendStyled(formatted, chunk, mentionColor, hashtagColor, onHashtag);
                    }
                }

                continue;
            }

            var linkSpan = new Span
            {
                Text = segment.Display,
                TextColor = linkColor,
            };
            AttachTap(linkSpan, () => onLink?.Invoke(segment.Href));
            formatted.Spans.Add(linkSpan);
            styled = true;
        }

        return styled ? formatted : null;
    }

    /// <summary>Разбирает один «обычный» кусок на спаны с разметкой и хештегами.</summary>
    private static bool AppendStyled(
        FormattedString formatted,
        string text,
        Color accentColor,
        Color? hashtagColor,
        Action<string>? onHashtag)
    {
        var styled = false;
        var pos = 0;
        foreach (Match match in Markup.Matches(text))
        {
            if (match.Index > pos)
            {
                formatted.Spans.Add(new Span { Text = text.Substring(pos, match.Index - pos) });
            }

            var group = match.Groups["bold"].Success ? "bold"
                : match.Groups["strike"].Success ? "strike"
                : match.Groups["code"].Success ? "code"
                : match.Groups["italic"].Success ? "italic"
                : "tag";

            var value = match.Groups[group].Value;
            var span = new Span { Text = value };

            switch (group)
            {
                case "bold":
                    span.FontAttributes = FontAttributes.Bold;
                    break;
                case "strike":
                    span.TextDecorations = TextDecorations.Strikethrough;
                    break;
                case "code":
                    if (Application.Current?.Resources.TryGetValue("FontFamilyMono", out var mono) == true &&
                        mono is string font)
                    {
                        span.FontFamily = font;
                    }
                    span.TextColor = accentColor;
                    break;
                case "italic":
                    span.FontAttributes = FontAttributes.Italic;
                    // убираем звёздочки/подчёркивания вокруг текста
                    span.Text = value.Length >= 2 && (value[0] == '*' || value[0] == '_')
                        ? value[1..^1]
                        : value;
                    break;
                case "tag":
                    span.TextColor = hashtagColor ?? accentColor;
                    span.FontAttributes = FontAttributes.Bold;
                    var tag = value.TrimStart('#');
                    if (!string.IsNullOrEmpty(tag))
                    {
                        AttachTap(span, () => onHashtag?.Invoke(tag));
                    }
                    break;
            }

            formatted.Spans.Add(span);
            styled = true;
            pos = match.Index + match.Length;
        }

        if (pos < text.Length)
        {
            formatted.Spans.Add(new Span { Text = text[pos..] });
        }

        return styled;
    }

    private static void AttachTap(Span span, Action action)
    {
        span.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => action()),
        });
    }
}
