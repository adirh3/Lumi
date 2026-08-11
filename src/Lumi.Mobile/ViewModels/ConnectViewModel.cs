using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Mobile.Services;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile.ViewModels;

public sealed partial class DiscoveredHostViewModel : ObservableObject
{
    public required string HostName { get; init; }

    public required string UserName { get; init; }

    public required string BaseUrl { get; init; }

    public string Subtitle => string.IsNullOrWhiteSpace(UserName) ? BaseUrl : $"{UserName} · {BaseUrl}";
}

/// <summary>Steps of the onboarding flow, in order.</summary>
public enum ConnectStep
{
    FindPc,
    EnterCode,
    Connecting
}

/// <summary>
/// Discovery → pairing → connected. Kept deliberately linear: one decision per screen so the whole
/// flow works one-handed on a compact phone.
/// </summary>
public sealed partial class ConnectViewModel : ObservableObject
{
    private readonly LumiRemoteClient _client;
    private readonly LumiDiscoveryClient _discovery;
    private readonly Func<string, string, Task> _onPaired;
    private CancellationTokenSource? _searchCts;

    [ObservableProperty] private ConnectStep _step = ConnectStep.FindPc;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _manualAddress = "";
    [ObservableProperty] private string _pairingCode = "";
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private DiscoveredHostViewModel? _selectedHost;
    [ObservableProperty] private string _targetHostName = "";
    [ObservableProperty] private bool _allowInsecureLanDiscovery;

    public ConnectViewModel(
        LumiRemoteClient client,
        LumiDiscoveryClient discovery,
        Func<string, string, Task> onPaired)
    {
        _client = client;
        _discovery = discovery;
        _onPaired = onPaired;
    }

    public ObservableCollection<DiscoveredHostViewModel> Hosts { get; } = [];

    public bool HasHosts => Hosts.Count > 0;

    public bool IsFindStep => Step == ConnectStep.FindPc;

    public bool IsCodeStep => Step == ConnectStep.EnterCode;

    public bool IsBusy => Step == ConnectStep.Connecting;

    public bool CanSubmitCode => PairingCode.Length == 6;

    private string _targetBaseUrl = "";

    partial void OnStepChanged(ConnectStep value)
    {
        OnPropertyChanged(nameof(IsFindStep));
        OnPropertyChanged(nameof(IsCodeStep));
        OnPropertyChanged(nameof(IsBusy));
    }

    partial void OnPairingCodeChanged(string value)
    {
        var digits = new string(value
            .Where(static character => character is >= '0' and <= '9')
            .Take(6)
            .ToArray());
        if (!string.Equals(digits, value, StringComparison.Ordinal))
        {
            PairingCode = digits;
            return;
        }

        OnPropertyChanged(nameof(CanSubmitCode));
    }

    partial void OnAllowInsecureLanDiscoveryChanged(bool value)
    {
        if (value)
            return;

        _searchCts?.Cancel();
        Hosts.Clear();
        StatusText = null;
        OnPropertyChanged(nameof(HasHosts));
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (IsSearching)
            return;
        if (!AllowInsecureLanDiscovery)
        {
            ErrorText = "Confirm trusted-network access before searching your LAN.";
            return;
        }

        IsSearching = true;
        ErrorText = null;
        StatusText = "Looking for Lumi on your network…";
        var searchCts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _searchCts, searchCts);
        previous?.Cancel();
        previous?.Dispose();

        try
        {
            var found = await _discovery.DiscoverAsync(
                TimeSpan.FromSeconds(2),
                searchCts.Token);
            if (!AllowInsecureLanDiscovery)
                return;

            Hosts.Clear();
            foreach (var beacon in found)
            {
                Hosts.Add(new DiscoveredHostViewModel
                {
                    HostName = beacon.HostName,
                    UserName = beacon.UserName,
                    BaseUrl = beacon.BaseUrl
                });
            }

            StatusText = Hosts.Count == 0
                ? "No Lumi found. Make sure Lumi is open on your PC with phone access turned on, or type its address below."
                : null;
        }
        catch (OperationCanceledException) when (searchCts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _searchCts, null, searchCts), searchCts))
                searchCts.Dispose();
            IsSearching = false;
            OnPropertyChanged(nameof(HasHosts));
        }
    }

    [RelayCommand]
    private Task ChooseHostAsync(DiscoveredHostViewModel? host)
    {
        if (host is null)
            return Task.CompletedTask;
        if (!AllowInsecureLanDiscovery && RequiresTrustedAddressConfirmation(host.BaseUrl))
        {
            ErrorText = "Local-network connections are unencrypted. Enable the LAN option before continuing.";
            return Task.CompletedTask;
        }

        return BeginPairingAsync(host.BaseUrl, host.HostName);
    }

    [RelayCommand]
    private Task ConnectManuallyAsync()
    {
        var address = ManualAddress.Trim();
        var baseUrl = LumiRemoteClient.NormalizeBaseUrl(address);
        if (!AllowInsecureLanDiscovery && RequiresTrustedAddressConfirmation(baseUrl))
        {
            ErrorText = "Direct LAN connections are unencrypted. Enable the LAN option only on a network you trust.";
            return Task.CompletedTask;
        }

        return address.Length == 0
            ? Task.CompletedTask
            : BeginPairingAsync(baseUrl, address);
    }

    internal static bool RequiresTrustedAddressConfirmation(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.Trim('[', ']');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IPAddress.TryParse(host, out var address))
            return true;

        return !IPAddress.IsLoopback(address) && !RemoteProtocol.IsTailscaleAddress(address);
    }

    private async Task BeginPairingAsync(string baseUrl, string displayName)
    {
        ErrorText = null;
        StatusText = "Saying hello…";
        Step = ConnectStep.Connecting;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var hello = await _client.HelloAsync(baseUrl, timeout.Token);

            if (hello is null)
            {
                ErrorText = "Lumi didn't answer at that address.";
                Step = ConnectStep.FindPc;
                return;
            }

            if (!RemoteProtocol.IsCompatibleVersion(hello.ProtocolVersion)
                || !RemoteProtocol.HasRequiredCapabilities(hello.Capabilities))
            {
                ErrorText = "That Lumi is a different version. Update both apps and try again.";
                Step = ConnectStep.FindPc;
                return;
            }

            _targetBaseUrl = LumiRemoteClient.NormalizeBaseUrl(baseUrl);
            TargetHostName = string.IsNullOrWhiteSpace(hello.HostName) ? displayName : hello.HostName;

            if (hello.IsPaired)
            {
                StatusText = null;
                await _onPaired(_targetBaseUrl, TargetHostName);
                return;
            }

            StatusText = null;
            PairingCode = "";
            Step = ConnectStep.EnterCode;
        }
        catch (OperationCanceledException)
        {
            ErrorText = "That address took too long to answer.";
            Step = ConnectStep.FindPc;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            Step = ConnectStep.FindPc;
        }
    }

    [RelayCommand]
    private async Task SubmitCodeAsync()
    {
        if (!CanSubmitCode)
            return;

        ErrorText = null;
        StatusText = "Pairing…";
        Step = ConnectStep.Connecting;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await _client.PairAsync(_targetBaseUrl, PairingCode.Trim(), timeout.Token);

            if (!response.Ok)
            {
                ErrorText = response.Error ?? "Pairing failed.";
                Step = ConnectStep.EnterCode;
                return;
            }

            TargetHostName = string.IsNullOrWhiteSpace(response.HostName) ? TargetHostName : response.HostName;
            StatusText = null;
            await _onPaired(_targetBaseUrl, TargetHostName);
        }
        catch (OperationCanceledException)
        {
            ErrorText = "Pairing took too long.";
            Step = ConnectStep.EnterCode;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            Step = ConnectStep.EnterCode;
        }
    }

    [RelayCommand]
    private void Back()
    {
        ErrorText = null;
        StatusText = null;
        Step = ConnectStep.FindPc;
    }
}
