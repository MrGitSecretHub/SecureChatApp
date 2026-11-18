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
    /// <summary>Локализованная подпись вкладки; внутренние ID остаются русскими.</summary>
    public static string DisplayName(string folder) => folder switch
    {
        All => Loc.T("sf_all", All),
        Unread => Loc.T("sf_unread", Unread),
        Muted => Loc.T("sf_muted", Muted),
        Archive => Loc.T("sf_archive", Archive),
        _ => folder,
    };

    public static string TabText(
        string folder,
        IReadOnlyList<StoredConversation> regular,
        int archivedCount) => folder switch
    {
        Unread => WithBadge(DisplayName(folder), regular.Count(c => c.UnreadCount > 0)),
        Muted => WithBadge(DisplayName(folder), regular.Count(c => c.IsMuted)),
        Archive => WithBadge(DisplayName(folder), archivedCount),
        _ => DisplayName(folder),
    };

    private static string WithBadge(string folder, int count) =>
        count > 0 ? $"{folder} · {count}" : folder;

    /// <summary>Фиксированные вкладки — их имена нельзя отдавать пользовательским папкам.</summary>
    public static bool IsFixed(string folder) =>
        folder is All or Unread or Muted or Archive;

    /// <summary>Имя зарезервировано; сравнение регистронезависимое.</summary>
    public static bool IsReserved(string name) =>
        FixedNames.Any(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));

    public static readonly string[] FixedNames = [All, Unread, Muted, Archive];

    public static StoredChatFolder? Find(IReadOnlyList<StoredChatFolder> folders, string name) =>
        folders.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Входит ли чат в пользовательскую папку.</summary>
    public static bool Contains(StoredChatFolder folder, StoredConversation conversation) =>
        folder.Usernames.Contains(conversation.Username, StringComparer.OrdinalIgnoreCase);

    /// <summary>Счётчик непрочитанных для подписки папки на панели вкладок.</summary>
    public static string FolderTabText(
        StoredChatFolder folder,
        IReadOnlyList<StoredConversation> regular) =>
        WithBadge(
            folder.Name,
            regular.Count(c => Contains(folder, c) && c.UnreadCount > 0));
}
