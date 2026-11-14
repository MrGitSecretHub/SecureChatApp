namespace SecureChat.Core.Text;

/// <summary>
/// Разбор и нормализация HEX-цветов темы оформления.
/// Живёт в Core, чтобы правила можно было покрыть тестами без UI.
/// </summary>
public static class ThemeColors
{
    /// <summary>
    /// Приводит введённое значение к каноническому виду #RRGGBB.
    /// Поддерживает #RGB, #RRGGBB, #AARRGGBB и значения без решётки.
    /// Возвращает null, если значение не похоже на цвет.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (!text.StartsWith('#'))
        {
            text = "#" + text;
        }

        if (text.Length == 4)
        {
            // #RGB → #RRGGBB
            text = string.Concat("#", text[1], text[1], text[2], text[2], text[3], text[3]);
        }
        else if (text.Length == 9)
        {
            // #AARRGGBB → #RRGGBB (альфа-канал в темах не используется)
            text = string.Concat("#", text[3], text[4], text[5], text[6], text[7], text[8]);
        }

        if (text.Length != 7)
        {
            return null;
        }

        for (var i = 1; i < 7; i++)
        {
            var c = text[i];
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return null;
            }
        }

        return text.ToUpperInvariant();
    }

    /// <summary>Разбирает цвет в каналы RGB.</summary>
    public static bool TryParse(string? value, out byte red, out byte green, out byte blue)
    {
        red = green = blue = 0;
        var normalized = Normalize(value);
        if (normalized is null)
        {
            return false;
        }

        red = Convert.ToByte(normalized.Substring(1, 2), 16);
        green = Convert.ToByte(normalized.Substring(3, 2), 16);
        blue = Convert.ToByte(normalized.Substring(5, 2), 16);
        return true;
    }
}

/// <summary>
/// Ключи ресурсов оформления, которые разрешено переопределять своей темой.
/// </summary>
public static class ThemeColorKeys
{
    public const string PageBackground = "PageBackground";
    public const string Surface = "Surface";
    public const string SurfaceAlt = "SurfaceAlt";
    public const string BorderColor = "BorderColor";
    public const string TextPrimary = "TextPrimary";
    public const string TextSecondary = "TextSecondary";
    public const string TextMuted = "TextMuted";
    public const string Accent = "Accent";
    public const string MessageMe = "MessageMe";
    public const string MessageThem = "MessageThem";
    public const string MessageThemText = "MessageThemText";
    public const string Success = "Success";
    public const string Danger = "Danger";
    public const string Warning = "Warning";

    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        PageBackground, Surface, SurfaceAlt, BorderColor,
        TextPrimary, TextSecondary, TextMuted,
        Accent, MessageMe, MessageThem, MessageThemText,
        Success, Danger, Warning
    };

    /// <summary>Все разрешённые ключи.</summary>
    public static IReadOnlyCollection<string> All => Allowed;

    /// <summary>Приводит ключ к каноническому виду либо возвращает null,
    /// если роль не разрешена для переопределения.</summary>
    public static string? NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return Allowed.FirstOrDefault(item =>
            string.Equals(item, key.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
