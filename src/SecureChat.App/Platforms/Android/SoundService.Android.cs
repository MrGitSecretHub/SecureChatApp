using Android.Media;

namespace SecureChat.App.Services;

public sealed partial class SoundService
{
    // Держим ссылку: если объект соберёт GC, мелодия оборвётся на полуслове.
    private static Ringtone? _ringtone;

    private partial void PlayNotificationCore()
    {
        try
        {
            var context = Android.App.Application.Context;
            var uri = RingtoneManager.GetDefaultUri(RingtoneType.Notification);
            if (uri is null)
            {
                return;
            }

            var ringtone = RingtoneManager.GetRingtone(context, uri);
            if (ringtone is null)
            {
                return;
            }

            ringtone.AudioAttributes = new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Notification)!
                .SetContentType(AudioContentType.Sonification)!
                .Build()!;

            _ringtone?.Stop();
            _ringtone = ringtone;
            ringtone.Play();
        }
        catch
        {
            // Звук не критичен для доставки сообщений.
        }
    }
}
