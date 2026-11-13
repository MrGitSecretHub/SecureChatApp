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
            await ArchiveConversationAsync(vm.Username, !vm.IsArchived);
        }
        else if (choice == muteAction)
        {
            await _chatManager.SetConversationMutedAsync(vm.Username, !vm.IsMuted);
            await RefreshAsync();
        }
        else if (choice == "🧹 Удалить чат" || choice == "🧽 Очистить историю")
        {
            await DeleteOrClearConversationAsync(
                vm.Username,
                clearHistory: choice == "🧽 Очистить историю");
        }
    }

    /// <summary>
    /// Удаление чата или очистка истории: сначала выбор области («для всех» / «только у себя»), затем подтверждение.
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
                    : $"Чат с {username} будет удален только у вас. Собеседник увидит историю как раньше.",
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
                ShowHint(forEveryone ? "История удалена у всех" : "История удалена только у вас");
            }
            else
            {
                ShowHint("Не удалось очистить историю", isError: true);
            }
        }
        else
        {
            ok = await _chatManager.DeleteConversationAsync(username, forEveryone);
            if (ok)
            {
                await RefreshAsync();
                ShowHint(forEveryone ? "Чат удален у всех" : "Чат удален только у вас");
            }
            else
            {
                ShowHint("Не удалось удалить чат", isError: true);
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
            "⚡ Офлайн-режим: история доступна, новые сообщения отправятся после подключения.";
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

            var folder = _session.ChatFolder;
            var source = folder == ChatFolders.Archive ? _archived : _regular;

            _conversations.Clear();
            foreach (var conversation in source)
            {
                if (!ChatFolders.Matches(folder, conversation))
                {
                    continue;
                }

                _conversations.Add(ConversationVm.From(conversation));
            }

            UpdateFolderTabs();
        }
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
