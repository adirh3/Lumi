using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Lumi.Models;

namespace Lumi.ViewModels;

public partial class MainViewModel
{
    private readonly NavigationHistory _navigationHistory = new();
    private bool _navigationHistoryReady;
    private bool _navigationRecordPending;

    private void InitializeNavigationHistory()
    {
        ObserveNavigation(SettingsVM, 7, nameof(SettingsViewModel.SelectedPageIndex), nameof(SettingsViewModel.SearchQuery));
        ObserveNavigation(MemoriesVM, MemoriesNavIndex,
            nameof(MemoriesViewModel.SelectedMemory), nameof(MemoriesViewModel.IsEditing),
            nameof(MemoriesViewModel.CategoryFilter));
        ObserveNavigation(ProjectsVM, 2, nameof(ProjectsViewModel.SelectedProject), nameof(ProjectsViewModel.IsEditing));
        ObserveNavigation(SkillsVM, 3, nameof(SkillsViewModel.SelectedSkill), nameof(SkillsViewModel.IsEditing));
        ObserveNavigation(AgentsVM, 4, nameof(AgentsViewModel.SelectedAgent), nameof(AgentsViewModel.IsEditing));
        ObserveNavigation(McpServersVM, 6, nameof(McpServersViewModel.SelectedServer), nameof(McpServersViewModel.IsEditing));
        ObserveNavigation(SkillsVM.External, 3, nameof(ExternalCapabilitiesViewModel.OpenItem));
        ObserveNavigation(AgentsVM.External, 4, nameof(ExternalCapabilitiesViewModel.OpenItem));
        ObserveNavigation(McpServersVM.External, 6, nameof(ExternalCapabilitiesViewModel.OpenItem));
        ObserveNavigation(DiscoverVM, DiscoverNavIndex,
            nameof(DiscoverViewModel.Filter), nameof(DiscoverViewModel.SourceKey),
            nameof(DiscoverViewModel.OpenCard), nameof(DiscoverViewModel.SearchQuery));
        ObserveNavigation(LibraryVM, LibraryNavIndex,
            nameof(LibraryViewModel.SelectedItem), nameof(LibraryViewModel.HasActiveFilters));
        ObserveNavigation(JobsVM, 1, nameof(BackgroundJobsViewModel.SelectedJob), nameof(BackgroundJobsViewModel.IsEditing));

        _navigationHistoryReady = true;
        _navigationHistory.Record(CaptureNavigationState());
    }

    private void ObserveNavigation(INotifyPropertyChanged source, int pageIndex, params string[] properties)
    {
        source.PropertyChanged += (_, args) =>
        {
            if (SelectedNavIndex == pageIndex && Array.IndexOf(properties, args.PropertyName) >= 0)
                QueueNavigationRecord();
        };
    }

    private void QueueNavigationRecord()
    {
        if (!_navigationHistoryReady || _isDisposed || _navigationHistory.IsRestoring || _navigationRecordPending)
            return;

        // A single click can select a section, open its detail and sync its sidebar. Record the
        // completed destination, not those intermediate property changes.
        _navigationRecordPending = true;
        Dispatcher.UIThread.Post(FlushNavigationRecord, DispatcherPriority.Normal);
    }

    private void FlushNavigationRecord()
    {
        if (!_navigationRecordPending || _isDisposed)
            return;

        _navigationRecordPending = false;
        _navigationHistory.Record(CaptureNavigationState());
    }

    private NavigationState CaptureNavigationState() => SelectedNavIndex switch
    {
        0 => new ChatNavigationState(ChatVM.CurrentChat?.Id, SelectedProjectFilter),
        1 => new JobNavigationState(JobsVM.SelectedJob?.Id, JobsVM.IsEditing),
        2 => new ManagementNavigationState(2, ProjectsVM.SelectedProject?.Id, ProjectsVM.IsEditing),
        3 => new ManagementNavigationState(3, SkillsVM.SelectedSkill?.Id, SkillsVM.IsEditing,
            SkillsVM.External.NavigationItemKey),
        4 => new ManagementNavigationState(4, AgentsVM.SelectedAgent?.Id, AgentsVM.IsEditing,
            AgentsVM.External.NavigationItemKey),
        MemoriesNavIndex => new ManagementNavigationState(MemoriesNavIndex, MemoriesVM.SelectedMemory?.Id,
            MemoriesVM.IsEditing, CategoryFilter: MemoriesVM.CategoryFilter),
        6 => new ManagementNavigationState(6, McpServersVM.SelectedServer?.Id, McpServersVM.IsEditing,
            McpServersVM.External.NavigationItemKey),
        7 => new SettingsNavigationState(SettingsVM.SelectedPageIndex, SettingsVM.SearchQuery),
        LibraryNavIndex => LibraryVM.CaptureNavigationState(),
        DiscoverNavIndex => new DiscoverNavigationState(DiscoverVM.Filter, DiscoverVM.SourceKey,
            DiscoverVM.OpenCard, DiscoverVM.SearchQuery),
        _ => new NavigationState(SelectedNavIndex),
    };

    private bool IsNavigationAvailable(NavigationState state) => state switch
    {
        ChatNavigationState { ChatId: { } id } => _dataStore.Data.Chats.Any(chat => chat.Id == id),
        JobNavigationState { JobId: { } id } => _dataStore.Data.BackgroundJobs.Any(job => job.Id == id),
        ManagementNavigationState { ExternalItemKey: { } key } management =>
            ExternalForPage(management.PageIndex)?.ContainsNavigationItem(key) == true,
        ManagementNavigationState { ItemId: { } id } management => management.PageIndex switch
        {
            2 => _dataStore.Data.Projects.Any(project => project.Id == id),
            3 => _dataStore.Data.Skills.Any(skill => skill.Id == id),
            4 => _dataStore.Data.Agents.Any(agent => agent.Id == id),
            MemoriesNavIndex => _dataStore.Data.Memories.Any(memory => memory.Id == id
                && string.Equals(memory.Status, MemoryStatuses.Active, StringComparison.OrdinalIgnoreCase)),
            6 => _dataStore.Data.McpServers.Any(server => server.Id == id),
            _ => false,
        },
        LibraryNavigationState { ItemKey: { } key } => LibraryVM.ContainsNavigationItem(key),
        _ => true,
    };

    private ExternalCapabilitiesViewModel? ExternalForPage(int pageIndex) => pageIndex switch
    {
        3 => SkillsVM.External,
        4 => AgentsVM.External,
        6 => McpServersVM.External,
        _ => null,
    };

    private async Task<bool> ApplyNavigationEntryAsync(NavigationState state)
    {
        if (state is ChatNavigationState chat)
            return await ApplyChatNavigationEntryAsync(chat);

        // Set the Settings subpage first, otherwise its "reopen Memories" behavior redirects
        // a history entry for a different Settings page.
        if (state is SettingsNavigationState settings)
        {
            SettingsVM.SelectedPageIndex = settings.SettingsPageIndex;
            SettingsVM.SearchQuery = settings.SearchQuery;
        }

        SelectedNavIndex = state.PageIndex;

        switch (state)
        {
            case ManagementNavigationState management:
                RestoreManagementNavigation(management);
                break;
            case DiscoverNavigationState discover:
                DiscoverVM.RestoreNavigation(discover);
                break;
            case LibraryNavigationState library:
                LibraryVM.RestoreNavigation(library);
                break;
            case JobNavigationState job:
                if (job.JobId is { } id)
                    JobsVM.SelectedJob = _dataStore.Data.BackgroundJobs.First(candidate => candidate.Id == id);
                else if (job.IsEditing && (JobsVM.SelectedJob is not null || !JobsVM.IsEditing))
                    JobsVM.NewJobCommand.Execute(null);
                else if (!job.IsEditing)
                    JobsVM.SelectedJob = null;
                JobsVM.IsEditing = job.IsEditing;
                break;
        }

        return true;
    }

    private void RestoreManagementNavigation(ManagementNavigationState state)
    {
        var external = ExternalForPage(state.PageIndex);
        if (state.ExternalItemKey is { } key)
        {
            external!.OpenNavigationItem(key);
            return;
        }

        external?.Close();
        switch (state.PageIndex)
        {
            case 2:
                RestoreEditor(state, ProjectsVM.SelectedProject?.Id, ProjectsVM.IsEditing,
                    ProjectsVM.OpenById, ProjectsVM.NewProjectCommand, ProjectsVM.CloseDetailCommand);
                break;
            case 3:
                RestoreEditor(state, SkillsVM.SelectedSkill?.Id, SkillsVM.IsEditing,
                    SkillsVM.OpenById, SkillsVM.NewSkillCommand, SkillsVM.CloseDetailCommand);
                break;
            case 4:
                RestoreEditor(state, AgentsVM.SelectedAgent?.Id, AgentsVM.IsEditing,
                    AgentsVM.OpenById, AgentsVM.NewAgentCommand, AgentsVM.CloseDetailCommand);
                break;
            case MemoriesNavIndex:
                MemoriesVM.CategoryFilter = state.CategoryFilter;
                RestoreEditor(state, MemoriesVM.SelectedMemory?.Id, MemoriesVM.IsEditing,
                    id => MemoriesVM.SelectedMemory = _dataStore.Data.Memories.First(memory => memory.Id == id),
                    MemoriesVM.NewMemoryCommand, MemoriesVM.CloseDetailCommand);
                break;
            case 6:
                RestoreEditor(state, McpServersVM.SelectedServer?.Id, McpServersVM.IsEditing,
                    McpServersVM.OpenById, McpServersVM.NewServerCommand, McpServersVM.CloseDetailCommand);
                break;
        }
    }

    private static void RestoreEditor(
        ManagementNavigationState state,
        Guid? currentId,
        bool isEditing,
        Action<Guid> openById,
        IRelayCommand create,
        IRelayCommand close)
    {
        if (state.ItemId == currentId && state.IsEditing == isEditing)
            return;

        if (!state.IsEditing)
            close.Execute(null);
        else if (state.ItemId is { } id)
            openById(id);
        else
            create.Execute(null);
    }

    public async Task<bool> TryNavigateHistoryAsync(int direction)
    {
        FlushNavigationRecord();
        try
        {
            return await _navigationHistory.TryNavigateAsync(direction, IsNavigationAvailable, ApplyNavigationEntryAsync);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [RelayCommand]
    private Task NavigateBack() => TryNavigateHistoryAsync(-1);

    [RelayCommand]
    private Task NavigateForward() => TryNavigateHistoryAsync(1);
}
