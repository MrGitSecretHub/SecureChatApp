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

public sealed class TcpSpaceJoinRequestMessage
{
    public required string SpaceId { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class TcpSpaceJoinResolveRequest
{
    public required string RequestId { get; set; }
    public required bool Approved { get; set; }
}

public sealed class TcpSpaceInviteRequest
{
    public required string SpaceId { get; set; }
    public required string Username { get; set; }
}

public sealed class TcpSpaceKeySaveRequest
{
    public required string SpaceId { get; set; }
    public required string UserId { get; set; }
    public required string EnvelopeJson { get; set; }
}

public sealed class TcpSpaceKeyRequestAll
{
    public required string SpaceId { get; set; }
}

public sealed class TcpSpaceKeysSaveAllRequest
{
    public required string SpaceId { get; set; }
    public required Dictionary<string, string> Envelopes { get; set; }
}
