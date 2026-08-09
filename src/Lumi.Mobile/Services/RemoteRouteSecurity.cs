using System.Net;
using System.Net.NetworkInformation;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile.Services;

public interface IRemoteRouteVerifier
{
    bool IsTrustedTailscaleRoute(IPAddress targetAddress);
}

public static class RemotePlatformServices
{
    public static IRemoteRouteVerifier RouteVerifier { get; set; } = new DefaultRemoteRouteVerifier();
}

public static class RemoteRouteSecurity
{
    public static void EnsureTrusted(Uri requestUri, IRemoteRouteVerifier routeVerifier)
    {
        var host = requestUri.Host.Trim('[', ']');
        if (!IPAddress.TryParse(host, out var address)
            || IPAddress.IsLoopback(address)
            || !RemoteProtocol.IsTailscaleAddress(address))
        {
            return;
        }

        if (!routeVerifier.IsTrustedTailscaleRoute(address))
        {
            throw new InvalidOperationException(
                "Tailscale is not connected for this PC address. Reconnect Tailscale and try again.");
        }
    }

    public static bool IsTrustedTailscaleTopology(
        IPAddress targetAddress,
        IEnumerable<IPAddress> localAddresses,
        IEnumerable<RemoteNetworkRoute> routes)
    {
        targetAddress = NormalizeAddress(targetAddress);
        if (!RemoteProtocol.IsTailscaleAddress(targetAddress)
            || !localAddresses.Any(static address =>
                RemoteProtocol.IsTailscaleAddress(NormalizeAddress(address))))
        {
            return false;
        }

        var minimumPrefixLength = targetAddress.AddressFamily
            == System.Net.Sockets.AddressFamily.InterNetwork
            ? 10
            : 48;
        return routes.Any(route =>
            route.PrefixLength >= minimumPrefixLength
            && RouteContainsAddress(route, targetAddress));
    }

    private static bool RouteContainsAddress(RemoteNetworkRoute route, IPAddress targetAddress)
    {
        var networkAddress = NormalizeAddress(route.NetworkAddress);
        targetAddress = NormalizeAddress(targetAddress);
        if (networkAddress.AddressFamily != targetAddress.AddressFamily)
            return false;

        var networkBytes = networkAddress.GetAddressBytes();
        var targetBytes = targetAddress.GetAddressBytes();
        var prefixLength = route.PrefixLength;
        if (prefixLength < 0 || prefixLength > networkBytes.Length * 8)
            return false;

        var wholeBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        for (var index = 0; index < wholeBytes; index++)
        {
            if (networkBytes[index] != targetBytes[index])
                return false;
        }

        if (remainingBits == 0)
            return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (networkBytes[wholeBytes] & mask) == (targetBytes[wholeBytes] & mask);
    }

    private static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

public readonly record struct RemoteNetworkRoute(IPAddress NetworkAddress, int PrefixLength);

internal sealed class DefaultRemoteRouteVerifier : IRemoteRouteVerifier
{
    public bool IsTrustedTailscaleRoute(IPAddress targetAddress)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(static network => network.OperationalStatus == OperationalStatus.Up)
                .SelectMany(static network => network.GetIPProperties().UnicastAddresses)
                .Any(address => RemoteProtocol.IsTailscaleAddress(address.Address));
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
