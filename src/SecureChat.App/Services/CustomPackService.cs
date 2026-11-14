namespace SecureChat.App.Services;

/// <summary>
/// Свои пакеты GIF и стикеров, импортированные на устройство.
/// Файлы лежат в локальной базе, зашифрованные ключом устройства, и отправляются
/// в чат обычным шифрованным вложением: сервер содержимое не видит.
/// </summary>
public sealed class CustomPackService
{
    public const string GifPackKind = "gif";
    public const string StickerPackKind = "sticker";

    /// <summary>Максимальный размер одного файла в паке (20 МБ).</summary>
    public const int MaxItemBytes = 20 * 1024 * 1024;

    private readonly LocalStore _store;

    public CustomPackService(LocalStore store) => _store = store;

    /// <summary>Пакеты пользователя в порядке добавления.</summary>
    public Task<IReadOnlyList<CustomPack>> GetPacksAsync() =>
        _store.GetCustomPacksAsync();

    /// <summary>Импортирует файлы в пак (создаёт пак, если его ещё нет).</summary>
    public async Task<CustomPack> ImportAsync(
        string packKind,
        string packName,
        IEnumerable<(string FileName, string ContentType, byte[] Data)> files)
    {
        var packs = await _store.GetCustomPacksAsync();
        var pack = packs.FirstOrDefault(item =>
            string.Equals(item.Kind, packKind, StringComparison.OrdinalIgnoreCase));

        if (pack is null)
        {
            pack = new CustomPack
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = packName,
                Kind = packKind
            };
        }

        foreach (var (fileName, contentType, data) in files)
        {
            if (data.Length == 0 || data.Length > MaxItemBytes)
            {
                continue;
            }

            var itemId = Guid.NewGuid().ToString("N");
            await _store.SavePackFileAsync(itemId, data);
            pack.Items.Add(new CustomPackItem
            {
                Id = itemId,
                FileName = fileName,
                ContentType = contentType,
                Size = data.LongLength
            });
        }

        return await _store.SaveCustomPackAsync(pack);
    }

    /// <summary>Удаляет файл из пака.</summary>
    public async Task<bool> DeleteItemAsync(string packId, string itemId)
    {
        var packs = await _store.GetCustomPacksAsync();
        var pack = packs.FirstOrDefault(item =>
            string.Equals(item.Id, packId, StringComparison.Ordinal));
        if (pack is null)
        {
            return false;
        }

        var item = pack.Items.FirstOrDefault(entry =>
            string.Equals(entry.Id, itemId, StringComparison.Ordinal));
        if (item is null)
        {
            return false;
        }

        pack.Items.Remove(item);
        await _store.SaveCustomPackAsync(pack);
        await _store.DeletePackFileAsync(itemId);
        return true;
    }

    /// <summary>Удаляет пак целиком.</summary>
    public Task<bool> DeletePackAsync(string packId) =>
        _store.DeleteCustomPackAsync(packId);

    /// <summary>Байты файла пака (расшифрованные).</summary>
    public Task<byte[]?> LoadItemAsync(string itemId) =>
        _store.LoadPackFileAsync(itemId);

    /// <summary>Определяет тип файла: анимированный GIF или статичная картинка-стикер.</summary>
    public static string KindForFile(string fileName, string contentType)
    {
        if (contentType.StartsWith("image/gif", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            return GifPackKind;
        }

        return StickerPackKind;
    }

    /// <summary>Имя пака по умолчанию для импорта.</summary>
    public static string DefaultPackName(string packKind) =>
        packKind == GifPackKind ? "Мои GIF" : "Мои стикеры";
}
