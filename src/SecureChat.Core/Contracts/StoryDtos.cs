namespace SecureChat.Core.Contracts;

/// <summary>Зашифрованный конверт истории для одного получателя.</summary>
public sealed class StoryEnvelopeDto
{
    public string RecipientUserId { get; set; } = string.Empty;
    public string EnvelopeJson { get; set; } = string.Empty;
}

/// <summary>Публикация истории: visibility = "all" | "contacts".</summary>
public sealed class StoryPublishRequest
{
    public string Visibility { get; set; } = "contacts";
    public long CreatedAtUnixMs { get; set; }
    public List<StoryEnvelopeDto> Envelopes { get; set; } = new();
}

/// <summary>История для requesting user — уже с его конвертом.</summary>
public sealed class StoryDto
{
    public string Id { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string OwnerUsername { get; set; } = string.Empty;
    public string EnvelopeJson { get; set; } = string.Empty;
    public long CreatedAtUnixMs { get; set; }
    public long ExpiresAtUnixMs { get; set; }

    /// <summary>Сколько участников просмотрели историю.</summary>
    public int ViewCount { get; set; }

    /// <summary>Просмотрел ли текущий пользователь.</summary>
    public bool ViewedByMe { get; set; }
}
