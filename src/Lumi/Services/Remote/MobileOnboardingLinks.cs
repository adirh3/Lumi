using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Lumi.Remote.Protocol;

namespace Lumi.Services.Remote;

internal enum MobileOnboardingTransport
{
    Tailscale,
    LocalNetwork,
    DevTunnel
}

internal sealed record MobileOnboardingEndpoint(
    string BaseUrl,
    MobileOnboardingTransport Transport);

internal static class MobileOnboardingLinks
{
    public static MobileOnboardingEndpoint? SelectEndpoint(
        IReadOnlyList<string> addresses,
        MobileOnboardingTransport transport,
        IReadOnlySet<IPAddress>? preferredLocalAddresses = null)
    {
        if (transport == MobileOnboardingTransport.DevTunnel)
        {
            var origin = addresses.FirstOrDefault(static address =>
                Uri.TryCreate(address, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && uri.IsDefaultPort
                && uri.UserInfo.Length == 0
                && uri.AbsolutePath == "/"
                && uri.Query.Length == 0
                && uri.Fragment.Length == 0
                && uri.IdnHost.EndsWith(".devtunnels.ms", StringComparison.Ordinal));
            return origin is null ? null : new MobileOnboardingEndpoint(origin, transport);
        }

        var parsed = addresses
            .Select(static address =>
                Uri.TryCreate(address, UriKind.Absolute, out var uri)
                    ? uri
                    : null)
            .Where(static uri => uri is not null)
            .Select(static uri => uri!)
            .Where(static uri =>
                IPAddress.TryParse(uri.Host.Trim('[', ']'), out _))
            .DistinctBy(static uri => uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (transport == MobileOnboardingTransport.Tailscale)
        {
            var tailscale = parsed.FirstOrDefault(static uri =>
                IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address)
                && RemoteProtocol.IsTailscaleAddress(address));
            return tailscale is null
                ? null
                : new MobileOnboardingEndpoint(
                    tailscale.GetLeftPart(UriPartial.Authority),
                    MobileOnboardingTransport.Tailscale);
        }

        var localAddress = SelectLocalAddress(
            parsed.Select(static uri => IPAddress.Parse(uri.Host.Trim('[', ']'))),
            preferredLocalAddresses);
        if (localAddress is null)
            return null;

        var local = parsed.First(uri =>
            IPAddress.Parse(uri.Host.Trim('[', ']')).Equals(localAddress));
        return new MobileOnboardingEndpoint(
            local.GetLeftPart(UriPartial.Authority),
            MobileOnboardingTransport.LocalNetwork);
    }

    public static string BuildWebAppUrl(string baseUrl) =>
        $"{baseUrl.TrimEnd('/')}/app/";

    public static string BuildAndroidInstallUrl(
        string baseUrl,
        string version) =>
        $"{baseUrl.TrimEnd('/')}/app/android.html?" +
        $"version={Uri.EscapeDataString(version)}";

    internal static IPAddress? SelectLocalAddress(
        IEnumerable<IPAddress> addresses,
        IReadOnlySet<IPAddress>? preferredLocalAddresses = null)
    {
        preferredLocalAddresses ??= GetGatewayBackedLocalAddresses();
        return addresses
            .Where(static address =>
                address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(address)
                && !RemoteProtocol.IsTailscaleAddress(address))
            .OrderBy(address => preferredLocalAddresses.Contains(address) ? 0 : 1)
            .ThenBy(RankLocalAddress)
            .FirstOrDefault();
    }

    private static IReadOnlySet<IPAddress> GetGatewayBackedLocalAddresses()
    {
        var addresses = new HashSet<IPAddress>();
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up
                    || network.NetworkInterfaceType is
                        NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var properties = network.GetIPProperties();
                var hasGateway = properties.GatewayAddresses.Any(static gateway =>
                    gateway.Address.AddressFamily == AddressFamily.InterNetwork
                    && !gateway.Address.Equals(IPAddress.Any)
                    && !IPAddress.IsLoopback(gateway.Address));
                if (!hasGateway)
                    continue;

                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(unicast.Address))
                    {
                        addresses.Add(unicast.Address);
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }

        return addresses;
    }

    private static int RankLocalAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return int.MaxValue;

        var bytes = address.GetAddressBytes();
        if (bytes[0] == 192 && bytes[1] == 168)
            return 0;
        if (bytes[0] == 10)
            return 1;
        if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            return 2;
        return 3;
    }
}
