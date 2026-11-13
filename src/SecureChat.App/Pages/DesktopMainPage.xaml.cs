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

        StatusPicker.Items.Add("Онлайн");
        StatusPicker.Items.Add("Неактивен");
        StatusPicker.Items.Add("Занят");

        // Индикатор соединения в шапке сайдбара
        ConnectionDot.TextColor = (Color)Application.Current!.Resources["Danger"];
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

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
        UserStatusLabel.Text = status;

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

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                _regular = regularAll;
                _archived = archivedAll;
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
        var source = folder == ChatFolders.Archive ? _archived : _regular;

        _suppressSelection = true;
        _conversations.Clear();

        foreach (var conversation in source)
        {
            if (!ChatFolders.Matches(folder, conversation))
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
                "Новый чат",
                "Введите имя пользователя:",
                "Открыть",
                "Отмена",
                "например, bob",
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
            await DisplayAlert("Не удалось добавить контакт", ex.Message, "OK");
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

    /// <summary>Вкладки: бейджи-счётчики по полному списку и подсветка активной.</summary>
    private void UpdateFolderTabs()
    {
        Color Res(string key) => (Color)Application.Current!.Resources[key];
        foreach (var button in new[]
                 {
                     FolderAllButton, FolderUnreadButton, FolderMutedButton, FolderArchiveButton,
                 })
        {
            var folder = (string)button.CommandParameter;
            button.Text = ChatFolders.TabText(folder, _regular, _archived.Count);
            var selected = string.Equals(folder, _session.ChatFolder, StringComparison.Ordinal);
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
            await DisplayAlert("Избранное",
                "Это служебный чат для сохранения сообщений. Его нельзя удалить или заархивировать.",
                "OK");
            return;
        }

        var archiveAction = vm.IsArchived ? "📤 Разархивировать" : "📦 Архивировать";
        var muteAction = vm.IsMuted ? "🔔 Включить звук" : "🔇 Без звука";
        var choice = await DisplayActionSheet(vm.Username, "Отмена", null,
            archiveAction, muteAction, "🧹 Удалить чат", "🧽 Очистить историю");

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
        else if (choice == "🧹 Удалить чат" || choice == "🧽 Очистить историю")
        {
            await DeleteOrClearConversationAsync(vm.Username, clearHistory: choice == "🧽 Очистить историю");
        }
    }

    /// <summary>
    /// Удаление чата или очистка истории: сначала выбор области («для всех» /
    /// «только у себя»), затем подтверждение.
    /// </summary>
    private async Task DeleteOrClearConversationAsync(string username, bool clearHistory)
    {
        var scopeChoice = await DisplayActionSheet(
            clearHistory ? "Очистить историю?" : "Удалить чат?",
            "Отмена",
            null,
            "Удалить для всех",
            clearHistory ? "Только у себя" : "Удалить для себя");

        if (scopeChoice is null || scopeChoice == "Отмена")
        {
            return;
        }

        var forEveryone = string.Equals(scopeChoice, "Удалить для всех", StringComparison.OrdinalIgnoreCase);

        var confirmed = await DisplayAlert(
            clearHistory ? "Очистить историю" : "Удалить чат",
            clearHistory
                ? forEveryone
                    ? $"История переписки с {username} будет удалена у вас и у собеседника."
                    : $"История переписки с {username} будет удалена только у вас."
                : forEveryone
                    ? $"Переписка с {username} будет удалена у вас и у собеседника."
                    : $"Чат с {username} будет удалён только у вас. Собеседник увидит историю как раньше.",
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
                ? forEveryone ? "История удалена у всех" : "История удалена только у вас"
                : "Не удалось очистить историю", isError: !ok);
        }
        else
        {
            ok = await _chatManager.DeleteConversationAsync(username, forEveryone);
            ShowHint(ok
                ? forEveryone ? "Чат удалён у всех" : "Чат удалён только у вас"
                : "Не удалось удалить чат", isError: !ok);

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
                "Добавить контакт",
                "Введите имя пользователя:",
                "Добавить",
                "Отмена",
                "например, bob",
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
            await DisplayAlert("Не удалось добавить контакт", ex.Message, "OK");
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
            await DisplayAlert("Не удалось открыть", ex.Message, "OK");
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
            await DisplayAlert("Не удалось открыть браузер", ex.Message, "OK");
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
            "Выход",
            "Завершить сеанс? Локальная история и ключи останутся на устройстве.",
            "Выйти",
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
            1 => "Неактивен",
            2 => "Занят",
            _ => "Онлайн"
        };

        _session.StatusMessage = status;
        UserStatusLabel.Text = status;

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
                : "Новое сообщение";
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
                "⚡ Офлайн-режим: сообщения сохраняются и отправятся после подключения.");
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
}