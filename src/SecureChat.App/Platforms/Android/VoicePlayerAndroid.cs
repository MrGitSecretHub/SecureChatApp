#if ANDROID
using System;
using System.Threading.Tasks;

namespace SecureChat.App.Services;

/// <summary>Воспроизведение голосовых на Android через системный MediaPlayer.</summary>
public sealed class VoicePlayerAndroid : IVoicePlayer
{
    private Android.Media.MediaPlayer? _player;

    public bool IsPlaying => _player?.IsPlaying ?? false;
    public double PositionSeconds => _player?.CurrentPosition / 1000.0 ?? 0;
    public double DurationSeconds => _player?.Duration / 1000.0 ?? 0;

    public event EventHandler? Completed;

    public Task<bool> OpenAsync(string path)
    {
        var player = new Android.Media.MediaPlayer();
        try
        {
            player.SetDataSource(path);
            player.Prepare();
        }
        catch
        {
            try
            {
                player.Release();
            }
            catch
            {
            }

            throw;
        }

        player.Completion += (_, _) => Completed?.Invoke(this, EventArgs.Empty);
        _player = player;
        return Task.FromResult(true);
    }

    public void Play() => _player?.Start();

    public void Pause() => _player?.Pause();

    public void SeekSeconds(double seconds)
    {
        if (_player is null)
        {
            return;
        }

        var ms = (int)Math.Clamp(seconds * 1000.0, 0, Math.Max(_player.Duration, 0));
        _player.SeekTo(ms);
    }

    public void Release()
    {
        var player = _player;
        _player = null;
        if (player is null)
        {
            return;
        }

        try
        {
            player.Stop();
        }
        catch
        {
        }

        try
        {
            player.Release();
        }
        catch
        {
        }
    }
}
#endif
