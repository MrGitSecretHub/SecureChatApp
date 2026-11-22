using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using SecureChat.Core.Contracts;

namespace SecureChat.App.Services;

/// <summary>
/// Подключение к SignalR-хабу и подписки на события сервера.
/// Поддерживает два транспорта: SignalR (WebSocket) и прямой TCP.
/// </summary>
public sealed class ChatService : IDisposable
{
    private readonly AppSession _session;
    private readonly TcpTransport _tcp = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private HubConnection? _connection;
    private bool _usingTcp;
    private bool _disposed;

    public ChatService(AppSession session)
    {
        _session = session;

        _tcp.EnvelopeReceived += envelope =>
            EnvelopeReceived?.Invoke(envelope);
        _tcp.EnvelopeAccepted += id =>
            EnvelopeAccepted?.Invoke(id);
        _tcp.MessagesDelivered += ids =>
            MessagesDelivered?.Invoke(ids);
        _tcp.MessagesRead += ids =>
            MessagesRead?.Invoke(ids);
        _tcp.TypingReceived += (username, activity) =>
            TypingReceived?.Invoke(username, activity);
        _tcp.ConnectionStateChanged += state =>
            ConnectionStateChanged?.Invoke(state);
        _tcp.Error += message =>
            ConnectionError?.Invoke(message);
    }

    public event Action<EncryptedEnvelope>? EnvelopeReceived;
    public event Action<string>? EnvelopeAccepted;
    public event Action<string[]>? MessagesDelivered;
    public event Action<string[]>? MessagesRead;
    public event Action<string, string>? TypingReceived;
    public event Action<bool>? ConnectionStateChanged;
    public event Action<string>? ConnectionError;

    public event Action<string>? PrivateMessageDeleted;

    public event Action<string>? PrivateConversationDeleted;

    public event Action<string>? PrivateConversationCleared;

    public bool IsConnected => _usingTcp
        ? _tcp.IsConnected
        : _connection?.State == HubConnectionState.Connected;

    /// <summary>SignalR сейчас пытается восстановить соединение автоматически.</summary>
    public bool IsReconnecting =>
        !_usingTcp && _connection?.State == HubConnectionState.Reconnecting;

    /// <summary>Текущее подключение SignalR (null, если активен TCP-транспорт).</summary>
    public HubConnection? Connection => _connection;

    /// <summary>Активный транспорт: «SignalR» или «TCP».</summary>
    public string ActiveTransport => _usingTcp ? "TCP" : "SignalR";

    /// <summary>Активный TCP-транспорт (для пространств при недоступном WebSocket).</summary>
    public TcpTransport Tcp => _tcp;

    /// <summary>Сейчас используется TCP вместо SignalR.</summary>
    public bool IsUsingTcp => _usingTcp;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.IsAuthenticated)
        {
            throw new InvalidOperationException("Нет активной сессии.");
        }

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected)
            {
                return;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }

            var mode = _session.TransportMode;

            // TCP не шифрует трафик и передаёт JWT открытым текстом.
            // Автоматический откат на него недопустим — только явный выбор пользователя.
            if (!string.Equals(mode, "TCP", StringComparison.OrdinalIgnoreCase))
            {
                await ConnectSignalRAsync(cancellationToken);
                _usingTcp = false;
                return;
            }

            await ConnectTcpAsync(cancellationToken);
            _usingTcp = true;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Пространства живут только в SignalR-хабе: поднимает WebSocket без отката
    /// на TCP. При успехе TCP закрывается — личные сообщения поедут по SignalR.
    /// </summary>
    public async Task<HubConnection?> ConnectSignalROnlyAsync(CancellationToken cancellationToken = default)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection?.State == HubConnectionState.Connected)
            {
                return _connection;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }

            try
            {
                await ConnectSignalRAsync(cancellationToken);
            }
            catch (Exception)
            {
                // WebSocket не поднять — оставляем текущий транспорт (возможно, TCP).
                return null;
            }

            _usingTcp = false;
            try
            {
                await _tcp.DisconnectAsync();
            }
            catch
            {
                // TCP уже закрыт или недоступен — не важно, перешли на SignalR.
            }

            return _connection;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task ConnectSignalRAsync(CancellationToken cancellationToken)
    {
        var builder = new HubConnectionBuilder()
            .WithUrl($"{_session.ServerUrl}/hubs/chat", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(_session.Token);

                options.HttpMessageHandlerFactory = inner =>
                {
                    // Сервер в локальной сети: системный прокси (например, включённый
                    // 127.0.0.1:10809 в Windows) рвёт TLS и ломает подключение.
                    // Меняем конкретный inner-handler, сохраняя цепочку SignalR.
                    switch (inner)
                    {
                        case SocketsHttpHandler sockets:
                            sockets.UseProxy = false;
                            sockets.SslOptions.RemoteCertificateValidationCallback =
                                ServerCertificatePin.Validate;
                            break;
                        case HttpClientHandler http:
                            http.UseProxy = false;
                            http.ServerCertificateCustomValidationCallback =
                                ServerCertificatePin.Validate;
                            break;
                    }

                    return inner;
                };
            })
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10)
            })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy =
                    JsonNamingPolicy.CamelCase;
                options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
            });

        _connection = builder.Build();

        _connection.On<EncryptedEnvelope>(
            "ReceiveEnvelope",
            envelope => EnvelopeReceived?.Invoke(envelope));
        _connection.On<string, long>(
            "EnvelopeAccepted",
            (id, _) => EnvelopeAccepted?.Invoke(id));
        _connection.On<string[]>(
            "MessagesDelivered",
            ids => MessagesDelivered?.Invoke(ids));
        _connection.On<string[]>(
            "MessagesRead",
            ids => MessagesRead?.Invoke(ids));
        _connection.On<string, string>(
            "Typing",
            (username, activity) => TypingReceived?.Invoke(username, activity ?? "typing"));
        _connection.On<string>(
            "PrivateMessageDeleted",
            messageId => PrivateMessageDeleted?.Invoke(messageId));
        _connection.On<string>(
            "PrivateConversationDeleted",
            userId => PrivateConversationDeleted?.Invoke(userId));
        _connection.On<string>(
            "PrivateConversationCleared",
            userId => PrivateConversationCleared?.Invoke(userId));
        _connection.Reconnecting += _ =>
        {
            ConnectionStateChanged?.Invoke(false);
            return Task.CompletedTask;
        };
        _connection.Reconnected += _ =>
        {
            ConnectionStateChanged?.Invoke(true);
            return Task.CompletedTask;
        };
        _connection.Closed += error =>
        {
            ConnectionStateChanged?.Invoke(false);
            if (error is not null)
            {
                ConnectionError?.Invoke(error.Message);
            }

            return Task.CompletedTask;
        };

        await _connection.StartAsync(cancellationToken);
        ConnectionStateChanged?.Invoke(true);
    }

    private async Task ConnectTcpAsync(CancellationToken cancellationToken)
    {
        var (host, port) = ResolveTcpEndpoint();
        await _tcp.ConnectAsync(host, port, _session.Token!, cancellationToken);
    }

    private (string Host, int Port) ResolveTcpEndpoint()
    {
        var serverUrl = _session.ServerUrl;
        var host = "localhost";
        var port = 5444;

        // Явно заданный хост TCP-туннеля (например, домен Playit) имеет приоритет
        // над хостом из ServerUrl — у туннелей HTTP и TCP разные адреса.
        if (!string.IsNullOrWhiteSpace(_session.TcpHost))
        {
            host = _session.TcpHost.Trim();
        }
        else if (Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) &&
                 !string.IsNullOrWhiteSpace(uri.Host))
        {
            host = uri.Host;
        }

        if (_session.TcpPort > 0)
        {
            port = _session.TcpPort;
        }

        return (host, port);
    }

    public async Task<SendEnvelopeResult> SendEnvelopeAsync(
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            await ConnectAsync(cancellationToken);
        }

        if (_usingTcp && _tcp.IsConnected)
        {
            await _tcp.SendAsync(envelope, cancellationToken);
            return new SendEnvelopeResult { Accepted = true };
        }

        var connection = _connection
            ?? throw new InvalidOperationException("Нет подключения к серверу.");

        return await connection.InvokeAsync<SendEnvelopeResult>(
            "SendEnvelope",
            envelope,
            cancellationToken);
    }

    public async Task AckAsync(
        IEnumerable<string> messageIds,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return;
        }

        await _connection!.InvokeAsync<int>(
            "AckMessages",
            messageIds.ToArray(),
            cancellationToken);
    }

    public async Task ReadMessagesAsync(
        IEnumerable<string> messageIds,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return;
        }

        await _connection!.InvokeAsync(
            "ReadMessages",
            messageIds.ToArray(),
            cancellationToken);
    }

    public async Task SendTypingAsync(string recipientId, string activity = "typing", string recipientUsername = "")
    {
        if (!IsConnected)
        {
            return;
        }

        if (_usingTcp)
        {
            // TCP поддерживает активность отдельным кадром.
            await _tcp.SendTypingAsync(
                string.IsNullOrWhiteSpace(recipientUsername) ? recipientId : recipientUsername,
                activity);
            return;
        }

        await _connection!.SendAsync("Typing", recipientId, activity);
    }

    public async Task SetPresenceHiddenAsync(bool hidden)
    {
        if (_connection?.State != HubConnectionState.Connected)
        {
            return;
        }

        await _connection.InvokeAsync("SetPresenceHidden", hidden);
    }

    /// <summary>Сообщает серверу, что одноразовое сообщение просмотрено.</summary>
    public async Task<bool> ConsumeViewOnceAsync(string messageId, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            await ConnectAsync(cancellationToken);
        }

        var connection = _connection
            ?? throw new InvalidOperationException("Нет подключения к серверу.");

        if (_usingTcp)
        {
            return false;
        }

        return await connection.InvokeAsync<bool>(
            "ConsumeViewOnce",
            messageId,
            cancellationToken);
    }

    public async Task<bool> DeletePrivateMessageAsync(string messageId, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            await ConnectAsync(cancellationToken);
        }

        var connection = _connection
            ?? throw new InvalidOperationException("Нет подключения к серверу.");

        if (_usingTcp)
        {
            // TCP-транспорт не поддерживает удаление для всех — только локально.
            return false;
        }

        return await connection.InvokeAsync<bool>(
            "DeletePrivateMessage",
            messageId,
            cancellationToken);
    }

    public async Task<bool> DeletePrivateConversationAsync(string username, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            await ConnectAsync(cancellationToken);
        }

        var connection = _connection
            ?? throw new InvalidOperationException("Нет подключения к серверу.");

        if (_usingTcp)
        {
            // TCP-транспорт не поддерживает удаление для всех — только локально.
            return false;
        }

        return await connection.InvokeAsync<bool>(
            "DeletePrivateConversation",
            username,
            cancellationToken);
    }

    public async Task<bool> ClearPrivateConversationAsync(string username, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            await ConnectAsync(cancellationToken);
        }

        var connection = _connection
            ?? throw new InvalidOperationException("Нет подключения к серверу.");

        if (_usingTcp)
        {
            // TCP-транспорт не поддерживает очистку для всех — только локально.
            return false;
        }

        return await connection.InvokeAsync<bool>(
            "ClearPrivateConversation",
            username,
            cancellationToken);
    }

    public async Task DisconnectAsync()
    {
        await _connectLock.WaitAsync();
        try
        {
            await _tcp.DisconnectAsync();

            if (_connection is not null)
            {
                await _connection.StopAsync();
                await _connection.DisposeAsync();
                _connection = null;
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _tcp.Dispose();
    }

    public void Dispose()
    {
        _tcp.Dispose();
        _connection?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
    }
}
