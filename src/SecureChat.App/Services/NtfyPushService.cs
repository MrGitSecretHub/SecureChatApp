using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SecureChat.App.Services;

/// <summary>
/// Подписка на ntfy-topic текущего пользователя. В уведомление приходит только
/// имя отправителя — сервер не знает содержимое E2E-сообщений.
/// </summary>
public sealed class NtfyPushService
{
    private const string NtfyBase = "https://ntfy.sh";
    private readonly AppSession _session;
    private readonly ApiClient _api;
    private readonly NotificationService _notifications;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public NtfyPushService(
        AppSession session,
        ApiClient api,
        NotificationService notifications)
    {
        _session = session;
        _api = api;
        _notifications = notifications;
    }

    public void Start()
    {
        if (_loop is not null && !_loop.IsCompleted) return;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
    }

    private async Task RunAsync(CancellationToken token)
    {
        // topic тоже доступен в профиле после логина, но запрос с сервера
        // гарантирует актуальное значение после перерегистрации.
        string topic;
        try
        {
            topic = await _api.GetPushTopicAsync(_session.ServerUrl, _session.Token!, token);
        }
        catch
        {
            topic = _session.User?.PushTopic ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(topic)) return;

        var uri = new Uri($"wss://ntfy.sh/{Uri.EscapeDataString(topic)}/ws");
        while (!token.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(uri, token);
                var buffer = new byte[16 * 1024];
                while (!token.IsCancellationRequested &&
                       socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    Handle(json);
                }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                // Временный сбой сети — повторяем подписку позже.
            }

            if (token.IsCancellationRequested) break;
            try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Handle(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("event", out var ev) ||
                ev.GetString() != "message") return;
            var body = root.TryGetProperty("message", out var msg)
                ? msg.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(body)) return;

            var sender = body.StartsWith("New message from ", StringComparison.Ordinal)
                ? body["New message from ".Length..]
                : string.Empty;
            _notifications.ShowNotification(
                "SecureChat", body, sender, 1, silent: false);
        }
        catch (JsonException) { }
    }
}
