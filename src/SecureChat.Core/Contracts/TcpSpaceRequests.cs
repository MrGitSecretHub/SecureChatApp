namespace SecureChat.Core.Contracts;

/// <summary>Запрос к пространствам по идентификатору.</summary>
public sealed class TcpSpaceIdRequest
{
    public required string SpaceId { get; set; }
}

/// <summary>Создание темы форума через TCP.</summary>
public sealed class TcpSpaceTopicRequest
{
    public required string SpaceId { get; set; }
    public required string Title { get; set; }
    public string? Text { get; set; }
}

/// <summary>Запрос вступления/выхода из публичного пространства.</summary>
public sealed class TcpSpaceJoinRequest
{
    public required string SpaceId { get; set; }
}
