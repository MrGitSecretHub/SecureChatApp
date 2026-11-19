using SecureChat.App.Services;

namespace SecureChat.App.Pages;

/// <summary>Лента историй: аватары с обложками, создание новой истории.</summary>
public sealed class StoriesPage : ContentPage
{
    private readonly StoryService _stories = AppServices.Get<StoryService>();
    private readonly HorizontalStackLayout _strip = new() { Spacing = 12, Padding = new Thickness(4, 0) };

    public StoriesPage()
    {
        Title = "Истории";
        var create = new Button { Text = "＋ Новая история" };
        create.Clicked += async (_, _) => await Navigation.PushAsync(new StoryCreatePage());

        Content = new ScrollView
        {
            Orientation = ScrollOrientation.Vertical,
            Content = new VerticalStackLayout
            {
                Spacing = 16,
                Padding = 16,
                Children =
                {
                    create,
                    new Label { Text = "Истории контактов", FontAttributes = FontAttributes.Bold },
                    new ScrollView
                    {
                        Orientation = ScrollOrientation.Horizontal,
                        Content = _strip
                    }
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _strip.Clear();
        try
        {
            var items = await _stories.GetAsync();
            foreach (var story in items)
            {
                var avatar = new Button
                {
                    WidthRequest = 64,
                    HeightRequest = 64,
                    CornerRadius = 32,
                    Padding = 0,
                    Text = string.IsNullOrEmpty(story.Text) ? "◉" : story.Text[..1].ToUpperInvariant(),
                    TextColor = Colors.White,
                    BackgroundColor = (Color?)Application.Current?.Resources["Accent"] ?? Colors.CornflowerBlue
                };
                if (!string.IsNullOrEmpty(story.AvatarBase64))
                {
                    try
                    {
                        avatar.ImageSource = ImageSource.FromStream(() =>
                            new MemoryStream(Convert.FromBase64String(story.AvatarBase64!)));
                        avatar.Text = string.Empty;
                    }
                    catch (FormatException) { }
                }
                avatar.Clicked += async (_, _) => await Navigation.PushAsync(new StoryViewerPage(story));
                _strip.Add(new VerticalStackLayout
                {
                    Spacing = 4,
                    HorizontalOptions = LayoutOptions.Center,
                    Children =
                    {
                        avatar,
                        new Label
                        {
                            Text = story.OwnerUsername,
                            FontSize = 12,
                            HorizontalTextAlignment = TextAlignment.Center,
                            LineBreakMode = LineBreakMode.TailTruncation,
                            WidthRequest = 72
                        }
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _strip.Add(new Label { Text = ex.Message, TextColor = Colors.Red });
        }
    }
}
