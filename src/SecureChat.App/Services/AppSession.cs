using SecureChat.Core.Contracts;
using SecureChat.Core.Crypto;

namespace SecureChat.App.Services;

public sealed class AppSession
{
    public UserProfileDto? User { get; private set; }
    public IdentityKeys? Identity { get; private set; }
    public string? Token { get; private set; }
    public string ServerUrl { get; private set; } = DefaultServerUrl;

    public bool IsAuthenticated =>
        User is not null && Identity is not null && !string.IsNullOrEmpty(Token);

    public string Username => User?.Username ?? string.Empty;
    public string Fingerprint => User?.Fingerprint ?? string.Empty;

    /// <summary>Локальное отображаемое имя (редактируется в профиле).</summary>
    public string Nickname { get; set; } = string.Empty;

    /// <summary>Статус: "Онлайн", "Неактивен", "Занят" и т.п.</summary>
    public string StatusMessage { get; set; } = "Онлайн";

    /// <summary>Био / описание профиля.</summary>
    public string Bio { get; set; } = string.Empty;

    /// <summary>Аватар в формате base64 (хранится локально).</summary>
    public string? AvatarBase64 { get; set; }

    /// <summary>Тёмная тема интерфейса.</summary>
    public bool IsDarkTheme { get; set; } = true;

    /// <summary>Режим темы: «Классическая», «Синяя», «Ночная синяя» и т.п. или «Системная».</summary>
    public string Theme { get; set; } = "Классическая";

    /// <summary>Акцентный цвет интерфейса (HEX). Пусто — используется акцент темы.</summary>
    public string Accent { get; set; } = string.Empty;

    /// <summary>Сохранённые пользователем темы (конструктор оформления).</summary>
    public List<CustomTheme> CustomThemes { get; set; } = new();

    /// <summary>Идентификатор активной своей темы. Пусто — используется встроенная палитра.</summary>
    public string ActiveCustomThemeId { get; set; } = string.Empty;

    /// <summary>Активная своя тема (по <see cref="ActiveCustomThemeId"/>).</summary>
    public CustomTheme? ActiveCustomTheme =>
        CustomThemes.FirstOrDefault(theme =>
            string.Equals(theme.Id, ActiveCustomThemeId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Масштаб шрифта: «Маленький», «Обычный» или «Крупный».</summary>
    public string FontScale { get; set; } = "Обычный";

    /// <summary>Плотность сообщений: «Компактная», «Обычная» или «Просторная».</summary>
    public string Density { get; set; } = "Обычная";

    /// <summary>Форма пузырей: «Скруглённая», «Мягкая» или «Прямая».</summary>
    public string BubbleShape { get; set; } = "Скруглённая";

    /// <summary>Цвет вашего имени (HEX). Пусто — используется акцент темы.</summary>
    public string NameColor { get; set; } = string.Empty;

    /// <summary>Шрифт сообщений: «Обычный», «С засечками» или «Моноширинный».</summary>
    public string MessageFont { get; set; } = "Обычный";

    /// <summary>Авто-ночной режим: ночью (20:00–07:00) светлая тема меняется
    /// на ночную пару, днём ночная — обратно на светлую.</summary>
    public bool AutoNightMode { get; set; } = false;

    /// <summary>Путь к картинке обоев переписки. Пусто — без обоев.</summary>
    public string WallpaperPath { get; set; } = string.Empty;

    /// <summary>Затемнять обои полупрозрачной вуалью для читабельности.</summary>
    public bool WallpaperDim { get; set; } = true;

    /// <summary>Обои для пространств: spaceId -> путь к картинке (кэш в памяти).</summary>
    public Dictionary<string, string> SpaceWallpaperPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Обои для пространств: spaceId -> затемнять вуалью (кэш в памяти).</summary>
    public Dictionary<string, bool> SpaceWallpaperDims { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private LocalStore? _localStore;
    private LocalStore LocalStore => _localStore ??= AppServices.Get<LocalStore>();

    /// <summary>Получить путь к обоям пространства (из LocalStore).</summary>
    public async Task<string?> GetSpaceWallpaperPathAsync(string spaceId)
    {
        var fromLocalStore = await LocalStore.GetSpaceWallpaperPathAsync(spaceId);
        if (!string.IsNullOrEmpty(fromLocalStore))
        {
            SpaceWallpaperPaths[spaceId] = fromLocalStore;
            return fromLocalStore;
        }
        return SpaceWallpaperPaths.TryGetValue(spaceId, out var path) ? path : string.Empty;
    }

    /// <summary>Получить флаг затемнения для пространства (из LocalStore).</summary>
    public async Task<bool> GetSpaceWallpaperDimAsync(string spaceId)
    {
        var fromLocalStore = await LocalStore.GetSpaceWallpaperDimAsync(spaceId);
        if (fromLocalStore || SpaceWallpaperDims.ContainsKey(spaceId))
        {
            return fromLocalStore || SpaceWallpaperDims.TryGetValue(spaceId, out var dim) && dim;
        }
        return SpaceWallpaperDims.TryGetValue(spaceId, out var dim2) && dim2;
    }

    /// <summary>Установить обои для пространства (в LocalStore и в кэш).</summary>
    public async Task SetSpaceWallpaperAsync(string spaceId, string path, bool dim)
    {
        SpaceWallpaperPaths[spaceId] = path;
        SpaceWallpaperDims[spaceId] = dim;
        await LocalStore.SetSpaceWallpaperAsync(spaceId, path, dim);
        NotifyAppearanceChanged();
    }

    /// <summary>Удалить обои пространства.</summary>
    public async Task RemoveSpaceWallpaperAsync(string spaceId)
    {
        SpaceWallpaperPaths.Remove(spaceId);
        SpaceWallpaperDims.Remove(spaceId);
        await LocalStore.RemoveSpaceWallpaperAsync(spaceId);
        NotifyAppearanceChanged();
    }

    /// <summary>Показывать аватары в переписке.</summary>
    public bool ShowAvatarsInChat { get; set; } = true;

    /// <summary>Показывать время у сообщений.</summary>
    public bool ShowTimestamps { get; set; } = true;

    /// <summary>Компактный список чатов (без превью последнего сообщения).</summary>
    public bool CompactSidebar { get; set; } = false;

    /// <summary>Сигнал об изменении оформления — интерфейс перерисовывается сразу.</summary>
    public event Action? AppearanceChanged;

    public void NotifyAppearanceChanged() => AppearanceChanged?.Invoke();

    /// <summary>Скрывать статус онлайн от собеседников.</summary>
    public bool HideOnlineStatus { get; set; } = false;

    /// <summary>Скрывать факт прочтения сообщений.</summary>
    public bool HideReadReceipts { get; set; } = false;

    /// <summary>Показывать собеседникам, что вы печатаете.</summary>
    public bool ShowTypingStatus { get; set; } = true;

    /// <summary>Показывать текст сообщения в уведомлениях.</summary>
    public bool ShowNotificationPreview { get; set; } = true;

    /// <summary>Звук уведомления при получении нового сообщения.</summary>
    public bool SoundsEnabled { get; set; } = true;

    /// <summary>Выбранный рингтон уведомлений: «system» или идентификатор
    /// одного из встроенных тонов (Ringtones.All).</summary>
    public string RingtoneId { get; set; } = Ringtones.DefaultId;

    /// <summary>Принимать сообщения только от контактов.</summary>
    public bool AcceptOnlyFromContacts { get; set; } = false;

    /// <summary>Генерировать превью ссылок (Open Graph). Включено по умолчанию:
    /// внимание, владелец сайта увидит IP устройства, которое открыло чат.</summary>
    public bool LinkPreviewEnabled { get; set; } = true;

    /// <summary>Заблокированные пользователи (имена пользователей).</summary>
    public List<string> BlockedUsernames { get; set; } = new();

    /// <summary>Автозагрузка вложений: «Всегда», «По Wi-Fi» или «Никогда».</summary>
    public string AutoDownloadMedia { get; set; } = MediaAutoDownload.Wifi;

    /// <summary>Выбранная вкладка списка чатов: «Все», «Непрочитанные», «Без звука», «Архив».</summary>
    public string ChatFolder { get; set; } = ChatFolders.All;

    /// <summary>Разрешена ли автозагрузка вложений при текущем подключении.</summary>
    public bool IsAutoDownloadAllowed => MediaAutoDownload.IsAllowed(AutoDownloadMedia);

    public bool IsBlocked(string username) =>
        BlockedUsernames.Any(name => string.Equals(name, username, StringComparison.OrdinalIgnoreCase));

    /// <summary>Транспорт подключения: «Авто», «SignalR» или «TCP».</summary>
    public string TransportMode { get; set; } = "Авто";

    /// <summary>Порт TCP-сервера чата.</summary>
    public int TcpPort { get; set; } = 5444;

    /// <summary>
    /// Хост TCP-туннеля (например, домен Playit). Пусто — использовать хост
    /// из адреса сервера (локальная сеть).
    /// </summary>
    public string TcpHost { get; set; } = string.Empty;

    /// <summary>Сессия восстановлена без сервера, по локальному профилю.</summary>
    public bool RestoredOffline { get; set; }

    public static string DefaultServerUrl
    {
        get
        {
#if ANDROID
            // Локальная отладка на эмуляторе — HTTP; на устройстве сервер
            // поднимается по HTTPS с LAN-сертификатом, доверенным приложению.
            return "http://10.0.2.2:5080";
#else
            return "https://localhost:5443";
#endif
        }
    }

    public void SignIn(AuthResponse auth, IdentityKeys identity, string serverUrl)
    {
        User = auth.User;
        Identity = identity;
        Token = auth.Token;
        ServerUrl = NormalizeServerUrl(serverUrl);
        RestoredOffline = false;
    }

    public void SignOut()
    {
        User = null;
        Identity = null;
        Token = null;
    }

    /// <summary>
    /// Приводит адрес сервера к каноническому виду: убирает пробелы и
    /// завершающий слэш, добавляет схему, если пользователь её не указал.
    /// По умолчанию — HTTPS; порт 5080 означает локальный HTTP-отладчик.
    /// </summary>
    public static string NormalizeServerUrl(string value)
    {
        var text = value.Trim().TrimEnd('/');
        if (text.Length == 0)
        {
            return text;
        }

        // Уже есть схема (http:// или https://) — только чистим хвост.
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        // Домен туннеля (не IP): Playit и подобные сервисы пробрасывают HTTPS
        // на стандартный 443, а не на 5443. Если пользователь указал
        // «domain:5443», убираем порт — иначе соединение висит и падает с SSL.
        var hostPart = text.Split(':', 2)[0];
        var isIpHost = System.Net.IPAddress.TryParse(hostPart, out _);
        var isLocal = hostPart.StartsWith("localhost", StringComparison.OrdinalIgnoreCase);

        if (!isIpHost && !isLocal && text.EndsWith(":5443", StringComparison.Ordinal))
        {
            text = hostPart;
        }

        // 5080 — локальный HTTP-профиль сервера, всё остальное — HTTPS.
        var isLocalHttp = text.Contains(":5080", StringComparison.Ordinal) ||
                          isIpHost || isLocal;

        return (isLocalHttp ? "http://" : "https://") + text;
    }

    public static bool IsValidServerUrl(string value) =>
        Uri.TryCreate(NormalizeServerUrl(value), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && !string.IsNullOrWhiteSpace(uri.Host);
}
