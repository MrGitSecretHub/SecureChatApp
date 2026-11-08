using System;
using System.Globalization;

namespace SecureChat.App.Views;

/// <summary>Конвертер: процент (0-100) → double (0-1) для ProgressBar.</summary>
public sealed class PercentToDoubleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? d / 100.0 : 0.0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
