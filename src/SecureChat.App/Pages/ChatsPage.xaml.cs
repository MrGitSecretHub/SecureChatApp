using System.Collections.ObjectModel;
using Microsoft.Maui.Controls;
using SecureChat.App.Services;
using SecureChat.App.Views;

namespace SecureChat.App.Pages;

public partial class ChatsPage : ContentPage
{
    private readonly ChatManager _chatManager = AppServices.Get<ChatManager>();
    private readonly AccountService _account = AppServices.Get<AccountService>();
    private readonly ObservableCollection<ConversationVm> _conversations = new();
    private bool _subscribed;
    private bool _isArchiveMode;

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
            _chatManager.Error += OnError;
            _chatManager.ConnectionStateChanged += OnConnectionStateChanged;
        }

        await RefreshAsync();
        UpdateConnectionStatus(!_chatManager.IsOffline);

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

    private async void OnToggleArchiveModeClicked(object sender, EventArgs e)
    {
        _isArchiveMode = !_isArchiveMode;
        ArchiveToggleButton.Text = _isArchiveMode ? "💬" : "📦";
        ToolTipProperties.SetText(ArchiveToggleButton, _isArchiveMode
            ? "Показать обычные чаты"
            : "Показать архив чатов");
        await RefreshAsync();
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
            archiveAction, muteAction, "🧹 Удалить чат");

        if (choice == archiveAction)
        {
            await ArchiveConversationAsync(vm.Username, !vm.IsArchived);
        }
        else if (choice == muteAction)
        {
            await _chatManager.SetConversationMutedAsync(vm.Username, !vm.IsMuted);
            await RefreshAsync();
        }
        else if (choice == "🧹 Удалить чат")
        {
            var confirmed = await DisplayAlert(
                "Удалить чат",
                $"Удалить чат с {vm.Username}? Сообщения будут удалены с этого устройства.",
                "Удалить", "Отмена");
            if (confirmed)
            {
                var deleted = await _chatManager.DeleteConversationAsync(vm.Username);
                if (deleted)
                {
                    await RefreshAsync();
                }
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
        if (AppServices.Get<AppSession>().IsAuthenticated)
        {
            var conversations = _isArchiveMode
                ? await _chatManager.GetArchivedConversationsAsync()
                : await _chatManager.GetConversationsAsync();

            _conversations.Clear();
            foreach (var conversation in conversations)
            {
                _conversations.Add(ConversationVm.From(conversation));
            }
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
}
