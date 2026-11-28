namespace SecureChat.App.Views;

/// <summary>
/// Полноэкранный просмотр аватара в стиле Telegram: тёмный фон, стрелки
/// и счётчик для истории аватаров, свайп вниз для закрытия.
/// </summary>
public sealed class AvatarViewerPage : ContentPage
{
    private readonly IReadOnlyList<string> _avatars;
    private int _index;
    private readonly Image _image;
    private readonly Label _counter;
    private readonly Button _prev;
    private readonly Button _next;

    public AvatarViewerPage(IReadOnlyList<string> avatars, int index = 0)
    {
        _avatars = avatars ?? Array.Empty<string>();
        _index = _avatars.Count == 0 ? 0 : Math.Clamp(index, 0, _avatars.Count - 1);
        BackgroundColor = Colors.Black;
        Shell.SetNavBarIsVisible(this, false);
        NavigationPage.SetHasNavigationBar(this, false);

        _image = new Image
        {
            Aspect = Aspect.AspectFit,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => Navigation.PopAsync();
        _image.GestureRecognizers.Add(tap);
        _image.GestureRecognizers.Add(new SwipeGestureRecognizer
        {
            Direction = SwipeDirection.Right
        });
        _image.GestureRecognizers.Add(new SwipeGestureRecognizer
        {
            Direction = SwipeDirection.Left
        });
        foreach (var recognizer in _image.GestureRecognizers.OfType<SwipeGestureRecognizer>())
        {
            recognizer.Swiped += (_, e) =>
            {
                if (e.Direction == SwipeDirection.Right) Show(_index - 1);
                else Show(_index + 1);
            };
        }

        _counter = new Label
        {
            TextColor = Color.FromArgb("#B3FFFFFF"),
            FontSize = 13,
            HorizontalOptions = LayoutOptions.Center
        };
        _prev = new Button
        {
            Text = "‹", FontSize = 34, BackgroundColor = Color.FromArgb("#55FFFFFF"),
            TextColor = Colors.White, WidthRequest = 46, HeightRequest = 46,
            CornerRadius = 23, Padding = 0,
            VerticalOptions = LayoutOptions.Center
        };
        _prev.Clicked += (_, _) => Show(_index - 1);
        _next = new Button
        {
            Text = "›", FontSize = 34, BackgroundColor = Color.FromArgb("#55FFFFFF"),
            TextColor = Colors.White, WidthRequest = 46, HeightRequest = 46,
            CornerRadius = 23, Padding = 0,
            VerticalOptions = LayoutOptions.Center
        };
        _next.Clicked += (_, _) => Show(_index + 1);

        var close = new Button
        {
            Text = "✕", FontSize = 18, BackgroundColor = Color.FromArgb("#55FFFFFF"),
            TextColor = Colors.White, WidthRequest = 42, HeightRequest = 42,
            CornerRadius = 21, Padding = 0,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(0, 12, 16, 0)
        };
        close.Clicked += async (_, _) => await Navigation.PopAsync();

        Content = new Grid
        {
            BackgroundColor = Colors.Black,
            Children =
            {
                new BoxView { Color = Colors.Black, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill },
                _image,
                new HorizontalStackLayout
                {
                    Spacing = 8,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    Children = { _prev, _counter, _next }
                },
                close
            }
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Show(_index);
    }

    private void Show(int index)
    {
        if (_avatars.Count == 0) return;
        _index = (index + _avatars.Count) % _avatars.Count;
        try
        {
            var bytes = Convert.FromBase64String(_avatars[_index]);
            _image.Source = ImageSource.FromStream(() => new MemoryStream(bytes));
        }
        catch { }
        var many = _avatars.Count > 1;
        _prev.IsVisible = many;
        _next.IsVisible = many;
        _counter.IsVisible = many;
        _counter.Text = $"{_index + 1} / {_avatars.Count}";
    }
}
