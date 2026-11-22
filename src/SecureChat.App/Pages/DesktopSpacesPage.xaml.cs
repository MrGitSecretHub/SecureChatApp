using SecureChat.App.Services;
using SecureChat.Core.Contracts;

namespace SecureChat.App.Pages;

public partial class DesktopSpacesPage : ContentPage
{
    private readonly SpaceService _spaces = AppServices.Get<SpaceService>();
    private List<SpaceDto> _all = new();
    private SpaceDto? _selected;
    private string _filter = "all";

    public DesktopSpacesPage()
    {
        InitializeComponent();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _all = await _spaces.ListAsync();
            ApplyFilter();
            await RefreshStoriesAsync();
        }
        catch (Exception ex)
        {
            DetailSubtitle.Text = ex.Message;
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnFilterClicked(object sender, EventArgs e)
    {
        if (sender is not Button b) return;
        _filter = b.ClassId ?? "all";
        var pills = new[] { AllFilterButton, GroupsFilterButton, ChannelsFilterButton, ForumsFilterButton, CommunitiesFilterButton };
        foreach (var pill in pills)
            pill.Style = pill == b ? (Style)Resources["DSFilterPillActive"] : (Style)Resources["DSFilterPill"];
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        SpaceGroups.Clear();
        var query = SearchEntry.Text?.Trim();
        var filtered = _all.Where(s =>
            (_filter == "all" || s.Type == _filter) &&
            (string.IsNullOrWhiteSpace(query) ||
             s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             s.Description.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();

        var mine = filtered.Where(s => !string.IsNullOrEmpty(s.MyRole)).ToList();
        if (mine.Count > 0)
        {
            SpaceGroups.Children.Add(new Label
            {
                Text = $"Мои пространства ({mine.Count})",
                FontSize = 13, FontAttributes = FontAttributes.Bold,
                TextColor = (Color?)Application.Current?.Resources["TextPrimary"] ?? Colors.White,
                Margin = new Thickness(6, 8, 6, 4)
            });
            AddCards(mine);
        }
        var others = filtered.Where(s => string.IsNullOrEmpty(s.MyRole)).ToList();
        if (others.Count > 0)
        {
            SpaceGroups.Children.Add(new Label
            {
                Text = $"Доступные ({others.Count})",
                FontSize = 13, FontAttributes = FontAttributes.Bold,
                TextColor = (Color?)Application.Current?.Resources["TextPrimary"] ?? Colors.White,
                Margin = new Thickness(6, 8, 6, 4)
            });
            AddCards(others);
        }
    }

    private void AddCards(List<SpaceDto> items)
    {
        foreach (var space in items)
        {
            var avatar = new Border
            {
                WidthRequest = 38, HeightRequest = 38,
                BackgroundColor = (Color?)Application.Current?.Resources["AccentSoft"] ?? Colors.CornflowerBlue,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.Ellipse(),
                Content = new Label
                {
                    Text = string.IsNullOrEmpty(space.Name) ? "?" : space.Name[..1].ToUpperInvariant(),
                    FontSize = 14, FontAttributes = FontAttributes.Bold,
                    TextColor = Colors.White,
                    HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center
                }
            };
            var info = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
            info.Children.Add(new Label
            {
                Text = space.Name,
                FontSize = 14, FontAttributes = FontAttributes.Bold,
                TextColor = (Color?)Application.Current?.Resources["TextPrimary"] ?? Colors.White,
                LineBreakMode = LineBreakMode.TailTruncation
            });
            info.Children.Add(new Label
            {
                Text = $"{space.Type} • {space.MemberCount} участников",
                FontSize = 11,
                TextColor = (Color?)Application.Current?.Resources["TextMuted"] ?? Colors.Gray,
                LineBreakMode = LineBreakMode.TailTruncation
            });
            var meta = new Label
            {
                Text = space.LastMessageAtUnixMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(space.LastMessageAtUnixMs).ToLocalTime().ToString("dd.MM")
                    : string.Empty,
                FontSize = 10,
                TextColor = (Color?)Application.Current?.Resources["TextDisabled"] ?? Colors.DimGray,
                VerticalOptions = LayoutOptions.Center
            };
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(6, 4), MinimumHeightRequest = 52 };
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(38)));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetRow(avatar, 0); Grid.SetColumn(avatar, 0); row.Children.Add(avatar);
            Grid.SetRow(info, 0); Grid.SetColumn(info, 1); row.Children.Add(info);
            Grid.SetRow(meta, 0); Grid.SetColumn(meta, 2); row.Children.Add(meta);
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => SelectSpace(space);
            row.GestureRecognizers.Add(tap);
            SpaceGroups.Children.Add(row);
        }
    }

    private void SelectSpace(SpaceDto space)
    {
        _selected = space;
        DetailSubtitle.Text = string.Empty;
        DetailBody.IsVisible = true;
        DetailName.Text = space.Name;
        DetailType.Text = space.Type;
        DetailMembers.Text = $"{space.MemberCount} участников" + (space.TopicCount > 0 ? $", {space.TopicCount} тем" : string.Empty);
        DetailDescription.Text = space.Description;
    }

    private async void OnOpenSpaceClicked(object sender, EventArgs e)
    {
        if (_selected is null) return;
        await Navigation.PushAsync(new SpaceChatPage(_selected.Id, _selected.Name, _selected.Type, _selected.CanPost));
    }

    private async void OnDetailStoriesClicked(object sender, EventArgs e)
    {
        if (_selected is null) return;
        var items = (await AppServices.Get<StoryService>().GetAsync())
            .Where(s => s.SpaceId == _selected.Id).OrderByDescending(s => s.CreatedAtUnixMs).ToList();
        if (items.Count == 0)
        {
            await DisplayAlert("Истории", "В этом пространстве пока нет историй.", "OK");
            return;
        }
        await Navigation.PushAsync(new StoryViewerPage(items, 0));
    }

    private async Task RefreshStoriesAsync()
    {
        StoriesStrip.Clear();
        try
        {
            var items = (await AppServices.Get<StoryService>().GetAsync())
                .Where(s => !string.IsNullOrWhiteSpace(s.SpaceId)).ToList();
            if (items.Count == 0)
            {
                StoriesStrip.Children.Add(new Label { Text = "Нет историй", FontSize = 12, TextColor = (Color?)Application.Current?.Resources["TextMuted"] ?? Colors.Gray, VerticalOptions = LayoutOptions.Center });
                return;
            }
            foreach (var group in items.GroupBy(s => s.SpaceId!, StringComparer.OrdinalIgnoreCase))
            {
                var list = group.OrderByDescending(s => s.CreatedAtUnixMs).ToList();
                var space = _all.FirstOrDefault(s => s.Id == group.Key);
                var btn = new Button
                {
                    Text = string.IsNullOrEmpty(space?.Name) ? "◉" : space.Name[..1].ToUpperInvariant(),
                    WidthRequest = 36, HeightRequest = 36, CornerRadius = 18, Padding = 0, FontSize = 12,
                    TextColor = Colors.White,
                    BackgroundColor = list.Any(s => !s.ViewedByMe)
                        ? (Color?)Application.Current?.Resources["Accent"] ?? Colors.CornflowerBlue
                        : (Color?)Application.Current?.Resources["SurfaceAlt"] ?? Colors.Gray
                };
                btn.Clicked += async (_, _) => await Navigation.PushAsync(new StoryViewerPage(list, 0));
                StoriesStrip.Children.Add(btn);
            }
        }
        catch { }
    }

    private async void OnStoriesAddClicked(object sender, EventArgs e)
    {
        var action = await DisplayActionSheet("История пространства", "Отмена", null,
            _all.Where(s => !string.IsNullOrEmpty(s.MyRole)).Select(s => s.Name).ToArray());
        if (string.IsNullOrWhiteSpace(action)) return;
        var space = _all.FirstOrDefault(s => s.Name == action);
        if (space is null) return;
        await Navigation.PushAsync(new StoryCreatePage(space.Id));
    }

    private async void OnCreateSpaceClicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new SpacesPage());
    }
}
