namespace SecureChat.Core.Contracts;

/// <summary>
/// Заявка на вступление в приватное пространство. Хранится на сервере;
/// рассмотреть её может владелец или администратор пространства.
/// </summary>
public sealed class SpaceJoinRequestDto
{
    public required string Id { get; set; }
    public required string SpaceId { get; set; }
    public required string SpaceName { get; set; }
    public required string UserId { get; set; }
    public required string Username { get; set; }
    public string Message { get; set; } = string.Empty;
    public long CreatedAtUnixMs { get; set; }

    /// <summary>Кто рассмотрел (UserId), пусто — заявка ожидает.</summary>
    public string? ResolvedBy { get; set; }

    /// <summary>true — принята, false — отклонена, null — ожидает.</summary>
    public bool? Approved { get; set; }
}
