using SecureChat.App.Pages;
using SecureChat.App.Services;
using SecureChat.Core.Serialization;
#if WINDOWS
using SecureChat.App.WinUI;
#endif

namespace SecureChat.App;

public partial class App : Application
{
    private const string ThemeKey = "ui.theme";
    private const string AccentKey = "ui.accent";
    private const string FontKey = "ui.fontScale";
    private const string DensityKey = "ui.density";
    private const string NameColorKey = "ui.nameColor";
    private const string MessageFontKey = "ui.msgFont";
    private const string AutoNightKey = "ui.autoNight";
    private const string WallpaperKey = "ui.wallpaper";
    private const string WallpaperDimKey = "ui.wallpaperDim";
    private const string TcpHostKey = "net.tcpHost";
    private const string TcpPortKey = "net.tcpPort";
    private const string TransportKey = "net.transport";
    private const string ProxyKey = "net.proxy";
    private const string CustomThemesKey = "ui.customThemes";
    private const string ActiveCustomThemeKey = "ui.activeCustomTheme";

    /// <summary>Пресеты акцентного цвета в духе Telegram / Discord / Signal.</summary>
    public static readonly (string Name, string Color)[] AccentPresets =
    {
        ("Discord", "#5865F2"),
        ("Telegram", "#0088CC"),
        ("Signal", "#3BA55D"),
        ("Фиолетовый", "#8B5CF6"),
        ("Красный", "#ED4245"),
        ("Оранжевый", "#FAA81A")
    };

    public App()
    {
        InitializeComponent();

        // Словарь локализации — синхронно, без обращения к хранилищу,
        // чтобы не блокировать запуск. Сохранённый язык восстановим позже.
        AppServices.Get<LocalizationService>().InitializeDefault();

        ApplyAppearance();
        _ = LoadAppearanceAsync();
        _ = AppServices.Get<LocalizationService>().RestoreAsync();
        EnsureAutoNightTimer();

        // Звук уведомлений: подписка на уровне приложения, поэтому сигнал
        // слышен на любой странице, а не только в списке чатов.
        AppServices.Get<ChatManager>().IncomingMessage += OnIncomingMessage;
        AppServices.Get<NotificationService>().Activated += OnNotificationActivated;

        // Упоминания @ в пространствах: системное уведомление или звук
        // независимо от того, какие страницы открыты.
        AppServices.Get<SpaceMentionNotifier>();

        // Отложенные посты пространств: таймер проверяет очередь и отправляет
        // посты, чьё время пришло.
        AppServices.Get<SecureChat.App.Scheduling.ScheduledPostService>().Start();
    }

    private static async void OnIncomingMessage(StoredMessage message, string username)
    {
        try
        {
            var session = AppServices.Get<AppSession>();

            // В «беззвучном» чате не показываем ни звук, ни уведомление.
            if (AppServices.Get<ChatManager>().IsConversationMuted(username))
            {
                return;
            }

            var isAndroidBackground =
                DeviceInfo.Platform == DevicePlatform.Android && !AppLifecycle.IsForeground;

            if (isAndroidBackground)
            {
                var preview = session.ShowNotificationPreview
                    ? MessagePreview.Build(message.Text)
                    : "Новое сообщение";

                var unread = 1;
                var conversation = await AppServices.Get<ChatManager>()
                    .GetConversationAsync(username);
                if (conversation is not null)
                {
                    unread = Math.Max(1, conversation.UnreadCount);
                }

                if (AppServices.Get<NotificationService>()
                    .ShowNotification(
                        username,
                        preview,
                        username,
                        unread,
                        silent: !session.SoundsEnabled))
                {
                    // Системное уведомление само проигрывает звук — не дублируем.
                    return;
                }
            }

            if (session.SoundsEnabled)
            {
                AppServices.Get<SoundService>().PlayNotification();
            }
        }
        catch
        {
            // Сбой уведомления не должен влиять на доставку сообщений.
        }
    }

    private static void OnNotificationActivated(string username)
    {
        if (DeviceInfo.Platform == DevicePlatform.WinUI)
        {
            // На Windows навигацией занимается DesktopMainPage.
            return;
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var notifications = AppServices.Get<NotificationService>();
            var shell = Shell.Current;
            if (shell is null)
            {
                notifications.RememberPendingUsername(username);
                return;
            }

            notifications.ConsumePendingUsername();
            await shell.Navigation.PushAsync(new ChatPage(username));
        });
    }

    /// <summary>Применяет оформление: тему, акцентный цвет и масштаб шрифта.
    /// Цвета и размеры подставляются через DynamicResource, поэтому интерфейс
    /// перекрашивается сразу, без перезапуска.</summary>
    /// <param name="extraOverrides">Дополнительные цвета поверх темы — черновик
    /// конструктора оформления (живой предпросмотр).</param>
    /// <param name="baseThemeOverride">Встроенная тема-основа вместо выбранной
    /// пользователем (предпросмотр своей темы).</param>
    public static void ApplyAppearance(
        IReadOnlyDictionary<string, string>? extraOverrides = null,
        string? baseThemeOverride = null)
    {
        var res = Application.Current?.Resources;
        if (res is null)
        {
            return;
        }

        var session = AppServices.Get<AppSession>();
        var palette = ResolvePalette(EffectiveTheme(session, baseThemeOverride));
        session.IsDarkTheme = palette.IsDark;

        res["PageBackground"] = palette.PageBackgroundColor;
        res["Surface"] = palette.SurfaceColor;
        res["SurfaceAlt"] = palette.SurfaceAltColor;
        res["SurfaceHover"] = palette.SurfaceHoverColor;
        res["BorderColor"] = palette.BorderColorValue;
        res["BorderHover"] = palette.BorderHoverColor;
        res["TextPrimary"] = palette.TextPrimaryColor;
        res["TextSecondary"] = palette.TextSecondaryColor;
        res["TextMuted"] = palette.TextMutedColor;
        res["TextDisabled"] = palette.TextDisabledColor;
        res["Muted"] = palette.TextMutedColor;
        res["Success"] = palette.SuccessColor;
        res["SuccessSoft"] = palette.SuccessColor.WithAlpha(0x22);
        res["Danger"] = palette.DangerColor;
        res["DangerSoft"] = palette.DangerColor.WithAlpha(0x22);
        res["Warning"] = palette.WarningColor;
        res["MessageThem"] = palette.MessageThemColor;
        res["MessageThemText"] = palette.MessageThemTextColor;
        res["UnreadDot"] = palette.UnreadDotColor;
        res["OnlineDot"] = palette.OnlineDotColor;
        res["OfflineDot"] = palette.TextDisabledColor;

        ApplyAccent(res, session.Accent, palette);
        ApplyCustomTheme(res, session);
        ApplyColorOverrides(res, extraOverrides);
        ApplyFontScale(res, session.FontScale);
        ApplyDensity(res, session.Density);
        ApplyBubbleShape(res, session.BubbleShape);

        res["ShowAvatars"] = session.ShowAvatarsInChat;
        res["ShowTimestamps"] = session.ShowTimestamps;
        res["SidebarPreviewVisible"] = !session.CompactSidebar;

        // Цвет вашего имени: настройка или акцент темы (в том числе переопределённый
        // в своей теме). Виден в пространствах (имя автора) — как «Your name color»
        // в Telegram.
        res["SenderNameColor"] = string.IsNullOrWhiteSpace(session.NameColor)
            ? (Color)res["Accent"]
            : ParseColor(session.NameColor, palette.Accent);

        // Шрифт текста сообщений (стили MessageTextMe/Them читают через
        // DynamicResource): обычный / с засечками / моноширинный.
        var isWindows = DeviceInfo.Platform == DevicePlatform.WinUI;
        var isAndroid = DeviceInfo.Platform == DevicePlatform.Android;
        res["FontFamilyMessage"] = session.MessageFont switch
        {
            "С засечками" => isWindows ? "Georgia" : isAndroid ? "serif" : "Georgia",
            "Моноширинный" => isWindows ? "Consolas" : isAndroid ? "monospace" : "Menlo",
            _ => isWindows ? "Segoe UI Variable" : isAndroid ? "sans-serif" : "SF Pro Text"
        };

        _autoNightIsNight = session.AutoNightMode &&
            (DateTime.Now.Hour >= 20 || DateTime.Now.Hour < 7);
    }

    private static bool _autoNightIsNight;
    private static System.Timers.Timer? _autoNightTimer;

    /// <summary>Тема с учётом авто-ночного режима: ночью светлая тема
    /// подменяется ночной парой, днём ночная — светлой. «Системная» и
    /// изначально тёмная «Классическая» не трогаются.</summary>
    /// <param name="baseThemeOverride">Тема-основа вместо выбранной пользователем.</param>
    private static string EffectiveTheme(AppSession session, string? baseThemeOverride = null)
    {
        var theme = string.IsNullOrWhiteSpace(baseThemeOverride) ? session.Theme : baseThemeOverride;

        if (!session.AutoNightMode ||
            string.Equals(theme, ThemeCatalog.SystemTheme, StringComparison.OrdinalIgnoreCase))
        {
            return theme;
        }

        var isNight = DateTime.Now.Hour >= 20 || DateTime.Now.Hour < 7;
        return (theme, isNight) switch
        {
            ("Синяя", true) => "Ночная синяя",
            ("Зелёная", true) => "Ночная зелёная",
            ("Фиолетовая", true) => "Ночная фиолетовая",
            ("Ночная синяя", false) => "Синяя",
            ("Ночная зелёная", false) => "Зелёная",
            ("Ночная фиолетовая", false) => "Фиолетовая",
            _ => theme
        };
    }

    /// <summary>Раз в минуту проверяет смену дня/ночи и перекрашивает интерфейс.</summary>
    private static void EnsureAutoNightTimer()
    {
        if (_autoNightTimer is not null)
        {
            return;
        }

        _autoNightTimer = new System.Timers.Timer(60_000)
        {
            AutoReset = true
        };
        _autoNightTimer.Elapsed += (_, _) =>
            MainThread.BeginInvokeOnMainThread(CheckAutoNightTick);
        _autoNightTimer.Start();
    }

    private static void CheckAutoNightTick()
    {
        try
        {
            var session = AppServices.Get<AppSession>();
            if (!session.AutoNightMode)
            {
                return;
            }

            var isNight = DateTime.Now.Hour >= 20 || DateTime.Now.Hour < 7;
            if (isNight == _autoNightIsNight)
            {
                return;
            }

            _autoNightIsNight = isNight;
            ApplyAppearance();
            session.NotifyAppearanceChanged();
        }
        catch
        {
            // Тик авто-ночи не должен ронять приложение.
        }
    }

    /// <summary>Возвращает палитру выбранной темы; «Системная» берёт тему системы.</summary>
    private static ThemePalette ResolvePalette(string? theme)
    {
        if (string.Equals(theme, ThemeCatalog.SystemTheme, StringComparison.OrdinalIgnoreCase))
        {
            var isDark = ResolveDarkTheme(ThemeCatalog.SystemTheme);
            return isDark
                ? ThemeCatalog.Resolve(ThemeCatalog.DefaultTheme)
                : ThemeCatalog.Resolve("Синяя");
        }

        return ThemeCatalog.Resolve(theme);
    }

    /// <summary>Пересобирает стили пузырей под выбранную форму (как в Telegram).</summary>
    private static void ApplyBubbleShape(ResourceDictionary res, string shape)
    {
        var (me, them) = shape switch
        {
            "Прямая" => ("0,0,0,0", "0,0,0,0"),
            "Мягкая" => ("8,8,0,8", "8,8,8,0"),
            _ => ("16,16,0,16", "16,16,16,0")
        };

        res["MessageBubbleMe"] = BuildBubbleStyle(
            (Color)res["MessageMe"],
            me,
            LayoutOptions.End);

        res["MessageBubbleThem"] = BuildBubbleStyle(
            (Color)res["MessageThem"],
            them,
            LayoutOptions.Start);
    }

    private static Style BuildBubbleStyle(Color background, string radius, LayoutOptions alignment)
    {
        var corners = radius
            .Split(',')
            .Select(item => double.TryParse(item, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0d)
            .ToArray();

        var round = corners.Length switch
        {
            1 => new CornerRadius(corners[0]),
            2 => new CornerRadius(corners[0], corners[1], corners[0], corners[1]),
            4 => new CornerRadius(corners[0], corners[1], corners[2], corners[3]),
            _ => new CornerRadius(16)
        };

        var style = new Style(typeof(Border));

        style.Setters.Add(new Setter { Property = Border.BackgroundColorProperty, Value = background });
        style.Setters.Add(new Setter { Property = Border.StrokeThicknessProperty, Value = 0d });
        style.Setters.Add(new Setter
        {
            Property = Border.StrokeShapeProperty,
            Value = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = round }
        });
        style.Setters.Add(new Setter { Property = Border.PaddingProperty, Value = new Thickness(12, 9) });
        style.Setters.Add(new Setter { Property = Border.MaximumWidthRequestProperty, Value = 320d });
        style.Setters.Add(new Setter { Property = Layout.HorizontalOptionsProperty, Value = alignment });

        return style;
    }

    private static void ApplyDensity(ResourceDictionary res, string density)
    {
        (Thickness bubble, Thickness row) = density switch
        {
            "Компактная" => (new Thickness(10, 6), new Thickness(12, 2)),
            "Просторная" => (new Thickness(16, 12), new Thickness(16, 5)),
            _ => (new Thickness(12, 9), new Thickness(14, 3))
        };

        res["BubblePadding"] = bubble;
        res["MessageRowPadding"] = row;
    }

    private static void ApplyAccent(ResourceDictionary res, string accent, ThemePalette palette)
    {
        var color = string.IsNullOrWhiteSpace(accent) || accent == UseThemeAccent
            ? palette.AccentColor
            : ParseColor(accent, palette.Accent);

        res["Accent"] = color;
        res["AccentText"] = Colors.White;
        res["AccentSoft"] = color.WithAlpha(0x22);
        res["SelectionOverlay"] = color.WithAlpha(0x33);
        res["MessageMe"] = color;
    }

    /// <summary>Значение акцента, означающее «использовать акцент темы».</summary>
    public const string UseThemeAccent = "Как в теме";

    /// <summary>Накладывает произвольные цвета (черновик конструктора) поверх
    /// палитры, акцента и активной своей темы. Используется для живого
    /// предпросмотра, пока пользователь подбирает цвета.</summary>
    private static void ApplyColorOverrides(
        ResourceDictionary res,
        IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return;
        }

        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in overrides)
        {
            var key = ThemeColorRoles.NormalizeKey(pair.Key);
            if (key is null || !ThemeColor.TryParse(pair.Value, out var color))
            {
                continue;
            }

            res[key] = color;
            applied.Add(key);
        }

        if (applied.Contains("Accent"))
        {
            var accent = (Color)res["Accent"];
            res["AccentSoft"] = accent.WithAlpha(0x22);
            res["SelectionOverlay"] = accent.WithAlpha(0x33);

            if (!applied.Contains("MessageMe"))
            {
                res["MessageMe"] = accent;
            }
        }

        // Полупрозрачные варианты статусов следуют за своим цветом.
        if (applied.Contains("Success"))
        {
            res["SuccessSoft"] = ((Color)res["Success"]).WithAlpha(0x22);
        }

        if (applied.Contains("Danger"))
        {
            res["DangerSoft"] = ((Color)res["Danger"]).WithAlpha(0x22);
        }

        // Цвета наведения — чуть светлее/темнее своей базы.
        if (applied.Contains("Surface"))
        {
            res["SurfaceHover"] = Shift((Color)res["Surface"], 0.10);
        }

        if (applied.Contains("BorderColor"))
        {
            res["BorderHover"] = Shift((Color)res["BorderColor"], 0.14);
        }
    }

    /// <summary>Сдвигает цвет к светлому или тёмному — для цвета наведения.</summary>
    private static Color Shift(Color color, double amount)
    {
        var luminance = (color.Red * 0.299) + (color.Green * 0.587) + (color.Blue * 0.114);
        var toWhite = luminance < 0.5;

        static float Mix(float channel, float target, double amount) =>
            (float)(channel + ((target - channel) * amount));

        var red = Mix(color.Red, toWhite ? 1f : 0f, amount);
        var green = Mix(color.Green, toWhite ? 1f : 0f, amount);
        var blue = Mix(color.Blue, toWhite ? 1f : 0f, amount);

        return Color.FromRgb(red, green, blue);
    }

    /// <summary>Накладывает цвета активной своей темы поверх палитры и акцента.
    /// Неизвестные ключи игнорируются — так темы из старых версий не ломают
    /// оформление.</summary>
    private static void ApplyCustomTheme(ResourceDictionary res, AppSession session)
    {
        // В конструкторе темы показываем «чистую» тему, а сохранённую свою —
        // только по явному выбору пользователя.
        if (SuppressCustomTheme)
        {
            return;
        }

        var theme = session.ActiveCustomTheme;
        if (theme is null)
        {
            return;
        }

        ApplyColorOverrides(res, theme.Colors);
    }

    /// <summary>Не применять сохранённую свою тему (используется конструктором
    /// оформления для предпросмотра).</summary>
    public static bool SuppressCustomTheme { get; set; }

    private static void ApplyFontScale(ResourceDictionary res, string scale)
    {
        (double body, double caption, double message, double title, double heading) = scale switch
        {
            "Маленький" => (13, 11, 14, 15, 18),
            "Крупный" => (16, 13, 17, 18, 22),
            _ => (14, 12, 15, 16, 20)
        };

        res["FontSizeBody"] = body;
        res["FontSizeCaption"] = caption;
        res["FontSizeMessage"] = message;
        res["FontSizeTitle"] = title;
        res["FontSizeHeading"] = heading;
    }

    private static bool ResolveDarkTheme(string theme)
    {
        if (string.Equals(theme, "Системная", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return AppInfo.Current.RequestedTheme == AppTheme.Dark;
            }
            catch
            {
                return true;
            }
        }

        return !string.Equals(theme, "Светлая", StringComparison.OrdinalIgnoreCase);
    }

    private static Color ParseColor(string value, string fallback)
    {
        try
        {
            return Color.FromArgb(value);
        }
        catch
        {
            return Color.FromArgb(fallback);
        }
    }

    /// <summary>Читает сохранённое оформление и применяет его при старте.</summary>
    public static async Task LoadAppearanceAsync()
    {
        try
        {
            var secrets = AppServices.Get<SecretStore>();
            var session = AppServices.Get<AppSession>();

            var theme = await secrets.GetAsync(ThemeKey);
            var accent = await secrets.GetAsync(AccentKey);
            var font = await secrets.GetAsync(FontKey);
            var density = await secrets.GetAsync(DensityKey);
            var nameColor = await secrets.GetAsync(NameColorKey);
            var messageFont = await secrets.GetAsync(MessageFontKey);
            var autoNight = await secrets.GetAsync(AutoNightKey);
            var wallpaper = await secrets.GetAsync(WallpaperKey);
            var wallpaperDim = await secrets.GetAsync(WallpaperDimKey);
            var customThemes = await secrets.GetAsync(CustomThemesKey);
            var activeCustomTheme = await secrets.GetAsync(ActiveCustomThemeKey);
            var tcpHost = await secrets.GetAsync(TcpHostKey);
            var tcpPort = await secrets.GetAsync(TcpPortKey);
            var transport = await secrets.GetAsync(TransportKey);
            var proxy = await secrets.GetAsync(ProxyKey);

            session.TcpHost = tcpHost ?? string.Empty;
            if (int.TryParse(tcpPort, out var parsedPort) && parsedPort > 0)
            {
                session.TcpPort = parsedPort;
            }

            if (!string.IsNullOrWhiteSpace(transport))
            {
                session.TransportMode = transport;
            }

            session.ProxyUrl = proxy ?? string.Empty;
            AppServices.Get<ApiClient>().SetProxy(session.ProxyUrl);

            if (!string.IsNullOrWhiteSpace(theme))
            {
                session.Theme = theme;
            }

            if (!string.IsNullOrWhiteSpace(accent))
            {
                session.Accent = accent;
            }

            if (!string.IsNullOrWhiteSpace(font))
            {
                session.FontScale = font;
            }

            if (!string.IsNullOrWhiteSpace(density))
            {
                session.Density = density;
            }

            if (!string.IsNullOrWhiteSpace(nameColor))
            {
                session.NameColor = nameColor;
            }

            if (!string.IsNullOrWhiteSpace(messageFont))
            {
                session.MessageFont = messageFont;
            }

            if (!string.IsNullOrWhiteSpace(autoNight))
            {
                session.AutoNightMode = autoNight == "1";
            }

            session.WallpaperPath = wallpaper ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(wallpaperDim))
            {
                session.WallpaperDim = wallpaperDim == "1";
            }

            if (!string.IsNullOrWhiteSpace(customThemes))
            {
                try
                {
                    session.CustomThemes = ChatJson.Deserialize<List<CustomTheme>>(customThemes)
                        ?? new List<CustomTheme>();
                }
                catch
                {
                    session.CustomThemes = new List<CustomTheme>();
                }
            }

            if (!string.IsNullOrWhiteSpace(activeCustomTheme))
            {
                session.ActiveCustomThemeId = activeCustomTheme;
            }

            ApplyAppearance();
        }
        catch
        {
            ApplyAppearance();
        }
    }

    /// <summary>Сохраняет оформление в зашифрованное хранилище устройства.</summary>
    public static async Task SaveAppearanceAsync()
    {
        try
        {
            var secrets = AppServices.Get<SecretStore>();
            var session = AppServices.Get<AppSession>();

            await secrets.SetAsync(ThemeKey, session.Theme);
            await secrets.SetAsync(AccentKey, session.Accent);
            await secrets.SetAsync(FontKey, session.FontScale);
            await secrets.SetAsync(DensityKey, session.Density);
            await secrets.SetAsync(NameColorKey, session.NameColor);
            await secrets.SetAsync(MessageFontKey, session.MessageFont);
            await secrets.SetAsync(AutoNightKey, session.AutoNightMode ? "1" : "0");
            await secrets.SetAsync(WallpaperKey, session.WallpaperPath);
            await secrets.SetAsync(WallpaperDimKey, session.WallpaperDim ? "1" : "0");
            await secrets.SetAsync(CustomThemesKey, ChatJson.Serialize(session.CustomThemes));
            await secrets.SetAsync(ActiveCustomThemeKey, session.ActiveCustomThemeId);
            await secrets.SetAsync(TcpHostKey, session.TcpHost);
            await secrets.SetAsync(TcpPortKey, session.TcpPort.ToString());
            await secrets.SetAsync(TransportKey, session.TransportMode);
            await secrets.SetAsync(ProxyKey, session.ProxyUrl);
        }
        catch
        {
            // Не удалось сохранить оформление — текущий сеанс всё равно применяет его.
        }
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        AppServices.Get<NotificationService>().Initialize();

        var window = new Window(new NavigationPage(AppServices.Get<LoginPage>()))
        {
            Title = "SecureChat"
        };

        // Android дополнительно сообщает о foreground/background из MainActivity.
        window.Activated += (_, _) => AppLifecycle.IsForeground = true;
        window.Deactivated += (_, _) => AppLifecycle.IsForeground = false;
        window.Resumed += (_, _) => AppLifecycle.IsForeground = true;
        window.Stopped += (_, _) => AppLifecycle.IsForeground = false;

        if (DeviceInfo.Platform == DevicePlatform.WinUI)
        {
            window.Width = 1180;
            window.Height = 800;
            window.MinimumWidth = 900;
            window.MinimumHeight = 600;

#if WINDOWS
            window.Created += (_, _) => WindowPlacement.Attach(window);
#endif
        }

        return window;
    }

    /// <summary>Главный экран после входа: двухпанельный на Windows, Shell на Android.</summary>
    public static Page CreateMainPage()
    {
        if (DeviceInfo.Platform == DevicePlatform.WinUI)
        {
            var desktop = new DesktopMainPage();
            NavigationPage.SetHasNavigationBar(desktop, false);
            return new NavigationPage(desktop);
        }

        return new AppShell();
    }

    public static void SetRoot(Page page)
    {
        var app = Current;
        if (app is null || app.Windows.Count == 0)
        {
            return;
        }

        var window = app.Windows[0];
        DisposeRootPages(window.Page);

        window.Page = page;
        window.Title = "SecureChat";
    }

    private static void DisposeRootPages(Page? page)
    {
        if (page is NavigationPage navigation)
        {
            foreach (var item in navigation.Navigation.NavigationStack.ToArray())
            {
                if (item is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            return;
        }

        if (page is IDisposable disposablePage)
        {
            disposablePage.Dispose();
        }
    }
}
