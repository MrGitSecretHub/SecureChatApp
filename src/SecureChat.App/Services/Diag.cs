using System.Text;

namespace SecureChat.App.Services;

/// <summary>Временный диагностический лог: пишется рядом с исполняемым файлом.</summary>
public static class Diag
{
    private static readonly object Gate = new();
    private static string Path =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "securechat-diag.log");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                var line = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";
                File.AppendAllText(Path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Диагностика не должна ломать приложение.
        }
    }
}
