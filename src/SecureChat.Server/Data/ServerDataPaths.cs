using Microsoft.Extensions.Hosting;

namespace SecureChat.Server.Data;

/// <summary>
/// Единый каталог данных для всех способов запуска сервера.
/// <para>
/// Путь <c>App_Data</c> в конфигурации относительный, а <see cref="IHostEnvironment.ContentRootPath"/>
/// у <c>dotnet run</c> и у exe из <c>bin\Debug</c> / <c>bin\Release</c> разный — из-за этого
/// создавались три независимые базы. Здесь каталог проекта ищется вверх по дереву
/// по файлу <c>SecureChat.Server.csproj</c>, поэтому все локальные запуски используют
/// одну и ту же папку <c>src\SecureChat.Server\App_Data</c>.
/// </para>
/// </summary>
public static class ServerDataPaths
{
    private const string ProjectMarker = "SecureChat.Server.csproj";
    private const string DataDirectoryName = "App_Data";

    /// <summary>Каталог данных. Переопределяется переменной окружения SECURECHAT_DATA_DIR.</summary>
    public static string ResolveDataDirectory(IHostEnvironment environment)
    {
        var configured = Environment.GetEnvironmentVariable("SECURECHAT_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Path.Combine(FindProjectDirectory(environment.ContentRootPath), DataDirectoryName);
    }

    /// <summary>Абсолютный путь к файлу базы из относительного Data Source.</summary>
    public static string ResolveDatabasePath(string relativeOrAbsolute, IHostEnvironment environment)
    {
        if (Path.IsPathRooted(relativeOrAbsolute))
        {
            return relativeOrAbsolute;
        }

        // Конфигурация по умолчанию — "App_Data/securechat.db": берём только имя файла,
        // чтобы не получить App_Data\App_Data. Любой относительный путь кладём в единый каталог.
        var fileName = Path.GetFileName(relativeOrAbsolute);
        return Path.Combine(ResolveDataDirectory(environment), fileName);
    }

    private static string FindProjectDirectory(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, ProjectMarker)))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        // Опубликованное приложение без csproj — данные рядом с контентом.
        return startDirectory;
    }
}
