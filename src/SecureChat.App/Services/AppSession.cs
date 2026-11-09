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

    /// <summary>Масштаб шрифта: «Маленький», «Обычный» или «Крупный».</summary>
    public string FontScale { get; set; } = "Обычный";

    /// <summary>Плотность сообщений: «Компактная», «Обычная» или «Просторная».</summary>
    public string Density { get; set; } = "Обычная";

    /// <summary>Форма пузырей: «Скруглённая», «Мягкая» или «Прямая».</summary>
    public string BubbleShape { get; set; } = "Скруглённая";

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

    /// <summary>Генерировать превью ссылок (Open Graph). Внимание: владелец сайта
    /// увидит IP устройства, которое открыло чат.</summary>
    public bool LinkPreviewEnabled { get; set; } = false;

    /// <summary>Заблокированные пользователи (имена пользователей).</summary>
    public List<string> BlockedUsernames { get; set; } = new();

    public bool IsBlocked(string username) =>
        BlockedUsernames.Any(name => string.Equals(name, username, StringComparison.OrdinalIgnoreCase));

    /// <summary>Транспорт подключения: «Авто», «SignalR» или «TCP».</summary>
    public string TransportMode { get; set; } = "Авто";

    /// <summary>Порт TCP-сервера чата.</summary>
    public int TcpPort { get; set; } = 5444;

    /// <summary>Сессия восстановлена без сервера, по локальному профилю.</summary>
    public bool RestoredOffline { get; set; }

    public static string DefaultServerUrl
    {
        get
        {
#if ANDROID
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

    public static string NormalizeServerUrl(string value) =>
        value.Trim().TrimEnd('/');

    public static bool IsValidServerUrl(string value) =>
        Uri.TryCreate(NormalizeServerUrl(value), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
