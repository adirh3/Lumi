using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Remote.Protocol;

namespace Lumi.Services.Remote;

/// <summary>
/// Answers LAN discovery probes so a phone can find this Lumi without the user typing an IP.
/// </summary>
/// <remarks>
/// A tiny request/response beacon over UDP is used instead of mDNS/Bonjour: it needs no extra
/// dependency, no platform service, and works identically on Windows, Linux, macOS and Android.
/// The desktop only ever replies to a probe — it never broadcasts unsolicited — so an idle Lumi is
/// invisible on the network.
/// </remarks>
internal sealed class RemoteDiscoveryResponder : IDisposable
{
    private readonly string _instanceId;
    private readonly Func<int> _portProvider;
    private readonly Func<string> _userNameProvider;
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;
    private Task? _loop;

    public RemoteDiscoveryResponder(string instanceId, Func<int> portProvider, Func<string> userNameProvider)
    {
        _instanceId = instanceId;
        _portProvider = portProvider;
        _userNameProvider = userNameProvider;
    }

    public void Start()
    {
        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, RemoteProtocol.DiscoveryPort));
            udp.EnableBroadcast = true;
            _udp = udp;
            _loop = Task.Run(() => ListenAsync(udp, _cts.Token));
        }
        catch (SocketException ex)
        {
            // Another Lumi window already owns the discovery port, or the OS blocked the bind.
            // Manual address entry still works, so this is not fatal.
            Trace.TraceInformation($"[Remote] Discovery responder unavailable: {ex.Message}");
        }
    }

    private async Task ListenAsync(UdpClient udp, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            try
            {
                var probe = Encoding.UTF8.GetString(result.Buffer);
                if (!probe.StartsWith(RemoteProtocol.DiscoveryProbe, StringComparison.Ordinal))
                    continue;

                if (!LumiRemoteServer.IsPrivateCaller(result.RemoteEndPoint))
                    continue;

                var payload = BuildBeacon(result.RemoteEndPoint.Address);
                await udp.SendAsync(payload, payload.Length, result.RemoteEndPoint).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // Transient network hiccup; keep listening.
            }
        }
    }

    private byte[] BuildBeacon(IPAddress requester)
    {
        var beacon = new RemoteBeacon
        {
            InstanceId = _instanceId,
            HostName = Environment.MachineName,
            UserName = _userNameProvider(),
            Address = PickAdvertisedAddress(requester),
            Port = _portProvider()
        };

        var json = JsonSerializer.Serialize(beacon, RemoteJsonContext.Default.RemoteBeacon);
        return Encoding.UTF8.GetBytes(RemoteProtocol.DiscoveryBeacon + json);
    }

    /// <summary>
    /// Picks the local address that shares the longest prefix with the requester, so a machine with
    /// several adapters (Wi-Fi, Ethernet, VM bridges, VPN) advertises the one the phone can reach.
    /// </summary>
    private static string PickAdvertisedAddress(IPAddress requester)
    {
        var candidates = LumiRemoteServer.GetLocalAddresses();
        if (candidates.Count == 0)
            return IPAddress.Loopback.ToString();

        var requesterBytes = requester.AddressFamily == AddressFamily.InterNetwork
            ? requester.GetAddressBytes()
            : null;

        if (requesterBytes is null)
            return candidates[0];

        return candidates
            .OrderByDescending(candidate => SharedPrefixLength(IPAddress.Parse(candidate).GetAddressBytes(), requesterBytes))
            .First();
    }

    private static int SharedPrefixLength(byte[] left, byte[] right)
    {
        var shared = 0;
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            if (left[i] != right[i])
                break;
            shared++;
        }

        return shared;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
        _cts.Dispose();
    }
}
