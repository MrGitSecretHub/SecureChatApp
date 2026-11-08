using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SecureChat.Core.Contracts;
using SecureChat.Core.Serialization;
using SecureChat.Server.Data;
using SecureChat.Server.Services;

namespace SecureChat.Server.Services;

/// <summary>
/// Прямое TCP-соединение с сервером чата. Работает как альтернатива WebSocket:
/// клиент авторизуется JWT-токеном в первом кадре и дальше обменивается
/// такими же E2E-конвертами, как через SignalR.
/// Формат кадра: 4 байта длины (big-endian) + UTF-8 JSON.
/// </summary>
public sealed class TcpChatServer : BackgroundService
{
    private const int MaxFrameBytes = 1_000_000;

    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, TcpSession>> _sessions = new();
    private readonly EnvelopeRouter _router;
    private readonly ChatRepository _repository;
    private readonly JwtSettings _jwt;
    private readonly ILogger<TcpChatServer> _logger;

    public TcpChatServer(
        EnvelopeRouter router,
        ChatRepository repository,
        IOptions<JwtSettings> jwtOptions,
        IConfiguration configuration,
        ILogger<TcpChatServer> logger)
    {
        _router = router;
        _repository = repository;
        _jwt = jwtOptions.Value;
        _logger = logger;

        var port = configuration.GetValue("Tcp:Port", 5444);
        var bindAddress = configuration.GetValue<string>("Tcp:BindAddress", "0.0.0.0");
        _listener = new TcpListener(
            IPAddress.Parse(bindAddress),
            port);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _listener.Start();
            _logger.LogInformation("TCP-сервер чата слушает порт {Port}", ((IPEndPoint)_listener.LocalEndpoint).Port);
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex, "TCP-сервер чата не запущен");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка принятия TCP-соединения");
                continue;
            }

            _ = HandleClientAsync(client, stoppingToken);
        }

        _listener.Stop();
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken stoppingToken)
    {
        var session = new TcpSession(client);
        var userId = string.Empty;

        try
        {
            using var stream = client.GetStream();
            var principal = await AuthenticateAsync(stream, stoppingToken);
            if (principal is null)
            {
                await WriteFrameAsync(stream, new TcpFrame { Type = "error", Message = "Не авторизован" }, stoppingToken);
                return;
            }

            userId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            var user = await _repository.GetUserByIdAsync(userId);
            if (user is null)
            {
                await WriteFrameAsync(stream, new TcpFrame { Type = "error", Message = "Пользователь не найден" }, stoppingToken);
                return;
            }

            var userSessions = _sessions.GetOrAdd(userId, _ => new ConcurrentDictionary<Guid, TcpSession>());
            userSessions[session.Id] = session;

            await WriteFrameAsync(
                stream,
                new TcpFrame { Type = "auth_ok", UserId = userId, Username = user.Username },
                stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(stream, stoppingToken);
                if (frame is null)
                {
                    break;
                }

                await HandleFrameAsync(frame, principal, stoppingToken);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            // Соединение закрыто клиентом.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка обработки TCP-соединения");
        }
        finally
        {
            if (!string.IsNullOrEmpty(userId) &&
                _sessions.TryGetValue(userId, out var list))
            {
                list.TryRemove(session.Id, out _);
            }

            session.Dispose();
        }
    }

    private async Task HandleFrameAsync(TcpFrame frame, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case "send" when frame.Envelope is not null:
            {
                var route = await _router.RouteAsync(frame.Envelope, principal);
                if (!route.Accepted)
                {
                    await SendToUserAsync(
                        principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
                        new TcpFrame { Type = "ack", Id = frame.Envelope.Id, Accepted = false, Message = route.Error },
                        cancellationToken);
                    return;
                }

                await SendToUserAsync(
                    route.RecipientUserId!,
                    new TcpFrame { Type = "message", Envelope = route.Envelope },
                    cancellationToken);

                await SendToUserAsync(
                    principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
                    new TcpFrame { Type = "ack", Id = frame.Envelope.Id, Accepted = true },
                    cancellationToken);
                return;
            }

            case "ping":
                await SendToUserAsync(
                    principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
                    new TcpFrame { Type = "pong" },
                    cancellationToken);
                return;
        }
    }

    private async Task SendToUserAsync(string userId, TcpFrame frame, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(userId, out var sessions))
        {
            return;
        }

        foreach (var session in sessions.Values)
        {
            try
            {
                await session.WriteAsync(frame, cancellationToken);
            }
            catch
            {
                // Соединение могло закрыться — оно будет удалено при обработке finally.
            }
        }
    }

    private async Task<ClaimsPrincipal?> AuthenticateAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var frame = await ReadFrameAsync(stream, cancellationToken);
        if (frame is null || frame.Type != "auth" || string.IsNullOrWhiteSpace(frame.Token))
        {
            return null;
        }

        try
        {
            if (_jwt.KeyBytes is null or { Length: 0 })
            {
                _logger.LogWarning("JWT-ключ не настроен, сервер не может авторизовать TCP-клиента.");
                return null;
            }

            var handler = new JwtSecurityTokenHandler();
            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _jwt.Issuer,
                ValidateAudience = true,
                ValidAudience = _jwt.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(_jwt.KeyBytes),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1)
            };

            var principal = handler.ValidateToken(frame.Token, parameters, out _);
            return principal;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось авторизовать TCP-клиента");
            return null;
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

    private static async Task WriteFrameAsync(NetworkStream stream, TcpFrame frame, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);

        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

/// <summary>Кадр TCP-протокола.</summary>
public sealed class TcpFrame
{
    public string Type { get; set; } = string.Empty;
    public string? Token { get; set; }
    public string? UserId { get; set; }
    public string? Username { get; set; }
    public string? Id { get; set; }
    public bool Accepted { get; set; }
    public string? Message { get; set; }
    public EncryptedEnvelope? Envelope { get; set; }
}

/// <summary>Одно TCP-соединение пользователя.</summary>
internal sealed class TcpSession : IDisposable
{
    private readonly TcpClient _client;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public TcpSession(TcpClient client) => _client = client;

    public Guid Id { get; } = Guid.NewGuid();

    public async Task WriteAsync(TcpFrame frame, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var stream = _client.GetStream();
            var payload = JsonSerializer.SerializeToUtf8Bytes(frame, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);

            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(payload, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        try
        {
            _client.Close();
        }
        catch
        {
            // Игнорируем ошибки закрытия.
        }

        _client.Dispose();
        _writeLock.Dispose();
    }
}
