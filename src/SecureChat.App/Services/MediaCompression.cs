using Microsoft.Maui.Graphics.Platform;

namespace SecureChat.App.Services;

/// <summary>Подготовка изображений перед отправкой в чат.</summary>
public static class MediaCompression
{
    /// <summary>
    /// Масштабирует фото до 2560 px по длинной стороне и пережимает:
    /// камера отдаёт многомегабайтные снимки, в чат уходят разумные размеры.
    /// Если пережать не удалось или результат больше оригинала — возвращается
    /// исходный файл.
    /// </summary>
    public static (byte[] Data, string ContentType) CompressImage(byte[] data, string contentType)
    {
        if (data.Length < 400 * 1024 ||
            !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return (data, contentType);
        }

        try
        {
            using var source = new MemoryStream(data);
            var image = PlatformImage.FromStream(source);
            if (image is null)
            {
                return (data, contentType);
            }

            const int maxSide = 2560;
            var scale = Math.Min(1.0, (double)maxSide / Math.Max(image.Width, image.Height));
            var targetW = Math.Max(1, (int)Math.Round(image.Width * scale));
            var targetH = Math.Max(1, (int)Math.Round(image.Height * scale));

            using var resized = image.Resize(targetW, targetH, ResizeMode.Fit);
            using var target = new MemoryStream();

            var keepPng = contentType.Contains("png", StringComparison.OrdinalIgnoreCase) ||
                          contentType.Contains("webp", StringComparison.OrdinalIgnoreCase);
            resized.Save(target, keepPng ? ImageFormat.Png : ImageFormat.Jpeg, keepPng ? 100 : 85);

            var result = target.ToArray();
            if (result.Length == 0 || result.Length >= data.Length)
            {
                return (data, contentType);
            }

            return (result, keepPng ? contentType : "image/jpeg");
        }
        catch
        {
            // Не удалось пережать (формат без кодека) — отправляем оригинал.
            return (data, contentType);
        }
    }
}
