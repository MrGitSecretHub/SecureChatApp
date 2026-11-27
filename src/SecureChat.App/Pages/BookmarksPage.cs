using System.Collections.ObjectModel;
using SecureChat.App.Services;
using SecureChat.App.Views;

namespace SecureChat.App.Pages;

/// <summary>Закладки: сохранённые сообщения из всех чатов.</summary>
public sealed class BookmarksPage : ContentPage
{
    private sealed class Row
    {
        public required StoredMessageSearchResult Message { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Preview { get; init; } = string.Empty;
        public string Time { get; init; } = string.Empty;
    }

    private readonly ChatManager _chat = AppServices.Get<ChatManager>();
    private readonly ObservableCollection<Row> _rows = new();

    public BookmarksPage()
    {
        Title = Loc.T("bookmarks_title", "Bookmarks");
        var list = new CollectionView
        {
            ItemsSource = _rows,
            SelectionMode = SelectionMode.Single
        };
        list.SelectionChanged += OnSelected;
        list.ItemTemplate = new DataTemplate(() =>
        {
            var stack = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(4, 8) };
            var title = new Label { FontSize = 14, TextColor = (Color?)Application.Current?.Resources["TextPrimary"] ?? Colors.White };
            title.SetBinding(Label.TextProperty, nameof(Row.Title));
            var preview = new Label { FontSize = 12, TextColor = (Color?)Application.Current?.Resources["TextMuted"] ?? Colors.Gray };
            preview.SetBinding(Label.TextProperty, nameof(Row.Preview));
            var time = new Label { FontSize = 10, TextColor = (Color?)Application.Current?.Resources["TextMuted"] ?? Colors.Gray };
            time.SetBinding(Label.TextProperty, nameof(Row.Time));
            stack.Add(title);
            stack.Add(preview);
            stack.Add(time);
            return stack;
        });
        Content = list;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var items = await _chat.GetBookmarkedMessagesAsync();
        _rows.Clear();
        foreach (var item in items)
        {
            _rows.Add(new Row
            {
                Message = item,
                Title = item.Username,
                Preview = MessagePreview.Build(item.Text, 140),
                Time = DateTimeOffset.FromUnixTimeMilliseconds(item.SentAtUnixMs)
                    .ToLocalTime().ToString("dd.MM.yyyy HH:mm")
            });
        }
    }

    private async void OnSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not Row row) return;
        var list = (CollectionView)sender!;
        list.SelectedItem = null;
        var desktop = Navigation.NavigationStack.OfType<DesktopMainPage>().LastOrDefault();
        if (desktop is not null)
        {
            await Navigation.PopAsync();
            await desktop.OpenChatAsync(row.Message.Username);
        }
        else
        {
            await Navigation.PushAsync(new ChatPage(row.Message.Username));
        }
    }
}
