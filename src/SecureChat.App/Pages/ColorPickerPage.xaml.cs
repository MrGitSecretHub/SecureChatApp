using SecureChat.Core.Text;

namespace SecureChat.App.Pages;

/// <summary>
/// Пикер цвета в духе Telegram: квадрат насыщенности/яркости, ползунок тона,
/// числовые поля H/S/V и R/G/B, поле HEX. Выбранный цвет возвращается
/// в свойстве <see cref="SelectedColor"/> после закрытия страницы.
/// </summary>
public partial class ColorPickerPage : ContentPage
{
    private HsvColor _hsv = new(0, 1, 1);

    /// <summary>Выбранный цвет (null — если пользователь отменил).</summary>
    public Color? SelectedColor { get; private set; }

    /// <summary>Заголовок страницы (например, «Акцентный цвет»).</summary>
    public string PickerTitle { get; set; } = "Выберите цвет";

    public ColorPickerPage()
    {
        InitializeComponent();
    }

    public ColorPickerPage(Color initial, string? title = null)
    {
        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(title))
        {
            PickerTitle = title;
        }

        var (r, g, b) = (initial.Red, initial.Green, initial.Blue);
        _hsv = HsvColor.FromRgb(
            (byte)Math.Round(r * 255),
            (byte)Math.Round(g * 255),
            (byte)Math.Round(b * 255));
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        TitleLabel.Text = PickerTitle;
        UpdateGradients();
        UpdateFields();
    }

    // ============================== ГРАДИЕНТЫ ==============================

    /// <summary>Перерисовывает квадрат S/V и ползунок тона под текущий тон.</summary>
    private void UpdateGradients()
    {
        var hue = _hsv.H;
        var (hueR, hueG, hueB) = HsvColor.FromHue(hue, 1, 1).ToRgb();

        // Квадрат: по горизонтали от белого к чистому тону, по вертикали —
        // от прозрачного к чёрному.
        SaturationLayer.Background = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Colors.White, 0f),
                new GradientStop(Color.FromRgb(hueR, hueG, hueB), 1f)
            },
            new Point(0, 0),
            new Point(1, 0));

        ValueLayer.Background = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Colors.Transparent, 0f),
                new GradientStop(Colors.Black, 1f)
            },
            new Point(0, 0),
            new Point(0, 1));

        // Ползунок тона: полный круг оттенков.
        HueLayer.Background = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromRgb(255, 0, 0), 0f),
                new GradientStop(Color.FromRgb(255, 255, 0), 1f / 6),
                new GradientStop(Color.FromRgb(0, 255, 0), 2f / 6),
                new GradientStop(Color.FromRgb(0, 255, 255), 3f / 6),
                new GradientStop(Color.FromRgb(0, 0, 255), 4f / 6),
                new GradientStop(Color.FromRgb(255, 0, 255), 5f / 6),
                new GradientStop(Color.FromRgb(255, 0, 0), 1f)
            },
            new Point(0, 0),
            new Point(1, 0));

        PreviewBorder.Background = CurrentColor();
    }

    /// <summary>Текущий цвет в терминах MAUI.</summary>
    private Color CurrentColor()
    {
        var (r, g, b) = _hsv.ToRgb();
        return Color.FromRgb(r, g, b);
    }

    // ============================== МАРКЕРЫ ==============================

    /// <summary>Ставит маркеры по текущему цвету (после изменения размера окна).</summary>
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        UpdateMarkers();
    }

    /// <summary>Обновляет положение маркеров на квадрате и ползунке.</summary>
    private void UpdateMarkers()
    {
        var boxWidth = SaturationValueBox.Width;
        var boxHeight = SaturationValueBox.Height;
        if (boxWidth > 0 && boxHeight > 0)
        {
            var x = _hsv.S * boxWidth - SaturationMarker.Width / 2;
            var y = (1 - _hsv.V) * boxHeight - SaturationMarker.Height / 2;
            SaturationMarker.TranslationX = Math.Clamp(x, 0, boxWidth - SaturationMarker.Width);
            SaturationMarker.TranslationY = Math.Clamp(y, 0, boxHeight - SaturationMarker.Height);
        }

        var sliderWidth = HueSlider.Width;
        if (sliderWidth > 0)
        {
            var hueX = (_hsv.H / 360d) * sliderWidth - HueMarker.Width / 2;
            HueMarker.TranslationX = Math.Clamp(hueX, 0, sliderWidth - HueMarker.Width);
        }
    }

    // ============================== ЖЕСТЫ ==============================

    /// <summary>Точка, взятая за основу текущего перетаскивания.</summary>
    private double _dragBaseX;
    private double _dragBaseY;

    private void OnSaturationValueTapped(object? sender, TappedEventArgs e)
    {
        var point = e.GetPosition(SaturationValueBox);
        if (point is null)
        {
            return;
        }

        ApplySaturationValue(point.Value.X, point.Value.Y);
    }

    private void OnSaturationValuePanned(object? sender, PanUpdatedEventArgs e)
    {
        // У события перетаскивания нет позиции касания, поэтому берём смещение
        // от точки, на которой начался жест.
        if (e.StatusType == GestureStatus.Started)
        {
            var start = MarkerPosition();
            _dragBaseX = start.X;
            _dragBaseY = start.Y;
        }

        ApplySaturationValue(_dragBaseX + e.TotalX, _dragBaseY + e.TotalY);
    }

    /// <summary>Текущее положение маркера на квадрате (в координатах квадрата).</summary>
    private Point MarkerPosition()
    {
        var width = SaturationValueBox.Width;
        var height = SaturationValueBox.Height;
        return new Point(_hsv.S * width, (1 - _hsv.V) * height);
    }

    private void ApplySaturationValue(double x, double y)
    {
        var width = SaturationValueBox.Width;
        var height = SaturationValueBox.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var saturation = Math.Clamp(x / width, 0, 1);
        var value = Math.Clamp(1 - (y / height), 0, 1);

        _hsv = new HsvColor(_hsv.H, saturation, value);
        OnColorChanged();
    }

    private void OnHueTapped(object? sender, TappedEventArgs e)
    {
        var point = e.GetPosition(HueSlider);
        if (point is null)
        {
            return;
        }

        ApplyHue(point.Value.X);
    }

    private void OnHuePanned(object? sender, PanUpdatedEventArgs e)
    {
        if (e.StatusType == GestureStatus.Started)
        {
            _dragBaseX = HueMarkerPosition();
        }

        ApplyHue(_dragBaseX + e.TotalX);
    }

    /// <summary>Текущее положение маркера тона (в координатах ползунка).</summary>
    private double HueMarkerPosition() => (_hsv.H / 360d) * HueSlider.Width;

    private void ApplyHue(double x)
    {
        var width = HueSlider.Width;
        if (width <= 0)
        {
            return;
        }

        var hue = Math.Clamp(x / width, 0, 1) * 360d;
        _hsv = new HsvColor(hue, _hsv.S, _hsv.V);
        OnColorChanged();
    }

    /// <summary>Цвет изменился: обновляем градиенты, поля и маркеры.</summary>
    private void OnColorChanged()
    {
        UpdateGradients();
        UpdateFields();
        UpdateMarkers();
    }

    // ============================== ПОЛЯ ==============================

    /// <summary>Обновляет текст полей без обратной связи (иначе — рекурсия).</summary>
    private bool _suppressFieldEvents;

    private void UpdateFields()
    {
        _suppressFieldEvents = true;
        try
        {
            var (r, g, b) = _hsv.ToRgb();

            HueEntry.Text = Math.Round(_hsv.H).ToString();
            SaturationEntry.Text = Math.Round(_hsv.S * 100).ToString();
            ValueEntry.Text = Math.Round(_hsv.V * 100).ToString();

            RedEntry.Text = r.ToString();
            GreenEntry.Text = g.ToString();
            BlueEntry.Text = b.ToString();

            HexEntry.Text = _hsv.ToHex();
        }
        finally
        {
            _suppressFieldEvents = false;
        }
    }

    private void OnHsvFieldChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressFieldEvents)
        {
            return;
        }

        var hue = ParseDouble(HueEntry.Text, _hsv.H);
        var saturation = ParseDouble(SaturationEntry.Text, _hsv.S * 100) / 100d;
        var value = ParseDouble(ValueEntry.Text, _hsv.V * 100) / 100d;

        _hsv = new HsvColor(hue, saturation, value);
        OnColorChanged();
    }

    private void OnRgbFieldChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressFieldEvents)
        {
            return;
        }

        var r = (byte)Math.Clamp(Math.Round(ParseDouble(RedEntry.Text, _hsv.ToRgb().R)), 0, 255);
        var g = (byte)Math.Clamp(Math.Round(ParseDouble(GreenEntry.Text, _hsv.ToRgb().G)), 0, 255);
        var b = (byte)Math.Clamp(Math.Round(ParseDouble(BlueEntry.Text, _hsv.ToRgb().B)), 0, 255);

        _hsv = HsvColor.FromRgb(r, g, b);
        OnColorChanged();
    }

    private void OnHexFieldChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressFieldEvents)
        {
            return;
        }

        var parsed = HsvColor.FromHex(HexEntry.Text);
        if (parsed is null)
        {
            return;
        }

        _hsv = parsed.Value;
        OnColorChanged();
    }

    private static double ParseDouble(string? text, double fallback) =>
        double.TryParse(text?.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    // ============================== КНОПКИ ==============================

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        SelectedColor = null;
        await Navigation.PopModalAsync();
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        SelectedColor = CurrentColor();
        await Navigation.PopModalAsync();
    }
}
