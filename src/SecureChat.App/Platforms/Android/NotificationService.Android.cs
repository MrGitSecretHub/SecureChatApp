#if ANDROID
using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using AndroidColor = Android.Graphics.Color;
using Microsoft.Maui.ApplicationModel;
using SecureChat.App.Services;

namespace SecureChat.App.Services;

public sealed partial class NotificationService
{
    private const string ChannelId = "securechat.messages";
    private const string ChannelName = "Сообщения SecureChat";
    private const string GroupKey = "securechat.messages.group";
    private const int SummaryNotificationId = 1;
    private const int PermissionRequestCode = 4501;

    public partial void Initialize()
    {
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var channel = new NotificationChannel(
                    ChannelId,
                    ChannelName,
                    NotificationImportance.High)
                {
                    Description = "Уведомления о новых сообщениях"
                };
                channel.EnableLights(true);
                channel.LightColor = AndroidColor.ParseColor("#0088CC");
                channel.EnableVibration(true);

                var manager = (NotificationManager)Android.App.Application.Context
                    .GetSystemService(Context.NotificationService)!;
                manager.CreateNotificationChannel(channel);
            }
        }
        catch
        {
            // Канал уведомлений не критичен, встроенный тост покажет сообщение.
        }
    }

    public partial void EnsurePermission()
    {
        try
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu)
            {
                return;
            }

            var activity = Platform.CurrentActivity;
            if (activity is null)
            {
                return;
            }

            if (ContextCompat.CheckSelfPermission(
                    activity,
                    Android.Manifest.Permission.PostNotifications) == Permission.Granted)
            {
                return;
            }

            ActivityCompat.RequestPermissions(
                activity,
                new[] { Android.Manifest.Permission.PostNotifications },
                PermissionRequestCode);
        }
        catch
        {
            // Отказ в разрешении не мешает работе чата.
        }
    }

    public partial bool ShowNotification(string title, string body, string username)
    {
        try
        {
            var context = Android.App.Application.Context;
            var compat = NotificationManagerCompat.From(context);
            if (!compat.AreNotificationsEnabled())
            {
                return false;
            }

            var notificationId = NotificationIdFor(username);
            var intent = new Intent(context, typeof(MainActivity));
            intent.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop | ActivityFlags.NewTask);
            intent.PutExtra("open_chat", username);

            var pendingIntent = PendingIntent.GetActivity(
                context,
                notificationId,
                intent,
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

            var builder = new NotificationCompat.Builder(context, ChannelId)
                .SetContentTitle(title)
                .SetContentText(body)
                .SetSmallIcon(Resource.Drawable.ic_notification)
                .SetColor(AndroidColor.ParseColor("#0088CC"))
                .SetAutoCancel(true)
                .SetContentIntent(pendingIntent)
                .SetPriority((int)NotificationPriority.High)
                .SetDefaults((int)NotificationDefaults.Sound | (int)NotificationDefaults.Vibrate)
                .SetGroup(GroupKey)
                .SetShowWhen(true)
                .SetWhen(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            compat.Notify(notificationId, builder.Build());

            // Сводное уведомление группы — без звука, чтобы не дублировать сигнал.
            var summary = new NotificationCompat.Builder(context, ChannelId)
                .SetContentTitle("SecureChat")
                .SetContentText("Новые сообщения")
                .SetSmallIcon(Resource.Drawable.ic_notification)
                .SetColor(AndroidColor.ParseColor("#0088CC"))
                .SetAutoCancel(true)
                .SetSilent(true)
                .SetGroup(GroupKey)
                .SetGroupSummary(true)
                .SetContentIntent(pendingIntent)
                .Build();

            compat.Notify(SummaryNotificationId, summary);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int NotificationIdFor(string username)
    {
        // FNV-1a: стабильный ID в пределах процесса для каждого чата.
        uint hash = 2166136261;
        foreach (var ch in username.ToLowerInvariant())
        {
            hash = (hash ^ ch) * 16777619;
        }

        return 1000 + (int)(hash % 100000);
    }

    public partial void ActivateWindow()
    {
        // На Android активация окна обрабатывается через PendingIntent в уведомлении.
    }
}
#endif
