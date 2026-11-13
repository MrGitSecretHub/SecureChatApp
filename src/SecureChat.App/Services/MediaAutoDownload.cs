namespace SecureChat.App.Services;

/// <summary>
/// Режим автозагрузки вложений: «Всегда», «Только по Wi-Fi» или «Никогда».
/// Экономит мобильный трафик: крупные файлы не тянутся сами, пока
/// пользователь не откроет их вручную.
/// </summary>
public static class MediaAutoDownload
{
    public const string Always = "Всегда";
    public const string Wifi = "По Wi-Fi";
    public const string Never = "Никогда";

    public static readonly string[] Options = { Always, Wifi, Never };

    /// <summary>Разрешена ли автозагрузка при текущем режиме и сети.</summary>
    public static bool IsAllowed(string? mode) => mode switch
    {
        Always => true,
        Never => false,
        _ => NetworkHelper.IsWifi
    };
}
