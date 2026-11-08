using System;
using System.Globalization;

namespace SecureChat.App.Views;

/// <summary>Конвертер: bool IsPlaying → символ кнопки (⏸ / ▶).</summary>
public sealed class PausePlayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && b ? "⏸" : "▶";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
