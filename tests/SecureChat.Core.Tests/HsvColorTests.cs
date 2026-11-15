using SecureChat.Core.Text;

namespace SecureChat.Core.Tests;

public class HsvColorTests
{
    [Theory]
    [InlineData(0, 1, 1, 255, 0, 0)]
    [InlineData(120, 1, 1, 0, 255, 0)]
    [InlineData(240, 1, 1, 0, 0, 255)]
    [InlineData(60, 1, 1, 255, 255, 0)]
    [InlineData(180, 1, 1, 0, 255, 255)]
    [InlineData(300, 1, 1, 255, 0, 255)]
    public void ToRgb_ConvertsPureHues(int hue, int saturation, int value, byte r, byte g, byte b)
    {
        var (red, green, blue) = new HsvColor(hue, saturation, value).ToRgb();

        Assert.Equal(r, red);
        Assert.Equal(g, green);
        Assert.Equal(b, blue);
    }

    [Fact]
    public void ToRgb_HandlesZeroSaturation()
    {
        var (r, g, b) = new HsvColor(0, 0, 0.5).ToRgb();

        Assert.Equal(r, g);
        Assert.Equal(g, b);
        Assert.InRange(r, 127, 128);
    }

    [Theory]
    [InlineData("#FF0000", 0)]
    [InlineData("#00FF00", 120)]
    [InlineData("#0000FF", 240)]
    [InlineData("#FF00FF", 300)]
    public void FromRgb_RecoversHue(string hex, double expectedHue)
    {
        var hsv = HsvColor.FromHex(hex);

        Assert.NotNull(hsv);
        Assert.InRange(hsv!.Value.H, expectedHue - 0.5, expectedHue + 0.5);
        Assert.Equal(1, hsv.Value.S, 3);
        Assert.Equal(1, hsv.Value.V, 3);
    }

    [Fact]
    public void RoundTrip_HexPreservesColor()
    {
        foreach (var hex in new[] { "#FF0000", "#00FF00", "#0000FF", "#123456", "#FFFFFF", "#000000", "#808080" })
        {
            var hsv = HsvColor.FromHex(hex);

            Assert.NotNull(hsv);
            Assert.Equal(hex, hsv!.Value.ToHex());
        }
    }

    [Fact]
    public void FromHex_RejectsInvalid()
    {
        Assert.Null(HsvColor.FromHex("не цвет"));
        Assert.Null(HsvColor.FromHex(null));
        Assert.Null(HsvColor.FromHex("#12"));
    }

    [Fact]
    public void Hue_WrapsAround()
    {
        Assert.Equal(10, new HsvColor(370, 1, 1).H, 3);
        Assert.Equal(350, new HsvColor(-10, 1, 1).H, 3);
    }

    [Fact]
    public void Lightness_IsHalfBetweenMinAndMax()
    {
        // Белый — 100%, чёрный — 0%, средний серый — около 50%.
        Assert.Equal(1, new HsvColor(0, 0, 1).Lightness, 3);
        Assert.Equal(0, new HsvColor(0, 0, 0).Lightness, 3);
        Assert.InRange(new HsvColor(0, 0, 0.5).Lightness, 0.49, 0.51);
    }

    [Fact]
    public void FromHue_ClampsValues()
    {
        var color = HsvColor.FromHue(30, 5, -1);

        Assert.Equal(1, color.S, 3);
        Assert.Equal(0, color.V, 3);
    }
}
