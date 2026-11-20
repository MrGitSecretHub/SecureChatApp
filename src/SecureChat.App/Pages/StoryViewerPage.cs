using SecureChat.App.Services;

namespace SecureChat.App.Pages;

/// <summary>Просмотр историй: 5 секунд на историю, просмотры, удаление своей.</summary>
public sealed class StoryViewerPage : ContentPage
{
    private readonly StoryService _stories = AppServices.Get<StoryService>();
    private readonly List<StoryItem> _items;
    private readonly Image _image = new() { Aspect = Aspect.AspectFit, VerticalOptions = LayoutOptions.FillAndExpand };
    private readonly Label _caption = new()
    {
        TextColor = Colors.White,
        FontSize = 18,
        HorizontalTextAlignment = TextAlignment.Center,
        Margin = new Thickness(16)
    };
    private readonly Label _meta = new()
    {
        TextColor = Colors.LightGray,
        FontSize = 12,
        HorizontalTextAlignment = TextAlignment.Center
    };
    private readonly ProgressBar _progress = new() { ProgressColor = Colors.White };
    private readonly Button _views = new()
    {
        TextColor = Colors.White,
        BackgroundColor = Color.FromArgb("#33FFFFFF"),
        FontSize = 13
    };
    private readonly Button _delete = new()
    {
        Text = Loc.T("stories_delete", "Удалить историю"),
        TextColor = Colors.White,
        BackgroundColor = Color.FromArgb("#33FFFFFF"),
        FontSize = 13,
        IsVisible = false
    };
    private CancellationTokenSource? _cts;
    private int _index;

    public StoryViewerPage(StoryItem story) : this(new List<StoryItem> { story }, 0) { }

    public StoryViewerPage(IReadOnlyList<StoryItem> items, int startIndex)
    {
        _items = items.ToList();
        _index = Math.Clamp(startIndex, 0, _items.Count - 1);
        BackgroundColor = Colors.Black;

        var close = new Button
        {
            Text = "✕",
            TextColor = Colors.White,
            BackgroundColor = Colors.Transparent,
            HorizontalOptions = LayoutOptions.End,
            WidthRequest = 48
        };
        close.Clicked += async (_, _) => await Navigation.PopAsync();
        _views.Clicked += OnViewsClicked;
        _delete.Clicked += OnDeleteClicked;

        var actions = new HorizontalStackLayout
        {
            Spacing = 8,
            HorizontalOptions = LayoutOptions.Center,
            Children = { _views, _delete }
        };

        Content = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new(GridLength.Auto), new(), new(GridLength.Auto),
                new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)
            },
            Padding = new Thickness(8, 30),
            Children = { _image, _caption, _meta, _progress, actions, close }
        };
        Grid.SetRow(_image, 1);
        Grid.SetRow(_caption, 2);
        Grid.SetRow(_meta, 3);
        Grid.SetRow(_progress, 4);
        Grid.SetRow(actions, 5);
        Grid.SetRow(close, 0);
        ShowCurrent();
    }

    private void ShowCurrent()
    {
        if (_items.Count == 0) return;
        var story = _items[_index];
        _caption.Text = string.IsNullOrEmpty(story.Text)
            ? story.OwnerUsername
            : $"{story.OwnerUsername}\n{story.Text}";
        _image.Source = null;
        if (!string.IsNullOrEmpty(story.ImageBase64))
        {
            try
            {
                _image.Source = ImageSource.FromStream(() =>
                    new MemoryStream(Convert.FromBase64String(story.ImageBase64)));
            }
            catch (FormatException) { }
        }
        _meta.Text = $"{story.CreatedAtUnixMs:dd.MM HH:mm} · {story.ViewCount} 👁";
        _views.Text = Loc.T("stories_views", "Просмотры") + $" · {story.ViewCount}";
        _delete.IsVisible = story.IsMine;
        _progress.Progress = 0;
        _ = _stories.MarkViewedAsync(story.Id);
        story.ViewedByMe = true;
        story.ViewCount = Math.Max(story.ViewCount, 1);
        _meta.Text = $"{story.CreatedAtUnixMs:dd.MM HH:mm} · {story.ViewCount} 👁";
    }

    private async void OnViewsClicked(object? sender, EventArgs e)
    {
        var story = _items[_index];
        var viewers = await _stories.GetViewersAsync(story.Id);
        var title = viewers.Count == 0
            ? Loc.T("stories_no_viewers", "Пока никто")
            : Loc.T("stories_viewers", "Просмотрели") + $" ({viewers.Count})";
        await DisplayAlert(title,
            viewers.Count == 0 ? string.Empty : string.Join("\n", viewers),
            Loc.T("stories_close", "Закрыть"));
    }

    private async void OnDeleteClicked(object? sender, EventArgs e)
    {
        var story = _items[_index];
        if (!story.IsMine) return;
        await _stories.DeleteAsync(story.Id);
        _items.RemoveAt(_index);
        if (_items.Count == 0)
        {
            await Navigation.PopAsync();
            return;
        }
        if (_index >= _items.Count) _index = _items.Count - 1;
        ShowCurrent();
        RestartTimer();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RestartTimer();
    }

    private void RestartTimer()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = RunTimerAsync(token);
    }

    private async Task RunTimerAsync(CancellationToken token)
    {
        const int seconds = 5;
        try
        {
            for (var i = 1; i <= seconds; i++)
            {
                await Task.Delay(1000, token);
                MainThread.BeginInvokeOnMainThread(() => _progress.Progress = (float)i / seconds);
            }
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (_index < _items.Count - 1)
                {
                    _index++;
                    ShowCurrent();
                    RestartTimer();
                }
                else
                {
                    await Navigation.PopAsync();
                }
            });
        }
        catch (TaskCanceledException) { }
    }

    protected override void OnDisappearing()
    {
        _cts?.Cancel();
        base.OnDisappearing();
    }
}
