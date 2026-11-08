#if WINDOWS
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;
using SecureChat.App.Services;

namespace SecureChat.App.Services;

/// <summary>Встроенная запись видео на Windows через MediaCapture (без внешнего приложения камеры).</summary>
public sealed class VideoRecorderWindows : IVideoRecorder
{
    private MediaCapture? _capture;
    private StorageFile? _outputFile;
    private bool _recording;

    public async Task<string?> RecordAsync(CancellationToken cancellationToken = default)
    {
        if (_recording)
        {
            return _outputFile?.Path;
        }

        _capture = new MediaCapture();
        var settings = new MediaCaptureInitializationSettings
        {
            StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo,
            MediaCategory = MediaCategory.Speech
        };

        try
        {
            await _capture.InitializeAsync(settings).AsTask(cancellationToken);
        }
        catch
        {
            _capture.Dispose();
            _capture = null;
            throw;
        }

        var folder = ApplicationData.Current.TemporaryFolder;
        _outputFile = await folder.CreateFileAsync($"circle_{Guid.NewGuid():N}.mp4", CreationCollisionOption.GenerateUniqueName);

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        try
        {
            await _capture.StartRecordToStorageFileAsync(profile, _outputFile).AsTask(cancellationToken);
            _recording = true;
        }
        catch
        {
            _capture.Dispose();
            _capture = null;
            throw;
        }

        await WaitForCancelAsync(cancellationToken);

        if (!_recording)
        {
            return null;
        }

        await _capture.StopRecordAsync().AsTask(cancellationToken);
        await _capture.StopPreviewAsync().AsTask(cancellationToken);
        _capture.Dispose();
        _capture = null;
        _recording = false;

        return _outputFile.Path;
    }

    public Task CancelAsync()
    {
        _recording = false;
        try
        {
            _capture?.StopRecordAsync().AsTask().Wait(1000);
            _capture?.StopPreviewAsync().AsTask().Wait(1000);
            _capture?.Dispose();
        }
        catch
        {
        }
        _capture = null;

        if (_outputFile != null)
        {
            try
            {
                _outputFile.DeleteAsync().AsTask().Wait(1000);
            }
            catch
            {
            }
            _outputFile = null;
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
