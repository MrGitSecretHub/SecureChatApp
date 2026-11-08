using System.Runtime.InteropServices;

namespace SecureChat.App.Services;

public sealed partial class SoundService
{
    private const uint MbIconAsterisk = 0x00000040;

    private partial void PlayNotificationCore()
    {
        try
        {
            // Системный звук «Уведомление» из звуковой схемы Windows.
            // MessageBeep не требует ссылок на WinForms/WPF.
            MessageBeep(MbIconAsterisk);
        }
        catch
        {
            // Звук не критичен для доставки сообщений.
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint uType);
}
