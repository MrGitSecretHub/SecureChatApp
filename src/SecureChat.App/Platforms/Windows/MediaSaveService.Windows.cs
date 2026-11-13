namespace SecureChat.App.Services;

public sealed partial class MediaSaveService
{
    private partial async Task<string?> SaveCoreAsync(string fileName, string contentType, byte[] bytes)
    {
        var folder = SelectFolder(contentType);
        Directory.CreateDirectory(folder);

        var target = Path.Combine(folder, SanitizeFileName(fileName));
        await File.WriteAllBytesAsync(target, bytes);
        return target;
    }

    /// <summary>Куда складывать файл: фото — в «Pictures», видео — в «Videos».</summary>
    private static string SelectFolder(string contentType)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = FileSystem.AppDataDirectory;
        }

        if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (!string.IsNullOrWhiteSpace(videos))
            {
                return Path.Combine(videos, "SecureChat");
            }
        }

        if (contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (!string.IsNullOrWhiteSpace(music))
            {
                return Path.Combine(music, "SecureChat");
            }
        }

        return Path.Combine(root, "SecureChat");
    }

    /// <summary>Имя файла без недопустимых символов.</summary>
    private static string SanitizeFileName(string fileName)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "file" : fileName;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }
}
