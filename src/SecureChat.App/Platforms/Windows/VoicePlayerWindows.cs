#if WINDOWS
using System;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace SecureChat.App.Services;

/// <summary>Воспроизведение голосовых на Windows через WinRT MediaPlayer.</summary>
public sealed class VoicePlayerWindows : IVoicePlayer
{
    private Windows.Media.Playback.MediaPlayer? _player;

    public bool IsPlaying => _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
    public double PositionSeconds => _player?.PlaybackSession.Position.TotalSeconds ?? 0;
    public double DurationSeconds => _player?.PlaybackSession.NaturalDuration.TotalSeconds ?? 0;

    public event EventHandler? Completed;

    public async Task<bool> OpenAsync(string path)
    {
        var player = new Windows.Media.Playback.MediaPlayer
        {
            AutoPlay = false
        };

        var opened = new TaskCompletionSource();
        void OnOpened(MediaPlayer sender, object args) => opened.TrySetResult();
        void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
            opened.TrySetException(new InvalidOperationException(
                string.IsNullOrWhiteSpace(args.ErrorMessage)
                    ? "Windows не смогла открыть аудиофайл."
                    : args.ErrorMessage));

        player.MediaOpened += OnOpened;
        player.MediaFailed += OnFailed;

        try
        {
            player.Source = MediaSource.CreateFromUri(new Uri(path));
        }
        catch
        {
            player.MediaOpened -= OnOpened;
            player.MediaFailed -= OnFailed;
            player.Dispose();
            throw;
        }

        try
        {
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
            // Длительность подтянется через инлайн-таймер (250 мс).
        }
        finally
        {
            player.MediaOpened -= OnOpened;
            player.MediaFailed -= OnFailed;
        }

        player.MediaEnded += (_, _) => Completed?.Invoke(this, EventArgs.Empty);
        player.MediaFailed += (_, _) => Completed?.Invoke(this, EventArgs.Empty);

        _player = player;
        return true;
    }

    public void Play()
    {
        if (_player is not null &&
            _player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
        {
            _player.Play();
        }
    }

    public void Pause()
    {
        if (_player is not null &&
            _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _player.Pause();
        }
    }

    public void SeekSeconds(double seconds)
    {
        if (_player?.PlaybackSession is { } session && session.CanSeek)
        {
            var max = session.NaturalDuration;
            var target = max > TimeSpan.Zero
                ? TimeSpan.FromSeconds(Math.Clamp(seconds, 0, max.TotalSeconds))
                : TimeSpan.FromSeconds(Math.Max(0, seconds));

            session.Position = target;
        }
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
            player.Dispose();
        }
        catch
        {
        }
    }
}
#endif
