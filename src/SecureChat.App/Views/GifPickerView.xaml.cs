namespace SecureChat.App.Views;

/// <summary>
/// Вкладка «GIF» в стиле Telegram: последние анимации из переписки. Источник —
/// локальная история и кэш медиа (никаких внешних сервисов). Тап по превью
/// отправляет тот же GIF заново — байты уже расшарены на устройстве.
/// </summary>
public partial class GifPickerView : ContentView
{
    public sealed class GifItem
    {
        /// <summary>Сообщение-источник GIF (по нему берутся локальные байты).</summary>
        public required string MessageId { get; init; }
        public required string FileName { get; init; }
        public required string ContentType { get; init; }
        public required byte[] Bytes { get; init; }
    }

    private const int Columns = 5;

    public GifPickerView()
    {
        InitializeComponent();
    }

    /// <summary>Нажали GIF — отправляем его в текущий чат.</summary>
    public event Action<GifItem>? GifPicked;

    /// <summary>Заполняет сетку недавних GIF (свежими вперёд).</summary>
    public void SetItems(IEnumerable<GifItem> items)
    {
        var list = items.ToList();

        Container.Children.Clear();
        EmptyLabel.IsVisible = list.Count == 0;

        if (list.Count == 0)
        {
            return;
        }

        for (var start = 0; start < list.Count; start += Columns)
        {
            var row = new HorizontalStackLayout { Spacing = 4 };

            foreach (var item in list.Skip(start).Take(Columns))
            {
                row.Children.Add(CreateCell(item));
            }

            Container.Children.Add(row);
        }
    }

    private View CreateCell(GifItem item)
    {
        var itemRef = item;
        var image = new Image
        {
            Source = ImageSource.FromStream(() => new MemoryStream(itemRef.Bytes)),
            Aspect = Aspect.AspectFill,
            WidthRequest = 86,
            HeightRequest = 64
        };

        var cell = new Border
        {
            Padding = new Thickness(0),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle
            {
                CornerRadius = new Microsoft.Maui.CornerRadius(8)
            },
            Content = image
        };

        cell.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command<GifItem>(picked => GifPicked?.Invoke(picked)),
            CommandParameter = item
        });

        return cell;
    }
}
