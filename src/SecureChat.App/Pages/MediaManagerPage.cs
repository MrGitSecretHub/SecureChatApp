using System.Collections.ObjectModel;
using SecureChat.App.Services;
using SecureChat.App.Views;

namespace SecureChat.App.Pages;

/// <summary>Менеджер кэша вложений: разбивка по чатам и выборочная очистка.</summary>
public sealed class MediaManagerPage : ContentPage
{
    private sealed class Row
    {
        public required LocalStore.MediaCacheGroup Group { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Subtitle { get; init; } = string.Empty;
    }

    private readonly LocalStore _store = AppServices.Get<LocalStore>();
    private readonly ObservableCollection<Row> _rows = new();
    private readonly Label _summary;

    public MediaManagerPage()
    {
        Title = Loc.T("media_manager_title", "Files and memory");
        _summary = new Label
        {
            FontSize = 13,
            TextColor = (Color?)Application.Current?.Resources["TextMuted"] ?? Colors.Gray
        };

        var list = new CollectionView { ItemsSource = _rows, SelectionMode = SelectionMode.None };
        list.ItemTemplate = new DataTemplate(() =>
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(GridLength.Star), new(GridLength.Auto)
                },
                ColumnSpacing = 10,
                Padding = new Thickness(4, 8)
            };
            var stack = new VerticalStackLayout { Spacing = 2 };
            var title = new Label
            {
                FontSize = 14,
                TextColor = (Color?)Application.Current?.Resources["TextPrimary"] ?? Colors.White
            };
            title.SetBinding(Label.TextProperty, nameof(Row.Title));
            var subtitle = new Label
            {
                FontSize = 11,
                TextColor = (Color?)Application.Current?.Resources["TextMuted"] ?? Colors.Gray
            };
            subtitle.SetBinding(Label.TextProperty, nameof(Row.Subtitle));
            stack.Add(title);
            stack.Add(subtitle);
            grid.Add(stack, 0, 0);

            var clear = new Button
            {
                Text = Loc.T("media_manager_clear", "Clear"),
                FontSize = 12,
                VerticalOptions = LayoutOptions.Center
            };
            clear.SetBinding(Button.CommandParameterProperty, nameof(Row.Group));
            clear.Clicked += OnClearGroupClicked;
            grid.Add(clear, 1, 0);
            return grid;
        });

        var clearAll = new Button
        {
            Text = Loc.T("media_manager_clear_all", "Clear all"),
            Style = (Style?)Application.Current?.Resources["SecondaryButton"]
        };
        clearAll.Clicked += OnClearAllClicked;

        var empty = new Label
        {
            Text = Loc.T("media_manager_empty", "No cached files"),
            FontSize = 14,
            HorizontalOptions = LayoutOptions.Center,
            TextColor = (Color?)Application.Current?.Resources["TextMuted"] ?? Colors.Gray
        };
        empty.SetBinding(Label.IsVisibleProperty, new Binding
        {
            Source = _rows,
            Path = nameof(_rows.Count),
            Converter = new InvertedBoolConverter()
        });

        Content = new VerticalStackLayout
        {
            Padding = 16,
            Spacing = 10,
            Children = { _summary, empty, list, clearAll }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshAsync();
    }

    private static string SizeText(long bytes) =>
        bytes >= 1024 * 1024
            ? $"{bytes / (1024.0 * 1024.0):0.0} MB"
            : $"{bytes / 1024.0:0.0} KB";

    private async Task RefreshAsync()
    {
        var groups = await _store.GetMediaCacheBreakdownAsync();
        _rows.Clear();
        foreach (var group in groups)
        {
            _rows.Add(new Row
            {
                Group = group,
                Title = (group.IsSpace ? "🌐 " : "💬 ") + group.Name,
                Subtitle = $"{SizeText(group.Bytes)} · {group.Files}"
            });
        }
        var total = groups.Sum(g => g.Bytes);
        var files = groups.Sum(g => g.Files);
        _summary.Text = string.Format(
            Loc.T("media_manager_summary", "Cached files: {0} · {1}"),
            files, SizeText(total));
    }

    private async void OnClearGroupClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: LocalStore.MediaCacheGroup group }) return;
        var confirmed = await DisplayAlert(
            Loc.T("media_manager_clear", "Clear"),
            string.Format(Loc.T("media_manager_confirm", "Clear files for {0}?"), group.Name),
            Loc.T("media_manager_clear", "Clear"), Loc.T("common_cancel", "Cancel"));
        if (!confirmed) return;
        await _store.ClearMediaCacheAsync(group.Ids);
        await RefreshAsync();
    }

    private async void OnClearAllClicked(object? sender, EventArgs e)
    {
        var confirmed = await DisplayAlert(
            Loc.T("media_manager_clear_all", "Clear all"),
            Loc.T("media_manager_confirm", "Clear files for {0}?"),
            Loc.T("media_manager_clear_all", "Clear all"), Loc.T("common_cancel", "Cancel"));
        if (!confirmed) return;
        await _store.ClearMediaCacheAsync();
        await RefreshAsync();
    }
}
