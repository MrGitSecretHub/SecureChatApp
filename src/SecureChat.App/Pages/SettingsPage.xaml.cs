using SecureChat.App.Services;

namespace SecureChat.App.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly AppSession _session = AppServices.Get<AppSession>();
    private readonly AccountService _account = AppServices.Get<AccountService>();
    private readonly ChatManager _chatManager = AppServices.Get<ChatManager>();
    private readonly ApiClient _apiClient = AppServices.Get<ApiClient>();
    private readonly LocalStore _localStore = AppServices.Get<LocalStore>();
    private CancellationTokenSource? _profileSyncCts;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        UsernameLabel.Text = _session.Username;
        ServerLabel.Text = _session.ServerUrl;
        FingerprintLabel.Text = $"Отпечаток ключа: {_session.Fingerprint}";
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
        RefreshBlockedList();

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

        FontScalePicker.Items.Clear();
        FontScalePicker.Items.Add("Маленький");
        FontScalePicker.Items.Add("Обычный");
        FontScalePicker.Items.Add("Крупный");
        FontScalePicker.SelectedIndex = _session.FontScale switch
        {
            "Маленький" => 0,
            "Крупный" => 2,
            _ => 1
        };

        BubbleShapePicker.Items.Clear();
        BubbleShapePicker.Items.Add("Скруглённая");
        BubbleShapePicker.Items.Add("Мягкая");
        BubbleShapePicker.Items.Add("Прямая");
        BubbleShapePicker.SelectedIndex = _session.BubbleShape switch
        {
            "Мягкая" => 1,
            "Прямая" => 2,
            _ => 0
        };

        AvatarsSwitch.IsToggled = _session.ShowAvatarsInChat;

        TransportPicker.Items.Clear();
        TransportPicker.Items.Add("Авто");
        TransportPicker.Items.Add("SignalR (WebSocket)");
        TransportPicker.Items.Add("TCP");
        TransportPicker.SelectedIndex = _session.TransportMode switch
        {
            "SignalR" => 1,
            "TCP" => 2,
            _ => 0
        };
        TimestampsSwitch.IsToggled = _session.ShowTimestamps;
        CompactSidebarSwitch.IsToggled = _session.CompactSidebar;

        DensityPicker.Items.Clear();
        DensityPicker.Items.Add("Компактная");
        DensityPicker.Items.Add("Обычная");
        DensityPicker.Items.Add("Просторная");
        DensityPicker.SelectedIndex = _session.Density switch
        {
            "Компактная" => 0,
            "Просторная" => 2,
            _ => 1
        };

        BuildAccentRow();
        AccentNameLabel.Text = string.IsNullOrEmpty(_session.Accent) || _session.Accent == App.UseThemeAccent
            ? "Как в теме"
            : App.AccentPresets.FirstOrDefault(item => item.Color == _session.Accent).Name is { Length: > 0 } accentName
                ? accentName
                : "Свой цвет";
        UpdateAccentSelection();
        UpdateThemePreview();

        StatusPicker.Items.Clear();
        StatusPicker.Items.Add("Онлайн");
        StatusPicker.Items.Add("Неактивен");
        StatusPicker.Items.Add("Занят");
        StatusPicker.SelectedIndex = _session.StatusMessage switch
        {
            "Неактивен" => 1,
            "Занят" => 2,
            _ => 0
        };
    }

    private async void OnFontScaleChanged(object sender, EventArgs e)
    {
        _session.FontScale = FontScalePicker.SelectedIndex switch
        {
            0 => "Маленький",
            2 => "Крупный",
            _ => "Обычный"
        };

        App.ApplyAppearance();
        await App.SaveAppearanceAsync();
    }

    private async void OnBubbleShapeChanged(object sender, EventArgs e)
    {
        _session.BubbleShape = BubbleShapePicker.SelectedIndex switch
        {
            1 => "Мягкая",
            2 => "Прямая",
            _ => "Скруглённая"
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
            0 => "Компактная",
            2 => "Просторная",
            _ => "Обычная"
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

    private void UpdateThemePreview()
    {
        ThemePreviewRow.Children.Clear();

        var palette = ThemeCatalog.Resolve(_session.Theme);
        if (string.Equals(_session.Theme, ThemeCatalog.SystemTheme, StringComparison.OrdinalIgnoreCase))
        {
            palette = _session.IsDarkTheme
                ? ThemeCatalog.Resolve(ThemeCatalog.DefaultTheme)
                : ThemeCatalog.Resolve("Синяя");
        }

        var samples = new (string Name, Color Color)[]
        {
            ("Фон", palette.PageBackgroundColor),
            ("Панель", palette.SurfaceColor),
            ("Акцент", palette.AccentColor),
            ("Свои", palette.MessageMeColor)
        };

        foreach (var sample in samples)
        {
            var border = new Border
            {
                WidthRequest = 34,
                HeightRequest = 34,
                BackgroundColor = sample.Color,
                Stroke = (Color)Application.Current!.Resources["BorderColor"],
                StrokeThickness = 1
            };

            border.StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle
            {
                CornerRadius = new CornerRadius(8)
            };

            ThemePreviewRow.Children.Add(border);
        }

        var label = new Label
        {
            Text = palette.IsDark ? "Тёмная" : "Светлая",
            FontSize = 12,
            TextColor = (Color)Application.Current!.Resources["TextMuted"],
            VerticalOptions = Microsoft.Maui.Controls.LayoutOptions.Center
        };

        ThemePreviewRow.Children.Add(label);
    }

    private async void OnThemeChanged(object sender, EventArgs e)
    {
        _session.Theme = ThemePicker.SelectedIndex >= 0 &&
            ThemePicker.SelectedIndex < ThemeCatalog.Themes.Length
            ? ThemeCatalog.Themes[ThemePicker.SelectedIndex].Name
            : ThemeCatalog.SystemTheme;

        App.ApplyAppearance();
        UpdateThemePreview();
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
            ? "Как в теме"
            : App.AccentPresets.FirstOrDefault(item => item.Color == accent).Name is { Length: > 0 } name
                ? name
                : "Свой цвет";

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
        catch
        {
            // Профиль останется локальным, если сервер недоступен.
        }
    }
    private async void OnAvatarClicked(object sender, EventArgs e)
    {
        try
        {
            var file = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Выберите аватар",
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
            await DisplayAlert("Ошибка", $"Не удалось загрузить аватар: {ex.Message}", "OK");
        }
    }

    private void OnStatusChanged(object sender, EventArgs e)
    {
        _session.StatusMessage = StatusPicker.SelectedIndex switch
        {
            1 => "Неактивен",
            2 => "Занят",
            _ => "Онлайн"
        };

        _ = SyncProfileAsync();
    }

    private async void OnCheckServerClicked(object sender, EventArgs e)
    {
        try
        {
            var serverTime = await _apiClient.GetServerTimeAsync(_session.ServerUrl);
            await DisplayAlert(
                "Сервер доступен",
                serverTime is null
                    ? "Сервер ответил, но время получить не удалось."
                    : $"Сервер ответил. Время сервера: {serverTime:dd.MM.yyyy HH:mm:ss} UTC.",
                "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Сервер недоступен", ex.Message, "OK");
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
            "Текущий пароль",
            "Введите пароль, с которым вы входите в аккаунт.");
        if (string.IsNullOrEmpty(current))
        {
            return;
        }

        var next = await PasswordPromptPage.ShowAsync(
            Navigation,
            "Новый пароль",
            "Не менее 8 символов. Он понадобится при следующем входе.");
        if (string.IsNullOrEmpty(next))
        {
            return;
        }

        var repeat = await PasswordPromptPage.ShowAsync(
            Navigation,
            "Повторите пароль",
            "Введите новый пароль ещё раз.",
            "Сменить пароль");
        if (repeat is null)
        {
            return;
        }

        if (!string.Equals(next, repeat, StringComparison.Ordinal))
        {
            await DisplayAlert("Пароли не совпадают", "Новый пароль введён по-разному.", "OK");
            return;
        }

        try
        {
            await _account.ChangePasswordAsync(current, next);
            await DisplayAlert(
                "Пароль изменён",
                "Используйте новый пароль при следующем входе. Текущая сессия остаётся активной.",
                "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Не удалось сменить пароль", ex.Message, "OK");
        }
    }

    private async void OnDeleteClicked(object sender, EventArgs e)
    {
        var confirmed = await DisplayAlert(
            "Удалить аккаунт навсегда?",
            "С сервера будут удалены аккаунт, переписка и вложения. Локальная история " +
            "и ключи устройства тоже будут стёрты. Восстановить данные невозможно.",
            "Удалить навсегда",
            "Отмена");

        if (!confirmed)
        {
            return;
        }

        var password = await PasswordPromptPage.ShowAsync(
            Navigation,
            "Подтвердите удаление",
            "Введите пароль аккаунта. Это действие необратимо.",
            "Удалить аккаунт");
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
            await DisplayAlert("Не удалось удалить аккаунт", ex.Message, "OK");
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
            await DisplayAlert("Безопасность", "Отпечаток ключа недоступен.", "OK");
            return;
        }

        await DisplayAlert(
            "Отпечаток ключа",
            fingerprint,
            "Скопировать",
            "Закрыть");

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
            "Рингтон уведомлений",
            "Отмена",
            null,
            options);

        if (string.IsNullOrEmpty(picked) || picked == "Отмена")
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

        if (e.Value)
        {
            await DisplayAlert(
                "Превью ссылок",
                "Чат будет сам заходить на сайт ссылки, чтобы загрузить её заголовок и картинку. Владелец сайта увидит IP вашего устройства. Отключите опцию в настройках, если это нежелательно.",
                "Понятно");
        }
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
            await DisplayAlert("Блокировка", "Нельзя заблокировать самого себя.", "OK");
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
            ? "Список пуст"
            : "Выберите пользователя";
    }

    private Task SavePrivacyAsync() =>
        PrivacyStore.SaveAsync(_session, AppServices.Get<SecretStore>());

    private async void OnChangePasswordClicked(object sender, EventArgs e)
    {
        var secrets = AppServices.Get<SecretStore>();
        var hasPasscode = await PrivacyStore.HasPasscodeAsync(secrets);
        var choice = await DisplayActionSheet(
            "Код-пароль приложения",
            "Отмена",
            null,
            hasPasscode ? new[] { "Изменить код", "Выключить код" } : new[] { "Установить код" });

        if (string.IsNullOrEmpty(choice) || choice == "Отмена")
        {
            return;
        }

        if (hasPasscode)
        {
            var current = await DisplayPromptAsync(
                "Текущий код",
                "Введите текущий код-пароль:",
                "Далее",
                "Отмена",
                maxLength: 32,
                keyboard: Keyboard.Text);

            if (current is null)
            {
                return;
            }

            if (!await PrivacyStore.VerifyPasscodeAsync(secrets, current))
            {
                await DisplayAlert("Код-пароль", "Неверный код-пароль.", "OK");
                return;
            }
        }

        if (choice == "Выключить код")
        {
            await PrivacyStore.RemovePasscodeAsync(secrets);
            await DisplayAlert("Код-пароль", "Код-пароль выключен.", "OK");
            return;
        }

        var newCode = await DisplayPromptAsync(
            "Новый код-пароль",
            "Минимум 4 символа:",
            "Далее",
            "Отмена",
            maxLength: 32,
            keyboard: Keyboard.Text);

        if (string.IsNullOrEmpty(newCode))
        {
            return;
        }

        if (newCode.Length < 4)
        {
            await DisplayAlert("Код-пароль", "Код должен содержать не менее 4 символов.", "OK");
            return;
        }

        var repeat = await DisplayPromptAsync(
            "Повторите код",
            "Введите новый код ещё раз:",
            "Сохранить",
            "Отмена",
            maxLength: 32,
            keyboard: Keyboard.Text);

        if (!string.Equals(repeat, newCode, StringComparison.Ordinal))
        {
            await DisplayAlert("Код-пароль", "Коды не совпадают.", "OK");
            return;
        }

        await PrivacyStore.SetPasscodeAsync(secrets, newCode);
        await DisplayAlert("Код-пароль", "Код-пароль сохранён. Он будет запрашиваться при запуске.", "OK");
    }

    private async void OnExportHistoryClicked(object sender, EventArgs e)
    {
        try
        {
            var data = await _localStore.ExportHistoryAsync();
            var base64 = Convert.ToBase64String(data);

            await Clipboard.Default.SetTextAsync(base64);
            await DisplayAlert("История", "История переписки скопирована в буфер обмена (base64).", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", $"Не удалось экспортировать историю: {ex.Message}", "OK");
        }
    }

    private async void OnImportHistoryClicked(object sender, EventArgs e)
    {
        try
        {
            var base64 = await Clipboard.Default.GetTextAsync();
            if (string.IsNullOrWhiteSpace(base64))
            {
                await DisplayAlert("История", "Буфер обмена пуст.", "OK");
                return;
            }

            var data = Convert.FromBase64String(base64);
            await _localStore.ImportHistoryAsync(data);
            await DisplayAlert("История", "История переписки импортирована из буфера обмена.", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", $"Не удалось импортировать историю: {ex.Message}", "OK");
        }
    }

    private async void OnTransportChanged(object sender, EventArgs e)
    {
        _session.TransportMode = TransportPicker.SelectedIndex switch
        {
            1 => "SignalR",
            2 => "TCP",
            _ => "Авто"
        };

        await DisplayAlert(
            "Подключение",
            $"Транспорт: {_session.TransportMode}. Изменение применится при следующем подключении.",
            "OK");
    }
}
