using Android.Media;

namespace SecureChat.App.Services;

public sealed partial class SoundService
{
    // Держим ссылку: если объект соберёт GC, мелодия оборвётся на полуслове.
    private static Ringtone? _ringtone;
    private static MediaPlayer? _player;

    /// <summary>Системный рингтон уведомлений (настройки ОС).</summary>
    private static void PlaySystemRingtone()
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

    private partial void PlayCore(string ringtoneId)
    {
        try
        {
            if (string.IsNullOrEmpty(ringtoneId) || ringtoneId == Ringtones.DefaultId)
            {
                PlaySystemRingtone();
                return;
            }

            var context = Android.App.Application.Context;
            var resourceId = context.Resources?.GetIdentifier(
                $"ringtone_{ringtoneId}", "raw", context.PackageName);
            if (resourceId is null or 0)
            {
                PlaySystemRingtone();
                return;
            }

            // Останавливаем предыдущий тон раз и навсегда: MediaPlayer нельзя
            // переиспользовать для другого источника без release.
            _player?.Stop();
            _player?.Release();
            _player = null;

            var descriptor = context.Resources!.OpenRawResourceFd(resourceId.Value);
            if (descriptor is null)
            {
                PlaySystemRingtone();
                return;
            }

            var player = new MediaPlayer();
            player.SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Notification)
                .SetContentType(AudioContentType.Sonification)
                .Build()!);
            player.SetDataSource(
                descriptor.FileDescriptor!,
                descriptor.StartOffset,
                descriptor.Length);
            descriptor.Close();
            player.Prepare();
            player.Completion += OnPlaybackCompleted;

            _player = player;
            player.Start();
        }
        catch (Exception)
        {
            try
            {
                _player?.Release();
            }
            finally
            {
                _player = null;
            }

            try
            {
                PlaySystemRingtone();
            }
            catch
            {
                // Звук не критичен для доставки сообщений.
            }
        }
    }

    private static void OnPlaybackCompleted(object? sender, EventArgs e)
    {
        if (sender is not MediaPlayer player || !ReferenceEquals(player, _player))
        {
            return;
        }

        player.Release();
        _player = null;
    }
}
