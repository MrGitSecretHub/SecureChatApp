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

/// <summary>Запись аудио на Windows через MediaCapture.</summary>
public sealed class AudioRecorderWindows : IAudioRecorder
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

        _capture?.Dispose();
        _capture = new MediaCapture();
        var settings = new MediaCaptureInitializationSettings
        {
            StreamingCaptureMode = StreamingCaptureMode.Audio,
            MediaCategory = MediaCategory.Speech,
            SharingMode = MediaCaptureSharingMode.SharedReadOnly
        };

        try
        {
            // Для аудио-only записи не нужно начинать превью видео устройства.
            await _capture.InitializeAsync(settings).AsTask(cancellationToken);
        }
        catch (Exception ex)
        {
            _capture.Dispose();
            _capture = null;
            throw new InvalidOperationException("Микрофон недоступен или права на запись аудио не предоставлены.", ex);
        }

        var dir = Path.Combine(Path.GetTempPath(), "SecureChat");
        Directory.CreateDirectory(dir);
        var voicePath = Path.Combine(dir, $"voice_{Guid.NewGuid():N}.m4a");
        await using (var stream = File.Create(voicePath))
        {
        }

        _outputFile = await StorageFile.GetFileFromPathAsync(voicePath).AsTask(cancellationToken);

        var profile = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Auto);
        try
        {
            await _capture.StartRecordToStorageFileAsync(profile, _outputFile).AsTask(cancellationToken);
            _recording = true;
        }
        catch (Exception ex)
        {
            _capture.Dispose();
            _capture = null;
            throw new InvalidOperationException("Не удалось начать запись аудио.", ex);
        }

        try
        {
            await WaitForCancelAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }

        if (!_recording)
        {
            return null;
        }

        try
        {
            await _capture.StopRecordAsync();
        }
        catch
        {
        }
        try
        {
            await _capture.StopPreviewAsync();
        }
        catch
        {
        }
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
