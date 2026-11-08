using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace SecureChat.App.Services;

/// <summary>
/// Генерирует превью ссылки (Open Graph) на стороне клиента.
/// Сервер SecureChat не участвует — владелец сайта увидит IP устройства,
/// которое открыло чат. По умолчанию функция выключена.
/// </summary>
public sealed class LinkPreviewService
{
    private static readonly Regex OgRegex =
        new(@"<meta[^>]+property\s*=\s*[""']og:([a-z]+)[""'][^>]+content\s*=\s*[""']([^""']*)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TitleRegex =
        new(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    private const int MaxReadBytes = 256 * 1024;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly Dictionary<string, LinkPreview?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public LinkPreviewService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; SecureChat/1.0; +https://secure.chat/bot)");
    }

    /// <summary>Возвращает кэшированное превью или загружает его с сайта.</summary>
    public Task<LinkPreview?> GetPreviewAsync(string url, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(url, out var cached))
        {
            return Task.FromResult(cached);
        }

        return FetchPreviewAsync(url, ct);
    }

    private async Task<LinkPreview?> FetchPreviewAsync(string url, CancellationToken ct)
    {
        LinkPreview? result = null;
        try
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
            var buffer = new byte[MaxReadBytes];
            var read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
            var html = Encoding.UTF8.GetString(buffer, 0, read);

            var og = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in OgRegex.Matches(html))
            {
                if (!og.ContainsKey(m.Groups[1].Value))
                {
                    og[m.Groups[1].Value] = m.Groups[2].Value;
                }
            }

            var title = og.GetValueOrDefault("title");
            if (string.IsNullOrWhiteSpace(title))
            {
                var m = TitleRegex.Match(html);
                title = m.Success ? m.Groups[1].Value.Trim() : null;
            }

            var description = og.GetValueOrDefault("description");
            var imageUrl = ResolveImageUrl(url, og.GetValueOrDefault("image"));

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(description) &&
                imageUrl is null)
            {
                return null;
            }

            result = new LinkPreview
            {
                Url = url,
                Title = title?.Trim(),
                Description = description?.Trim(),
                ImageUrl = imageUrl,
                Domain = new Uri(url).Host
            };
        }
        catch
        {
            // Сайт недоступен или некорректен — превью просто не будет.
        }

        _cache[url] = result;
        return result;
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
