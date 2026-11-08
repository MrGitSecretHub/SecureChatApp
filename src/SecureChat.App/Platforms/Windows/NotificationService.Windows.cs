using System.Security.Principal;
using Microsoft.Maui;
using Microsoft.UI.Windowing;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace SecureChat.App.Services;

public sealed partial class NotificationService
{
    private bool _initialized;
    private bool _available;

    public partial void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            // Windows не показывает toast-уведомления приложениям, запущенным
            // от имени администратора. В этом случае клиент покажет уведомление
            // внутри окна.
            if (IsElevated())
            {
                return;
            }

            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            _available = true;
        }
        catch
        {
            _available = false;
        }
    }

    public partial void EnsurePermission()
    {
        // Windows не запрашивает разрешение на уведомления.
    }

    public partial bool ShowNotification(
        string title,
        string body,
        string username,
        int unreadCount,
        bool silent)
    {
        if (!_available)
        {
            return false;
        }
        try
        {
            var notification = new AppNotificationBuilder()
                .AddArgument("action", "open-chat")
                .AddArgument("username", username)
                .AddText(title)
                .AddText(body)
                .MuteAudio() // звук проигрывает SoundService — без двойного сигнала
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public partial void ActivateWindow()
    {
        if (Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView
            is not MauiWinUIWindow nativeWindow)
        {
            return;
        }

        nativeWindow.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (nativeWindow.AppWindow.Presenter is OverlappedPresenter presenter &&
                    presenter.State == OverlappedPresenterState.Minimized)
                {
                    presenter.Restore();
                }
            }
            catch
            {
                // Восстановление окна не критично для открытия чата.
            }

            nativeWindow.Activate();
        });
    }

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        if (!args.Arguments.TryGetValue("username", out var username) ||
            string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        ActivateWindow();
        MainThread.BeginInvokeOnMainThread(() => RaiseActivated(username));
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
