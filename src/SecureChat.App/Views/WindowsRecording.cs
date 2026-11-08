#if WINDOWS
using System;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;

namespace SecureChat.App.Views;

/// <summary>Помощник доступа к микрофону и камере на Windows (запуск настроек системы).</summary>
public static class WindowsRecording
{
    /// <summary>Проверяет и запрашивает доступ к микрофону; при отказе открывает настройки Windows.</summary>
    public static async Task<bool> EnsureMicrophoneAllowedAsync(Page page)
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.Microphone>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.Microphone>();
            }

            if (status == PermissionStatus.Granted)
            {
                return true;
            }

            var open = await page.DisplayAlert(
                "Нет доступа к микрофону",
                "Windows запрещает доступ к микрофону. В «Настройки → Конфиденциальность → Микрофон» включите доступ для классических приложений.",
                "Открыть настройки",
                "Закрыть");

            if (open)
            {
                try
                {
                    await Launcher.OpenAsync("ms-settings:privacy-microphone");
                }
                catch
                {
                    // если настройки не открылись — пользователь сделает это вручную
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            await page.DisplayAlert("Ошибка", $"Не удалось проверить доступ к микрофону: {ex.Message}", "OK");
            return false;
        }
    }

    /// <summary>Проверяет, что камера подключена и доступ разрешён системно; иначе открывает настройки.</summary>
    public static async Task<bool> EnsureCameraReadyAsync(Page page)
    {
        try
        {
            var devices = await Windows.Devices.Enumeration.DeviceInformation
                .FindAllAsync(Windows.Devices.Enumeration.DeviceClass.VideoCapture);
            if (devices.Count == 0)
            {
                await page.DisplayAlert("Видеокружок", "Камера не найдена. Подключите веб-камеру и попробуйте снова.", "OK");
                return false;
            }

            var access = Windows.Devices.Enumeration.DeviceAccessInformation
                .CreateFromDeviceClass(Windows.Devices.Enumeration.DeviceClass.VideoCapture)
                .CurrentStatus;

            if (access == Windows.Devices.Enumeration.DeviceAccessStatus.Allowed)
            {
                return true;
            }

            var open = await page.DisplayAlert(
                "Нет доступа к камере",
                "Windows запрещает доступ к камере. В «Настройки → Конфиденциальность → Камера» включите доступ для классических приложений.",
                "Открыть настройки",
                "Закрыть");

            if (open)
            {
                try
                {
                    await Launcher.OpenAsync("ms-settings:privacy-webcam");
                }
                catch
                {
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            await page.DisplayAlert("Ошибка", $"Камера недоступна: {ex.Message}", "OK");
            return false;
        }
    }
}
#endif
