using System.Net.NetworkInformation;

namespace Lumi.Mobile.Services;

/// <summary>
/// Tells the connection supervisor when the phone's network appears or changes, so a stalled or
/// failed link is retried at once instead of on a timer.
/// </summary>
public interface IRemoteNetworkMonitor
{
    bool IsNetworkAvailable { get; }

    /// <summary>Raised when connectivity is gained, lost, or moves to another network or VPN.</summary>
    event Action? NetworkChanged;
}

/// <summary>A monitor for hosts that cannot observe the network: always online, never changes.</summary>
public sealed class StaticNetworkMonitor : IRemoteNetworkMonitor
{
    public static StaticNetworkMonitor Available { get; } = new();

    public bool IsNetworkAvailable => true;

    public event Action? NetworkChanged
    {
        add { }
        remove { }
    }
}

/// <summary>A monitor whose state is pushed in by the host (browser online/offline events, tests).</summary>
public sealed class PushedNetworkMonitor : IRemoteNetworkMonitor
{
    private volatile bool _isNetworkAvailable = true;

    public bool IsNetworkAvailable => _isNetworkAvailable;

    public event Action? NetworkChanged;

    public void Report(bool isNetworkAvailable)
    {
        _isNetworkAvailable = isNetworkAvailable;
        NetworkChanged?.Invoke();
    }
}

/// <summary>
/// Desktop/simulator monitor over <see cref="NetworkChange"/>. Android does not allow apps to watch
/// routing sockets, so the Android host installs its ConnectivityManager monitor instead.
/// </summary>
public sealed class SystemNetworkMonitor : IRemoteNetworkMonitor
{
    private static readonly Lazy<IRemoteNetworkMonitor> Shared = new(Create);

    private SystemNetworkMonitor()
    {
        NetworkChange.NetworkAvailabilityChanged += (_, _) => NetworkChanged?.Invoke();
        NetworkChange.NetworkAddressChanged += (_, _) => NetworkChanged?.Invoke();
    }

    public static IRemoteNetworkMonitor Instance => Shared.Value;

    public bool IsNetworkAvailable
    {
        get
        {
            try
            {
                return NetworkInterface.GetIsNetworkAvailable();
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
                return true;
            }
        }
    }

    public event Action? NetworkChanged;

    private static IRemoteNetworkMonitor Create()
    {
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsBrowser() || OperatingSystem.IsWasi())
            return StaticNetworkMonitor.Available;

        try
        {
            return new SystemNetworkMonitor();
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return StaticNetworkMonitor.Available;
        }
    }
}
