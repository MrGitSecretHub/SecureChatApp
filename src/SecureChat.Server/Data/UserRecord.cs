namespace SecureChat.Server.Data;

public sealed class UserRecord
{
    public required string Id { get; init; }
    public required string Username { get; init; }
    public string? Email { get; init; }
    public required byte[] AuthHash { get; init; }
    public required byte[] AuthSalt { get; init; }
    public required string EncryptionPublicKey { get; init; }
    public required string SigningPublicKey { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string Nickname { get; init; } = string.Empty;
    public string Bio { get; init; } = string.Empty;
    public string AvatarBase64 { get; init; } = string.Empty;
    public string StatusMessage { get; init; } = "Онлайн";
    public long LastSeenUnixMs { get; init; }
    public bool IsAdmin { get; init; }
    public string PushTopic { get; set; } = string.Empty;
}

public sealed record PendingEnvelope(string Id, string Json);

public sealed record DeliveredMessage(string Id, string SenderId);

public sealed class MediaRecord
{
    public required string Id { get; init; }
    public required string SenderId { get; init; }
    public required string RecipientId { get; init; }
    public long Size { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
