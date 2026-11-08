using System;
using System.Globalization;

namespace SecureChat.App.Views;

/// <summary>
/// Инвертирует значение bool (true → false, false → true).
/// Может использоваться для определения цвета неактивного чата.
/// </summary>
public sealed class InvertedBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return !b;
        }
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}