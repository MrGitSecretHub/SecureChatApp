using SecureChat.App.Services;
using System.Globalization;

namespace SecureChat.App.Views;

/// <summary>
/// Модель чата для боковой панели (список переписок).
/// </summary>
public sealed class ConversationVm
{
    public required string Username { get; init; }
    public required string LastMessage { get; init; }
    public required string TimeText { get; init; }
    public required string UnreadText { get; init; }
    public bool HasUnread { get; init; }
    public bool KeysChanged { get; init; }
    public string Initial { get; init; } = "?";
    public ImageSource? AvatarSource { get; init; }
    public string? AvatarBase64 { get; init; }
    public string OnlineStatus { get; init; } = "Онлайн";
    public string OnlineStatusColor { get; init; } = "#3BA55D";
    public bool IsSelected { get; set; }
    public bool IsArchived { get; init; }
    public bool IsMuted { get; init; }

    public static ConversationVm From(StoredConversation source) => new()
    {
        Username = source.Username,
        LastMessage = string.IsNullOrWhiteSpace(source.LastMessage)
            ? Loc.T("vs_nomessages", "Нет сообщений")
            : source.LastMessage,
        TimeText = source.LastMessageAtUnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(source.LastMessageAtUnixMs)
                .ToLocalTime().ToString("dd.MM HH:mm")
            : string.Empty,
        HasUnread = source.UnreadCount > 0,
        UnreadText = source.UnreadCount > 99 ? "99+" : source.UnreadCount.ToString(),
        KeysChanged = source.KeysChanged,
        IsArchived = source.IsArchived,
        IsMuted = source.IsMuted,
        Initial = string.IsNullOrWhiteSpace(source.Username)
            ? "?"
            : source.Username[..1].ToUpperInvariant(),
        AvatarSource = !string.IsNullOrEmpty(source.AvatarBase64)
            ? ImageSource.FromStream(() => new MemoryStream(Convert.FromBase64String(source.AvatarBase64)))
            : null,
        AvatarBase64 = source.AvatarBase64,
        OnlineStatus = source.OnlineStatus ?? "Онлайн",
        OnlineStatusColor = source.OnlineStatusColor ?? "#3BA55D"
    };
}
