using SecureChat.Core.Contracts;
using SecureChat.Core.Text;

namespace SecureChat.App.Views;

/// <summary>Чип со скрытым адресом markdown-ссылки [текст](url): показывает, куда ведёт ссылка.</summary>
public sealed record LinkChip(string Display, string Href);

/// <summary>
/// Строит FormattedString для текста сообщения: кликабельные ссылки (URL, e-mail,
/// телефон, markdown-метки) и подсветка @упоминаний. Возвращает null, если стилизовать
/// нечего — показывается обычный DisplayText.
/// </summary>
internal static class InlineTextBuilder
{
    public static FormattedString? Build(
        string? text,
        IReadOnlyCollection<string> mentionNames,
        Color linkColor,
        Color mentionColor,
        Action<string>? onLink,
        Action<string>? onMention)
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
                // Обычный кусок текста: внутри него подсвечиваем упоминания.
                // Упомянутые логины внутри URL уже «съедены» ссылкой и не подсветятся.
                foreach (var (chunk, mention) in SpaceMentions.Split(segment.Display, mentionNames))
                {
                    var span = new Span { Text = chunk };
                    if (mention is not null)
                    {
                        // Как в Telegram: полужирное упоминание с подчёркиванием.
                        span.TextColor = mentionColor;
                        span.TextDecorations = TextDecorations.Underline;
                        span.FontAttributes = FontAttributes.Bold;
                        AttachTap(span, () => onMention?.Invoke(mention));
                        styled = true;
                    }

                    formatted.Spans.Add(span);
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

    private static void AttachTap(Span span, Action action)
    {
        span.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => action()),
        });
    }
}
