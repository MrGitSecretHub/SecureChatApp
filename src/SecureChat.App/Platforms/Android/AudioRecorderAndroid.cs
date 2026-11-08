#if ANDROID
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Android.Media;
using Android.OS;
using SecureChat.App.Services;

namespace SecureChat.App.Services;

/// <summary>Запись аудио на Android через MediaRecorder.</summary>
public sealed class AudioRecorderAndroid : IAudioRecorder
{
    private MediaRecorder? _recorder;
    private string? _outputPath;
    private bool _recording;

    public async Task<string?> RecordAsync(CancellationToken cancellationToken = default)
    {
        if (_recording)
        {
            return _outputPath;
        }

        // Освобождаем предыдущий объект, если он остался.
        _recorder?.Dispose();
        _recorder = null;

        var dir = Android.App.Application.Context.GetExternalFilesDir(Android.OS.Environment.DirectoryMusic)
            ?? Android.App.Application.Context.CacheDir;
        if (dir is null)
        {
            return null;
        }

        _outputPath = System.IO.Path.Combine(dir.AbsolutePath, $"voice_{Guid.NewGuid():N}.m4a");

        _recorder = new MediaRecorder();
        _recorder.SetAudioSource(AudioSource.Mic);
        _recorder.SetOutputFormat(OutputFormat.Mpeg4);
        _recorder.SetAudioEncoder(AudioEncoder.Aac);
        _recorder.SetOutputFile(_outputPath);

        try
        {
            _recorder.Prepare();
            _recorder.Start();
            _recording = true;
        }
        catch
        {
            _recorder?.Release();
            _recorder = null;
            throw;
        }

        await WaitForCancelAsync(cancellationToken);

        if (!_recording)
        {
            return null;
        }

        _recorder.Stop();
        _recorder.Release();
        _recorder = null;
        _recording = false;

        return _outputPath;
    }

    public Task CancelAsync()
    {
        _recording = false;
        try
        {
            _recorder?.Stop();
            _recorder?.Release();
        }
        catch
        {
        }
        _recorder = null;

        if (!string.IsNullOrEmpty(_outputPath) && System.IO.File.Exists(_outputPath))
        {
            try
            {
                System.IO.File.Delete(_outputPath);
            }
            catch
            {
            }
        }

        return Task.CompletedTask;
    }

    private static async Task WaitForCancelAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>();
        using var registration = cancellationToken.Register(() => tcs.TrySetResult(true));
        await tcs.Task;
    }
}
#endif
