using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using SecureChat.Core.Contracts;

namespace SecureChat.Server.Data;

/// <summary>
/// Пространства: группы, каналы, сообщества и форумы.
/// Форум состоит из тем (тредов), канал — из сообщений, доступных всем участникам.
/// </summary>
public sealed class SpaceRepository
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ILogger<SpaceRepository> _logger;

    public SpaceRepository(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<SpaceRepository> logger)
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
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Spaces (
                Id          TEXT PRIMARY KEY,
                Name        TEXT NOT NULL,
                Type        TEXT NOT NULL,
                Description TEXT NOT NULL DEFAULT '',
                IsPublic    INTEGER NOT NULL DEFAULT 0,
                OwnerId     TEXT NOT NULL,
                CreatedAt   TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS SpaceMembers (
                SpaceId          TEXT NOT NULL,
                UserId           TEXT NOT NULL,
                Role             TEXT NOT NULL,
                CanPost          INTEGER NOT NULL DEFAULT 1,
                MutedUntilUnixMs INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (SpaceId, UserId),
                FOREIGN KEY (SpaceId) REFERENCES Spaces(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS Topics (
                Id          TEXT PRIMARY KEY,
                SpaceId     TEXT NOT NULL,
                Title       TEXT NOT NULL,
                CreatedBy   TEXT NOT NULL,
                CreatedAt   TEXT NOT NULL,
                FOREIGN KEY (SpaceId) REFERENCES Spaces(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS SpaceMessages (
                Id         TEXT PRIMARY KEY,
                SpaceId    TEXT NOT NULL,
                TopicId    TEXT,
                SenderId   TEXT NOT NULL,
                SenderUsername TEXT NOT NULL,
                Text       TEXT NOT NULL DEFAULT '',
                EditedText TEXT,
                SentAtUnixMs INTEGER NOT NULL,
                Nonce      TEXT NOT NULL DEFAULT '',
                Tag        TEXT NOT NULL DEFAULT '',
                Ciphertext TEXT NOT NULL DEFAULT '',
                Signature  TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS SpaceKeys (
                SpaceId    TEXT NOT NULL,
                UserId     TEXT NOT NULL,
                Envelope   TEXT NOT NULL,
                PRIMARY KEY (SpaceId, UserId)
            );

            CREATE TABLE IF NOT EXISTS SpaceMessageReactions (
                MessageId  TEXT NOT NULL,
                UserId     TEXT NOT NULL,
                Emoji      TEXT NOT NULL,
                PRIMARY KEY (MessageId, UserId, Emoji)
            );

            CREATE INDEX IF NOT EXISTS IX_SpaceMessages_Space
                ON SpaceMessages (SpaceId, SentAtUnixMs);

            CREATE INDEX IF NOT EXISTS IX_Topics_Space
                ON Topics (SpaceId, CreatedAt);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureMessageColumnsAsync(connection, cancellationToken);
        await EnsureMemberColumnsAsync(connection, cancellationToken);
        _logger.LogInformation("Хранилище пространств готово");
    }

    /// <summary>Добавляет колонки шифротекста в уже созданную базу.</summary>
    private static async Task EnsureMessageColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(SpaceMessages);";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existing.Add(reader.GetString(1));
            }
        }

        var columns = new (string Name, string Definition)[]
        {
            ("Nonce", "TEXT NOT NULL DEFAULT ''"),
            ("Tag", "TEXT NOT NULL DEFAULT ''"),
            ("Ciphertext", "TEXT NOT NULL DEFAULT ''"),
            ("Signature", "TEXT NOT NULL DEFAULT ''"),
            ("EditedAtUnixMs", "INTEGER NOT NULL DEFAULT 0"),
            ("IsPinned", "INTEGER NOT NULL DEFAULT 0")
        };

        foreach (var column in columns)
        {
            if (existing.Contains(column.Name))
            {
                continue;
            }

            await using var alter = connection.CreateCommand();
            alter.CommandText =
                $"ALTER TABLE SpaceMessages ADD COLUMN {column.Name} {column.Definition};";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>Добавляет колонку мута в уже созданную таблицу SpaceMembers.</summary>
    private static async Task EnsureMemberColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var info = connection.CreateCommand();
        info.CommandText = "PRAGMA table_info(SpaceMembers);";
        await using var reader = await info.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            existing.Add(reader.GetString(1));
        }

        if (!existing.Contains("MutedUntilUnixMs"))
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE SpaceMembers ADD COLUMN MutedUntilUnixMs INTEGER NOT NULL DEFAULT 0;";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>Сохраняет «конверт» общего ключа пространства для участника.</summary>
    public async Task SaveSpaceKeyAsync(
        string spaceId,
        string userId,
        string envelope,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO SpaceKeys (SpaceId, UserId, Envelope)
                VALUES ($spaceId, $userId, $envelope)
                ON CONFLICT(SpaceId, UserId) DO UPDATE SET Envelope = excluded.Envelope;
                """;
            command.Parameters.AddWithValue("$spaceId", spaceId);
            command.Parameters.AddWithValue("$userId", userId);
            command.Parameters.AddWithValue("$envelope", envelope);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Возвращает «конверт» ключа пространства для пользователя (или null).</summary>
    public async Task<string?> GetSpaceKeyAsync(
        string spaceId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Envelope FROM SpaceKeys WHERE SpaceId = $spaceId AND UserId = $userId;
            """;
        command.Parameters.AddWithValue("$spaceId", spaceId);
        command.Parameters.AddWithValue("$userId", userId);

        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>Возвращает «конверты» ключа пространства для всех участников.</summary>
    public async Task<Dictionary<string, string>> GetSpaceKeysAsync(
        string spaceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT UserId, Envelope FROM SpaceKeys WHERE SpaceId = $spaceId;";
        command.Parameters.AddWithValue("$spaceId", spaceId);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    public async Task<SpaceDto> CreateAsync(
        string ownerId,
        string ownerUsername,
        CreateSpaceRequest request,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO Spaces (Id, Name, Type, Description, IsPublic, OwnerId, CreatedAt)
                    VALUES ($id, $name, $type, $description, $isPublic, $ownerId, $createdAt);
                    """;
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$name", request.Name.Trim());
                insert.Parameters.AddWithValue("$type", request.Type);
                insert.Parameters.AddWithValue("$description", request.Description?.Trim() ?? string.Empty);
                insert.Parameters.AddWithValue("$isPublic", request.IsPublic ? 1 : 0);
                insert.Parameters.AddWithValue("$ownerId", ownerId);
                insert.Parameters.AddWithValue("$createdAt", now.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var member = connection.CreateCommand())
            {
                member.CommandText = """
                    INSERT INTO SpaceMembers (SpaceId, UserId, Role, CanPost)
                    VALUES ($spaceId, $userId, $role, 1)
                    ON CONFLICT(SpaceId, UserId) DO NOTHING;
                    """;
                member.Parameters.AddWithValue("$spaceId", id);
                member.Parameters.AddWithValue("$userId", ownerId);
                member.Parameters.AddWithValue("$role", SpaceRoles.Owner);
                await member.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _writeLock.Release();
        }

        return await GetAsync(id, ownerId, cancellationToken) ?? throw new InvalidOperationException("Пространство не создано.");
    }

    public async Task<SpaceDto?> GetAsync(
        string spaceId,
        string viewerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.Id, s.Name, s.Type, s.Description, s.IsPublic, s.OwnerId,
                   COALESCE(m.Role, ''), COALESCE(m.CanPost, 0),
                   (SELECT COUNT(*) FROM SpaceMembers sm WHERE sm.SpaceId = s.Id),
                   (SELECT COUNT(*) FROM Topics t WHERE t.SpaceId = s.Id)
            FROM Spaces s
            LEFT JOIN SpaceMembers m ON m.SpaceId = s.Id AND m.UserId = $viewerId
            WHERE s.Id = $spaceId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$spaceId", spaceId);
        command.Parameters.AddWithValue("$viewerId", viewerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var ownerId = reader.GetString(5);
        var ownerUsername = ownerId;

        return new SpaceDto
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            Type = reader.GetString(2),
            Description = reader.GetString(3),
            IsPublic = reader.GetInt64(4) == 1,
            OwnerId = ownerId,
            OwnerUsername = ownerUsername,
            MyRole = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            CanPost = !reader.IsDBNull(7) && reader.GetInt64(7) == 1,
            MemberCount = reader.GetInt32(8),
            TopicCount = reader.GetInt32(9)
        };
    }

    public async Task<List<SpaceDto>> ListForUserAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.Id, s.Name, s.Type, s.Description, s.IsPublic, s.OwnerId,
                   COALESCE(m.Role, ''), COALESCE(m.CanPost, 0),
                   (SELECT COUNT(*) FROM SpaceMembers sm WHERE sm.SpaceId = s.Id),
                   (SELECT COUNT(*) FROM Topics t WHERE t.SpaceId = s.Id),
                   (SELECT COALESCE(MAX(SentAtUnixMs), 0) FROM SpaceMessages sm WHERE sm.SpaceId = s.Id),
                   (SELECT COALESCE(Text, '') FROM SpaceMessages sm
                       WHERE sm.SpaceId = s.Id ORDER BY SentAtUnixMs DESC LIMIT 1)
            FROM Spaces s
            LEFT JOIN SpaceMembers m ON m.SpaceId = s.Id AND m.UserId = $userId
            WHERE m.UserId IS NOT NULL OR s.IsPublic = 1
            ORDER BY 11 DESC, s.Name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$userId", userId);

        var result = new List<SpaceDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SpaceDto
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Type = reader.GetString(2),
                Description = reader.GetString(3),
                IsPublic = reader.GetInt64(4) == 1,
                OwnerId = reader.GetString(5),
                OwnerUsername = reader.GetString(5),
                MyRole = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                CanPost = !reader.IsDBNull(7) && reader.GetInt64(7) == 1,
                MemberCount = reader.GetInt32(8),
                TopicCount = reader.GetInt32(9),
                LastMessageAtUnixMs = reader.GetInt64(10),
                LastMessageText = reader.IsDBNull(11) ? string.Empty : reader.GetString(11)
            });
        }

        return result;
    }

    public async Task<List<SpaceMemberDto>> GetMembersAsync(
        string spaceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT u.Id, u.Username, m.Role, m.CanPost
            FROM SpaceMembers m
            JOIN Users u ON u.Id = m.UserId
            WHERE m.SpaceId = $spaceId
            ORDER BY u.Username COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$spaceId", spaceId);

        var result = new List<SpaceMemberDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SpaceMemberDto
            {
                UserId = reader.GetString(0),
                Username = reader.GetString(1),
                Role = reader.GetString(2),
                CanPost = reader.GetInt64(3) == 1,
                OnlineStatus = "Онлайн"
            });
        }

        return result;
    }

    public async Task<bool> AddMemberAsync(
        string spaceId,
        string userId,
        string role,
        bool canPost,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO SpaceMembers (SpaceId, UserId, Role, CanPost)
                VALUES ($spaceId, $userId, $role, $canPost)
                ON CONFLICT(SpaceId, UserId) DO UPDATE SET
                    Role = excluded.Role,
                    CanPost = excluded.CanPost;
                """;
            command.Parameters.AddWithValue("$spaceId", spaceId);
            command.Parameters.AddWithValue("$userId", userId);
            command.Parameters.AddWithValue("$role", role);
            command.Parameters.AddWithValue("$canPost", canPost ? 1 : 0);

            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Замутить участника пространства на указанное время (Unix мс). 0 — снять мут.</summary>
    public async Task<bool> MuteSpaceMemberAsync(
        string spaceId,
        string userId,
        long mutedUntilUnixMs,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE SpaceMembers
                SET MutedUntilUnixMs = $mutedUntil
                WHERE SpaceId = $spaceId AND UserId = $userId;
                """;
            command.Parameters.AddWithValue("$spaceId", spaceId);
            command.Parameters.AddWithValue("$userId", userId);
            command.Parameters.AddWithValue("$mutedUntil", mutedUntilUnixMs);

            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Снять мут с участника пространства.</summary>
    public async Task<bool> UnmuteSpaceMemberAsync(
        string spaceId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        return await MuteSpaceMemberAsync(spaceId, userId, 0, cancellationToken);
    }

    /// <summary>Замучен ли участник пространства прямо сейчас.</summary>
    public async Task<bool> IsMemberMutedAsync(
        string spaceId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MutedUntilUnixMs FROM SpaceMembers
            WHERE SpaceId = $spaceId AND UserId = $userId;
            """;
        command.Parameters.AddWithValue("$spaceId", spaceId);
        command.Parameters.AddWithValue("$userId", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return false;
        }

        return reader.GetInt64(0) > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public async Task<bool> UpdateAsync(
        string spaceId,
        string name,
        string description,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Spaces
                SET Name = $name,
                    Description = $description
                WHERE Id = $spaceId;
                """;
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$description", description);
            command.Parameters.AddWithValue("$spaceId", spaceId);

            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> UpdateMemberAsync(
        string spaceId,
        string userId,
        string role,
        bool canPost,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE SpaceMembers
                SET Role = $role,
                    CanPost = $canPost
                WHERE SpaceId = $spaceId AND UserId = $userId;
                """;
            command.Parameters.AddWithValue("$role", role);
            command.Parameters.AddWithValue("$canPost", canPost ? 1 : 0);
            command.Parameters.AddWithValue("$spaceId", spaceId);
            command.Parameters.AddWithValue("$userId", userId);

            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> RemoveMemberAsync(
        string spaceId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM SpaceMembers WHERE SpaceId = $spaceId AND UserId = $userId;";
            command.Parameters.AddWithValue("$spaceId", spaceId);
            command.Parameters.AddWithValue("$userId", userId);

            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(
        string spaceId,
        string requesterId,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using (var check = connection.CreateCommand())
            {
                check.CommandText = """
                    SELECT OwnerId FROM Spaces WHERE Id = $spaceId AND OwnerId = $requesterId;
                    """;
                check.Parameters.AddWithValue("$spaceId", spaceId);
                check.Parameters.AddWithValue("$requesterId", requesterId);

                var owner = await check.ExecuteScalarAsync(cancellationToken) as string;
                if (owner is null)
                {
                    return false;
                }
            }

            await using var delete = connection.CreateCommand();
            delete.CommandText = """
                DELETE FROM SpaceMessages WHERE SpaceId = $spaceId;
                DELETE FROM Topics WHERE SpaceId = $spaceId;
                DELETE FROM SpaceMembers WHERE SpaceId = $spaceId;
                DELETE FROM Spaces WHERE Id = $spaceId;
                """;
            delete.Parameters.AddWithValue("$spaceId", spaceId);

            await delete.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<string> SaveMessageAsync(
        string spaceId,
        string? topicId,
        string senderId,
        string senderUsername,
        string text,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO SpaceMessages
                    (Id, SpaceId, TopicId, SenderId, SenderUsername, Text, SentAtUnixMs)
                VALUES
                    ($id, $spaceId, $topicId, $senderId, $senderUsername, $text, $sentAt);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$spaceId", spaceId);
            command.Parameters.AddWithValue("$topicId", (object?)topicId ?? DBNull.Value);
            command.Parameters.AddWithValue("$senderId", senderId);
            command.Parameters.AddWithValue("$senderUsername", senderUsername);
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$sentAt", now);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }

        return id;
    }

    /// <summary>Сохраняет зашифрованное сообщение пространства.</summary>
    public async Task SaveEncryptedMessageAsync(
        SpaceMessageDto message,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO SpaceMessages
                    (Id, SpaceId, TopicId, SenderId, SenderUsername, Text,
                     SentAtUnixMs, Nonce, Tag, Ciphertext, Signature)
                VALUES
                    ($id, $spaceId, $topicId, $senderId, $senderUsername, '',
                     $sentAt, $nonce, $tag, $ciphertext, $signature);
                """;
            command.Parameters.AddWithValue("$id", message.Id);
            command.Parameters.AddWithValue("$spaceId", message.SpaceId);
            command.Parameters.AddWithValue("$topicId", (object?)message.TopicId ?? DBNull.Value);
            command.Parameters.AddWithValue("$senderId", message.SenderId);
            command.Parameters.AddWithValue("$senderUsername", message.SenderUsername);
            command.Parameters.AddWithValue("$sentAt", message.SentAtUnixMs);
            command.Parameters.AddWithValue("$nonce", message.Nonce);
            command.Parameters.AddWithValue("$tag", message.Tag);
            command.Parameters.AddWithValue("$ciphertext", message.Ciphertext);
            command.Parameters.AddWithValue("$signature", message.Signature);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Возвращает зашифрованные сообщения пространства.</summary>
    public async Task<List<SpaceMessageDto>> ListEncryptedMessagesAsync(
        string spaceId,
        string? topicId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = topicId is null
            ? """
              SELECT Id, SpaceId, TopicId, SenderId, SenderUsername,
                     Nonce, Tag, Ciphertext, Signature, SentAtUnixMs,
                     EditedAtUnixMs, IsPinned
              FROM SpaceMessages
              WHERE SpaceId = $spaceId AND Ciphertext <> ''
              ORDER BY SentAtUnixMs DESC
              LIMIT $limit;
              """
            : """
              SELECT Id, SpaceId, TopicId, SenderId, SenderUsername,
                     Nonce, Tag, Ciphertext, Signature, SentAtUnixMs,
                     EditedAtUnixMs, IsPinned
              FROM SpaceMessages
              WHERE SpaceId = $spaceId AND TopicId = $topicId AND Ciphertext <> ''
              ORDER BY SentAtUnixMs DESC
              LIMIT $limit;
              """;
        command.Parameters.AddWithValue("$spaceId", spaceId);
        command.Parameters.AddWithValue("$limit", limit);
        if (topicId is not null)
        {
            command.Parameters.AddWithValue("$topicId", topicId);
        }

        var result = new List<SpaceMessageDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SpaceMessageDto
            {
                Id = reader.GetString(0),
                SpaceId = reader.GetString(1),
                TopicId = reader.IsDBNull(2) ? null : reader.GetString(2),
                SenderId = reader.GetString(3),
                SenderUsername = reader.GetString(4),
                Nonce = reader.GetString(5),
                Tag = reader.GetString(6),
                Ciphertext = reader.GetString(7),
                Signature = reader.GetString(8),
                SentAtUnixMs = reader.GetInt64(9),
                EditedAtUnixMs = reader.GetInt64(10),
                IsPinned = reader.GetInt64(11) != 0
            });
        }

        result.Reverse();
        await FillReactionsAsync(result, cancellationToken);
        return result;
    }

    /// <summary>Возвращает одно сообщение пространства с реакциями или null.</summary>
    public async Task<SpaceMessageDto?> GetEncryptedMessageAsync(
        string spaceId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, SpaceId, TopicId, SenderId, SenderUsername,
                   Nonce, Tag, Ciphertext, Signature, SentAtUnixMs,
                   EditedAtUnixMs, IsPinned
            FROM SpaceMessages
            WHERE SpaceId = $spaceId AND Id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$spaceId", spaceId);
        command.Parameters.AddWithValue("$id", messageId);

        SpaceMessageDto? message = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                message = ReadMessage(reader);
            }
        }

        if (message is not null)
        {
            await FillReactionsAsync(new List<SpaceMessageDto> { message }, cancellationToken);
        }

        return message;
    }

    /// <summary>Заменяет шифротекст сообщения при редактировании автором.</summary>
    public async Task<bool> UpdateEncryptedMessageAsync(
        SpaceMessageDto message,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE SpaceMessages
                SET Nonce = $nonce, Tag = $tag, Ciphertext = $ciphertext,
                    Signature = $signature, EditedAtUnixMs = $editedAt
                WHERE SpaceId = $spaceId AND Id = $id AND SenderId = $senderId;
                """;
            command.Parameters.AddWithValue("$nonce", message.Nonce);
            command.Parameters.AddWithValue("$tag", message.Tag);
            command.Parameters.AddWithValue("$ciphertext", message.Ciphertext);
            command.Parameters.AddWithValue("$signature", message.Signature);
            command.Parameters.AddWithValue("$editedAt", message.EditedAtUnixMs);
            command.Parameters.AddWithValue("$spaceId", message.SpaceId);
            command.Parameters.AddWithValue("$id", message.Id);
            command.Parameters.AddWithValue("$senderId", message.SenderId);

            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Удаляет сообщение пространства вместе с его реакциями.</summary>
    public async Task<bool> DeleteEncryptedMessageAsync(
        string spaceId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using (var reactions = connection.CreateCommand())
            {
                reactions.CommandText = "DELETE FROM SpaceMessageReactions WHERE MessageId = $id;";
                reactions.Parameters.AddWithValue("$id", messageId);
                await reactions.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM SpaceMessages WHERE SpaceId = $spaceId AND Id = $id;";
            command.Parameters.AddWithValue("$spaceId", spaceId);
            command.Parameters.AddWithValue("$id", messageId);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Добавляет или снимает реакцию участника на сообщение.</summary>
    public async Task SetReactionAsync(
        string messageId,
        string userId,
        string emoji,
        bool on,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = on
                ? "INSERT OR IGNORE INTO SpaceMessageReactions (MessageId, UserId, Emoji) VALUES ($id, $userId, $emoji);"
                : "DELETE FROM SpaceMessageReactions WHERE MessageId = $id AND UserId = $userId AND Emoji = $emoji;";
            command.Parameters.AddWithValue("$id", messageId);
            command.Parameters.AddWithValue("$userId", userId);
            command.Parameters.AddWithValue("$emoji", emoji);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Закрепляет или открепляет сообщение пространства.</summary>
    public async Task SetPinnedAsync(
        string spaceId,
        string messageId,
        bool pinned,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE SpaceMessages SET IsPinned = $pinned WHERE SpaceId = $spaceId AND Id = $id;";
            command.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
            command.Parameters.AddWithValue("$spaceId", spaceId);
            command.Parameters.AddWithValue("$id", messageId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static SpaceMessageDto ReadMessage(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        SpaceId = reader.GetString(1),
        TopicId = reader.IsDBNull(2) ? null : reader.GetString(2),
        SenderId = reader.GetString(3),
        SenderUsername = reader.GetString(4),
        Nonce = reader.GetString(5),
        Tag = reader.GetString(6),
        Ciphertext = reader.GetString(7),
        Signature = reader.GetString(8),
        SentAtUnixMs = reader.GetInt64(9),
        EditedAtUnixMs = reader.GetInt64(10),
        IsPinned = reader.GetInt64(11) != 0
    };

    /// <summary>Загружает реакции для списка сообщений одним запросом.</summary>
    private async Task FillReactionsAsync(
        List<SpaceMessageDto> messages,
        CancellationToken cancellationToken)
    {
        if (messages.Count == 0)
        {
            return;
        }

        var byId = messages.ToDictionary(item => item.Id, StringComparer.Ordinal);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var names = new List<string>();
        for (var i = 0; i < messages.Count; i++)
        {
            names.Add($"$m{i}");
            command.Parameters.AddWithValue($"$m{i}", messages[i].Id);
        }

        command.CommandText =
            $"SELECT MessageId, UserId, Emoji FROM SpaceMessageReactions WHERE MessageId IN ({string.Join(", ", names)});";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (byId.TryGetValue(reader.GetString(0), out var message))
            {
                message.Reactions.Add(new Reaction
                {
                    UserId = reader.GetString(1),
                    Emoji = reader.GetString(2)
                });
            }
        }
    }

    public async Task<TopicDto> CreateTopicAsync(
        string spaceId,
        string title,
        string createdBy,
        string createdByUsername,
        string? firstMessage,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO Topics (Id, SpaceId, Title, CreatedBy, CreatedAt)
                    VALUES ($id, $spaceId, $title, $createdBy, $createdAt);
                    """;
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$spaceId", spaceId);
                insert.Parameters.AddWithValue("$title", title.Trim());
                insert.Parameters.AddWithValue("$createdBy", createdBy);
                insert.Parameters.AddWithValue("$createdAt", now.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(firstMessage))
            {
                await SaveMessageAsync(
                    spaceId,
                    id,
                    createdBy,
                    createdByUsername,
                    firstMessage,
                    cancellationToken);
            }
        }
        finally
        {
            _writeLock.Release();
        }

        return new TopicDto
        {
            Id = id,
            SpaceId = spaceId,
            Title = title.Trim(),
            CreatedBy = createdBy,
            CreatedByUsername = createdByUsername,
            CreatedAtUnixMs = now.ToUnixTimeMilliseconds(),
            MessageCount = 1,
            LastMessageAtUnixMs = now.ToUnixTimeMilliseconds()
        };
    }

    public async Task<List<TopicDto>> ListTopicsAsync(
        string spaceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.Id, t.SpaceId, t.Title, t.CreatedBy, u.Username, t.CreatedAt,
                   (SELECT COUNT(*) FROM SpaceMessages sm WHERE sm.TopicId = t.Id),
                   (SELECT COALESCE(MAX(SentAtUnixMs), 0) FROM SpaceMessages sm WHERE sm.TopicId = t.Id)
            FROM Topics t
            LEFT JOIN Users u ON u.Id = t.CreatedBy
            WHERE t.SpaceId = $spaceId
            ORDER BY t.CreatedAt DESC;
            """;
        command.Parameters.AddWithValue("$spaceId", spaceId);

        var result = new List<TopicDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TopicDto
            {
                Id = reader.GetString(0),
                SpaceId = reader.GetString(1),
                Title = reader.GetString(2),
                CreatedBy = reader.GetString(3),
                CreatedByUsername = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                CreatedAtUnixMs = DateTimeOffset.Parse(
                    reader.GetString(5),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind).ToUnixTimeMilliseconds(),
                MessageCount = reader.GetInt32(6),
                LastMessageAtUnixMs = reader.GetInt64(7)
            });
        }

        return result;
    }

    public async Task<bool> DeleteTopicAsync(
        string topicId,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM SpaceMessages WHERE TopicId = $topicId;
                DELETE FROM Topics WHERE Id = $topicId;
                """;
            command.Parameters.AddWithValue("$topicId", topicId);

            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<List<string>> GetMemberIdsAsync(
        string spaceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT UserId FROM SpaceMembers WHERE SpaceId = $spaceId;";
        command.Parameters.AddWithValue("$spaceId", spaceId);

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <summary>
    /// Возвращает идентификаторы пространств, участником которых является пользователь.
    /// Нужно, чтобы при подключении к серверу он вступил в группы SignalR
    /// и получал сообщения, реакции и закрепления пространств.
    /// </summary>
    public async Task<List<string>> GetSpaceIdsForUserAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT SpaceId FROM SpaceMembers WHERE UserId = $userId;";
        command.Parameters.AddWithValue("$userId", userId);

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }
}