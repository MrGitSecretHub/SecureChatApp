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
    private const string ChannelIdBase = "securechat.messages";
    private const string ChannelName = "Сообщения SecureChat";
    private const string GroupKey = "securechat.messages.group";
    private const int SummaryNotificationId = 1;
    private const int PermissionRequestCode = 4501;
    private static readonly HashSet<string> ActiveChats = new(StringComparer.OrdinalIgnoreCase);
    private static bool _permissionRequested;

    /// <summary>
    /// Идентификатор канала зависит от выбранного рингтона: у Android звук
    /// канала неизменяем, поэтому смена тона создаёт новый канал. «Системный»
    /// использует базовый идентификатор.
    /// </summary>
    private static string ChannelId
    {
        get
        {
            string id;
            try
            {
                id = AppServices.Get<AppSession>().RingtoneId;
            }
            catch
            {
                id = Ringtones.DefaultId;
            }

            var normalized = Ringtones.Normalize(id);
            return normalized == Ringtones.DefaultId
                ? ChannelIdBase
                : $"{ChannelIdBase}_{normalized}";
        }
    }

    public partial void Initialize()
    {
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var context = Android.App.Application.Context;
                var channelId = ChannelId;
                var channel = new NotificationChannel(
                    channelId,
                    ChannelName,
                    NotificationImportance.High)
                {
                    Description = "Уведомления о новых сообщениях"
                };
                channel.EnableLights(true);
                channel.LightColor = AndroidColor.ParseColor("#0088CC");
                channel.EnableVibration(true);

                // Для встроенного тона назначаем его звуком канала, чтобы он
                // звучал и для фоновых системных уведомлений.
                if (channelId != ChannelIdBase)
                {
                    var builtIn = channelId.Substring(ChannelIdBase.Length + 1);
                    var uri = Android.Net.Uri.Parse(
                        $"android.resource://{context.PackageName}/raw/ringtone_{builtIn}");
                    channel.SetSound(
                        uri,
                        new Android.Media.AudioAttributes.Builder()
                            .SetUsage(Android.Media.AudioUsageKind.Notification)
                            .SetContentType(Android.Media.AudioContentType.Sonification)
                            .Build()!);
                }

                var manager = (NotificationManager)context
                    .GetSystemService(Context.NotificationService)!;
                manager.CreateNotificationChannel(channel);

                // Удаляем каналы прежней версии: звук канала нельзя изменить.
                foreach (var existing in manager.NotificationChannels ?? Enumerable.Empty<NotificationChannel>())
                {
                    var existingId = existing.Id;
                    if (existingId is not null
                        && existingId.StartsWith(ChannelIdBase, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(existingId, channelId, StringComparison.Ordinal))
                    {
                        manager.DeleteNotificationChannel(existingId);
                    }
                }
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

            if (_permissionRequested)
            {
                return;
            }

            _permissionRequested = true;
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

    public partial bool ShowNotification(
        string title,
        string body,
        string username,
        int unreadCount,
        bool silent)
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
                .SetNumber(unreadCount)
                .SetShowWhen(true)
                .SetWhen(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            if (silent)
            {
                // Настройка «Звук уведомлений» выключена: канал High, но
                // конкретное уведомление не должно звучать и вибрировать.
                builder.SetSilent(true);
            }
            else
            {
                builder.SetDefaults(
                    (int)NotificationDefaults.Sound | (int)NotificationDefaults.Vibrate);
            }

            compat.Notify(notificationId, builder.Build());

            lock (ActiveChats)
            {
                ActiveChats.Add(username);

                // Сводное уведомление нужно только когда чатов два и больше:
                // иначе Android свернул бы группу и спрятал имя собеседника.
                if (ActiveChats.Count > 1)
                {
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
                }
                else
                {
                    compat.Cancel(SummaryNotificationId);
                }
            }

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
