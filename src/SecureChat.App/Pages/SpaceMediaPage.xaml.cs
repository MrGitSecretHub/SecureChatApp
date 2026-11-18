using System.Collections.ObjectModel;
using SecureChat.App.Services;
using SecureChat.App.Views;
using SecureChat.Core.Net;

namespace SecureChat.App.Pages;

/// <summary>
/// Галерея вложений пространства: медиа и файлы из ленты, сгруппированные по
/// дате. Открывается из шапки пространства (кнопка «🖼»).
/// </summary>
public partial class SpaceMediaPage : ContentPage
{
    private readonly SpaceService _spaces = AppServices.Get<SpaceService>();
    private readonly ObservableCollection<SpaceMediaItemVm> _items = new();
    private readonly string _spaceId;
    private readonly string? _topicId;

    public SpaceMediaPage(string spaceId, string? topicId = null)
    {
        InitializeComponent();

        _spaceId = spaceId;
        _topicId = topicId;
        ItemsView.ItemsSource = _items;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    /// <summary>Загружает ленту и собирает из неё вложения.</summary>
    private async Task LoadAsync()
    {
        _items.Clear();
        StatusLabel.Text = "Загрузка…";

        try
        {
            var messages = await _spaces.GetMessagesAsync(_spaceId, _topicId);
            DateTime? previousDate = null;

            foreach (var message in messages)
            {
                var payload = await _spaces.DecryptPayloadAsync(_spaceId, message);
                if (payload?.Media is null)
                {
                    continue;
                }

                var vm = SpaceMessageVm.From(
                    message,
                    payload,
                    string.Empty,
                    false,
                    isChannel: false,
                    myUsername: AppServices.Get<AppSession>().User?.Username);

                var local = DateTimeOffset.FromUnixTimeMilliseconds(message.SentAtUnixMs).ToLocalTime().Date;
                if (previousDate is null || previousDate.Value != local)
                {
                    _items.Add(SpaceMediaItemVm.Header(FormatDate(local)));
                    previousDate = local;
                }

                _items.Add(SpaceMediaItemVm.Item(vm));
            }

            StatusLabel.Text = _items.Count == 0
                ? string.Empty
                : string.Format(Loc.T("rs_000"), _items.Count(item => item.IsItem));
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"⚡ {ex.Message}";

            if (!ConnectionErrors.IsConnectionProblem(ex.Message))
            {
                await this.ShowErrorAsync(ex);
            }
        }
    }

    private static string FormatDate(DateTime date)
    {
        var today = DateTime.Now.Date;
        if (date == today)
        {
            return Loc.T("rs_142", "Сегодня");
        }

        if (date == today.AddDays(-1))
        {
            return Loc.T("rs_027", "Вчера");
        }

        return date.ToString("d MMMM yyyy", new System.Globalization.CultureInfo("ru-RU"));
    }

    /// <summary>Скачивает вложение и открывает его.</summary>
    private async void OnItemTapped(object? sender, EventArgs e)
    {
        if (sender is not BindableObject { BindingContext: SpaceMediaItemVm item } ||
            item.Message is not { } message || message.Media is null)
        {
            return;
        }

        try
        {
            var path = Path.Combine(FileSystem.CacheDirectory,
                message.Id + Path.GetExtension(message.FileName));

            byte[] bytes;
            if (File.Exists(path))
            {
                bytes = await File.ReadAllBytesAsync(path);
            }
            else
            {
                bytes = await _spaces.DownloadMediaAsync(message.Media);
                await File.WriteAllBytesAsync(path, bytes);
            }

            if (message.IsImage)
            {
                message.SetImageContent(bytes);
                await Navigation.PushModalAsync(
                    new NavigationPage(new ImageViewerPage(bytes, message.FileName)));
                return;
            }

            // Видео открываем встроенным плеером, остальное — системным приложением.
            if (message.Media?.ContentType?.StartsWith("video", StringComparison.OrdinalIgnoreCase) == true)
            {
                await Navigation.PushModalAsync(
                    new NavigationPage(new MediaPlayerPage(path, message.FileName, message.Media.ContentType)));
                return;
            }

            await Launcher.Default.OpenAsync(new OpenFileRequest(message.FileName, new ReadOnlyFile(path)));
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }
}
