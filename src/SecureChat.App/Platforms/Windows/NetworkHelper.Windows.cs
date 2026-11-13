using System.Net.NetworkInformation;

namespace SecureChat.App.Services;

public sealed partial class NetworkHelper
{
    private partial bool IsWifiCore()
    {
        try
        {
            // Ethernet на ПК считаем безлимитным, как и Wi-Fi.
            return NetworkInterface.GetAllNetworkInterfaces()
                .Any(item => item.OperationalStatus == OperationalStatus.Up &&
                    item.NetworkInterfaceType is NetworkInterfaceType.Wireless80211
                        or NetworkInterfaceType.Ethernet
                        or NetworkInterfaceType.Loopback);
        }
        catch
        {
            return false;
        }
    }
}
