using System.IO;
using SecureChat.App.Services;

namespace SecureChat.App.Views;

/// <summary>
/// Полноэкранный просмотрщик фото: pinch-to-zoom, панорамирование,
/// двойной тап (приблизить/сбросить) и свайп вниз для закрытия.
/// </summary>
public partial class ImageViewerPage : ContentPage
{
    private const double MaxScale = 4.0;
    private const double DoubleTapScale = 2.5;

    private readonly byte[] _bytes;
    private readonly string _fileName;

    private double _currentScale = 1;
    private double _startScale = 1;
    private double _offsetX;
    private double _offsetY;
    private double _startOffsetX;
    private double _startOffsetY;
    private bool _closing;

    public ImageViewerPage(byte[] imageBytes, string? fileName)
    {
        Diag.Log("VIEWER ctor");
        InitializeComponent();

        _bytes = imageBytes;
        _fileName = string.IsNullOrWhiteSpace(fileName) ? "photo.jpg" : fileName;

        var isAnimatedGif = GifWebView.IsGifData(imageBytes);
        Title = string.IsNullOrWhiteSpace(fileName)
            ? (isAnimatedGif ? "GIF" : Loc.T("rs_055", "Изображение"))
            : $"{(isAnimatedGif ? "GIF" : Loc.T("rs_167", "Фото"))}: {fileName}";
        BindingContext = this;

        if (isAnimatedGif)
        {
            // GIF анимируем через WebView; статичный Image остаётся пустым.
            Image.IsVisible = false;
            var gifView = new GifWebView
            {
                Bytes = imageBytes,
                SurfaceHex = "#000000",
                Fill = true,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill
            };
            var grid = (Microsoft.Maui.Controls.Grid)Content;
            grid.Children.Insert(0, gifView);
            ZoomHint.IsVisible = false;
        }
        else
        {
            Image.Source = ImageSource.FromStream(() => new MemoryStream(imageBytes, writable: false));
        }
    }

    public ImageSource? Source => Image.Source;

    private void OnPinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        if (e.Status == GestureStatus.Started)
        {
            _startScale = _currentScale;
            _startOffsetX = _offsetX;
            _startOffsetY = _offsetY;
            return;
        }

        if (e.Status == GestureStatus.Running)
        {
            var next = Math.Clamp(_startScale * e.Scale, 1.0, MaxScale);
            var growth = next / _currentScale;
            _currentScale = next;

            // Приближаем к точке между пальцами: смещение растёт пропорционально масштабу.
            var originX = (e.ScaleOrigin.X - 0.5) * Image.Width;
            var originY = (e.ScaleOrigin.Y - 0.5) * Image.Height;
            _offsetX = _startOffsetX * growth - originX * (growth - 1);
            _offsetY = _startOffsetY * growth - originY * (growth - 1);
            ClampOffsets();
            ApplyTransform();
            return;
        }

        if (e.Status is GestureStatus.Completed or GestureStatus.Canceled)
        {
            if (_currentScale <= 1.01)
            {
                ResetZoom();
            }
            else
            {
                ClampOffsets();
                ApplyTransform();
            }
        }
    }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (_currentScale <= 1.01)
        {
            return;
        }

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _startOffsetX = _offsetX;
                _startOffsetY = _offsetY;
                break;
            case GestureStatus.Running:
                _offsetX = _startOffsetX + e.TotalX;
                _offsetY = _startOffsetY + e.TotalY;
                ClampOffsets();
                ApplyTransform();
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                ClampOffsets();
                ApplyTransform();
                break;
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_currentScale > 1.05)
        {
            ResetZoom();
            return;
        }

        _currentScale = DoubleTapScale;

        // Приближаем к точке двойного касания, а не к центру экрана.
        var point = e.GetPosition(Image);
        if (point is not null && Image.Width > 0 && Image.Height > 0)
        {
            var relativeX = point.Value.X - Image.Width / 2;
            var relativeY = point.Value.Y - Image.Height / 2;
            _offsetX = -relativeX * (_currentScale - 1);
            _offsetY = -relativeY * (_currentScale - 1);
        }

        ClampOffsets();
        ApplyTransform();
    }

    private async void OnSwipeDown(object? sender, SwipedEventArgs e)
    {
        Diag.Log("VIEWER swipe down");
        if (_currentScale > 1.05)
        {
            ResetZoom();
            return;
        }

        await CloseAsync();
    }

    private async Task CloseAsync()
    {
        Diag.Log($"VIEWER close begin {_closing}");
        if (_closing) return;
        _closing = true;
        await Navigation.PopAsync();
        Diag.Log("VIEWER closed");
    }

    private void ResetZoom()
    {
        _currentScale = 1;
        _offsetX = 0;
        _offsetY = 0;
        ApplyTransform();
    }

    private void ClampOffsets()
    {
        var scaledWidth = Image.Width * _currentScale;
        var scaledHeight = Image.Height * _currentScale;
        var maxX = Math.Max(0, (scaledWidth - Width) / 2);
        var maxY = Math.Max(0, (scaledHeight - Height) / 2);
        _offsetX = Math.Clamp(_offsetX, -maxX, maxX);
        _offsetY = Math.Clamp(_offsetY, -maxY, maxY);
    }

    private void ApplyTransform()
    {
        Image.Scale = _currentScale;
        Image.TranslationX = _offsetX;
        Image.TranslationY = _offsetY;
        ZoomHint.IsVisible = _currentScale <= 1.05;
    }

    private async void OnCloseClicked(object? sender, EventArgs e)
    {
        Diag.Log("VIEWER X clicked");
        await CloseAsync();
    }

    /// <summary>Сохранить фото на устройство: в галерею или в папку «Pictures».</summary>
    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        var contentType = Path.GetExtension(_fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".heic" => "image/heic",
            _ => "image/jpeg"
        };

        var result = await AppServices.Get<MediaSaveService>()
            .SaveAsync(_fileName, contentType, _bytes);

        if (string.IsNullOrEmpty(result))
        {
            return;
        }

        await DisplayAlert(
            result.StartsWith(Loc.T("uc_022", "Не удалось"), StringComparison.Ordinal) ? Loc.T("common_error", "Ошибка") : Loc.T("dg_064", "Сохранено"),
            result,
            "OK");
    }
}
