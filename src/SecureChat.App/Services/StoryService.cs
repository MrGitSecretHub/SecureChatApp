using System.Security.Cryptography;
using System.Text.Json;
using SecureChat.Core.Contracts;
using SecureChat.Core.Crypto;

namespace SecureChat.App.Services;

/// <summary>История, готовая к показу (расшифрованная на этом устройстве).</summary>
public sealed class StoryItem
{
    public string Id { get; set; } = string.Empty;
    public string OwnerUsername { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string ImageBase64 { get; set; } = string.Empty;
    public string? AvatarBase64 { get; set; }
    public long CreatedAtUnixMs { get; set; }
    public long ExpiresAtUnixMs { get; set; }
    public bool IsMine { get; set; }
}

internal sealed class StoryContent
{
    public string Text { get; set; } = string.Empty;
    public string ImageBase64 { get; set; } = string.Empty;
}

/// <summary>Публикация и загрузка историй с E2E-шифрованием для каждого получателя.</summary>
public sealed class StoryService
{
    private readonly ApiClient _api = AppServices.Get<ApiClient>();
    private readonly AppSession _session = AppServices.Get<AppSession>();
    private readonly LocalStore _store = AppServices.Get<LocalStore>();

    public async Task PublishAsync(string text, string imageBase64, bool contactsOnly)
    {
        var user = _session.User ?? throw new InvalidOperationException("Нет активного аккаунта.");
        var identity = _session.Identity ?? throw new InvalidOperationException("Нет ключей устройства.");
        var created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var users = await _api.ListUsersAsync(_session.ServerUrl, _session.Token!);
        var recipients = users.Where(u => !string.Equals(u.Id, user.Id, StringComparison.Ordinal)).ToList();
        if (contactsOnly)
        {
            var conversations = await _store.GetConversationsAsync();
            var names = conversations.Select(c => c.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
            recipients = recipients.Where(u => names.Contains(u.Username)).ToList();
        }
        if (recipients.Count == 0)
            throw new InvalidOperationException("Нет получателей для истории.");

        var content = new StoryContent { Text = text.Trim(), ImageBase64 = imageBase64 };
        var request = new StoryPublishRequest
        {
            Visibility = contactsOnly ? "contacts" : "all",
            CreatedAtUnixMs = created
        };

        using var signingKey = identity.CreateSigningKey();
        foreach (var recipient in recipients.Take(500))
        {
            var payload = new MessagePayload
            {
                Kind = "text",
                Text = JsonSerializer.Serialize(content)
            };
            var envelope = MessageCrypto.EncryptPayload(
                Guid.NewGuid().ToString("N"),
                user.Id,
                user.Username,
                recipient.Id,
                recipient.Username,
                created,
                payload,
                recipient.EncryptionPublicKey,
                signingKey);
            request.Envelopes.Add(new StoryEnvelopeDto
            {
                RecipientUserId = recipient.Id,
                EnvelopeJson = JsonSerializer.Serialize(envelope)
            });
        }

        await _api.PublishStoryAsync(_session.ServerUrl, _session.Token!, request);
    }

    public async Task<IReadOnlyList<StoryItem>> GetAsync()
    {
        var identity = _session.Identity;
        if (identity is null || _session.Token is null) return Array.Empty<StoryItem>();

        var dtos = await _api.GetStoriesAsync(_session.ServerUrl, _session.Token);
        var result = new List<StoryItem>();
        foreach (var dto in dtos)
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<EncryptedEnvelope>(dto.EnvelopeJson);
                if (envelope is null) continue;
                var profile = await _api.GetUserAsync(_session.ServerUrl, _session.Token, dto.OwnerUsername);
                var payload = MessageCrypto.DecryptPayload(
                    envelope, identity.EncryptionPrivateKey, profile.SigningPublicKey);
                var content = JsonSerializer.Deserialize<StoryContent>(payload.Text);
                if (content is null) continue;

                var conversation = await _store.GetConversationAsync(dto.OwnerUsername);
                result.Add(new StoryItem
                {
                    Id = dto.Id,
                    OwnerUsername = dto.OwnerUsername,
                    Text = content.Text,
                    ImageBase64 = content.ImageBase64,
                    AvatarBase64 = conversation?.AvatarBase64 ?? profile.AvatarBase64,
                    CreatedAtUnixMs = dto.CreatedAtUnixMs,
                    ExpiresAtUnixMs = dto.ExpiresAtUnixMs,
                    IsMine = string.Equals(dto.OwnerId, _session.User?.Id, StringComparison.Ordinal)
                });
            }
            catch (JsonException) { }
            catch (CryptographicException) { }
        }
        return result.OrderByDescending(s => s.CreatedAtUnixMs).ToList();
    }
}
