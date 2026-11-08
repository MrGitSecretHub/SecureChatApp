using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using SecureChat.Core.Contracts;
using SecureChat.Core.Crypto;

namespace SecureChat.App.Services;

/// <summary>
/// Высокоуровневая логика чата: контакты, отправка, приём, расшифровка и
/// обновление локальной истории.
/// </summary>
public sealed class ChatManager
{
    private readonly AppSession _session;
    private readonly LocalStore _localStore;
    private readonly ChatService _chatService;
    private readonly ApiClient _apiClient;
    private readonly ConcurrentDictionary<string, Task> _mediaDownloads = new();
    private readonly ConcurrentDictionary<string, bool> _mutedChats = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _queueFlushGate = new(1, 1);
    private readonly object _reconnectSync = new();
    private string? _activeChat;
    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectTask;
    private bool _started;
    private volatile bool _silentConnect;
    private bool _presenceApplied;

    public ChatManager(
        AppSession session,
        LocalStore localStore,
        ChatService chatService,
        ApiClient apiClient)
    {
        _session = session;
        _localStore = localStore;
        _chatService = chatService;
        _apiClient = apiClient;

        _chatService.EnvelopeReceived += envelope =>
            _ = ProcessEnvelopeAsync(envelope);
        _chatService.EnvelopeAccepted += id =>
            _ = UpdateStatusAsync(new[] { id }, "Отправлено");
        _chatService.MessagesDelivered += ids =>
            _ = UpdateStatusAsync(ids, "Доставлено");
        _chatService.MessagesRead += ids =>
            _ = UpdateStatusAsync(ids, "Прочитано");
        _chatService.TypingReceived += username =>
            TypingReceived?.Invoke(username);
        _chatService.ConnectionStateChanged += state =>
        {
            IsOffline = !state;

            if (!state)
            {
                _presenceApplied = false;
            }
            else
            {
                _session.RestoredOffline = false;
            }

            ConnectionStateChanged?.Invoke(state);

            if (state)
            {
                _ = FlushPendingAsync();
            }
        };
        _chatService.ConnectionError += message =>
        {
            // Ошибки фоновых попыток подключения не показываем: для них
            // есть постоянный индикатор офлайн-режима.
            if (!_silentConnect && _chatService.IsConnected)
            {
                Error?.Invoke(message);
            }
        };
    }

    public event Action? ConversationsChanged;
    public event Action<StoredMessage, string>? MessageAdded;
    public event Action<StoredMessage, string>? IncomingMessage;
    public event Action<string, string>? MessageStatusChanged;
    public event Action<string, string>? MessageMediaChanged;
    public event Action<string>? TypingReceived;
    public event Action<string>? Error;
    public event Action<bool>? ConnectionStateChanged;

    /// <summary>Есть ли сейчас соединение с сервером. В офлайн-режиме false.</summary>
    public bool IsOnline => _chatService.IsConnected;

    /// <summary>Приложение работает без сервера: показана локальная история.</summary>
    public bool IsOffline { get; private set; }

    public async Task StartAsync()
    {
        await _localStore.InitializeAsync(_session.Username);
        await RefreshMuteCacheAsync();

        IsOffline = _session.RestoredOffline;
        _started = true;
        EnsureReconnectLoop();
    }

    public async Task SetPresenceHiddenAsync(bool hidden)
    {
        try
        {
            await _chatService.SetPresenceHiddenAsync(hidden);
            _presenceApplied = true;
        }
        catch
        {
            // Статус присутствия не критичен для доставки сообщений.
            _presenceApplied = false;
        }
    }

    private async Task ApplyPresenceAsync()
    {
        if (_presenceApplied || !_chatService.IsConnected)
        {
            return;
        }

        try
        {
            await _chatService.SetPresenceHiddenAsync(_session.HideOnlineStatus);
            _presenceApplied = true;
        }
        catch
        {
            // Старый сервер может не знать метод — статус просто останется видимым.
        }
    }

    private void EnsureReconnectLoop()
    {
        lock (_reconnectSync)
        {
            if (!_started || _reconnectTask is { IsCompleted: false })
            {
                return;
            }

            _reconnectCts?.Dispose();
            _reconnectCts = new CancellationTokenSource();
            _reconnectTask = Task.Run(() => ReconnectLoopAsync(_reconnectCts.Token));
        }
    }

    /// <summary>
    /// Поддерживает соединение в фоне: при недоступности сервера повторяет
    /// попытки, а после восстановления связи отправляет очередь сообщений.
    /// </summary>
    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(3);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_chatService.IsConnected)
                {
                    await ApplyPresenceAsync();
                    await FlushPendingAsync();
                    delay = TimeSpan.FromSeconds(10);
                }
                else if (!_chatService.IsReconnecting)
                {
                    if (await TryConnectAsync(cancellationToken))
                    {
                        delay = TimeSpan.FromSeconds(5);
                        await ApplyPresenceAsync();
                        await FlushPendingAsync();
                    }
                    else
                    {
                        delay = TimeSpan.FromSeconds(
                            Math.Min(delay.TotalSeconds * 2, 30));
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Повторим попытку после паузы.
            }

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch
            {
                break;
            }
        }
    }

    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));

        _silentConnect = true;
        try
        {
            await _chatService.ConnectAsync(timeout.Token);
            return _chatService.IsConnected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            IsOffline = true;
            ConnectionStateChanged?.Invoke(false);
            return false;
        }
        finally
        {
            _silentConnect = false;
        }
    }

    /// <summary>
    /// Отправляет накопленные офлайн-сообщения. Выполняется в фоне после
    /// подключения и не блокирует интерфейс.
    /// </summary>
    private async Task FlushPendingAsync()
    {
        if (!_chatService.IsConnected || !_session.IsAuthenticated)
        {
            return;
        }

        if (!_queueFlushGate.Wait(0))
        {
            return;
        }

        try
        {
            var user = _session.User;
            var identity = _session.Identity;
            if (user is null || identity is null)
            {
                return;
            }

            var pending = await _localStore.GetPendingOutgoingAsync();
            foreach (var item in pending)
            {
                if (!_chatService.IsConnected)
                {
                    break;
                }

                var conversation = await _localStore.GetConversationAsync(item.Username);
                if (conversation is null || IsSelfUser(conversation.UserId))
                {
                    continue;
                }

                var message = item.Message;
                if (message.Kind != MessageKinds.Text && message.Kind != MessageKinds.Sticker)
                {
                    try
                    {
                        if (!await TryUploadPendingMediaAsync(message, conversation))
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        // Связь снова пропала — очередь останется на месте.
                        break;
                    }
                }

                await SetStatusAsync(new[] { message.Id }, "Отправка");

                try
                {
                    using var signingKey = identity.CreateSigningKey();
                    var envelope = MessageCrypto.EncryptPayload(
                        message.Id,
                        user.Id,
                        user.Username,
                        conversation.UserId,
                        conversation.Username,
                        message.SentAtUnixMs,
                        CreatePayload(message),
                        conversation.EncryptionPublicKey,
                        signingKey);

                    var result = await _chatService.SendEnvelopeAsync(envelope);
                    if (result.Accepted)
                    {
                        await SetStatusAsync(new[] { message.Id }, "Отправлено");
                    }
                    else
                    {
                        await SetStatusAsync(new[] { message.Id }, "Ошибка");
                        Error?.Invoke(result.Error ?? "Сервер отклонил сообщение.");
                    }
                }
                catch
                {
                    await SetStatusAsync(new[] { message.Id }, LocalStore.QueuedStatus);
                    break;
                }
            }
        }
        catch
        {
            // Следующая попытка произойдёт в фоновом цикле.
        }
        finally
        {
            _queueFlushGate.Release();
        }
    }

    private async Task<bool> TryUploadPendingMediaAsync(
        StoredMessage message,
        StoredConversation conversation)
    {
        var ciphertext = await _localStore.LoadPendingUploadAsync(message.Id);
        if (ciphertext is null)
        {
            // Файл уже был загружен ранее, осталось отправить конверт.
            return true;
        }

        if (string.IsNullOrWhiteSpace(message.MediaId))
        {
            await SetStatusAsync(new[] { message.Id }, "Ошибка");
            Error?.Invoke("Не удалось отправить вложение: потеряны параметры файла.");
            return false;
        }

        try
        {
            await _apiClient.UploadMediaAsync(
                _session.ServerUrl,
                _session.Token!,
                message.MediaId,
                conversation.UserId,
                ciphertext);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // Файл уже на сервере (например, предыдущая попытка прервалась).
        }
        catch (ApiException ex)
        {
            await _localStore.DeletePendingUploadAsync(message.Id);
            await SetStatusAsync(new[] { message.Id }, "Ошибка");
            Error?.Invoke($"Не удалось отправить вложение: {ex.Message}");
            return false;
        }

        await _localStore.DeletePendingUploadAsync(message.Id);
        return true;
    }

    private static MessagePayload CreatePayload(StoredMessage message)
    {
        var payload = new MessagePayload
        {
            Text = message.Text,
            Kind = message.Kind,
            IsPinned = message.IsPinned,
            IsForwarded = message.IsForwarded,
            Reactions = LocalStore.ReadReactions(message.ReactionsJson)
        };

        if (message.Kind != MessageKinds.Text && message.Kind != MessageKinds.Sticker)
        {
            payload.Media = new MediaAttachment
            {
                Id = message.MediaId,
                FileName = message.FileName,
                ContentType = message.ContentType,
                Size = message.MediaSize,
                Key = message.MediaKey,
                Nonce = message.MediaNonce,
                Tag = message.MediaTag
            };
        }

        return payload;
    }

    public Task<IReadOnlyList<StoredConversation>> GetConversationsAsync() =>
        _localStore.GetConversationsAsync();

    public Task<IReadOnlyList<StoredConversation>> GetArchivedConversationsAsync() =>
        _localStore.GetArchivedConversationsAsync();

    public Task<StoredConversation?> GetConversationAsync(string username) =>
        _localStore.GetConversationAsync(username);

    public Task<bool> ArchiveConversationAsync(string username, bool archived = true)
    {
        // «Избранное» (хранилище) нельзя архивировать — оно всегда в основном списке.
        if (string.Equals(username, SelfChatUsername, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(false);
        }

        return _localStore.SetConversationArchivedAsync(username, archived);
    }

    /// <summary>Уведомления и звук для чата отключены.</summary>
    public bool IsConversationMuted(string username) =>
        _mutedChats.ContainsKey(username);

    public async Task<bool> SetConversationMutedAsync(string username, bool muted)
    {
        if (string.Equals(username, SelfChatUsername, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!await _localStore.SetConversationMutedAsync(username, muted))
        {
            return false;
        }

        if (muted)
        {
            _mutedChats[username] = true;
        }
        else
        {
            _mutedChats.TryRemove(username, out _);
        }

        ConversationsChanged?.Invoke();
        return true;
    }

    private async Task RefreshMuteCacheAsync()
    {
        _mutedChats.Clear();

        foreach (var conversation in await _localStore.GetConversationsAsync())
        {
            if (conversation.IsMuted)
            {
                _mutedChats[conversation.Username] = true;
            }
        }

        foreach (var conversation in await _localStore.GetArchivedConversationsAsync())
        {
            if (conversation.IsMuted)
            {
                _mutedChats[conversation.Username] = true;
            }
        }
    }

    public async Task<bool> DeleteConversationAsync(string username)
    {
        if (string.Equals(username, SelfChatUsername, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var deleted = await _localStore.DeleteConversationAsync(username);
        if (deleted)
        {
            _mutedChats.TryRemove(username, out _);
        }

        return deleted;
    }

    /// <summary>Имя пользователя для чата с самим собой (избранное/хранилище).</summary>
    public const string SelfChatUsername = "Избранное";

    public async Task<StoredConversation> GetOrCreateSelfConversationAsync()
    {
        var user = RequireUser();
        var conversation = await _localStore.AddOrUpdateContactAsync(new UserProfileDto
        {
            Id = user.Id,
            Username = SelfChatUsername,
            EncryptionPublicKey = user.EncryptionPublicKey,
            SigningPublicKey = user.SigningPublicKey,
            Fingerprint = user.Fingerprint,
            CreatedAt = user.CreatedAt
        });

        // «Избранное» не может быть в архиве — чиним состояние, если успели заархивировать.
        if (conversation.IsArchived)
        {
            await _localStore.SetConversationArchivedAsync(SelfChatUsername, false);
            conversation.IsArchived = false;
        }

        return conversation;
    }

    private bool IsSelfUser(string userId) =>
        string.Equals(userId, RequireUser().Id, StringComparison.Ordinal);

    /// <summary>Сообщения в «Избранном» хранятся только на устройстве и на сервер не уходят.</summary>
    private async Task SaveSelfMessageAsync(string text, string kind = MessageKinds.Text)
    {
        var self = await GetOrCreateSelfConversationAsync();
        var message = new StoredMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = text,
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            IsMine = true,
            Status = "Сохранено",
            DeliveryStatus = "Доставлено",
            Kind = kind,
            SenderUsername = self.Username
        };

        if (await _localStore.AddMessageAsync(self.Username, message, incrementUnread: false))
        {
            MessageAdded?.Invoke(message, self.Username);
            ConversationsChanged?.Invoke();
        }
    }

    private async Task SaveSelfMediaAsync(string fileName, string contentType, string kind, byte[] data)
    {
        var self = await GetOrCreateSelfConversationAsync();
        var messageId = Guid.NewGuid().ToString("N");
        await _localStore.SaveMediaAsync(messageId, data);

        var message = new StoredMessage
        {
            Id = messageId,
            Text = BuildMediaPreview(kind, fileName),
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            IsMine = true,
            Status = "Сохранено",
            DeliveryStatus = "Доставлено",
            Kind = kind,
            MediaId = messageId,
            FileName = fileName,
            ContentType = contentType,
            MediaSize = data.LongLength,
            MediaDownloaded = true,
            SenderUsername = self.Username
        };

        if (await _localStore.AddMessageAsync(self.Username, message, incrementUnread: false))
        {
            MessageAdded?.Invoke(message, self.Username);
            ConversationsChanged?.Invoke();
        }
    }

    public async Task<StoredConversation> AddContactAsync(string username)
    {
        var user = RequireUser();
        var profile = await _apiClient.GetUserAsync(_session.ServerUrl, _session.Token!, username.Trim());

        if (string.Equals(profile.Id, user.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Нельзя добавить в контакты самого себя.");
        }

        var conversation = await _localStore.AddOrUpdateContactAsync(profile);
        ConversationsChanged?.Invoke();
        return conversation;
    }

    public async Task OpenChatAsync(string username)
    {
        _activeChat = username;
        await _localStore.MarkReadAsync(username);
        ConversationsChanged?.Invoke();
        await SendReadReceiptsAsync(username);
    }

    public void CloseChat(string username)
    {
        if (string.Equals(_activeChat, username, StringComparison.OrdinalIgnoreCase))
        {
            _activeChat = null;
        }
    }

    public Task<byte[]?> GetLocalMediaAsync(string messageId) =>
        _localStore.LoadMediaAsync(messageId);

    /// <summary>
    /// Загружает вложение с сервера, если его ещё нет локально. Повторный
    /// вызов для того же сообщения переиспользует уже идущую загрузку.
    /// </summary>
    public Task EnsureMediaAsync(string username, string messageId)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(messageId))
        {
            return Task.CompletedTask;
        }

        return _mediaDownloads.GetOrAdd(
            messageId,
            _ => DownloadMediaCoreAsync(username, messageId));
    }

    public async Task SendMessageAsync(
        string username,
        string text,
        string? replyToMessageId = null,
        string? replyToText = null,
        string? replyToFileName = null,
        string kind = MessageKinds.Text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var user = RequireUser();
        var identity = _session.Identity
            ?? throw new InvalidOperationException("Ключи устройства не загружены.");

        var conversation = await _localStore.GetConversationAsync(username)
            ?? throw new InvalidOperationException("Контакт не найден.");

        await SendMessageToUserAsync(
            conversation.UserId,
            conversation.Username,
            text,
            conversation.EncryptionPublicKey,
            replyToMessageId: replyToMessageId,
            replyToText: replyToText,
            replyToFileName: replyToFileName,
            kind: kind);
    }

    /// <summary>
    /// Редактирует отправленное сообщение. Перешифровывает новый текст и отправляет
    /// на сервер как обычное сообщение с тем же id — получатель увидит отметку
    /// «отредактировано» и актуальный текст.
    /// </summary>
    public async Task EditMessageAsync(string username, string messageId, string newText)
    {
        if (string.IsNullOrWhiteSpace(newText))
        {
            return;
        }

        var user = RequireUser();
        var identity = _session.Identity
            ?? throw new InvalidOperationException("Ключи устройства не загружены.");

        var conversation = await _localStore.GetConversationAsync(username)
            ?? throw new InvalidOperationException("Контакт не найден.");

        var message = await _localStore.GetMessageAsync(username, messageId);
        if (message is null || !message.IsMine)
        {
            return;
        }

        if (IsSelfUser(conversation.UserId))
        {
            if (await _localStore.EditMessageAsync(username, messageId, newText))
            {
                var edited = await _localStore.GetMessageAsync(username, messageId);
                if (edited is not null)
                {
                    MessageAdded?.Invoke(edited, username);
                }
                ConversationsChanged?.Invoke();
            }
            return;
        }

        var sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var signingKey = identity.CreateSigningKey();
        var envelope = MessageCrypto.Encrypt(
            messageId,
            user.Id,
            user.Username,
            conversation.UserId,
            conversation.Username,
            sentAt,
            newText,
            conversation.EncryptionPublicKey,
            signingKey);

        var saved = await _localStore.EditMessageAsync(username, messageId, newText);
        if (saved)
        {
            var updated = await _localStore.GetMessageAsync(username, messageId);
            if (updated is not null)
            {
                MessageAdded?.Invoke(updated, username);
            }
            ConversationsChanged?.Invoke();
        }

        await SendEnvelopeAndUpdateStatusAsync(messageId, envelope);
    }

    /// <summary>
    /// Отправляет текстовое сообщение конкретному пользователю по его идентификатору.
    /// Гарантирует, что сообщение уйдёт именно тому аккаунту, чей UserId указан,
    /// независимо от отображаемого имени (например, «Избранное»).
    /// </summary>
    public async Task SendMessageToUserAsync(
        string recipientUserId,
        string recipientUsername,
        string text,
        string recipientEncryptionPublicKey,
        bool isForwarded = false,
        string? replyToMessageId = null,
        string? replyToText = null,
        string? replyToFileName = null,
        string kind = MessageKinds.Text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (IsSelfUser(recipientUserId))
        {
            await SaveSelfMessageAsync(text, kind);
            return;
        }

        var user = RequireUser();
        var identity = _session.Identity
            ?? throw new InvalidOperationException("Ключи устройства не загружены.");

        var messageId = Guid.NewGuid().ToString("N");
        var sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var signingKey = identity.CreateSigningKey();
        var payload = new MessagePayload
        {
            Text = text,
            Kind = kind,
            SentAtUnixMs = sentAt,
            IsForwarded = isForwarded,
            ReplyToMessageId = replyToMessageId,
            ReplyToText = replyToText,
            ReplyToFileName = replyToFileName
        };
        var envelope = MessageCrypto.EncryptPayload(
            messageId,
            user.Id,
            user.Username,
            recipientUserId,
            recipientUsername,
            sentAt,
            payload,
            recipientEncryptionPublicKey,
            signingKey);

        var conversation = await _localStore.GetConversationAsync(recipientUsername)
            ?? await _localStore.AddOrUpdateContactAsync(new UserProfileDto
            {
                Id = recipientUserId,
                Username = recipientUsername,
                EncryptionPublicKey = recipientEncryptionPublicKey,
                SigningPublicKey = string.Empty,
                Fingerprint = string.Empty,
                CreatedAt = DateTimeOffset.UtcNow
            });

        var localMessage = new StoredMessage
        {
            Id = messageId,
            Text = text,
            SentAtUnixMs = sentAt,
            IsMine = true,
            Status = LocalStore.QueuedStatus,
            IsForwarded = isForwarded,
            Kind = kind,
            SenderUsername = user.Username,
            ReplyToMessageId = replyToMessageId,
            ReplyToText = replyToText,
            ReplyToFileName = replyToFileName
        };

        if (await _localStore.AddMessageAsync(conversation.Username, localMessage, incrementUnread: false))
        {
            MessageAdded?.Invoke(localMessage, conversation.Username);
            ConversationsChanged?.Invoke();
        }

        if (!_chatService.IsConnected)
        {
            return;
        }

        await SendEnvelopeAndUpdateStatusAsync(messageId, envelope);
    }

    /// <summary>
    /// Удаляет сообщение локально (только свои сообщения).
    /// </summary>
    public async Task DeleteMessageAsync(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return;
        }

        var username = _activeChat;
        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        var removed = await _localStore.DeleteMessageAsync(username, messageId);
        if (removed)
        {
            ConversationsChanged?.Invoke();
        }
    }

    public async Task ToggleFavoriteAsync(string messageId, bool favorite)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return;
        }

        var username = _activeChat;
        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        var changed = await _localStore.SetFavoriteAsync(username, messageId, favorite);
        if (changed)
        {
            ConversationsChanged?.Invoke();
        }
    }

    public async Task<bool> ToggleReactionAsync(string messageId, string reaction)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        var username = _activeChat;
        if (string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        var changed = await _localStore.ToggleReactionAsync(
            username,
            messageId,
            reaction,
            _session.Username);

        if (changed)
        {
            ConversationsChanged?.Invoke();
        }

        var message = await _localStore.GetMessageAsync(username, messageId);
        if (message is not null)
        {
            MessageAdded?.Invoke(message, username);

            var reactions = LocalStore.ReadReactions(message.ReactionsJson);
            await SendMessageStateAsync(username, messageId, null, reactions);
        }

        return changed;
    }

    public async Task TogglePinnedAsync(string messageId, bool pinned)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return;
        }

        var username = _activeChat;
        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        var changed = await _localStore.SetPinnedAsync(username, messageId, pinned);
        if (changed)
        {
            ConversationsChanged?.Invoke();
        }

        var updated = await _localStore.GetMessageAsync(username, messageId);
        if (updated is not null)
        {
            MessageAdded?.Invoke(updated, username);
        }

        await SendMessageStateAsync(username, messageId, pinned, null);
    }

    /// <summary>
    /// Отправляет собеседнику обновление состояния сообщения (закрепление, реакции)
    /// тем же зашифрованным конвертом, что и при редактировании текста.
    /// </summary>
    private async Task SendMessageStateAsync(
        string username,
        string messageId,
        bool? pinned,
        IReadOnlyList<Reaction>? reactions)
    {
        try
        {
            if (!_chatService.IsConnected)
            {
                return;
            }

            var user = RequireUser();
            var identity = _session.Identity
                ?? throw new InvalidOperationException("Ключи устройства не загружены.");

            var conversation = await _localStore.GetConversationAsync(username);
            if (conversation is null || IsSelfUser(conversation.UserId))
            {
                return;
            }

            var message = await _localStore.GetMessageAsync(username, messageId);
            if (message is null)
            {
                return;
            }

            var payload = new MessagePayload
            {
                Text = message.Text,
                Kind = message.Kind,
                IsPinned = pinned ?? message.IsPinned,
                IsForwarded = message.IsForwarded,
                Reactions = reactions?.ToList()
                    ?? LocalStore.ReadReactions(message.ReactionsJson)
            };

            if (message.Kind != MessageKinds.Text && message.Kind != MessageKinds.Sticker)
            {
                payload.Media = new MediaAttachment
                {
                    Id = message.MediaId,
                    FileName = message.FileName,
                    ContentType = message.ContentType,
                    Size = message.MediaSize,
                    Key = message.MediaKey,
                    Nonce = message.MediaNonce,
                    Tag = message.MediaTag
                };
            }

            using var signingKey = identity.CreateSigningKey();
            var envelope = MessageCrypto.EncryptPayload(
                messageId,
                user.Id,
                user.Username,
                conversation.UserId,
                conversation.Username,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                payload,
                conversation.EncryptionPublicKey,
                signingKey);

            await _chatService.SendEnvelopeAsync(envelope);
        }
        catch
        {
            // Синхронизация состояния не критична: локально изменение уже применено.
        }
    }

    public Task SaveDraftAsync(string username, string text) =>
        _localStore.SaveDraftAsync(username, text);

    public Task<string> GetDraftAsync(string username) =>
        _localStore.GetDraftAsync(username);

    /// <summary>
    /// Шифрует файл, загружает шифротекст на сервер и отправляет E2E-конверт
    /// с ключом и метаданными вложения. Поддерживает отмену (CTS) и отчёт о
    /// прогрессе загрузки (<paramref name="progress"/> в процентах).
    /// </summary>
    public async Task SendMediaAsync(
        string username,
        string fileName,
        string contentType,
        byte[] data,
        bool isForwarded = false,
        string? messageKind = null,
        string? clientMessageId = null,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length == 0)
        {
            throw new InvalidOperationException("Нельзя отправить пустой файл.");
        }

        if (data.Length > MediaLimits.MaxBytes)
        {
            throw new InvalidOperationException(
                $"Файл слишком большой. Максимум — {MediaLimits.MaxBytes / (1024 * 1024)} МБ.");
        }

        var user = RequireUser();
        var identity = _session.Identity
            ?? throw new InvalidOperationException("Ключи устройства не загружены.");

        var conversation = await _localStore.GetConversationAsync(username)
            ?? throw new InvalidOperationException("Контакт не найден.");

        var safeName = SanitizeFileName(fileName);
        var normalizedContentType = string.IsNullOrWhiteSpace(contentType)
            ? "application/octet-stream"
            : contentType.Trim();
        var kind = messageKind
            ?? (normalizedContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? MessageKinds.Image
                : normalizedContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                    ? MessageKinds.Voice
                    : normalizedContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                        ? MessageKinds.VideoCircle
                        : MessageKinds.File);

        var messageId = clientMessageId ?? Guid.NewGuid().ToString("N");

        if (IsSelfUser(conversation.UserId))
        {
            await SaveSelfMediaAsync(safeName, normalizedContentType, kind, data);
            return;
        }

        var mediaId = Guid.NewGuid().ToString("N");
        var sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var encryptedMedia = await Task.Run(() => MediaCrypto.Encrypt(data, mediaId), cancellationToken);

        // Сохраняем и расшифрованный файл для просмотра, и шифротекст для
        // отправки. Если связи нет, шифротекст останется на устройстве
        // до восстановления соединения.
        await _localStore.SaveMediaAsync(messageId, data);
        await _localStore.SavePendingUploadAsync(messageId, encryptedMedia.Ciphertext);

        var localMessage = new StoredMessage
        {
            Id = messageId,
            Text = BuildMediaPreview(kind, safeName),
            SentAtUnixMs = sentAt,
            IsMine = true,
            Status = LocalStore.QueuedStatus,
            Kind = kind,
            MediaId = mediaId,
            FileName = safeName,
            ContentType = normalizedContentType,
            MediaSize = data.LongLength,
            MediaKey = encryptedMedia.Key,
            MediaNonce = encryptedMedia.Nonce,
            MediaTag = encryptedMedia.Tag,
            MediaDownloaded = true,
            IsForwarded = isForwarded,
            SenderUsername = user.Username
        };

        if (await _localStore.AddMessageAsync(username, localMessage, incrementUnread: false))
        {
            MessageAdded?.Invoke(localMessage, username);
            ConversationsChanged?.Invoke();
        }

        if (!_chatService.IsConnected)
        {
            return;
        }

        try
        {
            var uploadProgress = new Progress<long>(bytes =>
            {
                if (data.Length > 0)
                {
                    progress?.Report((int)(bytes * 100 / data.Length));
                }
            });
            await _apiClient.UploadMediaAsync(
                _session.ServerUrl,
                _session.Token!,
                mediaId,
                conversation.UserId,
                encryptedMedia.Ciphertext,
                uploadProgress,
                cancellationToken);
            await _localStore.DeletePendingUploadAsync(messageId);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // Вложение уже было загружено предыдущей попыткой.
            await _localStore.DeletePendingUploadAsync(messageId);
        }
        catch (OperationCanceledException)
        {
            // Пользователь отменил отправку — чистим отложенную загрузку.
            await _localStore.DeletePendingUploadAsync(messageId);
            throw;
        }
        catch (ApiException ex)
        {
            await _localStore.DeletePendingUploadAsync(messageId);
            await SetStatusAsync(new[] { messageId }, "Ошибка");
            Error?.Invoke($"Не удалось отправить вложение: {ex.Message}");
            return;
        }
        catch
        {
            await SetStatusAsync(new[] { messageId }, LocalStore.QueuedStatus);
            return;
        }

        using var signingKey = identity.CreateSigningKey();
        var payload = CreatePayload(localMessage);
        var envelope = MessageCrypto.EncryptPayload(
            messageId,
            user.Id,
            user.Username,
            conversation.UserId,
            conversation.Username,
            sentAt,
            payload,
            conversation.EncryptionPublicKey,
            signingKey);

        await SendEnvelopeAndUpdateStatusAsync(messageId, envelope);
    }

    private async Task DownloadMediaCoreAsync(string username, string messageId)
    {
        try
        {
            var message = await _localStore.GetMessageAsync(username, messageId);
            if (message is null ||
                message.MediaDownloaded ||
                string.IsNullOrWhiteSpace(message.MediaId) ||
                !_session.IsAuthenticated ||
                !_chatService.IsConnected)
            {
                return;
            }

            var ciphertext = await _apiClient.DownloadMediaAsync(
                _session.ServerUrl,
                _session.Token!,
                message.MediaId);

            var plaintext = await Task.Run(() => MediaCrypto.Decrypt(
                message.MediaId,
                ciphertext,
                message.MediaKey,
                message.MediaNonce,
                message.MediaTag));

            if (message.MediaSize > 0 && plaintext.LongLength != message.MediaSize)
            {
                throw new CryptographicException("Размер вложения не совпадает с заявленным.");
            }

            if (await _localStore.SaveMessageMediaAsync(username, messageId, plaintext))
            {
                MessageMediaChanged?.Invoke(messageId, username);
            }
        }
        catch (Exception ex)
        {
            if (_chatService.IsConnected)
            {
                Error?.Invoke($"Не удалось загрузить вложение: {ex.Message}");
            }
        }
        finally
        {
            _mediaDownloads.TryRemove(messageId, out _);
        }
    }

    private async Task SendEnvelopeAndUpdateStatusAsync(
        string messageId,
        EncryptedEnvelope envelope)
    {
        await SetStatusAsync(new[] { messageId }, "Отправка");

        if (!_chatService.IsConnected)
        {
            await SetStatusAsync(new[] { messageId }, LocalStore.QueuedStatus);
            return;
        }

        try
        {
            var result = await _chatService.SendEnvelopeAsync(envelope);
            if (result.Accepted)
            {
                await SetStatusAsync(new[] { messageId }, "Отправлено");
            }
            else
            {
                await SetStatusAsync(new[] { messageId }, "Ошибка");
                Error?.Invoke(result.Error ?? "Сервер отклонил сообщение.");
            }
        }
        catch
        {
            // Связь пропала во время отправки — сообщение уйдёт после
            // восстановления соединения.
            await SetStatusAsync(new[] { messageId }, LocalStore.QueuedStatus);
        }
    }

    private static bool IsValidMedia(MediaAttachment media)
    {
        if (!Guid.TryParse(media.Id, out _) ||
            string.IsNullOrWhiteSpace(media.FileName) ||
            string.IsNullOrWhiteSpace(media.ContentType) ||
            media.Size <= 0 ||
            media.Size > MediaLimits.MaxBytes)
        {
            return false;
        }

        try
        {
            return Convert.FromBase64String(media.Key).Length == 32 &&
                   Convert.FromBase64String(media.Nonce).Length == 12 &&
                   Convert.FromBase64String(media.Tag).Length == 16;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName.Trim());
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "file";
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name.Length <= 200 ? name : name[..200];
    }

    private static string BuildMediaPreview(string kind, string fileName) =>
        kind switch
        {
            MessageKinds.Image => "📷 Фото",
            MessageKinds.Voice => "🎤 Голосовое сообщение",
            MessageKinds.VideoCircle => "📹 Видеокружок",
            _ => $"📎 {fileName}"
        };

    public async Task NotifyTypingAsync(string recipientId)
    {
        if (!_session.ShowTypingStatus)
        {
            return;
        }

        try
        {
            await _chatService.SendTypingAsync(recipientId);
        }
        catch
        {
            // Индикатор набора текста не критичен для доставки сообщений.
        }
    }

    public async Task ShutdownAsync()
    {
        _activeChat = null;
        _started = false;

        CancellationTokenSource? cts;
        lock (_reconnectSync)
        {
            cts = _reconnectCts;
            _reconnectCts = null;
            _reconnectTask = null;
        }

        cts?.Cancel();
        cts?.Dispose();

        await _chatService.DisconnectAsync();
    }

    private async Task ProcessEnvelopeAsync(EncryptedEnvelope envelope)
    {
        try
        {
            var user = _session.User;
            var identity = _session.Identity;
            if (user is null || identity is null || !_session.IsAuthenticated)
            {
                return;
            }

            if (!string.Equals(envelope.RecipientId, user.Id, StringComparison.Ordinal) ||
                !string.Equals(envelope.RecipientUsername, user.Username, StringComparison.Ordinal))
            {
                return;
            }

            var conversation = await _localStore.GetConversationAsync(envelope.SenderUsername);
            if (_session.IsBlocked(envelope.SenderUsername) ||
                (_session.AcceptOnlyFromContacts && conversation is null))
            {
                try
                {
                    await _chatService.AckAsync(new[] { envelope.Id });
                }
                catch
                {
                    // Конверт сбросится при следующем подключении.
                }

                return;
            }

            UserProfileDto? fetchedProfile = null;

            // Всегда загружаем актуальный профиль отправителя — иначе аватар,
            // ник и статус собеседника не обновится после первого контакта.
            var profile = await _apiClient.GetUserAsync(
                _session.ServerUrl,
                _session.Token!,
                envelope.SenderUsername);

            if (!string.Equals(profile.Id, envelope.SenderId, StringComparison.Ordinal))
            {
                Error?.Invoke("Сервер вернул неверный профиль отправителя.");
                return;
            }

            fetchedProfile = profile;

            var signingKey = fetchedProfile?.SigningPublicKey ?? conversation!.SigningPublicKey;
            MessagePayload payload;
            try
            {
                payload = MessageCrypto.DecryptPayload(
                    envelope,
                    identity.EncryptionPrivateKey,
                    signingKey);
            }
            catch (CryptographicException)
            {
                // fetchedProfile уже загружен выше — ключ отправителя мог измениться.
                if (fetchedProfile is null)
                {
                    Error?.Invoke(
                        "Не удалось проверить подпись входящего сообщения. " +
                        "Сообщение отклонено.");
                    return;
                }

                if (string.Equals(
                        fetchedProfile.SigningPublicKey,
                        signingKey,
                        StringComparison.Ordinal))
                {
                    Error?.Invoke(
                        "Не удалось проверить подпись входящего сообщения. " +
                        "Сообщение отклонено.");
                    return;
                }

                signingKey = fetchedProfile.SigningPublicKey;

                payload = MessageCrypto.DecryptPayload(
                    envelope,
                    identity.EncryptionPrivateKey,
                    signingKey);
            }

            var kind = payload.Kind switch
            {
                MessageKinds.Image => MessageKinds.Image,
                MessageKinds.File => MessageKinds.File,
                MessageKinds.Voice => MessageKinds.Voice,
                MessageKinds.VideoCircle => MessageKinds.VideoCircle,
                MessageKinds.Sticker => MessageKinds.Sticker,
                _ => MessageKinds.Text
            };

            MediaAttachment? media = null;
            if (kind != MessageKinds.Text && kind != MessageKinds.Sticker)
            {
                media = payload.Media;
                if (media is null || !IsValidMedia(media))
                {
                    Error?.Invoke("Получено сообщение с некорректным вложением. Оно отклонено.");
                    return;
                }

                media.FileName = SanitizeFileName(media.FileName);
            }

            conversation = fetchedProfile is null
                ? conversation!
                : await _localStore.AddOrUpdateContactAsync(fetchedProfile);
            if (fetchedProfile is not null)
            {
                ConversationsChanged?.Invoke();
            }

            var message = new StoredMessage
            {
                Id = envelope.Id,
                Text = kind == MessageKinds.Text || kind == MessageKinds.Sticker
                    ? payload.Text
                    : BuildMediaPreview(kind, media!.FileName),
                SentAtUnixMs = envelope.SentAtUnixMs,
                IsMine = false,
                Status = "Получено",
                Kind = kind,
                MediaId = media?.Id ?? string.Empty,
                FileName = media?.FileName ?? string.Empty,
                ContentType = media?.ContentType ?? string.Empty,
                MediaSize = media?.Size ?? 0,
                MediaKey = media?.Key ?? string.Empty,
                MediaNonce = media?.Nonce ?? string.Empty,
                MediaTag = media?.Tag ?? string.Empty,
                MediaDownloaded = false,
                SenderUsername = envelope.SenderUsername,
                SenderAvatarBase64 = fetchedProfile?.AvatarBase64,
                IsPinned = payload.IsPinned,
                IsForwarded = payload.IsForwarded,
                ReplyToMessageId = payload.ReplyToMessageId,
                ReplyToText = payload.ReplyToText,
                ReplyToFileName = payload.ReplyToFileName,
                ReactionsJson = payload.Reactions is null or { Count: 0 }
                    ? string.Empty
                    : JsonSerializer.Serialize(payload.Reactions)
            };

            var shouldIncrement =
                !string.Equals(_activeChat, conversation.Username, StringComparison.OrdinalIgnoreCase);

            var existing = await _localStore.GetMessageAsync(conversation.Username, envelope.Id);
            if (existing is not null)
            {
                // Сообщение уже есть — это обновление состояния (закрепление/реакции/правка).
                var stateChanged = await _localStore.UpdateMessageStateAsync(
                    conversation.Username,
                    envelope.Id,
                    payload.IsPinned,
                    payload.Reactions);
                var textChanged = !string.Equals(existing.Text, message.Text, StringComparison.Ordinal);
                if (textChanged)
                {
                    await _localStore.ApplyRemoteEditAsync(
                        conversation.Username,
                        envelope.Id,
                        message.Text);
                }

                if (stateChanged || textChanged)
                {
                    var updated = await _localStore.GetMessageAsync(conversation.Username, envelope.Id);
                    if (updated is not null)
                    {
                        MessageAdded?.Invoke(updated, conversation.Username);
                        ConversationsChanged?.Invoke();
                    }
                }

                try
                {
                    await _chatService.AckAsync(new[] { envelope.Id });
                }
                catch
                {
                    // Повторная доставка будет обработана при следующем подключении.
                }

                return;
            }

            if (await _localStore.AddMessageAsync(
                    conversation.Username,
                    message,
                    shouldIncrement))
            {
                MessageAdded?.Invoke(message, conversation.Username);
                ConversationsChanged?.Invoke();

                if (!message.IsMine)
                {
                    IncomingMessage?.Invoke(message, conversation.Username);
                }
            }

            try
            {
                await _chatService.AckAsync(new[] { envelope.Id });
            }
            catch
            {
                // Повторная доставка будет обработана при следующем подключении.
            }

            if (!shouldIncrement)
            {
                await _localStore.MarkReadAsync(conversation.Username);
                await SendReadReceiptsAsync(conversation.Username);
            }

            if (kind != MessageKinds.Text && kind != MessageKinds.Sticker)
            {
                _ = EnsureMediaAsync(conversation.Username, message.Id);
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke($"Ошибка обработки входящего сообщения: {ex.Message}");
        }
    }

    private async Task SendReadReceiptsAsync(string username)
    {
        var ids = await _localStore.MarkIncomingReadAsync(username);
        if (ids.Count == 0 || _session.HideReadReceipts)
        {
            return;
        }

        try
        {
            await _chatService.ReadMessagesAsync(ids);
        }
        catch
        {
            // Отметка о прочтении не критична: статус отправителя просто не обновится.
        }
    }

    private async Task UpdateStatusAsync(IEnumerable<string> ids, string status)
    {
        var idArray = ids as string[] ?? ids.ToArray();
        var changed = await _localStore.UpdateStatusAsync(idArray, status);
        foreach (var id in idArray)
        {
            MessageStatusChanged?.Invoke(id, status);
        }

        if (changed.Count > 0)
        {
            ConversationsChanged?.Invoke();
        }
    }

    private async Task SetStatusAsync(IEnumerable<string> ids, string status)
    {
        var idArray = ids as string[] ?? ids.ToArray();
        await _localStore.UpdateStatusAsync(idArray, status);
        foreach (var id in idArray)
        {
            MessageStatusChanged?.Invoke(id, status);
        }
    }

    private UserProfileDto RequireUser()
    {
        if (!_session.IsAuthenticated || _session.User is null)
        {
            throw new InvalidOperationException("Нет активной сессии.");
        }

        return _session.User;
    }
}
