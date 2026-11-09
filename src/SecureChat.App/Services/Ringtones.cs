namespace SecureChat.App.Services;

/// <summary>
/// Регистр рингтонов уведомлений. Все встроенные тоны синтезируются скриптом
/// tools/GenerateRingtones.ps1 (оригинальные звуки, без сторонних материалов)
/// и физически хранятся как raw-ресурсы Android; на Windows они подписываются
/// как встроенные ресурсы с тем же именем файла.
/// </summary>
public static class Ringtones
{
    public sealed record Option(string Id, string Name);

    public static readonly IReadOnlyList<Option> All = new Option[]
    {
        new("system", "Системный"),
        new("chime", "Колокольчик"),
        new("soft", "Мягкий"),
        new("beep", "Сигнал"),
        new("ping", "Пинг"),
    };

    public const string DefaultId = "system";

    public static string NameOf(string id) =>
        All.FirstOrDefault(item => item.Id == id)?.Name ?? "Системный";

    /// <summary>Нормализует сохранённый идентификатор: неизвестные значения
    /// (например, тон, удалённый из сборки) превращаются в системный.</summary>
    public static string Normalize(string? id) =>
        id is not null && All.Any(item => item.Id == id) ? id : DefaultId;
}
