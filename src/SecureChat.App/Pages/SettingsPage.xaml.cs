using SecureChat.App.Services;
using System.Collections.ObjectModel;

namespace SecureChat.App.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly AppSession _session = AppServices.Get<AppSession>();
    private readonly AccountService _account = AppServices.Get<AccountService>();
    private readonly ChatManager _chatManager = AppServices.Get<ChatManager>();
    private readonly ApiClient _apiClient = AppServices.Get<ApiClient>();
    private readonly LocalStore _localStore = AppServices.Get<LocalStore>();
    private readonly WallpaperService _wallpapers = AppServices.Get<WallpaperService>();
    private readonly LocalizationService _localization = AppServices.Get<LocalizationService>();

    private CancellationTokenSource? _profileSyncCts;
    private bool _showAllThemes;
    private bool _tcpTunnelInitialized;
    private readonly ObservableCollection<ThemeTileVm> _themeItems = new();

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        UsernameLabel.Text = _session.Username;
        ServerLabel.Text = _session.ServerUrl;
        FingerprintLabel.Text = string.Format(Loc.T("set_fmt_001"), _session.Fingerprint);
        NicknameEntry.Text = _session.Nickname;
        BioEditor.Text = _session.Bio;

        if (!string.IsNullOrEmpty(_session.AvatarBase64))
        {
            AvatarImage.Source = ImageSource.FromStream(() =>
                new MemoryStream(Convert.FromBase64String(_session.AvatarBase64)));
        }

        HideOnlineSwitch.IsToggled = _session.HideOnlineStatus;
        HideReadSwitch.IsToggled = _session.HideReadReceipts;
        TypingSwitch.IsToggled = _session.ShowTypingStatus;
        PreviewSwitch.IsToggled = _session.ShowNotificationPreview;
        SoundsSwitch.IsToggled = _session.SoundsEnabled;
        RingtoneButton.Text = Ringtones.NameOf(_session.RingtoneId);
        ContactsOnlySwitch.IsToggled = _session.AcceptOnlyFromContacts;
        LinkPreviewSwitch.IsToggled = _session.LinkPreviewEnabled;
        UpdateLinkPreviewHint();
        RefreshBlockedList();

        LanguagePicker.Items.Clear();
        foreach (var (code, name) in LocalizationService.Languages)
        {
            LanguagePicker.Items.Add($"{name} ({code})");
        }
        var langIndex = LocalizationService.Languages
            .ToList().FindIndex(item => item.Code == _localization.Language);
        LanguagePicker.SelectedIndex = langIndex < 0 ? 0 : langIndex;

        ThemePicker.Items.Clear();
        foreach (var name in ThemeCatalog.Names)
        {
            ThemePicker.Items.Add(name);
        }

        ThemePicker.SelectedIndex = Array.IndexOf(ThemeCatalog.Names, _session.Theme);
        if (ThemePicker.SelectedIndex < 0)
        {
            ThemePicker.SelectedIndex = 0;
        }

        AutoDownloadPicker.Items.Clear();
        foreach (var option in MediaAutoDownload.Options)
        {
            AutoDownloadPicker.Items.Add(option);
        }

        AutoDownloadPicker.SelectedIndex = Math.Max(
            0,
            Array.IndexOf(MediaAutoDownload.Options, _session.AutoDownloadMedia));

        _ = RefreshMediaCacheAsync();

        FontScalePicker.Items.Clear();
        FontScalePicker.Items.Add(Loc.T("set_074", "Маленький"));
        FontScalePicker.Items.Add(Loc.T("set_097", "Обычный"));
        FontScalePicker.Items.Add(Loc.T("set_070", "Крупный"));
        FontScalePicker.SelectedIndex = _session.FontScale switch
        {
            "Маленький" => 0,
            "Крупный" => 2,
            _ => 1
        };

        BubbleShapePicker.Items.Clear();
        BubbleShapePicker.Items.Add(Loc.T("set_148", "Скруглённая"));
        BubbleShapePicker.Items.Add(Loc.T("set_078", "Мягкая"));
        BubbleShapePicker.Items.Add(Loc.T("set_129", "Прямая"));
        BubbleShapePicker.SelectedIndex = _session.BubbleShape switch
        {
            "Мягкая" => 1,
            "Прямая" => 2,
            _ => 0
        };

        AvatarsSwitch.IsToggled = _session.ShowAvatarsInChat;

        TransportPicker.Items.Clear();
        TransportPicker.Items.Add(Loc.T("set_009", "Авто"));
        TransportPicker.Items.Add("SignalR (WebSocket)");
        TransportPicker.Items.Add("TCP");
        TransportPicker.SelectedIndex = _session.TransportMode switch
        {
            "SignalR" => 1,
            "TCP" => 2,
            _ => 0
        };
        _tcpTunnelInitialized = false;
        TcpHostEntry.Text = _session.TcpHost;
        TcpPortEntry.Text = _session.TcpPort.ToString();
        _tcpTunnelInitialized = true;
        TimestampsSwitch.IsToggled = _session.ShowTimestamps;
        CompactSidebarSwitch.IsToggled = _session.CompactSidebar;

        DensityPicker.Items.Clear();
        DensityPicker.Items.Add(Loc.T("set_065", "Компактная"));
        DensityPicker.Items.Add(Loc.T("set_096", "Обычная"));
        DensityPicker.Items.Add(Loc.T("set_127", "Просторная"));
        DensityPicker.SelectedIndex = _session.Density switch
        {
            "Компактная" => 0,
            "Просторная" => 2,
            _ => 1
        };

        BuildAccentRow();
        AccentNameLabel.Text = string.IsNullOrEmpty(_session.Accent) || _session.Accent == App.UseThemeAccent
            ? Loc.T("set_057", "Как в теме")
            : App.AccentPresets.FirstOrDefault(item => item.Color == _session.Accent).Name is { Length: > 0 } accentName
                ? accentName
                : Loc.T("set_137", "Свой цвет");
        UpdateAccentSelection();
        UpdateThemePreview();
        UpdateCustomThemeLabel();

        BuildNameColorRow();
        UpdateNameColorSelection();

        MessageFontPicker.Items.Clear();
        MessageFontPicker.Items.Add(Loc.T("set_097", "Обычный"));
        MessageFontPicker.Items.Add(Loc.T("set_134", "С засечками"));
        MessageFontPicker.Items.Add(Loc.T("set_077", "Моноширинный"));
        MessageFontPicker.SelectedIndex = _session.MessageFont switch
        {
            "С засечками" => 1,
            "Моноширинный" => 2,
            _ => 0
        };

        AutoNightSwitch.IsToggled = _session.AutoNightMode;
        UpdateWallpaperUI();

        StatusPicker.Items.Clear();
        StatusPicker.Items.Add(Loc.T("set_098", "Онлайн"));
        StatusPicker.Items.Add(Loc.T("set_086", "Неактивен"));
        StatusPicker.Items.Add(Loc.T("set_042", "Занят"));
        StatusPicker.SelectedIndex = _session.StatusMessage switch
        {
            "Неактивен" => 1,
            "Занят" => 2,
            _ => 0
        };
    }

    /// <summary>Показывает активную свою тему (или подсказку, что своих тем нет).</summary>
    private void UpdateCustomThemeLabel()
    {
        var active = _session.ActiveCustomTheme;
        CustomThemeLabel.Text = active is null
            ? Loc.T("set_136", "Своих тем пока нет — соберите палитру в конструкторе")
            : string.Format(Loc.T("set_fmt_002"), active.Name, active.BaseTheme);

        BuildCustomThemesRow();
    }

    /// <summary>Список своих тем в настройках: применить или отключить.</summary>
    private void BuildCustomThemesRow()
    {
        CustomThemesLayout.Children.Clear();

        foreach (var theme in _session.CustomThemes)
        {
            var isActive = string.Equals(theme.Id, _session.ActiveCustomThemeId, StringComparison.OrdinalIgnoreCase);

            var dots = new HorizontalStackLayout { Spacing = 4 };
            foreach (var key in new[] { "PageBackground", "Surface", "Accent", "MessageMe", "MessageThem" })
            {
                if (!theme.Colors.TryGetValue(key, out var hex) || !ThemeColor.TryParse(hex, out var color))
                {
                    continue;
                }

                dots.Children.Add(new Border
                {
                    BackgroundColor = color,
                    WidthRequest = 16,
                    HeightRequest = 16,
                    StrokeThickness = 1,
                    Stroke = (Color)Application.Current!.Resources["BorderColor"],
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 }
                });
            }

            var button = new Button
            {
                Text = isActive ? $"{theme.Name} ✓" : theme.Name,
                Style = (Style)Application.Current!.Resources["SecondaryButton"],
                CommandParameter = theme.Id
            };
            button.Clicked += OnCustomThemeClicked;

            CustomThemesLayout.Children.Add(new HorizontalStackLayout
            {
                Spacing = 8,
                Children = { dots, button }
            });
        }
    }

    /// <summary>Применяет свою тему или снимает её (повторное нажатие).</summary>
    private async void OnCustomThemeClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string id })
        {
            return;
        }

        var theme = _session.CustomThemes.FirstOrDefault(item => item.Id == id);
        if (theme is null)
        {
            return;
        }

        if (string.Equals(_session.ActiveCustomThemeId, id, StringComparison.OrdinalIgnoreCase))
        {
            // Повторное нажатие — вернуться к встроенной теме.
            _session.ActiveCustomThemeId = string.Empty;
        }
        else
        {
            _session.ActiveCustomThemeId = id;
            if (ThemeCatalog.Resolve(theme.BaseTheme).Name == theme.BaseTheme)
            {
                _session.Theme = theme.BaseTheme;
            }
        }

        App.ApplyAppearance();
        UpdateThemePreview();
        UpdateCustomThemeLabel();
        _session.NotifyAppearanceChanged();
        await App.SaveAppearanceAsync();
    }

    /// <summary>Открывает конструктор оформления: свои цвета и свои темы.</summary>
    private async void OnThemeEditorClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new ThemeEditorPage());
        UpdateCustomThemeLabel();
        UpdateThemePreview();
    }

    /// <summary>Сохраняет адрес TCP-туннеля (хост и порт) без переподключения.</summary>
    private async void OnTcpTunnelChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_tcpTunnelInitialized)
        {
            return;
        }

        _session.TcpHost = TcpHostEntry.Text?.Trim() ?? string.Empty;
        if (int.TryParse(TcpPortEntry.Text?.Trim(), out var port) && port is > 0 and < 65536)
        {
            _session.TcpPort = port;
        }

        await App.SaveAppearanceAsync();
    }

    private async void OnLanguageChanged(object sender, EventArgs e)
    {
        if (LanguagePicker.SelectedIndex < 0 ||
            LanguagePicker.SelectedIndex >= LocalizationService.Languages.Count)
        {
            return;
        }

        var code = LocalizationService.Languages[LanguagePicker.SelectedIndex].Code;
        await _localization.SetLanguageAsync(code);
    }

    private async void OnFontScaleChanged(object sender, EventArgs e)
    {
        _session.FontScale = FontScalePicker.SelectedIndex switch
        {
            0 => Loc.T("set_074", "Маленький"),
            2 => Loc.T("set_070", "Крупный"),
            _ => Loc.T("set_097", "Обычный")
        };

        App.ApplyAppearance();
        await App.SaveAppearanceAsync();
    }

    private async void OnBubbleShapeChanged(object sender, EventArgs e)
    {
        _session.BubbleShape = BubbleShapePicker.SelectedIndex switch
        {
            1 => Loc.T("set_078", "Мягкая"),
            2 => Loc.T("set_129", "Прямая"),
            _ => Loc.T("set_148", "Скруглённая")
        };

        App.ApplyAppearance();
        _session.NotifyAppearanceChanged();
        await App.SaveAppearanceAsync();
    }

    private async void OnAvatarsToggled(object sender, ToggledEventArgs e)
    {
        _session.ShowAvatarsInChat = e.Value;
        App.ApplyAppearance();
        _session.NotifyAppearanceChanged();
        await App.SaveAppearanceAsync();
    }

    private async void OnTimestampsToggled(object sender, ToggledEventArgs e)
    {
        _session.ShowTimestamps = e.Value;
        App.ApplyAppearance();
        _session.NotifyAppearanceChanged();
        await App.SaveAppearanceAsync();
    }

    private async void OnCompactSidebarToggled(object sender, ToggledEventArgs e)
    {
        _session.CompactSidebar = e.Value;
        App.ApplyAppearance();
        _session.NotifyAppearanceChanged();
        await App.SaveAppearanceAsync();
    }

    private async void OnDensityChanged(object sender, EventArgs e)
    {
        _session.Density = DensityPicker.SelectedIndex switch
        {
            0 => Loc.T("set_065", "Компактная"),
            2 => Loc.T("set_127", "Просторная"),
            _ => Loc.T("set_096", "Обычная")
        };

        App.ApplyAppearance();
        await App.SaveAppearanceAsync();
    }

    private void BuildAccentRow()
    {
        AccentRow.Children.Clear();

        foreach (var preset in App.AccentPresets)
        {
            var button = new Button
            {
                CommandParameter = preset.Color,
                BackgroundColor = Color.FromArgb(preset.Color),
                CornerRadius = 20,
                WidthRequest = 40,
                HeightRequest = 40,
                Padding = 0,
                BorderWidth = 0
            };

            button.Clicked += OnAccentClicked;
            AccentRow.Children.Add(button);
        }

        // «Как в теме» — отдельная кнопка-образец.
        var themeButton = new Button
        {
            CommandParameter = App.UseThemeAccent,
            Text = "◐",
            FontSize = 16,
            TextColor = Colors.White,
            BackgroundColor = (Color)Application.Current!.Resources["Accent"],
            CornerRadius = 20,
            WidthRequest = 40,
            HeightRequest = 40,
            Padding = 0,
            BorderWidth = 0
        };

        themeButton.Clicked += OnAccentClicked;
        AccentRow.Children.Add(themeButton);
    }

    // ============================== ТЕМЫ-ПЛИТКИ ==============================

    /// <summary>Базовые темы первого ряда, как в Telegram: Classic / Day / Tinted / Night.</summary>
    private static readonly string[] CoreThemes =
    {
        ThemeCatalog.DefaultTheme, // Классическая
        Loc.T("set_144", "Синяя"),                   // День
        Loc.T("set_107", "Оттенок"),                 // Tinted
        Loc.T("set_092", "Ночная синяя")             // Ночь
    };

    private void OnShowAllThemesClicked(object? sender, EventArgs e)
    {
        _showAllThemes = !_showAllThemes;
        UpdateThemePreview();
        ShowAllThemesButton.Text = _showAllThemes ? Loc.T("set_149", "Скрыть дополнительные темы") : Loc.T("set_117", "Показать все темы");
    }

    /// <summary>Выбор темы в плитках: применяем оформление и сохраняем его.</summary>
    private async void OnThemeSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not ThemeTileVm tile)
        {
            return;
        }

        if (string.Equals(_session.Theme, tile.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _session.Theme = tile.Name;

        // Встроенная тема снимает свою тему — иначе переключение не было бы видно.
        if (_session.ActiveCustomThemeId.Length > 0)
        {
            _session.ActiveCustomThemeId = string.Empty;
        }

        App.ApplyAppearance();
        UpdateThemeSelection();
        UpdateCustomThemeLabel();
        _session.NotifyAppearanceChanged();
        await App.SaveAppearanceAsync();
    }

    /// <summary>Рисует плитки тем в духе Telegram: фон страницы темы, два
    /// «пузыря» превью и кружок выбора под ними.</summary>
    private void UpdateThemePreview()
    {
        _themeItems.Clear();
        var names = _showAllThemes
            ? ThemeCatalog.Themes.Select(item => item.Name).ToList()
            : CoreThemes.ToList();

        foreach (var name in names)
        {
            _themeItems.Add(new ThemeTileVm(name));
        }

        ThemeCollectionView.ItemsSource = null;
        ThemeCollectionView.ItemsSource = _themeItems;
        UpdateThemeSelection();
    }

    /// <summary>Отмечает выбранную тему: обводка плитки и кружок-радиокнопка.</summary>
    private void UpdateThemeSelection()
    {
        foreach (var tile in _themeItems)
        {
            tile.IsSelected = string.Equals(tile.Name, _session.Theme, StringComparison.OrdinalIgnoreCase);
        }

        var selected = _themeItems.FirstOrDefault(tile => tile.IsSelected);
        if (selected is not null && !ReferenceEquals(ThemeCollectionView.SelectedItem, selected))
        {
            ThemeCollectionView.SelectedItem = selected;
        }
    }

    private async void OnThemeChanged(object sender, EventArgs e)
    {
        _session.Theme = ThemePicker.SelectedIndex >= 0 &&
            ThemePicker.SelectedIndex < ThemeCatalog.Themes.Length
            ? ThemeCatalog.Themes[ThemePicker.SelectedIndex].Name
            : ThemeCatalog.SystemTheme;

        _session.ActiveCustomThemeId = string.Empty;

        App.ApplyAppearance();
        UpdateThemePreview();
        UpdateCustomThemeLabel();
        _session.NotifyAppearanceChanged();
        await App.SaveAppearanceAsync();
    }

    private async void OnAccentClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string accent })
        {
            return;
        }

        _session.Accent = accent;
        AccentNameLabel.Text = accent == App.UseThemeAccent
            ? Loc.T("set_057", "Как в теме")
            : App.AccentPresets.FirstOrDefault(item => item.Color == accent).Name is { Length: > 0 } name
                ? name
                : Loc.T("set_137", "Свой цвет");

        App.ApplyAppearance();
        UpdateAccentSelection();
        _session.NotifyAppearanceChanged();
        await App.SaveAppearanceAsync();
    }

    /// <summary>Открывает HSV-пикер для акцентного цвета.</summary>
    private async void OnAccentPickerClicked(object? sender, EventArgs e)
    {
        var current = (Color)Application.Current!.Resources["Accent"];
        var picker = new ColorPickerPage(current, Loc.T("set_014", "Акцентный цвет"));
        await Navigation.PushModalAsync(picker);

        if (picker.SelectedColor is not { } chosen)
        {
            return;
        }

        _session.Accent = ThemeColor.ToHex(chosen);
        AccentNameLabel.Text = Loc.T("set_137", "Свой цвет");

        App.ApplyAppearance();
        UpdateAccentSelection();
        _session.NotifyAppearanceChanged();
        await App.SaveAppearanceAsync();
    }

    private void UpdateAccentSelection()
    {
        foreach (var view in AccentRow.Children)
        {
            if (view is Button button && button.CommandParameter is string hex)
            {
                var selected = string.Equals(hex, _session.Accent, StringComparison.OrdinalIgnoreCase);
                button.BorderColor = selected
                    ? (Color)Application.Current!.Resources["TextPrimary"]
                    : Colors.Transparent;
                button.BorderWidth = selected ? 3 : 0;
            }
        }
    }

    // ============================== ЦВЕТ ВАШЕГО ИМЕНИ ==============================

    /// <summary>Пресеты цвета имени в духе Telegram.</summary>
    private static readonly (string Name, string Hex)[] NameColorPresets =
    {
        (Loc.T("set_069", "Красный"), "#E17076"),
        (Loc.T("set_099", "Оранжевый"), "#F2A33C"),
        (Loc.T("set_173", "Фиолетовый"), "#A695E7"),
        (Loc.T("set_047", "Зелёный"), "#7BC862"),
        (Loc.T("set_017", "Бирюзовый"), "#6EC9CB"),
        (Loc.T("set_143", "Синий"), "#65AADD"),
        (Loc.T("set_133", "Розовый"), "#EE7AAE")
    };

    private void BuildNameColorRow()
    {
        NameColorRow.Children.Clear();

        foreach (var preset in NameColorPresets)
        {
            var button = new Button
            {
                CommandParameter = preset.Hex,
                BackgroundColor = Color.FromArgb(preset.Hex),
                CornerRadius = 18,
                WidthRequest = 36,
                HeightRequest = 36,
                Padding = 0,
                BorderWidth = 0
            };
            button.Clicked += OnNameColorClicked;
            NameColorRow.Children.Add(button);
        }

        // «Как в теме» — сброс на акцент темы.
        var themeButton = new Button
        {
            CommandParameter = string.Empty,
            Text = "◐",
            FontSize = 15,
            TextColor = Colors.White,
            BackgroundColor = (Color)Application.Current!.Resources["Accent"],
            CornerRadius = 18,
            WidthRequest = 36,
            HeightRequest = 36,
            Padding = 0,
            BorderWidth = 0
        };
        themeButton.Clicked += OnNameColorClicked;
        NameColorRow.Children.Add(themeButton);
    }

    private async void OnNameColorClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string hex })
        {
            return;
        }

        _session.NameColor = hex;
        App.ApplyAppearance();
        UpdateNameColorSelection();
        _session.NotifyAppearanceChanged();
        await App.SaveAppearanceAsync();
    }

    private void UpdateNameColorSelection()
    {
        foreach (var view in NameColorRow.Children)
        {
            if (view is Button button && button.CommandParameter is string hex)
            {
                var selected = string.Equals(hex, _session.NameColor, StringComparison.OrdinalIgnoreCase);
                button.BorderColor = selected
                    ? (Color)Application.Current!.Resources["TextPrimary"]
                    : Colors.Transparent;
                button.BorderWidth = selected ? 3 : 0;
            }
        }
    }

    // ============================== АВТО-НОЧЬ И ШРИФТ ==============================

    private async void OnAutoNightToggled(object sender, ToggledEventArgs e)
    {
        _session.AutoNightMode = e.Value;
        App.ApplyAppearance();
        UpdateThemePreview();
        _session.NotifyAppearanceChanged();
        await App.SaveAppearanceAsync();
    }

    private async void OnMessageFontChanged(object? sender, EventArgs e)
    {
        _session.MessageFont = MessageFontPicker.SelectedIndex switch
        {
            1 => Loc.T("set_134", "С засечками"),
            2 => Loc.T("set_077", "Моноширинный"),
            _ => Loc.T("set_097", "Обычный")
        };

        App.ApplyAppearance();
        await App.SaveAppearanceAsync();
    }

    // ============================== ОБОИ ЧАТА ==============================

    private void UpdateWallpaperUI()
    {
        var path = _session.WallpaperPath;
        var hasWallpaper = !string.IsNullOrWhiteSpace(path) && File.Exists(path);

        WallpaperPreviewImage.Source = hasWallpaper ? ImageSource.FromFile(path) : null;
        WallpaperEmptyLabel.IsVisible = !hasWallpaper;
        RemoveWallpaperButton.IsVisible = hasWallpaper;
        WallpaperDimSwitch.IsToggled = _session.WallpaperDim;
    }

    private async void OnWallpaperGalleryClicked(object? sender, EventArgs e)
    {
        if (await _wallpapers.PickFromGalleryAsync())
        {
            UpdateWallpaperUI();
        }
    }

    private async void OnWallpaperFileClicked(object? sender, EventArgs e)
    {
        if (await _wallpapers.PickFromFileAsync())
        {
            UpdateWallpaperUI();
        }
    }

    private async void OnWallpaperRemoveClicked(object? sender, EventArgs e)
    {
        await _wallpapers.RemoveAsync();
        UpdateWallpaperUI();
    }

    private async void OnWallpaperDimToggled(object sender, ToggledEventArgs e)
    {
        _session.WallpaperDim = e.Value;
        _session.NotifyAppearanceChanged();
        await App.SaveAppearanceAsync();
    }

    /// <summary>Показывает, сколько места занимают локальные копии вложений.</summary>
    private async Task RefreshMediaCacheAsync()
    {
        try
        {
            var (bytes, files) = await _localStore.GetMediaCacheInfoAsync();
            MediaCacheLabel.Text = files == 0
                ? Loc.T("set_027", "Вложения не загружены — места не занимает.")
                : string.Format(Loc.T("set_fmt_003"), FormatBytes(bytes), files);
            ClearMediaCacheButton.IsEnabled = files > 0;
        }
        catch
        {
            MediaCacheLabel.Text = Loc.T("set_082", "Не удалось посчитать размер кэша.");
        }
    }

    /// <summary>Удаляет локальные копии вложений: переписка остаётся.</summary>
    private async void OnClearMediaCacheClicked(object? sender, EventArgs e)
    {
        var confirmed = await DisplayAlert(
            Loc.T("set_100", "Освободить место"),
            Loc.T("set_072", "Локальные копии вложений будут удалены. Переписка и файлы на сервере останутся — вложения снова скачаются, когда их откроют."),
            Loc.T("set_108", "Очистить"), Loc.T("set_102", "Отмена"));
        if (!confirmed)
        {
            return;
        }

        var freed = await _localStore.ClearMediaCacheAsync();
        await RefreshMediaCacheAsync();

        await DisplayAlert(
            Loc.T("set_036", "Готово"),
            freed > 0
                ? string.Format(Loc.T("set_fmt_004"), FormatBytes(freed))
                : Loc.T("set_073", "Локальных копий вложений не было."),
            "OK");
    }

    /// <summary>Режим автозагрузки вложений.</summary>
    private async void OnAutoDownloadChanged(object? sender, EventArgs e)
    {
        var index = AutoDownloadPicker.SelectedIndex;
        if (index < 0 || index >= MediaAutoDownload.Options.Length)
        {
            return;
        }

        _session.AutoDownloadMedia = MediaAutoDownload.Options[index];
        await SavePrivacyAsync();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        <= 0 => Loc.T("set_000", "0 Б"),
        < 1024 => string.Format(Loc.T("set_fmt_005"), bytes),
        < 1024 * 1024 => string.Format(Loc.T("set_fmt_006"), bytes / 1024.0),
        _ => string.Format(Loc.T("set_fmt_007"), bytes / (1024.0 * 1024))
    };

    private void OnNicknameChanged(object sender, TextChangedEventArgs e)
    {
        _session.Nickname = e.NewTextValue ?? string.Empty;
        ScheduleProfileSync();
    }

    private void OnBioChanged(object sender, TextChangedEventArgs e)
    {
        _session.Bio = e.NewTextValue ?? string.Empty;
        ScheduleProfileSync();
    }

    /// <summary>Отправляет публичный профиль на сервер с задержкой, чтобы не слать
    /// запрос на каждый символ.</summary>
    private void ScheduleProfileSync()
    {
        _profileSyncCts?.Cancel();
        _profileSyncCts = new CancellationTokenSource();
        var token = _profileSyncCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(800, token);
                await SyncProfileAsync();
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // Синхронизация профиля не критична для работы чата.
            }
        });
    }

    private async Task SyncProfileAsync()
    {
        if (!_session.IsAuthenticated || string.IsNullOrEmpty(_session.Token))
        {
            return;
        }

        try
        {
            await _apiClient.UpdateProfileAsync(
                _session.ServerUrl,
                _session.Token!,
                _session.Nickname,
                _session.Bio,
                _session.AvatarBase64 ?? string.Empty,
                _session.StatusMessage);
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("set_016", "Ошибка"),
                string.Format(Loc.T("set_fmt_009", "Не удалось сохранить: {0}"), ex.Message), "OK");
        }
    }
    private async void OnAvatarClicked(object sender, EventArgs e)
    {
        try
        {
            var file = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = Loc.T("set_029", "Выберите аватар"),
                FileTypes = FilePickerFileType.Images
            });

            if (file is null) return;

            using var stream = await file.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var bytes = ms.ToArray();

            _session.AvatarBase64 = Convert.ToBase64String(bytes);
            AvatarImage.Source = ImageSource.FromStream(() => new MemoryStream(bytes));
            await SyncProfileAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("set_109"), string.Format(Loc.T("set_fmt_009"), ex.Message), "OK");
        }
    }

    private void OnStatusChanged(object sender, EventArgs e)
    {
        _session.StatusMessage = StatusPicker.SelectedIndex switch
        {
            1 => Loc.T("set_086", "Неактивен"),
            2 => Loc.T("set_042", "Занят"),
            _ => Loc.T("set_098", "Онлайн")
        };

        _ = SyncProfileAsync();
    }

    private async void OnCheckServerClicked(object sender, EventArgs e)
    {
        try
        {
            var serverTime = await _apiClient.GetServerTimeAsync(_session.ServerUrl);
            await DisplayAlert(
                Loc.T("set_139", "Сервер доступен"),
                serverTime is null
                    ? Loc.T("set_141", "Сервер ответил, но время получить не удалось.")
                    : string.Format(Loc.T("set_fmt_008"), serverTime),
                "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("set_140", "Сервер недоступен"), ex.Message, "OK");
        }
    }

    private async void OnLogoutClicked(object sender, EventArgs e)
    {
        await _chatManager.ShutdownAsync();
        await _account.LogoutAsync();
        App.SetRoot(new NavigationPage(AppServices.Get<LoginPage>()));
    }

    private async void OnChangeAccountPasswordClicked(object sender, EventArgs e)
    {
        var current = await PasswordPromptPage.ShowAsync(
            Navigation,
            Loc.T("set_165", "Текущий пароль"),
            Loc.T("set_023", "Введите пароль, с которым вы входите в аккаунт."));
        if (string.IsNullOrEmpty(current))
        {
            return;
        }

        var next = await PasswordPromptPage.ShowAsync(
            Navigation,
            Loc.T("set_090", "Новый пароль"),
            Loc.T("set_079", "Не менее 8 символов. Он понадобится при следующем входе."));
        if (string.IsNullOrEmpty(next))
        {
            return;
        }

        var repeat = await PasswordPromptPage.ShowAsync(
            Navigation,
            Loc.T("set_114", "Повторите пароль"),
            Loc.T("set_021", "Введите новый пароль ещё раз."),
            Loc.T("set_152", "Сменить пароль"));
        if (repeat is null)
        {
            return;
        }

        if (!string.Equals(next, repeat, StringComparison.Ordinal))
        {
            await DisplayAlert(Loc.T("set_110", "Пароли не совпадают"), Loc.T("set_091", "Новый пароль введён по-разному."), "OK");
            return;
        }

        try
        {
            await _account.ChangePasswordAsync(current, next);
            await DisplayAlert(
                Loc.T("set_111", "Пароль изменён"),
                Loc.T("set_053", "Используйте новый пароль при следующем входе. Текущая сессия остаётся активной."),
                "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("set_083", "Не удалось сменить пароль"), ex.Message, "OK");
        }
    }

    private async void OnDeleteClicked(object sender, EventArgs e)
    {
        var confirmed = await DisplayAlert(
            Loc.T("set_170", "Удалить аккаунт навсегда?"),
            "С сервера будут удалены аккаунт, переписка и вложения. Локальная история " +
            Loc.T("set_180", "и ключи устройства тоже будут стёрты. Восстановить данные невозможно."),
            Loc.T("set_171", "Удалить навсегда"),
            Loc.T("set_102", "Отмена"));

        if (!confirmed)
        {
            return;
        }

        var password = await PasswordPromptPage.ShowAsync(
            Navigation,
            Loc.T("set_116", "Подтвердите удаление"),
            Loc.T("set_022", "Введите пароль аккаунта. Это действие необратимо."),
            Loc.T("set_168", "Удалить аккаунт"));
        if (string.IsNullOrEmpty(password))
        {
            return;
        }

        try
        {
            await _account.DeleteAccountAsync(password);
            await _chatManager.ShutdownAsync();
            App.SetRoot(new NavigationPage(AppServices.Get<LoginPage>()));
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("set_084", "Не удалось удалить аккаунт"), ex.Message, "OK");
        }
    }

    // ============================================================
    // БЕЗОПАСНОСТЬ И ПРИВАТНОСТЬ
    // ============================================================

    private async void OnShowFingerprintClicked(object sender, EventArgs e)
    {
        var fingerprint = _session.Fingerprint;
        if (string.IsNullOrEmpty(fingerprint))
        {
            await DisplayAlert(Loc.T("set_016", "Безопасность"), Loc.T("set_105", "Отпечаток ключа недоступен."), "OK");
            return;
        }

        await Navigation.PushAsync(new FingerprintQrPage(fingerprint));

        // В реальном приложении здесь можно скопировать в буфер обмена
    }

    private async void OnHideOnlineToggled(object sender, ToggledEventArgs e)
    {
        _session.HideOnlineStatus = e.Value;
        await SavePrivacyAsync();
        await _chatManager.SetPresenceHiddenAsync(e.Value);
    }

    private async void OnHideReadToggled(object sender, ToggledEventArgs e)
    {
        _session.HideReadReceipts = e.Value;
        await SavePrivacyAsync();
    }

    private async void OnTypingToggled(object sender, ToggledEventArgs e)
    {
        _session.ShowTypingStatus = e.Value;
        await SavePrivacyAsync();
    }

    private async void OnPreviewToggled(object sender, ToggledEventArgs e)
    {
        _session.ShowNotificationPreview = e.Value;
        await SavePrivacyAsync();
    }

    private async void OnSoundsToggled(object sender, ToggledEventArgs e)
    {
        // При открытии страницы значение выставляется программно — не играем звук.
        var changed = e.Value != _session.SoundsEnabled;
        _session.SoundsEnabled = e.Value;
        await SavePrivacyAsync();

        if (changed && e.Value)
        {
            AppServices.Get<SoundService>().PlayNotification();
        }
    }

    private async void OnRingtoneClicked(object sender, EventArgs e)
    {
        var options = Ringtones.All.Select(item => item.Name).ToArray();
        var picked = await DisplayActionSheet(
            Loc.T("set_132", "Рингтон уведомлений"),
            Loc.T("set_102", "Отмена"),
            null,
            options);

        if (string.IsNullOrEmpty(picked) || picked == Loc.T("set_102", "Отмена"))
        {
            return;
        }

        var option = Ringtones.All.FirstOrDefault(item => item.Name == picked);
        if (option is null)
        {
            return;
        }

        _session.RingtoneId = option.Id;
        RingtoneButton.Text = option.Name;
        await SavePrivacyAsync();

        // У Android звук канала неизменяем: при смене тона пере-создаём канал,
        // чтобы фоновые уведомления тоже звучали выбранным рингтоном.
        AppServices.Get<NotificationService>().Initialize();

        // Прослушать выбранный тон сразу.
        AppServices.Get<SoundService>().Play(option.Id);
    }

    private async void OnContactsOnlyToggled(object sender, ToggledEventArgs e)
    {
        _session.AcceptOnlyFromContacts = e.Value;
        await SavePrivacyAsync();
    }

    private async void OnLinkPreviewToggled(object sender, ToggledEventArgs e)
    {
        _session.LinkPreviewEnabled = e.Value;
        await SavePrivacyAsync();
        UpdateLinkPreviewHint();
    }

    /// <summary>Подпись под переключателем превью: что делает опция сейчас.</summary>
    private void UpdateLinkPreviewHint()
    {
        LinkPreviewHintLabel.Text = _session.LinkPreviewEnabled
            ? Loc.T("set_026", "Включено — чат сам запросит заголовок и картинку ссылки у сайта; владелец сайта увидит ваш IP")
            : Loc.T("set_034", "Выключено — чат не заходит на сайты ссылок и не показывает карточки");
    }

    private async void OnBlockClicked(object sender, EventArgs e)
    {
        var name = BlockEntry.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return;
        }

        if (string.Equals(name, _session.Username, StringComparison.OrdinalIgnoreCase))
        {
            await DisplayAlert(Loc.T("set_018", "Блокировка"), Loc.T("set_088", "Нельзя заблокировать самого себя."), "OK");
            return;
        }

        if (!_session.IsBlocked(name))
        {
            _session.BlockedUsernames.Add(name);
            await SavePrivacyAsync();
        }

        BlockEntry.Text = string.Empty;
        RefreshBlockedList();
    }

    private async void OnUnblockClicked(object sender, EventArgs e)
    {
        if (BlockedPicker.SelectedItem is not string name)
        {
            return;
        }

        _session.BlockedUsernames.RemoveAll(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        await SavePrivacyAsync();
        RefreshBlockedList();
    }

    private void RefreshBlockedList()
    {
        BlockedPicker.Items.Clear();
        foreach (var name in _session.BlockedUsernames)
        {
            BlockedPicker.Items.Add(name);
        }

        BlockedPicker.Title = _session.BlockedUsernames.Count == 0
            ? Loc.T("set_160", "Список пуст")
            : Loc.T("set_030", "Выберите пользователя");
    }

    private Task SavePrivacyAsync() =>
        PrivacyStore.SaveAsync(_session, AppServices.Get<SecretStore>());

    private async void OnChangePasswordClicked(object sender, EventArgs e)
    {
        var secrets = AppServices.Get<SecretStore>();
        var hasPasscode = await PrivacyStore.HasPasscodeAsync(secrets);
        var choice = await DisplayActionSheet(
            Loc.T("set_062", "Код-пароль приложения"),
            Loc.T("set_102", "Отмена"),
            null,
            hasPasscode ? new[] { Loc.T("set_050", "Изменить код"), Loc.T("set_035", "Выключить код") } : new[] { Loc.T("set_172", "Установить код") });

        if (string.IsNullOrEmpty(choice) || choice == Loc.T("set_102", "Отмена"))
        {
            return;
        }

        if (hasPasscode)
        {
            var current = await DisplayPromptAsync(
                Loc.T("set_164", "Текущий код"),
                Loc.T("set_024", "Введите текущий код-пароль:"),
                Loc.T("set_037", "Далее"),
                Loc.T("set_102", "Отмена"),
                maxLength: 32,
                keyboard: Keyboard.Text);

            if (current is null)
            {
                return;
            }

            if (!await PrivacyStore.VerifyPasscodeAsync(secrets, current))
            {
                await DisplayAlert(Loc.T("set_060", "Код-пароль"), Loc.T("set_087", "Неверный код-пароль."), "OK");
                return;
            }
        }

        if (choice == Loc.T("set_035", "Выключить код"))
        {
            await PrivacyStore.RemovePasscodeAsync(secrets);
            await DisplayAlert(Loc.T("set_060", "Код-пароль"), Loc.T("set_061", "Код-пароль выключен."), "OK");
            return;
        }

        var newCode = await DisplayPromptAsync(
            Loc.T("set_089", "Новый код-пароль"),
            Loc.T("set_076", "Минимум 4 символа:"),
            Loc.T("set_037", "Далее"),
            Loc.T("set_102", "Отмена"),
            maxLength: 32,
            keyboard: Keyboard.Text);

        if (string.IsNullOrEmpty(newCode))
        {
            return;
        }

        if (newCode.Length < 4)
        {
            await DisplayAlert(Loc.T("set_060", "Код-пароль"), Loc.T("set_059", "Код должен содержать не менее 4 символов."), "OK");
            return;
        }

        var repeat = await DisplayPromptAsync(
            Loc.T("set_113", "Повторите код"),
            Loc.T("set_020", "Введите новый код ещё раз:"),
            "Сохранить",
            Loc.T("set_102", "Отмена"),
            maxLength: 32,
            keyboard: Keyboard.Text);

        if (!string.Equals(repeat, newCode, StringComparison.Ordinal))
        {
            await DisplayAlert(Loc.T("set_060", "Код-пароль"), Loc.T("set_064", "Коды не совпадают."), "OK");
            return;
        }

        await PrivacyStore.SetPasscodeAsync(secrets, newCode);
        await DisplayAlert(Loc.T("set_060", "Код-пароль"), Loc.T("set_063", "Код-пароль сохранён. Он будет запрашиваться при запуске."), "OK");
    }

    private async void OnExportHistoryClicked(object sender, EventArgs e)
    {
        try
        {
            var data = await _localStore.ExportHistoryAsync();
            var base64 = Convert.ToBase64String(data);

            await Clipboard.Default.SetTextAsync(base64);
            await DisplayAlert(Loc.T("set_054", "История"), Loc.T("set_056", "История переписки скопирована в буфер обмена (base64)."), "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("set_109"), string.Format(Loc.T("set_fmt_010"), ex.Message), "OK");
        }
    }

    private async void OnImportHistoryClicked(object sender, EventArgs e)
    {
        try
        {
            var base64 = await Clipboard.Default.GetTextAsync();
            if (string.IsNullOrWhiteSpace(base64))
            {
                await DisplayAlert(Loc.T("set_054", "История"), Loc.T("set_019", "Буфер обмена пуст."), "OK");
                return;
            }

            var data = Convert.FromBase64String(base64);
            await _localStore.ImportHistoryAsync(data);
            await DisplayAlert(Loc.T("set_054", "История"), Loc.T("set_055", "История переписки импортирована из буфера обмена."), "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("set_109"), string.Format(Loc.T("set_fmt_011"), ex.Message), "OK");
        }
    }

    private async void OnTransportChanged(object sender, EventArgs e)
    {
        _session.TransportMode = TransportPicker.SelectedIndex switch
        {
            1 => "SignalR",
            2 => "TCP",
            _ => Loc.T("set_009", "Авто")
        };

        await App.SaveAppearanceAsync();

        // Без модального окна: транспорт показывается рядом с переключателем.
        TransportHintLabel.Text =
            string.Format(Loc.T("set_fmt_012"), _session.TransportMode);
        TransportHintLabel.IsVisible = true;
    }
}
