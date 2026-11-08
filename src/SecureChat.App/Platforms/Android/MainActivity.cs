using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Microsoft.Maui;
using SecureChat.App.Services;

namespace SecureChat.App;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    Exported = true,
    ConfigurationChanges =
        ConfigChanges.ScreenSize |
        ConfigChanges.Orientation |
        ConfigChanges.UiMode |
        ConfigChanges.ScreenLayout |
        ConfigChanges.SmallestScreenSize |
        ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        HandleNotificationIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleNotificationIntent(intent);
    }

    protected override void OnResume()
    {
        base.OnResume();
        AppLifecycle.IsForeground = true;
    }

    protected override void OnPause()
    {
        AppLifecycle.IsForeground = false;
        base.OnPause();
    }

    private static void HandleNotificationIntent(Intent? intent)
    {
        var username = intent?.GetStringExtra("open_chat");
        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        // Убираем extra, чтобы повторное открытие Activity не открывало чат снова.
        intent!.RemoveExtra("open_chat");

        try
        {
            AppServices.Get<NotificationService>().ActivateFromNotification(username);
        }
        catch
        {
            // Приложение ещё инициализируется — уведомление откроется после входа.
        }
    }
}
