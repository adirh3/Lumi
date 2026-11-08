using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.ViewModels;

/// <summary>
/// Settings → Permissions → Sandbox: the sandbox projects follow unless they choose otherwise, and the
/// folders it reaches in every sandboxed chat. Like the default mode, a change reaches live chats at once.
/// </summary>
public partial class SettingsViewModel
{
    private SandboxHostState? _sandboxHost;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSandboxEnabledModified))]
    private bool _sandboxEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSandboxAllowInternetModified))]
    private bool _sandboxAllowInternet;

    /// <summary>What this computer's sandbox can do; asked of the runtime when the page opens.</summary>
    public SandboxHostState SandboxHost
    {
        get
        {
            if (_sandboxHost is null)
            {
                _sandboxHost = new SandboxHostState(_copilotService.GetSandboxHostSupportAsync);
                _sandboxHost.Changed += OnSandboxHostChanged;
            }
            return _sandboxHost;
        }
    }

    public ObservableCollection<SandboxFolderRow> SandboxFolders { get; } = [];
    public bool HasSandboxFolders => SandboxFolders.Count > 0;

    public bool IsSandboxEnabledModified => SandboxEnabled != _defaults.Sandbox.Enabled;
    public bool IsSandboxAllowInternetModified => SandboxAllowInternet != _defaults.Sandbox.AllowInternet;

    /// <summary>A computer that can't sandbox can still turn the default off, never on.</summary>
    public bool CanToggleSandboxDefault => SandboxEnabled || SandboxHost.IsSupported;

    /// <summary>What the default does, or why this computer can't run the sandbox.</summary>
    public string SandboxDefaultDescription => SandboxHost.UnavailableText ?? Loc.Get("Permissions_SandboxDefaultDesc");

    public string SandboxInternetDescription => SandboxHost.CanReachInternet
        ? Loc.Get("Permissions_SandboxInternetDesc")
        : SandboxHost.InternetUnavailableText;

    private void InitializeSandbox(UserSettings settings)
    {
        // Loading, not changing: skip the change handlers that save and re-apply the policy.
#pragma warning disable MVVMTK0034
        _sandboxEnabled = settings.Sandbox.Enabled;
        _sandboxAllowInternet = settings.Sandbox.AllowInternet;
#pragma warning restore MVVMTK0034
        LoadSandboxFolders(settings.Sandbox.Folders);
    }

    /// <summary>The page opened: approval cards don't change these folders, but this computer's answer may be new.</summary>
    private void RefreshSandbox()
    {
        OnPropertyChanged(nameof(CanToggleSandboxDefault));
        _ = SandboxHost.RefreshAsync();
    }

    partial void OnSandboxEnabledChanged(bool value)
    {
        _dataStore.Data.Settings.Sandbox.Enabled = value;
        OnPropertyChanged(nameof(CanToggleSandboxDefault));
        SandboxChanged();
    }

    partial void OnSandboxAllowInternetChanged(bool value)
    {
        _dataStore.Data.Settings.Sandbox.AllowInternet = value;
        SandboxChanged();
    }

    /// <summary>Folders picked for every sandboxed project: their commands may read and write them until the user narrows that.</summary>
    public void AddSandboxFolders(IEnumerable<string> paths)
    {
        if (SandboxFolderRow.AddPaths(SandboxFolders, paths, NewSandboxFolderRow))
            SaveSandboxFolders();
    }

    [RelayCommand] private void RevertSandboxEnabled() => SandboxEnabled = _defaults.Sandbox.Enabled;
    [RelayCommand] private void RevertSandboxAllowInternet() => SandboxAllowInternet = _defaults.Sandbox.AllowInternet;

    private void LoadSandboxFolders(IEnumerable<SandboxFolder> folders)
    {
        SandboxFolders.Clear();
        foreach (var folder in folders)
            SandboxFolders.Add(NewSandboxFolderRow(folder));
        OnPropertyChanged(nameof(HasSandboxFolders));
    }

    private SandboxFolderRow NewSandboxFolderRow(SandboxFolder folder)
        => new(folder, _ => SaveSandboxFolders(), RemoveSandboxFolder) { CanBlock = SandboxHost.CanBlockFolders };

    private void RemoveSandboxFolder(SandboxFolderRow row)
    {
        SandboxFolders.Remove(row);
        SaveSandboxFolders();
    }

    private void SaveSandboxFolders()
    {
        _dataStore.Data.Settings.Sandbox.Folders = SandboxFolders.Select(row => row.ToFolder()).ToList();
        OnPropertyChanged(nameof(HasSandboxFolders));
        SandboxChanged();
    }

    private void SandboxChanged()
    {
        Save();
        NotifyModified();
        ApprovalPolicyChanged?.Invoke();
    }

    private void OnSandboxHostChanged()
    {
        foreach (var row in SandboxFolders)
            row.CanBlock = SandboxHost.CanBlockFolders;
        OnPropertyChanged(nameof(CanToggleSandboxDefault));
        OnPropertyChanged(nameof(SandboxDefaultDescription));
        OnPropertyChanged(nameof(SandboxInternetDescription));
    }

    /// <summary>Restoring defaults puts back the switches; folders, like rules, are the user's own list and stay.</summary>
    private void ResetSandbox(UserSettings defaults)
    {
        SandboxEnabled = defaults.Sandbox.Enabled;
        SandboxAllowInternet = defaults.Sandbox.AllowInternet;
    }
}
