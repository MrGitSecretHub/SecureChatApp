using Android.Content;
using Android.Net;

namespace SecureChat.App.Services;

public sealed partial class HostAddress
{
    public static partial string? TryGetHostAddress()
    {
        try
        {
            using var connectivity =
                (ConnectivityManager?)Android.App.Application.Context.GetSystemService(
                    Context.ConnectivityService);

            var network = connectivity?.ActiveNetwork;
            if (network is null)
            {
                return null;
            }

            using var properties = connectivity!.GetLinkProperties(network);
            var routes = properties?.Routes;
            if (routes is null)
            {
                return null;
            }

            // Сначала ищем маршрут по умолчанию, затем — любой с шлюзом.
            var route = routes.FirstOrDefault(item => item.IsDefaultRoute) ??
                routes.FirstOrDefault(item => item.Gateway is not null);

            var gateway = route?.Gateway;
            if (gateway is null)
            {
                return null;
            }

            var text = gateway.HostAddress;
            if (string.IsNullOrWhiteSpace(text) ||
                text == "0.0.0.0" ||
                text.StartsWith("127.", StringComparison.Ordinal))
            {
                return null;
            }

            return text;
        }
        catch
        {
            return null;
        }
    }
}
