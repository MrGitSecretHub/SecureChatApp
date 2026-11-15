using System.Text.Json;
using System.Text.Json.Serialization;

namespace SecureChat.Core.Text;

/// <summary>
/// Тема оформления в формате обмена между устройствами.
/// Не содержит ничего, кроме публичных цветов, поэтому безопасен для
/// отправки в чате или сохранения в файл.
/// </summary>
public sealed class ThemeTransfer
{
    /// <summary>Название темы.</summary>
    public string Name { get; set; } = "Моя тема";

    /// <summary>Встроенная тема-основа (название).</summary>
    public string BaseTheme { get; set; } = "Классическая";

    /// <summary>Цвета темы: ключ ресурса → HEX.</summary>
    public Dictionary<string, string> Colors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>Готовит текст для обмена: одна строка JSON.</summary>
    public string ToJson()
    {
        var payload = new Dictionary<string, object?>
        {
            ["name"] = Name,
            ["base"] = BaseTheme,
            ["colors"] = Colors
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>Разбирает полученный текст темы. Возвращает null, если это не тема.</summary>
    public static ThemeTransfer? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Пользователь может прислать текст с посторонними словами вокруг JSON —
        // вырезаем первый объект по фигурным скобкам.
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        var json = text.Substring(start, end - start + 1);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var transfer = new ThemeTransfer();

            if (root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                transfer.Name = name.GetString() ?? transfer.Name;
            }

            if (root.TryGetProperty("base", out var baseTheme) && baseTheme.ValueKind == JsonValueKind.String)
            {
                transfer.BaseTheme = baseTheme.GetString() ?? transfer.BaseTheme;
            }

            if (root.TryGetProperty("colors", out var colors) && colors.ValueKind == JsonValueKind.Object)
            {
                foreach (var pair in colors.EnumerateObject())
                {
                    var key = ThemeColorKeys.NormalizeKey(pair.Name);
                    if (key is null)
                    {
                        continue;
                    }

                    var hex = pair.Value.ValueKind == JsonValueKind.String
                        ? pair.Value.GetString()
                        : null;

                    if (ThemeColors.Normalize(hex) is { } normalized)
                    {
                        transfer.Colors[key] = normalized;
                    }
                }
            }

            return transfer.Colors.Count == 0 ? null : transfer;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
