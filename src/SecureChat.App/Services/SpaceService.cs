using System.Security.Cryptography;
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

        var connection = await GetConnectionAsync();
        var envelope = await connection.InvokeAsync<SpaceKeyEnvelope?>(
            "GetSpaceKey",
            spaceId);

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
        return restored;
    }

    public event Action<SpaceMessageDto>? SpaceMessageReceived;
    public event Action<SpaceMessageDto>? SpaceMessageUpdated;
    public event Action<string, string>? SpaceMessageDeleted;
    public event Action<TopicDto>? TopicCreated;

    /// <summary>Тему переименовали, закрыли или переставили.</summary>
    public event Action<TopicDto>? TopicUpdated;

    private HubConnection? _subscribedConnection;
    public void Subscribe()
    {
        var connection = Connection;
        if (connection is null || ReferenceEquals(connection, _subscribedConnection))
        {
            return;
        }

        _subscribedConnection = connection;

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

        // Пригласили в новое пространство — подтягиваем участие в его группе,
        // чтобы получать сообщения без переподключения к серверу.
        connection.On<SpaceDto>(
            "SpaceAdded",
            async _ => { await JoinGroupsForCurrentConnectionAsync(); });

        // Кто-то вступил в публичное пространство: ключ до него должен доставить
        // любой онлайн-участник — пробуем мы, если ключ у нас есть.
        connection.On<string, string, string>(
            "SpaceMemberNeedKey",
            (spaceId, userId, username) => _ = ProvideSpaceKeyAsync(spaceId, userId, username));
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

    public async Task<List<SpaceDto>> ListAsync(CancellationToken cancellationToken = default)
    {
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
            Members = members.ToList()
        };

        return await connection.InvokeAsync<SpaceDto?>("CreateSpace", request, cancellationToken);
    }

    public async Task<List<SpaceMemberDto>> GetMembersAsync(
        string spaceId,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return new List<SpaceMemberDto>();
        }

        return await connection.InvokeAsync<List<SpaceMemberDto>>(
            "GetSpaceMembers", spaceId, cancellationToken);
    }

    public async Task<bool> JoinAsync(string spaceId, CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return false;
        }

        return await connection.InvokeAsync<bool>("JoinSpace", spaceId, cancellationToken);
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

        var connection = await GetConnectionAsync();
        var invite = await connection.InvokeAsync<InviteSpaceMemberResult>(
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

        var saved = await connection.InvokeAsync<bool>(
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

        return await connection.InvokeAsync<bool>("LeaveSpace", spaceId, cancellationToken);
    }

    public async Task<List<SpaceMessageDto>> GetMessagesAsync(
        string spaceId,
        string? topicId,
        CancellationToken cancellationToken = default)
    {
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
        bool? signMessages = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync();
        if (connection is null)
        {
            return null;
        }

        var request = new UpdateSpaceRequest
        {
            SpaceId = spaceId,
            Name = name,
            Description = description,
            IsPublic = isPublic,
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
        CancellationToken cancellationToken = default)
    {
        var ciphertext = await AppServices.Get<ApiClient>().DownloadMediaAsync(
            _session.ServerUrl,
            _session.Token!,
            media.Id,
            null,
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
}
