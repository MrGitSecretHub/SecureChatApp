namespace SecureChat.App.Services;

/// <summary>
/// Палитра оформления интерфейса. Набор тем в духе Telegram: светлые
/// (синяя, зелёная, красная, фиолетовая) и ночные (синяя, зелёная, фиолетовая).
/// </summary>
public sealed record ThemePalette(
    string Name,
    bool IsDark,
    string PageBackground,
    string Surface,
    string SurfaceAlt,
    string SurfaceHover,
    string BorderColor,
    string BorderHover,
    string TextPrimary,
    string TextSecondary,
    string TextMuted,
    string TextDisabled,
    string Accent,
    string AccentHover,
    string MessageMe,
    string MessageThem,
    string MessageThemText,
    string Success,
    string Danger,
    string Warning,
    string OnlineDot,
    string UnreadDot)
{
    public Color AccentColor => Color.FromArgb(Accent);
    public Color PageBackgroundColor => Color.FromArgb(PageBackground);
    public Color SurfaceColor => Color.FromArgb(Surface);
    public Color SurfaceAltColor => Color.FromArgb(SurfaceAlt);
    public Color SurfaceHoverColor => Color.FromArgb(SurfaceHover);
    public Color BorderColorValue => Color.FromArgb(BorderColor);
    public Color BorderHoverColor => Color.FromArgb(BorderHover);
    public Color TextPrimaryColor => Color.FromArgb(TextPrimary);
    public Color TextSecondaryColor => Color.FromArgb(TextSecondary);
    public Color TextMutedColor => Color.FromArgb(TextMuted);
    public Color TextDisabledColor => Color.FromArgb(TextDisabled);
    public Color AccentHoverColor => Color.FromArgb(AccentHover);
    public Color MessageMeColor => Color.FromArgb(MessageMe);
    public Color MessageThemColor => Color.FromArgb(MessageThem);
    public Color MessageThemTextColor => Color.FromArgb(MessageThemText);
    public Color SuccessColor => Color.FromArgb(Success);
    public Color DangerColor => Color.FromArgb(Danger);
    public Color WarningColor => Color.FromArgb(Warning);
    public Color OnlineDotColor => Color.FromArgb(OnlineDot);
    public Color UnreadDotColor => Color.FromArgb(UnreadDot);
}

/// <summary>Каталог доступных тем оформления.</summary>
public static class ThemeCatalog
{
    /// <summary>Тема, используемая по умолчанию.</summary>
    public const string DefaultTheme = "Классическая";

    /// <summary>Опция «Как в системе».</summary>
    public const string SystemTheme = "Системная";

    public static readonly ThemePalette[] Themes =
    {
        new(
            Name: DefaultTheme,
            IsDark: true,
            PageBackground: "#0B0E16",
            Surface: "#111827",
            SurfaceAlt: "#1F2937",
            SurfaceHover: "#374151",
            BorderColor: "#374151",
            BorderHover: "#4B5563",
            TextPrimary: "#F9FAFB",
            TextSecondary: "#D1D5DB",
            TextMuted: "#9CA3AF",
            TextDisabled: "#6B7280",
            Accent: "#5865F2",
            AccentHover: "#4752C4",
            MessageMe: "#5865F2",
            MessageThem: "#2F3136",
            MessageThemText: "#FFFFFF",
            Success: "#3BA55D",
            Danger: "#ED4245",
            Warning: "#FAA81A",
            OnlineDot: "#3BA55D",
            UnreadDot: "#ED4245"),

        new(
            Name: "Синяя",
            IsDark: false,
            PageBackground: "#FFFFFF",
            Surface: "#F3F4F6",
            SurfaceAlt: "#E5E7EB",
            SurfaceHover: "#D1D5DB",
            BorderColor: "#D1D5DB",
            BorderHover: "#9CA3AF",
            TextPrimary: "#111827",
            TextSecondary: "#374151",
            TextMuted: "#6B7280",
            TextDisabled: "#9CA3AF",
            Accent: "#0088CC",
            AccentHover: "#0077B3",
            MessageMe: "#0088CC",
            MessageThem: "#EBEDF0",
            MessageThemText: "#111827",
            Success: "#1FA855",
            Danger: "#D63A3D",
            Warning: "#C77700",
            OnlineDot: "#1FA855",
            UnreadDot: "#D63A3D"),

        new(
            Name: "Зелёная",
            IsDark: false,
            PageBackground: "#FFFFFF",
            Surface: "#F2F7F4",
            SurfaceAlt: "#E3EFE7",
            SurfaceHover: "#CFE3D5",
            BorderColor: "#CFE3D5",
            BorderHover: "#9EC5A9",
            TextPrimary: "#12261A",
            TextSecondary: "#2C4A37",
            TextMuted: "#5E7D6A",
            TextDisabled: "#8FAA99",
            Accent: "#3BA55D",
            AccentHover: "#2E8A4A",
            MessageMe: "#3BA55D",
            MessageThem: "#E8F1EB",
            MessageThemText: "#12261A",
            Success: "#2E8A4A",
            Danger: "#D63A3D",
            Warning: "#C77700",
            OnlineDot: "#2E8A4A",
            UnreadDot: "#D63A3D"),

        new(
            Name: "Фиолетовая",
            IsDark: false,
            PageBackground: "#FFFFFF",
            Surface: "#F6F3FC",
            SurfaceAlt: "#EBE5F8",
            SurfaceHover: "#DBD1F0",
            BorderColor: "#DBD1F0",
            BorderHover: "#BCA9E0",
            TextPrimary: "#1E1533",
            TextSecondary: "#3F3260",
            TextMuted: "#6E6290",
            TextDisabled: "#9A90B5",
            Accent: "#8B5CF6",
            AccentHover: "#7546DC",
            MessageMe: "#8B5CF6",
            MessageThem: "#EEE9FA",
            MessageThemText: "#1E1533",
            Success: "#2E8A4A",
            Danger: "#D63A3D",
            Warning: "#C77700",
            OnlineDot: "#2E8A4A",
            UnreadDot: "#D63A3D"),

        new(
            Name: "Оттенок",
            IsDark: true,
            PageBackground: "#232B33",
            Surface: "#2C3540",
            SurfaceAlt: "#38434F",
            SurfaceHover: "#46525F",
            BorderColor: "#46525F",
            BorderHover: "#56626F",
            TextPrimary: "#EDEFF2",
            TextSecondary: "#C7CCD4",
            TextMuted: "#9AA3AE",
            TextDisabled: "#707A85",
            Accent: "#5B9BC9",
            AccentHover: "#4C86B3",
            MessageMe: "#4C7FA6",
            MessageThem: "#313B46",
            MessageThemText: "#EDEFF2",
            Success: "#3BA55D",
            Danger: "#ED4245",
            Warning: "#FAA81A",
            OnlineDot: "#3BA55D",
            UnreadDot: "#ED4245"),

        new(
            Name: "Ночная синяя",
            IsDark: true,
            PageBackground: "#17212B",
            Surface: "#1D2733",
            SurfaceAlt: "#242F3D",
            SurfaceHover: "#2B3B4E",
            BorderColor: "#2B3B4E",
            BorderHover: "#3A4E63",
            TextPrimary: "#F5F7FA",
            TextSecondary: "#D5D9E0",
            TextMuted: "#9BA3AE",
            TextDisabled: "#6E7783",
            Accent: "#0088CC",
            AccentHover: "#0077B3",
            MessageMe: "#0088CC",
            MessageThem: "#242F3D",
            MessageThemText: "#F5F7FA",
            Success: "#3BA55D",
            Danger: "#ED4245",
            Warning: "#FAA81A",
            OnlineDot: "#3BA55D",
            UnreadDot: "#ED4245"),

        new(
            Name: "Ночная зелёная",
            IsDark: true,
            PageBackground: "#12211A",
            Surface: "#18291F",
            SurfaceAlt: "#1F3327",
            SurfaceHover: "#264030",
            BorderColor: "#264030",
            BorderHover: "#31523D",
            TextPrimary: "#EDF7F0",
            TextSecondary: "#C9DCCF",
            TextMuted: "#93AC9C",
            TextDisabled: "#6A8072",
            Accent: "#3BA55D",
            AccentHover: "#2E8A4A",
            MessageMe: "#3BA55D",
            MessageThem: "#1F3327",
            MessageThemText: "#EDF7F0",
            Success: "#3BA55D",
            Danger: "#ED4245",
            Warning: "#FAA81A",
            OnlineDot: "#3BA55D",
            UnreadDot: "#ED4245"),

        new(
            Name: "Ночная фиолетовая",
            IsDark: true,
            PageBackground: "#1B1622",
            Surface: "#221C2C",
            SurfaceAlt: "#2A2338",
            SurfaceHover: "#342B45",
            BorderColor: "#342B45",
            BorderHover: "#423756",
            TextPrimary: "#F4F1F9",
            TextSecondary: "#D6CFE6",
            TextMuted: "#9C92B5",
            TextDisabled: "#6F6688",
            Accent: "#8B5CF6",
            AccentHover: "#7546DC",
            MessageMe: "#8B5CF6",
            MessageThem: "#2A2338",
            MessageThemText: "#F4F1F9",
            Success: "#3BA55D",
            Danger: "#ED4245",
            Warning: "#FAA81A",
            OnlineDot: "#3BA55D",
            UnreadDot: "#ED4245")
    };

    /// <summary>Возвращает палитру по имени; при неизвестном имени — тему по умолчанию.</summary>
    public static ThemePalette Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Themes[0];
        }

        return Themes.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];
    }

    /// <summary>Список названий тем для выбора в настройках.</summary>
    public static string[] Names =>
        Themes.Select(item => item.Name).Append(SystemTheme).ToArray();
}
