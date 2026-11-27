using System.Net.Http.Headers;

namespace SecureChat.Server.Services;

/// <summary>
/// Отправка push через ntfy. В уведомление не попадает текст сообщения —
/// только имя отправителя и общий заголовок, чтобы не раскрывать E2E-содержимое.
/// </summary>
public sealed class PushNotificationService
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly ILogger<PushNotificationService> _logger;

    public PushNotificationService(IConfiguration configuration, ILogger<PushNotificationService> logger)
    {
        _logger = logger;
        _baseUrl = (configuration["Ntfy:BaseUrl"] ?? "https://ntfy.sh").TrimEnd('/');
        var handler = new SocketsHttpHandler { UseProxy = false };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>Отправить уведомление владельцу topic.</summary>
    public async Task NotifyAsync(
        string topic,
        string senderName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(topic)) return;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{_baseUrl}/{Uri.EscapeDataString(topic)}");
            request.Content = new StringContent("SecureChat: new message");
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            request.Headers.TryAddWithoutValidation("Title", "SecureChat");
            request.Headers.TryAddWithoutValidation("Message", $"New message from {senderName}");
            request.Headers.TryAddWithoutValidation("Tags", "envelope");
            await _http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            // Push не должен ломать доставку сообщения.
            _logger.LogWarning(ex, "Не удалось отправить ntfy push");
        }
    }

    /// <summary>Уведомить всех получателей, кроме отправителя.</summary>
    public async Task NotifyManyAsync(
        IEnumerable<string> topics,
        string senderName,
        CancellationToken cancellationToken = default)
    {
        foreach (var topic in topics.Distinct(StringComparer.Ordinal))
        {
            await NotifyAsync(topic, senderName, cancellationToken);
        }
    }
}
