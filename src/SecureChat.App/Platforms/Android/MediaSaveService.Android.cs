using Android.Content;
using Android.Provider;

namespace SecureChat.App.Services;

public sealed partial class MediaSaveService
{
    // Стандартные колонки MediaStore (в .NET Android нет констант-обёрток).
    private const string ColumnDisplayName = "display_name";
    private const string ColumnMimeType = "mime_type";
    private const string ColumnRelativePath = "relative_path";
    private const string ColumnIsPending = "is_pending";

    private partial async Task<string?> SaveCoreAsync(string fileName, string contentType, byte[] bytes)
    {
        var context = Android.App.Application.Context;
        var resolver = context.ContentResolver;
        if (resolver is null)
        {
            return null;
        }

        var (collection, relativePath, mimeType) = SelectTarget(contentType, fileName);

        var values = new ContentValues();
        values.Put(ColumnDisplayName, fileName);
        values.Put(ColumnMimeType, mimeType);
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            values.Put(ColumnRelativePath, relativePath);
            values.Put(ColumnIsPending, 1);
        }

        var uri = resolver.Insert(collection, values);
        if (uri is null)
        {
            return null;
        }

        try
        {
            await using var stream = resolver.OpenOutputStream(uri);
            if (stream is null)
            {
                return null;
            }

            await stream.WriteAsync(bytes);
        }
        finally
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                var done = new ContentValues();
                done.Put(ColumnIsPending, 0);
                resolver.Update(uri, done, null, null);
            }
        }

        return relativePath;
    }

    /// <summary>Куда складывать файл: фото — в «Pictures», видео — в «Movies».</summary>
    private static (Android.Net.Uri Collection, string RelativePath, string MimeType) SelectTarget(
        string contentType,
        string fileName)
    {
        var mime = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;

        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return (MediaStore.Images.Media.ExternalContentUri!, "Pictures/SecureChat", mime);
        }

        if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return (MediaStore.Video.Media.ExternalContentUri!, "Movies/SecureChat", mime);
        }

        if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            return (MediaStore.Audio.Media.ExternalContentUri!, "Music/SecureChat", mime);
        }

        // Остальные файлы — в общую папку загрузок.
        return (MediaStore.Downloads.ExternalContentUri!, "Download/SecureChat", mime);
    }
}
