using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using SecureChat.Core.Contracts;

namespace SecureChat.App.Services;

/// <summary>
/// Прямое TCP-соединение с сервером чата. Используется как альтернатива
/// WebSocket (SignalR) и как автоматический резерв, если WebSocket недоступен.
/// Кадр: 4 байта длины (big-endian) + UTF-8 JSON.
/// </summary>
public sealed class TcpTransport : IDisposable
{
    private const int MaxFrameBytes = 1_000_000;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readCts;
    private string _host = "localhost";
    private int _port = 5444;
    private string _token = string.Empty;
    private bool _disposed;

    public event Action<EncryptedEnvelope>? EnvelopeReceived;
    public event Action<string, bool>? AckReceived;
    public event Action<string>? EnvelopeAccepted;
    public event Action<string[]>? MessagesDelivered;
    public event Action<string[]>? MessagesRead;
    public event Action<string>? TypingReceived;
    public event Action<bool>? ConnectionStateChanged;
    public event Action<string>? Error;

    public bool IsConnected => _client?.Connected == true;

    public async Task ConnectAsync(
        string host,
        int port,
        string token,
        CancellationToken cancellationToken = default)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            await DisconnectCoreAsync();

            _host = host;
            _port = port;
            _token = token;

            var client = new TcpClient
            {
                NoDelay = true,
                ReceiveTimeout = 0,
                SendTimeout = 15000
            };

            await client.ConnectAsync(host, port, cancellationToken);
            _client = client;
            _stream = client.GetStream();

            await WriteFrameAsync(new TcpFrame { Type = "auth", Token = token }, cancellationToken);

            _readCts = new CancellationTokenSource();
            _ = Task.Run(() => ReadLoopAsync(_readCts.Token), CancellationToken.None);

            ConnectionStateChanged?.Invoke(true);
        }
        catch (Exception ex)
        {
            Error?.Invoke($"TCP-подключение не удалось: {ex.Message}");
            await DisconnectCoreAsync();
            throw;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task SendAsync(EncryptedEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var stream = _stream;
        if (stream is null || !IsConnected)
        {
            throw new InvalidOperationException("Нет TCP-соединения.");
        }

        await WriteFrameAsync(
            new TcpFrame { Type = "send", Envelope = envelope },
            cancellationToken);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(_stream!, cancellationToken);
                if (frame is null)
                {
                    break;
                }

                switch (frame.Type)
                {
                    case "message" when frame.Envelope is not null:
                        EnvelopeReceived?.Invoke(frame.Envelope);
                        break;

                    case "envelopeAccepted" when !string.IsNullOrEmpty(frame.Id):
                        EnvelopeAccepted?.Invoke(frame.Id);
                        break;

                    case "messagesDelivered":
                        MessagesDelivered?.Invoke(
                            frame.Message?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>());
                        break;

                    case "messagesRead":
                        MessagesRead?.Invoke(
                            frame.Message?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>());
                        break;

                    case "typing":
                        TypingReceived?.Invoke(frame.Message ?? string.Empty);
                        break;

                    case "ack" when !string.IsNullOrEmpty(frame.Id):
                        AckReceived?.Invoke(frame.Id, frame.Accepted);
                        if (!frame.Accepted && !string.IsNullOrEmpty(frame.Message))
                        {
                            Error?.Invoke(frame.Message);
                        }

                        break;

                    case "error" when !string.IsNullOrEmpty(frame.Message):
                        Error?.Invoke(frame.Message);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                Error?.Invoke($"TCP-соединение прервано: {ex.Message}");
            }
        }
        finally
        {
            ConnectionStateChanged?.Invoke(false);
        }
    }

    private async Task WriteFrameAsync(TcpFrame frame, CancellationToken cancellationToken)
    {
        var stream = _stream;
        if (stream is null)
        {
            throw new InvalidOperationException("Нет TCP-соединения.");
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(payload, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<TcpFrame?> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var read = 0;
        while (read < header.Length)
        {
            var chunk = await stream.ReadAsync(header.AsMemory(read, header.Length - read), cancellationToken);
            if (chunk == 0)
            {
                return null;
            }

            read += chunk;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MaxFrameBytes)
        {
            return null;
        }

        var payload = new byte[length];
        read = 0;
        while (read < payload.Length)
        {
            var chunk = await stream.ReadAsync(payload.AsMemory(read, payload.Length - read), cancellationToken);
            if (chunk == 0)
            {
                return null;
            }

            read += chunk;
        }

        try
        {
            return JsonSerializer.Deserialize<TcpFrame>(payload, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task DisconnectCoreAsync()
    {
        _readCts?.Cancel();
        _readCts?.Dispose();
        _readCts = null;

        if (_stream is not null)
        {
            await _stream.DisposeAsync();
            _stream = null;
        }

        _client?.Dispose();
        _client = null;
    }

    public async Task DisconnectAsync()
    {
        await _connectLock.WaitAsync();
        try
        {
            await DisconnectCoreAsync();
            ConnectionStateChanged?.Invoke(false);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _readCts?.Cancel();
        _stream?.Dispose();
        _client?.Dispose();
        _writeLock.Dispose();
        _connectLock.Dispose();
    }
}

/// <summary>Кадр TCP-протокола клиента.</summary>
public sealed class TcpFrame
{
    public string Type { get; set; } = string.Empty;
    public string? Token { get; set; }
    public string? Id { get; set; }
    public bool Accepted { get; set; }
    public string? Message { get; set; }
    public EncryptedEnvelope? Envelope { get; set; }
}
