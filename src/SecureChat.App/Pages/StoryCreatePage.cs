using SecureChat.App.Services;

namespace SecureChat.App.Pages;

/// <summary>Публикация истории: текст и/или фото, видимость — все или контакты.</summary>
public sealed class StoryCreatePage : ContentPage
{
    private readonly StoryService _stories = AppServices.Get<StoryService>();
    private readonly Entry _text = new() { Placeholder = "Подпись истории" };
    private readonly CheckBox _contactsOnly = new() { IsChecked = true };
    private string _image = string.Empty;

    public StoryCreatePage()
    {
        Title = "Новая история";
        var pick = new Button { Text = "📷 Выбрать фото" };
        pick.Clicked += OnPickClicked;
        var publish = new Button { Text = "Опубликовать" };
        publish.Clicked += OnPublishClicked;

        Content = new VerticalStackLayout
        {
            Spacing = 12,
            Padding = 16,
            Children =
            {
                new Label { Text = "История будет доступна 24 часа", FontSize = 12, TextColor = Colors.Gray },
                _text,
                pick,
                new HorizontalStackLayout
                {
                    Spacing = 8,
                    Children = { _contactsOnly, new Label { Text = "Только контакты", VerticalOptions = LayoutOptions.Center } }
                },
                publish
            }
        };
    }

    private async void OnPickClicked(object? sender, EventArgs e)
    {
        var file = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Выберите изображение",
            FileTypes = FilePickerFileType.Images
        });
        if (file is null) return;
        using var stream = await file.OpenReadAsync();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        _image = Convert.ToBase64String(memory.ToArray());
    }

    private async void OnPublishClicked(object? sender, EventArgs e)
    {
        var text = _text.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(_image))
        {
            await DisplayAlert("История", "Добавьте текст или фото.", "OK");
            return;
        }
        try
        {
            await _stories.PublishAsync(text, _image, _contactsOnly.IsChecked);
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", ex.Message, "OK");
        }
    }
}
