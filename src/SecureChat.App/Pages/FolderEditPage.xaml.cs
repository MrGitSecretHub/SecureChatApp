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
    private static readonly string CreateItem = Loc.T("rs_196", "＋ Создать папку");

    private static readonly (string Name, string Hex)[] Colors =
    {
        ("Telegram", "#3390EC"), ("Discord", "#5865F2"), ("Green", "#26A69A"),
        ("Red", "#EF5350"), ("Orange", "#FFA726"), ("Purple", "#7E57C2")
    };

    private readonly ChatManager _chatManager = AppServices.Get<ChatManager>();
    private readonly StoredChatFolder? _existing;
    private readonly ObservableCollection<FolderChatItem> _items = new();

    public FolderEditPage(StoredChatFolder? existing)
    {
        InitializeComponent();
        _existing = existing;
        ChatsView.ItemsSource = _items;
        foreach (var (name, _) in Colors) ColorPicker.Items.Add(name);
        ColorPicker.SelectedIndex = 0;
        ColorPicker.SelectedIndexChanged += (_, _) =>
        {
            if (ColorPicker.SelectedIndex >= 0 && ColorPicker.SelectedIndex < Colors.Length)
                ColorEntry.Text = Colors[ColorPicker.SelectedIndex].Hex;
        };

        if (existing is not null)
        {
            Title = "Папка: " + existing.Name;
            NameEntry.Text = existing.Name;
            IconEntry.Text = existing.Icon;
            ColorEntry.Text = existing.Color;
            var index = Array.FindIndex(Colors, c => c.Hex == existing.Color);
            if (index >= 0) ColorPicker.SelectedIndex = index;
            DeleteButton.IsVisible = true;
        }
        else
        {
            Title = Loc.T("rs_107", "Новая папка");
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
            ShowError(Loc.T("rs_017", "Введите название папки."));
            return;
        }

        if (ChatFolders.IsReserved(name))
        {
            ShowError(Loc.T("rs_174", "Это имя занято служебной вкладкой, выберите другое."));
            return;
        }

        var usernames = _items.Where(item => item.IsChecked)
            .Select(item => item.Username)
            .ToList();

        var saved = await _chatManager.SaveChatFolderAsync(
            _existing?.Name, name, usernames,
            IconEntry.Text?.Trim() ?? "📁",
            ColorEntry.Text?.Trim() ?? string.Empty);
        if (!saved)
        {
            ShowError(Loc.T("rs_120", "Папка с таким именем уже есть."));
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
            string.Format(Loc.T("dg_044"), _existing.Name),
            Loc.T("common_delete", "Удалить"), Loc.T("common_cancel", "Отмена"));
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
