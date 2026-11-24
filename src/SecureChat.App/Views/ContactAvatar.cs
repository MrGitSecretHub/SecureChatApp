using System.Security.Cryptography;
using System.Text;

namespace SecureChat.App.Views;

/// <summary>Стабильный цвет аватара контакта по имени пользователя.</summary>
public static class ContactAvatar
{
    private static readonly string[] Palette =
    {
        "#2563EB", "#7C3AED", "#DB2777", "#D97706", "#059669",
        "#0891B2", "#DC2626", "#4F46E5", "#0D9488", "#EA580C",
        "#9333EA", "#16A34A"
    };

    public static Color ColorFor(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return Color.FromArgb(Palette[0]);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(username.ToLowerInvariant()));
        return Color.FromArgb(Palette[hash[0] % Palette.Length]);
    }
}
