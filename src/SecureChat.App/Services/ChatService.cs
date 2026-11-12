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
        _tcp.TypingReceived += username =>
            TypingReceived?.Invoke(username);
        _tcp.ConnectionStateChanged += state =>
            ConnectionStateChanged?.Invoke(state);
        _tcp.Error += message =>
            ConnectionError?.Invoke(message);
    }

    public event Action<EncryptedEnvelope>? EnvelopeReceived;
    public event Action<string>? EnvelopeAccepted;
    public event Action<string[]>? MessagesDelivered;
    public event Action<string[]>? MessagesRead;
    public event Action<string>? TypingReceived;
    public event Action<bool>? ConnectionStateChanged;
    public event Action<string>? ConnectionError;

    public event Action<string>? PrivateMessageDeleted;

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
            if (!string.Equals(mode, "TCP", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await ConnectSignalRAsync(cancellationToken);
                    _usingTcp = false;
                    return;
                }
                catch (Exception ex) when (
                    !string.Equals(mode, "SignalR", StringComparison.OrdinalIgnoreCase))
                {
                    ConnectionError?.Invoke($"WebSocket недоступен, переключаюсь на TCP: {ex.Message}");
                }
            }

            await ConnectTcpAsync(cancellationToken);
            _usingTcp = true;
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
        _connection.On<string>(
            "Typing",
            username => TypingReceived?.Invoke(username));
        _connection.On<string>(
            "PrivateMessageDeleted",
            messageId => PrivateMessageDeleted?.Invoke(messageId));
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

        if (Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
        {
            host = uri.Host;
            if (string.Equals(host, "10.0.2.2", StringComparison.OrdinalIgnoreCase))
            {
                host = "10.0.2.2";
            }
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

    public async Task SendTypingAsync(string recipientId)
    {
        if (!IsConnected)
        {
            return;
        }

        if (_usingTcp)
        {
            // TCP не поддерживает индикатор набора текста — пропускаем.
            return;
        }

        await _connection!.SendAsync("Typing", recipientId);
    }

    public async Task SetPresenceHiddenAsync(bool hidden)
    {
        if (_connection?.State != HubConnectionState.Connected)
        {
            return;
        }

        await _connection.InvokeAsync("SetPresenceHidden", hidden);
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
