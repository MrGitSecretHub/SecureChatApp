using System.Runtime.InteropServices;

namespace SecureChat.App.Services;

public sealed partial class SoundService
{
    private const uint MbIconAsterisk = 0x00000040;

    private const int SndAsync = 0x0001;
    private const int SndMemory = 0x0004;

    private static readonly object PlaySync = new();
    private static readonly Dictionary<string, byte[]?> Sounds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Встроенные тоны подписаны из файла Android raw-ресурса;
    /// манифест-имя — просто имя файла.</summary>
    private static byte[]? GetBuiltInSound(string ringtoneId)
    {
        lock (PlaySync)
        {
            if (Sounds.TryGetValue(ringtoneId, out var cached))
            {
                return cached;
            }

            byte[]? data = null;
            try
            {
                using var stream = typeof(SoundService).Assembly.GetManifestResourceStream(
                    $"ringtone_{ringtoneId}.wav");
                if (stream is not null)
                {
                    var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    data = buffer.Length > 0 ? buffer.ToArray() : null;
                }
            }
            catch
            {
                data = null;
            }

            Sounds[ringtoneId] = data;
            return data;
        }
    }

    private partial void PlayCore(string ringtoneId)
    {
        try
        {
            var fallback = new Action(() =>
            {
                try
                {
                    // Системный звук «Уведомление» из звуковой схемы Windows.
                    MessageBeep(MbIconAsterisk);
                }
                catch
                {
                    // Звук не критичен для доставки сообщений.
                }
            });

            if (string.IsNullOrEmpty(ringtoneId) || ringtoneId == Ringtones.DefaultId)
            {
                fallback();
                return;
            }

            var sound = GetBuiltInSound(ringtoneId);
            if (sound is null
                || !PlaySound(sound, nint.Zero, SndAsync | SndMemory))
            {
                fallback();
            }
        }
        catch
        {
            // Звук не критичен для доставки сообщений.
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint uType);

    [DllImport("winmm.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(byte[] pszSound, nint hmod, int fdwSound);
}
