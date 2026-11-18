using System.Collections.ObjectModel;
using SecureChat.App.Services;

namespace SecureChat.App.Pages;

public partial class ContactsPage : ContentPage
{
    private readonly ChatManager _chatManager = AppServices.Get<ChatManager>();
    private readonly ObservableCollection<ContactVm> _contacts = new();
    private bool _subscribed;

    public ContactsPage()
    {
        InitializeComponent();
        ContactsView.ItemsSource = _contacts;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (!_subscribed)
        {
            _subscribed = true;
            _chatManager.ConversationsChanged += OnConversationsChanged;
        }

        await RefreshAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        if (_subscribed)
        {
            _subscribed = false;
            _chatManager.ConversationsChanged -= OnConversationsChanged;
        }
    }

    private void OnConversationsChanged() =>
        MainThread.BeginInvokeOnMainThread(async () => await RefreshAsync());

    private async Task RefreshAsync()
    {
        if (!AppServices.Get<AppSession>().IsAuthenticated)
        {
            return;
        }

        var conversations = await _chatManager.GetConversationsAsync();
        _contacts.Clear();
        foreach (var conversation in conversations.OrderBy(
                     item => item.Username,
                     StringComparer.OrdinalIgnoreCase))
        {
            _contacts.Add(new ContactVm
            {
                Username = conversation.Username,
                Fingerprint = conversation.Fingerprint,
                KeysChanged = conversation.KeysChanged
            });
        }
    }

    private async void OnAddClicked(object sender, EventArgs e)
    {
        var username = UsernameEntry.Text?.Trim() ?? string.Empty;
        if (username.Length < 3)
        {
            await DisplayAlert("Контакты", Loc.T("uc_005", "Введите имя пользователя."), "OK");
            return;
        }

        AddButton.IsEnabled = false;

        try
        {
            var conversation = await _chatManager.AddContactAsync(username);
            UsernameEntry.Text = string.Empty;
            await RefreshAsync();
            await Navigation.PushAsync(new ChatPage(conversation.Username));
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("uc_026", "Не удалось добавить контакт"), ex.Message, "OK");
        }
        finally
        {
            AddButton.IsEnabled = true;
        }
    }

    private async void OnContactSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not ContactVm contact)
        {
            return;
        }

        ContactsView.SelectedItem = null;
        await Navigation.PushAsync(new ChatPage(contact.Username));
    }

    private async void OnOpenChatClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: ContactVm contact })
        {
            return;
        }

        await Navigation.PushAsync(new ChatPage(contact.Username));
    }
}

public sealed class ContactVm
{
    public required string Username { get; init; }
    public required string Fingerprint { get; init; }
    public bool KeysChanged { get; init; }
    public string Initial => string.IsNullOrWhiteSpace(Username)
        ? "?"
        : Username[..1].ToUpperInvariant();
}
