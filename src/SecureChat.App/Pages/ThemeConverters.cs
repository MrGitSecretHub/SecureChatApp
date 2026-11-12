using System;
using System.Globalization;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using SecureChat.App.Services;

namespace SecureChat.App.Pages;

/// <summary>Цвет фона плитки темы по имени темы.</summary>
public class ThemeNameToBackgroundColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo? culture)
    {
        if (value is not string name) return Colors.Transparent;
        var palette = ThemeCatalog.Resolve(name);
        return palette.PageBackgroundColor;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo? culture)
        => throw new NotImplementedException();
}

/// <summary>Обводка плитки: акцентный цвет если тема выбрана, иначе прозрачно.</summary>
public class ThemeNameToSelectionStrokeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo? culture)
    {
        if (value is not string name) return Colors.Transparent;
        var session = AppServices.Get<AppSession>();
        var isSelected = !string.Equals(session.Theme, ThemeCatalog.SystemTheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(name, session.Theme, StringComparison.OrdinalIgnoreCase);
        return isSelected ? (Color)Application.Current!.Resources["Accent"] : Colors.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo? culture)
        => throw new NotImplementedException();
}

/// <summary>Обводка выбранной плитки: акцентный цвет, у остальных — прозрачная.</summary>
public class SelectedToStrokeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo? culture)
        => value is true
            ? (Color)Application.Current!.Resources["Accent"]
            : Colors.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo? culture)
        => throw new NotImplementedException();
}

/// <summary>Кружок выбора плитки: белый у выбранной темы, полупрозрачный у остальных.</summary>
public class SelectedToRingConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo? culture)
        => value is true ? Colors.White : Colors.White.WithAlpha(0.35f);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo? culture)
        => throw new NotImplementedException();
}

/// <summary>Цвет «пузыря» собеседника в превью плитки темы.</summary>
public class ThemeNameToThemBubbleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo? culture)
    {
        if (value is not string name) return Colors.Transparent;
        return ThemeCatalog.Resolve(name).MessageThemColor;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo? culture)
        => throw new NotImplementedException();
}

/// <summary>Цвет «пузыря» своих сообщений в превью плитки темы.</summary>
public class ThemeNameToMeBubbleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo? culture)
    {
        if (value is not string name) return Colors.Transparent;
        return ThemeCatalog.Resolve(name).MessageMeColor;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo? culture)
        => throw new NotImplementedException();
}
