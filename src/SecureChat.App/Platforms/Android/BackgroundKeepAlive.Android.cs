using Android.Content;
using Android.Net;
using Android.Net.Wifi;
using Android.OS;

namespace SecureChat.App.Services;

/// <summary>Android-реализация: partial wake lock + Wi-Fi lock.</summary>
public sealed partial class BackgroundKeepAlive
{
    private static readonly object Sync = new();
    private static PowerManager.WakeLock? _wakeLock;
    private static WifiManager.WifiLock? _wifiLock;

    private static partial void AcquireCore()
    {
        lock (Sync)
        {
            if (_wakeLock is not null)
            {
                return;
            }

            var context = Android.App.Application.Context;

            try
            {
                var power = (PowerManager?)context.GetSystemService(Context.PowerService);
                _wakeLock = power?.NewWakeLock(WakeLockFlags.Partial, "SecureChat:background");
                _wakeLock?.SetReferenceCounted(false);
                _wakeLock?.Acquire();
            }
            catch
            {
                _wakeLock = null;
            }

            try
            {
                var wifi = (WifiManager?)context.GetSystemService(Context.WifiService);
                // API 34+: FullLowLatency; ранее — FullHighPerf (deprecated,
                // но работает и включает high-perf режим Wi-Fi).
                var mode = (int)Build.VERSION.SdkInt >= 34
                    ? WifiMode.FullLowLatency
                    : WifiMode.FullHighPerf;

                _wifiLock = wifi?.CreateWifiLock(mode, "SecureChat:background-wifi");
                _wifiLock?.SetReferenceCounted(false);
                _wifiLock?.Acquire();
            }
            catch
            {
                _wifiLock = null;
            }
        }
    }

    private static partial void ReleaseCore()
    {
        lock (Sync)
        {
            try
            {
                if (_wakeLock?.IsHeld == true)
                {
                    _wakeLock.Release();
                }
            }
            catch
            {
                // Освобождение не критично.
            }

            _wakeLock = null;

            try
            {
                if (_wifiLock?.IsHeld == true)
                {
                    _wifiLock.Release();
                }
            }
            catch
            {
                // Освобождение не критично.
            }

            _wifiLock = null;
        }
    }
}
