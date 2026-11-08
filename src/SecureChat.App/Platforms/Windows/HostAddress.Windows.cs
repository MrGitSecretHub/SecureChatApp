using System.Net;
using System.Net.Sockets;

namespace SecureChat.App.Services;

public sealed partial class HostAddress
{
    public static partial string? TryGetHostAddress()
    {
        try
        {
            var addresses = Dns.GetHostEntry(Dns.GetHostName()).AddressList;
            return addresses
                .Where(item => item.AddressFamily == AddressFamily.InterNetwork)
                .Select(item => item.ToString())
                .FirstOrDefault(item => !item.StartsWith("127.", StringComparison.Ordinal));
        }
        catch
        {
            return null;
        }
    }
}
