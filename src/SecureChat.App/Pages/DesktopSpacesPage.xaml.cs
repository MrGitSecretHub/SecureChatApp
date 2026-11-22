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
            AddStrip("Мои пространства", mine);
            AddCards(mine);
        }
        var others = filtered.Where(s => string.IsNullOrEmpty(s.MyRole)).ToList();
        if (others.Count > 0)
        {
            AddStrip("Доступные", others);
            AddCards(others);
        }
    }

    private void AddStrip(string title, List<SpaceDto> items)
    {
        var row = new HorizontalStackLayout { Spacing = 8, VerticalOptions = LayoutOptions.Center };
        row.Children.Add(new Label { Text = title, FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = (Color?)Application.Current?.Resources["TextMuted"] ?? Colors.Gray, VerticalOptions = LayoutOptions.Center });
        foreach (var space in items.Take(8))
        {
            var chip = new Button
            {
                Text = string.IsNullOrEmpty(space.Name) ? "?" : space.Name[..1].ToUpperInvariant(),
                WidthRequest = 34, HeightRequest = 34, CornerRadius = 17, Padding = 0, FontSize = 12,
                BackgroundColor = (Color?)Application.Current?.Resources["AccentSoft"] ?? Colors.CornflowerBlue,
                TextColor = Colors.White
            };
            chip.Clicked += (_, _) => SelectSpace(space);
            row.Children.Add(chip);
        }
        SpaceGroups.Children.Add(row);
    }

    private void AddCards(List<SpaceDto> items)
    {
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var index = 0;
        foreach (var space in items)
        {
            var card = new Border
            {
                Style = (Style)Application.Current!.Resources["Card"],
                Padding = 12,
                MinimumHeightRequest = 84
            };
            var inner = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new ColumnDefinition(44), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 10 };
            var avatar = new Border
            {
                WidthRequest = 44, HeightRequest = 44,
                BackgroundColor = (Color?)Application.Current?.Resources["AccentSoft"] ?? Colors.CornflowerBlue,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.Ellipse(),
                Content = new Label { Text = string.IsNullOrEmpty(space.Name) ? "?" : space.Name[..1].ToUpperInvariant(), FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Colors.White, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center }
            };
            Grid.SetRow(avatar, 0); Grid.SetColumn(avatar, 0); inner.Children.Add(avatar);
            var info = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
            info.Children.Add(new Label { Text = space.Name, FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = (Color?)Application.Current?.Resources["TextPrimary"] ?? Colors.White, LineBreakMode = LineBreakMode.TailTruncation });
            info.Children.Add(new Label { Text = space.Type, FontSize = 11, TextColor = (Color?)Application.Current?.Resources["Accent"] ?? Colors.CornflowerBlue });
            info.Children.Add(new Label { Text = $"{space.MemberCount} участников", FontSize = 11, TextColor = (Color?)Application.Current?.Resources["TextMuted"] ?? Colors.Gray });
            Grid.SetRow(info, 0); Grid.SetColumn(info, 1); inner.Children.Add(info);
            card.Content = inner;
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => SelectSpace(space);
            card.GestureRecognizers.Add(tap);

            Grid.SetRow(card, index / 2);
            Grid.SetColumn(card, index % 2);
            grid.Children.Add(card);
            index++;
        }
        SpaceGroups.Children.Add(grid);
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
