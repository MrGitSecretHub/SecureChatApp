#if ANDROID
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Android.Graphics;
using Android.Media;
using Android.OS;
using Android.Views;
using SecureChat.App.Services;

namespace SecureChat.App.Services;

/// <summary>Встроенная запись видео на Android через MediaRecorder (без внешнего приложения камеры).</summary>
public sealed class VideoRecorderAndroid : IVideoRecorder
{
    private MediaRecorder? _recorder;
    private SurfaceTexture? _surfaceTexture;
    private Surface? _surface;
    private string? _outputPath;
    private bool _recording;

    public async Task<string?> RecordAsync(CancellationToken cancellationToken = default)
    {
        if (_recording)
        {
            return _outputPath;
        }

        var dir = Android.App.Application.Context.GetExternalFilesDir(Android.OS.Environment.DirectoryMovies)
            ?? Android.App.Application.Context.CacheDir;
        if (dir is null)
        {
            return null;
        }

        _outputPath = System.IO.Path.Combine(dir.AbsolutePath, $"circle_{Guid.NewGuid():N}.mp4");

        _surfaceTexture = new SurfaceTexture(0);
        _surface = new Surface(_surfaceTexture);

        _recorder = new MediaRecorder();
        _recorder.SetVideoSource(VideoSource.Camera);
        _recorder.SetAudioSource(AudioSource.Mic);
        _recorder.SetOutputFormat(OutputFormat.Mpeg4);
        _recorder.SetVideoEncoder(VideoEncoder.H264);
        _recorder.SetAudioEncoder(AudioEncoder.Aac);
        _recorder.SetOutputFile(_outputPath);
        _recorder.SetVideoSize(640, 480);
        _recorder.SetVideoFrameRate(30);
        _recorder.SetPreviewDisplay(_surface);

        try
        {
            _recorder.Prepare();
            _recorder.Start();
            _recording = true;
        }
        catch
        {
            _surface?.Dispose();
            _surfaceTexture?.Release();
            _surfaceTexture = null;
            _surface = null;
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
        _surface?.Release();
        _surfaceTexture?.Release();
        _recorder = null;
        _surface = null;
        _surfaceTexture = null;
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

        try
        {
            _surface?.Release();
            _surfaceTexture?.Release();
        }
        catch
        {
        }
        _surface = null;
        _surfaceTexture = null;

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
