using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using SecureChat.Core.Contracts;
using SecureChat.Core.Crypto;
using SecureChat.Core.Serialization;

namespace SecureChat.App.Services;

/// <summary>
/// Операции с пространствами: группы, каналы, сообщества и форумы.
/// Используется тот же хаб, что и для личных сообщений.
/// </summary>
public sealed class SpaceService
{
    private readonly ChatService _chatService;
    private readonly AppSession _session;
    private readonly ApiClient _apiClient;

    public SpaceService(ChatService chatService, AppSession session, ApiClient apiClient)
    {
        _chatService = chatService;
        _session = session;
        _apiClient = apiClient;
    }

    private HubConnection? Connection => _chatService.Connection;

    private readonly Dictionary<string, byte[]> _spaceKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Возвращает общий ключ пространства: получает «конверт» с сервера
    /// и расшифровывает его приватным ключом устройства. Ключ кэшируется в памяти.
    /// </summary>
    public async Task<byte[]?> GetSpaceKeyAsync(string spaceId)
    {
        if (_spaceKeys.TryGetValue(spaceId, out var cached))
        {
            return cached;
        }

        var identity = _session.Identity;
        if (identity is null)
        {
            return null;
        }

        var tcpKey = await TryTcpAsync("spaces.key", new TcpSpaceIdRequest { SpaceId = spaceId }, CancellationToken.None);
        SpaceKeyEnvelope? envelope;
        if (tcpKey.HasValue)
        {
            var json = tcpKey.Value;
            string? raw = null;
            if (json.ValueKind == System.Text.Json.JsonValueKind.Object &&
                json.TryGetProperty("envelope", out var envElement))
            {
                raw = envElement.ValueKind == System.Text.Json.JsonValueKind.String
                    ? envElement.GetString()
                    : envElement.GetRawText();
            }
            envelope = string.IsNullOrWhiteSpace(raw) ? null : ChatJson.Deserialize<SpaceKeyEnvelope>(raw);
        }
        else
        {
            var connection = await GetConnectionAsync();
            envelope = await connection.InvokeAsync<SpaceKeyEnvelope?>("GetSpaceKey", spaceId);
        }

        if (envelope is null)
        {
            return null;
        }

        try
        {
            var key = SpaceCrypto.DecryptSpaceKey(
                envelope,
                spaceId,
                _session.User?.Id ?? string.Empty,
                identity);

            _spaceKeys[spaceId] = key;
            return key;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Сбросить кэшированный ключ, чтобы получить актуальный с сервера.</summary>
    public void InvalidateSpaceKey(string spaceId)
    {
        _spaceKeys.Remove(spaceId);
    }

    /// <summary>Возвращает активное подключение, при необходимости переподключаясь.</summary>
    private async Task<HubConnection> GetConnectionAsync()
    {
        var connection = Connection;
        if (connection is not null && connection.State == HubConnectionState.Connected)
        {
            return connection;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        // Пространства существуют только в SignalR-хабе: TCP не годится откатом.
        var restored = await _chatService.ConnectSignalROnlyAsync(timeout.Token);
        if (restored is null)
        {
            throw new InvalidOperationException(
                "Нет подключения к серверу по WebSocket. Проверьте сеть и адрес сервера.");
        }

        // Новое подключение нужно заново подписать и ввести в группы пространств.
        Subscribe();
        await JoinGroupsForCurrentConnectionAsync();
        _ = DistributeKeysForAllSpacesAsync();
        return restored;
    }

    public event Action<SpaceMessageDto>? SpaceMessageReceived;
    public event Action<SpaceMessageDto>? SpaceMessageUpdated;
    public event Action<string, string>? SpaceMessageDeleted;
    public event Action<TopicDto>? TopicCreated;

    /// <summary>Тему переименовали, закрыли или переставили.</summary>
    public event Action<TopicDto>? TopicUpdated;

    /// <summary>Новая заявка на вступление (у администраторов).</summary>
    public event Action<string>? JoinRequestReceived;

    /// <summary>Заявку приняли или отклонили (у заявителя).</summary>
    public event Action<string, bool, string>? JoinRequestResolved;

    /// <summary>Ключ пространства доставлен — можно загружать историю.</summary>
    public event Action<string>? SpaceKeyReady;

    private HubConnection? _subscribedConnection;
    private bool _tcpSpaceSubscribed;
    public void Subscribe()
    {
        var connection = Connection;
        if (connection is null || ReferenceEquals(connection, _subscribedConnection))
        {
            return;
        }

        _subscribedConnection = connection;

        _chatService.Tcp.SpaceEventReceived -= OnTcpSpaceEvent;
        _chatService.Tcp.SpaceEventReceived += OnTcpSpaceEvent;

        connection.On<SpaceMessageDto>(
            "SpaceMessage",
            message => SpaceMessageReceived?.Invoke(message));

        connection.On<SpaceMessageDto>(
            "SpaceMessageUpdated",
            message => SpaceMessageUpdated?.Invoke(message));

        connection.On<string, string>(
            "SpaceMessageDeleted",
            (spaceId, messageId) => SpaceMessageDeleted?.Invoke(spaceId, messageId));

        connection.On<TopicDto>(
            "TopicCreated",
            topic => TopicCreated?.Invoke(topic));

        connection.On<TopicDto>(
            "TopicUpdated",
            topic => TopicUpdated?.Invoke(topic));

        connection.On<string>(
            "JoinRequestReceived",
            spaceId => JoinRequestReceived?.Invoke(spaceId));

        connection.On<string, bool, string>(
            "JoinRequestResolved",
            (spaceId, approved, spaceName) => JoinRequestResolved?.Invoke(spaceId, approved, spaceName));

        // Пригласили в новое пространство — подтягиваем участие в его группе,
        // чтобы получать сообщения без переподключения к серверу.
        connection.On<SpaceDto>(
            "SpaceAdded",
            async space =>
            {
                InvalidateSpaceKey(space.Id);
                SpaceKeyReady?.Invoke(space.Id);
                await JoinGroupsForCurrentConnectionAsync();
            });

        // Кто-то вступил в публичное пространство: ключ до него должен доставить
        // любой онлайн-участник — пробуем мы, если ключ у нас есть.
        connection.On<string, string, string>(
            "SpaceMemberNeedKey",
            (spaceId, userId, username) => _ = ProvideSpaceKeyAsync(spaceId, userId, username));
    }

    /// <summary>
    /// Раздаёт ключ участникам, которым он ещё не выдан (только админ/владелец).
    /// Вызывается при открытии пространства — чинит историю для новичков,
    /// вступивших, когда nikto из участников не был онлайн.
    /// </summary>
    public async Task<int> EnsureKeyDistributedAsync(string spaceId)
    {
        try
        {
            var token = _session.Token;
            if (string.IsNullOrWhiteSpace(token)) return 0;
            var tcpMissing = await TryTcpAsync("spaces.keysMissing",
                new TcpSpaceKeyRequestAll { SpaceId = spaceId }, CancellationToken.None);
            var missing = tcpMissing.HasValue
                ? tcpMissing.Value.Deserialize<List<string>>(ChatJson.Options) ?? new List<string>()
                : await (await GetConnectionAsync()).InvokeAsync<List<string>>("GetSpaceMembersWithoutKey", spaceId);
            if (missing is null || missing.Count == 0) return 0;

            var key = await GetSpaceKeyAsync(spaceId);
            if (key is null) return 0;

            var users = await _apiClient.ListUsersAsync(_session.ServerUrl, token);
            var byId = users.ToDictionary(u => u.Id, StringComparer.Ordinal);
            var envelopes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var userId in missing.Take(500))
            {
                if (!byId.TryGetValue(userId, out var user) ||
                    string.IsNullOrWhiteSpace(user.EncryptionPublicKey)) continue;
                envelopes[userId] = ChatJson.Serialize(SpaceCrypto.EncryptSpaceKey(
                    key, spaceId, userId, user.EncryptionPublicKey));
            }
            if (envelopes.Count == 0) return 0;
            var tcpSave = await TryTcpAsync("spaces.keysSaveAll",
                new TcpSpaceKeysSaveAllRequest { SpaceId = spaceId, Envelopes = envelopes }, CancellationToken.None);
            if (!tcpSave.HasValue)
                await (await GetConnectionAsync()).InvokeAsync<bool>("SaveSpaceMemberKeys", spaceId, envelopes);
            return envelopes.Count;
        }
        catch
        {
            // раздача не критична: сработает авто-доставка при следующем входе
            return 0;
        }
    }

    private int _distributingForAll;

    /// <summary>При запуске приложения раздаём ключ везде, где мы админ/владелец.</summary>
    private async Task DistributeKeysForAllSpacesAsync()
    {
        if (Interlocked.Exchange(ref _distributingForAll, 1) == 1) return;
        try
        {
            var spaces = await ListAsync();
            foreach (var space in spaces.Where(x =>
                x.MyRole is SpaceRoles.Owner or SpaceRoles.Admin))
            {
                await EnsureKeyDistributedAsync(space.Id);
            }
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _distributingForAll, 0);
        }
    }

    /// <summary>Просим группу ещё раз прислать ключ этому участнику.</summary>
    public async Task RequestSpaceKeyAsync(string spaceId)
    {
        try
        {
            var tcpReq = await TryTcpAsync("spaces.keyRequest",
                new TcpSpaceKeyRequestAll { SpaceId = spaceId }, CancellationToken.None);
            if (!tcpReq.HasValue)
            {
                var connection = await GetConnectionAsync();
                await connection.InvokeAsync("RequestSpaceKey", spaceId);
            }
        }
        catch
        {
            // сервер может быть офлайн — повторим по таймеру
        }
    }

    /// <summary>Авто-раздача E2E-ключа новому участнику публичного пространства.</summary>
    private async Task ProvideSpaceKeyAsync(string spaceId, string userId, string username)
    {
        try
        {
            var me = _session.User;
            if (me is null || string.Equals(me.Id, userId, StringComparison.Ordinal))
            {
                return;
            }

            var spaceKey = await GetSpaceKeyAsync(spaceId);
            if (spaceKey is null)
            {
                return; // у нас ключа нет — пусть доставит кто-то другой
            }

            var token = _session.Token;
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(username))
            {
                return;
            }

            var profile = await _apiClient.GetUserAsync(_session.ServerUrl, token, username, CancellationToken.None);
            if (profile is null ||
                !string.Equals(profile.Id, userId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(profile.EncryptionPublicKey))
            {
                return;
            }

            var envelope = SpaceCrypto.EncryptSpaceKey(spaceKey, spaceId, userId, profile.EncryptionPublicKey);
            var connection = Connection;
            if (connection is null || connection.State != HubConnectionState.Connected)
            {
                return;
            }

            await connection.InvokeAsync<bool>("SaveSpaceMemberKey", spaceId, userId, ChatJson.Serialize(envelope));
        }
        catch
        {
            // Раздача ключа не должна ломать приём сообщений.
        }
    }

    /// <summary>Добавляет текущее подключение во все группы пространств пользователя.</summary>
    private async Task JoinGroupsForCurrentConnectionAsync()
    {
        try
        {
            var connection = Connection;
            if (connection is not null && connection.State == HubConnectionState.Connected)
            {
                await connection.InvokeAsync<bool>("SyncSpaceGroups", CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // фоновая синхронизация не критична: группы восстановятся при переподключении
        }
    }

    /// <summary>
    /// Выполняет запрос через TCP, если активен TCP-транспорт.
    /// Возвращает null, если нужно идти через SignalR.
    /// </summary>
    private async Task<System.Text.Json.JsonElement?> TryTcpAsync<TRequest>(
        string type,
        TRequest request,
        CancellationToken cancellationToken)
    {
        if (!_chatService.IsUsingTcp || !_chatService.Tcp.IsConnected)
        {
            return null;
        }

        try
        {
            return await _chatService.Tcp.RequestAsync(type, request, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Ошибка TCP-запроса «{type}»: {ex.Message}", ex);
        }
    }

    public async Task<List<SpaceDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var tcp = await TryTcpAsync("spaces.list", new { }, cancellationToken);
        if (tcp.HasValue)
        {
            return tcp.Value.Deserialize<List<SpaceDto>>(ChatJson.Options)
                ?? new List<SpaceDto>();
        }

        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return new List<SpaceDto>();
        }

        return await connection.InvokeAsync<List<SpaceDto>>("ListSpaces", cancellationToken);
    }

    public async Task<SpaceDto?> CreateAsync(
        string name,
        string type,
        string description,
        bool isPublic,
        IEnumerable<string> members,
        bool isHidden = false,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return null;
        }

        var request = new CreateSpaceRequest
        {
            Name = name,
            Type = type,
            Description = description,
            IsPublic = isPublic,
            IsHidden = isHidden,
            Members = members.ToList()
        };

        return await connection.InvokeAsync<SpaceDto?>("CreateSpace", request, cancellationToken);
    }

    public async Task<List<SpaceMemberDto>> GetMembersAsync(
        string spaceId,
        CancellationToken cancellationToken = default)
    {
        var tcpMembers = await TryTcpAsync("spaces.members", new TcpSpaceIdRequest { SpaceId = spaceId }, cancellationToken);
        if (tcpMembers.HasValue)
            return tcpMembers.Value.Deserialize<List<SpaceMemberDto>>(ChatJson.Options) ?? new List<SpaceMemberDto>();

        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return new List<SpaceMemberDto>();
        }

        return await connection.InvokeAsync<List<SpaceMemberDto>>(
            "GetSpaceMembers", spaceId, cancellationToken);
    }

    public async Task RecordViewsAsync(
        string spaceId,
        IEnumerable<string> messageIds,
        CancellationToken cancellationToken = default)
    {
        var list = messageIds.Take(200).ToList();
        if (list.Count == 0) return;
        var connection = await GetConnectionAsync();
        if (connection is null) return;
        try
        {
            await connection.InvokeAsync("RecordSpaceMessageViews", spaceId, list, cancellationToken);
        }
        catch { }
    }

    public async Task<IReadOnlyList<string>> GetViewersAsync(
        string spaceId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync();
        if (connection is null) return Array.Empty<string>();
        try
        {
            return await connection.InvokeAsync<List<string>>(
                "GetSpaceMessageViewers", spaceId, messageId, cancellationToken);
        }
        catch { return Array.Empty<string>(); }
    }

    public async Task<bool> JoinAsync(string spaceId, CancellationToken cancellationToken = default)
    {
        var tcp = await TryTcpAsync("spaces.join", new TcpSpaceJoinRequest { SpaceId = spaceId }, cancellationToken);
        if (tcp.HasValue)
        {
            return true;
        }

        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return false;
        }

        return await connection.InvokeAsync<bool>("JoinSpace", spaceId, cancellationToken);
    }

    /// <summary>Подать заявку на вступление в приватное пространство.</summary>
    public async Task<bool> RequestToJoinAsync(
        string spaceId,
        string message,
        CancellationToken cancellationToken = default)
    {
        var tcpReq = await TryTcpAsync("spaces.requestJoin", new TcpSpaceJoinRequestMessage { SpaceId = spaceId, Message = message }, cancellationToken);
        if (tcpReq.HasValue) return true;

        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return false;
        }

        return await connection.InvokeAsync<bool>(
            "RequestToJoinSpace", spaceId, message, cancellationToken);
    }

    /// <summary>Ожидающие заявки на вступление (для владельца и админов).</summary>
    public async Task<List<SpaceJoinRequestDto>> GetJoinRequestsAsync(
        string spaceId,
        CancellationToken cancellationToken = default)
    {
        var tcpReq = await TryTcpAsync("spaces.joinRequests", new TcpSpaceIdRequest { SpaceId = spaceId }, cancellationToken);
        if (tcpReq.HasValue)
            return tcpReq.Value.Deserialize<List<SpaceJoinRequestDto>>(ChatJson.Options) ?? new List<SpaceJoinRequestDto>();

        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return new List<SpaceJoinRequestDto>();
        }

        return await connection.InvokeAsync<List<SpaceJoinRequestDto>>(
            "GetJoinRequests", spaceId, cancellationToken)
            ?? new List<SpaceJoinRequestDto>();
    }

    /// <summary>Принять или отклонить заявку.</summary>
    public async Task<bool> ResolveJoinRequestAsync(
        string requestId,
        bool approved,
        CancellationToken cancellationToken = default)
    {
        var tcpReq = await TryTcpAsync("spaces.resolveJoin", new TcpSpaceJoinResolveRequest { RequestId = requestId, Approved = approved }, cancellationToken);
        if (tcpReq.HasValue) return true;

        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return false;
        }

        return await connection.InvokeAsync<bool>(
            "ResolveJoinRequest", requestId, approved, cancellationToken);
    }

    /// <summary>
    /// Приглашает участника в пространство по логину.
    /// Сервер добавляет его в список, а клиент расшифровывает общий ключ
    /// и передаёт новому участнику «конвертом», зашифрованным его публичным
    /// ключом (E2E — сервер ключа не видит).
    /// </summary>
    public async Task<InviteSpaceMemberResult> InviteMemberAsync(
        string spaceId,
        string username,
        CancellationToken cancellationToken = default)
    {
        var result = new InviteSpaceMemberResult();

        var token = _session.Token;
        if (string.IsNullOrWhiteSpace(token) || _session.User is null)
        {
            result.Error = "not_authorized";
            return result;
        }

        // Профиль нужен, чтобы получить Id и публичный ключ приглашаемого.
        var profile = await _apiClient.GetUserAsync(
            _session.ServerUrl,
            token,
            username.Trim(),
            cancellationToken);

        if (string.Equals(profile.Id, _session.User.Id, StringComparison.Ordinal))
        {
            result.Error = "already_member";
            result.Username = profile.Username;
            return result;
        }

        var spaceKey = await GetSpaceKeyAsync(spaceId);
        if (spaceKey is null)
        {
            result.Error = "key_unavailable";
            return result;
        }

        var tcpInvite = await TryTcpAsync("spaces.invite", new TcpSpaceInviteRequest
        {
            SpaceId = spaceId, Username = username.Trim()
        }, cancellationToken);
        var invite = tcpInvite.HasValue
            ? tcpInvite.Value.Deserialize<InviteSpaceMemberResult>(ChatJson.Options)
            : await (await GetConnectionAsync()).InvokeAsync<InviteSpaceMemberResult>(
            "InviteSpaceMember",
            spaceId,
            profile.Id,
            cancellationToken);

        if (invite is null || !invite.Success)
        {
            return invite ?? new InviteSpaceMemberResult { Error = "error" };
        }

        var envelope = SpaceCrypto.EncryptSpaceKey(
            spaceKey,
            spaceId,
            profile.Id,
            profile.EncryptionPublicKey);

        var connection = await GetConnectionAsync();
        var tcpKeySave = await TryTcpAsync("spaces.keySave", new TcpSpaceKeySaveRequest
        {
            SpaceId = spaceId,
            UserId = profile.Id,
            EnvelopeJson = ChatJson.Serialize(envelope)
        }, cancellationToken);
        var saved = tcpKeySave.HasValue || await connection.InvokeAsync<bool>(
            "SaveSpaceMemberKey",
            spaceId,
            profile.Id,
            ChatJson.Serialize(envelope),
            cancellationToken);

        if (!saved)
        {
            invite.Success = false;
            invite.Error = "key_save_failed";
            return invite;
        }

        return invite;
    }

    public async Task<bool> LeaveAsync(string spaceId, CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return false;
        }

        var tcpLeave = await TryTcpAsync("spaces.leave", new TcpSpaceIdRequest { SpaceId = spaceId }, cancellationToken);
        if (tcpLeave.HasValue) return true;
        return await connection.InvokeAsync<bool>("LeaveSpace", spaceId, cancellationToken);
    }

    public async Task<List<SpaceMessageDto>> GetMessagesAsync(
        string spaceId,
        string? topicId,
        CancellationToken cancellationToken = default)
    {
        var tcp = await TryTcpAsync("spaces.messages", new TcpSpaceMessagesRequest
        {
            SpaceId = spaceId,
            TopicId = topicId,
            Limit = 1000
        }, cancellationToken);
        if (tcp.HasValue)
        {
            var tcpMessages = tcp.Value.Deserialize<List<SpaceMessageDto>>(ChatJson.Options)
                ?? new List<SpaceMessageDto>();
            // Пустой ответ по TCP может быть гонкой подключения — страхуемся WebSocket.
            if (tcpMessages.Count > 0)
            {
                return tcpMessages;
            }
        }

        var connection = await GetConnectionAsync();
        return await connection.InvokeAsync<List<SpaceMessageDto>>(
            "GetSpaceMessages", spaceId, topicId, cancellationToken);
    }

    /// <summary>
    /// Шифрует содержимое сообщения общим ключом пространства. Отправитель — GUID пользователя,
    /// тот же идентификатор, по которому сервер проверяет подпись.
    /// </summary>
    private async Task<SpaceCipher> EncryptPayloadAsync(
        string spaceId,
        string messageId,
        SpaceMessagePayload payload,
        string? topicId)
    {
        var identity = _session.Identity
            ?? throw new InvalidOperationException("Ключи устройства не загружены.");
        var senderId = _session.User?.Id
            ?? throw new InvalidOperationException("Вы не вошли в аккаунт.");
        var key = await GetSpaceKeyAsync(spaceId)
            ?? throw new InvalidOperationException(
                "Нет ключа шифрования для этого пространства. " +
                "Попросите автора добавить вас в участники.");

        using var signingKey = identity.CreateSigningKey();
        var cipher = SpaceCrypto.EncryptMessage(
            key,
            spaceId,
            messageId,
            senderId,
            SpacePayloadCodec.Encode(payload),
            signingKey);

        cipher.TopicId = topicId;
        return cipher;
    }

    /// <summary>Отправляет новое сообщение пространства с произвольным содержимым.</summary>
    public async Task<SpaceMessageDto?> SendPayloadAsync(
        string spaceId,
        string? topicId,
        SpaceMessagePayload payload,
        CancellationToken cancellationToken = default)
    {
        var messageId = Guid.NewGuid().ToString("N");
        var cipher = await EncryptPayloadAsync(spaceId, messageId, payload, topicId);

        var connection = await GetConnectionAsync();
        return await connection.InvokeAsync<SpaceMessageDto?>(
            "SendSpaceMessage",
            new SendSpaceMessageRequest
            {
                SpaceId = spaceId,
                TopicId = topicId,
                Cipher = cipher
            },
            cancellationToken);
    }

    /// <summary>
    /// Создаёт отложенный пост: шифрует содержимое сейчас и сохраняет в
    /// локальной очереди до указанного времени.
    /// </summary>
    public async Task<ScheduledSpacePost?> ScheduleMessageAsync(
        string spaceId,
        string? topicId,
        string text,
        DateTime sendAt,
        CancellationToken cancellationToken = default)
    {
        var messageId = Guid.NewGuid().ToString("N");
        var payload = new SpaceMessagePayload
        {
            Kind = "text",
            Text = text
        };
        var cipher = await EncryptPayloadAsync(spaceId, messageId, payload, topicId);

        var post = new ScheduledSpacePost
        {
            Id = messageId,
            SpaceId = spaceId,
            TopicId = topicId,
            SendAtUnixMs = new DateTimeOffset(sendAt).ToUnixTimeMilliseconds(),
            Cipher = cipher,
            Preview = text.Length > 60 ? text[..60] + "…" : text
        };

        await AppServices.Get<LocalStore>().AddScheduledSpacePostAsync(post);
        return post;
    }

    /// <summary>
    /// Отправляет уже зашифрованное сообщение (используется планировщиком
    /// отложенных постов — шифротекст готов заранее и хранится на устройстве).
    /// </summary>
    public async Task<SpaceMessageDto?> SendCipherAsync(
        string spaceId,
        string? topicId,
        SpaceCipher cipher,
        CancellationToken cancellationToken = default)
    {
        var request = new TcpSpaceSendRequest
        {
            SpaceId = spaceId,
            TopicId = topicId,
            Cipher = cipher
        };

        var tcp = await TryTcpAsync("spaces.send", request, cancellationToken);
        if (tcp.HasValue)
        {
            return tcp.Value.Deserialize<SpaceMessageDto>(ChatJson.Options);
        }

        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return null;
        }

        return await connection.InvokeAsync<SpaceMessageDto?>(
            "SendSpaceMessage",
            new SendSpaceMessageRequest
            {
                SpaceId = spaceId,
                TopicId = topicId,
                Cipher = cipher
            },
            cancellationToken);
    }

    public Task<SpaceMessageDto?> SendMessageAsync(
        string spaceId,
        string? topicId,
        string text,
        string? replyToMessageId = null,
        string? replyToText = null,
        CancellationToken cancellationToken = default) =>
        SendPayloadAsync(
            spaceId,
            topicId,
            new SpaceMessagePayload
            {
                Kind = "text",
                Text = text,
                ReplyToMessageId = replyToMessageId,
                ReplyToText = replyToText,
            },
            cancellationToken);


    /// <summary>Шифрует файл собственным ключом, загружает шифротекст и отправляет сообщение-вложение.</summary>
    public async Task<SpaceMessageDto?> SendMediaAsync(
        string spaceId,
        string? topicId,
        string fileName,
        string contentType,
        byte[] data,
        string caption,
        string? replyToMessageId = null,
        string? replyToText = null,
        bool isForwarded = false,
        string? messageKind = null,
        CancellationToken cancellationToken = default,
        IProgress<int>? progress = null)
    {
        if (data.Length == 0)
        {
            throw new InvalidOperationException("Нельзя отправить пустой файл.");
        }

        if (data.Length > MediaLimits.MaxBytes)
        {
            throw new InvalidOperationException(
                $"Файл слишком большой. Максимум — {MediaLimits.MaxBytes / (1024 * 1024)} МБ.");
        }

        var mediaId = Guid.NewGuid().ToString("N");
        var encrypted = await Task.Run(() => MediaCrypto.Encrypt(data, mediaId), cancellationToken);

        var uploadProgress = new Progress<long>(bytes =>
        {
            if (data.Length > 0)
            {
                progress?.Report((int)(bytes * 100 / data.Length));
            }
        });

        await AppServices.Get<ApiClient>().UploadMediaAsync(
            _session.ServerUrl,
            _session.Token!,
            mediaId,
            string.Empty,
            encrypted.Ciphertext,
            uploadProgress,
            cancellationToken,
            spaceId: spaceId);

        var payload = new SpaceMessagePayload
        {
            Kind = messageKind
                ?? (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "image"
                : contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? MessageKinds.Voice
                : contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? MessageKinds.VideoCircle
                : "file"),
            Text = caption,
            ReplyToMessageId = replyToMessageId,
            ReplyToText = replyToText,
            IsForwarded = isForwarded,
            Media = new SpaceMediaAttachment
            {
                Id = mediaId,
                FileName = fileName,
                ContentType = contentType,
                Size = data.LongLength,
                Key = encrypted.Key,
                Nonce = encrypted.Nonce,
                Tag = encrypted.Tag
            }
        };

        return await SendPayloadAsync(spaceId, topicId, payload, cancellationToken);
    }

    /// <summary>Редактирует сообщение автора: участники получают новый шифротекст.</summary>
    public async Task<SpaceMessageDto?> EditMessageAsync(
        string spaceId,
        string messageId,
        SpaceMessagePayload payload,
        CancellationToken cancellationToken = default)
    {
        var cipher = await EncryptPayloadAsync(spaceId, messageId, payload, null);
        var tcpEdit = await TryTcpAsync("spaces.edit", new TcpSpaceEditRequest { SpaceId = spaceId, Cipher = cipher }, cancellationToken);
        if (tcpEdit.HasValue)
            return tcpEdit.Value.Deserialize<SpaceMessageDto>(ChatJson.Options);

        var connection = await GetConnectionAsync();
        return await connection.InvokeAsync<SpaceMessageDto?>(
            "EditSpaceMessage",
            spaceId,
            cipher,
            cancellationToken);
    }

    public async Task<bool> DeleteMessageAsync(
        string spaceId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        var tcpDel = await TryTcpAsync("spaces.delete", new TcpSpaceDeleteRequest { SpaceId = spaceId, MessageId = messageId }, cancellationToken);
        if (tcpDel.HasValue) return true;

        var connection = await GetConnectionAsync();
        return await connection.InvokeAsync<bool>(
            "DeleteSpaceMessage",
            spaceId,
            messageId,
            cancellationToken);
    }

    public async Task<SpaceMessageDto?> SetReactionAsync(
        string spaceId,
        string messageId,
        string emoji,
        bool on,
        CancellationToken cancellationToken = default)
    {
        var tcpReact = await TryTcpAsync("spaces.reaction", new TcpSpaceReactionRequest { SpaceId = spaceId, MessageId = messageId, Emoji = emoji, On = on }, cancellationToken);
        if (tcpReact.HasValue) return null;

        var connection = await GetConnectionAsync();
        return await connection.InvokeAsync<SpaceMessageDto?>(
            "SetSpaceReaction",
            spaceId,
            messageId,
            emoji,
            on,
            cancellationToken);
    }

    public async Task<bool> SetPinnedAsync(
        string spaceId,
        string messageId,
        bool pinned,
        CancellationToken cancellationToken = default)
    {
        var tcpPin = await TryTcpAsync("spaces.pinned", new TcpSpacePinnedRequest { SpaceId = spaceId, MessageId = messageId, Pinned = pinned }, cancellationToken);
        if (tcpPin.HasValue) return true;

        var connection = await GetConnectionAsync();
        return await connection.InvokeAsync<bool>(
            "SetSpacePinned",
            spaceId,
            messageId,
            pinned,
            cancellationToken);
    }

    public async Task<List<TopicDto>> GetTopicsAsync(
        string spaceId,
        CancellationToken cancellationToken = default)
    {
        var tcp = await TryTcpAsync("spaces.topics", new TcpSpaceIdRequest { SpaceId = spaceId }, cancellationToken);
        if (tcp.HasValue)
        {
            return tcp.Value.Deserialize<List<TopicDto>>(ChatJson.Options)
                ?? new List<TopicDto>();
        }

        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return new List<TopicDto>();
        }

        return await connection.InvokeAsync<List<TopicDto>>("GetTopics", spaceId, cancellationToken);
    }

    public async Task<TopicDto?> CreateTopicAsync(
        string spaceId,
        string title,
        string? text,
        CancellationToken cancellationToken = default)
    {
        var tcp = await TryTcpAsync("spaces.createTopic", new TcpSpaceTopicRequest
        {
            SpaceId = spaceId,
            Title = title,
            Text = text
        }, cancellationToken);
        if (tcp.HasValue)
        {
            return tcp.Value.Deserialize<TopicDto>(ChatJson.Options);
        }

        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return null;
        }

        var request = new CreateTopicRequest
        {
            SpaceId = spaceId,
            Title = title,
            Text = text
        };

        return await connection.InvokeAsync<TopicDto?>("CreateTopic", request, cancellationToken);
    }

    public async Task<bool> DeleteSpaceAsync(
        string spaceId,
        CancellationToken cancellationToken = default)
    {
        var tcpDelete = await TryTcpAsync("spaces.deleteSpace", new TcpSpaceDeleteSpaceRequest { SpaceId = spaceId }, cancellationToken);
        if (tcpDelete.HasValue) return true;

        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return false;
        }

        return await connection.InvokeAsync<bool>("DeleteSpace", spaceId, cancellationToken);
    }

    /// <summary>Изменяет название, описание, публичность и режимы пространства.</summary>
    public async Task<SpaceDto?> UpdateAsync(
        string spaceId,
        string name,
        string description,
        bool? isPublic = null,
        bool? isHidden = null,
        bool? signMessages = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return null;
        }

        var tcpUpdate = await TryTcpAsync("spaces.update", new TcpSpaceUpdateRequest
        {
            SpaceId = spaceId, Name = name, Description = description
        }, cancellationToken);
        if (tcpUpdate.HasValue)
            return tcpUpdate.Value.Deserialize<SpaceDto>(ChatJson.Options);

        var request = new UpdateSpaceRequest
        {
            SpaceId = spaceId,
            Name = name,
            Description = description,
            IsPublic = isPublic,
            IsHidden = isHidden,
            SignMessages = signMessages
        };

        return await connection.InvokeAsync<SpaceDto?>("UpdateSpace", request, cancellationToken);
    }

    /// <summary>Меняет роль участника и/или право писать (null — оставить как есть).</summary>
    public async Task<bool> UpdateMemberAsync(
        string spaceId,
        string userId,
        string? role = null,
        bool? canPost = null,
        CancellationToken cancellationToken = default)
    {
        var tcpMember = await TryTcpAsync("spaces.updateMember", new TcpSpaceMemberRequest
        {
            SpaceId = spaceId, UserId = userId, Role = role, CanPost = canPost
        }, cancellationToken);
        if (tcpMember.HasValue) return true;

        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return false;
        }

        return await connection.InvokeAsync<bool>(
            "UpdateSpaceMember", spaceId, userId, role, canPost, cancellationToken);
    }

    /// <summary>Запрещает участнику писать до указанного момента.</summary>
    public async Task<bool> MuteMemberAsync(
        string spaceId,
        string userId,
        DateTimeOffset until,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return false;
        }

        return await connection.InvokeAsync<bool>(
            "MuteSpaceMember", spaceId, userId, until.ToUnixTimeMilliseconds(), cancellationToken);
    }

    /// <summary>Снимает запрет записи с участника.</summary>
    public async Task<bool> UnmuteMemberAsync(
        string spaceId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return false;
        }

        return await connection.InvokeAsync<bool>(
            "UnmuteSpaceMember", spaceId, userId, cancellationToken);
    }

    /// <summary>Удаляет участника из пространства.</summary>
    public async Task<bool> RemoveMemberAsync(
        string spaceId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var tcpRemove = await TryTcpAsync("spaces.removeMember", new TcpSpaceRemoveMemberRequest { SpaceId = spaceId, UserId = userId }, cancellationToken);
        if (tcpRemove.HasValue) return true;

        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return false;
        }

        return await connection.InvokeAsync<bool>(
            "RemoveSpaceMember", spaceId, userId, cancellationToken);
    }

    /// <summary>Удаляет тему форума.</summary>
    public async Task<bool> DeleteTopicAsync(
        string spaceId,
        string topicId,
        CancellationToken cancellationToken = default)
    {
        var tcpTopic = await TryTcpAsync("spaces.deleteTopic", new TcpTopicDeleteRequest { SpaceId = spaceId, TopicId = topicId }, cancellationToken);
        if (tcpTopic.HasValue) return true;

        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return false;
        }

        return await connection.InvokeAsync<bool>(
            "DeleteTopic", spaceId, topicId, cancellationToken);
    }

    /// <summary>Переименовывает тему форума.</summary>
    public async Task<TopicDto?> RenameTopicAsync(
        string spaceId,
        string topicId,
        string title,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return null;
        }

        return await connection.InvokeAsync<TopicDto?>(
            "RenameTopic", spaceId, topicId, title, cancellationToken);
    }

    /// <summary>Закрывает или открывает тему форума.</summary>
    public async Task<TopicDto?> SetTopicClosedAsync(
        string spaceId,
        string topicId,
        bool isClosed,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return null;
        }

        return await connection.InvokeAsync<TopicDto?>(
            "SetTopicClosed", spaceId, topicId, isClosed, cancellationToken);
    }

    /// <summary>Меняет порядок темы в списке форума.</summary>
    public async Task<TopicDto?> SetTopicPositionAsync(
        string spaceId,
        string topicId,
        int position,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return null;
        }

        return await connection.InvokeAsync<TopicDto?>(
            "SetTopicPosition", spaceId, topicId, position, cancellationToken);
    }

    /// <summary>Скачивает и расшифровывает вложение пространства (доступно участникам).</summary>
    public async Task<byte[]> DownloadMediaAsync(
        SpaceMediaAttachment media,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IProgress<long>? bytesProgress = null;
        if (progress is not null && media.Size > 0)
        {
            bytesProgress = new Progress<long>(bytes =>
                progress.Report((int)Math.Clamp(bytes * 100L / media.Size, 0, 100)));
        }

        var ciphertext = await AppServices.Get<ApiClient>().DownloadMediaAsync(
            _session.ServerUrl,
            _session.Token!,
            media.Id,
            bytesProgress,
            cancellationToken);

        return await Task.Run(
            () => MediaCrypto.Decrypt(media.Id, ciphertext, media.Key, media.Nonce, media.Tag),
            cancellationToken);
    }

    /// <summary>Расшифровывает содержимое сообщения пространства: текст, ответ, пересылку и вложение.</summary>
    public async Task<SpaceMessagePayload?> DecryptPayloadAsync(
        string spaceId,
        SpaceMessageDto message,
        CancellationToken cancellationToken = default)
    {
        var key = await GetSpaceKeyAsync(spaceId);
        if (key is null)
        {
            return null;
        }

        try
        {
            var plaintext = SpaceCrypto.DecryptMessage(
                key,
                spaceId,
                message.Id,
                message.SenderId,
                new SpaceCipher
                {
                    MessageId = message.Id,
                    TopicId = message.TopicId,
                    Nonce = message.Nonce,
                    Tag = message.Tag,
                    Ciphertext = message.Ciphertext,
                    Signature = message.Signature
                });

            return SpacePayloadCodec.Decode(plaintext);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public async Task<string?> DecryptTextAsync(
        string spaceId,
        SpaceMessageDto message,
        CancellationToken cancellationToken = default) =>
        (await DecryptPayloadAsync(spaceId, message, cancellationToken))?.Text;
    private void OnTcpSpaceEvent(string type, System.Text.Json.JsonElement? payload)
    {
        if (type == "spaceMemberNeedKey")
        {
            var spaceId = payload.HasValue && payload.Value.ValueKind == System.Text.Json.JsonValueKind.String
                ? payload.Value.GetString()
                : null;
            var userId = payload.HasValue && payload.Value.ValueKind == System.Text.Json.JsonValueKind.Object &&
                         payload.Value.TryGetProperty("userId", out var uid) ? uid.GetString() : null;
            var username = payload.HasValue && payload.Value.ValueKind == System.Text.Json.JsonValueKind.Object &&
                           payload.Value.TryGetProperty("username", out var un) ? un.GetString() : null;
            if (!string.IsNullOrWhiteSpace(spaceId) && !string.IsNullOrWhiteSpace(userId))
                _ = ProvideSpaceKeyAsync(spaceId, userId, username ?? string.Empty);
        }
        else if (type == "spaceAdded" || type == "joinRequest" || type == "topicCreated")
        {
            SpaceKeyReady?.Invoke(string.Empty);
        }
    }

}
