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
    public int ViewCount { get; set; }
    public bool ViewedByMe { get; set; }
    public string BackgroundHex { get; set; } = "#3B6FE8";
    public string? TextHex { get; set; }
    public int FontSize { get; set; } = 18;
    public string? SpaceId { get; set; }
    public string SpaceName { get; set; } = string.Empty;
}

internal sealed class StoryContent
{
    public string Text { get; set; } = string.Empty;
    public string ImageBase64 { get; set; } = string.Empty;
    public string BackgroundHex { get; set; } = "#3B6FE8";
    public string? TextHex { get; set; }
    public int FontSize { get; set; } = 18;
}

/// <summary>Публикация и загрузка историй с E2E-шифрованием для каждого получателя.</summary>
public sealed class StoryService
{
    private readonly ApiClient _api = AppServices.Get<ApiClient>();
    private readonly AppSession _session = AppServices.Get<AppSession>();
    private readonly LocalStore _store = AppServices.Get<LocalStore>();

    public async Task MarkViewedAsync(string storyId)
    {
        if (string.IsNullOrWhiteSpace(storyId) || _session.Token is null) return;
        try
        {
            await _api.MarkStoryViewedAsync(_session.ServerUrl, _session.Token, storyId);
        }
        catch { }
    }

    public async Task<IReadOnlyList<string>> GetViewersAsync(string storyId)
    {
        if (string.IsNullOrWhiteSpace(storyId) || _session.Token is null) return Array.Empty<string>();
        try
        {
            return await _api.GetStoryViewersAsync(_session.ServerUrl, _session.Token, storyId);
        }
        catch { return Array.Empty<string>(); }
    }

    public async Task DeleteAsync(string storyId)
    {
        if (string.IsNullOrWhiteSpace(storyId) || _session.Token is null) return;
        try
        {
            await _api.DeleteStoryAsync(_session.ServerUrl, _session.Token, storyId);
            await _store.RemoveStoryAsync(storyId);
        }
        catch { }
    }

    public async Task PublishAsync(
        string text,
        string imageBase64,
        bool contactsOnly,
        int lifetimeHours = 24,
        string backgroundHex = "#3B6FE8",
        string? textHex = null,
        int fontSize = 18,
        string? spaceId = null)
    {
        var user = _session.User ?? throw new InvalidOperationException("Нет активного аккаунта.");
        var identity = _session.Identity ?? throw new InvalidOperationException("Нет ключей устройства.");
        var created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var users = await _api.ListUsersAsync(_session.ServerUrl, _session.Token!);
        // Including self lets the author see their own story on any device.
        var recipients = users.ToList();
        if (!string.IsNullOrWhiteSpace(spaceId))
        {
            var members = await AppServices.Get<SpaceService>().GetMembersAsync(spaceId);
            var ids = members.Select(m => m.UserId).ToHashSet(StringComparer.Ordinal);
            ids.Add(user.Id);
            recipients = recipients.Where(u => ids.Contains(u.Id)).ToList();
            contactsOnly = false;
        }
        else if (contactsOnly)
        {
            var names = (await _api.GetContactsAsync(_session.ServerUrl, _session.Token!))
                .Select(u => u.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
            recipients = recipients.Where(u =>
                names.Contains(u.Username) ||
                string.Equals(u.Id, user.Id, StringComparison.Ordinal)).ToList();
        }
        if (recipients.Count == 0)
            throw new InvalidOperationException("Нет получателей для истории.");

        var content = new StoryContent
        {
            Text = text.Trim(),
            ImageBase64 = imageBase64,
            BackgroundHex = string.IsNullOrWhiteSpace(backgroundHex) ? "#3B6FE8" : backgroundHex,
            TextHex = textHex,
            FontSize = Math.Clamp(fontSize, 12, 36)
        };
        var request = new StoryPublishRequest
        {
            Visibility = !string.IsNullOrWhiteSpace(spaceId) ? "space:" + spaceId : (contactsOnly ? "contacts" : "all"),
            SpaceId = spaceId,
            CreatedAtUnixMs = created,
            LifetimeHours = Math.Clamp(lifetimeHours, 1, 24)
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
        await _store.AddStoryAsync(
            content.Text,
            content.ImageBase64,
            _session.User?.Username ?? string.Empty,
            created,
            lifetimeHours);
    }

    public async Task<IReadOnlyList<StoryItem>> GetAsync()
    {
        var identity = _session.Identity;
        if (identity is null || _session.Token is null) return Array.Empty<StoryItem>();

        var result = new List<StoryItem>();
        try
        {
            var dtos = await _api.GetStoriesAsync(_session.ServerUrl, _session.Token);
            foreach (var dto in dtos)
            {
                var envelope = JsonSerializer.Deserialize<EncryptedEnvelope>(dto.EnvelopeJson);
                if (envelope is null) continue;
                var profile = await _api.GetUserAsync(_session.ServerUrl, _session.Token, dto.OwnerUsername);
                var payload = MessageCrypto.DecryptPayload(
                    envelope, identity.EncryptionPrivateKey, profile.SigningPublicKey);
                var content = JsonSerializer.Deserialize<StoryContent>(payload.Text);
                if (content is null) continue;

                var mine = string.Equals(dto.OwnerId, _session.User?.Id, StringComparison.Ordinal);
                string spaceName = string.Empty;
                if (!string.IsNullOrWhiteSpace(dto.SpaceId))
                {
                    try
                    {
                        var spaces = await AppServices.Get<SpaceService>().ListAsync();
                        spaceName = spaces.FirstOrDefault(x => x.Id == dto.SpaceId)?.Name ?? string.Empty;
                    }
                    catch { }
                }
                result.Add(new StoryItem
                {
                    Id = dto.Id,
                    OwnerUsername = mine ? (_session.User?.Username ?? dto.OwnerUsername) : dto.OwnerUsername,
                    Text = content.Text,
                    ImageBase64 = content.ImageBase64,
                    AvatarBase64 = mine ? _session.AvatarBase64 : profile.AvatarBase64,
                    CreatedAtUnixMs = dto.CreatedAtUnixMs,
                    ExpiresAtUnixMs = dto.ExpiresAtUnixMs,
                    IsMine = mine,
                    ViewCount = dto.ViewCount,
                    ViewedByMe = dto.ViewedByMe,
                    BackgroundHex = content.BackgroundHex,
                    TextHex = content.TextHex,
                    FontSize = content.FontSize,
                    SpaceId = dto.SpaceId,
                    SpaceName = spaceName
                });
            }
            return result.OrderByDescending(s => s.CreatedAtUnixMs).ToList();
        }
        catch
        {
            // offline: локальная копия своих историй
            foreach (var own in await _store.GetActiveStoriesAsync())
            {
                result.Add(new StoryItem
                {
                    Id = own.Id,
                    OwnerUsername = own.OwnerUsername,
                    Text = own.Text,
                    ImageBase64 = own.ImageBase64,
                    AvatarBase64 = _session.AvatarBase64,
                    CreatedAtUnixMs = own.CreatedAtUnixMs,
                    ExpiresAtUnixMs = own.ExpiresAtUnixMs,
                    IsMine = true
                });
            }
            return result;
        }
    }
}
