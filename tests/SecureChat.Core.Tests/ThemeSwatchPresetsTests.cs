using SecureChat.Core.Text;

namespace SecureChat.Core.Tests;

public class ThemeSwatchPresetsTests
{
    [Fact]
    public void AllSwatches_AreValidHex()
    {
        foreach (var hex in ThemeSwatchPresets.All)
        {
            Assert.NotNull(ThemeColors.Normalize(hex));
        }
    }

    [Fact]
    public void AllSwatches_AreUnique()
    {
        var unique = new HashSet<string>(ThemeSwatchPresets.All, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ThemeSwatchPresets.All.Count, unique.Count);
    }

    [Fact]
    public void Families_HaveTitlesAndColors()
    {
        foreach (var family in ThemeSwatchPresets.Families)
        {
            Assert.False(string.IsNullOrWhiteSpace(family.Title));
            Assert.NotEmpty(family.Colors);
        }
    }

    [Fact]
    public void All_ContainsEveryFamilyColor()
    {
        var fromFamilies = ThemeSwatchPresets.Families
            .SelectMany(family => family.Colors)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(fromFamilies.Count, ThemeSwatchPresets.All.Count);
    }

    [Fact]
    public void Palettes_LookLikeThemePresets()
    {
        // Образцов должно быть заметно больше, чем было (26) — это цель доработки.
        Assert.True(ThemeSwatchPresets.All.Count >= 60,
            $"Слишком мало образцов: {ThemeSwatchPresets.All.Count}");
    }
}
