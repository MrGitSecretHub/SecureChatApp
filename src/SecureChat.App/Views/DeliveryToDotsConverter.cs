using System;
using System.Globalization;

namespace SecureChat.App.Views;

/// <summary>
/// Конвертер: статус доставки → галочки.
/// </summary>
public sealed class DeliveryToDotsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            "В очереди" => "🕐",
            "Отправка" => "⏳",
            "Отправлено" => "✓",
            "Доставлено" => "✓✓",
            "Прочитано" => "✓✓",
            "Ошибка" => "!",
            "Не отправлено" => "!",
            _ => string.Empty
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}