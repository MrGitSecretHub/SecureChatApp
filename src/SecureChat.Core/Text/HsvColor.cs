namespace SecureChat.Core.Text;

/// <summary>
/// Цвет в HSV-пространстве (тон, насыщенность, яркость) — основа пикера цвета.
/// H — градусы 0..360, S и V — доли 0..1.
/// </summary>
public readonly record struct HsvColor
{
    /// <summary>Тон: градусы 0..360 (нормализуется при создании).</summary>
    public double H { get; }

    /// <summary>Насыщенность: 0..1.</summary>
    public double S { get; }

    /// <summary>Яркость: 0..1.</summary>
    public double V { get; }

    public HsvColor(double hue, double saturation, double value)
    {
        H = ((hue % 360) + 360) % 360;
        S = Math.Clamp(saturation, 0, 1);
        V = Math.Clamp(value, 0, 1);
    }

    /// <summary>Каналы красного, зелёного и синего — 0..255.</summary>
    public (byte R, byte G, byte B) ToRgb()
    {
        var h = H / 60d;
        var c = V * S;
        var x = c * (1 - Math.Abs(h % 2 - 1));
        var m = V - c;

        var (r, g, b) = h switch
        {
            < 1 => (c, x, 0d),
            < 2 => (x, c, 0d),
            < 3 => (0d, c, x),
            < 4 => (0d, x, c),
            < 5 => (x, 0d, c),
            _ => (c, 0d, x)
        };

        return (
            ToByte(r + m),
            ToByte(g + m),
            ToByte(b + m));
    }

    /// <summary>Цвет в формате #RRGGBB.</summary>
    public string ToHex()
    {
        var (r, g, b) = ToRgb();
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>Собирает цвет из каналов RGB (0..255).</summary>
    public static HsvColor FromRgb(byte r, byte g, byte b)
    {
        var rf = r / 255d;
        var gf = g / 255d;
        var bf = b / 255d;

        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;

        double h;
        if (delta <= 0.0001)
        {
            h = 0;
        }
        else if (max == rf)
        {
            h = 60 * (((gf - bf) / delta) % 6);
        }
        else if (max == gf)
        {
            h = 60 * (((bf - rf) / delta) + 2);
        }
        else
        {
            h = 60 * (((rf - gf) / delta) + 4);
        }

        var s = max <= 0.0001 ? 0 : delta / max;
        return new HsvColor(h, s, max);
    }

    /// <summary>Разбирает HEX (#RGB, #RRGGBB, #AARRGGBB) в HSV.</summary>
    public static HsvColor? FromHex(string? value)
    {
        if (!ThemeColors.TryParse(value, out var r, out var g, out var b))
        {
            return null;
        }

        return FromRgb(r, g, b);
    }

    /// <summary>Цвет заданной насыщенности и яркости при указанном тоне.</summary>
    public static HsvColor FromHue(double hue, double saturation, double value) =>
        new(hue, Math.Clamp(saturation, 0, 1), Math.Clamp(value, 0, 1));

    /// <summary>Яркость в HSL (0..1) — как показывает Telegram.</summary>
    public double Lightness
    {
        get
        {
            var (r, g, b) = ToRgb();
            var max = Math.Max(r, Math.Max(g, b)) / 255d;
            var min = Math.Min(r, Math.Min(g, b)) / 255d;
            return (max + min) / 2;
        }
    }

    private static byte ToByte(double value) =>
        (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
}
