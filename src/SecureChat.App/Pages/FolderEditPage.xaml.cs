using System.Collections.ObjectModel;
using SecureChat.App.Services;
using SecureChat.App.Views;

namespace SecureChat.App.Pages;

/// <summary>
/// Создание и настройка пользовательской папки чатов: имя и список чатов внутри.
/// Открывается из «⋯» в панели вкладок списка чатов (Windows и Android).
/// </summary>
public partial class FolderEditPage : ContentPage
{
    private const string CreateItem = "＋ Создать папку";

    private readonly ChatManager _chatManager = AppServices.Get<ChatManager>();
    private readonly StoredChatFolder? _existing;
    private readonly ObservableCollection<FolderChatItem> _items = new();

    public FolderEditPage(StoredChatFolder? existing)
    {
        InitializeComponent();
        _existing = existing;
        ChatsView.ItemsSource = _items;

        if (existing is not null)
        {
            Title = "Папка: " + existing.Name;
            NameEntry.Text = existing.Name;
            DeleteButton.IsVisible = true;
        }
        else
        {
            Title = "Новая папка";
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        var regular = await _chatManager.GetConversationsAsync();
        var self = await _chatManager.GetOrCreateSelfConversationAsync();
        var chats = new List<StoredConversation> { self };
        chats.AddRange(regular.Where(c => c.Username != ChatManager.SelfChatUsername));

        var members = _existing?.Usernames ?? new List<string>();
        _items.Clear();
        foreach (var conversation in chats)
        {
            _items.Add(new FolderChatItem(
                conversation.Username,
                members.Contains(conversation.Username, StringComparer.OrdinalIgnoreCase)));
        }
    }

    private async void OnSaveClicked(object sender, EventArgs e)
    {
        var name = NameEntry.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            ShowError("Введите название папки.");
            return;
        }

        if (ChatFolders.IsReserved(name))
        {
            ShowError("Это имя занято служебной вкладкой, выберите другое.");
            return;
        }

        var usernames = _items.Where(item => item.IsChecked)
            .Select(item => item.Username)
            .ToList();

        var saved = await _chatManager.SaveChatFolderAsync(_existing?.Name, name, usernames);
        if (!saved)
        {
            ShowError("Папка с таким именем уже есть.");
            return;
        }

        await Navigation.PopAsync();
    }

    private async void OnDeleteClicked(object sender, EventArgs e)
    {
        if (_existing is null)
        {
            return;
        }

        var confirmed = await DisplayAlert(
            "Удалить папку",
            $"Папка «{_existing.Name}» исчезнет из панели вкладок. Сами чаты останутся во вкладке «Все».",
            "Удалить", "Отмена");
        if (!confirmed)
        {
            return;
        }

        await _chatManager.DeleteChatFolderAsync(_existing.Name);
        await Navigation.PopAsync();
    }

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.IsVisible = true;
    }
}
