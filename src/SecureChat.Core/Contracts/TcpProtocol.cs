using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SecureChat.Core.Contracts;

/// <summary>Типы кадров TCP-протокола.</summary>
public static class TcpFrameTypes
{
    /// <summary>Запрос клиента: ждём ответ с тем же идентификатором.</summary>
    public const string Request = "req";

    /// <summary>Успешный ответ сервера на запрос.</summary>
    public const string Response = "res";

    /// <summary>Событие сервера: доставка сообщения, реакции, закрепления.</summary>
    public const string Event = "evt";

    /// <summary>Ошибка: запрос отклонён.</summary>
    public const string Error = "err";
}

/// <summary>Методы TCP-протокола: клиент вызывает сервер.</summary>
public static class TcpMethods
{
    // Авторизация
    public const string AuthRegister = "auth.register";
    public const string AuthLogin = "auth.login";
    public const string AuthResume = "auth.resume";

    // Профиль
    public const string UserGet = "user.get";
    public const string UserMe = "user.me";
    public const string UserUpdateProfile = "user.profile";
    public const string ServerTime = "server.time";

    // Вложения
    public const string MediaUpload = "media.upload";
    public const string MediaDownload = "media.download";

    // Переписка
    public const string ChatSend = "chat.send";
    public const string ChatAck = "chat.ack";
    public const string ChatRead = "chat.read";
    public const string ChatTyping = "chat.typing";
    public const string PresenceHidden = "presence.hidden";

    // Пространства
    public const string SpaceList = "spaces.list";
    public const string SpaceCreate = "spaces.create";
    public const string SpaceKey = "spaces.key";
    public const string SpaceKeyRequest = "spaces.keyRequest";
    public const string SpaceKeySave = "spaces.keySave";
    public const string SpaceRotateKey = "spaces.rotateKey";
    public const string SpaceKeysMissing = "spaces.keysMissing";
    public const string SpaceKeysSaveAll = "spaces.keysSaveAll";
    public const string SpaceInvite = "spaces.invite";
    public const string SpaceRequestJoin = "spaces.requestJoin";
    public const string SpaceJoinRequests = "spaces.joinRequests";
    public const string SpaceResolveJoin = "spaces.resolveJoin";
    public const string SpaceMembers = "spaces.members";
    public const string SpaceJoin = "spaces.join";
    public const string SpaceLeave = "spaces.leave";
    public const string SpaceMessages = "spaces.messages";
    public const string SpaceSend = "spaces.send";
    public const string SpaceEdit = "spaces.edit";
    public const string SpaceDelete = "spaces.delete";
    public const string SpaceReaction = "spaces.reaction";
    public const string SpacePinned = "spaces.pinned";
    public const string SpaceTopics = "spaces.topics";
    public const string SpaceCreateTopic = "spaces.createTopic";
    public const string SpaceDeleteTopic = "spaces.deleteTopic";
    public const string SpacePinTopic = "spaces.pinTopic";
    public const string SpaceUpdate = "spaces.update";
    public const string SpaceUpdateMember = "spaces.updateMember";
    public const string SpaceRemoveMember = "spaces.removeMember";
    public const string SpaceDeleteSpace = "spaces.deleteSpace";
}

/// <summary>События TCP-протокола: сервер сообщает клиенту.</summary>
public static class TcpEvents
{
    /// <summary>Входящее зашифрованное сообщение.</summary>
    public const string Envelope = "envelope";

    /// <summary>Сервер принял сообщение в очередь доставки.</summary>
    public const string EnvelopeAccepted = "envelopeAccepted";

    /// <summary>Сообщения доставлены получателю.</summary>
    public const string MessagesDelivered = "messagesDelivered";

    /// <summary>Сообщения прочитаны.</summary>
    public const string MessagesRead = "messagesRead";

    /// <summary>Собеседник печатает.</summary>
    public const string Typing = "typing";

    /// <summary>Новое сообщение пространства.</summary>
    public const string SpaceMessage = "spaceMessage";

    /// <summary>Сообщение пространства изменено: реакция, закрепление, правка.</summary>
    public const string SpaceMessageUpdated = "spaceMessageUpdated";

    /// <summary>Сообщение пространства удалено.</summary>
    public const string SpaceMessageDeleted = "spaceMessageDeleted";

    /// <summary>Создана тема форума.</summary>
    public const string TopicCreated = "topicCreated";
}

/// <summary>Конверт одного кадра TCP-протокола.</summary>
public sealed class TcpEnvelope
{
    /// <summary>Тип кадра: см. <see cref="TcpFrameTypes"/>.</summary>
    public string Type { get; set; } = TcpFrameTypes.Request;

    /// <summary>Идентификатор запроса: по нему клиент ждёт ответ.</summary>
    public string? Id { get; set; }

    /// <summary>Имя метода для запроса.</summary>
    public string? Method { get; set; }

    /// <summary>Данные запроса или ответа.</summary>
    public JsonElement? Payload { get; set; }

    /// <summary>Текст ошибки.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Кадр TCP-протокола: 4 байта длины, 4 байта длины заголовка,
/// JSON-заголовок и необязательное бинарное тело (для вложений).
/// </summary>
public static class TcpFrame
{
    /// <summary>Предельный размер кадра: вложения до 16 МБ плюс заголовок.</summary>
    public const int MaxFrameBytes = 24 * 1024 * 1024;

    /// <summary>Настройки сериализации протокола.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Записывает кадр в поток.</summary>
    public static async Task WriteAsync(
        Stream stream,
        TcpEnvelope envelope,
        ReadOnlyMemory<byte>? body,
        CancellationToken cancellationToken)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        var bodyLength = body?.Length ?? 0;

        var frame = new byte[8 + header.Length + bodyLength];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), 4 + header.Length + bodyLength);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4, 4), header.Length);
        header.CopyTo(frame.AsSpan(8));
        body?.Span.CopyTo(frame.AsSpan(8 + header.Length));

        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>Читает кадр из потока. Возвращает null при закрытии соединения.</summary>
    public static async Task<(TcpEnvelope Envelope, byte[] Body)?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var headerLengthBytes = new byte[8];
        if (!await ReadExactAsync(stream, headerLengthBytes, cancellationToken))
        {
            return null;
        }

        var frameLength = BinaryPrimitives.ReadInt32BigEndian(headerLengthBytes.AsSpan(0, 4));
        var headerLength = BinaryPrimitives.ReadInt32BigEndian(headerLengthBytes.AsSpan(4, 4));

        if (frameLength < 4 || frameLength > MaxFrameBytes ||
            headerLength < 0 || headerLength > frameLength - 4)
        {
            throw new InvalidDataException("Повреждённый кадр TCP-протокола.");
        }

        var headerBytes = new byte[headerLength];
        if (!await ReadExactAsync(stream, headerBytes, cancellationToken))
        {
            return null;
        }

        var bodyLength = frameLength - 4 - headerLength;
        var body = new byte[bodyLength];
        if (bodyLength > 0 && !await ReadExactAsync(stream, body, cancellationToken))
        {
            return null;
        }

        TcpEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<TcpEnvelope>(headerBytes, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Неверный заголовок кадра TCP-протокола.", ex);
        }

        if (envelope is null)
        {
            throw new InvalidDataException("Пустой кадр TCP-протокола.");
        }

        return (envelope, body);
    }

    private static async Task<bool> ReadExactAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (chunk == 0)
            {
                return false;
            }

            read += chunk;
        }

        return true;
    }
}
