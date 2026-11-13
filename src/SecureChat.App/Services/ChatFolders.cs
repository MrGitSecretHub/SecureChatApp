namespace SecureChat.App.Services;

/// <summary>
/// Вкладки списка личных чатов — как папки в Telegram: «Все», «Непрочитанные»,
/// «Без звука», «Архив». Выбор вкладки хранится в AppSession (настройки этого
/// класса не переживают перезапуск, как и остальное).
/// </summary>
public static class ChatFolders
{
    public const string All = "Все";
    public const string Unread = "Непрочитанные";
    public const string Muted = "Без звука";
    public const string Archive = "Архив";

    /// <summary>Фильтр вкладки поверх уже выбранного списка (архив или обычные):
    /// «Архив» и «Все» различаются источником, для остальных считаем флаги чата.</summary>
    public static bool Matches(string folder, StoredConversation conversation) => folder switch
    {
        Unread => conversation.UnreadCount > 0,
        Muted => conversation.IsMuted,
        _ => true,
    };

    /// <summary>Подпись вкладки с бейджем: «Непрочитанные · 3»; без счётчика — просто имя.</summary>
    public static string TabText(
        string folder,
        IReadOnlyList<StoredConversation> regular,
        int archivedCount) => folder switch
    {
        Unread => WithBadge(folder, regular.Count(c => c.UnreadCount > 0)),
        Muted => WithBadge(folder, regular.Count(c => c.IsMuted)),
        Archive => WithBadge(folder, archivedCount),
        _ => folder,
    };

    private static string WithBadge(string folder, int count) =>
        count > 0 ? $"{folder} · {count}" : folder;
}
