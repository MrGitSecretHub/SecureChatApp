using SecureChat.App.Services;
using SecureChat.Core.Contracts;

namespace SecureChat.App.Pages;

/// <summary>Создание истории: фото, подпись, оформление, срок жизни и аудитория.</summary>
public sealed class StoryCreatePage : ContentPage
{
    private static readonly string[] Backgrounds =
    {
        "#3B6FE8", "#9B6FE8", "#E8A33D", "#5FBF6A", "#E2705A", "#1F2937", "#0F766E", "#B8336A"
    };

    private readonly StoryService _stories = AppServices.Get<StoryService>();
    private readonly Entry _text = new() { Placeholder = Loc.T("stories_question", "Подпись истории") };
    private readonly CheckBox _contactsOnly = new() { IsChecked = true };
    private readonly Picker _audience = new() { Title = Loc.T("story_audience", "Audience") };
    private readonly Picker _spacePicker = new() { Title = Loc.T("story_space", "Space"), IsVisible = false };
    private readonly List<SpaceDto> _spaces = new();
    private readonly Image _preview = new() { Aspect = Aspect.AspectFit, HeightRequest = 180, IsVisible = false };
    private readonly HorizontalStackLayout _palette = new() { Spacing = 8 };
    private readonly HorizontalStackLayout _stickers = new() { Spacing = 6 };
    private readonly Label _fontValue = new() { Text = "18", WidthRequest = 34, HorizontalTextAlignment = TextAlignment.Center, VerticalOptions = LayoutOptions.Center };
    private readonly Picker _lifetime = new() { Title = Loc.T("stories_lifetime", "Время жизни") };
    private readonly Button _removePhoto = new() { Text = Loc.T("stories_remove_photo", "Убрать фото"), IsVisible = false };
    private string _image = string.Empty;
    private string _background = Backgrounds[0];
    private readonly string? _spaceId;
    private int _fontSize = 18;

    public StoryCreatePage(string? spaceId = null)
    {
        _spaceId = spaceId;
        Title = Loc.T("stories_new", "Новая история");
        Shell.SetNavBarIsVisible(this, false);
        NavigationPage.SetHasNavigationBar(this, false);

        var close = new Button
        {
            Text = "✕ " + Loc.T("stories_close", "Закрыть"),
            HorizontalOptions = LayoutOptions.Start,
            BackgroundColor = Colors.Transparent,
            TextColor = (Color?)Application.Current?.Resources["TextPrimary"] ?? Colors.White,
            FontSize = 14,
            Padding = new Thickness(4, 2)
        };
        close.Clicked += async (_, _) => await Navigation.PopAsync();

        foreach (var hex in Backgrounds)
        {
            var swatch = new Button
            {
                BackgroundColor = Color.FromArgb(hex),
                WidthRequest = 32,
                HeightRequest = 32,
                CornerRadius = 16,
                Padding = 0,
                BorderColor = hex == _background ? Colors.White : Colors.Transparent,
                BorderWidth = 2
            };
            swatch.Clicked += (_, _) =>
            {
                _background = hex;
                foreach (var child in _palette.Children.OfType<Button>())
                    child.BorderWidth = child.BackgroundColor == Color.FromArgb(hex) ? 2 : 0;
                UpdatePreviewBackground();
            };
            _palette.Add(swatch);
        }

        foreach (var hours in new[] { 1, 6, 12, 24 })
            _lifetime.Items.Add($"{hours} {Loc.T("stories_hours", "часов")}");
        _lifetime.SelectedIndex = 3;

        var pick = new Button { Text = "📷 " + Loc.T("stories_choose_photo", "Выбрать фото") };
        pick.Clicked += OnPickClicked;
        _removePhoto.Clicked += (_, _) =>
        {
            _image = string.Empty;
            _preview.IsVisible = false;
            _removePhoto.IsVisible = false;
            UpdatePreviewBackground();
        };

        var plus = new Button { Text = "＋", WidthRequest = 36, Padding = 0 };
        var minus = new Button { Text = "－", WidthRequest = 36, Padding = 0 };
        minus.Clicked += (_, _) => SetFontSize(_fontSize - 2);
        plus.Clicked += (_, _) => SetFontSize(_fontSize + 2);

        var publish = new Button { Text = Loc.T("stories_publish", "Опубликовать") };
        publish.Clicked += OnPublishClicked;

        _audience.Items.Add(Loc.T("story_everyone", "Everyone"));
        _audience.Items.Add(Loc.T("story_contacts", "Contacts only"));
        _audience.Items.Add(Loc.T("story_space", "Space"));
        _audience.SelectedIndex = string.IsNullOrWhiteSpace(_spaceId) ? 1 : 2;
        _audience.SelectedIndexChanged += (_, _) =>
        {
            _spacePicker.IsVisible = _audience.SelectedIndex == 2;
            _contactsOnly.IsVisible = _audience.SelectedIndex != 2;
        };
        _ = LoadSpacesAsync();

        foreach (var sticker in new[] { "❤️", "🔥", "🎉", "👍", "😂", "✨", "🌈", "⭐" })
        {
            var button = new Button
            {
                Text = sticker,
                FontSize = 20,
                WidthRequest = 38,
                HeightRequest = 38,
                Padding = 0
            };
            button.Clicked += (_, _) => _text.Text = (_text.Text ?? string.Empty) + sticker;
            _stickers.Add(button);
        }

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 10,
                Padding = 16,
                Children =
                {
                    close,
                    new Label { Text = Loc.T("stories_create_hint", "История будет доступна 24 часа"), FontSize = 12, TextColor = Colors.Gray },
                    pick,
                    _preview,
                    _removePhoto,
                    _text,
                    new Label { Text = Loc.T("stories_stickers", "Стикеры"), FontSize = 12, TextColor = Colors.Gray },
                    new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = _stickers },
                    new Label { Text = Loc.T("stories_bg", "Цвет фона"), FontSize = 12, TextColor = Colors.Gray },
                    new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = _palette },
                    new Label { Text = Loc.T("stories_font", "Размер текста"), FontSize = 12, TextColor = Colors.Gray },
                    new HorizontalStackLayout { Spacing = 8, Children = { minus, _fontValue, plus } },
                    _lifetime,
                    new Label { Text = Loc.T("story_audience", "Audience"), FontSize = 12, TextColor = Colors.Gray },
                    _audience,
                    _spacePicker,
                    new HorizontalStackLayout
                    {
                        Spacing = 8,
                        IsVisible = false,
                        Children =
                        {
                            _contactsOnly,
                            new Label { Text = Loc.T("stories_only_contacts", "Только контакты"), VerticalOptions = LayoutOptions.Center }
                        }
                    },
                    publish
                }
            }
        };
        UpdatePreviewBackground();
    }

    private void SetFontSize(int size)
    {
        _fontSize = Math.Clamp(size, 12, 36);
        _fontValue.Text = _fontSize.ToString();
    }

    private void UpdatePreviewBackground()
    {
        _preview.BackgroundColor = Color.FromArgb(_background);
        if (!_preview.IsVisible)
        {
            _preview.IsVisible = true;
            _preview.Source = null;
        }
    }

    private async void OnPickClicked(object? sender, EventArgs e)
    {
        try
        {
            var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = Loc.T("stories_choose_photo", "Выбрать фото") });
            if (file is null) return;
            using var stream = await file.OpenReadAsync();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            var bytes = memory.ToArray();
            if (bytes.Length > 8 * 1024 * 1024)
            {
                await DisplayAlert(Loc.T("stories_error", "Ошибка"), Loc.T("stories_too_large", "Слишком большое изображение (максимум 8 МБ)."), "OK");
                return;
            }
            _image = Convert.ToBase64String(bytes);
            _preview.Source = ImageSource.FromStream(() => new MemoryStream(bytes));
            _removePhoto.IsVisible = true;
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("stories_error", "Ошибка"), ex.Message, "OK");
        }
    }

    private async void OnPublishClicked(object? sender, EventArgs e)
    {
        var text = _text.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(_image))
        {
            await DisplayAlert(Loc.T("stories_error", "Ошибка"), Loc.T("stories_not_enough", "Добавьте текст или фото."), "OK");
            return;
        }

        var hours = _lifetime.SelectedIndex switch
        {
            0 => 1,
            1 => 6,
            2 => 12,
            _ => 24
        };

        try
        {
            await _stories.PublishAsync(
                text, _image, _contactsOnly.IsChecked, hours,
                _background, null, _fontSize, SelectedSpaceId());
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("stories_error", "Ошибка"),
                Loc.T("stories_publish_error", "Не удалось опубликовать историю") + ": " + ex.Message, "OK");
        }
    }
    private string? SelectedSpaceId()
    {
        if (_audience.SelectedIndex != 2 || _spacePicker.SelectedIndex < 0) return null;
        if (_spacePicker.SelectedIndex >= _spaces.Count) return null;
        return _spaces[_spacePicker.SelectedIndex].Id;
    }

    private async Task LoadSpacesAsync()
    {
        try
        {
            var spaces = await AppServices.Get<SpaceService>().ListAsync();
            _spaces.Clear();
            _spaces.AddRange(spaces.Where(s => !string.IsNullOrEmpty(s.MyRole)));
            foreach (var space in _spaces)
                _spacePicker.Items.Add(space.Name);
            if (!string.IsNullOrWhiteSpace(_spaceId))
            {
                var index = _spaces.FindIndex(x => x.Id == _spaceId);
                if (index >= 0) _spacePicker.SelectedIndex = index;
            }
        }
        catch { }
    }

}
