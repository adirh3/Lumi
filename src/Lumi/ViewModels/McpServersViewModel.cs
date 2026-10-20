using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;
using StrataSearch;

namespace Lumi.ViewModels;

public partial class McpServersViewModel : ObservableObject
{
    private readonly DataStore _dataStore;
    /// <summary>Raised when MCP server config changes (add/edit/delete/toggle) so chat sessions can be invalidated.</summary>
    public event Action? McpConfigChanged;

    /// <summary>
    /// Raised to open the share sheet for a server; the shell owns the sheet. The flag is true when the
    /// open editor holds edits that the shared (saved) version does not include.
    /// </summary>
    public event Action<McpServer, bool>? ShareRequested;

    /// <summary>Raised for one-click "Copy for chat"; the flag has the same meaning as for <see cref="ShareRequested"/>.</summary>
    public event Action<McpServer, bool>? CopyForChatRequested;

    /// <summary>Raised when an agent in "Used by" is clicked; the shell switches to the Agents page.</summary>
    public event Action<Guid>? OpenAgentRequested;

    private string? _editorBaseline;
    private bool _restoringSelection;
    private bool _syncingEditor;
    private int _savedToastVersion;

    /// <summary>Raised to open the import sheet; the shell owns the sheet.</summary>
    public event Action? ImportRequested;

    [ObservableProperty] private McpServer? _selectedServer;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editDescription = "";
    [ObservableProperty] private int _editServerTypeIndex; // 0 = local, 1 = remote
    [ObservableProperty] private string _editCommand = "";
    [ObservableProperty] private string _editArgs = "";
    [ObservableProperty] private string _editNpxPackage = ""; // npm package name for npx servers
    [ObservableProperty] private string _editServerArgs = ""; // user-configurable args (after package name)
    [ObservableProperty] private bool _isNpxCommand;
    [ObservableProperty] private string _editUrl = "";
    [ObservableProperty] private string _editEnvVars = ""; // KEY=VALUE per line
    [ObservableProperty] private string _editHeaders = ""; // KEY=VALUE per line
    [ObservableProperty] private bool _editIsEnabled = true;
    [ObservableProperty] private string _searchQuery = "";

    /// <summary>The open server differs from what is stored.</summary>
    [ObservableProperty] private bool _hasUnsavedChanges;

    [ObservableProperty] private bool _isConfirmingDelete;
    [ObservableProperty] private bool _showSavedToast;

    /// <summary>0 sorts by name, 1 puts the newest first.</summary>
    [ObservableProperty] private int _sortIndex;

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    public ObservableCollection<McpServer> Servers { get; } = [];

    /// <summary>The overview gallery: the same filtered list as <see cref="Servers"/>, with usage.</summary>
    public ObservableCollection<McpServerCard> ServerCards { get; } = [];

    /// <summary>Lumis that pick the open server.</summary>
    public ObservableCollection<RelatedItem> UsedByAgents { get; } = [];

     public McpServersViewModel(DataStore dataStore)
     {
         _dataStore = dataStore;
         External.Opened += OnExternalOpened;
         External.CopyRequested += CopyExternal;
         External.PropertyChanged += OnExternalPropertyChanged;
         RefreshList();
     }

    /// <summary>MCP servers from plugins and the GitHub Copilot folder: listed, used in chats, read-only.</summary>
    public ExternalCapabilitiesViewModel External { get; } = new(CapabilityKind.McpServer);

    /// <summary>Raised to open Discover's MCP servers.</summary>
    public event Action? DiscoverRequested;

    public bool ShowOverview => !IsEditing && !External.IsViewing;
    public bool ShowYoursHeading => External.HasGroups && (HasResults || ShowCompactEmpty);
    public bool ShowCompactEmpty => !HasAnyServers && External.HasAnyItems && !IsSearching;
    public bool ShowStats => HasAnyServers || External.HasAnyItems;
    public bool HasExternal => External.HasAnyItems;
    public string ExternalCountText => External.TotalCountText;

    /// <summary>Every MCP server a chat can use, Lumi's and those from plugins and Copilot, for the Customize row.</summary>
    public string SectionCountText => (_dataStore.Data.McpServers.Count + External.TotalCount).ToString(CultureInfo.CurrentCulture);

    [ObservableProperty] private string _savedToastText = Loc.Mg_Saved;

    /// <summary>Shown after Discover adds a server that still needs keys or other values.</summary>
    [ObservableProperty] private bool _isFinishingSetup;

    [RelayCommand]
    private void FindInDiscover() => DiscoverRequested?.Invoke();

    /// <summary>Opens a server added from Discover that still needs values filled in.</summary>
    public void OpenForSetup(Guid id)
    {
        OpenById(id);
        IsFinishingSetup = SelectedServer?.Id == id;
    }

    private void OnExternalOpened(ExternalCapabilityItem item)
    {
        IsConfirmingDelete = false;
        _restoringSelection = true;
        try
        {
            SelectedServer = null;
        }
        finally
        {
            _restoringSelection = false;
        }

        IsEditing = false;
    }

    private void OnExternalPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ExternalCapabilitiesViewModel.IsViewing):
                OnPropertyChanged(nameof(ShowOverview));
                break;
            case nameof(ExternalCapabilitiesViewModel.HasGroups):
            case nameof(ExternalCapabilitiesViewModel.HasAnyItems):
            case nameof(ExternalCapabilitiesViewModel.TotalCountText):
                NotifyExternalOverview();
                break;
        }
    }

    private void NotifyExternalOverview()
    {
        OnPropertyChanged(nameof(ShowNoResults));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowYoursHeading));
        OnPropertyChanged(nameof(ShowCompactEmpty));
        OnPropertyChanged(nameof(ShowStats));
        OnPropertyChanged(nameof(HasExternal));
        OnPropertyChanged(nameof(ExternalCountText));
        OnPropertyChanged(nameof(SectionCountText));
    }

    /// <summary>Makes an editable Lumi server from a read-only one and opens it.</summary>
    private void CopyExternal(ExternalCapabilityItem item)
    {
        if (ExternalCapabilityCopier.CopyMcpServer(_dataStore, item.Descriptor) is not { } server)
            return;

        _ = _dataStore.SaveAsync();
        External.Close();
        if (!string.IsNullOrEmpty(SearchQuery))
            SearchQuery = "";
        RefreshList();
        SelectedServer = server;
        FlashSaved(Loc.External_CopiedToast);
        McpConfigChanged?.Invoke();
    }
    public bool IsNewServer => IsEditing && SelectedServer is null;
    public bool ShowSaveBar => IsEditing && (HasUnsavedChanges || IsNewServer) && !IsConfirmingDelete;
    public bool IsLocal => EditServerTypeIndex == 0;
    public bool IsRemote => EditServerTypeIndex == 1;
    public bool CanSave => !string.IsNullOrWhiteSpace(EditName)
        && (IsLocal ? !string.IsNullOrWhiteSpace(EditCommand) : !string.IsNullOrWhiteSpace(EditUrl));
    public string SaveButtonText => IsNewServer ? Loc.Mcp_Create : Loc.Mg_SaveChanges;
    public string SaveBarText => IsNewServer ? Loc.Mg_NotSavedYet : Loc.Mg_Unsaved;
    public string DiscardButtonText => IsNewServer ? Loc.Common_Cancel : Loc.Mg_Discard;
    public string DetailTitle => string.IsNullOrWhiteSpace(EditName) ? Loc.Mcp_NewTitle : EditName;
    public string DeleteConfirmText => string.Format(CultureInfo.CurrentCulture, Loc.Mg_DeleteConfirm, DetailTitle);
    public bool HasAnyServers => _dataStore.Data.McpServers.Count > 0;
    public bool HasResults => ServerCards.Count > 0;
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchQuery);
    public bool ShowNoResults => IsSearching && !HasResults && !External.HasGroups;
    public bool ShowEmptyState => !HasAnyServers && !External.HasAnyItems;
    public string NoResultsText => string.Format(CultureInfo.CurrentCulture, Loc.Mg_NoResults, SearchQuery.Trim());
    public string TotalCountText => _dataStore.Data.McpServers.Count.ToString(CultureInfo.CurrentCulture);
    public string EnabledCountText => _dataStore.Data.McpServers.Count(server => server.IsEnabled).ToString(CultureInfo.CurrentCulture);
    public bool IsSortedByName => SortIndex == 0;
    public bool IsSortedByRecent => SortIndex == 1;
    public bool HasUsedBy => UsedByAgents.Count > 0;
    public string UsedByLabel => HasUsedBy
        ? ManagementText.Count(UsedByAgents.Count, Loc.Skills_UsedByOne, Loc.Skills_UsedByMany)
        : Loc.Skills_NotUsed;
    public string CreatedLabel => SelectedServer is { } server
        ? string.Format(CultureInfo.CurrentCulture, Loc.Mg_Created, ManagementText.Date(server.CreatedAt))
        : "";
    public string EnabledLabel => EditIsEnabled ? Loc.Mcp_StatusOn : Loc.Mcp_StatusOff;

    /// <summary>What will run or be contacted, exactly as it would be launched.</summary>
    public string CommandPreview
    {
        get
        {
            if (IsRemote)
                return EditUrl.Trim();

            var args = IsNpxCommand && !string.IsNullOrWhiteSpace(EditNpxPackage)
                ? new[] { "-y", EditNpxPackage.Trim() }.Concat(SplitLines(EditServerArgs))
                : SplitLines(EditArgs);
            return string.Join(' ', new[] { EditCommand.Trim() }.Concat(args.Select(QuoteArgument)).Where(part => part.Length > 0));
        }
    }

    public bool HasCommandPreview => !string.IsNullOrWhiteSpace(CommandPreview);

    private static IEnumerable<string> SplitLines(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string QuoteArgument(string argument)
        => argument.Contains(' ') && !argument.StartsWith('"') ? $"\"{argument}\"" : argument;

    /// <summary>"npx -y @scope/server" for a local server, the host for a remote one.</summary>
    internal static string DescribeEndpoint(McpServer server)
    {
        if (string.Equals(server.ServerType, "remote", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(server.Url, UriKind.Absolute, out var uri) ? uri.Host + uri.AbsolutePath.TrimEnd('/') : server.Url;

        var command = System.IO.Path.GetFileNameWithoutExtension(server.Command);
        var package = server.Args.FirstOrDefault(arg => !arg.StartsWith('-'));
        return string.IsNullOrWhiteSpace(package) ? command : $"{command} {package}";
    }

     public void RefreshFromStore()
     {
         // Rebuilding the list clears the sidebar's selection, and through its two-way binding the
         // open server. Put it back quietly afterwards: the editor stays attached, the page stays in
         // the editor as it was, and edits in progress are kept.
         var open = SelectedServer;
         var hasUnsavedEdits = IsEditing && CaptureEditor() != _editorBaseline;
         RefreshList();

         if (open is null)
             return;

         var stored = _dataStore.Data.McpServers.FirstOrDefault(server => server.Id == open.Id);
         if (stored is null)
         {
             SelectedServer = null;
             IsEditing = false;
             return;
         }

         RestoreSelection(stored);
         if (!hasUnsavedEdits)
             SyncEditorFromServer(stored);
         RefreshRelations(stored);
     }

     private void RestoreSelection(McpServer server)
     {
         _restoringSelection = true;
         try
         {
             SelectedServer = server;
         }
         finally
         {
             _restoringSelection = false;
         }
     }

    private void RefreshList()
    {
        Servers.Clear();
        var hasQuery = !string.IsNullOrWhiteSpace(SearchQuery);
        var items = hasQuery
            ? SearchPipeline.Rank(
                _dataStore.Data.McpServers,
                SearchQuery,
                static server =>
                [
                    SearchField.Primary(server.Name, 3.2),
                    new SearchField(server.Description, 1.7),
                    new SearchField(server.Command, 1.0),
                    new SearchField(server.Url, 0.8),
                    new SearchField(string.Join(' ', server.Args), 0.9),
                    new SearchField(string.Join(' ', server.Tools), 0.85)
                ],
                static server => new SearchSortMetadata(Text: server.Name))
            : SortIndex == 1
                ? _dataStore.Data.McpServers.OrderByDescending(server => server.CreatedAt).ToArray()
                : _dataStore.Data.McpServers.OrderBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

        foreach (var server in items)
            Servers.Add(server);

        ServerCards.Clear();
        foreach (var server in Servers)
        {
            var usedBy = _dataStore.Data.Agents.Count(agent => agent.McpServerIds.Contains(server.Id));
            ServerCards.Add(new McpServerCard(server, usedBy, EditServer, ToggleServer));
        }

        // The sidebar follows the open server one way; re-announcing it re-selects the row after a rebuild.
        OnPropertyChanged(nameof(SelectedServer));
        OnPropertyChanged(nameof(HasAnyServers));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(ShowNoResults));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(NoResultsText));
        OnPropertyChanged(nameof(TotalCountText));
        OnPropertyChanged(nameof(EnabledCountText));
        NotifyExternalOverview();
    }

    [RelayCommand]
    private void NewServer()
    {
        SelectedServer = null;
        _syncingEditor = true;
        try
        {
            EditName = "";
            EditDescription = "";
            EditServerTypeIndex = 0;
            EditCommand = "";
            EditArgs = "";
            EditNpxPackage = "";
            EditServerArgs = "";
            IsNpxCommand = false;
            EditUrl = "";
            EditEnvVars = "";
            EditHeaders = "";
            EditIsEnabled = true;
        }
        finally
        {
            _syncingEditor = false;
        }

        _editorBaseline = CaptureEditor();
        UsedByAgents.Clear();
        IsConfirmingDelete = false;
        IsFinishingSetup = false;
        External.Close();
        IsEditing = true;
        UpdateDirtyState();
        RefreshRelationFacts();
    }

    [RelayCommand]
    private void EditServer(McpServer server)
    {
        SelectedServer = server;
    }

    /// <summary>Opens a server by id, clearing a search that would hide it from the list.</summary>
    public void OpenById(Guid id)
    {
        if (_dataStore.Data.McpServers.FirstOrDefault(server => server.Id == id) is not { } server)
            return;

        if (!Servers.Contains(server))
            SearchQuery = "";
        if (ReferenceEquals(SelectedServer, server))
        {
            External.Close();
            IsEditing = true;
            return;
        }

        SelectedServer = server;
    }

    /// <summary>Back to the overview.</summary>
    [RelayCommand]
    private void CloseDetail()
    {
        IsConfirmingDelete = false;
        IsFinishingSetup = false;
        SelectedServer = null;
        IsEditing = false;
        External.Close();
    }

     partial void OnSelectedServerChanged(McpServer? value)
     {
         if (value is null || _restoringSelection) return;
         External.Close();
         IsFinishingSetup = false;
         SyncEditorFromServer(value);
         RefreshRelations(value);

         IsConfirmingDelete = false;
         IsEditing = true;
     }

     private void SyncEditorFromServer(McpServer server)
     {
         _syncingEditor = true;
         try
         {
             EditName = server.Name;
             EditDescription = server.Description;
             EditServerTypeIndex = server.ServerType == "remote" ? 1 : 0;
             EditCommand = server.Command;
             EditArgs = string.Join("\n", server.Args);
             EditUrl = server.Url;
             EditEnvVars = string.Join("\n", server.Env.Select(kv => $"{kv.Key}={kv.Value}"));
             EditHeaders = string.Join("\n", server.Headers.Select(kv => $"{kv.Key}={kv.Value}"));
             EditIsEnabled = server.IsEnabled;

             IsNpxCommand = server.Command is "npx" or "npx.cmd";
             if (IsNpxCommand)
             {
                 var packageIndex = server.Args.FindIndex(arg => !arg.StartsWith('-'));
                 if (packageIndex >= 0)
                 {
                     EditNpxPackage = server.Args[packageIndex];
                     EditServerArgs = string.Join("\n", server.Args.Skip(packageIndex + 1));
                 }
                 else
                 {
                     EditNpxPackage = "";
                     EditServerArgs = "";
                 }
             }
             else
             {
                 EditNpxPackage = "";
                 EditServerArgs = "";
             }
         }
         finally
         {
             _syncingEditor = false;
         }

         _editorBaseline = CaptureEditor();
         UpdateDirtyState();
     }

    private void RefreshRelations(McpServer server)
    {
        UsedByAgents.Clear();
        foreach (var agent in _dataStore.Data.Agents
                     .Where(agent => agent.McpServerIds.Contains(server.Id))
                     .OrderBy(agent => agent.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            UsedByAgents.Add(new RelatedItem(agent.Id, agent.Name, string.IsNullOrWhiteSpace(agent.IconGlyph) ? "✦" : agent.IconGlyph));
        }

        RefreshRelationFacts();
    }

    private void RefreshRelationFacts()
    {
        OnPropertyChanged(nameof(HasUsedBy));
        OnPropertyChanged(nameof(UsedByLabel));
        OnPropertyChanged(nameof(CreatedLabel));
    }

    private void UpdateDirtyState()
    {
        if (_syncingEditor)
            return;

        HasUnsavedChanges = IsEditing && CaptureEditor() != _editorBaseline;
        OnPropertyChanged(nameof(IsNewServer));
        OnPropertyChanged(nameof(ShowSaveBar));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(SaveBarText));
        OnPropertyChanged(nameof(DiscardButtonText));
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(IsLocal));
        OnPropertyChanged(nameof(IsRemote));
        OnPropertyChanged(nameof(CommandPreview));
        OnPropertyChanged(nameof(HasCommandPreview));
        OnPropertyChanged(nameof(EnabledLabel));
    }

    partial void OnEditNameChanged(string value) => UpdateDirtyState();
    partial void OnEditDescriptionChanged(string value) => UpdateDirtyState();
    partial void OnEditServerTypeIndexChanged(int value) => UpdateDirtyState();
    partial void OnEditArgsChanged(string value) => UpdateDirtyState();
    partial void OnEditNpxPackageChanged(string value) => UpdateDirtyState();
    partial void OnEditServerArgsChanged(string value) => UpdateDirtyState();
    partial void OnEditUrlChanged(string value) => UpdateDirtyState();
    partial void OnEditEnvVarsChanged(string value) => UpdateDirtyState();
    partial void OnEditHeadersChanged(string value) => UpdateDirtyState();
    partial void OnEditIsEnabledChanged(bool value) => UpdateDirtyState();
    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowOverview));
        UpdateDirtyState();
    }

    partial void OnHasUnsavedChangesChanged(bool value) => OnPropertyChanged(nameof(ShowSaveBar));
    partial void OnIsConfirmingDeleteChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSaveBar));
        OnPropertyChanged(nameof(DeleteConfirmText));
    }

    partial void OnSortIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSortedByName));
        OnPropertyChanged(nameof(IsSortedByRecent));
        RefreshList();
    }

    [RelayCommand]
    private void SortByName() => SortIndex = 0;

    [RelayCommand]
    private void SortByRecent() => SortIndex = 1;

    [RelayCommand]
    private void UseLocal() => EditServerTypeIndex = 0;

    [RelayCommand]
    private void UseRemote() => EditServerTypeIndex = 1;

    [RelayCommand]
    private void OpenAgent(RelatedItem? item)
    {
        if (item is not null)
            OpenAgentRequested?.Invoke(item.Id);
    }

    [RelayCommand]
    private void RequestDelete()
    {
        if (SelectedServer is not null)
            IsConfirmingDelete = true;
    }

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private void ConfirmDelete()
    {
        IsConfirmingDelete = false;
        if (SelectedServer is { } server)
            DeleteServer(server);
    }

    /// <summary>Throws away edits: an existing server goes back to what is stored, a new one is dropped.</summary>
    [RelayCommand]
    private void DiscardChanges()
    {
        if (SelectedServer is null)
        {
            IsEditing = false;
            return;
        }

        SyncEditorFromServer(SelectedServer);
    }

    private async void FlashSaved(string? text = null)
    {
        var version = ++_savedToastVersion;
        SavedToastText = text ?? Loc.Mg_Saved;
        ShowSavedToast = true;
        await Task.Delay(1800);
        if (version == _savedToastVersion)
            ShowSavedToast = false;
    }

    private string CaptureEditor()
        => string.Join(
            '\u001F',
            EditName,
            EditDescription,
            EditServerTypeIndex,
            EditCommand,
            EditArgs,
            EditNpxPackage,
            EditServerArgs,
            EditUrl,
            EditEnvVars,
            EditHeaders,
            EditIsEnabled);

    partial void OnEditCommandChanged(string value)
    {
        IsNpxCommand = value is "npx" or "npx.cmd";
        UpdateDirtyState();
    }

    [RelayCommand]
    private void SaveServer()
    {
        if (string.IsNullOrWhiteSpace(EditName)) return;

        var isLocal = EditServerTypeIndex == 0;
        if (isLocal && string.IsNullOrWhiteSpace(EditCommand)) return;
        if (!isLocal && string.IsNullOrWhiteSpace(EditUrl)) return;

        List<string> args;
        if (isLocal && IsNpxCommand && !string.IsNullOrEmpty(EditNpxPackage))
        {
            // Reconstruct args: -y + package + user server args
            var serverArgs = EditServerArgs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            args = ["-y", EditNpxPackage, ..serverArgs];
        }
        else
        {
            args = EditArgs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        var envEntries = EditEnvVars.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var headerEntries = EditHeaders.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var existingIds = _dataStore.Data.McpServers.Select(static server => server.Id).ToHashSet();
        var result = new LumiFeatureManager(_dataStore).ManageMcps(
            action: SelectedServer is null ? "create" : "update",
            identifier: SelectedServer?.Id.ToString(),
            name: EditName.Trim(),
            description: EditDescription.Trim(),
            serverType: isLocal ? "local" : "remote",
            command: isLocal ? EditCommand.Trim() : null,
            args: isLocal ? [..args] : [],
            url: isLocal ? null : EditUrl.Trim(),
            envEntries: isLocal ? envEntries : [],
            headerEntries: isLocal ? [] : headerEntries,
            isEnabled: EditIsEnabled);
        if (!result.DataChanged)
            return;

        _ = _dataStore.SaveAsync();

        // Saving keeps the server open: the page now shows what was stored.
        var saved = SelectedServer is { } open
            ? _dataStore.Data.McpServers.FirstOrDefault(server => server.Id == open.Id)
            : _dataStore.Data.McpServers.FirstOrDefault(server => !existingIds.Contains(server.Id));
        RefreshList();
        if (saved is null)
        {
            IsEditing = false;
        }
        else
        {
            RestoreSelection(saved);
            SyncEditorFromServer(saved);
            RefreshRelations(saved);
            IsEditing = true;
            UpdateDirtyState();
            IsFinishingSetup = false;
            FlashSaved();
        }

        McpConfigChanged?.Invoke();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
    }

    [RelayCommand]
    private void OpenNpmPage()
    {
        if (string.IsNullOrEmpty(EditNpxPackage)) return;
        var url = $"https://www.npmjs.com/package/{EditNpxPackage}";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* ignore if browser launch fails */ }
    }

    [RelayCommand]
    private void DeleteServer(McpServer server)
    {
        var result = new LumiFeatureManager(_dataStore).ManageMcps("delete", identifier: server.Id.ToString());
        if (!result.DataChanged)
            return;

        _ = _dataStore.SaveAsync();
        if (SelectedServer == server)
        {
            SelectedServer = null;
            IsEditing = false;
        }
        RefreshList();
        McpConfigChanged?.Invoke();
    }

    [RelayCommand]
    private void ToggleServer(McpServer server)
    {
        server.IsEnabled = !server.IsEnabled;
        _ = _dataStore.SaveAsync();
        RefreshList();
        McpConfigChanged?.Invoke();
    }

    [RelayCommand]
    private void ShareServer(McpServer? server)
    {
        server ??= SelectedServer;
        if (server is null)
            return;

        var hasUnsavedEdits = IsEditing && ReferenceEquals(server, SelectedServer) && CaptureEditor() != _editorBaseline;
        ShareRequested?.Invoke(server, hasUnsavedEdits);
    }

    [RelayCommand]
    private void CopyServerForChat(McpServer? server)
    {
        server ??= SelectedServer;
        if (server is null)
            return;

        var hasUnsavedEdits = IsEditing && ReferenceEquals(server, SelectedServer) && CaptureEditor() != _editorBaseline;
        CopyForChatRequested?.Invoke(server, hasUnsavedEdits);
    }

    [RelayCommand]
    private void Import() => ImportRequested?.Invoke();

    partial void OnSearchQueryChanged(string value)
    {
        External.ApplySearch(value);
        RefreshList();
    }
}
