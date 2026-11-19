using SecureChat.App.Pages;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using SecureChat.App.Services;
using SecureChat.App.Views;
#if WINDOWS
using SecureChat.App.WinUI;
#endif

namespace SecureChat.App.Pages;

/// <summary>
/// Главный экран Windows-клиента (Discord-стиль):
/// сайдбар со списком чатов слева, открытая переписка справа.
/// </summary>
public partial class DesktopMainPage : ContentPage, IDisposable
{
    private readonly AppSession _session = AppServices.Get<AppSession>();
    private readonly AccountService _account = AppServices.Get<AccountService>();
    private readonly ChatManager _chatManager = AppServices.Get<ChatManager>();
    private readonly NotificationService _notifications = AppServices.Get<NotificationService>();
    private readonly ObservableCollection<ConversationVm> _conversations = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // Вкладки («папки») считают бейджи по полному списку, поэтому держим оба среза:
    // обычные чаты (с «Избранным» во главе) и архив.
    private IReadOnlyList<StoredConversation> _regular = Array.Empty<StoredConversation>();
    private IReadOnlyList<StoredConversation> _archived = Array.Empty<StoredConversation>();
    private IReadOnlyList<StoredChatFolder> _folders = Array.Empty<StoredChatFolder>();
    private CancellationTokenSource? _toastCts;
    private CancellationTokenSource? _statusCts;
    private CancellationTokenSource? _hintCts;
    private IDisposable? _shortcuts;
    private string? _selectedUsername;
    private string? _toastUsername;
    private bool _suppressSelection;
    private bool _pageVisible;
    private bool _windowActive = true;
    private bool _windowHooked;
    private bool _promptOpen;
    private bool _disposed;

    public DesktopMainPage()
    {
        InitializeComponent();

        Resources.Add("BoolToSidebarColorConverter", new BoolToSidebarColorConverter());
        Resources.Add("InvertedBoolConverter", new Views.InvertedBoolConverter());
        Resources.Add("DeliveryToDotsConverter", new Views.DeliveryToDotsConverter());

        ConversationsView.ItemsSource = _conversations;

        _chatManager.ConversationsChanged += OnConversationsChanged;
        _chatManager.MessageAdded += OnMessageAdded;
        _chatManager.Error += OnError;
        _chatManager.ConnectionStateChanged += OnConnectionStateChanged;
        _notifications.Activated += OnNotificationActivated;

        ChatHost.SettingsRequested += OnChatSettingsRequested;
        ChatHost.CloseRequested += OnChatCloseRequested;

        Loaded += OnLoaded;

        StatusPicker.Items.Add(Loc.T("vs_online", "Онлайн"));
        StatusPicker.Items.Add(Loc.T("vs_away", "Неактивен"));
        StatusPicker.Items.Add(Loc.T("vs_busy", "Занят"));

        // Индикатор соединения в шапке сайдбара
        ConnectionDot.TextColor = (Color)Application.Current!.Resources["Danger"];
    }

    /// <summary>Локализованная подпись статуса; в сессии хранятся русские значения.</summary>
    private static string StatusDisplay(string status) => status switch
    {
        "Онлайн" => Loc.T("vs_online", "Онлайн"),
        "Неактивен" => Loc.T("vs_away", "Неактивен"),
        "Занят" => Loc.T("vs_busy", "Занят"),
        _ => status
    };

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshStoriesAsync();

        _pageVisible = true;
        UpdateHeader();
        EnsureWindowHooks();
        await RefreshAsync();
        UpdateConnectionState(!_chatManager.IsOffline);

        if (_selectedUsername is not null && _session.IsAuthenticated)
        {
            await _chatManager.OpenChatAsync(_selectedUsername);
            await RefreshAsync();
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _pageVisible = false;

        if (_selectedUsername is not null)
        {
            _chatManager.CloseChat(_selectedUsername);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _chatManager.ConversationsChanged -= OnConversationsChanged;
        _chatManager.MessageAdded -= OnMessageAdded;
        _chatManager.Error -= OnError;
        _chatManager.ConnectionStateChanged -= OnConnectionStateChanged;
        _notifications.Activated -= OnNotificationActivated;

        ChatHost.SettingsRequested -= OnChatSettingsRequested;
        ChatHost.CloseRequested -= OnChatCloseRequested;

        if (_windowHooked && Window is not null)
        {
            Window.Activated -= OnWindowActivated;
            Window.Deactivated -= OnWindowDeactivated;
        }

        _shortcuts?.Dispose();
        _toastCts?.Cancel();
        _toastCts?.Dispose();
        _statusCts?.Cancel();
        _statusCts?.Dispose();
        _hintCts?.Cancel();
        _hintCts?.Dispose();

        ChatHost.Close();
    }

    private void OnLoaded(object? sender, EventArgs e) => EnsureWindowHooks();

    private void EnsureWindowHooks()
    {
        if (_windowHooked || Window is null)
        {
            return;
        }

        _windowHooked = true;
        Window.Activated += OnWindowActivated;
        Window.Deactivated += OnWindowDeactivated;

#if WINDOWS
        _shortcuts = WindowShortcuts.Attach(
            Window,
            () => MainThread.BeginInvokeOnMainThread(async () => await StartNewChatAsync()),
            () => MainThread.BeginInvokeOnMainThread(CloseChat),
            () => MainThread.BeginInvokeOnMainThread(FocusSearch));
#endif
    }

    // ================================================================
    // ШАПКА
    // ================================================================

    private void UpdateHeader()
    {
        var displayName = !string.IsNullOrWhiteSpace(_session.Nickname)
            ? _session.Nickname
            : _session.Username;
        UserLabel.Text = displayName;

        var status = _session.StatusMessage;
        UserStatusLabel.Text = StatusDisplay(status);

        StatusDot.BackgroundColor = status switch
        {
            "Неактивен" => (Color)Application.Current!.Resources["OfflineDot"],
            "Занят" => (Color)Application.Current!.Resources["Warning"],
            _ => (Color)Application.Current!.Resources["OnlineDot"]
        };

        if (!string.IsNullOrEmpty(_session.AvatarBase64))
        {
            HeaderAvatar.Source = ImageSource.FromStream(() =>
                new MemoryStream(Convert.FromBase64String(_session.AvatarBase64)));
        }
        else
        {
            HeaderAvatar.Source = null;
        }

        StatusPicker.SelectedIndex = status switch
        {
            "Неактивен" => 1,
            "Занят" => 2,
            _ => 0
        };
    }

    private async Task RefreshAsync()
    {
        if (_disposed || !_session.IsAuthenticated)
        {
            return;
        }

        await _refreshLock.WaitAsync();
        try
        {
            var regular = await _chatManager.GetConversationsAsync();
            if (_disposed)
            {
                return;
            }

            // «Избранное» всегда во главе списка обычных чатов.
            var selfConversation = await _chatManager.GetOrCreateSelfConversationAsync();
            var regularAll = new List<StoredConversation> { selfConversation };
            regularAll.AddRange(regular.Where(c => c.Username != ChatManager.SelfChatUsername));
            var archivedAll = await _chatManager.GetArchivedConversationsAsync();
            var foldersAll = await _chatManager.GetChatFoldersAsync();

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                _regular = regularAll;
                _archived = archivedAll;
                _folders = foldersAll;
                RebuildFolderChips();
                ApplyFilter();
                UpdateWindowTitle();
            });
        }
        catch (Exception ex)
        {
            await MainThread.InvokeOnMainThreadAsync(() => ShowStatus(ex.Message));
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void ApplyFilter()
    {
        var query = SearchEntry.Text?.Trim();
        var folder = _session.ChatFolder;
        var custom = ChatFolders.Find(_folders, folder);
        if (custom is null && !ChatFolders.IsFixed(folder))
        {
            // Папку удалили или переименовали — возвращаемся к «Все».
            folder = _session.ChatFolder = ChatFolders.All;
        }

        var source = folder == ChatFolders.Archive ? _archived : _regular;

        _suppressSelection = true;
        _conversations.Clear();

        foreach (var conversation in source)
        {
            if (custom is not null)
            {
                if (!ChatFolders.Contains(custom, conversation))
                {
                    continue;
                }
            }
            else if (!ChatFolders.Matches(folder, conversation))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(query) &&
                !conversation.Username.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !conversation.LastMessage.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var vm = ConversationVm.From(conversation);
            vm.IsSelected = string.Equals(
                conversation.Username, _selectedUsername, StringComparison.OrdinalIgnoreCase);
            _conversations.Add(vm);
        }

        ConversationsView.SelectedItem = _selectedUsername is null
            ? null
            : _conversations.FirstOrDefault(item =>
                string.Equals(item.Username, _selectedUsername, StringComparison.OrdinalIgnoreCase));

        _suppressSelection = false;
        UpdateFolderTabs();
    }

    /// <summary>Чипы пользовательских папок после фиксированных вкладок.</summary>
    private void RebuildFolderChips()
    {
        UserFolderChips.Children.Clear();
        foreach (var folder in _folders)
        {
            var chip = new Button
            {
                Text = folder.Name,
                CommandParameter = folder.Name,
                FontSize = 11,
                HeightRequest = 30,
                Padding = new Thickness(8, 2),
                CornerRadius = 14,
            };
            chip.Clicked += OnFolderClicked;
            UserFolderChips.Children.Add(chip);
        }
    }

    /// <summary>Меню управления папками: создать или открыть любую на настройку.</summary>
    private async void OnFoldersManageClicked(object sender, EventArgs e)
    {
        string create = Loc.T("rs_196", "＋ Создать папку");
        if (_folders.Count == 0)
        {
            await Navigation.PushAsync(new FolderEditPage(null));
            await RefreshAsync();
            return;
        }

        var names = _folders.Select(f => f.Name).ToArray();
        var choice = await DisplayActionSheet(
            Loc.T("dg_045", "Папки чатов"), "Отмена", null,
            names.Prepend(create).ToArray());

        if (string.IsNullOrEmpty(choice) || choice == "Отмена")
        {
            return;
        }

        var existing = string.Equals(choice, create, StringComparison.Ordinal)
            ? null
            : ChatFolders.Find(_folders, choice);
        await Navigation.PushAsync(new FolderEditPage(existing));
        await RefreshAsync();
    }

    private void UpdateWindowTitle()
    {
        if (Window is null)
        {
            return;
        }

        var unread = _regular.Sum(item => item.UnreadCount) +
                     _archived.Sum(item => item.UnreadCount);
        Window.Title = unread > 0 ? $"SecureChat ({unread})" : "SecureChat";
    }

    // ================================================================
    // ОТКРЫТИЕ / ЗАКРЫТИЕ ЧАТА
    // ================================================================

    public async Task OpenChatAsync(string username)
    {
        if (_disposed || !_session.IsAuthenticated)
        {
            return;
        }

        try
        {
            _selectedUsername = username;

            await ChatHost.OpenAsync(username);

            EmptyState.IsVisible = false;
            ChatHost.IsVisible = true;

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            CloseChat();
            ShowStatus(ex.Message);
        }
    }

    private void CloseChat()
    {
        if (_disposed)
        {
            return;
        }

        HideToast();
        ChatHost.Close();
        ChatHost.IsVisible = false;
        EmptyState.IsVisible = true;
        _selectedUsername = null;

        _suppressSelection = true;
        ConversationsView.SelectedItem = null;
        _suppressSelection = false;
    }

    /// <summary>Собеседник удалил чат — закрываем открытую переписку.</summary>
    public void CloseChatFromPeer()
    {
        if (_disposed)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_disposed)
            {
                CloseChat();
            }
        });
    }

    /// <summary>Кнопка ✕ в шапке переписки (Windows-клиент).</summary>
    private void OnChatCloseRequested() => CloseChat();

    private async Task StartNewChatAsync()
    {
        if (_promptOpen || _disposed)
        {
            return;
        }

        _promptOpen = true;
        try
        {
            var username = await DisplayPromptAsync(
                Loc.T("vs_newchat", "Новый чат"),
                Loc.T("rs_015", "Введите имя пользователя:"),
                Loc.T("vs_open", "Открыть"),
                "Отмена",
                Loc.T("rs_180", "например, bob"),
                maxLength: 32,
                keyboard: Keyboard.Text);

            if (string.IsNullOrWhiteSpace(username))
            {
                return;
            }

            var conversation = await _chatManager.AddContactAsync(username.Trim());
            await RefreshAsync();
            await OpenChatAsync(conversation.Username);
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("uc_026", "Не удалось добавить контакт"), ex.Message, "OK");
        }
        finally
        {
            _promptOpen = false;
        }
    }

    // ================================================================
    // ОБРАБОТЧИКИ
    // ================================================================

    private async void OnNewChatClicked(object sender, EventArgs e) =>
        await StartNewChatAsync();

    private async void OnSearchUsersClicked(object sender, EventArgs e) =>
        await Navigation.PushAsync(new SearchUsersPage());

    /// <summary>Переключение вкладки списка чатов («Все», «Непрочитанные», …).</summary>
    private void OnFolderClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string folder })
        {
            return;
        }

        _session.ChatFolder = folder;
        ApplyFilter();
    }

    /// <summary>Вкладки (фиксированные и папки): бейджи-счётчики и подсветка активной.</summary>
    private void UpdateFolderTabs()
    {
        Color Res(string key) => (Color)Application.Current!.Resources[key];
        var active = _session.ChatFolder;

        foreach (var button in new[]
                 {
                     FolderAllButton, FolderUnreadButton, FolderMutedButton, FolderArchiveButton,
                 }.Concat(UserFolderChips.Children.OfType<Button>()))
        {
            var folder = (string)button.CommandParameter;
            var custom = ChatFolders.Find(_folders, folder);
            button.Text = custom is not null
                ? ChatFolders.FolderTabText(custom, _regular)
                : ChatFolders.TabText(folder, _regular, _archived.Count);
            var selected = string.Equals(folder, active, StringComparison.Ordinal);
            button.BackgroundColor = selected ? Res("Accent") : Res("SurfaceAlt");
            button.TextColor = selected ? Res("AccentText") : Res("TextMuted");
        }
    }

    private async void OnConversationMenuClicked(object sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: ConversationVm vm } ||
            string.IsNullOrWhiteSpace(vm.Username))
        {
            return;
        }

        // «Избранное» — служебный чат, его нельзя удалять и архивировать.
        if (string.Equals(vm.Username, ChatManager.SelfChatUsername, StringComparison.OrdinalIgnoreCase))
        {
            await DisplayAlert(Loc.T("vs_favorites", "Избранное"),
                Loc.T("dg_090", "Это служебный чат для сохранения сообщений. Его нельзя удалить или заархивировать."),
                "OK");
            return;
        }

        var archiveAction = vm.IsArchived ? Loc.T("rs_205", "📤 Разархивировать") : Loc.T("rs_207", "📦 Архивировать");
        var muteAction = vm.IsMuted ? Loc.T("rs_217", "🔔 Включить звук") : Loc.T("rs_211", "🔇 Без звука");
        var choice = await DisplayActionSheet(vm.Username, "Отмена", null,
            archiveAction, muteAction, Loc.T("dg_097", "🧹 Удалить чат"), Loc.T("dg_098", "🧽 Очистить историю"));

        if (choice == archiveAction)
        {
            await _chatManager.ArchiveConversationAsync(vm.Username, !vm.IsArchived);
            await RefreshAsync();
        }
        else if (choice == muteAction)
        {
            await _chatManager.SetConversationMutedAsync(vm.Username, !vm.IsMuted);
            await RefreshAsync();
        }
        else if (choice == Loc.T("dg_097", "🧹 Удалить чат") || choice == Loc.T("dg_098", "🧽 Очистить историю"))
        {
            await DeleteOrClearConversationAsync(vm.Username, clearHistory: choice == Loc.T("dg_098", "🧽 Очистить историю"));
        }
    }

    /// <summary>
    /// Удаление чата или очистка истории: сначала выбор области («для всех» /
    /// «только у себя»), затем подтверждение.
    /// </summary>
    private async Task DeleteOrClearConversationAsync(string username, bool clearHistory)
    {
        var scopeChoice = await DisplayActionSheet(
            clearHistory ? Loc.T("dg_042", "Очистить историю?") : Loc.T("dg_081", "Удалить чат?"),
            "Отмена",
            null,
            Loc.T("dg_074", "Удалить для всех"),
            clearHistory ? Loc.T("dg_073", "Только у себя") : Loc.T("dg_075", "Удалить для себя"));

        if (scopeChoice is null || scopeChoice == "Отмена")
        {
            return;
        }

        var forEveryone = string.Equals(scopeChoice, Loc.T("dg_074", "Удалить для всех"), StringComparison.OrdinalIgnoreCase);

        var confirmed = await DisplayAlert(
            clearHistory ? Loc.T("dg_041", "Очистить историю") : Loc.T("dg_080", "Удалить чат"),
            clearHistory
                ? forEveryone
                    ? string.Format(Loc.T("dg_031"), username)
                    : string.Format(Loc.T("dg_030"), username)
                : forEveryone
                    ? string.Format(Loc.T("dg_046"), username)
                    : string.Format(Loc.T("dg_086"), username),
            "Удалить", "Отмена") == true;
        if (!confirmed)
        {
            return;
        }

        bool ok;
        if (clearHistory)
        {
            ok = await _chatManager.ClearConversationAsync(username, forEveryone);
            ShowHint(ok
                ? forEveryone ? Loc.T("rs_058", "История удалена у всех") : Loc.T("rs_057", "История удалена только у вас")
                : Loc.T("rs_089", "Не удалось очистить историю"), isError: !ok);
        }
        else
        {
            ok = await _chatManager.DeleteConversationAsync(username, forEveryone);
            ShowHint(ok
                ? forEveryone ? Loc.T("rs_172", "Чат удалён у всех") : Loc.T("rs_171", "Чат удалён только у вас")
                : Loc.T("rs_098", "Не удалось удалить чат"), isError: !ok);

            if (ok && string.Equals(_selectedUsername, username, StringComparison.OrdinalIgnoreCase))
            {
                CloseChat();
            }
        }

        await RefreshAsync();
    }

    private async void OnContactsClicked(object sender, EventArgs e)
    {
        try
        {
            var username = await DisplayPromptAsync(
                Loc.T("vs_addcontact", "Добавить контакт"),
                Loc.T("rs_015", "Введите имя пользователя:"),
                Loc.T("vs_add", "Добавить"),
                "Отмена",
                Loc.T("rs_180", "например, bob"),
                maxLength: 32,
                keyboard: Keyboard.Text);

            if (string.IsNullOrWhiteSpace(username))
            {
                return;
            }

            var conversation = await _chatManager.AddContactAsync(username.Trim());
            await RefreshAsync();
            await OpenChatAsync(conversation.Username);
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("uc_026", "Не удалось добавить контакт"), ex.Message, "OK");
        }
    }

    /// <summary>Фокус на поле поиска (горячая клавиша Ctrl+F).</summary>
    private void FocusSearch()
    {
        SearchEntry.Focus();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void OnGlobalSearchPressed(object sender, EventArgs e)
    {
        var query = SearchEntry.Text?.Trim() ?? string.Empty;
        await Navigation.PushAsync(new GlobalSearchPage(query));
    }

    private async void OnSpacesClicked(object sender, EventArgs e)
    {
        try
        {
            await Navigation.PushAsync(new SpacesPage());
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("uc_033", "Не удалось открыть"), ex.Message, "OK");
        }
    }

    private async void OnBrowserClicked(object sender, EventArgs e)
    {
        try
        {
            await Navigation.PushAsync(new BrowserPage());
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("uc_034", "Не удалось открыть браузер"), ex.Message, "OK");
        }
    }

    private async void OnConversationSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection ||
            e.CurrentSelection.FirstOrDefault() is not ConversationVm conversation)
        {
            return;
        }

        if (string.Equals(conversation.Username, _selectedUsername, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await OpenChatAsync(conversation.Username);
    }

    private void OnCloseChatClicked(object sender, EventArgs e) => CloseChat();

    private async void OnSettingsClicked(object sender, EventArgs e) =>
        await Navigation.PushAsync(AppServices.Get<SettingsPage>());

    private async void OnMyProfileClicked(object sender, EventArgs e) =>
        await Navigation.PushAsync(AppServices.Get<SettingsPage>());

    /// <summary>Клик по карточке профиля в сайдбаре — открывает профиль и настройки.</summary>
    private async void OnProfileTapped(object sender, EventArgs e) =>
        await Navigation.PushAsync(AppServices.Get<SettingsPage>());

    /// <summary>Кнопка «⚙» в шапке чата — открывает профиль и настройки.</summary>
    private async void OnChatSettingsRequested() =>
        await Navigation.PushAsync(AppServices.Get<SettingsPage>());

    private async void OnLogoutClicked(object sender, EventArgs e)
    {
        var confirmed = await DisplayAlert(
            Loc.T("dg_022", "Выход"),
            Loc.T("dg_024", "Завершить сеанс? Локальная история и ключи останутся на устройстве."),
            Loc.T("dg_019", "Выйти"),
            "Отмена");

        if (!confirmed)
        {
            return;
        }

        await _chatManager.ShutdownAsync();
        await _account.LogoutAsync();
        App.SetRoot(new NavigationPage(AppServices.Get<LoginPage>()));
    }

    private void OnStatusChanged(object sender, EventArgs e)
    {
        var status = StatusPicker.SelectedIndex switch
        {
            1 => Loc.T("vs_away", "Неактивен"),
            2 => Loc.T("vs_busy", "Занят"),
            _ => "Онлайн"
        };

        _session.StatusMessage = status;
        UserStatusLabel.Text = StatusDisplay(status);

        StatusDot.BackgroundColor = status switch
        {
            "Неактивен" => (Color)Application.Current!.Resources["OfflineDot"],
            "Занят" => (Color)Application.Current!.Resources["Warning"],
            _ => (Color)Application.Current!.Resources["OnlineDot"]
        };
    }

    // ================================================================
    // СОБЫТИЯ
    // ================================================================

    private void OnConversationsChanged() =>
        MainThread.BeginInvokeOnMainThread(async () => await RefreshAsync());

    private void OnMessageAdded(StoredMessage message, string username)
    {
        if (_disposed || message.IsMine || _chatManager.IsConversationMuted(username))
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_disposed)
            {
                return;
            }

            var isOpenAndVisible =
                _pageVisible &&
                _windowActive &&
                string.Equals(_selectedUsername, username, StringComparison.OrdinalIgnoreCase);

            if (isOpenAndVisible)
            {
                return;
            }

            var preview = _session.ShowNotificationPreview
                ? MessagePreview.Build(message.Text)
                : Loc.T("rs_110", "Новое сообщение");
            if (!_notifications.ShowNotification(username, preview, username, 0, silent: false))
            {
                ShowInAppToast(username, preview);
            }
        });
    }

    private void OnError(string message) =>
        MainThread.BeginInvokeOnMainThread(() => ShowStatus(message));

    private void OnConnectionStateChanged(bool connected) =>
        MainThread.BeginInvokeOnMainThread(() => UpdateConnectionState(connected));

    private void UpdateConnectionState(bool connected)
    {
        // Индикатор соединения в шапке сайдбара
        ConnectionDot.TextColor = connected
            ? (Color)Application.Current!.Resources["OnlineDot"]
            : (Color)Application.Current!.Resources["Danger"];

        if (connected)
        {
            HideStatus();
        }
        else
        {
            ShowOfflineStatus(
                Loc.T("rs_188", "⚡ Офлайн-режим: сообщения сохраняются и отправятся после подключения."));
        }
    }

    private void ShowOfflineStatus(string message)
    {
        _statusCts?.Cancel();
        _statusCts?.Dispose();
        _statusCts = null;

        StatusLabel.Text = message;
        StatusLabel.TextColor = (Color)Application.Current!.Resources["Warning"];
        StatusLabel.IsVisible = true;
    }

    private void ShowStatus(string message)
    {
        StatusLabel.Text = message;
        StatusLabel.TextColor = (Color)Application.Current!.Resources["Danger"];
        StatusLabel.IsVisible = true;

        _statusCts?.Cancel();
        _statusCts?.Dispose();
        _statusCts = new CancellationTokenSource();
        var token = _statusCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(7000, token);
                MainThread.BeginInvokeOnMainThread(HideStatus);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void HideStatus()
    {
        _statusCts?.Cancel();
        if (!_disposed)
        {
            StatusLabel.IsVisible = false;
        }
    }

    /// <summary>Короткая подсказка о результате действия (удаление/очистка чата).</summary>
    private void ShowHint(string text, bool isError = false)
    {
        if (_disposed)
        {
            return;
        }

        HintLabel.Text = text;
        HintLabel.TextColor = isError
            ? (Color)Application.Current!.Resources["Danger"]
            : (Color)Application.Current!.Resources["TextMuted"];
        HintLabel.IsVisible = true;

        _hintCts?.Cancel();
        _hintCts = new CancellationTokenSource();
        _ = HideHintLaterAsync(isError ? 6000 : 4000, _hintCts.Token);
    }

    private async Task HideHintLaterAsync(int delayMs, CancellationToken token)
    {
        try
        {
            await Task.Delay(delayMs, token);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!_disposed)
                {
                    HintLabel.IsVisible = false;
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Новая подсказка заменила предыдущую.
        }
    }

    private void ShowInAppToast(string username, string text)
    {
        _toastUsername = username;
        ToastTitle.Text = username;
        ToastBody.Text = text;
        InAppToast.IsVisible = true;

        _toastCts?.Cancel();
        _toastCts?.Dispose();
        _toastCts = new CancellationTokenSource();
        var token = _toastCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(6000, token);
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (!_disposed)
                    {
                        InAppToast.IsVisible = false;
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void HideToast()
    {
        _toastCts?.Cancel();
        if (!_disposed)
        {
            InAppToast.IsVisible = false;
        }

        _toastUsername = null;
    }

    private async void OnToastTapped(object sender, TappedEventArgs e)
    {
        var username = _toastUsername;
        HideToast();

        if (username is null)
        {
            return;
        }

        if (!_pageVisible)
        {
            await Navigation.PopToRootAsync();
        }

        await OpenChatAsync(username);
    }

    private void OnNotificationActivated(string username) =>
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (_disposed)
            {
                return;
            }

            if (!_pageVisible)
            {
                await Navigation.PopToRootAsync();
            }

            await OpenChatAsync(username);
        });

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        _windowActive = true;

        if (!_pageVisible || _selectedUsername is null || _disposed)
        {
            return;
        }

        _ = _chatManager.OpenChatAsync(_selectedUsername);
        _ = RefreshAsync();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _windowActive = false;

        if (_selectedUsername is not null)
        {
            _chatManager.CloseChat(_selectedUsername);
        }
    }

    // ================================================================
    // КОНВЕРТЕРЫ
    // ================================================================

    /// <summary>true → AccentSoft (выбранный чат), false → прозрачный.</summary>
    public sealed class BoolToSidebarColorConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b && b)
            {
                return (Color)Application.Current!.Resources["AccentSoft"];
            }
            return Colors.Transparent;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private async Task RefreshStoriesAsync()
    {
        if (StoriesStrip is null) return;
        StoriesStrip.Clear();
        var add = new Button
        {
            Text = "＋",
            WidthRequest = 44,
            HeightRequest = 44,
            CornerRadius = 22,
            BackgroundColor = (Color?)Application.Current?.Resources["SurfaceAlt"] ?? Colors.Gray,
            TextColor = (Color?)Application.Current?.Resources["TextPrimary"] ?? Colors.White
        };
        add.Clicked += async (_, _) => await Navigation.PushAsync(new StoryCreatePage());
        StoriesStrip.Add(add);

        foreach (var story in await AppServices.Get<LocalStore>().GetActiveStoriesAsync())
        {
            var avatar = new Button
            {
                Text = string.IsNullOrEmpty(story.Text) ? "◉" : story.Text[..1].ToUpperInvariant(),
                WidthRequest = 44,
                HeightRequest = 44,
                CornerRadius = 22,
                BackgroundColor = (Color?)Application.Current?.Resources["Accent"] ?? Colors.CornflowerBlue,
                TextColor = Colors.White
            };
            avatar.Clicked += async (_, _) => await Navigation.PushAsync(new StoryViewerPage(story));
            StoriesStrip.Add(avatar);
        }
    }

}