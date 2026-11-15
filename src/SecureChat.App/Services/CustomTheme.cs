using SecureChat.Core.Text;

namespace SecureChat.App.Services;

/// <summary>
/// Своя тема оформления: набор переопределений цветов поверх встроенной палитры.
/// Хранится в зашифрованном хранилище устройства (SecretStore).
/// </summary>
public sealed class CustomTheme
{
    /// <summary>Идентификатор темы (неизменяемый).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Название темы, как её видно в списке «Мои темы».</summary>
    public string Name { get; set; } = "Моя тема";

    /// <summary>Встроенная тема, на которой сделана эта тема (основа палитры).</summary>
    public string BaseTheme { get; set; } = ThemeCatalog.DefaultTheme;

    /// <summary>Переопределения цветов: ключ ресурса (например, «PageBackground») → HEX.</summary>
    public Dictionary<string, string> Colors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Скопировать цвета темы (для редактирования в черновике).</summary>
    public Dictionary<string, string> CloneColors() =>
        new(Colors, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Роль цвета в палитре оформления: ключ ресурса интерфейса, название и пояснение.
/// </summary>
public sealed record ThemeColorRole(string Key, string Title, string Description);

/// <summary>
/// Группа ролей в конструкторе темы — подзаголовок в списке цветов.
/// </summary>
public sealed record ThemeColorGroup(string Title, string[] Roles);

/// <summary>
/// Список ролей, которые разрешено переопределять в конструкторе темы,
/// и палитра быстрого выбора цвета.
/// </summary>
public static class ThemeColorRoles
{
    public static readonly IReadOnlyList<ThemeColorRole> All = new List<ThemeColorRole>
    {
        new(ThemeColorKeys.PageBackground, "Фон", "Фон страниц и списков"),
        new(ThemeColorKeys.Surface, "Панели", "Карточки, шапки, поля ввода"),
        new(ThemeColorKeys.SurfaceAlt, "Панели 2", "Подложки и вложенные блоки"),
        new(ThemeColorKeys.BorderColor, "Границы", "Рамки и разделители"),
        new(ThemeColorKeys.TextPrimary, "Текст", "Основной текст"),
        new(ThemeColorKeys.TextSecondary, "Текст 2", "Второстепенный текст"),
        new(ThemeColorKeys.TextMuted, "Текст 3", "Подписи, время, счётчики"),
        new(ThemeColorKeys.Accent, "Акцент", "Кнопки, ссылки, свои пузыри"),
        new(ThemeColorKeys.MessageMe, "Свои пузыри", "Фон ваших сообщений"),
        new(ThemeColorKeys.MessageThem, "Чужие пузыри", "Фон сообщений собеседника"),
        new(ThemeColorKeys.MessageThemText, "Текст чужих пузырей", "Цвет текста в чужих пузырях"),
        new(ThemeColorKeys.Success, "Успех", "Подтверждения и статусы"),
        new(ThemeColorKeys.Danger, "Ошибка", "Опасные действия и ошибки"),
        new(ThemeColorKeys.Warning, "Внимание", "Предупреждения")
    };

    /// <summary>Палитра быстрого выбора (материальные тона + нейтральные).</summary>
    public static readonly IReadOnlyList<string> Presets = new List<string>
    {
        "#FFFFFF", "#F3F4F6", "#E5E7EB", "#D1D5DB", "#9CA3AF", "#6B7280",
        "#374151", "#1F2937", "#111827", "#000000",
        "#5865F2", "#3B82F6", "#0EA5E9", "#14B8A6", "#22C55E", "#84CC16",
        "#EAB308", "#F97316", "#EF4444", "#EC4899", "#8B5CF6", "#A855F7",
        "#64748B", "#0F766E", "#B45309", "#7F1D1D"
    };

    /// <summary>Группы ролей — показываются подзаголовками в списке цветов.</summary>
    public static readonly IReadOnlyList<ThemeColorGroup> Groups = new List<ThemeColorGroup>
    {
        new("Фон и поверхности", new[]
        {
            ThemeColorKeys.PageBackground, ThemeColorKeys.Surface,
            ThemeColorKeys.SurfaceAlt, ThemeColorKeys.BorderColor
        }),
        new("Текст", new[]
        {
            ThemeColorKeys.TextPrimary, ThemeColorKeys.TextSecondary,
            ThemeColorKeys.TextMuted
        }),
        new("Акцент и сообщения", new[]
        {
            ThemeColorKeys.Accent, ThemeColorKeys.MessageMe,
            ThemeColorKeys.MessageThem, ThemeColorKeys.MessageThemText
        }),
        new("Статусы", new[]
        {
            ThemeColorKeys.Success, ThemeColorKeys.Danger, ThemeColorKeys.Warning
        })
    };

    /// <summary>Приводит ключ к каноническому виду либо возвращает null,
    /// если роль не разрешена для переопределения.</summary>
    public static string? NormalizeKey(string? key) => ThemeColorKeys.NormalizeKey(key);
}

/// <summary>Разбор и формат цвета темы (HEX).</summary>
public static class ThemeColor
{
    /// <summary>Разбирает HEX-значение (#RGB, #RRGGBB, #AARRGGBB).</summary>
    public static bool TryParse(string? value, out Color color)
    {
        color = Colors.Transparent;
        if (!ThemeColors.TryParse(value, out var red, out var green, out var blue))
        {
            return false;
        }

        // Явное приведение к int: перегрузка FromRgb(int, int, int) считает
        // каналы в диапазоне 0..255 (не 0..1, как у float-версии).
        color = Color.FromRgb((int)red, (int)green, (int)blue);
        return true;
    }

    /// <summary>Цвет ресурса в виде #RRGGBB.</summary>
    public static string ToHex(Color color) =>
        $"#{ToByte(color.Red):X2}{ToByte(color.Green):X2}{ToByte(color.Blue):X2}";

    /// <summary>Канал цвета (0..1) в байт (0..255).</summary>
    private static int ToByte(float channel) =>
        (int)Math.Round(Math.Clamp(channel, 0f, 1f) * 255f);
}
