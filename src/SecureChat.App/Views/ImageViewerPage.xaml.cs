using System.IO;

namespace SecureChat.App.Views;

/// <summary>
/// Полноэкранный просмотрщик фото: pinch-to-zoom, панорамирование,
/// двойной тап (приблизить/сбросить) и свайп вниз для закрытия.
/// </summary>
public partial class ImageViewerPage : ContentPage
{
    private const double MaxScale = 4.0;
    private const double DoubleTapScale = 2.5;

    private double _currentScale = 1;
    private double _startScale = 1;
    private double _offsetX;
    private double _offsetY;
    private double _startOffsetX;
    private double _startOffsetY;

    public ImageViewerPage(byte[] imageBytes, string? fileName)
    {
        InitializeComponent();

        Title = string.IsNullOrWhiteSpace(fileName) ? "Изображение" : $"Фото: {fileName}";
        BindingContext = this;
        Image.Source = ImageSource.FromStream(() => new MemoryStream(imageBytes, writable: false));
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
        if (_currentScale > 1.05)
        {
            ResetZoom();
            return;
        }

        await Navigation.PopModalAsync();
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
        await Navigation.PopModalAsync();
    }
}
