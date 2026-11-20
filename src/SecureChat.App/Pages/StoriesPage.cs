using SecureChat.App.Services;

namespace SecureChat.App.Pages;

/// <summary>Лента историй: свои отдельно, контакты сгруппированы по авторам.</summary>
public sealed class StoriesPage : ContentPage
{
    private readonly StoryService _stories = AppServices.Get<StoryService>();
    private readonly VerticalStackLayout _root = new() { Spacing = 14, Padding = 16 };
    private bool _loaded;

    public StoriesPage()
    {
        Title = Loc.T("stories_my", "Истории");
        BackgroundColor = (Color?)Application.Current?.Resources["PageBackground"] ?? Colors.Black;
        Content = new ScrollView { Content = _root };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_loaded)
        {
            _loaded = true;
            var create = new Button { Text = "＋ " + Loc.T("stories_new", "Новая история") };
            create.Clicked += async (_, _) => await Navigation.PushAsync(new StoryCreatePage());
            _root.Add(create);
        }
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        while (_root.Count > 1) _root.RemoveAt(_root.Count - 1);

        IReadOnlyList<StoryItem> items;
        try { items = await _stories.GetAsync(); }
        catch (Exception ex)
        {
            _root.Add(new Label { Text = ex.Message, TextColor = Colors.Red });
            return;
        }

        var mine = items.Where(s => s.IsMine).OrderByDescending(s => s.CreatedAtUnixMs).ToList();
        var others = items.Where(s => !s.IsMine).ToList();

        _root.Add(new Label
        {
            Text = Loc.T("stories_my", "Мои истории"),
            FontAttributes = FontAttributes.Bold,
            TextColor = (Color?)Application.Current?.Resources["TextPrimary"] ?? Colors.White
        });
        if (mine.Count == 0)
        {
            _root.Add(new Label
            {
                Text = Loc.T("stories_empty", "Пока нет историй"),
                TextColor = (Color?)Application.Current?.Resources["TextMuted"] ?? Colors.Gray,
                FontSize = 13
            });
        }
        else
        {
            _root.Add(BuildGroup(mine));
        }

        _root.Add(new Label
        {
            Text = Loc.T("stories_contacts", "Истории контактов"),
            FontAttributes = FontAttributes.Bold,
            TextColor = (Color?)Application.Current?.Resources["TextPrimary"] ?? Colors.White
        });
        var groups = others.GroupBy(s => s.OwnerUsername, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Max(s => s.CreatedAtUnixMs)).ToList();
        if (groups.Count == 0)
        {
            _root.Add(new Label
            {
                Text = Loc.T("stories_empty", "Пока нет историй"),
                TextColor = (Color?)Application.Current?.Resources["TextMuted"] ?? Colors.Gray,
                FontSize = 13
            });
        }
        else
        {
            foreach (var group in groups)
            {
                _root.Add(BuildGroup(group.OrderByDescending(s => s.CreatedAtUnixMs).ToList()));
            }
        }
    }

    private View BuildGroup(List<StoryItem> stories)
    {
        var first = stories[0];
        var unseen = stories.Any(s => !s.ViewedByMe);
        var avatar = new Button
        {
            WidthRequest = 64,
            HeightRequest = 64,
            CornerRadius = 32,
            Padding = 2,
            BackgroundColor = unseen
                ? (Color?)Application.Current?.Resources["Accent"] ?? Colors.CornflowerBlue
                : (Color?)Application.Current?.Resources["SurfaceAlt"] ?? Colors.Gray,
            TextColor = Colors.White,
            Text = string.IsNullOrEmpty(first.Text) ? "◉" : first.Text[..1].ToUpperInvariant()
        };
        if (!string.IsNullOrEmpty(first.AvatarBase64))
        {
            try
            {
                avatar.ImageSource = ImageSource.FromStream(() =>
                    new MemoryStream(Convert.FromBase64String(first.AvatarBase64!)));
                avatar.Text = string.Empty;
            }
            catch (FormatException) { }
        }
        avatar.Clicked += async (_, _) =>
            await Navigation.PushAsync(new StoryViewerPage(stories, 0));

        var label = new Label
        {
            Text = first.IsMine
                ? Loc.T("stories_my", "Мои истории")
                : first.OwnerUsername,
            FontSize = 12,
            HorizontalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            WidthRequest = 80,
            TextColor = (Color?)Application.Current?.Resources["TextPrimary"] ?? Colors.White
        };
        var count = new Label
        {
            Text = stories.Count > 1 ? stories.Count.ToString() : string.Empty,
            FontSize = 11,
            HorizontalTextAlignment = TextAlignment.Center,
            TextColor = (Color?)Application.Current?.Resources["TextMuted"] ?? Colors.Gray
        };
        return new VerticalStackLayout
        {
            Spacing = 4,
            HorizontalOptions = LayoutOptions.Start,
            Children = { avatar, label, count }
        };
    }
}
