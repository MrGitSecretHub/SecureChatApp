using System;
using System.Globalization;

namespace SecureChat.App.Views;

/// <summary>Подпись «изменено» для отредактированного сообщения (видимость задаётся отдельно).</summary>
public sealed class EditedToLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        "изменено";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
