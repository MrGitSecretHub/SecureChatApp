using SecureChat.App.Pages;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using Microsoft.Maui.Controls;
using SecureChat.App.Services;
using SecureChat.App.Views;

namespace SecureChat.App.Pages;

public partial class ChatsPage : ContentPage
{
    private readonly ChatManager _chatManager = AppServices.Get<ChatManager>();
    private readonly AccountService _account = AppServices.Get<AccountService>();
    private readonly AppSession _session = AppServices.Get<AppSession>();
    private readonly ObservableCollection<ConversationVm> _conversations = new();
    // Полный список нужен для бейджей вкладок даже когда показан архив.
    private IReadOnlyList<StoredConversation> _regular = Array.Empty<StoredConversation>();
    private IReadOnlyList<StoredConversation> _archived = Array.Empty<StoredConversation>();
    private IReadOnlyList<StoredChatFolder> _folders = Array.Empty<StoredChatFolder>();
    private bool _subscribed;
    private CancellationTokenSource? _hintCts;

    public ChatsPage()
    {
        InitializeComponent();
        ConversationsView.ItemsSource = _conversations;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (!_subscribed)
        {
            _subscribed = true;
            _chatManager.ConversationsChanged += OnConversationsChanged;
            _chatManager.ConversationDeleted += OnConversationDeleted;
            _chatManager.Error += OnError;
            _chatManager.ConnectionStateChanged += OnConnectionStateChanged;
        }

        await RefreshAsync();
        UpdateConnectionStatus(!_chatManager.IsOffline);

        // Android 13+: разрешение на уведомления запрашиваем и при автовходе,
        // когда экран логина не показывался.
        AppServices.Get<NotificationService>().EnsurePermission();

        // Уведомление могло быть нажато до того, как экран чатов появился.
        var pending = AppServices.Get<NotificationService>().ConsumePendingUsername();
        if (!string.IsNullOrWhiteSpace(pending))
        {
            await Navigation.PushAsync(new ChatPage(pending));
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        if (_subscribed)
        {
            _subscribed = false;
            _chatManager.ConversationsChanged -= OnConversationsChanged;
            _chatManager.ConversationDeleted -= OnConversationDeleted;
            _chatManager.Error -= OnError;
            _chatManager.ConnectionStateChanged -= OnConnectionStateChanged;
        }
    }

    private void OnConversationsChanged() =>
        MainThread.BeginInvokeOnMainThread(async () => await RefreshAsync());

    private void OnError(string message) =>
        MainThread.BeginInvokeOnMainThread(() => ShowStatus(message));

    private void OnConnectionStateChanged(bool connected) =>
        MainThread.BeginInvokeOnMainThread(() => UpdateConnectionStatus(connected));

    private void OnConversationDeleted(string username) =>
        MainThread.BeginInvokeOnMainThread(async () => await RefreshAsync());

    /// <summary>Переключение вкладки списка чатов («Все», «Непрочитанные», …).</summary>
    private void OnFolderClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string folder })
        {
            return;
        }

        _session.ChatFolder = folder;
        _ = RefreshAsync();
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

    private async Task ArchiveConversationAsync(string username, bool archived)
    {
        await _chatManager.ArchiveConversationAsync(username, archived);
        await RefreshAsync();
    }

    private async void OnConversationMenuClicked(object sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: ConversationVm vm } ||
            string.IsNullOrWhiteSpace(vm.Username))
        {
            return;
        }

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
            await ArchiveConversationAsync(vm.Username, !vm.IsArchived);
        }
        else if (choice == muteAction)
        {
            await _chatManager.SetConversationMutedAsync(vm.Username, !vm.IsMuted);
            await RefreshAsync();
        }
        else if (choice == Loc.T("dg_097", "🧹 Удалить чат") || choice == Loc.T("dg_098", "🧽 Очистить историю"))
        {
            await DeleteOrClearConversationAsync(
                vm.Username,
                clearHistory: choice == Loc.T("dg_098", "🧽 Очистить историю"));
        }
    }

    /// <summary>
    /// Удаление чата или очистка истории: сначала выбор области («для всех» / «только у себя»), затем подтверждение.
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
                    : string.Format(Loc.T("dg_085"), username),
            "Удалить", "Отмена") == true;
        if (!confirmed)
        {
            return;
        }

        bool ok;
        if (clearHistory)
        {
            ok = await _chatManager.ClearConversationAsync(username, forEveryone);
            if (ok)
            {
                await RefreshAsync();
                ShowHint(forEveryone ? Loc.T("rs_058", "История удалена у всех") : Loc.T("rs_057", "История удалена только у вас"));
            }
            else
            {
                ShowHint(Loc.T("rs_089", "Не удалось очистить историю"), isError: true);
            }
        }
        else
        {
            ok = await _chatManager.DeleteConversationAsync(username, forEveryone);
            if (ok)
            {
                await RefreshAsync();
                ShowHint(forEveryone ? Loc.T("rs_170", "Чат удален у всех") : Loc.T("rs_169", "Чат удален только у вас"));
            }
            else
            {
                ShowHint(Loc.T("rs_098", "Не удалось удалить чат"), isError: true);
            }
        }
    }

    private void UpdateConnectionStatus(bool connected)
    {
        if (connected)
        {
            StatusLabel.IsVisible = false;
            return;
        }

        StatusLabel.Text =
            Loc.T("uc_074", "⚡ Офлайн-режим: история доступна, новые сообщения отправятся после подключения.");
        StatusLabel.TextColor = (Color)Application.Current!.Resources["Warning"];
        StatusLabel.IsVisible = true;
    }

    private async Task RefreshAsync()
    {
        if (_session.IsAuthenticated)
        {
            var regular = await _chatManager.GetConversationsAsync();

            // «Избранное» (чат с самим собой) всегда показывается в начале списка.
            var self = await _chatManager.GetOrCreateSelfConversationAsync();
            _regular = new[] { self }.Concat(regular
                .Where(c => c.Username != ChatManager.SelfChatUsername))
                .ToList();
            _archived = await _chatManager.GetArchivedConversationsAsync();
            _folders = await _chatManager.GetChatFoldersAsync();
            RebuildFolderChips();

            var folder = _session.ChatFolder;
            var custom = ChatFolders.Find(_folders, folder);
            if (custom is null && !ChatFolders.IsFixed(folder))
            {
                // Папку удалили или переименовали — возвращаемся к «Все».
                folder = _session.ChatFolder = ChatFolders.All;
            }

            var source = folder == ChatFolders.Archive ? _archived : _regular;

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

                _conversations.Add(ConversationVm.From(conversation));
            }

            UpdateFolderTabs();
        }
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

    private async void OnConversationSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not ConversationVm conversation)
        {
            return;
        }

        ConversationsView.SelectedItem = null;
        await Navigation.PushAsync(new ChatPage(conversation.Username));
    }

    private async void OnAddContactClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("//contacts");
    }

    private async void OnSearchMessagesPressed(object sender, EventArgs e)
    {
        var query = (sender as SearchBar)?.Text?.Trim() ?? string.Empty;
        await Navigation.PushAsync(new GlobalSearchPage(query));
    }

    private async void OnMyProfileClicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(AppServices.Get<SettingsPage>());
    }

    private async void OnSpacesClicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new SpacesPage());
    }

    private async void OnBrowserClicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new BrowserPage());
    }

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

    private void ShowStatus(string message)
    {
        StatusLabel.Text = message;
        StatusLabel.IsVisible = true;
    }

    private void ShowHint(string text, bool isError = false)
    {
        HintLabel.Text = text;
        HintLabel.TextColor = isError ? (Color)Application.Current!.Resources["Warning"] : (Color)Application.Current!.Resources["TextMuted"];
        HintLabel.IsVisible = true;

        _hintCts?.Cancel();
        _hintCts = new CancellationTokenSource();
        _ = HideHintLaterAsync(isError ? 6000 : 4000, _hintCts.Token);
    }

    private void HideHint()
    {
        _hintCts?.Cancel();
        HintLabel.IsVisible = false;
    }

    private async Task HideHintLaterAsync(int delayMs, CancellationToken token)
    {
        try
        {
            await Task.Delay(delayMs, token);
            MainThread.BeginInvokeOnMainThread(() => HintLabel.IsVisible = false);
        }
        catch (OperationCanceledException)
        {
            // Новая подсказка заменила предыдущую.
        }
    }

}
