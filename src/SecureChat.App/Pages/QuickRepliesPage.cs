using SecureChat.App.Services;

namespace SecureChat.App.Pages;

/// <summary>Управление быстрыми ответами: добавить/удалить заготовки.</summary>
public sealed class QuickRepliesPage : ContentPage
{
    private readonly QuickRepliesService _replies = AppServices.Get<QuickRepliesService>();
    private readonly Entry _entry = new() { Placeholder = "Текст быстрого ответа" };
    private readonly VerticalStackLayout _list = new() { Spacing = 4 };

    public QuickRepliesPage()
    {
        Title = "Быстрые ответы";
        var add = new Button { Text = "Добавить" };
        add.Clicked += OnAddClicked;

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 12,
                Padding = 16,
                Children =
                {
                    new Label { Text = "Заготовки появляются в чате по кнопке ⚡", FontSize = 12, TextColor = Colors.Gray },
                    _entry,
                    add,
                    _list
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshAsync();
    }

    private async void OnAddClicked(object? sender, EventArgs e)
    {
        var text = _entry.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        await _replies.AddAsync(text);
        _entry.Text = string.Empty;
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _list.Clear();
        foreach (var item in await _replies.GetAllAsync())
        {
            var label = new Label
            {
                Text = item,
                FontSize = 14,
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            var remove = new Button
            {
                Text = "✕",
                WidthRequest = 36,
                HeightRequest = 36,
                Padding = 0
            };
            remove.Clicked += async (_, _) =>
            {
                await _replies.RemoveAsync(item);
                await RefreshAsync();
            };
            _list.Add(new HorizontalStackLayout
            {
                Spacing = 8,
                Children = { label, remove }
            });
        }
    }
}
