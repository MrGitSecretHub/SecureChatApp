using System.Collections.ObjectModel;
using SecureChat.App.Services;
using SecureChat.App.Views;

namespace SecureChat.App.Pages;

/// <summary>Менеджер кэша вложений: разбивка по чатам и выборочная очистка.</summary>
public sealed class MediaManagerPage : ContentPage
{
    private static readonly Color[] Palette =
    {
        Color.FromArgb("#5865F2"), Color.FromArgb("#26A69A"), Color.FromArgb("#EF5350"),
        Color.FromArgb("#FFA726"), Color.FromArgb("#7E57C2"), Color.FromArgb("#42A5F5"),
        Color.FromArgb("#66BB6A"), Color.FromArgb("#EC407A")
    };

    private sealed class Row
    {
        public required LocalStore.MediaCacheGroup Group { get; init; }
        public required Color Color { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Subtitle { get; init; } = string.Empty;
    }

    private readonly LocalStore _store = AppServices.Get<LocalStore>();
    private readonly ObservableCollection<Row> _rows = new();
    private readonly Label _summary;
    private readonly Label _empty;
    private readonly MediaDonutChart _chart;
    private readonly Label _chartTotal;
    private readonly Picker _modePicker;
    private int _mode;

    public MediaManagerPage()
    {
        Title = Loc.T("media_manager_title", "Files and memory");
        _summary = new Label
        {
            FontSize = 13,
            TextColor = (Color?)Application.Current?.Resources["TextMuted"] ?? Colors.Gray
        };
        _empty = new Label
        {
            Text = Loc.T("media_manager_empty", "No cached files"),
            FontSize = 14,
            HorizontalOptions = LayoutOptions.Center,
            TextColor = (Color?)Application.Current?.Resources["TextMuted"] ?? Colors.Gray,
            IsVisible = false
        };
        _chart = new MediaDonutChart
        {
            HeightRequest = 190,
            HorizontalOptions = LayoutOptions.Center,
            WidthRequest = 260
        };
        _modePicker = new Picker
        {
            Title = Loc.T("media_mode_chat", "By chat"),
            ItemsSource = new[]
            {
                Loc.T("media_mode_chat", "By chat"),
                Loc.T("media_mode_type", "By type")
            },
            SelectedIndex = 0,
            HorizontalOptions = LayoutOptions.Fill
        };
        _modePicker.SelectedIndexChanged += async (_, _) =>
        {
            _mode = _modePicker.SelectedIndex;
            await RefreshAsync();
        };

        _chartTotal = new Label
        {
            FontSize = 16,
            FontAttributes = FontAttributes.Bold,
            TextColor = (Color?)Application.Current?.Resources["TextPrimary"] ?? Colors.White,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };

        var chartBox = new Grid
        {
            HeightRequest = 190,
            HorizontalOptions = LayoutOptions.Center,
            WidthRequest = 260,
            Children = { _chart, _chartTotal }
        };

        var list = new CollectionView { ItemsSource = _rows, SelectionMode = SelectionMode.None };
        list.ItemTemplate = new DataTemplate(() =>
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)
                },
                ColumnSpacing = 10,
                Padding = new Thickness(4, 8)
            };
            var swatch = new BoxView
            {
                WidthRequest = 10,
                HeightRequest = 10,
                CornerRadius = 3,
                VerticalOptions = LayoutOptions.Center
            };
            swatch.SetBinding(BoxView.ColorProperty, nameof(Row.Color));
            grid.Add(swatch, 0, 0);

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
            grid.Add(stack, 1, 0);

            var clear = new Button
            {
                Text = Loc.T("media_manager_clear", "Clear"),
                FontSize = 12,
                VerticalOptions = LayoutOptions.Center
            };
            clear.SetBinding(Button.CommandParameterProperty, nameof(Row.Group));
            clear.Clicked += OnClearGroupClicked;
            grid.Add(clear, 2, 0);
            return grid;
        });

        var clearAll = new Button
        {
            Text = Loc.T("media_manager_clear_all", "Clear all"),
            Style = (Style?)Application.Current?.Resources["SecondaryButton"]
        };
        clearAll.Clicked += OnClearAllClicked;

        Content = new VerticalStackLayout
        {
            Padding = 16,
            Spacing = 10,
            Children = { _summary, _modePicker, chartBox, _empty, list, clearAll }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshAsync();
    }

    private static string DisplayName(LocalStore.MediaCacheGroup group)
    {
        if (!group.Key.StartsWith("type:", StringComparison.Ordinal))
            return (group.IsSpace ? "🌐 " : "💬 ") + group.Name;

        return group.Name switch
        {
            "Photos" => "🖼 " + Loc.T("media_type_photos", "Photos"),
            "Videos" => "🎬 " + Loc.T("media_type_videos", "Videos"),
            "Voice" => "🎙 " + Loc.T("media_type_voice", "Voice"),
            _ => "📄 " + Loc.T("media_type_files", "Files")
        };
    }

    private static string SizeText(long bytes) =>
        bytes >= 1024 * 1024
            ? $"{bytes / (1024.0 * 1024.0):0.0} MB"
            : $"{bytes / 1024.0:0.0} KB";

    private async Task RefreshAsync()
    {
        var groups = _mode == 0
            ? await _store.GetMediaCacheBreakdownAsync()
            : await _store.GetMediaTypeBreakdownAsync();
        _rows.Clear();
        var segments = new List<MediaDonutSegment>();
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var color = Palette[i % Palette.Length];
            segments.Add(new MediaDonutSegment(color, group.Bytes));
            _rows.Add(new Row
            {
                Group = group,
                Color = color,
                Title = DisplayName(group),
                Subtitle = $"{SizeText(group.Bytes)} · {group.Files}"
            });
        }

        _chart.Segments = segments;
        var total = groups.Sum(g => g.Bytes);
        var files = groups.Sum(g => g.Files);
        _chartTotal.Text = SizeText(total);
        _summary.Text = string.Format(
            Loc.T("media_manager_summary", "Cached files: {0} · {1}"),
            files, SizeText(total));
        _empty.IsVisible = groups.Count == 0;
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
