using System.Text;

namespace SecureChat.App.Views;

/// <summary>
/// Проигрывает анимированный GIF: MAUI Image показывает только первый кадр,
/// поэтому кадры рисует системный WebView (data-URI внутри &lt;img&gt;).
/// Размер подстраивается под реальные пропорции GIF из заголовка файла.
/// </summary>
public partial class GifWebView : ContentView
{
    /// <summary>Ограничения кнопочного (компактного) режима.</summary>
    private const double CompactMaxWidth = 280;
    private const double CompactMaxHeight = 220;

    /// <summary>Размеры встроенного видео.</summary>
    private const double VideoMaxWidth = 280;
    private const double VideoMaxHeight = 160;

    private byte[]? _bytes;
    private string _surfaceHex = "#3B4048";

    public GifWebView()
    {
        InitializeComponent();
    }

    /// <summary>Определяет GIF по заголовку файла («GIF8»).</summary>
    public static bool IsGifData(byte[]? bytes) =>
        bytes is { Length: > 13 } data &&
        data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8';

    #region Bindable-свойства: Bytes, SurfaceHex, Fill

    public static readonly BindableProperty BytesProperty =
        BindableProperty.Create(
            nameof(Bytes), typeof(byte[]), typeof(GifWebView),
            defaultValue: null, propertyChanged: OnContentChanged);

    public byte[]? Bytes
    {
        get => (byte[]?)GetValue(BytesProperty);
        set => SetValue(BytesProperty, value);
    }

    public static readonly BindableProperty SurfaceHexProperty =
        BindableProperty.Create(
            nameof(SurfaceHex), typeof(string), typeof(GifWebView),
            defaultValue: "#3B4048", propertyChanged: OnContentChanged);

    public string SurfaceHex
    {
        get => (string)GetValue(SurfaceHexProperty);
        set => SetValue(SurfaceHexProperty, value);
    }

    public static readonly BindableProperty FillProperty =
        BindableProperty.Create(
            nameof(Fill), typeof(bool), typeof(GifWebView),
            defaultValue: false, propertyChanged: OnContentChanged);

    public bool Fill
    {
        get => (bool)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>Что рисуем: "Gif" (по умолчанию), "Video" или "VideoCircle".</summary>
    public static readonly BindableProperty MediaKindProperty =
        BindableProperty.Create(
            nameof(MediaKind), typeof(string), typeof(GifWebView),
            defaultValue: "Gif", propertyChanged: OnContentChanged);

    public string MediaKind
    {
        get => (string)GetValue(MediaKindProperty);
        set => SetValue(MediaKindProperty, value);
    }

    /// <summary>MIME-тип медиа (для видео: "video/mp4"; пусто — считаем mp4).</summary>
    public static readonly BindableProperty MediaContentTypeProperty =
        BindableProperty.Create(
            nameof(MediaContentType), typeof(string), typeof(GifWebView),
            defaultValue: string.Empty, propertyChanged: OnContentChanged);

    public string MediaContentType
    {
        get => (string)GetValue(MediaContentTypeProperty);
        set => SetValue(MediaContentTypeProperty, value);
    }

    private static void OnContentChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is GifWebView view)
        {
            view._bytes = view.Bytes;
            view._surfaceHex = string.IsNullOrWhiteSpace(view.SurfaceHex) ? "#3B4048" : view.SurfaceHex;
            view.Update();
        }
    }

    #endregion

    /// <summary>Байты расшифрованного GIF. null — пусто.</summary>
    public byte[]? BytesValue => _bytes;

    private void Update()
    {
        var isVideo = string.Equals(MediaKind, "Video", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(MediaKind, "VideoCircle", StringComparison.OrdinalIgnoreCase);

        if (isVideo)
        {
            UpdateVideo();
            return;
        }

        if (_bytes is not { Length: > 13 } data || !IsGifData(data))
        {
            GifView.Source = null;
            return;
        }

        var width = (double)(data[6] | (data[7] << 8));
        var height = (double)(data[8] | (data[9] << 8));
        if (width < 1 || height < 1)
        {
            width = 280;
            height = 220;
        }

        if (Fill)
        {
            HorizontalOptions = LayoutOptions.Fill;
            VerticalOptions = LayoutOptions.Fill;
            WidthRequest = -1;
            HeightRequest = -1;
        }
        else
        {
            var scale = Math.Min(1, Math.Min(CompactMaxWidth / width, CompactMaxHeight / height));
            HorizontalOptions = LayoutOptions.Start;
            WidthRequest = Math.Max(1, Math.Round(width * scale));
            HeightRequest = Math.Max(1, Math.Round(height * scale));
        }

        GifView.Source = new HtmlWebViewSource
        {
            Html = BuildHtml(data, _surfaceHex)
        };
    }

    /// <summary>Видео в пузыре: прямоугольник 16:9 или квадрат (видеокружок), как в Telegram.</summary>
    private void UpdateVideo()
    {
        if (_bytes is not { Length: > 16 })
        {
            GifView.Source = null;
            return;
        }

        if (string.Equals(MediaKind, "VideoCircle", StringComparison.OrdinalIgnoreCase))
        {
            HorizontalOptions = LayoutOptions.Center;
            WidthRequest = Math.Min(VideoMaxHeight, 240);
            HeightRequest = Math.Min(VideoMaxHeight, 240);
        }
        else
        {
            HorizontalOptions = LayoutOptions.Start;
            WidthRequest = VideoMaxWidth;
            HeightRequest = VideoMaxHeight;
        }

        var contentType = string.IsNullOrWhiteSpace(MediaContentType)
            ? "video/mp4"
            : MediaContentType.Trim();

        GifView.Source = new HtmlWebViewSource
        {
            Html = BuildVideoHtml(_bytes, contentType, _surfaceHex)
        };
    }

    private static string BuildVideoHtml(byte[] video, string contentType, string surfaceHex)
    {
        var builder = new StringBuilder(video.Length * 4 / 3 + 512);
        builder.Append(
            "<!doctype html><html><head><meta charset='utf-8'>" +
            "<meta name='viewport' content='width=device-width,initial-scale=1'>" +
            "<style>html,body{margin:0;padding:0;width:100%;height:100%;" +
            "background:#000;overflow:hidden}" +
            "video{width:100%;height:100%;object-fit:contain;display:block}" +
            "</style></head><body><video controls playsinline preload='metadata' src='data:");
        builder.Append(contentType);
        builder.Append(";base64,");
        builder.Append(Convert.ToBase64String(video));
        builder.Append("'></video></body></html>");
        return builder.ToString();
    }

    private static string BuildHtml(byte[] gif, string surfaceHex)
    {
        var builder = new StringBuilder(gif.Length * 4 / 3 + 512);
        builder.Append(
            "<!doctype html><html><head><meta charset='utf-8'>" +
            "<meta name='viewport' content='width=device-width,initial-scale=1'>" +
            "<style>html,body{margin:0;padding:0;width:100%;height:100%;" +
            "background:" + surfaceHex + ";overflow:hidden}" +
            "img{width:100%;height:100%;object-fit:contain;display:block}</style>" +
            "</head><body><img alt='' src='data:image/gif;base64,");
        builder.Append(Convert.ToBase64String(gif));
        builder.Append("'></body></html>");
        return builder.ToString();
    }
}
