namespace SecureChat.App.Services;

/// <summary>
/// Сохранение полученных вложений: фото и видео — в галерею устройства
/// (Android), на ПК — в выбранный пользователем файл.
/// </summary>
public sealed partial class MediaSaveService
{
    /// <summary>
    /// Сохраняет вложение. Возвращает текст результата для подсказки
    /// (пустая строка — пользователь отменил сохранение).
    /// </summary>
    public async Task<string> SaveAsync(string fileName, string contentType, byte[] bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            return "Файл пустой.";
        }

        try
        {
            var result = await SaveCoreAsync(fileName, contentType, bytes);
            return result ?? string.Empty;
        }
        catch (Exception ex)
        {
            return $"Не удалось сохранить: {ex.Message}";
        }
    }

    private partial Task<string?> SaveCoreAsync(string fileName, string contentType, byte[] bytes);
}
