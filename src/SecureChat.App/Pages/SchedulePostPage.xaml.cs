using SecureChat.App.Services;

namespace SecureChat.App.Pages;

/// <summary>
/// Выбор даты и времени отложенной публикации поста через нативные
/// DatePicker и TimePicker — надёжнее, чем цепочка текстовых диалогов.
/// </summary>
public partial class SchedulePostPage : ContentPage
{
    private readonly SpaceService _spaces = AppServices.Get<SpaceService>();
    private readonly string _spaceId;
    private readonly string? _topicId;
    private readonly string _text;

    public SchedulePostPage(string spaceId, string? topicId, string text)
    {
        InitializeComponent();

        _spaceId = spaceId;
        _topicId = topicId;
        _text = text;

        var now = DateTime.Now;
        DatePickerControl.Date = now.Date;
        TimePickerControl.Time = new TimeSpan(now.Hour, 0, 0);
        UpdatePreview();

        DatePickerControl.DateSelected += (_, _) => UpdatePreview();
        TimePickerControl.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TimePicker.Time))
            {
                UpdatePreview();
            }
        };
    }

    private DateTime SendAt
    {
        get
        {
            var date = DatePickerControl.Date;
            var time = TimePickerControl.Time;
            var result = new DateTime(date.Year, date.Month, date.Day,
                time.Hours, time.Minutes, 0, DateTimeKind.Local);

            // Прошедшее время переносим на завтра, чтобы пост не ушёл сразу.
            return result <= DateTime.Now ? result.AddDays(1) : result;
        }
    }

    private void UpdatePreview()
    {
        PreviewLabel.Text = $"Пост будет опубликован {SendAt:dddd, dd MMMM yyyy HH:mm}";
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        await Navigation.PopAsync();
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        var sendAt = SendAt;

        try
        {
            var post = await _spaces.ScheduleMessageAsync(_spaceId, _topicId, _text, sendAt);
            if (post is null)
            {
                await DisplayAlert("Не удалось", "Не получилось отложить пост.", "Ок");
                return;
            }

            await Navigation.PopAsync();
            await DisplayAlert("Пост отложен",
                $"Пост будет опубликован {sendAt:dd.MM.yyyy HH:mm}.",
                "Ок");
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }
}
