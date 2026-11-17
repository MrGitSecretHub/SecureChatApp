using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SecureChat.Core.Contracts;
using SecureChat.Core.Serialization;

namespace SecureChat.App.Services;

public sealed class StoredMessage
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public long SentAtUnixMs { get; set; }
    public bool IsMine { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Kind { get; set; } = MessageKinds.Text;
    public string MediaId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long MediaSize { get; set; }
    public string MediaKey { get; set; } = string.Empty;
    public string MediaNonce { get; set; } = string.Empty;
    public string MediaTag { get; set; } = string.Empty;
    public bool MediaDownloaded { get; set; }
    public bool IsFavorite { get; set; }
    public bool IsForwarded { get; set; }
    public string? ForwardedFrom { get; set; }
    public string? ReplyToMessageId { get; set; }
    public string? ReplyToText { get; set; }
    public string? ReplyToFileName { get; set; }
    public string? ReplyToSender { get; set; }
    public bool IsEdited { get; set; }
    public string? EditedText { get; set; }
    public string DeliveryStatus { get; set; } = "Отправка";
    public string SenderUsername { get; set; } = string.Empty;
    public string? SenderAvatarBase64 { get; set; }
    public bool IsPinned { get; set; } = false;
    public string? ReactionsJson { get; set; } = string.Empty;
    public bool IsDraft { get; set; } = false;
    public string? DraftText { get; set; } = string.Empty;

    /// <summary>Идентификатор альбома (несколько фото одним блоком).</summary>
    public string? AlbumId { get; set; }

    /// <summary>Номер вложения в альбоме (с нуля).</summary>
    public int AlbumIndex { get; set; }

    /// <summary>Всего вложений в альбоме.</summary>
    public int AlbumCount { get; set; }

    /// <summary>Одноразовое сообщение: исчезает у получателя после просмотра.</summary>
    public bool IsOnce { get; set; }
}

public sealed class StoredConversation
{
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string EncryptionPublicKey { get; set; } = string.Empty;
    public string SigningPublicKey { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string OnlineStatus { get; set; } = "Онлайн";
    public string OnlineStatusColor { get; set; } = "#3BA55D";
    public string? AvatarBase64 { get; set; }
    public string DraftText { get; set; } = string.Empty;
    public string LastMessage { get; set; } = string.Empty;
    public long LastMessageAtUnixMs { get; set; }
    public int UnreadCount { get; set; }
    public bool KeysChanged { get; set; }
    public bool IsArchived { get; set; }
    /// <summary>Уведомления и звук для этого чата отключены.</summary>
    public bool IsMuted { get; set; }
    public List<StoredMessage> Messages { get; set; } = new();
}

/// <summary>Совпадение при глобальном поиске по локальной истории.</summary>
public sealed class StoredMessageSearchResult
{
    public required string Username { get; init; }
    public required string MessageId { get; init; }
    public required string Text { get; init; }
    public long SentAtUnixMs { get; init; }
    public bool IsMine { get; init; }
}

/// <summary>Пользовательская папка чатов: имя и список логинов внутри неё.</summary>
public sealed class StoredChatFolder
{
    public required string Name { get; set; }
    public List<string> Usernames { get; set; } = new();
}

public sealed class LocalState
{
    public List<StoredConversation> Conversations { get; set; } = new();

    /// <summary>Пользовательские папки чатов; порядок = порядок вкладок.</summary>
    public List<StoredChatFolder> ChatFolders { get; set; } = new();

    /// <summary>Обои пространств: spaceId -> путь к файлу.</summary>
    public Dictionary<string, string> SpaceWallpaperPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Обои пространств: spaceId -> затемнять вуалью.</summary>
    public Dictionary<string, bool> SpaceWallpaperDims { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Пространства с отключённым звуком (локальная настройка устройства).</summary>
    public List<string> SpaceMutedIds { get; set; } = new();

    /// <summary>Свои пакеты GIF и стикеров, импортированные на устройство.</summary>
    public List<CustomPack> CustomPacks { get; set; } = new();
}

/// <summary>Пользовательский пак: набор файлов (GIF или стикеры) на устройстве.</summary>
public sealed class CustomPack
{
    public required string Id { get; set; }
    public required string Name { get; set; }

    /// <summary>"gif" — анимированные GIF, "sticker" — картинки-стикеры.</summary>
    public required string Kind { get; set; }

    public List<CustomPackItem> Items { get; set; } = new();
}

/// <summary>Файл внутри пользовательского пака.</summary>
public sealed class CustomPackItem
{
    public required string Id { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
}

/// <summary>Исходящее сообщение, ожидающее отправки после восстановления связи.</summary>
public sealed class PendingOutgoingMessage
{
    public required string Username { get; init; }
    public required StoredMessage Message { get; init; }
}

/// <summary>
/// Локальная история чатов. Файл целиком шифруется AES-256-GCM ключом,
/// который хранится в защищённом хранилище устройства.
/// </summary>
public sealed class LocalStore
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SCST");
    private static readonly byte[] Aad = Encoding.UTF8.GetBytes("SecureChat.LocalStore.v1");
    private const byte Version = 2;
    private const string DeviceKeyPrefix = "local.key";

    private static readonly byte[] MediaMagic = Encoding.ASCII.GetBytes("SCMD");
    private const byte MediaVersion = 1;
    private const string MediaDirectoryName = "securechat.media";

    /// <summary>Статус исходящего сообщения, которое ждёт восстановления сети.</summary>
    public const string QueuedStatus = "В очереди";

    private readonly SecretStore _secrets;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private LocalState? _state;
    private byte[] _deviceKey = Array.Empty<byte>();
    private string _owner = string.Empty;

    public LocalStore(SecretStore secrets) => _secrets = secrets;

    /// <summary>Файл истории отдельный для каждого аккаунта на этом устройстве.</summary>
    private string FilePath =>
        Path.Combine(FileSystem.AppDataDirectory, $"securechat.local.{_owner}.bin");

    private string MediaDirectory =>
        Path.Combine(FileSystem.AppDataDirectory, MediaDirectoryName, _owner);

    private string MediaPath(string messageId) =>
        Path.Combine(MediaDirectory, messageId + ".bin");

    /// <summary>Каталог файлов пользовательских паков (GIF и стикеры).</summary>
    private string PackDirectory =>
        Path.Combine(FileSystem.AppDataDirectory, "securechat.packs", _owner);

    private string PackPath(string itemId) =>
        Path.Combine(PackDirectory, itemId + ".bin");

    /// <summary>Шифротекст вложения, ещё не загруженный на сервер.</summary>
    private string PendingUploadPath(string messageId) =>
        Path.Combine(MediaDirectory, messageId + ".pending");

    /// <summary>
    /// Загружает историю владельца <paramref name="username"/>. Если в памяти открыта
    /// история другого аккаунта, она заменяется историей этого пользователя.
    /// </summary>
    public async Task InitializeAsync(string username)
    {
        var owner = username.Trim().ToLowerInvariant();
        if (owner.Length == 0)
        {
            throw new InvalidOperationException("Пользователь не задан, локальная история недоступна.");
        }

        await _lock.WaitAsync();
        try
        {
            if (_state is not null && _owner == owner)
            {
                return;
            }

            _state = null;
            _owner = owner;
            var deviceKeyName = $"{DeviceKeyPrefix}.{owner}";

            var keyBase64 = await _secrets.GetAsync(deviceKeyName);
            if (string.IsNullOrWhiteSpace(keyBase64))
            {
                _deviceKey = RandomNumberGenerator.GetBytes(32);
                await _secrets.SetAsync(deviceKeyName, Convert.ToBase64String(_deviceKey));
            }
            else
            {
                _deviceKey = Convert.FromBase64String(keyBase64);
            }

            if (File.Exists(FilePath))
            {
                var protectedBytes = await File.ReadAllBytesAsync(FilePath);
                _state = Decrypt(protectedBytes);
            }
            else
            {
                _state = new LocalState();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<StoredConversation>> GetConversationsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            return _state!.Conversations
                .Where(item => !item.IsArchived)
                .OrderByDescending(item => item.LastMessageAtUnixMs)
                .ThenBy(item => item.Username, StringComparer.OrdinalIgnoreCase)
                .Select(Clone)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<StoredConversation>> GetArchivedConversationsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            return _state!.Conversations
                .Where(item => item.IsArchived)
                .OrderByDescending(item => item.LastMessageAtUnixMs)
                .ThenBy(item => item.Username, StringComparer.OrdinalIgnoreCase)
                .Select(Clone)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> SetConversationArchivedAsync(string username, bool archived)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var conversation = Find(username);
            if (conversation is null)
            {
                return false;
            }

            conversation.IsArchived = archived;
            await SaveAsync();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> SetConversationMutedAsync(string username, bool muted)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var conversation = Find(username);
            if (conversation is null)
            {
                return false;
            }

            conversation.IsMuted = muted;
            await SaveAsync();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Локальная настройка: пространства с отключённым звуком.</summary>
    public async Task<IReadOnlyList<string>> GetMutedSpaceIdsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            return _state!.SpaceMutedIds.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetSpaceMutedAsync(string spaceId, bool muted)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var list = _state!.SpaceMutedIds;
            var exists = list.Any(item => string.Equals(item, spaceId, StringComparison.Ordinal));
            if (muted && !exists)
            {
                list.Add(spaceId);
            }
            else if (!muted && exists)
            {
                list.RemoveAll(item => string.Equals(item, spaceId, StringComparison.Ordinal));
            }

            await SaveAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Пользовательские папки в порядке панели вкладок.</summary>
    public async Task<IReadOnlyList<StoredChatFolder>> GetChatFoldersAsync()
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            return _state!.ChatFolders
                .Select(folder => new StoredChatFolder
                {
                    Name = folder.Name,
                    Usernames = new List<string>(folder.Usernames),
                })
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Создаёт папку (originalName = null) либо переименовывает её и задаёт состав.
    /// false — если исходная папка не найдена или имя уже занято другой папкой.
    /// </summary>
    public async Task<bool> SaveChatFolderAsync(string? originalName, string name, List<string> usernames)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var folders = _state!.ChatFolders;
            StoredChatFolder? target = null;
            if (originalName is not null)
            {
                target = folders.FirstOrDefault(f =>
                    string.Equals(f.Name, originalName, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    return false;
                }
            }

            var clash = folders.FirstOrDefault(f =>
                string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (clash is not null && !ReferenceEquals(clash, target))
            {
                return false;
            }

            if (target is null)
            {
                target = new StoredChatFolder { Name = name };
                folders.Add(target);
            }

            target.Name = name;
            target.Usernames = usernames;
            await SaveAsync();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteChatFolderAsync(string name)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var folder = _state!.ChatFolders.FirstOrDefault(f =>
                string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (folder is null)
            {
                return false;
            }

            _state!.ChatFolders.Remove(folder);
            await SaveAsync();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteConversationAsync(string username)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var conversation = Find(username);
            if (conversation is null)
            {
                return false;
            }

            _state!.Conversations.Remove(conversation);
            await SaveAsync();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Удаляет все сообщения в диалоге, но оставляет сам чат в списке.</summary>
    public async Task<bool> ClearConversationMessagesAsync(string username)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var conversation = Find(username);
            if (conversation is null)
            {
                return false;
            }

            conversation.Messages.Clear();
            conversation.LastMessage = string.Empty;
            conversation.LastMessageAtUnixMs = 0;
            conversation.UnreadCount = 0;
            await SaveAsync();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Получить путь к обоям пространства.</summary>
    public async Task<string?> GetSpaceWallpaperPathAsync(string spaceId)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            _state!.SpaceWallpaperPaths.TryGetValue(spaceId, out var path);
            return path;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Получить флаг затемнения обоев пространства.</summary>
    public async Task<bool> GetSpaceWallpaperDimAsync(string spaceId)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            return _state!.SpaceWallpaperDims.TryGetValue(spaceId, out var dim) && dim;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Сохранить обои пространства.</summary>
    public async Task<bool> SetSpaceWallpaperAsync(string spaceId, string path, bool dim)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            _state!.SpaceWallpaperPaths[spaceId] = path;
            _state!.SpaceWallpaperDims[spaceId] = dim;
            await SaveAsync();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Удалить обои пространства.</summary>
    public async Task<bool> RemoveSpaceWallpaperAsync(string spaceId)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var removedPaths = _state!.SpaceWallpaperPaths.Remove(spaceId);
            var removedDims = _state!.SpaceWallpaperDims.Remove(spaceId);
            if (removedPaths || removedDims)
            {
                await SaveAsync();
            }
            return removedPaths || removedDims;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<StoredConversation?> GetConversationAsync(string username)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var conversation = Find(username);
            return conversation is null ? null : Clone(conversation);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<StoredMessage?> GetMessageAsync(string username, string messageId)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var message = Find(username)?.Messages.FirstOrDefault(item => item.Id == messageId);
            return message is null ? null : CloneMessage(message);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Ищет подстроку во всех сообщениях (включая архив). Черновики
    /// и служебные записи без текста пропускаются.
    /// </summary>
    public async Task<IReadOnlyList<StoredMessageSearchResult>> SearchMessagesAsync(
        string query,
        int limit = 100)
    {
        var needle = query?.Trim() ?? string.Empty;
        if (needle.Length < 2)
        {
            return Array.Empty<StoredMessageSearchResult>();
        }

        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            return _state!.Conversations
                .SelectMany(conversation => conversation.Messages
                    .Where(message =>
                        !message.IsDraft &&
                        !string.IsNullOrEmpty(message.Text) &&
                        message.Text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    .Select(message => new StoredMessageSearchResult
                    {
                        Username = conversation.Username,
                        MessageId = message.Id,
                        Text = message.Text,
                        SentAtUnixMs = message.SentAtUnixMs,
                        IsMine = message.IsMine
                    }))
                .OrderByDescending(item => item.SentAtUnixMs)
                .Take(limit)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Возвращает исходящие сообщения, которые не удалось отправить при потере
    /// связи. Они отправляются автоматически после переподключения.
    /// </summary>
    public async Task<IReadOnlyList<PendingOutgoingMessage>> GetPendingOutgoingAsync()
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();

            return _state!.Conversations
                .SelectMany(conversation => conversation.Messages
                    .Where(message => message.IsMine &&
                        (string.Equals(message.DeliveryStatus, QueuedStatus, StringComparison.Ordinal) ||
                         string.Equals(message.DeliveryStatus, "Не отправлено", StringComparison.Ordinal)))
                    .Select(message => new PendingOutgoingMessage
                    {
                        Username = conversation.Username,
                        Message = CloneMessage(message)
                    }))
                .OrderBy(item => item.Message.SentAtUnixMs)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteMessageAsync(string username, string messageId)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var conversation = Find(username);
            if (conversation is null)
            {
                return false;
            }

            var removed = conversation.Messages.RemoveAll(item => item.Id == messageId) > 0;
            if (removed)
            {
                await SaveAsync();
                DeletePendingUploadFile(messageId);
            }

            return removed;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Ищет логин чата по идентификатору собеседника — сервер присылает в событиях
    /// удаления именно UserId, а локальные чаты хранятся по логину.
    /// </summary>
    public async Task<string?> FindUsernameByUserIdAsync(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            return _state!.Conversations
                .FirstOrDefault(item => string.Equals(item.UserId, userId, StringComparison.Ordinal))
                ?.Username;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Удаляет сообщение из всех чатов по его идентификатору.</summary>
    public async Task<bool> DeleteMessageEverywhereAsync(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var removed = false;
            foreach (var conversation in _state!.Conversations)
            {
                if (conversation.Messages.RemoveAll(item => item.Id == messageId) > 0)
                {
                    removed = true;
                }
            }

            if (removed)
            {
                await SaveAsync();
                DeletePendingUploadFile(messageId);
            }

            return removed;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> SetFavoriteAsync(string username, string messageId, bool favorite)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var conversation = Find(username);
            if (conversation is null)
            {
                return false;
            }

            var message = conversation.Messages.FirstOrDefault(item => item.Id == messageId);
            if (message is null || message.IsFavorite == favorite)
            {
                return false;
            }

            message.IsFavorite = favorite;
            await SaveAsync();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> ToggleReactionAsync(
        string username,
        string messageId,
        string reaction,
        string userId)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var conversation = Find(username);
            if (conversation is null)
            {
                return false;
            }

            var message = conversation.Messages.FirstOrDefault(item => item.Id == messageId);
            if (message is null)
            {
                return false;
            }

            var reactions = ReadReactions(message.ReactionsJson);

            // Снимается только собственная реакция: чужие не затрагиваются.
            var existing = reactions.FirstOrDefault(item =>
                string.Equals(item.Emoji, reaction, StringComparison.Ordinal) &&
                string.Equals(item.UserId, userId, StringComparison.Ordinal));

            if (existing is not null)
            {
                reactions.Remove(existing);
            }
            else
            {
                reactions.Add(new Reaction { Emoji = reaction, UserId = userId });
            }

            message.ReactionsJson = JsonSerializer.Serialize(reactions);
            await SaveAsync();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Заменяет набор реакций (приходит от собеседника).</summary>
    public async Task<bool> SetReactionsAsync(
        string username,
        string messageId,
        IReadOnlyList<Reaction> reactions)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var conversation = Find(username);
            if (conversation is null)
            {
                return false;
            }

            var message = conversation.Messages.FirstOrDefault(item => item.Id == messageId);
            if (message is null)
            {
                return false;
            }

            var json = JsonSerializer.Serialize(reactions);
            if (string.Equals(message.ReactionsJson, json, StringComparison.Ordinal))
            {
                return false;
            }

            message.ReactionsJson = json;
            await SaveAsync();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    internal static List<Reaction> ReadReactions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<Reaction>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<Reaction>>(json) ?? new List<Reaction>();
        }
        catch (JsonException)
        {
            // Старый формат — простой список эмодзи без авторов.
            try
            {
                var legacy = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                return legacy
                    .Select(item => new Reaction { Emoji = item, UserId = string.Empty })
                    .ToList();
            }
            catch (JsonException)
            {
                return new List<Reaction>();
            }
        }
    }

    public async Task<bool> UpdateMessageStateAsync(
        string username,
        string messageId,
        bool? pinned,
        IReadOnlyList<Reaction>? reactions)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var conversation = Find(username);
            if (conversation is null)
            {
                return false;
            }

            var message = conversation.Messages.FirstOrDefault(item => item.Id == messageId);
            if (message is null)
            {
                return false;
            }

            var changed = false;
            if (pinned is not null && message.IsPinned != pinned.Value)
            {
                message.IsPinned = pinned.Value;
                changed = true;
            }

            if (reactions is not null)
            {
                var json = JsonSerializer.Serialize(reactions.ToList());
                if (!string.Equals(message.ReactionsJson, json, StringComparison.Ordinal))
                {
                    message.ReactionsJson = json;
                    changed = true;
                }
            }
            if (changed)
            {
                await SaveAsync();
            }

            return changed;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> SetPinnedAsync(string username, string messageId, bool pinned)    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var conversation = Find(username);
            if (conversation is null)
            {
                return false;
            }

            var message = conversation.Messages.FirstOrDefault(item => item.Id == messageId);
            if (message is null || message.IsPinned == pinned)
            {
                return false;
            }

            message.IsPinned = pinned;
            await SaveAsync();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveDraftAsync(string username, string text)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var conversation = Find(username);
            if (conversation is null)
            {
                return;
            }

            conversation.DraftText = text ?? string.Empty;
            await SaveAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> GetDraftAsync(string username)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            return Find(username)?.DraftText ?? string.Empty;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<StoredConversation> AddOrUpdateContactAsync(UserProfileDto profile)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var conversation = Find(profile.Username);
            if (conversation is null)
            {
                conversation = new StoredConversation
                {
                    UserId = profile.Id,
                    Username = profile.Username,
                    EncryptionPublicKey = profile.EncryptionPublicKey,
                    SigningPublicKey = profile.SigningPublicKey,
                    Fingerprint = profile.Fingerprint
                };
                _state!.Conversations.Add(conversation);
            }
            else
            {
                if (!string.Equals(
                        conversation.SigningPublicKey,
                        profile.SigningPublicKey,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        conversation.EncryptionPublicKey,
                        profile.EncryptionPublicKey,
                        StringComparison.Ordinal))
                {
                    conversation.KeysChanged = true;
                }

                conversation.UserId = profile.Id;
                conversation.Username = profile.Username;
                conversation.EncryptionPublicKey = profile.EncryptionPublicKey;
                conversation.SigningPublicKey = profile.SigningPublicKey;
                conversation.Fingerprint = profile.Fingerprint;
                conversation.OnlineStatus = profile.OnlineStatus ?? "Онлайн";
                conversation.OnlineStatusColor = profile.OnlineStatusColor ?? "#3BA55D";
                conversation.AvatarBase64 = profile.AvatarBase64;
            }

            await SaveAsync();
            return Clone(conversation);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> AddMessageAsync(
        string username,
        StoredMessage message,
        bool incrementUnread)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var conversation = Find(username)
                ?? throw new InvalidOperationException("Контакт не найден.");

            if (conversation.Messages.Any(item => item.Id == message.Id))
            {
                return false;
            }

            // Копия сохраняется целиком: иначе теряются автор, закрепление,
            // реакции, признак пересылки и черновик.
            conversation.Messages.Add(CloneMessage(message));

            if (message.SentAtUnixMs >= conversation.LastMessageAtUnixMs)
            {
                conversation.LastMessage = message.Text;
                conversation.LastMessageAtUnixMs = message.SentAtUnixMs;
            }

            if (!message.IsMine && incrementUnread)
            {
                conversation.UnreadCount++;
            }

            await SaveAsync();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Отмечает входящие сообщения прочитанными и возвращает их id для отправки отправителю.</summary>
    public async Task<IReadOnlyList<string>> MarkIncomingReadAsync(string username)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var conversation = Find(username);
            if (conversation is null)
            {
                return Array.Empty<string>();
            }

            var unread = conversation.Messages
                .Where(item => !item.IsMine && item.Status == "Получено")
                .ToList();
            if (unread.Count == 0)
            {
                return Array.Empty<string>();
            }

            foreach (var message in unread)
            {
                message.Status = "Прочитано";
            }

            await SaveAsync();
            return unread.Select(item => item.Id).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task MarkReadAsync(string username)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var conversation = Find(username);
            if (conversation is null || conversation.UnreadCount == 0)
            {
                return;
            }

            conversation.UnreadCount = 0;
            await SaveAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> UpdateStatusAsync(
        IEnumerable<string> messageIds,
        string status)
    {
        var ids = messageIds.ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return Array.Empty<string>();
        }

        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var changed = new List<string>();

            foreach (var conversation in _state!.Conversations)
            {
                var touched = false;
                foreach (var message in conversation.Messages)
                {
                    if (!ids.Contains(message.Id))
                    {
                        continue;
                    }

                    var rank = DeliveryRank(status);
                    var currentRank = DeliveryRank(message.DeliveryStatus);

                    // Служебные статусы («В очереди», «Ошибка») не должны
                    // понижать уже отправленные или доставленные сообщения.
                    if (rank < 0 && currentRank > 0)
                    {
                        continue;
                    }

                    if (rank >= 0 && rank < currentRank)
                    {
                        continue;
                    }

                    if (message.Status != status ||
                        (message.IsMine && message.DeliveryStatus != status))
                    {
                        message.Status = status;
                        if (message.IsMine)
                        {
                            message.DeliveryStatus = status;
                        }

                        touched = true;
                    }
                }

                if (touched)
                {
                    changed.Add(conversation.Username);
                }
            }

            if (changed.Count > 0)
            {
                await SaveAsync();
            }

            return changed;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearKeysChangedAsync(string username)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var conversation = Find(username);
            if (conversation is null || !conversation.KeysChanged)
            {
                return;
            }

            conversation.KeysChanged = false;
            await SaveAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> EditMessageAsync(string username, string messageId, string newText)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var conversation = Find(username);
            if (conversation is null)
            {
                return false;
            }

            var message = conversation.Messages.FirstOrDefault(item => item.Id == messageId);
            if (message is null || !message.IsMine)
            {
                return false;
            }

            message.Text = newText;
            message.EditedText = newText;
            message.IsEdited = true;
            message.Status = "Отредактировано";
            await SaveAsync();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Применяет правку текста, пришедшую от собеседника.</summary>
    public async Task<bool> ApplyRemoteEditAsync(string username, string messageId, string newText)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var conversation = Find(username);
            if (conversation is null)
            {
                return false;
            }

            var message = conversation.Messages.FirstOrDefault(item => item.Id == messageId);
            if (message is null || string.Equals(message.EditedText, newText, StringComparison.Ordinal))
            {
                return false;
            }

            message.Text = newText;
            message.EditedText = newText;
            message.IsEdited = true;
            await SaveAsync();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> SetDeliveryStatusAsync(string username, string messageId, string status)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var conversation = Find(username);
            if (conversation is null)
            {
                return false;
            }

            var message = conversation.Messages.FirstOrDefault(item => item.Id == messageId);
            if (message is null || !message.IsMine)
            {
                return false;
            }

            message.DeliveryStatus = status;
            await SaveAsync();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Сохраняет расшифрованное вложение в зашифрованном виде на диск.</summary>
    public async Task SaveMediaAsync(string messageId, byte[] plaintext)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            await SaveMediaFileAsync(messageId, plaintext);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Сохраняет уже зашифрованный файл, который нужно загрузить на сервер
    /// после восстановления связи.
    /// </summary>
    public async Task SavePendingUploadAsync(string messageId, byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            if (!Guid.TryParse(messageId, out _))
            {
                throw new ArgumentException("Некорректный идентификатор сообщения.", nameof(messageId));
            }

            Directory.CreateDirectory(MediaDirectory);
            var path = PendingUploadPath(messageId);
            var tempPath = path + ".tmp";
            await File.WriteAllBytesAsync(tempPath, ciphertext);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<byte[]?> LoadPendingUploadAsync(string messageId)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
        }
        finally
        {
            _lock.Release();
        }

        if (!Guid.TryParse(messageId, out _))
        {
            return null;
        }

        var path = PendingUploadPath(messageId);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
    }

    public async Task DeletePendingUploadAsync(string messageId)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
        }
        finally
        {
            _lock.Release();
        }

        DeletePendingUploadFile(messageId);
    }

    private void DeletePendingUploadFile(string messageId)
    {
        if (!Guid.TryParse(messageId, out _))
        {
            return;
        }

        try
        {
            var path = PendingUploadPath(messageId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Не удалось удалить временный файл — он не влияет на историю.
        }
    }

    /// <summary>
    /// Сохраняет вложение и отмечает сообщение как готовое к просмотру.
    /// </summary>
    public async Task<bool> SaveMessageMediaAsync(
        string username,
        string messageId,
        byte[] plaintext)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var message = Find(username)?.Messages.FirstOrDefault(item => item.Id == messageId);
            if (message is null)
            {
                return false;
            }

            await SaveMediaFileAsync(messageId, plaintext);
            message.MediaDownloaded = true;
            await SaveAsync();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<byte[]?> LoadMediaAsync(string messageId)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
        }
        finally
        {
            _lock.Release();
        }

        if (!Guid.TryParse(messageId, out _))
        {
            return null;
        }

        var path = MediaPath(messageId);
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = await File.ReadAllBytesAsync(path);
        return DecryptMedia(messageId, protectedBytes);
    }

    // ================================================================
    // СВОИ ПАКИ (GIF и стикеры) — файлы на устройстве, шифруются ключом устройства
    // ================================================================

    /// <summary>Пакеты пользователя (метаданные) в порядке добавления.</summary>
    public async Task<IReadOnlyList<CustomPack>> GetCustomPacksAsync()
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            return _state!.CustomPacks
                .Select(pack => new CustomPack
                {
                    Id = pack.Id,
                    Name = pack.Name,
                    Kind = pack.Kind,
                    Items = pack.Items
                        .Select(item => new CustomPackItem
                        {
                            Id = item.Id,
                            FileName = item.FileName,
                            ContentType = item.ContentType,
                            Size = item.Size
                        })
                        .ToList()
                })
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Создаёт или обновляет пак.</summary>
    public async Task<CustomPack> SaveCustomPackAsync(CustomPack pack)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var packs = _state!.CustomPacks;
            var index = packs.FindIndex(item =>
                string.Equals(item.Id, pack.Id, StringComparison.Ordinal));
            var stored = new CustomPack
            {
                Id = pack.Id,
                Name = pack.Name,
                Kind = pack.Kind,
                Items = pack.Items
                    .Select(item => new CustomPackItem
                    {
                        Id = item.Id,
                        FileName = item.FileName,
                        ContentType = item.ContentType,
                        Size = item.Size
                    })
                    .ToList()
            };

            if (index >= 0)
            {
                packs[index] = stored;
            }
            else
            {
                packs.Add(stored);
            }

            await SaveAsync();
            return stored;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Удаляет пак вместе с файлами.</summary>
    public async Task<bool> DeleteCustomPackAsync(string packId)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var packs = _state!.CustomPacks;
            var pack = packs.FirstOrDefault(item =>
                string.Equals(item.Id, packId, StringComparison.Ordinal));
            if (pack is null)
            {
                return false;
            }

            foreach (var item in pack.Items)
            {
                await DeletePackFileAsync(item.Id);
            }

            packs.Remove(pack);
            await SaveAsync();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Сохраняет файл пака на устройстве (шифруется ключом устройства).</summary>
    public async Task SavePackFileAsync(string itemId, byte[] plaintext)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();

            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var ciphertext = new byte[plaintext.Length];
            var aad = PackAad(itemId);

            using (var aes = new AesGcm(_deviceKey, 16))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            }

            var result = new byte[MediaMagic.Length + 1 + nonce.Length + tag.Length + ciphertext.Length];
            var offset = 0;
            MediaMagic.CopyTo(result, offset);
            offset += MediaMagic.Length;
            result[offset++] = MediaVersion;
            nonce.CopyTo(result, offset);
            offset += nonce.Length;
            tag.CopyTo(result, offset);
            offset += tag.Length;
            ciphertext.CopyTo(result, offset);

            Directory.CreateDirectory(PackDirectory);
            var path = PackPath(itemId);
            var tempPath = path + ".tmp";
            await File.WriteAllBytesAsync(tempPath, result);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Читает файл пака (расшифровывает).</summary>
    public async Task<byte[]?> LoadPackFileAsync(string itemId)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
        }
        finally
        {
            _lock.Release();
        }

        var path = PackPath(itemId);
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = await File.ReadAllBytesAsync(path);
        return DecryptPackFile(itemId, protectedBytes);
    }

    /// <summary>Расшифровывает файл пака (AAD пака отличается от медиа).</summary>
    private byte[] DecryptPackFile(string itemId, byte[] protectedBytes)
    {
        if (protectedBytes.Length < MediaMagic.Length + 1 + 12 + 16 ||
            !protectedBytes.AsSpan(0, MediaMagic.Length).SequenceEqual(MediaMagic))
        {
            throw new CryptographicException("Повреждённый файл пака.");
        }

        var offset = MediaMagic.Length;
        var version = protectedBytes[offset++];
        if (version != MediaVersion)
        {
            throw new CryptographicException("Неподдерживаемая версия файла пака.");
        }

        var nonce = protectedBytes.AsSpan(offset, 12);
        offset += 12;
        var tag = protectedBytes.AsSpan(offset, 16);
        offset += 16;
        var ciphertext = protectedBytes.AsSpan(offset);

        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(_deviceKey, 16))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext, PackAad(itemId));
        }

        return plaintext;
    }

    /// <summary>Удаляет файл пака с диска.</summary>
    public async Task DeletePackFileAsync(string itemId)
    {
        try
        {
            var path = PackPath(itemId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Файл уже удалён или занят — не критично.
        }
    }

    private static byte[] PackAad(string itemId) =>
        Encoding.UTF8.GetBytes($"SecureChat.LocalPack.v1|{itemId}");

    /// <summary>Сколько места занимает кэш вложений: байты и число файлов.</summary>
    public async Task<(long Bytes, int Files)> GetMediaCacheInfoAsync()
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
        }
        finally
        {
            _lock.Release();
        }

        long bytes = 0;
        var files = 0;

        try
        {
            if (Directory.Exists(MediaDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(MediaDirectory, "*.bin"))
                {
                    var info = new FileInfo(file);
                    if (!info.Exists)
                    {
                        continue;
                    }

                    bytes += info.Length;
                    files++;
                }
            }
        }
        catch
        {
            // Не удалось посчитать кэш — покажем нули.
        }

        return (bytes, files);
    }

    /// <summary>
    /// Освобождает место: удаляет локальные копии вложений. Сообщения и их
    /// шифротексты остаются, файлы снова скачаются при открытии.
    /// </summary>
    public async Task<long> ClearMediaCacheAsync()
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();

            var removed = 0L;
            foreach (var conversation in _state!.Conversations)
            {
                foreach (var message in conversation.Messages)
                {
                    if (message.MediaDownloaded)
                    {
                        message.MediaDownloaded = false;
                    }
                }
            }

            await SaveAsync();

            try
            {
                if (Directory.Exists(MediaDirectory))
                {
                    foreach (var file in Directory.EnumerateFiles(MediaDirectory, "*.bin"))
                    {
                        try
                        {
                            var length = new FileInfo(file).Length;
                            File.Delete(file);
                            removed += length;
                        }
                        catch
                        {
                            // Файл занят — пропускаем.
                        }
                    }
                }
            }
            catch
            {
                // Часть файлов могла остаться.
            }

            return removed;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Удаляет все сообщения локально.</summary>
    public async Task DeleteAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _state = new LocalState();
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }

            if (Directory.Exists(MediaDirectory))
            {
                Directory.Delete(MediaDirectory, recursive: true);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Экспортирует всю локальную историю в зашифрованный файл.</summary>
    public async Task<byte[]> ExportHistoryAsync()
    {
        await _lock.WaitAsync();
        try
        {
            EnsureInitialized();
            var plain = Encoding.UTF8.GetBytes(ChatJson.Serialize(_state));
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var ciphertext = new byte[plain.Length];

            using (var aes = new AesGcm(_deviceKey, 16))
            {
                aes.Encrypt(nonce, plain, ciphertext, tag, Aad);
            }

            var result = new byte[Magic.Length + 1 + nonce.Length + tag.Length + ciphertext.Length];
            var offset = 0;
            Magic.CopyTo(result, offset);
            offset += Magic.Length;
            result[offset++] = Version;
            nonce.CopyTo(result, offset);
            offset += nonce.Length;
            tag.CopyTo(result, offset);
            offset += tag.Length;
            ciphertext.CopyTo(result, offset);

            CryptographicOperations.ZeroMemory(plain);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Импортирует историю из зашифрованного файла (заменяет текущую).</summary>
    public async Task ImportHistoryAsync(byte[] data)
    {
        await _lock.WaitAsync();
        try
        {
            if (data.Length < Magic.Length + 1 + 12 + 16 ||
                !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            {
                throw new CryptographicException("Повреждённый файл истории.");
            }

            var offset = Magic.Length;
            var version = data[offset++];
            if (version > Version)
            {
                throw new CryptographicException("Неподдерживаемая версия файла.");
            }

            var nonce = data.AsSpan(offset, 12);
            offset += 12;
            var tag = data.AsSpan(offset, 16);
            offset += 16;
            var ciphertext = data.AsSpan(offset);

            var plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(_deviceKey, 16))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, Aad);
            }

            var imported = ChatJson.Deserialize<LocalState>(Encoding.UTF8.GetString(plaintext));
            if (imported is null || imported.Conversations is null)
            {
                throw new CryptographicException("Невозможно прочитать историю.");
            }

            _state = imported;
            await SaveAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private static int DeliveryRank(string status) => status switch
    {
        "Отправка" => 0,
        "Отправлено" => 1,
        "Доставлено" => 2,
        "Прочитано" => 3,
        _ => -1
    };

    private StoredConversation? Find(string username) =>
        _state!.Conversations.FirstOrDefault(item =>
            string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase));

    private void EnsureInitialized()
    {
        if (_state is null)
        {
            throw new InvalidOperationException("LocalStore не инициализирован.");
        }
    }

    private async Task SaveAsync()
    {
        EnsureInitialized();
        var plain = Encoding.UTF8.GetBytes(ChatJson.Serialize(_state));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plain.Length];

        using (var aes = new AesGcm(_deviceKey, 16))
        {
            aes.Encrypt(nonce, plain, ciphertext, tag, Aad);
        }

        var result = new byte[Magic.Length + 1 + nonce.Length + tag.Length + ciphertext.Length];
        var offset = 0;
        Magic.CopyTo(result, offset);
        offset += Magic.Length;
        result[offset++] = Version;
        nonce.CopyTo(result, offset);
        offset += nonce.Length;
        tag.CopyTo(result, offset);
        offset += tag.Length;
        ciphertext.CopyTo(result, offset);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tempPath = FilePath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, result);
        File.Move(tempPath, FilePath, overwrite: true);

        CryptographicOperations.ZeroMemory(plain);
    }

    private async Task SaveMediaFileAsync(string messageId, byte[] plaintext)
    {
        if (!Guid.TryParse(messageId, out _))
        {
            throw new ArgumentException("Некорректный идентификатор сообщения.", nameof(messageId));
        }

        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        var aad = MediaAad(messageId);

        using (var aes = new AesGcm(_deviceKey, 16))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        }

        var result = new byte[MediaMagic.Length + 1 + nonce.Length + tag.Length + ciphertext.Length];
        var offset = 0;
        MediaMagic.CopyTo(result, offset);
        offset += MediaMagic.Length;
        result[offset++] = MediaVersion;
        nonce.CopyTo(result, offset);
        offset += nonce.Length;
        tag.CopyTo(result, offset);
        offset += tag.Length;
        ciphertext.CopyTo(result, offset);

        Directory.CreateDirectory(MediaDirectory);
        var path = MediaPath(messageId);
        var tempPath = path + ".tmp";
        await File.WriteAllBytesAsync(tempPath, result);
        File.Move(tempPath, path, overwrite: true);
    }

    private byte[] DecryptMedia(string messageId, byte[] protectedBytes)
    {
        if (protectedBytes.Length < MediaMagic.Length + 1 + 12 + 16 ||
            !protectedBytes.AsSpan(0, MediaMagic.Length).SequenceEqual(MediaMagic))
        {
            throw new CryptographicException("Повреждённый файл вложения.");
        }

        var offset = MediaMagic.Length;
        var version = protectedBytes[offset++];
        if (version != MediaVersion)
        {
            throw new CryptographicException("Неподдерживаемая версия файла вложения.");
        }

        var nonce = protectedBytes.AsSpan(offset, 12);
        offset += 12;
        var tag = protectedBytes.AsSpan(offset, 16);
        offset += 16;
        var ciphertext = protectedBytes.AsSpan(offset);

        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(_deviceKey, 16))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext, MediaAad(messageId));
        }

        return plaintext;
    }

    private static byte[] MediaAad(string messageId) =>
        Encoding.UTF8.GetBytes($"SecureChat.LocalMedia.v1|{messageId}");

    private LocalState Decrypt(byte[] protectedBytes)
    {
        if (protectedBytes.Length < Magic.Length + 1 + 12 + 16 ||
            !protectedBytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new CryptographicException("Повреждённый файл локальной истории.");
        }

        var offset = Magic.Length;
        var version = protectedBytes[offset++];
        if (version > Version)
        {
            throw new CryptographicException("Неподдерживаемая версия локальной истории.");
        }

        // Версия 1 не содержит SpaceWallpapers — словарь останется пустым (инициализирован в конструкторе LocalState).

        var nonce = protectedBytes.AsSpan(offset, 12);
        offset += 12;
        var tag = protectedBytes.AsSpan(offset, 16);
        offset += 16;
        var ciphertext = protectedBytes.AsSpan(offset);

        var plain = new byte[ciphertext.Length];
        using (var aes = new AesGcm(_deviceKey, 16))
        {
            aes.Decrypt(nonce, ciphertext, tag, plain, Aad);
        }

        var json = Encoding.UTF8.GetString(plain);
        CryptographicOperations.ZeroMemory(plain);

        return ChatJson.Deserialize<LocalState>(json);
    }

    private static StoredConversation Clone(StoredConversation source) => new()
    {
        UserId = source.UserId,
        Username = source.Username,
        EncryptionPublicKey = source.EncryptionPublicKey,
        SigningPublicKey = source.SigningPublicKey,
        Fingerprint = source.Fingerprint,
        OnlineStatus = source.OnlineStatus,
        OnlineStatusColor = source.OnlineStatusColor,
        AvatarBase64 = source.AvatarBase64,
        DraftText = source.DraftText,
        LastMessage = source.LastMessage,
        LastMessageAtUnixMs = source.LastMessageAtUnixMs,
        UnreadCount = source.UnreadCount,
        KeysChanged = source.KeysChanged,
        IsArchived = source.IsArchived,
        IsMuted = source.IsMuted,
        Messages = source.Messages.Select(CloneMessage).ToList()
    };

    private static StoredMessage CloneMessage(StoredMessage source) => new()
    {
        Id = source.Id,
        Text = source.Text,
        SentAtUnixMs = source.SentAtUnixMs,
        IsMine = source.IsMine,
        Status = source.Status,
        Kind = source.Kind,
        MediaId = source.MediaId,
        FileName = source.FileName,
        ContentType = source.ContentType,
        MediaSize = source.MediaSize,
        MediaKey = source.MediaKey,
        MediaNonce = source.MediaNonce,
        MediaTag = source.MediaTag,
        MediaDownloaded = source.MediaDownloaded,
        IsFavorite = source.IsFavorite,
        IsForwarded = source.IsForwarded,
        ForwardedFrom = source.ForwardedFrom,
        ReplyToMessageId = source.ReplyToMessageId,
        ReplyToText = source.ReplyToText,
        ReplyToFileName = source.ReplyToFileName,
        ReplyToSender = source.ReplyToSender,
        IsEdited = source.IsEdited,
        EditedText = source.EditedText,
        DeliveryStatus = source.DeliveryStatus,
        SenderUsername = source.SenderUsername,
        SenderAvatarBase64 = source.SenderAvatarBase64,
        IsPinned = source.IsPinned,
        ReactionsJson = source.ReactionsJson,
        IsDraft = source.IsDraft,
        DraftText = source.DraftText,
        AlbumId = source.AlbumId,
        AlbumIndex = source.AlbumIndex,
        AlbumCount = source.AlbumCount
    };
}
