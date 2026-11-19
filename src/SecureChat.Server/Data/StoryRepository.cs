using Microsoft.Data.Sqlite;

namespace SecureChat.Server.Data;

/// <summary>Хранилище stories: конверт шифруется клиентом отдельно для каждого получателя.</summary>
public sealed class StoryRepository
{
    private readonly string _connectionString;

    public StoryRepository(IConfiguration configuration, IHostEnvironment environment)
    {
        var raw = configuration.GetConnectionString("Chat") ?? "Data Source=App_Data/securechat.db";
        var builder = new SqliteConnectionStringBuilder(raw);
        if (!Path.IsPathRooted(builder.DataSource))
            builder.DataSource = ServerDataPaths.ResolveDatabasePath(builder.DataSource, environment);
        _connectionString = builder.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Stories (
                Id             TEXT PRIMARY KEY,
                OwnerId        TEXT NOT NULL,
                OwnerUsername  TEXT NOT NULL,
                Visibility     TEXT NOT NULL,
                CreatedAt      INTEGER NOT NULL,
                ExpiresAt      INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS StoryEnvelopes (
                StoryId        TEXT NOT NULL,
                RecipientId    TEXT NOT NULL,
                EnvelopeJson   TEXT NOT NULL,
                PRIMARY KEY (StoryId, RecipientId)
            );
            CREATE INDEX IF NOT EXISTS IX_Stories_Expires ON Stories(ExpiresAt);
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task PublishAsync(string ownerId, string ownerUsername,
        Core.Contracts.StoryPublishRequest request, CancellationToken ct = default)
    {
        var created = request.CreatedAtUnixMs > 0 ? request.CreatedAtUnixMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expires = created + 24L * 60 * 60 * 1000;
        var visibility = request.Visibility == "all" ? "all" : "contacts";

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = """
                INSERT OR REPLACE INTO Stories (Id, OwnerId, OwnerUsername, Visibility, CreatedAt, ExpiresAt)
                VALUES ($id, $owner, $name, $vis, $created, $expires);
                """;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("$owner", ownerId);
            cmd.Parameters.AddWithValue("$name", ownerUsername);
            cmd.Parameters.AddWithValue("$vis", visibility);
            cmd.Parameters.AddWithValue("$created", created);
            cmd.Parameters.AddWithValue("$expires", expires);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var storyId = await GetLastStoryIdAsync(connection, (SqliteTransaction)transaction, ownerId, created, ct);
        foreach (var envelope in request.Envelopes.Take(500))
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = """
                INSERT OR REPLACE INTO StoryEnvelopes (StoryId, RecipientId, EnvelopeJson)
                VALUES ($story, $rec, $json);
                """;
            cmd.Parameters.AddWithValue("$story", storyId);
            cmd.Parameters.AddWithValue("$rec", envelope.RecipientUserId);
            cmd.Parameters.AddWithValue("$json", envelope.EnvelopeJson);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    private static async Task<string> GetLastStoryIdAsync(SqliteConnection connection,
        SqliteTransaction transaction, string ownerId, long created, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT Id FROM Stories WHERE OwnerId=$owner AND CreatedAt=$created ORDER BY rowid DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$owner", ownerId);
        cmd.Parameters.AddWithValue("$created", created);
        return (await cmd.ExecuteScalarAsync(ct))?.ToString() ?? string.Empty;
    }

    public async Task<IReadOnlyList<Core.Contracts.StoryDto>> GetForUserAsync(string userId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.Id, s.OwnerId, s.OwnerUsername, e.EnvelopeJson, s.CreatedAt, s.ExpiresAt
            FROM Stories s
            JOIN StoryEnvelopes e ON e.StoryId = s.Id AND e.RecipientId = $me
            WHERE s.ExpiresAt > $now
            ORDER BY s.CreatedAt DESC;
            """;
        command.Parameters.AddWithValue("$me", userId);
        command.Parameters.AddWithValue("$now", now);
        var result = new List<Core.Contracts.StoryDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new Core.Contracts.StoryDto
            {
                Id = reader.GetString(0),
                OwnerId = reader.GetString(1),
                OwnerUsername = reader.GetString(2),
                EnvelopeJson = reader.GetString(3),
                CreatedAtUnixMs = reader.GetInt64(4),
                ExpiresAtUnixMs = reader.GetInt64(5)
            });
        }
        return result;
    }
}
