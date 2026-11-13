using SecureChat.Core.Contracts;

namespace SecureChat.App.Views;

/// <summary>Участник пространства для списка настроек.</summary>
public sealed class SpaceMemberVm
{
    private static readonly Color[] Palette =
    {
        Color.FromArgb("#5865F2"),
        Color.FromArgb("#0088CC"),
        Color.FromArgb("#3BA55D"),
        Color.FromArgb("#8B5CF6"),
        Color.FromArgb("#ED4245"),
        Color.FromArgb("#FAA81A")
    };

    public required string UserId { get; init; }
    public required string Username { get; init; }
    public string Role { get; init; } = SpaceRoles.Member;
    public bool CanPost { get; init; } = true;
    public string OnlineStatus { get; init; } = "Онлайн";
    public long MutedUntilUnixMs { get; init; }

    /// <summary>Запрет записи активен прямо сейчас.</summary>
    public bool IsMuted => MutedUntilUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public string Initial => string.IsNullOrEmpty(Username) ? "?" : Username[..1].ToUpperInvariant();

    public Color AvatarColor
    {
        get
        {
            if (string.IsNullOrEmpty(Username))
            {
                return Color.FromArgb("#9CA3AF");
            }

            var hash = 0;
            foreach (var c in Username)
            {
                hash = (hash * 31) + c;
            }

            return Palette[Math.Abs(hash) % Palette.Length];
        }
    }

    public string RoleText => Role switch
    {
        SpaceRoles.Owner => "Владелец",
        SpaceRoles.Admin => "Администратор",
        SpaceRoles.Guest => "Гость",
        _ => "Участник"
    };

    public string CanPostText => IsMuted
        ? "🔇 Запрет записи"
        : CanPost ? "Может писать" : "Только чтение";

    public string StatusText => IsMuted
        ? $"{OnlineStatus} · {RoleText} · 🔇"
        : $"{OnlineStatus} · {RoleText}";

    public static SpaceMemberVm From(SpaceMemberDto source) => new()
    {
        UserId = source.UserId,
        Username = source.Username,
        Role = source.Role,
        CanPost = source.CanPost,
        OnlineStatus = source.OnlineStatus,
        MutedUntilUnixMs = source.MutedUntilUnixMs,
    };
}
