using SecureChat.App.Services;

namespace SecureChat.App.Pages;

public partial class LoginPage : ContentPage
{
    private readonly AccountService _account = AppServices.Get<AccountService>();
    private readonly SessionStore _sessionStore = AppServices.Get<SessionStore>();
    private readonly AppSession _session = AppServices.Get<AppSession>();
    private readonly ChatManager _chatManager = AppServices.Get<ChatManager>();
    private bool _startupChecked;

    public LoginPage()
    {
        InitializeComponent();
        ServerEntry.Text = _session.ServerUrl;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_startupChecked)
        {
            return;
        }

        _startupChecked = true;
        try
        {
            await PrivacyStore.LoadAsync(AppServices.Get<AppSession>(), AppServices.Get<SecretStore>());

            // Канал уведомлений мог быть создан до загрузки настроек — обновляем
            // его звук под выбранный рингтон.
            AppServices.Get<NotificationService>().Initialize();

            var saved = await _sessionStore.LoadSessionAsync();
            if (saved is not null)
            {
                ServerEntry.Text = saved.ServerUrl;
                UsernameEntry.Text = saved.Username;
            }

            SetBusy(true, Loc.T("login_007", "Проверка сохранённой сессии…"));
            if (await _account.TryRestoreAsync())
            {
                await _chatManager.StartAsync();
                if (await PrivacyStore.HasPasscodeAsync(AppServices.Get<SecretStore>()))
                {
                    App.SetRoot(new LockPage());
                    return;
                }

                App.SetRoot(App.CreateMainPage());
                return;
            }

            StatusLabel.IsVisible = false;
        }
        catch (Exception ex)
        {
            ShowStatus(string.Format(Loc.T("login_fmt_001"), ex.Message));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnLoginClicked(object sender, EventArgs e)
    {
        StatusLabel.IsVisible = false;
        var server = ServerEntry.Text?.Trim() ?? string.Empty;
        var username = UsernameEntry.Text?.Trim() ?? string.Empty;
        var password = PasswordEntry.Text ?? string.Empty;

        try
        {
        var normalizedServer = AppSession.NormalizeServerUrl(server);
        if (!InsecureHttpCheck.IsChecked &&
            normalizedServer.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            ShowStatus(Loc.T("login_insecure_warn",
                "HTTP передаёт пароль и токен открытым текстом. Используйте HTTPS или поставьте галочку «Разрешить HTTP»."));
            return;
        }

            AccountService.Validate(server, username, password);
        }
        catch (ArgumentException ex)
        {
            ShowStatus(ex.Message);
            return;
        }

        SetBusy(true, Loc.T("login_002", "Вход…"));

        try
        {
            await _account.LoginAsync(server, username, password);
            await LaunchChatsAsync();
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnRegisterClicked(object sender, EventArgs e)
    {
        StatusLabel.IsVisible = false;
        var server = ServerEntry.Text?.Trim() ?? string.Empty;
        var username = UsernameEntry.Text?.Trim() ?? string.Empty;
        var password = PasswordEntry.Text ?? string.Empty;
        var confirm = ConfirmEntry.Text ?? string.Empty;

        try
        {
        var normalizedServer = AppSession.NormalizeServerUrl(server);
        if (!InsecureHttpCheck.IsChecked &&
            normalizedServer.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            ShowStatus(Loc.T("login_insecure_warn",
                "HTTP передаёт пароль и токен открытым текстом. Используйте HTTPS или поставьте галочку «Разрешить HTTP»."));
            return;
        }

            AccountService.Validate(server, username, password);
            if (!string.Equals(password, confirm, StringComparison.Ordinal))
            {
                throw new ArgumentException(Loc.T("login_005", "Пароли не совпадают."));
            }
        }
        catch (ArgumentException ex)
        {
            ShowStatus(ex.Message);
            return;
        }

        SetBusy(true, Loc.T("login_008", "Создание аккаунта и ключей…"));

        try
        {
            await _account.RegisterAsync(server, username, EmailEntry.Text?.Trim(), password);
            await LaunchChatsAsync();
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LaunchChatsAsync()
    {
        PasswordEntry.Text = string.Empty;
        ConfirmEntry.Text = string.Empty;
        await _chatManager.StartAsync();
        App.SetRoot(App.CreateMainPage());
        AppServices.Get<NotificationService>().EnsurePermission();
    }

    private void SetBusy(bool busy, string? message = null)
    {
        BusyIndicator.IsRunning = busy;
        BusyIndicator.IsVisible = busy;
        LoginButton.IsEnabled = !busy;
        RegisterButton.IsEnabled = !busy;
        ServerEntry.IsEnabled = !busy;
        UsernameEntry.IsEnabled = !busy;
        PasswordEntry.IsEnabled = !busy;
        ConfirmEntry.IsEnabled = !busy;

        if (busy && message is not null)
        {
            ShowStatus(message, isError: false);
        }
    }

    private void ShowStatus(string message, bool isError = true)
    {
        StatusLabel.Text = message;
        StatusLabel.TextColor = isError
            ? (Color)Application.Current!.Resources["Danger"]
            : (Color)Application.Current!.Resources["Muted"];
        StatusLabel.IsVisible = true;
    }

    /// <summary>Подсказывает адреса сервера, доступные с телефона или эмулятора.</summary>
    private async void OnServerHintTapped(object sender, TappedEventArgs e)
    {
        var candidates = HostAddress.Candidates();
        if (candidates.Length == 0)
        {
            await DisplayAlert(
                Loc.T("login_000", "Адрес сервера"),
                "Не удалось определить адрес автоматически. " +
                Loc.T("login_009", "Узнайте IP-адрес компьютера командой ipconfig и введите его вручную."),
                "OK");
            return;
        }

        var choice = await DisplayActionSheet(
            Loc.T("login_001", "Адрес сервера:"),
            "Отмена",
            null,
            candidates);
        if (string.IsNullOrWhiteSpace(choice) || choice == "Отмена")
        {
            return;
        }

        ServerEntry.Text = choice;
    }
}
