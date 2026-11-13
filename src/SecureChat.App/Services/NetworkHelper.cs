namespace SecureChat.App.Services;

/// <summary>
/// Проверка типа подключения: нужно ли автозагрузка вложений по сети.
/// Общая часть — проверка наличия интернета, детали — в платформенных файлах.
/// </summary>
public sealed partial class NetworkHelper
{
    /// <summary>Подключение по Wi-Fi (или Ethernet на ПК).</summary>
    public static bool IsWifi
    {
        get
        {
            try
            {
                return Shared.IsWifiCore();
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Есть ли подключение к интернету.</summary>
    public static bool IsOnline
    {
        get
        {
            try
            {
                return Connectivity.Current.NetworkAccess == NetworkAccess.Internet;
            }
            catch
            {
                return false;
            }
        }
    }

    private partial bool IsWifiCore();

    /// <summary>Экземпляр для вызова платформенного метода.</summary>
    private static readonly NetworkHelper Shared = new();
}
