using SecureChat.Core.Text;

namespace SecureChat.Core.Text;

/// <summary>
/// Готовая палитра оформления: именованный набор цветов, задающий сразу все
/// роли темы. Хранится в Core, чтобы наборы и правила их применения можно было
/// покрыть тестами без UI.
/// </summary>
public sealed class ThemePalettePreset
{
    /// <summary>Название палитры (для пользователя).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Короткое пояснение: какая палитра и для чего.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Цвета по ролям: ключ ресурса → HEX.</summary>
    public Dictionary<string, string> Colors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Сколько ролей задаёт палитра.</summary>
    public int ColorCount => Colors.Count;

    /// <summary>
    /// Применяет палитру к черновику: заданные роли перезаписываются,
    /// остальные сохраняются как есть.
    /// </summary>
    public void ApplyTo(IDictionary<string, string> draft)
    {
        foreach (var pair in Colors)
        {
            var key = ThemeColorKeys.NormalizeKey(pair.Key);
            if (key is null)
            {
                continue;
            }

            if (ThemeColors.Normalize(pair.Value) is { } normalized)
            {
                draft[key] = normalized;
            }
        }
    }
}

/// <summary>
/// Каталог готовых палитр оформления: тёмные и светлые наборы, заимствованные
/// из популярных приложений и дизайн-систем.
/// </summary>
public static class ThemePalettePresets
{
    /// <summary>Тёмная палитра в духе Telegram (сине-графитовая).</summary>
    public static ThemePalettePreset TelegramDark { get; } = new()
    {
        Name = "Telegram (тёмная)",
        Description = "Графитовая база с синим акцентом",
        Colors = Build(
            ("PageBackground", "#17212B"),
            ("Surface", "#202B36"),
            ("SurfaceAlt", "#242F3D"),
            ("BorderColor", "#2B3B4A"),
            ("TextPrimary", "#F5F5F5"),
            ("TextSecondary", "#C8D0D8"),
            ("TextMuted", "#94A5B4"),
            ("Accent", "#6AB3F3"),
            ("MessageMe", "#2AABEE"),
            ("MessageThem", "#182533"),
            ("MessageThemText", "#F5F5F5"),
            ("Success", "#4FAE4E"),
            ("Danger", "#E2504A"),
            ("Warning", "#E8A33D"))
    };

    /// <summary>Светлая палитра в духе Telegram.</summary>
    public static ThemePalettePreset TelegramLight { get; } = new()
    {
        Name = "Telegram (светлая)",
        Description = "Белая база с голубым акцентом",
        Colors = Build(
            ("PageBackground", "#FFFFFF"),
            ("Surface", "#F4F4F5"),
            ("SurfaceAlt", "#E9ECEF"),
            ("BorderColor", "#DDE1E4"),
            ("TextPrimary", "#0F1419"),
            ("TextSecondary", "#3D4E5C"),
            ("TextMuted", "#707A86"),
            ("Accent", "#3390EC"),
            ("MessageMe", "#3390EC"),
            ("MessageThem", "#EEF1F3"),
            ("MessageThemText", "#0F1419"),
            ("Success", "#3E9E3E"),
            ("Danger", "#D33B3B"),
            ("Warning", "#C08A1E"))
    };

    /// <summary>Тёмная палитра в духе Discord.</summary>
    public static ThemePalettePreset DiscordDark { get; } = new()
    {
        Name = "Discord (тёмная)",
        Description = "Тёмно-синяя база с фиолетовым акцентом",
        Colors = Build(
            ("PageBackground", "#313338"),
            ("Surface", "#2B2D31"),
            ("SurfaceAlt", "#1E1F22"),
            ("BorderColor", "#1E1F22"),
            ("TextPrimary", "#F2F3F5"),
            ("TextSecondary", "#B5BAC1"),
            ("TextMuted", "#949BA4"),
            ("Accent", "#5865F2"),
            ("MessageMe", "#5865F2"),
            ("MessageThem", "#383A40"),
            ("MessageThemText", "#F2F3F5"),
            ("Success", "#23A55A"),
            ("Danger", "#F23F43"),
            ("Warning", "#F0B232"))
    };

    /// <summary>Светлая палитра в духе Discord.</summary>
    public static ThemePalettePreset DiscordLight { get; } = new()
    {
        Name = "Discord (светлая)",
        Description = "Светло-серая база с индиго-акцентом",
        Colors = Build(
            ("PageBackground", "#FFFFFF"),
            ("Surface", "#F2F3F5"),
            ("SurfaceAlt", "#E3E5E8"),
            ("BorderColor", "#CFD2D6"),
            ("TextPrimary", "#060607"),
            ("TextSecondary", "#4E5058"),
            ("TextMuted", "#80848E"),
            ("Accent", "#5865F2"),
            ("MessageMe", "#5865F2"),
            ("MessageThem", "#EBEDEF"),
            ("MessageThemText", "#060607"),
            ("Success", "#1FA855"),
            ("Danger", "#D63A3D"),
            ("Warning", "#C77700"))
    };

    /// <summary>Палитра в духе Material 3 (светлая).</summary>
    public static ThemePalettePreset MaterialLight { get; } = new()
    {
        Name = "Material (светлая)",
        Description = "Светлая база с фиолетовым акцентом",
        Colors = Build(
            ("PageBackground", "#FFFBFE"),
            ("Surface", "#F4EFF4"),
            ("SurfaceAlt", "#E9E7EC"),
            ("BorderColor", "#CAC4D0"),
            ("TextPrimary", "#1D1B20"),
            ("TextSecondary", "#49454F"),
            ("TextMuted", "#7A757F"),
            ("Accent", "#6750A4"),
            ("MessageMe", "#6750A4"),
            ("MessageThem", "#ECE6F0"),
            ("MessageThemText", "#1D1B20"),
            ("Success", "#2E7D32"),
            ("Danger", "#B3261E"),
            ("Warning", "#B26A00"))
    };

    /// <summary>Палитра в духе Monokai (тёмная, кислотные акценты).</summary>
    public static ThemePalettePreset Monokai { get; } = new()
    {
        Name = "Monokai",
        Description = "Тёмная палитра редактора с кислотным акцентом",
        Colors = Build(
            ("PageBackground", "#272822"),
            ("Surface", "#2F302A"),
            ("SurfaceAlt", "#3E3D32"),
            ("BorderColor", "#49483E"),
            ("TextPrimary", "#F8F8F2"),
            ("TextSecondary", "#D6D5C4"),
            ("TextMuted", "#A6A28C"),
            ("Accent", "#A6E22E"),
            ("MessageMe", "#4E9A06"),
            ("MessageThem", "#3E3D32"),
            ("MessageThemText", "#F8F8F2"),
            ("Success", "#A6E22E"),
            ("Danger", "#F92672"),
            ("Warning", "#E6DB74"))
    };

    /// <summary>Палитра в духе Nord (тёмная, холодные синие).</summary>
    public static ThemePalettePreset Nord { get; } = new()
    {
        Name = "Nord",
        Description = "Холодная тёмно-синяя палитра",
        Colors = Build(
            ("PageBackground", "#2E3440"),
            ("Surface", "#3B4252"),
            ("SurfaceAlt", "#434C5E"),
            ("BorderColor", "#4C566A"),
            ("TextPrimary", "#ECEFF4"),
            ("TextSecondary", "#D8DEE9"),
            ("TextMuted", "#A9B3C0"),
            ("Accent", "#88C0D0"),
            ("MessageMe", "#5E81AC"),
            ("MessageThem", "#434C5E"),
            ("MessageThemText", "#ECEFF4"),
            ("Success", "#A3BE8C"),
            ("Danger", "#BF616A"),
            ("Warning", "#EBCB8B"))
    };

    /// <summary>Палитра в духе Solarized Light.</summary>
    public static ThemePalettePreset SolarizedLight { get; } = new()
    {
        Name = "Solarized (светлая)",
        Description = "Тёплая светлая палитра с синим акцентом",
        Colors = Build(
            ("PageBackground", "#FDF6E3"),
            ("Surface", "#EEE8D5"),
            ("SurfaceAlt", "#E4DDCA"),
            ("BorderColor", "#D3CBB7"),
            ("TextPrimary", "#073642"),
            ("TextSecondary", "#586E75"),
            ("TextMuted", "#93A1A1"),
            ("Accent", "#268BD2"),
            ("MessageMe", "#268BD2"),
            ("MessageThem", "#EEE8D5"),
            ("MessageThemText", "#073642"),
            ("Success", "#859900"),
            ("Danger", "#DC322F"),
            ("Warning", "#B58900"))
    };

    /// <summary>Палитра в духе Gruvbox (тёмная, тёплая).</summary>
    public static ThemePalettePreset GruvboxDark { get; } = new()
    {
        Name = "Gruvbox (тёмная)",
        Description = "Тёплая тёмная палитра с оранжевым акцентом",
        Colors = Build(
            ("PageBackground", "#282828"),
            ("Surface", "#32302F"),
            ("SurfaceAlt", "#3C3836"),
            ("BorderColor", "#504945"),
            ("TextPrimary", "#EBDBB2"),
            ("TextSecondary", "#D5C4A1"),
            ("TextMuted", "#A89984"),
            ("Accent", "#FE8019"),
            ("MessageMe", "#D65D0E"),
            ("MessageThem", "#3C3836"),
            ("MessageThemText", "#EBDBB2"),
            ("Success", "#98971A"),
            ("Danger", "#FB4934"),
            ("Warning", "#D79921"))
    };

    /// <summary>Все готовые палитры в порядке показа.</summary>
    public static IReadOnlyList<ThemePalettePreset> All => new List<ThemePalettePreset>
    {
        TelegramDark,
        TelegramLight,
        DiscordDark,
        DiscordLight,
        MaterialLight,
        Monokai,
        Nord,
        SolarizedLight,
        GruvboxDark
    };

    private static Dictionary<string, string> Build(params (string Key, string Value)[] pairs)
    {
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in pairs)
        {
            colors[pair.Key] = pair.Value;
        }

        return colors;
    }
}
