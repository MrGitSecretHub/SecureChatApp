namespace SecureChat.App.Services;

/// <summary>
/// Обои переписки: выбор картинки из галереи или файла, копирование её
/// в хранилище приложения, затемнение и снятие.
/// </summary>
public sealed class WallpaperService
{
    private readonly AppSession _session = AppServices.Get<AppSession>();

    /// <summary>Выбор картинки из галереи устройства.</summary>
    public async Task<bool> PickFromGalleryAsync()
    {
        try
        {
            var photo = await MediaPicker.PickPhotoAsync();
            return photo is not null && await ApplyAsync(photo);
        }
        catch
        {
            // Отмена выбора — не ошибка.
            return false;
        }
    }

    /// <summary>Выбор картинки из файлов.</summary>
    public async Task<bool> PickFromFileAsync()
    {
        try
        {
            var file = await FilePicker.PickAsync(new PickOptions
            {
                PickerTitle = "Выберите изображение для обоев",
                FileTypes = FilePickerFileType.Images
            });

            return file is not null && await ApplyAsync(file);
        }
        catch
        {
            // Отмена выбора — не ошибка.
            return false;
        }
    }

    /// <summary>Копирует выбранную картинку в хранилище приложения (только
    /// для чтения приложением — исходный файл может исчезнуть).</summary>
    public async Task<bool> ApplyAsync(FileResult file)
    {
        try
        {
            var extension = Path.GetExtension(file.FullPath ?? file.FileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".jpg";
            }

            var target = Path.Combine(FileSystem.AppDataDirectory, "wallpaper" + extension);
            await using (var output = File.Create(target))
            {
                using var source = await file.OpenReadAsync();
                await source.CopyToAsync(output);
            }

            RemoveOldWallpapers(target);

            _session.WallpaperPath = target;
            _session.NotifyAppearanceChanged();
            await App.SaveAppearanceAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Убирает обои переписки.</summary>
    public async Task RemoveAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_session.WallpaperPath) && File.Exists(_session.WallpaperPath))
            {
                File.Delete(_session.WallpaperPath);
            }
        }
        catch
        {
            // Файл может быть занят — не критично.
        }

        _session.WallpaperPath = string.Empty;
        _session.NotifyAppearanceChanged();
        await App.SaveAppearanceAsync();
    }

    private static void RemoveOldWallpapers(string keep)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(FileSystem.AppDataDirectory, "wallpaper.*"))
            {
                if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Не удалось удалить старые копии — не критично.
        }
    }
}
