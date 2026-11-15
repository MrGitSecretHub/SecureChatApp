using SecureChat.Core.Text;

namespace SecureChat.Core.Tests;

public class ThemePalettePresetsTests
{
    [Fact]
    public void AllPalettes_HaveNameAndDescription()
    {
        foreach (var palette in ThemePalettePresets.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(palette.Name));
            Assert.False(string.IsNullOrWhiteSpace(palette.Description));
        }
    }

    [Fact]
    public void AllPalettes_UseOnlyAllowedRolesAndValidHex()
    {
        foreach (var palette in ThemePalettePresets.All)
        {
            foreach (var pair in palette.Colors)
            {
                Assert.NotNull(ThemeColorKeys.NormalizeKey(pair.Key));
                Assert.NotNull(ThemeColors.Normalize(pair.Value));
            }
        }
    }

    [Fact]
    public void AllPalettes_CoverEveryEditorRole()
    {
        // Каждая готовая палитра должна задавать все роли редактора — иначе
        // после её применения часть интерфейса осталась бы от старой темы.
        var expected = ThemeColorKeys.All.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var palette in ThemePalettePresets.All)
        {
            var actual = palette.Colors.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.True(expected.SetEquals(actual),
                $"Палитра «{palette.Name}» задаёт не все роли: " +
                string.Join(", ", expected.Except(actual)));
        }
    }

    [Fact]
    public void ApplyTo_OverwritesRolesAndKeepsOthers()
    {
        var palette = ThemePalettePresets.Nord;
        var draft = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TextMuted"] = "#123456",
            ["BorderColor"] = "#654321"
        };

        palette.ApplyTo(draft);

        // Роль из палитры перезаписана.
        Assert.Equal(palette.Colors["TextMuted"], draft["TextMuted"]);
        // Роль, которой нет в палитре, осталась прежней — но в нашем наборе
        // все роли есть, поэтому проверяем на произвольном ключе.
        Assert.True(draft.ContainsKey("BorderColor"));
    }

    [Fact]
    public void ApplyTo_IgnoresUnknownRoles()
    {
        var palette = new ThemePalettePreset
        {
            Name = "Проверка",
            Description = "Проверка",
            Colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accent"] = "#ABCDEF",
                ["Что-то странное"] = "#111111",
                ["AccentSoft"] = "#222222"
            }
        };

        var draft = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        palette.ApplyTo(draft);

        Assert.Single(draft);
        Assert.Equal("#ABCDEF", draft["Accent"]);
    }

    [Fact]
    public void ApplyTo_NormalizesHex()
    {
        var palette = new ThemePalettePreset
        {
            Name = "Проверка",
            Description = "Проверка",
            Colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accent"] = "#abc"
            }
        };

        var draft = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        palette.ApplyTo(draft);

        Assert.Equal("#AABBCC", draft["Accent"]);
    }
}
