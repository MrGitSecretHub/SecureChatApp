namespace SecureChat.App.Views;

/// <summary>Визуальная идентификация пространства: стабильный цвет и глиф по типу.</summary>
public static class SpaceAvatar
{
    private static readonly string[] Palette =
    {
        "#3B82F6", "#8B5CF6", "#EC4899", "#F59E0B", "#10B981",
        "#06B6D4", "#EF4444", "#6366F1", "#14B8A6", "#F97316",
        "#A855F7", "#22C55E"
    };

    public static Color ColorFor(string seed)
    {
        if (string.IsNullOrWhiteSpace(seed)) return Color.FromArgb(Palette[0]);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        return Color.FromArgb(Palette[hash[0] % Palette.Length]);
    }

    public static string GlyphFor(string type) => type switch
    {
        "channel" => "📢",
        "forum" => "🧵",
        "community" => "🌐",
        _ => "👥"
    };

    public static string LetterFor(string name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : name[..1].ToUpperInvariant();
}
