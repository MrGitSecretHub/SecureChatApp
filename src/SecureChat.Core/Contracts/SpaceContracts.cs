namespace SecureChat.Core.Contracts;

/// <summary>Тип пространства: переписка, канал, сообщество или форум.</summary>
public static class SpaceTypes
{
    public const string Group = "group";
    public const string Channel = "channel";
    public const string Community = "community";
    public const string Forum = "forum";
}

/// <summary>Роли участника пространства.</summary>
public static class SpaceRoles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Member = "member";
    public const string Guest = "guest";
}

public sealed class CreateSpaceRequest
{
    public required string Name { get; set; }
    public required string Type { get; set; } = SpaceTypes.Group;
    public string? Description { get; set; }
    public bool IsPublic { get; set; }
    public List<string> Members { get; set; } = new();
}

public sealed class UpdateSpaceRequest
{
    public string? SpaceId { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Публичность: null — не менять, true/false — задать.</summary>
    public bool? IsPublic { get; set; }

    /// <summary>Подписывать посты канала именем автора («через @админ»): null — не менять.</summary>
    public bool? SignMessages { get; set; }
}

public sealed class SpaceDto
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Type { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool IsPublic { get; set; }
    public required string OwnerId { get; set; }
    public required string OwnerUsername { get; set; }
    public string MyRole { get; set; } = SpaceRoles.Member;
    public bool CanPost { get; set; } = true;
    public int MemberCount { get; set; }
    public int TopicCount { get; set; }
    public bool SignMessages { get; set; }
    public long LastMessageAtUnixMs { get; set; }
    public string LastMessageText { get; set; } = string.Empty;
}

public sealed class SpaceMemberDto
{
    public required string UserId { get; set; }
    public required string Username { get; set; }
    public required string Role { get; set; }
    public bool CanPost { get; set; } = true;
    public string OnlineStatus { get; set; } = "Онлайн";

    /// <summary>До какого времени участнику запрещено писать (Unix мс); 0 — не замучен.</summary>
    public long MutedUntilUnixMs { get; set; }
}

/// <summary>Результат приглашения участника в пространство.</summary>
public sealed class InviteSpaceMemberResult
{
    public bool Success { get; set; }

    /// <summary>Код причины неудачи: no_rights / already_member / user_not_found / space_not_found / key_unavailable / key_save_failed / error.</summary>
    public string Error { get; set; } = string.Empty;

    public string? UserId { get; set; }
    public string? Username { get; set; }
}

public sealed class TopicDto
{
    public required string Id { get; set; }
    public required string SpaceId { get; set; }
    public required string Title { get; set; }
    public required string CreatedBy { get; set; }
    public required string CreatedByUsername { get; set; }
    public long CreatedAtUnixMs { get; set; }
    public int MessageCount { get; set; }
    public long LastMessageAtUnixMs { get; set; }
}

public sealed class CreateTopicRequest
{
    public required string SpaceId { get; set; }
    public required string Title { get; set; }
    public string? Text { get; set; }
}

/// <summary>Сообщение в пространстве (группа, канал, форум, сообщество) — в зашифрованном виде.</summary>
public sealed class SpaceMessageDto
{
    public required string Id { get; set; }
    public required string SpaceId { get; set; }
    public string? TopicId { get; set; }
    public required string SenderId { get; set; }
    public required string SenderUsername { get; set; }
    public required string Nonce { get; set; }
    public required string Tag { get; set; }
    public required string Ciphertext { get; set; }
    public required string Signature { get; set; }
    public long SentAtUnixMs { get; set; }
    public long EditedAtUnixMs { get; set; }
    public bool IsPinned { get; set; }
    public List<Reaction> Reactions { get; set; } = new();
}

/// <summary>Запрос отправки зашифрованного сообщения в пространство.</summary>
public sealed class SendSpaceMessageRequest
{
    public required string SpaceId { get; set; }
    public string? TopicId { get; set; }
    public required SpaceCipher Cipher { get; set; }
}

/// <summary>
/// Ключ пространства, зашифрованный публичным ключом участника.
/// Сервер хранит только такие «конверты» и не может прочитать ключ.
/// </summary>
public sealed class SpaceKeyEnvelope
{
    public required string EphemeralPublicKey { get; set; }
    public required string Nonce { get; set; }
    public required string Tag { get; set; }
    public required string Ciphertext { get; set; }
}

/// <summary>Зашифрованное сообщение пространства (E2E).</summary>
public sealed class SpaceCipher
{
    public required string MessageId { get; set; }
    public string? TopicId { get; set; }
    public required string Nonce { get; set; }
    public required string Tag { get; set; }
    public required string Ciphertext { get; set; }

    /// <summary>Подпись отправителя (ECDSA P-256, base64).</summary>
    public required string Signature { get; set; }
}
