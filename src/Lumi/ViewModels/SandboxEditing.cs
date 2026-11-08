using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHub.Copilot.Rpc;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services.Permissions;

namespace Lumi.ViewModels;

/// <summary>
/// A folder in a sandbox's list, with what its commands may do there. Settings → Permissions and a
/// project's page list them alike (the row's template is shared from App.axaml).
/// </summary>
public sealed partial class SandboxFolderRow : ObservableObject
{
    private readonly Action<SandboxFolderRow> _changed;

    public SandboxFolderRow(SandboxFolder folder, Action<SandboxFolderRow> changed, Action<SandboxFolderRow> remove)
    {
        Path = folder.Path;
        _access = folder.Access;
        _changed = changed;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)) is { Length: > 0 } name ? name : Path;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadWrite))]
    [NotifyPropertyChangedFor(nameof(IsReadOnly))]
    [NotifyPropertyChangedFor(nameof(IsBlocked))]
    private SandboxFolderAccess _access;

    /// <summary>This computer can block a folder inside one the sandbox reaches (see <see cref="SandboxHostState.CanBlockFolders"/>).</summary>
    [ObservableProperty] private bool _canBlock = true;

    public bool IsReadWrite => Access == SandboxFolderAccess.ReadWrite;
    public bool IsReadOnly => Access == SandboxFolderAccess.ReadOnly;
    public bool IsBlocked => Access == SandboxFolderAccess.Blocked;

    public IRelayCommand RemoveCommand { get; }

    [RelayCommand]
    private void SetAccess(SandboxFolderAccess access) => Access = access;

    partial void OnAccessChanged(SandboxFolderAccess value) => _changed(this);

    public SandboxFolder ToFolder() => new() { Path = Path, Access = Access };

    /// <summary>Adds the picked folders not listed yet: commands may read and write them until the user narrows that.</summary>
    public static bool AddPaths(IList<SandboxFolderRow> rows, IEnumerable<string> paths, Func<SandboxFolder, SandboxFolderRow> create)
    {
        var added = false;
        // Folder pickers return "E:\folder\"; a drive root keeps its separator.
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(path => System.IO.Path.TrimEndingDirectorySeparator(path.Trim())))
        {
            if (rows.Any(row => SandboxPolicy.SamePath(row.Path, path)))
                continue;
            rows.Add(create(new SandboxFolder { Path = path, Access = SandboxFolderAccess.ReadWrite }));
            added = true;
        }
        return added;
    }
}

/// <summary>One option of a project's sandbox choice: your default (null), on or off.</summary>
public sealed partial class SandboxChoice : ObservableObject
{
    public SandboxChoice(bool? value, string label, Action<bool?> select)
    {
        Value = value;
        Label = label;
        SelectCommand = new RelayCommand(() => select(value));
    }

    public bool? Value { get; }
    public string Label { get; }
    public IRelayCommand SelectCommand { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isEnabled = true;

    /// <summary>Your default, on and off, in the order every sandbox choice shows them.</summary>
    public static IReadOnlyList<SandboxChoice> CreateAll(Action<bool?> select) =>
    [
        new(null, Loc.Get("Projects_ApprovalFollowDefault"), select),
        new(true, Loc.Get("Projects_SandboxOn"), select),
        new(false, Loc.Get("Projects_SandboxOff"), select),
    ];

    public static void Select(IEnumerable<SandboxChoice> choices, bool? value)
    {
        foreach (var choice in choices)
            choice.IsSelected = choice.Value == value;
    }
}

/// <summary>
/// What this computer's OS sandbox can do, read from the runtime when a page that sets it up opens.
/// Until the runtime answers everything counts as possible, so nothing is hidden by mistake.
/// </summary>
public sealed class SandboxHostState : ObservableObject
{
    private readonly Func<CancellationToken, Task<SandboxHostSupport?>>? _query;
    private SandboxHostSupport? _support;

    public SandboxHostState(Func<CancellationToken, Task<SandboxHostSupport?>>? query) => _query = query;

    public bool IsSupported => _support?.Supported != false;

    public string? UnavailableText => _support is { Supported: false } support
        ? Loc.Get("Projects_SandboxUnavailable", support.Reason ?? "")
        : null;

    public bool HasUnavailableText => UnavailableText is not null;

    public bool CanReachInternet => SandboxPolicy.Supports(_support, SandboxPolicy.NetworkCapability);

    public string InternetUnavailableText => Loc.Get("Projects_SandboxInternetUnavailable", Reason(SandboxPolicy.NetworkCapability));

    public bool CanBlockFolders => SandboxPolicy.Supports(_support, SandboxPolicy.BlockedFoldersCapability);

    /// <summary>Raised once the runtime answered, so a page updates what depends on it.</summary>
    public event Action? Changed;

    public async Task RefreshAsync()
    {
        if (_query is null)
            return;
        try
        {
            if (await _query(CancellationToken.None) is not { } support)
                return;
            _support = support;
        }
        catch (Exception)
        {
            return;
        }
        OnPropertyChanged(nameof(IsSupported));
        OnPropertyChanged(nameof(UnavailableText));
        OnPropertyChanged(nameof(HasUnavailableText));
        OnPropertyChanged(nameof(CanReachInternet));
        OnPropertyChanged(nameof(InternetUnavailableText));
        OnPropertyChanged(nameof(CanBlockFolders));
        Changed?.Invoke();
    }

    private string Reason(string capability)
        => _support?.Capabilities?.FirstOrDefault(c => c.Name == capability)?.Reason ?? "";
}
