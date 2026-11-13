using Android.Content;
using Android.Net;

namespace SecureChat.App.Services;

public sealed partial class NetworkHelper
{
    private partial bool IsWifiCore()
    {
        try
        {
            var connectivity = (ConnectivityManager?)Android.App.Application.Context
                .GetSystemService(Context.ConnectivityService);
            if (connectivity is null)
            {
                return false;
            }

            var info = connectivity.ActiveNetworkInfo;
            if (info is null || !info.IsConnected)
            {
                return false;
            }

            // Wi-Fi или Ethernet — безлимитный трафик.
            return info.Type is ConnectivityType.Wifi or ConnectivityType.Ethernet;
        }
        catch
        {
            return false;
        }
    }
}
