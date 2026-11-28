namespace SecureChat.App.Services;

/// <summary>Временная диагностика закладок.</summary>
public static class BookmarkDiag
{
    private static readonly object Gate = new();
    private static string Path =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "securechat-bookmarks.log");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path,
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
