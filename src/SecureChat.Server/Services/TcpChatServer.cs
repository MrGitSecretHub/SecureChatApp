using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SecureChat.Core.Contracts;
using SecureChat.Core.Crypto;
using SecureChat.Core.Serialization;
using SecureChat.Server.Data;
using SecureChat.Server.Hubs;
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
    private readonly SpaceRepository _spaces;
    private readonly IHubContext<ChatHub> _hub;
    private readonly JwtSettings _jwt;
    private readonly ILogger<TcpChatServer> _logger;

    public TcpChatServer(
        EnvelopeRouter router,
        ChatRepository repository,
        SpaceRepository spaces,
        IHubContext<ChatHub> hub,
        IOptions<JwtSettings> jwtOptions,
        IConfiguration configuration,
        ILogger<TcpChatServer> logger)
    {
        _router = router;
        _repository = repository;
        _spaces = spaces;
        _hub = hub;
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

            // ============================== ПРОСТРАНСТВА ==============================

            case "spaces.list":
            {
                var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
                var spaces = await _spaces.ListForUserAsync(userId, cancellationToken);
                await SendToCallerAsync(principal, new TcpFrame
                {
                    Type = "spaces.list.result",
                    Id = frame.Id,
                    Payload = JsonSerializer.SerializeToElement(spaces, SpaceJsonOptions)
                }, cancellationToken);
                return;
            }

            case "typing":
            {
                var activity = frame.Activities ?? "typing";
                var allowed = new[] { "typing", "uploading_photo", "uploading_video", "recording_voice", "recording_video", "seen" };
                if (!allowed.Contains(activity, StringComparer.OrdinalIgnoreCase)) activity = "typing";
                var targetUsername = frame.Message ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(targetUsername))
                {
                    var target = await _repository.GetUserByUsernameAsync(targetUsername, cancellationToken);
                    if (target is not null)
                    {
                        await SendToUserAsync(target.Id, new TcpFrame
                        {
                            Type = "typing",
                            Message = principal.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
                            Activities = activity
                        }, cancellationToken);
                    }
                }
                await SendToCallerAsync(principal, new TcpFrame { Type = "typing.ack", Id = frame.Id }, cancellationToken);
                return;
            }

            case "spaces.messages":
            {
                var request = frame.Payload?.Deserialize<TcpSpaceMessagesRequest>(SpaceJsonOptions);
                if (request is null)
                {
                    await SendErrorAsync(principal, frame.Id, "Некорректный запрос", cancellationToken);
                    return;
                }

                var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
                var space = await _spaces.GetAsync(request.SpaceId, userId, cancellationToken);
                if (space is null)
                {
                    await SendErrorAsync(principal, frame.Id, "Пространство недоступно", cancellationToken);
                    return;
                }

                var messages = await _spaces.ListEncryptedMessagesAsync(
                    request.SpaceId,
                    request.TopicId,
                    Math.Clamp(request.Limit, 1, 1000),
                    request.TopicId is null && space.Type == SpaceTypes.Forum,
                    cancellationToken);

                await SendToCallerAsync(principal, new TcpFrame
                {
                    Type = "spaces.messages.result",
                    Id = frame.Id,
                    Payload = JsonSerializer.SerializeToElement(messages, SpaceJsonOptions)
                }, cancellationToken);
                return;
            }

            case "spaces.send":
            {
                var request = frame.Payload?.Deserialize<TcpSpaceSendRequest>(SpaceJsonOptions);
                if (request?.Cipher is null)
                {
                    await SendErrorAsync(principal, frame.Id, "Некорректный запрос", cancellationToken);
                    return;
                }

                var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
                var space = await _spaces.GetAsync(request.SpaceId, userId, cancellationToken);
                if (space is null || !space.CanPost)
                {
                    await SendErrorAsync(principal, frame.Id, "Нет права писать", cancellationToken);
                    return;
                }

                if (await _spaces.IsMemberMutedAsync(request.SpaceId, userId, cancellationToken))
                {
                    await SendErrorAsync(principal, frame.Id, "Вам запрещено писать", cancellationToken);
                    return;
                }

                var sender = await _repository.GetUserByIdAsync(userId);
                if (sender is null)
                {
                    await SendErrorAsync(principal, frame.Id, "Пользователь не найден", cancellationToken);
                    return;
                }

                // Проверка подписи — та же, что в SignalR-хабе.
                if (!SpaceCrypto.VerifyMessage(
                        request.SpaceId, request.Cipher.MessageId, userId, request.Cipher,
                        sender.SigningPublicKey))
                {
                    await SendErrorAsync(principal, frame.Id, "Неверная подпись", cancellationToken);
                    return;
                }

                var message = new SpaceMessageDto
                {
                    Id = request.Cipher.MessageId,
                    SpaceId = request.SpaceId,
                    TopicId = request.TopicId ?? request.Cipher.TopicId,
                    SenderId = userId,
                    SenderUsername = sender.Username,
                    Nonce = request.Cipher.Nonce,
                    Tag = request.Cipher.Tag,
                    Ciphertext = request.Cipher.Ciphertext,
                    Signature = request.Cipher.Signature,
                    SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };

                await _spaces.SaveEncryptedMessageAsync(message, cancellationToken);

                // Рассылаем событие всем участникам с TCP-сессиями.
                var memberIds = await _spaces.GetMemberIdsAsync(request.SpaceId, cancellationToken);
                var eventFrame = new TcpFrame
                {
                    Type = "spaceMessage",
                    Payload = JsonSerializer.SerializeToElement(message, SpaceJsonOptions)
                };
                foreach (var memberId in memberIds)
                {
                    await SendToUserAsync(memberId, eventFrame, cancellationToken);
                }

                // Мост Transport → SignalR: получатели на WebSocket тоже должны
                // увидеть сообщение, отправленное по TCP.
                await _hub.Clients.Group($"space:{request.SpaceId}")
                    .SendAsync("SpaceMessage", message, cancellationToken);

                await SendToCallerAsync(principal, new TcpFrame
                {
                    Type = "spaces.send.result",
                    Id = frame.Id,
                    Payload = JsonSerializer.SerializeToElement(message, SpaceJsonOptions)
                }, cancellationToken);
                return;
            }

            case "spaces.topics":
            {
                var request = frame.Payload?.Deserialize<TcpSpaceIdRequest>(SpaceJsonOptions);
                if (request is null)
                {
                    await SendErrorAsync(principal, frame.Id, "Некорректный запрос", cancellationToken);
                    return;
                }

                var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
                var space = await _spaces.GetAsync(request.SpaceId, userId, cancellationToken);
                if (space is null)
                {
                    await SendErrorAsync(principal, frame.Id, "Пространство недоступно", cancellationToken);
                    return;
                }

                var topics = await _spaces.ListTopicsAsync(request.SpaceId, cancellationToken);
                await SendToCallerAsync(principal, new TcpFrame
                {
                    Type = "spaces.topics.result",
                    Id = frame.Id,
                    Payload = JsonSerializer.SerializeToElement(topics, SpaceJsonOptions)
                }, cancellationToken);
                return;
            }

            case "spaces.createTopic":
            {
                var request = frame.Payload?.Deserialize<TcpSpaceTopicRequest>(SpaceJsonOptions);
                if (request is null || string.IsNullOrWhiteSpace(request.Title))
                {
                    await SendErrorAsync(principal, frame.Id, "Некорректный запрос", cancellationToken);
                    return;
                }

                var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
                var space = await _spaces.GetAsync(request.SpaceId, userId, cancellationToken);
                if (space is null || !space.CanPost)
                {
                    await SendErrorAsync(principal, frame.Id, "Нет права создавать темы", cancellationToken);
                    return;
                }

                var user = await _repository.GetUserByIdAsync(userId);
                var topic = await _spaces.CreateTopicAsync(
                    request.SpaceId, request.Title, userId,
                    user?.Username ?? string.Empty, request.Text, cancellationToken);

                await SendToCallerAsync(principal, new TcpFrame
                {
                    Type = "spaces.createTopic.result",
                    Id = frame.Id,
                    Payload = JsonSerializer.SerializeToElement(topic, SpaceJsonOptions)
                }, cancellationToken);
                return;
            }

            case "spaces.join":
            {
                var request = frame.Payload?.Deserialize<TcpSpaceJoinRequest>(SpaceJsonOptions);
                if (request is null)
                {
                    await SendErrorAsync(principal, frame.Id, "Некорректный запрос", cancellationToken);
                    return;
                }

                var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
                var space = await _spaces.GetAsync(request.SpaceId, userId, cancellationToken);
                if (space is null || !space.IsPublic)
                {
                    await SendErrorAsync(principal, frame.Id, "Пространство приватное или недоступно", cancellationToken);
                    return;
                }

                var members = await _spaces.GetMembersAsync(request.SpaceId, cancellationToken);
                if (!members.Any(item => string.Equals(item.UserId, userId, StringComparison.Ordinal)))
                {
                    var canPost = string.Equals(space.Type, SpaceTypes.Group, StringComparison.OrdinalIgnoreCase);
                    await _spaces.AddMemberAsync(request.SpaceId, userId, SpaceRoles.Member, canPost, cancellationToken);
                }

                await SendToCallerAsync(principal, new TcpFrame
                {
                    Type = "spaces.join.result",
                    Id = frame.Id,
                    Payload = JsonSerializer.SerializeToElement(space, SpaceJsonOptions)
                }, cancellationToken);
                return;
            }
        }
    }

    /// <summary>Настройки JSON для пространств (те же, что в SignalR-хабе).</summary>
    private static readonly JsonSerializerOptions SpaceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private async Task SendToCallerAsync(
        ClaimsPrincipal principal,
        TcpFrame frame,
        CancellationToken cancellationToken)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        await SendToUserAsync(userId, frame, cancellationToken);
    }

    private async Task SendErrorAsync(
        ClaimsPrincipal principal,
        string? requestId,
        string message,
        CancellationToken cancellationToken)
    {
        await SendToCallerAsync(principal, new TcpFrame
        {
            Type = "error",
            Id = requestId,
            Message = message
        }, cancellationToken);
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
    public string? Activities { get; set; }
    public EncryptedEnvelope? Envelope { get; set; }

    /// <summary>Произвольные данные пространств (список, сообщения, темы).</summary>
    public System.Text.Json.JsonElement? Payload { get; set; }
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
