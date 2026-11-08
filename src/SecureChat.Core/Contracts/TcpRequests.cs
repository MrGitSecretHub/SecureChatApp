using System.Text.Json;

namespace SecureChat.Core.Contracts;

/// <summary>Запрос на регистрацию нового аккаунта.</summary>
public sealed class TcpRegisterRequest
{
    public required string Username { get; set; }
    public required string AuthSecret { get; set; }
    public required string EncryptionPublicKey { get; set; }
    public required string SigningPublicKey { get; set; }
}

/// <summary>Запрос на вход в аккаунт.</summary>
public sealed class TcpLoginRequest
{
    public required string Username { get; set; }
    public required string AuthSecret { get; set; }
}

/// <summary>Ответ на вход или регистрацию.</summary>
public sealed class TcpAuthResponse
{
    public required string Token { get; set; }
    public required string UserId { get; set; }
    public required string Username { get; set; }
}

/// <summary>Запрос профиля пользователя по имени.</summary>
public sealed class TcpUserRequest
{
    public required string Username { get; set; }
}

/// <summary>Загрузка вложения на сервер.</summary>
public sealed class TcpMediaUploadRequest
{
    public required string MediaId { get; set; }

    /// <summary>Получатель вложения (личный чат).</summary>
    public string? RecipientId { get; set; }

    /// <summary>Пространство-владелец вложения.</summary>
    public string? SpaceId { get; set; }
}

/// <summary>Скачивание вложения с сервера.</summary>
public sealed class TcpMediaDownloadRequest
{
    public required string MediaId { get; set; }
}

/// <summary>Отправка зашифрованного конверта получателю.</summary>
public sealed class TcpSendEnvelopeRequest
{
    public required EncryptedEnvelope Envelope { get; set; }
}

/// <summary>Подтверждение доставки или прочтения сообщений.</summary>
public sealed class TcpMessageIdsRequest
{
    public required List<string> Ids { get; set; }
}

/// <summary>Уведомление «печатает».</summary>
public sealed class TcpTypingRequest
{
    public required string RecipientId { get; set; }
}

/// <summary>Скрытие или показ статуса «в сети».</summary>
public sealed class TcpPresenceRequest
{
    public required bool Hidden { get; set; }
}

/// <summary>Создание пространства.</summary>
public sealed class TcpSpaceCreateRequest
{
    public required string Name { get; set; }
    public required string Type { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool IsPublic { get; set; }
    public List<string> Members { get; set; } = new();
}

/// <summary>Общий запрос с идентификатором пространства.</summary>
public sealed class TcpSpaceRequest
{
    public required string SpaceId { get; set; }
}

/// <summary>Запрос сообщений пространства (с необязательной темой форума).</summary>
public sealed class TcpSpaceMessagesRequest
{
    public required string SpaceId { get; set; }
    public string? TopicId { get; set; }
}

/// <summary>Отправка сообщения в пространство.</summary>
public sealed class TcpSpaceSendRequest
{
    public required string SpaceId { get; set; }
    public string? TopicId { get; set; }
    public required SpaceCipher Cipher { get; set; }
}

/// <summary>Правка сообщения пространства.</summary>
public sealed class TcpSpaceEditRequest
{
    public required string SpaceId { get; set; }
    public required SpaceCipher Cipher { get; set; }
}

/// <summary>Удаление сообщения пространства.</summary>
public sealed class TcpSpaceDeleteRequest
{
    public required string SpaceId { get; set; }
    public required string MessageId { get; set; }
}

/// <summary>Реакция на сообщение пространства.</summary>
public sealed class TcpSpaceReactionRequest
{
    public required string SpaceId { get; set; }
    public required string MessageId { get; set; }
    public required string Emoji { get; set; }
    public required bool On { get; set; }
}

/// <summary>Закрепление сообщения пространства.</summary>
public sealed class TcpSpacePinnedRequest
{
    public required string SpaceId { get; set; }
    public required string MessageId { get; set; }
    public required bool Pinned { get; set; }
}

/// <summary>Создание темы форума.</summary>
public sealed class TcpTopicCreateRequest
{
    public required string SpaceId { get; set; }
    public required string Title { get; set; }
    public string? Text { get; set; }
}

/// <summary>Удаление темы форума.</summary>
public sealed class TcpTopicDeleteRequest
{
    public required string SpaceId { get; set; }
    public required string TopicId { get; set; }
}

/// <summary>Изменение названия и описания пространства.</summary>
public sealed class TcpSpaceUpdateRequest
{
    public required string SpaceId { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = string.Empty;
}

/// <summary>Изменение роли участника пространства.</summary>
public sealed class TcpSpaceMemberRequest
{
    public required string SpaceId { get; set; }
    public required string UserId { get; set; }
    public string? Role { get; set; }
    public bool? CanPost { get; set; }
}

/// <summary>Удаление участника пространства.</summary>
public sealed class TcpSpaceRemoveMemberRequest
{
    public required string SpaceId { get; set; }
    public required string UserId { get; set; }
}

/// <summary>Удаление пространства.</summary>
public sealed class TcpSpaceDeleteSpaceRequest
{
    public required string SpaceId { get; set; }
}

/// <summary>Ошибка протокола: клиент показывает текст пользователю.</summary>
public sealed class TcpProtocolException : Exception
{
    public TcpProtocolException(string message) : base(message)
    {
    }

    /// <summary>Разбирает ошибку из ответа сервера.</summary>
    public static TcpProtocolException FromPayload(JsonElement? payload, string fallback)
    {
        var text = fallback;

        if (payload is { ValueKind: JsonValueKind.Object } element &&
            element.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.String)
        {
            text = error.GetString() ?? fallback;
        }

        return new TcpProtocolException(text);
    }
}
