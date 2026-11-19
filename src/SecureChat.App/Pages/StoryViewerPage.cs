using System.Text;
using SecureChat.App.Services;

namespace SecureChat.App.Pages;

/// <summary>Просмотр истории с прогрессом 24 часа.</summary>
public sealed class StoryViewerPage : ContentPage
{
    private readonly LocalStore _store = AppServices.Get<LocalStore>();
    private readonly StoredStory _story;
    private readonly ProgressBar _progress = new() { ProgressColor = Colors.White };
    private CancellationTokenSource? _cts;

    public StoryViewerPage(StoredStory story)
    {
        _story = story;
        BackgroundColor = Colors.Black;
        var close = new Button { Text = "✕", TextColor = Colors.White, HorizontalOptions = LayoutOptions.End, WidthRequest = 48 };
        close.Clicked += async (_, _) => await Navigation.PopAsync();

        var image = new Image { Aspect = Aspect.AspectFit, VerticalOptions = LayoutOptions.FillAndExpand };
        if (!string.IsNullOrEmpty(story.ImageBase64))
        {
            try { image.Source = ImageSource.FromStream(() => new MemoryStream(Convert.FromBase64String(story.ImageBase64))); }
            catch (FormatException) { }
        }

        var caption = new Label
        {
            Text = story.Text,
            TextColor = Colors.White,
            FontSize = 18,
            HorizontalTextAlignment = TextAlignment.Center,
            Margin = new Thickness(16)
        };

        var delete = new Button { Text = "Удалить историю", TextColor = Colors.White };
        delete.Clicked += async (_, _) =>
        {
            await _store.RemoveStoryAsync(_story.Id);
            await Navigation.PopAsync();
        };

        Content = new Grid
        {
            RowDefinitions = new RowDefinitionCollection { new(GridLength.Auto), new(), new(GridLength.Auto), new(GridLength.Auto) },
            Padding = new Thickness(8, 30),
            Children = { image, caption, _progress, delete, close }
        };
        Grid.SetRow(image, 1);
        Grid.SetRow(caption, 2);
        Grid.SetRow(_progress, 3);
        Grid.SetRow(delete, 3);
        Grid.SetRow(close, 0);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _cts = new CancellationTokenSource();
        var remaining = Math.Max(0, _story.ExpiresAtUnixMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var total = Math.Max(1, 24 * 60 * 60 * 1000.0);
        _progress.Progress = (float)(remaining / total);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(1000, _cts.Token);
                _progress.Progress = Math.Max(0, _progress.Progress - (float)(1000.0 / total));
                if (_progress.Progress <= 0) await Navigation.PopAsync();
            }
        }
        catch (TaskCanceledException) { }
    }

    protected override void OnDisappearing()
    {
        _cts?.Cancel();
        base.OnDisappearing();
    }
}
