using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SecureChat.App.Services;

/// <summary>
/// Генерирует превью ссылок (Open Graph) на стороне клиента.
/// Сервер SecureChat не участвует — владелец сайта увидит IP устройства,
/// которое открыло чат. По умолчанию функция включена, тумблер — в настройках.
/// </summary>
public sealed class LinkPreviewService
{
    // <meta ...>: ключ og:/twitter: в любом порядке атрибутов.
    private static readonly Regex MetaTag =
        new(@"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PropertyAttr =
        new(@"(?:property|name)\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ContentAttr =
        new(@"content\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TitleRegex =
        new(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex YouTubeId =
        new(@"(?:youtube\.com/watch\?(?:[^#\s]*&)?v=|youtu\.be/|youtube\.com/shorts/|youtube\.com/embed/)([A-Za-z0-9_-]{6,12})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const int MaxReadBytes = 256 * 1024;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly Dictionary<string, LinkPreview?> _cache = new(StringComparer.OrdinalIgnoreCase);

    // Запросы сериализуются: словарь-кэш не потокобезопасен, и одновременных
    // обращений к сайтам лучше не создавать.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LinkPreviewService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; SecureChat/1.0; +https://secure.chat/bot)");
    }

    /// <summary>Возвращает кэшированное превью или загружает его с сайта.</summary>
    public async Task<LinkPreview?> GetPreviewAsync(string url, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(url, out var cached))
            {
                return cached;
            }

            return _cache[url] = await FetchPreviewAsync(url, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LinkPreview?> FetchPreviewAsync(string url, CancellationToken ct)
    {
        LinkPreview? result = null;
        try
        {
            // YouTube и похожие сайты не отдают og-теги ботам, но у них есть
            // открытая oEmbed-выдача: заголовок, канал и обложка.
            var youTube = YouTubeId.Match(url);
            if (youTube.Success)
            {
                result = await FetchYouTubePreviewAsync(url, youTube.Groups[1].Value, ct);
            }

            result ??= await FetchHtmlPreviewAsync(url, ct);
        }
        catch
        {
            // Сайт недоступен или некорректен — превью просто не будет.
        }

        return result;
    }

    private async Task<LinkPreview?> FetchHtmlPreviewAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var html = await ReadHeadAsync(stream, ct);
        var meta = ParseMetaTags(html);

        var title = meta.GetValueOrDefault("title");
        if (string.IsNullOrWhiteSpace(title))
        {
            var m = TitleRegex.Match(html);
            title = m.Success ? m.Groups[1].Value.Trim() : null;
        }

        var description = meta.GetValueOrDefault("description");
        var siteName = meta.GetValueOrDefault("site_name");
        if (!string.IsNullOrWhiteSpace(siteName) && string.IsNullOrWhiteSpace(title))
        {
            title = siteName;
        }

        var imageUrl = ResolveImageUrl(url, meta.GetValueOrDefault("image"));

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(description) &&
            imageUrl is null)
        {
            return null;
        }

        return new LinkPreview
        {
            Url = url,
            Title = title?.Trim(),
            Description = description?.Trim(),
            ImageUrl = imageUrl,
            Domain = new Uri(url).Host,
        };
    }

    private async Task<LinkPreview?> FetchYouTubePreviewAsync(string pageUrl, string videoId, CancellationToken ct)
    {
        try
        {
            var watch = Uri.EscapeDataString($"https://www.youtube.com/watch?v={videoId}");
            using var response = await _http.GetAsync(
                $"https://www.youtube.com/oembed?url={watch}&format=json", ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = json.RootElement;
            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var channel = root.TryGetProperty("author_name", out var a) ? a.GetString() : null;
            var image = root.TryGetProperty("thumbnail_url", out var th) &&
                        Uri.TryCreate(th.GetString(), UriKind.Absolute, out _)
                ? th.GetString()
                : $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";

            return new LinkPreview
            {
                Url = pageUrl,
                Title = title.Trim(),
                Description = string.IsNullOrWhiteSpace(channel) ? null : $"Канал: {channel}",
                ImageUrl = image,
                Domain = new Uri(pageUrl).Host,
            };
        }
        catch
        {
            // oEmbed недоступен — попробуем обычный разбор страницы.
            return null;
        }
    }

    /// <summary>Читает ответ до </head> или до лимита: крупные SPA-страницы
    /// одним куском не прочитать, а мета-теги живут в голове.</summary>
    private static async Task<string> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new byte[16 * 1024];
        while (sb.Length < MaxReadBytes)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }

            sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
            if (sb.ToString().IndexOf("</head>", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                break;
            }
        }

        return sb.ToString();
    }

    /// <summary>og:* и twitter:* собираются в один словарь по коротким именам
    /// (title, description, image, site_name); первая встреча важнее повторов.</summary>
    private static Dictionary<string, string> ParseMetaTags(string html)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match tag in MetaTag.Matches(html))
        {
            var key = PropertyAttr.Match(tag.Value);
            if (!key.Success)
            {
                continue;
            }

            var name = key.Groups[1].Value;
            if (name.StartsWith("og:", StringComparison.OrdinalIgnoreCase))
            {
                name = name[3..];
            }
            else if (name.StartsWith("twitter:", StringComparison.OrdinalIgnoreCase))
            {
                name = name["twitter:".Length..];
            }
            else
            {
                continue;
            }

            // og: обычно стоит раньше twitter:, первая встреча важна: не перезаписываем.
            var content = ContentAttr.Match(tag.Value);
            if (content.Success && !values.ContainsKey(name))
            {
                values[name] = content.Groups[1].Value;
            }
        }

        return values;
    }

    private static string? ResolveImageUrl(string pageUrl, string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (imageUrl.StartsWith("//", StringComparison.Ordinal))
        {
            var pageUri = new Uri(pageUrl);
            return $"{pageUri.Scheme}:{imageUrl}";
        }

        try
        {
            var baseUri = new Uri(pageUrl);
            if (Uri.TryCreate(baseUri, imageUrl, out var resolved))
            {
                return resolved.ToString();
            }
        }
        catch
        {
            // Игнорируем некорректную ссылку на изображение.
        }

        return null;
    }
}
