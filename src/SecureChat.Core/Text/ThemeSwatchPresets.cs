namespace SecureChat.Core.Text;

/// <summary>
/// Семейство образцов цвета для быстрого выбора в конструкторе темы.
/// </summary>
public sealed record ThemeSwatchFamily(string Title, IReadOnlyList<string> Colors);

/// <summary>
/// Расширенная палитра образцов: нейтральные тона и цветные семейства.
/// Каждый цвет — #RRGGBB.
/// </summary>
public static class ThemeSwatchPresets
{
    /// <summary>Семейства образцов в порядке показа.</summary>
    public static readonly IReadOnlyList<ThemeSwatchFamily> Families = new List<ThemeSwatchFamily>
    {
        new("Нейтральные", new[]
        {
            "#FFFFFF", "#FAFAFA", "#F5F5F5", "#EBEBEB", "#E0E0E0",
            "#D4D4D4", "#C4C4C4", "#B0B0B0", "#9E9E9E", "#8C8C8C",
            "#787878", "#666666", "#525252", "#404040", "#2E2E2E",
            "#1F1F1F", "#141414", "#0D0D0D", "#000000"
        }),
        new("Серый (нейтральный)", new[]
        {
            "#F8FAFC", "#F1F5F9", "#E2E8F0", "#CBD5E1", "#94A3B8",
            "#64748B", "#475569", "#334155", "#1E293B", "#0F172A"
        }),
        new("Красный", new[]
        {
            "#FEF2F2", "#FEE2E2", "#FECACA", "#FCA5A5", "#F87171",
            "#EF4444", "#DC2626", "#B91C1C", "#991B1B", "#7F1D1D"
        }),
        new("Оранжевый", new[]
        {
            "#FFF7ED", "#FFEDD5", "#FED7AA", "#FDBA74", "#FB923C",
            "#F97316", "#EA580C", "#C2410C", "#9A3412", "#7C2D12"
        }),
        new("Жёлтый", new[]
        {
            "#FEFCE8", "#FEF9C3", "#FEF08A", "#FDE047", "#FACC15",
            "#EAB308", "#CA8A04", "#A16207", "#854D0E", "#713F12"
        }),
        new("Зелёный", new[]
        {
            "#F0FDF4", "#DCFCE7", "#BBF7D0", "#86EFAC", "#4ADE80",
            "#22C55E", "#16A34A", "#15803D", "#166534", "#14532D"
        }),
        new("Бирюзовый", new[]
        {
            "#F0FDFA", "#CCFBF1", "#99F6E4", "#5EEAD4", "#2DD4BF",
            "#14B8A6", "#0D9488", "#0F766E", "#115E59", "#134E4A"
        }),
        new("Голубой", new[]
        {
            "#F0F9FF", "#E0F2FE", "#BAE6FD", "#7DD3FC", "#38BDF8",
            "#0EA5E9", "#0284C7", "#0369A1", "#075985", "#0C4A6E"
        }),
        new("Синий", new[]
        {
            "#EFF6FF", "#DBEAFE", "#BFDBFE", "#93C5FD", "#60A5FA",
            "#3B82F6", "#2563EB", "#1D4ED8", "#1E40AF", "#1E3A8A"
        }),
        new("Индиго", new[]
        {
            "#EEF2FF", "#E0E7FF", "#C7D2FE", "#A5B4FC", "#818CF8",
            "#6366F1", "#4F46E5", "#4338CA", "#3730A3", "#312E81"
        }),
        new("Фиолетовый", new[]
        {
            "#FAF5FF", "#F3E8FF", "#E9D5FF", "#D8B4FE", "#C084FC",
            "#A855F7", "#9333EA", "#7E22CE", "#6B21A8", "#581C87"
        }),
        new("Розовый", new[]
        {
            "#FDF2F8", "#FCE7F3", "#FBCFE8", "#F9A8D4", "#F472B6",
            "#EC4899", "#DB2777", "#BE185D", "#9D174D", "#831843"
        })
    };

    /// <summary>Все образцы подряд (для быстрого доступа без группировки).</summary>
    public static IReadOnlyList<string> All => Families
        .SelectMany(family => family.Colors)
        .ToList();
}
