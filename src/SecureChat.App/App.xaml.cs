using SecureChat.App.Pages;
using SecureChat.App.Services;
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
        ApplyAppearance();
        _ = LoadAppearanceAsync();

        // Звук уведомлений: подписка на уровне приложения, поэтому сигнал
        // слышен на любой странице, а не только в списке чатов.
        AppServices.Get<ChatManager>().IncomingMessage += OnIncomingMessage;
        AppServices.Get<NotificationService>().Activated += OnNotificationActivated;

        // Упоминания @ в пространствах: системное уведомление или звук
        // независимо от того, какие страницы открыты.
        AppServices.Get<SpaceMentionNotifier>();
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
    public static void ApplyAppearance()
    {
        var res = Application.Current?.Resources;
        if (res is null)
        {
            return;
        }

        var session = AppServices.Get<AppSession>();
        var palette = ResolvePalette(session.Theme);
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
        ApplyFontScale(res, session.FontScale);
        ApplyDensity(res, session.Density);
        ApplyBubbleShape(res, session.BubbleShape);

        res["ShowAvatars"] = session.ShowAvatarsInChat;
        res["ShowTimestamps"] = session.ShowTimestamps;
        res["SidebarPreviewVisible"] = !session.CompactSidebar;
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
