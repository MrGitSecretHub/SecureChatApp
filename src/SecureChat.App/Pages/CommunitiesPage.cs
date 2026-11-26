using System.Collections.ObjectModel;
using SecureChat.App.Services;
using SecureChat.App.Views;
using SecureChat.Core.Contracts;

namespace SecureChat.App.Pages;

/// <summary>Каталог публичных сообществ с поиском и вступлением.</summary>
public sealed class CommunitiesPage : ContentPage
{
    private readonly SpaceService _spaces = AppServices.Get<SpaceService>();
    private readonly ObservableCollection<SpaceVm> _items = new();
    private readonly List<SpaceVm> _all = new();
    private readonly SearchBar _search;
    private bool _loaded;

    public CommunitiesPage()
    {
        Title = Loc.T("community_catalog", "Community catalog");

        _search = new SearchBar
        {
            Placeholder = Loc.T("community_search_channels", "Search channels"),
            Margin = new Thickness(0, 0, 0, 4)
        };
        _search.TextChanged += (_, _) => Filter();

        var list = new CollectionView
        {
            ItemsSource = _items,
            SelectionMode = SelectionMode.Single,
            Margin = new Thickness(0, 4, 0, 0)
        };
        list.SelectionChanged += OnSelectionChanged;
        list.ItemTemplate = new DataTemplate(() =>
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(GridLength.Auto), new(GridLength.Star)
                },
                ColumnSpacing = 10,
                Padding = new Thickness(12, 10)
            };
            var icon = new Label { FontSize = 20, VerticalOptions = LayoutOptions.Center };
            icon.SetBinding(Label.TextProperty, nameof(SpaceVm.Icon));
            grid.Add(icon, 0, 0);

            var stack = new VerticalStackLayout { Spacing = 2 };
            var name = new Label
            {
                FontSize = 14,
                FontAttributes = FontAttributes.Bold,
                TextColor = (Color?)Application.Current?.Resources["TextPrimary"] ?? Colors.White
            };
            name.SetBinding(Label.TextProperty, nameof(SpaceVm.Name));
            var meta = new Label
            {
                FontSize = 11,
                TextColor = (Color?)Application.Current?.Resources["TextMuted"] ?? Colors.Gray
            };
            meta.SetBinding(Label.TextProperty, nameof(SpaceVm.MembersText));
            var preview = new Label
            {
                FontSize = 12,
                TextColor = (Color?)Application.Current?.Resources["TextSecondary"] ?? Colors.LightGray,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            preview.SetBinding(Label.TextProperty, nameof(SpaceVm.PreviewLine));
            stack.Add(name);
            stack.Add(meta);
            stack.Add(preview);
            grid.Add(stack, 1, 0);
            return grid;
        });

        Content = new VerticalStackLayout
        {
            Padding = 16,
            Spacing = 6,
            Children = { _search, list }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        _loaded = true;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var spaces = await _spaces.ListAsync();
            _all.Clear();
            foreach (var dto in spaces.Where(s =>
                         s.Type == SpaceTypes.Community && s.IsPublic && !s.IsHidden))
            {
                _all.Add(SpaceVm.From(dto));
            }
            Filter();
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("common_error", "Error"), ex.Message, "OK");
        }
    }

    private void Filter()
    {
        var query = _search.Text?.Trim() ?? string.Empty;
        _items.Clear();
        foreach (var item in _all.Where(x =>
                     query.Length == 0 ||
                     x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     x.Description.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            _items.Add(item);
        }
    }

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not SpaceVm space) return;
        if (space.IsJoinable)
        {
            var join = await DisplayAlert(
                space.Name,
                Loc.T("community_join", "Join"),
                Loc.T("community_join", "Join"), Loc.T("common_cancel", "Cancel"));
            if (join && !await _spaces.JoinAsync(space.Id)) return;
        }
        await Navigation.PushAsync(new SpaceChatPage(
            space.Id, space.Name, space.Type, space.CanPost));
    }
}
