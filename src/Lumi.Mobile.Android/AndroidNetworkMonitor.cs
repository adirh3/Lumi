using Android.Content;
using Android.Net;
using Android.Runtime;
using Lumi.Mobile.Services;

namespace Lumi.Mobile.Android;

/// <summary>
/// Reports default-network changes (Wi-Fi, mobile data, Tailscale coming up or going down) so the
/// connection supervisor retries as soon as Lumi can be reachable again instead of on a timer.
/// Capability updates are filtered to VPN transport changes; signal-strength churn is ignored.
/// </summary>
// Packaged builds need a public registered Java peer for callbacks the platform invokes.
[Register("com/lumi/mobile/AndroidNetworkMonitor")]
public sealed class AndroidNetworkMonitor : ConnectivityManager.NetworkCallback, IRemoteNetworkMonitor
{
    private const string Tag = "LumiNetwork";
    private readonly ConnectivityManager? _connectivity;
    private volatile bool _isNetworkAvailable = true;
    // Callbacks arrive in order on one connectivity thread. A switch to a new default network is
    // reported through OnAvailable, so only losing the current default leaves the phone offline.
    private Network? _defaultNetwork;
    private int _hasVpn = -1;

    public AndroidNetworkMonitor(Context context)
    {
        _connectivity = context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;
        if (_connectivity is null)
            return;

        _isNetworkAvailable = _connectivity.ActiveNetwork is not null;
        try
        {
            _connectivity.RegisterDefaultNetworkCallback(this);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, $"Network callback unavailable: {ex.Message}");
        }
    }

    public bool IsNetworkAvailable => _isNetworkAvailable;

    public event Action? NetworkChanged;

    public override void OnAvailable(Network network)
    {
        base.OnAvailable(network);
        _defaultNetwork = network;
        _isNetworkAvailable = true;
        Raise("available");
    }

    public override void OnLost(Network network)
    {
        base.OnLost(network);
        // ActiveNetwork can still name the network being torn down here, so it is not consulted.
        if (_defaultNetwork is { } current && !current.Equals(network))
            return;

        _defaultNetwork = null;
        _isNetworkAvailable = false;
        Raise("lost");
    }

    public override void OnCapabilitiesChanged(Network network, NetworkCapabilities networkCapabilities)
    {
        base.OnCapabilitiesChanged(network, networkCapabilities);
        var hasVpn = networkCapabilities.HasTransport(TransportType.Vpn) ? 1 : 0;
        if (Interlocked.Exchange(ref _hasVpn, hasVpn) == hasVpn)
            return;

        Raise(hasVpn == 1 ? "VPN on" : "VPN off");
    }

    private void Raise(string change)
    {
        global::Android.Util.Log.Info(Tag, $"Default network {change}");
        try
        {
            NetworkChanged?.Invoke();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, $"Network change handler failed: {ex}");
        }
    }
}
