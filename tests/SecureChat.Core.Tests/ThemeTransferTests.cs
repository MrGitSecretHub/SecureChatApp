using SecureChat.Core.Text;

namespace SecureChat.Core.Tests;

public class ThemeTransferTests
{
    [Fact]
    public void RoundTrip_PreservesColors()
    {
        var transfer = new ThemeTransfer
        {
            Name = "Закат",
            BaseTheme = "Ночная синяя",
            Colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PageBackground"] = "#101014",
                ["Accent"] = "#FF7A45"
            }
        };

        var parsed = ThemeTransfer.Parse(transfer.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal("Закат", parsed!.Name);
        Assert.Equal("Ночная синяя", parsed.BaseTheme);
        Assert.Equal("#101014", parsed.Colors["PageBackground"]);
        Assert.Equal("#FF7A45", parsed.Colors["Accent"]);
    }

    [Fact]
    public void Parse_IgnoresUnknownRoles()
    {
        var parsed = ThemeTransfer.Parse(
            "{\"name\":\"X\",\"colors\":{\"Accent\":\"#111111\",\"Bogus\":\"#222222\",\"AccentSoft\":\"#333333\"}}");

        Assert.NotNull(parsed);
        Assert.Single(parsed!.Colors);
        Assert.True(parsed.Colors.ContainsKey("Accent"));
    }

    [Fact]
    public void Parse_KeepsOnlyValidHex()
    {
        var parsed = ThemeTransfer.Parse(
            "{\"colors\":{\"Accent\":\"не цвет\",\"Danger\":\"#ABCDEF\"}}");

        Assert.NotNull(parsed);
        Assert.Single(parsed!.Colors);
        Assert.Equal("#ABCDEF", parsed.Colors["Danger"]);
    }

    [Fact]
    public void Parse_RejectsGarbage()
    {
        Assert.Null(ThemeTransfer.Parse(null));
        Assert.Null(ThemeTransfer.Parse(""));
        Assert.Null(ThemeTransfer.Parse("просто текст"));
        Assert.Null(ThemeTransfer.Parse("{}"));
        Assert.Null(ThemeTransfer.Parse("{\"colors\":{}}"));
    }

    [Fact]
    public void Parse_FindsJsonInsideText()
    {
        var parsed = ThemeTransfer.Parse(
            "Вот моя тема: {\"name\":\"Тест\",\"colors\":{\"Accent\":\"#5865F2\"}} — держи!");

        Assert.NotNull(parsed);
        Assert.Equal("Тест", parsed!.Name);
    }

    [Fact]
    public void Parse_NormalizesShortHex()
    {
        var parsed = ThemeTransfer.Parse("{\"colors\":{\"Accent\":\"#abc\"}}");

        Assert.NotNull(parsed);
        Assert.Equal("#AABBCC", parsed!.Colors["Accent"]);
    }
}
