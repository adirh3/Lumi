using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile.Services;

/// <summary>
/// Finds Lumi desktops on the local network by broadcasting a UDP probe and collecting beacons.
/// Deliberately fire-and-forget: discovery is a convenience, manual host entry always works.
/// </summary>
public sealed class LumiDiscoveryClient
{
    private readonly int _discoveryPort;

    public LumiDiscoveryClient(int discoveryPort = RemoteProtocol.DiscoveryPort)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(discoveryPort);
        _discoveryPort = discoveryPort;
    }

    /// <summary>
    /// Broadcasts a probe and gathers every desktop that answers within <paramref name="timeout"/>.
    /// Results are de-duplicated by instance id, newest answer wins.
    /// </summary>
    public async Task<IReadOnlyList<RemoteBeacon>> DiscoverAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, RemoteBeacon>(StringComparer.OrdinalIgnoreCase);

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.EnableBroadcast = true;
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var probe = Encoding.UTF8.GetBytes(RemoteProtocol.DiscoveryProbe);

        foreach (var target in BroadcastTargets())
        {
            try
            {
                await udp.SendAsync(probe, probe.Length, target).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // An interface that refuses broadcast is not fatal — keep probing the others.
            }
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            while (!deadline.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(result.Buffer);
                if (!text.StartsWith(RemoteProtocol.DiscoveryBeacon, StringComparison.Ordinal))
                    continue;

                var json = text[RemoteProtocol.DiscoveryBeacon.Length..];
                RemoteBeacon? beacon;
                try
                {
                    beacon = JsonSerializer.Deserialize(json, RemoteJsonContext.Default.RemoteBeacon);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (beacon is null || beacon.ProtocolVersion != RemoteProtocol.Version)
                    continue;

                // Trust the responder's socket address over whatever it claims, when it claims nothing useful.
                if (string.IsNullOrWhiteSpace(beacon.Address))
                    beacon.Address = result.RemoteEndPoint.Address.ToString();

                var key = beacon.InstanceId.Length > 0 ? beacon.InstanceId : beacon.BaseUrl;
                found[key] = beacon;
            }
        }
        catch (OperationCanceledException)
        {
            // Deadline reached — normal completion path.
        }
        catch (SocketException ex)
        {
            Trace.TraceWarning($"[Mobile] Discovery socket error: {ex.Message}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return found.Values.OrderBy(b => b.HostName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private IEnumerable<IPEndPoint> BroadcastTargets()
    {
        yield return new IPEndPoint(IPAddress.Broadcast, _discoveryPort);

        // Global broadcast is dropped by many Wi-Fi APs and by loopback-only test setups, so also
        // aim at each interface's directed broadcast address plus loopback.
        yield return new IPEndPoint(IPAddress.Loopback, _discoveryPort);

        foreach (var address in DirectedBroadcastAddresses())
            yield return new IPEndPoint(address, _discoveryPort);
    }

    private static IEnumerable<IPAddress> DirectedBroadcastAddresses()
    {
        List<IPAddress> results = [];

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (unicast.IPv4Mask is not { } mask)
                        continue;

                    var addressBytes = unicast.Address.GetAddressBytes();
                    var maskBytes = mask.GetAddressBytes();
                    if (maskBytes.Length != 4)
                        continue;

                    var broadcast = new byte[4];
                    for (var i = 0; i < 4; i++)
                        broadcast[i] = (byte)(addressBytes[i] | (byte)~maskBytes[i]);

                    results.Add(new IPAddress(broadcast));
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            // Some Android configurations deny interface enumeration; the global broadcast still applies.
        }

        return results;
    }
}
