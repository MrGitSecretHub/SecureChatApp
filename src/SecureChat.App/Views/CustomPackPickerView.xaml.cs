using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using SecureChat.App.Services;

namespace SecureChat.App.Views;

/// <summary>
/// Панель «Свои пакеты»: GIF и стикеры, импортированные с устройства.
/// Файлы лежат в локальной базе (зашифрованы ключом устройства) и отправляются
/// в чат обычным шифрованным вложением.
/// </summary>
public partial class CustomPackPickerView : ContentView
{
    /// <summary>Файл пака, выбранный пользователем.</summary>
    public sealed class PackItem
    {
        public required string Id { get; init; }
        public required string FileName { get; init; }
        public required string ContentType { get; init; }
        public required bool IsGif { get; init; }
        public byte[]? Bytes { get; set; }
        public ImageSource? Preview { get; init; }
    }

    private const int MaxPreviewItems = 60;
    private const int Columns = 5;

    private readonly CustomPackService _packs = AppServices.Get<CustomPackService>();

    public CustomPackPickerView()
    {
        InitializeComponent();
    }

    /// <summary>Пользователь нажал «Импортировать»: вид пака (gif/sticker).</summary>
    public event Action<string>? ImportRequested;

    /// <summary>Пользователь выбрал файл из пака.</summary>
    public event Action<PackItem>? ItemPicked;

    /// <summary>Перечитывает пакеты с устройства и строит сетку превью.</summary>
    public async Task ReloadAsync()
    {
        Container.Children.Clear();

        var items = new List<PackItem>();
        try
        {
            var packs = await _packs.GetPacksAsync();
            foreach (var pack in packs)
            {
                foreach (var item in pack.Items.Take(MaxPreviewItems))
                {
                    var bytes = await _packs.LoadItemAsync(item.Id);
                    if (bytes is null || bytes.Length == 0)
                    {
                        continue;
                    }

                    var copy = bytes;
                    items.Add(new PackItem
                    {
                        Id = item.Id,
                        FileName = item.FileName,
                        ContentType = item.ContentType,
                        IsGif = CustomPackService.KindForFile(item.FileName, item.ContentType)
                            == CustomPackService.GifPackKind,
                        Bytes = copy,
                        Preview = ImageSource.FromStream(() => new MemoryStream(copy)),
                    });
                }
            }
        }
        catch
        {
            // Панель пакетов не критична: ошибка чтения не должна ломать чат.
        }

        EmptyLabel.IsVisible = items.Count == 0;
        for (var start = 0; start < items.Count; start += Columns)
        {
            var row = new HorizontalStackLayout { Spacing = 4 };
            foreach (var item in items.Skip(start).Take(Columns))
            {
                row.Children.Add(CreateCell(item));
            }

            Container.Children.Add(row);
        }
    }

    private View CreateCell(PackItem item)
    {
        var cell = new Border
        {
            Padding = new Thickness(2),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(8) },
            BackgroundColor = ResolveResource<Color>("Surface", Color.FromArgb("#1E2430")),
            Content = new Image
            {
                Source = item.Preview,
                Aspect = Aspect.AspectFill,
                WidthRequest = 52,
                HeightRequest = 52
            }
        };

        cell.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => ItemPicked?.Invoke(item))
        });

        return cell;
    }

    private void OnImportGifClicked(object? sender, EventArgs e) =>
        ImportRequested?.Invoke(CustomPackService.GifPackKind);

    private void OnImportStickerClicked(object? sender, EventArgs e) =>
        ImportRequested?.Invoke(CustomPackService.StickerPackKind);

    private static T ResolveResource<T>(string key, T fallback) where T : class
    {
        if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is T typed)
        {
            return typed;
        }

        return fallback;
    }
}
