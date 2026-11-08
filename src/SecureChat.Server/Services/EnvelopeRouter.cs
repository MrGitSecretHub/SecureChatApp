using System.Security.Claims;
using SecureChat.Core.Contracts;
using SecureChat.Core.Crypto;
using SecureChat.Server.Data;

namespace SecureChat.Server.Services;

/// <summary>
/// Проверка и маршрутизация зашифрованного конверта. Общий код для транспортов:
/// SignalR (WebSocket) и прямого TCP-соединения.
/// </summary>
public sealed class EnvelopeRouter
{
    private const int MaxEnvelopeBytes = 100_000;

    private readonly ChatRepository _repository;

    public EnvelopeRouter(ChatRepository repository) => _repository = repository;

    public async Task<EnvelopeRoute> RouteAsync(
        EncryptedEnvelope envelope,
        ClaimsPrincipal principal)
    {
        var senderId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(senderId))
        {
            return EnvelopeRoute.Rejected("Сессия не авторизована.");
        }

        var sender = await _repository.GetUserByIdAsync(senderId);
        if (sender is null)
        {
            return EnvelopeRoute.Rejected("Пользователь не найден.");
        }

        var validationError = ValidateEnvelope(envelope, sender);
        if (validationError is not null)
        {
            return EnvelopeRoute.Rejected(validationError);
        }

        var recipient = await _repository.GetUserByIdAsync(envelope.RecipientId);
        if (recipient is null)
        {
            return EnvelopeRoute.Rejected("Получатель не найден.");
        }

        if (!string.Equals(envelope.RecipientUsername, recipient.Username, StringComparison.Ordinal))
        {
            return EnvelopeRoute.Rejected("Имя получателя не совпадает с его идентификатором.");
        }

        if (!MessageCrypto.VerifyEnvelope(envelope, sender.SigningPublicKey))
        {
            return EnvelopeRoute.Rejected("Недействительная подпись сообщения.");
        }

        var json = SecureChat.Core.Serialization.ChatJson.Serialize(envelope);
        if (json.Length > MaxEnvelopeBytes)
        {
            return EnvelopeRoute.Rejected("Сообщение слишком большое.");
        }

        if (!await _repository.SaveMessageAsync(
                envelope.Id,
                sender.Id,
                recipient.Id,
                json))
        {
            return EnvelopeRoute.Rejected("Сообщение с таким идентификатором уже принято.");
        }

        return EnvelopeRoute.Success(envelope, recipient.Id);
    }

    private static string? ValidateEnvelope(EncryptedEnvelope envelope, UserRecord sender)
    {
        if (string.IsNullOrWhiteSpace(envelope.Id) ||
            string.IsNullOrWhiteSpace(envelope.SenderId) ||
            string.IsNullOrWhiteSpace(envelope.SenderUsername) ||
            string.IsNullOrWhiteSpace(envelope.RecipientId) ||
            string.IsNullOrWhiteSpace(envelope.RecipientUsername) ||
            string.IsNullOrWhiteSpace(envelope.Ciphertext) ||
            string.IsNullOrWhiteSpace(envelope.Signature))
        {
            return "Конверт заполнен не полностью.";
        }

        if (!string.Equals(envelope.SenderId, sender.Id, StringComparison.Ordinal))
        {
            return "Отправитель конверта не совпадает с авторизованным.";
        }

        if (!string.Equals(envelope.SenderUsername, sender.Username, StringComparison.Ordinal))
        {
            return "Имя отправителя не совпадает с его идентификатором.";
        }

        return null;
    }
}

/// <summary>Результат маршрутизации конверта.</summary>
public sealed record EnvelopeRoute(
    bool Accepted,
    EncryptedEnvelope? Envelope,
    string? RecipientUserId,
    string? Error)
{
    public static EnvelopeRoute Rejected(string error) =>
        new(false, null, null, error);

    public static EnvelopeRoute Success(EncryptedEnvelope envelope, string recipientUserId) =>
        new(true, envelope, recipientUserId, null);
}
