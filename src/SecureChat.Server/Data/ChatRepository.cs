using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;

namespace SecureChat.Server.Data;

/// <summary>
/// Хранилище пользователей и зашифрованных конвертов. Сервер не хранит
/// открытый текст сообщений и не имеет ключей расшифровки.
/// </summary>
public sealed class ChatRepository
{
    private const int MaxPendingMessages = 500;
    private readonly string _connectionString;
    private readonly string _mediaDirectory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ILogger<ChatRepository> _logger;

    public ChatRepository(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<ChatRepository> logger)
    {
        _logger = logger;

        var raw = configuration.GetConnectionString("Chat")
            ?? "Data Source=App_Data/securechat.db";

        var builder = new SqliteConnectionStringBuilder(raw);
        if (!Path.IsPathRooted(builder.DataSource))
        {
            builder.DataSource = ServerDataPaths.ResolveDatabasePath(builder.DataSource, environment);
        }

        _connectionString = builder.ToString();
        _mediaDirectory = Path.Combine(
            Path.GetDirectoryName(builder.DataSource) ?? environment.ContentRootPath,
            "media");
    }

    public string GetMediaPath(string mediaId) =>
        Path.Combine(_mediaDirectory, mediaId + ".bin");

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var dataSource = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        Directory.CreateDirectory(Path.GetDirectoryName(dataSource)!);
        Directory.CreateDirectory(_mediaDirectory);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS Users (
                Id                 TEXT PRIMARY KEY,
                Username           TEXT NOT NULL COLLATE NOCASE UNIQUE,
                Email              TEXT,
                AuthHash           BLOB NOT NULL,
                AuthSalt           BLOB NOT NULL,
                EncryptionPublicKey TEXT NOT NULL,
                SigningPublicKey   TEXT NOT NULL,
                CreatedAt          TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Messages (
                Id          TEXT PRIMARY KEY,
                SenderId    TEXT NOT NULL,
                RecipientId TEXT NOT NULL,
                Envelope    TEXT NOT NULL,
                CreatedAt   TEXT NOT NULL,
                Delivered   INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (SenderId) REFERENCES Users(Id),
                FOREIGN KEY (RecipientId) REFERENCES Users(Id)
            );

            CREATE INDEX IF NOT EXISTS IX_Messages_Pending
                ON Messages (RecipientId, Delivered, CreatedAt);

            CREATE TABLE IF NOT EXISTS Media (
                Id          TEXT PRIMARY KEY,
                SenderId    TEXT NOT NULL,
                RecipientId TEXT NOT NULL,
                Size        INTEGER NOT NULL,
                CreatedAt   TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Media_Recipient
                ON Media (RecipientId, CreatedAt);

            CREATE TABLE IF NOT EXISTS BannedUsers (
                UserId      TEXT PRIMARY KEY,
                BannedAt    TEXT NOT NULL,
                Reason      TEXT NOT NULL DEFAULT '',
                BannedBy    TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE
            );

            -- Миграция: добавить колонку Email в существующие базы
            ALTER TABLE Users ADD COLUMN Email TEXT;
            """;

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // Колонка Email уже существует — игнорируем.
        }

        await EnsureProfileColumnsAsync(connection, cancellationToken);
        await EnsureMessageExpiryColumnsAsync(connection, cancellationToken);
        await EnsureMediaTableMigrationAsync(connection, cancellationToken);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");
        await using var cleanup = connection.CreateCommand();
        cleanup.CommandText =
            "DELETE FROM Messages WHERE Delivered = 1 AND CreatedAt < $cutoff;";
        cleanup.Parameters.AddWithValue("$cutoff", cutoff);
        await cleanup.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation("База данных готова: {DataSource}", dataSource);
    }

    private async Task EnsureProfileColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(Users);";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existing.Add(reader.GetString(1));
            }
        }

        var columns = new (string Name, string Definition)[]
        {
            ("Nickname", "TEXT NOT NULL DEFAULT ''"),
            ("Bio", "TEXT NOT NULL DEFAULT ''"),
            ("AvatarBase64", "TEXT NOT NULL DEFAULT ''"),
            ("StatusMessage", "TEXT NOT NULL DEFAULT 'Онлайн'"),
            ("LastSeenUnixMs", "INTEGER NOT NULL DEFAULT 0"),
            ("IsAdmin", "INTEGER NOT NULL DEFAULT 0")
        };

        foreach (var column in columns)
        {
            if (existing.Contains(column.Name))
            {
                continue;
            }

            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE Users ADD COLUMN {column.Name} {column.Definition};";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>Добавляет колонку срока жизни в таблицу Messages для уже созданных баз.</summary>
    private async Task EnsureMessageExpiryColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(Messages);";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existing.Add(reader.GetString(1));
            }
        }

        if (!existing.Contains("ExpireAtUnixMs"))
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE Messages ADD COLUMN ExpireAtUnixMs INTEGER NOT NULL DEFAULT 0;";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>Пересоздаёт таблицу Media без внешних ключей, если они там есть.</summary>
    private async Task EnsureMediaTableMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        string? sql;
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='Media';";
            await using var reader = await check.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return; // таблицы нет — нечего мигрировать
            }
            sql = reader.GetString(0);
        }

        if (sql is null || !sql.Contains("FOREIGN KEY"))
        {
            return; // уже без внешних ключей
        }

        // Пересоздаём таблицу без внешних ключей, сохраняя данные.
        // Читатель sqlite_master закрыт перед началом транзакции и DDL,
        // иначе падаем с "database table is locked".
        await using var tx = connection.BeginTransaction();
        try
        {
            await using var cmd1 = connection.CreateCommand();
            cmd1.CommandText = """
                CREATE TABLE Media_new (
                    Id          TEXT PRIMARY KEY,
                    SenderId    TEXT NOT NULL,
                    RecipientId TEXT NOT NULL,
                    Size        INTEGER NOT NULL,
                    CreatedAt   TEXT NOT NULL
                );
                """;
            await cmd1.ExecuteNonQueryAsync(cancellationToken);

            await using var cmd2 = connection.CreateCommand();
            cmd2.CommandText = """
                INSERT INTO Media_new (Id, SenderId, RecipientId, Size, CreatedAt)
                SELECT Id, SenderId, RecipientId, Size, CreatedAt FROM Media;
                """;
            await cmd2.ExecuteNonQueryAsync(cancellationToken);

            await using var cmd3 = connection.CreateCommand();
            cmd3.CommandText = "DROP TABLE Media;";
            await cmd3.ExecuteNonQueryAsync(cancellationToken);

            await using var cmd4 = connection.CreateCommand();
            cmd4.CommandText = "ALTER TABLE Media_new RENAME TO Media;";
            await cmd4.ExecuteNonQueryAsync(cancellationToken);

            await using var cmd5 = connection.CreateCommand();
            cmd5.CommandText = "CREATE INDEX IF NOT EXISTS IX_Media_Recipient ON Media (RecipientId, CreatedAt);";
            await cmd5.ExecuteNonQueryAsync(cancellationToken);

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<UserRecord?> GetUserByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Username, Email, AuthHash, AuthSalt,
                   EncryptionPublicKey, SigningPublicKey, CreatedAt,
                   Nickname, Bio, AvatarBase64, StatusMessage, LastSeenUnixMs,
                   IsAdmin
            FROM Users
            WHERE Username = $username COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$username", username);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapUser(reader) : null;
    }

    public async Task<UserRecord?> GetUserByEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Username, Email, AuthHash, AuthSalt,
                   EncryptionPublicKey, SigningPublicKey, CreatedAt,
                   Nickname, Bio, AvatarBase64, StatusMessage, LastSeenUnixMs,
                   IsAdmin
            FROM Users
            WHERE Email = $email COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$email", email);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapUser(reader) : null;
    }

    public async Task<UserRecord?> GetUserByIdAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Username, Email, AuthHash, AuthSalt,
                   EncryptionPublicKey, SigningPublicKey, CreatedAt,
                   Nickname, Bio, AvatarBase64, StatusMessage, LastSeenUnixMs,
                   IsAdmin
            FROM Users
            WHERE Id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapUser(reader) : null;
    }

    /// <summary>Забанить пользователя на сервере.</summary>
    public async Task<bool> BanUserAsync(
        string userId,
        string bannedBy,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO BannedUsers (UserId, BannedAt, Reason, BannedBy)
                VALUES ($userId, $bannedAt, $reason, $bannedBy)
                ON CONFLICT(UserId) DO UPDATE SET
                    BannedAt = $bannedAt,
                    Reason = $reason,
                    BannedBy = $bannedBy;
                """;
            command.Parameters.AddWithValue("$userId", userId);
            command.Parameters.AddWithValue("$bannedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$bannedBy", bannedBy);

            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Разбанить пользователя.</summary>
    public async Task<bool> UnbanUserAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM BannedUsers WHERE UserId = $userId;";
            command.Parameters.AddWithValue("$userId", userId);

            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Забанен ли пользователь на сервере.</summary>
    public async Task<bool> IsUserBannedAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM BannedUsers WHERE UserId = $userId LIMIT 1;";
        command.Parameters.AddWithValue("$userId", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }

    /// <summary>Заменяет хеш и соль auth-secret после смены пароля.</summary>
    public async Task<bool> UpdateUserPasswordAsync(
        string userId,
        byte[] authHash,
        byte[] authSalt,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Users
                SET AuthHash = $hash, AuthSalt = $salt
                WHERE Id = $userId;
                """;
            command.Parameters.AddWithValue("$hash", authHash);
            command.Parameters.AddWithValue("$salt", authSalt);
            command.Parameters.AddWithValue("$userId", userId);

            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Полностью удаляет аккаунт: переписку, вложения, членство в пространствах
    /// и созданные пользователем пространства. Файлы вложений удаляются
    /// после фиксации транзакции.
    /// </summary>
    public async Task<bool> DeleteUserAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var mediaIds = new List<string>();

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction();

            await using (var collect = connection.CreateCommand())
            {
                collect.Transaction = transaction;
                collect.CommandText =
                    "SELECT Id FROM Media WHERE SenderId = $userId OR RecipientId = $userId;";
                collect.Parameters.AddWithValue("$userId", userId);

                await using var reader = await collect.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    mediaIds.Add(reader.GetString(0));
                }
            }

            // Пространства, которыми владел пользователь, удаляем целиком.
            await ExecuteForUserAsync(connection, transaction, userId, cancellationToken, """
                DELETE FROM SpaceMessageReactions
                WHERE MessageId IN (
                    SELECT Id FROM SpaceMessages
                    WHERE SpaceId IN (SELECT Id FROM Spaces WHERE OwnerId = $userId));
                DELETE FROM SpaceMessages
                WHERE SpaceId IN (SELECT Id FROM Spaces WHERE OwnerId = $userId);
                DELETE FROM Topics
                WHERE SpaceId IN (SELECT Id FROM Spaces WHERE OwnerId = $userId);
                DELETE FROM SpaceKeys
                WHERE SpaceId IN (SELECT Id FROM Spaces WHERE OwnerId = $userId);
                DELETE FROM SpaceMembers
                WHERE SpaceId IN (SELECT Id FROM Spaces WHERE OwnerId = $userId);
                DELETE FROM Spaces WHERE OwnerId = $userId;
                """);

            // Участие в чужих пространствах и собственные сообщения/реакции.
            await ExecuteForUserAsync(connection, transaction, userId, cancellationToken, """
                DELETE FROM SpaceMessageReactions
                WHERE UserId = $userId
                   OR MessageId IN (SELECT Id FROM SpaceMessages WHERE SenderId = $userId);
                DELETE FROM SpaceMessages WHERE SenderId = $userId;
                DELETE FROM SpaceKeys WHERE UserId = $userId;
                DELETE FROM SpaceMembers WHERE UserId = $userId;
                """);

            // Личные чаты, вложения и бан.
            await ExecuteForUserAsync(connection, transaction, userId, cancellationToken, """
                DELETE FROM Messages WHERE SenderId = $userId OR RecipientId = $userId;
                DELETE FROM Media WHERE SenderId = $userId OR RecipientId = $userId;
                DELETE FROM BannedUsers WHERE UserId = $userId;
                """);

            bool deleted;
            await using (var deleteUser = connection.CreateCommand())
            {
                deleteUser.Transaction = transaction;
                deleteUser.CommandText = "DELETE FROM Users WHERE Id = $userId;";
                deleteUser.Parameters.AddWithValue("$userId", userId);
                deleted = await deleteUser.ExecuteNonQueryAsync(cancellationToken) > 0;
            }

            await transaction.CommitAsync(cancellationToken);

            foreach (var mediaId in mediaIds)
            {
                try
                {
                    File.Delete(GetMediaPath(mediaId));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Не удалось удалить вложение {MediaId}", mediaId);
                }
            }

            return deleted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task ExecuteForUserAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string userId,
        CancellationToken cancellationToken,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$userId", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Обновляет публичный профиль пользователя.</summary>
    public async Task UpdateProfileAsync(
        string userId,
        string nickname,
        string bio,
        string avatarBase64,
        string statusMessage,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Users
                SET Nickname = $nickname,
                    Bio = $bio,
                    AvatarBase64 = $avatar,
                    StatusMessage = $status
                WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$nickname", nickname);
            command.Parameters.AddWithValue("$bio", bio);
            command.Parameters.AddWithValue("$avatar", avatarBase64);
            command.Parameters.AddWithValue("$status", statusMessage);
            command.Parameters.AddWithValue("$id", userId);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Фиксирует время последнего выхода в сеть.</summary>
    public async Task TouchLastSeenAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Users
                SET LastSeenUnixMs = $now
                WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$id", userId);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> CreateUserAsync(
        UserRecord user,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Users
                    (Id, Username, Email, AuthHash, AuthSalt,
                     EncryptionPublicKey, SigningPublicKey, CreatedAt)
                VALUES
                    ($id, $username, $email, $hash, $salt,
                     $encryptionKey, $signingKey, $createdAt);
                """;
            command.Parameters.AddWithValue("$id", user.Id);
            command.Parameters.AddWithValue("$username", user.Username);
            command.Parameters.AddWithValue("$email", (object?)user.Email ?? DBNull.Value);
            command.Parameters.AddWithValue("$hash", user.AuthHash);
            command.Parameters.AddWithValue("$salt", user.AuthSalt);
            command.Parameters.AddWithValue("$encryptionKey", user.EncryptionPublicKey);
            command.Parameters.AddWithValue("$signingKey", user.SigningPublicKey);
            command.Parameters.AddWithValue("$createdAt", user.CreatedAt.ToString("O"));

            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // UNIQUE constraint: имя уже занято.
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> SaveMessageAsync(
        string id,
        string senderId,
        string recipientId,
        string envelopeJson,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Messages
                    (Id, SenderId, RecipientId, Envelope, CreatedAt, Delivered)
                VALUES
                    ($id, $senderId, $recipientId, $envelope, $createdAt, 0)
                ON CONFLICT(Id) DO UPDATE SET
                    Envelope = excluded.Envelope
                WHERE (Messages.SenderId = excluded.SenderId
                       AND Messages.RecipientId = excluded.RecipientId)
                   OR (Messages.SenderId = excluded.RecipientId
                       AND Messages.RecipientId = excluded.SenderId);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$senderId", senderId);
            command.Parameters.AddWithValue("$recipientId", recipientId);
            command.Parameters.AddWithValue("$envelope", envelopeJson);
            command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));

            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> CreateMediaAsync(
        MediaRecord media,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Media
                    (Id, SenderId, RecipientId, Size, CreatedAt)
                VALUES
                    ($id, $senderId, $recipientId, $size, $createdAt)
                ON CONFLICT(Id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$id", media.Id);
            command.Parameters.AddWithValue("$senderId", media.SenderId);
            command.Parameters.AddWithValue("$recipientId", media.RecipientId);
            command.Parameters.AddWithValue("$size", media.Size);
            command.Parameters.AddWithValue("$createdAt", media.CreatedAt.ToString("O"));

            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<MediaRecord?> GetMediaAsync(
        string mediaId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, SenderId, RecipientId, Size, CreatedAt
            FROM Media
            WHERE Id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", mediaId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MediaRecord
        {
            Id = reader.GetString(0),
            SenderId = reader.GetString(1),
            RecipientId = reader.GetString(2),
            Size = reader.GetInt64(3),
            CreatedAt = DateTimeOffset.Parse(
                reader.GetString(4),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind)
        };
    }

    public async Task<IReadOnlyList<PendingEnvelope>> GetPendingAsync(
        string recipientId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<PendingEnvelope>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Envelope
            FROM Messages
            WHERE RecipientId = $recipientId AND Delivered = 0
            ORDER BY CreatedAt
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$recipientId", recipientId);
        command.Parameters.AddWithValue("$limit", MaxPendingMessages);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PendingEnvelope(reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    public async Task<IReadOnlyList<DeliveredMessage>> GetRecipientMessagesAsync(
        string recipientId,
        IReadOnlyCollection<string> messageIds,
        CancellationToken cancellationToken = default)
    {
        if (messageIds.Count == 0)
        {
            return Array.Empty<DeliveredMessage>();
        }

        var ids = messageIds.Take(200).ToArray();
        var parameterNames = ids.Select((_, index) => $"$id{index}").ToArray();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, SenderId
            FROM Messages
            WHERE RecipientId = $recipientId
              AND Id IN ({string.Join(", ", parameterNames)});
            """;
        command.Parameters.AddWithValue("$recipientId", recipientId);
        for (var i = 0; i < ids.Length; i++)
        {
            command.Parameters.AddWithValue(parameterNames[i], ids[i]);
        }

        var messages = new List<DeliveredMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new DeliveredMessage(reader.GetString(0), reader.GetString(1)));
        }

        return messages;
    }

    public async Task<IReadOnlyList<DeliveredMessage>> MarkDeliveredAsync(
        string recipientId,
        IReadOnlyCollection<string> messageIds,
        CancellationToken cancellationToken = default)
    {
        if (messageIds.Count == 0)
        {
            return Array.Empty<DeliveredMessage>();
        }

        var ids = messageIds.Take(200).ToArray();
        var parameterNames = ids.Select((_, index) => $"$id{index}").ToArray();

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                UPDATE Messages
                SET Delivered = 1
                WHERE RecipientId = $recipientId
                  AND Delivered = 0
                  AND Id IN ({string.Join(", ", parameterNames)})
                RETURNING Id, SenderId;
                """;
            command.Parameters.AddWithValue("$recipientId", recipientId);
            for (var i = 0; i < ids.Length; i++)
            {
                command.Parameters.AddWithValue(parameterNames[i], ids[i]);
            }

            var delivered = new List<DeliveredMessage>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                delivered.Add(new DeliveredMessage(reader.GetString(0), reader.GetString(1)));
            }

            return delivered;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static UserRecord MapUser(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Username = reader.GetString(1),
        Email = reader.IsDBNull(2) ? null : reader.GetString(2),
        AuthHash = reader.GetFieldValue<byte[]>(3),
        AuthSalt = reader.GetFieldValue<byte[]>(4),
        EncryptionPublicKey = reader.GetString(5),
        SigningPublicKey = reader.GetString(6),
        CreatedAt = DateTimeOffset.Parse(
            reader.GetString(7),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind),
        Nickname = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
        Bio = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
        AvatarBase64 = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
        StatusMessage = reader.IsDBNull(11) ? "Онлайн" : reader.GetString(11),
        LastSeenUnixMs = reader.IsDBNull(12) ? 0 : reader.GetInt64(12),
        IsAdmin = reader.IsDBNull(13) ? false : reader.GetInt64(13) == 1
    };
}