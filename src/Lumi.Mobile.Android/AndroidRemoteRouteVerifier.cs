using System.Net;
using Android.Content;
using Android.Net;
using Lumi.Mobile.Services;

namespace Lumi.Mobile.Android;

/// <summary>
/// Uses Android's public VPN topology APIs to require both a Tailscale local address and a
/// Tailscale-specific route to the requested peer. Android does not expose another app's VPN owner
/// UID to ordinary applications, so an owner-package check would reject legitimate Tailscale use.
/// </summary>
internal sealed class AndroidRemoteRouteVerifier(Context context) : IRemoteRouteVerifier
{
    public bool IsTrustedTailscaleRoute(IPAddress targetAddress)
    {
        var connectivity = context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;
        if (connectivity is null)
            return false;

        var network = connectivity.ActiveNetwork;
        var capabilities = network is null ? null : connectivity.GetNetworkCapabilities(network);
        if (network is null
            || capabilities?.HasTransport(TransportType.Vpn) != true)
        {
            return false;
        }

        var properties = connectivity.GetLinkProperties(network);
        if (properties is null)
            return false;

        var localAddresses = new List<IPAddress>();
        foreach (var linkAddress in properties.LinkAddresses)
        {
            var host = linkAddress.Address?.HostAddress?.Split('%')[0];
            if (IPAddress.TryParse(host, out var localAddress))
                localAddresses.Add(localAddress);
        }

        var routes = new List<RemoteNetworkRoute>();
        foreach (var route in properties.Routes)
        {
            var destination = route.Destination;
            var host = destination?.Address?.HostAddress?.Split('%')[0];
            if (destination is not null && IPAddress.TryParse(host, out var networkAddress))
                routes.Add(new RemoteNetworkRoute(networkAddress, destination.PrefixLength));
        }

        return RemoteRouteSecurity.IsTrustedTailscaleTopology(
            targetAddress,
            localAddresses,
            routes);
    }
}
