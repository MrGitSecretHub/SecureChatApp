using System.Text.RegularExpressions;

namespace SecureChat.Core.Text;

/// <summary>Вид найденной ссылки: сайт, почта, телефон.</summary>
public enum LinkKind
{
    Web,
    Mail,
    Phone,
}

/// <summary>Фрагмент текста сообщения: обычный (Href null) или ссылка с отображаемым
/// текстом и адресом назначения.</summary>
public sealed class InlineSegment
{
    internal InlineSegment(string display, string? href, LinkKind kind, bool isHiddenTarget)
    {
        Display = display;
        Href = href;
        Kind = kind;
        IsHiddenTarget = isHiddenTarget;
    }

    /// <summary>Текст для показа: метка markdown-ссылки, сокращённый длинный URL или исходный фрагмент.</summary>
    public string Display { get; }

    /// <summary>Полный адрес для открытия (https://…, mailto:…, tel:…). null — обычный текст.</summary>
    public string? Href { get; }

    public LinkKind Kind { get; }

    /// <summary>Markdown-ссылка [текст](url): настоящий адрес в тексте скрыт, для него показывают чип.</summary>
    public bool IsHiddenTarget { get; }

    internal static InlineSegment Plain(string text) => new(text, null, LinkKind.Web, false);
}

/// <summary>
/// Поиск ссылок в тексте сообщения в стиле Telegram: http(s)-URL, адреса www. и домены
/// без схемы (только с известным TLD, чтобы «index.html» или «1.2.3» ссылками не считались),
/// e-mail (mailto), телефоны с «+» (tel) и markdown-ссылки [текст](url).
/// </summary>
public static class TextLinks
{
    /// <summary>Длинные URL показываем короче: домен и начало пути.</summary>
    public const int MaxDisplayLength = 48;

    // Список TLD для доменов без схемы. Без него «Привет.Мир» или «файл.mov» стали бы
    // ссылками; список покрывает популярные gTLD, ccTLD и новые доменные зоны.
    private const string Tlds =
        "com|org|net|edu|gov|mil|int|info|biz|name|pro|app|dev|ai|io|co|me|cc|tv|fm|gg|sh|to|im|" +
        "in|it|de|fr|es|nl|pl|se|no|fi|dk|be|ch|at|cz|sk|hu|gr|pt|ro|bg|hr|si|lt|lv|ee|ie|is|uk|us|" +
        "ca|au|nz|jp|kr|cn|hk|tw|sg|my|th|id|ph|vn|il|tr|ru|su|ua|by|kz|uz|ge|am|az|rs|ba|mk|al|md|" +
        "za|eg|ng|ke|ae|sa|qa|kw|bh|om|jo|lb|iq|ir|pk|bd|np|lk|xyz|top|club|online|site|store|shop|" +
        "tech|live|video|photo|pics|art|blog|wiki|page|space|website|news|digital|network|media|email|" +
        "cloud|host|link|click|stream|radio|audio|watch|plus|one|life|today|center|systems|solutions|" +
        "group|team|works|guide|support|agency|studio|social|design|fun|run|asia|coop|aero|museum|post|" +
        "jobs|cat|eu";

    // Хвост URL: любые символы, кроме пробелов, управляющих, «угловых» скобок и кавычек.
    private const string UrlTail = @"[^\s<>""'`]+";

    private const string BoundaryBefore = @"(?<![@\p{L}\p{N}._~%:/\-])";
    private const string BoundaryAfter = @"(?![\p{L}\p{N}])";

    private static readonly Regex Markdown = new(
        @"\[([^\[\]\n]{1,200}?)\]\((?<url>(?:https?://|www\.)[^\s()<>""'\n]{1,500})\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SchemeUrl = new(
        @"(?<![A-Za-z0-9@])https?://" + UrlTail,
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WwwUrl = new(
        BoundaryBefore + @"www\." + UrlTail,
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Email = new(
        @"(?<![\p{L}\p{N}._%+\-])[A-Za-z0-9][A-Za-z0-9._%+\-]*@(?:[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,24}" +
        BoundaryAfter,
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BareDomain = new(
        BoundaryBefore + @"(?:[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?\.)+(?:" + Tlds + @")" +
        @"(?::\d{2,5})?(?:/" + UrlTail + @")?" + BoundaryAfter,
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Phone = new(
        @"(?<![\p{L}\p{N}@+])\+\d[\d\s().\-]{5,19}\d" + BoundaryAfter,
        RegexOptions.Compiled);

    private static readonly char[] TrailingPunctuation = ['.', ',', ';', ':', '!', '?', '»', '"', '\'', '“', '…'];

    /// <summary>Разбивает текст на последовательные фрагменты: обычные куски и ссылки.
    /// Фрагменты не пересекаются и покрывают весь текст. Markdown-ссылки сохраняют
    /// метку как Display, сырой адрес скрывается (IsHiddenTarget).</summary>
    public static IReadOnlyList<InlineSegment> Segment(string? text)
    {
        var result = new List<InlineSegment>();
        if (string.IsNullOrEmpty(text))
        {
            result.Add(InlineSegment.Plain(text ?? string.Empty));
            return result;
        }

        var matches = Collect(text);
        var position = 0;
        foreach (var match in matches)
        {
            if (match.Index > position)
            {
                result.Add(InlineSegment.Plain(text.Substring(position, match.Index - position)));
            }

            result.Add(match.ToSegment());
            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            result.Add(InlineSegment.Plain(text[position..]));
        }

        return result;
    }

    /// <summary>Все веб-адреса сообщения (https://…, включая цели markdown-ссылок)
    /// без дубликатов, в порядке текста. Для превью, меню и копирования.</summary>
    public static IReadOnlyList<string> WebHrefs(string? text)
    {
        var result = new List<string>();
        foreach (var segment in Segment(text))
        {
            if (segment.Href is { } href && segment.Kind == LinkKind.Web &&
                !result.Contains(href, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(href);
            }
        }

        return result;
    }

    /// <summary>Возвращает адрес для открытия: дополняет схемой https, заворачивает
    /// почту в mailto, телефон — в tel.</summary>
    public static string ToHref(string raw, LinkKind kind) => kind switch
    {
        LinkKind.Mail => "mailto:" + raw,
        LinkKind.Phone => "tel:" + new string([.. raw.Where(c => char.IsDigit(c) || c == '+')]),
        _ => raw.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? raw : "https://" + raw,
    };

    /// <summary>Сокращает длинный адрес для показа: без схемы, с «…» на конце.</summary>
    public static string ShortenHref(string href)
    {
        var value = href;
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0)
        {
            value = value[(scheme + 3)..];
        }

        if (value.Length <= MaxDisplayLength)
        {
            return value;
        }

        var boundary = value.IndexOf('/');
        var host = boundary < 0 ? value : value[..boundary];
        if (host.Length >= MaxDisplayLength - 4)
        {
            return host[..(MaxDisplayLength - 1)] + "…";
        }

        var tail = value[host.Length..];
        return host + tail[..Math.Min(MaxDisplayLength - host.Length - 1, tail.Length - 1)] + "…";
    }

    private sealed record FoundMatch(int Index, int Length, string Text, LinkKind Kind, bool Hidden, string? Label)
    {
        public InlineSegment ToSegment()
        {
            var href = ToHref(Text, Kind);
            if (Hidden)
            {
                return new InlineSegment(Label ?? Text, href, Kind, true);
            }

            var display = Kind == LinkKind.Web && Text.Length > MaxDisplayLength ? ShortenHref(href) : Text;
            return new InlineSegment(display, href, Kind, false);
        }
    }

    private static List<FoundMatch> Collect(string text)
    {
        var matches = new List<FoundMatch>();

        void Add(Regex regex, Func<Match, FoundMatch?> map)
        {
            foreach (Match match in regex.Matches(text))
            {
                var overlaps = matches.Any(existing =>
                    match.Index < existing.Index + existing.Length &&
                    existing.Index < match.Index + match.Length);
                if (overlaps)
                {
                    continue;
                }

                var item = map(match);
                if (item is not null)
                {
                    matches.Add(item);
                }
            }
        }

        // Порядок раундов важнее самой регулярки: ранние «закрашивают» диапазон,
        // поздние в него уже не попадают (домен внутри URL не станет отдельной ссылкой).
        Add(Markdown, m =>
        {
            var target = m.Groups["url"].Value;
            return new FoundMatch(m.Index, m.Length, target, LinkKind.Web, true, m.Groups[1].Value.Trim());
        });

        Add(SchemeUrl, m => TrimAnd(m, LinkKind.Web));
        Add(WwwUrl, m => TrimAnd(m, LinkKind.Web));
        Add(Email, m => new FoundMatch(m.Index, m.Length, m.Value, LinkKind.Mail, false, null));
        Add(BareDomain, m => TrimAnd(m, LinkKind.Web));
        Add(Phone, m =>
        {
            var digits = m.Value.Count(char.IsDigit);
            return digits is >= 7 and <= 15
                ? new FoundMatch(m.Index, m.Length, m.Value, LinkKind.Phone, false, null)
                : null;
        });

        matches.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        return matches;
    }

    private static FoundMatch? TrimAnd(Match match, LinkKind kind)
    {
        var value = TrimTrailing(match.Value);
        return value.Length == 0 ? null : new FoundMatch(match.Index, value.Length, value, kind, false, null);
    }

    /// <summary>Убирает прилипшую к концу пунктацацию и лишние закрывающие скобки.</summary>
    private static string TrimTrailing(string value)
    {
        var text = value.TrimEnd(TrailingPunctuation);
        while (text.Length > 0 && (text[^1] is ')' or ']' or '}'))
        {
            var closer = text[^1];
            var opener = closer switch { ')' => '(', ']' => '[', _ => '{' };
            var open = text.Count(c => c == opener);
            var close = text.Count(c => c == closer);
            if (close > open)
            {
                text = text[..^1].TrimEnd(TrailingPunctuation);
            }
            else
            {
                break;
            }
        }

        return text;
    }
}
