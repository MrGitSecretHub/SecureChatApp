using SecureChat.Core.Text;

namespace SecureChat.Core.Tests;

public class ThemeColorsTests
{
    [Theory]
    [InlineData("#5865F2", "#5865F2")]
    [InlineData("#5865f2", "#5865F2")]
    [InlineData("5865F2", "#5865F2")]
    [InlineData("  #5865F2  ", "#5865F2")]
    [InlineData("#abc", "#AABBCC")]
    [InlineData("#12345678", "#345678")] // #AARRGGBB → отбрасывается альфа-канал
    public void Normalize_ReturnsCanonicalHex(string input, string expected)
    {
        Assert.Equal(expected, ThemeColors.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#GGGGGG")]
    [InlineData("не цвет")]
    public void Normalize_RejectsInvalidValues(string? input)
    {
        Assert.Null(ThemeColors.Normalize(input));
    }

    [Fact]
    public void TryParse_ReturnsChannels()
    {
        Assert.True(ThemeColors.TryParse("#5865F2", out var red, out var green, out var blue));
        Assert.Equal(0x58, red);
        Assert.Equal(0x65, green);
        Assert.Equal(0xF2, blue);
    }

    [Fact]
    public void TryParse_RejectsInvalidValue()
    {
        Assert.False(ThemeColors.TryParse("#ZZZ", out _, out _, out _));
    }

    [Fact]
    public void NormalizeKey_AcceptsKnownRolesCaseInsensitively()
    {
        Assert.Equal("PageBackground", ThemeColorKeys.NormalizeKey("pagebackground"));
        Assert.Equal("MessageThemText", ThemeColorKeys.NormalizeKey("MESSAGETHEMTEXT"));
        Assert.Equal("Accent", ThemeColorKeys.NormalizeKey("  Accent "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("AccentSoft")]
    [InlineData("SelectionOverlay")]
    public void NormalizeKey_RejectsUnknownRoles(string? key)
    {
        Assert.Null(ThemeColorKeys.NormalizeKey(key));
    }

    [Fact]
    public void AllowedRoles_CoverEditorPalette()
    {
        // Каждый ключ из палитры редактора должен быть разрешён к переопределению.
        foreach (var key in ThemeColorKeys.All)
        {
            Assert.Equal(key, ThemeColorKeys.NormalizeKey(key));
        }
    }
}
