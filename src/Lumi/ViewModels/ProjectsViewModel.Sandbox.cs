using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services.Permissions;

namespace Lumi.ViewModels;

/// <summary>
/// Customize → Projects → Sandbox: whether the project's chats run their commands in the OS sandbox (your
/// default from Settings → Permissions, or the project's own choice), and what it lets them reach.
/// </summary>
public partial class ProjectsViewModel
{
    /// <summary>
    /// The project's sandbox as the page last took it in. Approval cards change the project's folders
    /// while the page may hold unsaved edits, so those edits are measured, and merged, against this.
    /// </summary>
    private ProjectSandbox _sandboxBase = new();

    /// <summary>The project's own choice; null follows your default.</summary>
    [ObservableProperty] private bool? _editSandboxEnabled;

    /// <summary>Whether its commands reach the internet; null follows your default.</summary>
    [ObservableProperty] private bool? _editSandboxAllowInternet;

    public SandboxHostState SandboxHost { get; }
    public IReadOnlyList<SandboxChoice> SandboxChoices { get; }
    public IReadOnlyList<SandboxChoice> SandboxInternetChoices { get; }

    public ObservableCollection<SandboxFolderRow> SandboxFolders { get; } = [];
    public bool HasSandboxFolders => SandboxFolders.Count > 0;

    private SandboxSettings SandboxDefaults => _dataStore.Data.Settings.Sandbox;

    /// <summary>The project as edited sandboxes its chats' commands, so the page shows what the sandbox reaches.</summary>
    public bool IsSandboxOn => EditSandboxEnabled ?? (SandboxDefaults.Enabled && HasWorkingDirectory);

    /// <summary>What the choice means for the project's chats, as the approvals choice says it.</summary>
    public string SandboxChoiceHint => Loc.Get(EditSandboxEnabled switch
    {
        true => "Projects_SandboxOnHint",
        false => "Projects_SandboxOffHint",
        _ => SandboxDefaults.Enabled ? "Projects_SandboxFollowsOn" : "Projects_SandboxFollowsOff"
    });

    /// <summary>A sandbox needs the project's folder to contain its commands in.</summary>
    public bool ShowSandboxNeedsFolder => !HasWorkingDirectory && SandboxHost.IsSupported;

    public string SandboxInternetDescription => !SandboxHost.CanReachInternet
        ? SandboxHost.InternetUnavailableText
        : Loc.Get(EditSandboxAllowInternet is not null ? "Projects_SandboxInternetDescription"
            : SandboxDefaults.AllowInternet ? "Projects_SandboxInternetFollowsOn" : "Projects_SandboxInternetFollowsOff");

    /// <summary>The folders Settings → Permissions gives every sandboxed project, which reach this one too.</summary>
    public string? SandboxDefaultFoldersNote => SandboxDefaults.Folders.Count switch
    {
        0 => null,
        1 => Loc.Get("Projects_SandboxDefaultFoldersOne"),
        var count => Loc.Get("Projects_SandboxDefaultFolders", count)
    };

    public bool HasSandboxDefaultFoldersNote => SandboxDefaults.Folders.Count > 0;

    /// <summary>Everything the page shows that follows your default, the project's folder or this computer.</summary>
    private void NotifySandboxPropertiesChanged()
    {
        // On needs a folder to contain the commands in and a computer that can; the other choices are always open.
        SandboxChoices.First(choice => choice.Value == true).IsEnabled =
            EditSandboxEnabled == true || (HasWorkingDirectory && SandboxHost.IsSupported);
        foreach (var choice in SandboxInternetChoices)
            choice.IsEnabled = SandboxHost.CanReachInternet;
        foreach (var row in SandboxFolders)
            row.CanBlock = SandboxHost.CanBlockFolders;
        OnPropertyChanged(nameof(IsSandboxOn));
        OnPropertyChanged(nameof(SandboxChoiceHint));
        OnPropertyChanged(nameof(ShowSandboxNeedsFolder));
        OnPropertyChanged(nameof(SandboxInternetDescription));
        OnPropertyChanged(nameof(SandboxDefaultFoldersNote));
        OnPropertyChanged(nameof(HasSandboxDefaultFoldersNote));
    }

    private void SyncSandboxEditor(Project? project)
    {
        var sandbox = project?.Sandbox ?? new ProjectSandbox();
        EditSandboxEnabled = sandbox.Enabled;
        EditSandboxAllowInternet = sandbox.AllowInternet;
        // The same choice as the last project's raises no change, so mark it here.
        SandboxChoice.Select(SandboxChoices, sandbox.Enabled);
        SandboxChoice.Select(SandboxInternetChoices, sandbox.AllowInternet);
        SandboxFolders.Clear();
        foreach (var folder in sandbox.Folders)
            SandboxFolders.Add(NewSandboxRow(folder));
        _sandboxBase = Copy(sandbox);
        OnPropertyChanged(nameof(HasSandboxFolders));
        NotifySandboxPropertiesChanged();
        _ = SandboxHost.RefreshAsync();
    }

    private bool IsSandboxEdited => Describe(SandboxFromEditor()) != Describe(_sandboxBase);

    private static string Describe(ProjectSandbox sandbox)
        => string.Join('\u001E', [
            sandbox.Enabled?.ToString() ?? "default",
            sandbox.AllowInternet?.ToString() ?? "default",
            .. sandbox.Folders.Select(folder => folder.Path + "=" + folder.Access)]);

    private static ProjectSandbox Copy(ProjectSandbox sandbox) => new()
    {
        Enabled = sandbox.Enabled,
        AllowInternet = sandbox.AllowInternet,
        Folders = CopyFolders(sandbox.Folders)
    };

    private static List<SandboxFolder> CopyFolders(IEnumerable<SandboxFolder> folders)
        => folders.Select(folder => new SandboxFolder { Path = folder.Path, Access = folder.Access }).ToList();

    private ProjectSandbox SandboxFromEditor() => new()
    {
        Enabled = EditSandboxEnabled,
        AllowInternet = EditSandboxAllowInternet,
        Folders = SandboxFolders.Select(row => row.ToFolder()).ToList()
    };

    /// <summary>
    /// Folders an approval card allowed for the project show up here too, and so does a changed default.
    /// When the page holds unsaved sandbox edits, the card's change joins them and the user's own edits win.
    /// </summary>
    private void RefreshSandboxFromProject(Project? project)
    {
        if (project is null || !IsEditing)
        {
            NotifySandboxPropertiesChanged();
            return;
        }
        _syncingEditor = true;
        try
        {
            if (IsSandboxEdited)
                MergeSandboxFolders(project.Sandbox.Folders);
            else
                SyncSandboxEditor(project);
        }
        finally
        {
            _syncingEditor = false;
        }
        NotifySandboxPropertiesChanged();
        UpdateDirtyState();
    }

    /// <summary>
    /// A three-way merge against what the page last took in: a folder the project gained is added, and
    /// one whose access the project changed, or that it dropped, follows it unless the user changed it here.
    /// </summary>
    private void MergeSandboxFolders(IReadOnlyList<SandboxFolder> current)
    {
        foreach (var folder in current)
        {
            var before = _sandboxBase.Folders.Find(candidate => SandboxPolicy.SamePath(candidate.Path, folder.Path));
            var row = SandboxFolders.FirstOrDefault(candidate => SandboxPolicy.SamePath(candidate.Path, folder.Path));
            if (before is null && row is null)
                SandboxFolders.Add(NewSandboxRow(folder));
            else if (before is not null && row is not null && row.Access == before.Access)
                row.Access = folder.Access;
        }
        foreach (var before in _sandboxBase.Folders)
        {
            if (current.Any(folder => SandboxPolicy.SamePath(folder.Path, before.Path)))
                continue;
            if (SandboxFolders.FirstOrDefault(row => SandboxPolicy.SamePath(row.Path, before.Path)) is { } row
                && row.Access == before.Access)
                SandboxFolders.Remove(row);
        }
        _sandboxBase.Folders = CopyFolders(current);
        OnPropertyChanged(nameof(HasSandboxFolders));
    }

    /// <summary>Folders picked for the sandbox: commands may read and write them until the user narrows that.</summary>
    public void AddSandboxFolders(IEnumerable<string> paths)
    {
        SandboxFolderRow.AddPaths(SandboxFolders, paths, NewSandboxRow);
        OnPropertyChanged(nameof(HasSandboxFolders));
        UpdateDirtyState();
    }

    private SandboxFolderRow NewSandboxRow(SandboxFolder folder)
        => new(folder, _ => UpdateDirtyState(), RemoveSandboxFolder) { CanBlock = SandboxHost.CanBlockFolders };

    private void RemoveSandboxFolder(SandboxFolderRow row)
    {
        SandboxFolders.Remove(row);
        OnPropertyChanged(nameof(HasSandboxFolders));
        UpdateDirtyState();
    }

    /// <summary>The sandbox section should come into view: set when a chat asks to open it, cleared once the page shows it.</summary>
    [ObservableProperty] private bool _isRevealingSandbox;

    /// <summary>Opens a project on its sandbox, as the chat's approval picker asks.</summary>
    public void OpenSandbox(Guid projectId)
    {
        OpenById(projectId);
        if (SelectedProject?.Id == projectId)
            IsRevealingSandbox = true;
    }

    partial void OnEditSandboxEnabledChanged(bool? value)
    {
        SandboxChoice.Select(SandboxChoices, value);
        NotifySandboxPropertiesChanged();
        UpdateDirtyState();
    }

    partial void OnEditSandboxAllowInternetChanged(bool? value)
    {
        SandboxChoice.Select(SandboxInternetChoices, value);
        OnPropertyChanged(nameof(SandboxInternetDescription));
        UpdateDirtyState();
    }
}
