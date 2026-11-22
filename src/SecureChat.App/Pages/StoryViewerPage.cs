using SecureChat.App.Services;

namespace SecureChat.App.Pages;

/// <summary>Полноэкранный просмотр историй: фон, медиа, сегменты, ответ.</summary>
public sealed class StoryViewerPage : ContentPage
{
    private readonly StoryService _stories = AppServices.Get<StoryService>();
    private readonly List<StoryItem> _items;
    private readonly Grid _backdrop = new();
    private readonly BoxView _backdropTop = new() { Color = Color.FromArgb("#121826") };
    private readonly BoxView _backdropBottom = new() { Color = Color.FromArgb("#0B1220") };
    private readonly Image _avatar = new() { Aspect = Aspect.AspectFill, WidthRequest = 34, HeightRequest = 34, IsVisible = false };
    private readonly Image _image = new() { Aspect = Aspect.AspectFill, IsVisible = false };
    private readonly Grid _segments = new() { ColumnSpacing = 4, HeightRequest = 4, Margin = new Thickness(12, 10, 12, 0) };
    private readonly Label _name = new() { TextColor = Colors.White, FontAttributes = FontAttributes.Bold, FontSize = 15 };
    private readonly Label _time = new() { TextColor = Color.FromArgb("#CCFFFFFF"), FontSize = 11 };
    private readonly Label _caption = new()
    {
        TextColor = Colors.White,
        HorizontalTextAlignment = TextAlignment.Center,
        VerticalTextAlignment = TextAlignment.Center,
        VerticalOptions = LayoutOptions.Center,
        HorizontalOptions = LayoutOptions.Fill,
        LineBreakMode = LineBreakMode.WordWrap,
        Margin = new Thickness(28, 12),
        Padding = new Thickness(18, 14)
    };
    private readonly Button _views = MakeGhost("👁 0");
    private readonly Button _delete = MakeGhost("🗑 " + Loc.T("stories_delete", "Удалить историю"));
    private readonly Button _reply = MakeAction("↩ " + Loc.T("stories_reply", "Reply"));
    private readonly Entry _replyEntry = new()
    {
        Placeholder = Loc.T("stories_reply_hint", "Reply to story…"),
        TextColor = Colors.White,
        PlaceholderColor = Color.FromArgb("#AAFFFFFF"),
        BackgroundColor = Color.FromArgb("#33FFFFFF"),
        IsVisible = false
    };
    private CancellationTokenSource? _cts;
    private int _index;

    private static Button MakeAction(string text) => new()
    {
        Text = text,
        TextColor = Colors.White,
        BackgroundColor = Color.FromArgb("#44000000"),
        FontSize = 11,
        HeightRequest = 30,
        Padding = new Thickness(10, 0)
    };

    private static Button MakeGhost(string text) => new()
    {
        Text = text,
        TextColor = Color.FromArgb("#CCFFFFFF"),
        BackgroundColor = Color.FromArgb("#33000000"),
        FontSize = 12,
        HeightRequest = 34,
        Padding = new Thickness(8, 0)
    };

    public StoryViewerPage(StoryItem story) : this(new List<StoryItem> { story }, 0) { }

    public StoryViewerPage(IReadOnlyList<StoryItem> items, int startIndex)
    {
        _items = items.ToList();
        _index = Math.Clamp(startIndex, 0, _items.Count - 1);
        _delete.IsVisible = false;
        _reply.IsVisible = false;
        BackgroundColor = Colors.Black;
        Shell.SetNavBarIsVisible(this, false);
        NavigationPage.SetHasNavigationBar(this, false);

        var close = new Button
        {
            Text = "✕",
            TextColor = Colors.White,
            BackgroundColor = Colors.Transparent,
            FontSize = 18,
            WidthRequest = 40,
            HeightRequest = 40,
            Padding = 0
        };
        close.Clicked += async (_, _) => await Navigation.PopAsync();

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            Padding = new Thickness(12, 0, 8, 0)
        };
        header.Add(new HorizontalStackLayout
        {
            Spacing = 8,
            VerticalOptions = LayoutOptions.Center,
            Children = { _avatar, _name, _time }
        }, 0, 0);
        header.Add(new HorizontalStackLayout
        {
            Spacing = 4,
            VerticalOptions = LayoutOptions.Center,
            Children = { _views, _delete, close }
        }, 0, 1);

        var prev = new BoxView { Color = Colors.Transparent };
        var next = new BoxView { Color = Colors.Transparent };
        prev.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(Previous) });
        next.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(Next) });
        var taps = new Grid
        {
            RowDefinitions = new RowDefinitionCollection { new RowDefinition(GridLength.Star) },
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            }
        };
        taps.Add(prev, 0, 0);
        taps.Add(next, 0, 1);

        _views.Clicked += OnViewsClicked;
        _delete.Clicked += OnDeleteClicked;
        _reply.Clicked += OnReplyClicked;
        _replyEntry.Completed += OnReplyCompleted;
        var actions = new HorizontalStackLayout
        {
            Spacing = 8,
            HorizontalOptions = LayoutOptions.Center,
            Children = { _reply }
        };

        var bottom = new VerticalStackLayout
        {
            Spacing = 6,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.End,
            Padding = new Thickness(12, 0, 12, 16),
            Children = { _replyEntry, actions }
        };

        var overlay = new VerticalStackLayout
        {
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            InputTransparent = true,
            CascadeInputTransparent = false
        };
        overlay.Add(_segments);
        overlay.Add(header);
        overlay.Add(new ContentView
        {
            Content = _caption,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.FillAndExpand,
            MinimumHeightRequest = 160,
            InputTransparent = true,
            CascadeInputTransparent = false
        });
        overlay.Add(bottom);

        var root = new Grid
        {
            RowDefinitions = new RowDefinitionCollection { new RowDefinition(GridLength.Star) },
            ColumnDefinitions = new ColumnDefinitionCollection { new ColumnDefinition(GridLength.Star) }
        };
        _backdrop.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        _backdrop.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        _backdrop.Add(_backdropTop, 0, 0);
        _backdrop.Add(_backdropBottom, 1, 0);
        root.Add(_backdrop, 0, 0);
        root.Add(_image, 0, 0);
        root.Add(taps, 0, 0);
        root.Add(overlay, 0, 0);
        Content = root;
        BuildSegments();
        ShowCurrent();
    }

    private void BuildSegments()
    {
        _segments.Clear();
        _segments.ColumnDefinitions.Clear();
        for (var i = 0; i < _items.Count; i++)
        {
            var track = new Grid { HorizontalOptions = LayoutOptions.Fill };
            track.Add(new BoxView
            {
                Color = Color.FromArgb("#66FFFFFF"),
                HeightRequest = 3,
                HorizontalOptions = LayoutOptions.Fill
            });
            var fill = new BoxView
            {
                Color = Colors.White,
                HeightRequest = 3,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
                AnchorX = 0,
                ScaleX = i < _index ? 1 : 0
            };
            track.Add(fill);
            _segments.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            _segments.Add(track, 0, i);
        }
    }

    private void UpdateSegmentColors()
    {
        for (var i = 0; i < _segments.Children.Count; i++)
        {
            if (_segments.Children[i] is not Grid track) continue;
            if (track.Children.FirstOrDefault() is BoxView bg)
                bg.Color = i < _index ? Colors.White : Color.FromArgb("#66FFFFFF");
            if (track.Children.LastOrDefault() is BoxView fill)
                fill.ScaleX = i < _index ? 1 : (i == _index ? fill.ScaleX : 0);
        }
    }

    /// <summary>Плавно заполняет текущий сегмент за 5 секунд.</summary>
    private void AnimateCurrentSegment(CancellationToken token)
    {
        if (_index < 0 || _index >= _segments.Children.Count ||
            _segments.Children[_index] is not Grid track ||
            track.Children.LastOrDefault() is not BoxView fill)
        {
            return;
        }

        fill.ScaleX = 0;
        var animation = new Animation(v => fill.ScaleX = v, 0, 1, Easing.Linear);
        animation.Commit(this, "story_progress", 16, 5000, Easing.Linear);
        _ = CompleteAfterAsync(token);
    }

    private async Task CompleteAfterAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(5000, token);
            MainThread.BeginInvokeOnMainThread(Next);
        }
        catch (TaskCanceledException) { }
    }

    private void ShowCurrent()
    {
        if (_items.Count == 0) return;
        var story = _items[_index];
        var avatarSource = string.IsNullOrWhiteSpace(story.AvatarBase64) ? null : story.AvatarBase64;
        _avatar.IsVisible = avatarSource is not null;
        if (avatarSource is not null)
        {
            try { _avatar.Source = ImageSource.FromStream(() => new MemoryStream(Convert.FromBase64String(avatarSource))); }
            catch (FormatException) { _avatar.IsVisible = false; }
        }

        _name.Text = !string.IsNullOrWhiteSpace(story.SpaceName)
            ? story.SpaceName
            : (story.IsMine && string.IsNullOrWhiteSpace(story.SpaceId)
                ? Loc.T("stories_my", "Мои истории")
                : story.OwnerUsername);
        _time.Text = DateTimeOffset.FromUnixTimeMilliseconds(story.CreatedAtUnixMs)
            .ToLocalTime().ToString("dd.MM HH:mm");

        _caption.Text = story.Text;
        _caption.IsVisible = !string.IsNullOrWhiteSpace(story.Text);
        _caption.FontSize = story.FontSize > 0 ? story.FontSize : 18;
        _caption.TextColor = string.IsNullOrEmpty(story.TextHex) ? Colors.White : Color.FromArgb(story.TextHex);
        _caption.BackgroundColor = Color.FromArgb("#AA0B1220");

        var hasImage = !string.IsNullOrEmpty(story.ImageBase64);
        _image.IsVisible = hasImage;
        _image.Source = null;
        var bg = Color.FromArgb(string.IsNullOrEmpty(story.BackgroundHex) ? "#3B6FE8" : story.BackgroundHex);
        _backdropTop.Color = bg;
        _backdropBottom.Color = Color.FromArgb("#0B1220");
        if (hasImage)
        {
            try { _image.Source = ImageSource.FromStream(() => new MemoryStream(Convert.FromBase64String(story.ImageBase64))); }
            catch (FormatException) { _image.IsVisible = false; }
        }

        _views.Text = $"👁 {story.ViewCount}";
        _delete.IsVisible = story.IsMine;
        _reply.IsVisible = !story.IsMine;
        _replyEntry.IsVisible = false;
        _replyEntry.Text = string.Empty;
        UpdateSegmentColors();

        if (!story.ViewedByMe)
        {
            _ = _stories.MarkViewedAsync(story.Id);
            story.ViewedByMe = true;
        }
    }

    private void Previous()
    {
        if (_index <= 0) return;
        _index--;
        ShowCurrent();
        RestartTimer();
    }

    private void Next()
    {
        if (_index < _items.Count - 1)
        {
            _index++;
            ShowCurrent();
            RestartTimer();
        }
        else
        {
            Dispatcher.Dispatch(async () => await Navigation.PopAsync());
        }
    }

    private async void OnViewsClicked(object? sender, EventArgs e)
    {
        var viewers = await _stories.GetViewersAsync(_items[_index].Id);
        await DisplayAlert(
            viewers.Count == 0 ? Loc.T("stories_no_viewers", "Пока никто") : Loc.T("stories_viewers", "Просмотрели"),
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

    private void OnReplyClicked(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        _replyEntry.IsVisible = true;
        _replyEntry.Focus();
    }

    private async void OnReplyCompleted(object? sender, EventArgs e) => await SendReplyAsync();

    private async Task SendReplyAsync()
    {
        var text = _replyEntry.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        var story = _items[_index];
        _replyEntry.IsVisible = false;
        _replyEntry.Text = string.Empty;
        try
        {
            var chat = AppServices.Get<ChatManager>();
            await chat.RefreshContactProfileAsync(story.OwnerUsername);
            await chat.SendMessageAsync(story.OwnerUsername, "↪ " + text);
            await DisplayAlert(Loc.T("stories_reply", "Reply"), Loc.T("stories_reply_sent", "Ответ отправлен"), "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("stories_error", "Ошибка"), ex.Message, "OK");
        }
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
        this.AbortAnimation("story_progress");
        _cts = new CancellationTokenSource();
        AnimateCurrentSegment(_cts.Token);
    }



    protected override void OnDisappearing()
    {
        _cts?.Cancel();
        this.AbortAnimation("story_progress");
        base.OnDisappearing();
    }
}
