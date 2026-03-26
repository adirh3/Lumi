using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

public class ChatGroup
{
    public string Label { get; set; } = "";
    public ObservableCollection<Chat> Chats { get; set; } = [];
}

public partial class MainViewModel : ObservableObject
{
    private readonly DataStore _dataStore;
    private readonly CopilotService _copilotService;
    /// <summary>A dedicated BrowserService for Settings cookie import/clear (not tied to any chat).</summary>
    private readonly BrowserService _settingsBrowserService;
    private bool _isRefreshingCopilotState;

    [ObservableProperty] private int _selectedNavIndex;
    [ObservableProperty] private bool _isDarkTheme = true;
    [ObservableProperty] private bool _isCompactDensity;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _connectionStatus = Loc.Status_Disconnected;
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private bool _isOnboarded;
    [ObservableProperty] private string _onboardingName = "";
    [ObservableProperty] private int _onboardingSexIndex; // 0=Male, 1=Female, 2=Prefer not to say
    [ObservableProperty] private int _onboardingLanguageIndex; // index into Loc.AvailableLanguages
    [ObservableProperty] private Guid? _selectedProjectFilter;
    [ObservableProperty] private bool _isSidebarCollapsed;

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    [ObservableProperty] private Guid? _activeChatId;

    // Sub-ViewModels
    public ChatViewModel ChatVM { get; }
    public SkillsViewModel SkillsVM { get; }
    public AgentsViewModel AgentsVM { get; }
    public ProjectsViewModel ProjectsVM { get; }
    public MemoriesViewModel MemoriesVM { get; }
    public McpServersViewModel McpServersVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public OnboardingViewModel OnboardingVM { get; }
    public SearchOverlayViewModel SearchOverlayVM { get; }
    public GitHubLoginViewModel LoginVM { get; }

    /// <summary>The browser service used for Settings cookie import/clear.</summary>
    public BrowserService SettingsBrowserService => _settingsBrowserService;

    /// <summary>The application data store.</summary>
    public DataStore DataStore => _dataStore;

    // Grouped chat list for sidebar
    public ObservableCollection<ChatGroup> ChatGroups { get; } = [];

    // Project list for filter
    public ObservableCollection<Project> Projects { get; } = [];

    public MainViewModel(DataStore dataStore, CopilotService copilotService, bool forceOnboarding = false)
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
        _settingsBrowserService = new BrowserService();

        var settings = _dataStore.Data.Settings;
        _isDarkTheme = settings.IsDarkTheme;
        _isCompactDensity = settings.IsCompactDensity;
        _userName = settings.UserName ?? "";
        _isOnboarded = settings.IsOnboarded && !forceOnboarding;

        // Shared GitHub login ViewModel
        LoginVM = new GitHubLoginViewModel(copilotService);

        // Onboarding ViewModel — available even if already onboarded (for --onboarding flag)
        OnboardingVM = new OnboardingViewModel(dataStore, copilotService);
        OnboardingVM.LoginVM = LoginVM;
        OnboardingVM.OnboardingCompleted += () =>
        {
            UserName = OnboardingVM.UserName;
            IsDarkTheme = OnboardingVM.IsDarkTheme;
            IsOnboarded = true;

            // Sync GitHub auth state if user signed in during onboarding
            if (LoginVM.IsAuthenticated)
                _ = SettingsVM.RefreshAuthStatusAsync();

            // Refresh memories in case learning created some
            ChatVM.RefreshComposerCatalogs();
        };
        OnboardingVM.ThemeChanged += isDark => IsDarkTheme = isDark;

        ChatVM = new ChatViewModel(dataStore, copilotService);
        SkillsVM = new SkillsViewModel(dataStore);
        AgentsVM = new AgentsViewModel(dataStore);
        ProjectsVM = new ProjectsViewModel(dataStore);
        MemoriesVM = new MemoriesViewModel(dataStore);
        McpServersVM = new McpServersViewModel(dataStore);
        SettingsVM = new SettingsViewModel(dataStore, copilotService, _settingsBrowserService);
        SettingsVM.LoginVM = LoginVM;
        SearchOverlayVM = new SearchOverlayViewModel(dataStore, () => SelectedNavIndex);

        // When the chat model selector changes the global default, sync it to SettingsVM
        ChatVM.DefaultModelChanged += model =>
        {
            if (SettingsVM.PreferredModel != model)
                SettingsVM.PreferredModel = model;
        };

        // Sync settings changes back to MainViewModel
        SettingsVM.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.IsDarkTheme))
                IsDarkTheme = SettingsVM.IsDarkTheme;
            else if (args.PropertyName == nameof(SettingsViewModel.IsCompactDensity))
                IsCompactDensity = SettingsVM.IsCompactDensity;
            else if (args.PropertyName == nameof(SettingsViewModel.PreferredModel)
                     && !string.IsNullOrWhiteSpace(SettingsVM.PreferredModel)
                     && (ChatVM.CurrentChat is null || ChatVM.CurrentChat.Messages.Count == 0))
                ChatVM.RestoreDefaultModelSelection();
            else if (args.PropertyName == nameof(SettingsViewModel.SendWithEnter))
                ChatVM.SendWithEnter = SettingsVM.SendWithEnter;
            else if (args.PropertyName is nameof(SettingsViewModel.ShowTimestamps)
                     or nameof(SettingsViewModel.ShowToolCalls)
                     or nameof(SettingsViewModel.ShowReasoning)
                     or nameof(SettingsViewModel.ExpandReasoningWhileStreaming))
                ChatVM.RebuildTranscript();
            else if (args.PropertyName == nameof(SettingsViewModel.IsAuthenticated))
            {
                if (SettingsVM.IsAuthenticated)
                    _ = RefreshCopilotStateAsync(refreshAuthStatus: false);
                else if (!_isRefreshingCopilotState && !IsConnecting)
                {
                    IsConnected = false;
                    ConnectionStatus = Loc.Status_Disconnected;
                }
            }
            else if (args.PropertyName == nameof(SettingsViewModel.UserName))
                UserName = SettingsVM.UserName;
        };

        SkillsVM.SkillsChanged += () => ChatVM.RefreshComposerCatalogs();
        AgentsVM.AgentsChanged += () => ChatVM.RefreshComposerCatalogs();

        SettingsVM.SettingsChanged += () =>
        {
            RefreshChatList();
        };

        ChatVM.ChatUpdated += () => { SubscribeChatRunningState(); RefreshChatList(); };
        ChatVM.ChatTitleChanged += OnChatTitleChanged;
        ChatVM.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatViewModel.CurrentChat))
                ActiveChatId = ChatVM.CurrentChat?.Id;
            else if (args.PropertyName == nameof(ChatViewModel.IsBusy))
                RefreshProjectRunningState();
        };

        ProjectsVM.ProjectsChanged += () =>
        {
            LoadProjects();
            RefreshChatList();
            ChatVM.RefreshComposerCatalogs();
        };

        McpServersVM.McpConfigChanged += () =>
        {
            ChatVM.InvalidateMcpSession();
            ChatVM.PopulateDefaultMcps();
            ChatVM.RefreshComposerCatalogs();
        };

        ChatVM.ComposerProjectFilterRequested += projectId =>
        {
            if (projectId == SelectedProjectFilter)
                return;

            if (!projectId.HasValue)
            {
                ClearProjectFilterCommand.Execute(null);
                return;
            }

            var project = _dataStore.Data.Projects.FirstOrDefault(p => p.Id == projectId.Value);
            if (project is not null)
                SelectProjectFilterCommand.Execute(project);
        };

        LoadProjects();
        SubscribeChatRunningState();
        RefreshChatList();
        ChatVM.RefreshComposerCatalogs();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await RefreshCopilotStateAsync(refreshAuthStatus: true);
    }

    private async Task RefreshCopilotStateAsync(bool refreshAuthStatus)
    {
        if (_isRefreshingCopilotState)
            return;

        try
        {
            _isRefreshingCopilotState = true;
            IsConnecting = true;
            ConnectionStatus = Loc.Status_Connecting;

            if (!_copilotService.IsConnected)
                await _copilotService.ConnectAsync();

            if (refreshAuthStatus)
                await SettingsVM.RefreshAuthStatusAsync();

            var models = await _copilotService.GetModelsAsync();
            var modelIds = models.Select(m => m.Id).ToList();

            IsConnected = true;
            ConnectionStatus = Loc.Status_Connected;

            // Auto-select best model on clean state (no user preference saved)
            var selected = ChatVM.SelectedModel;
            var isCleanState = string.IsNullOrWhiteSpace(selected)
                || !modelIds.Contains(selected);
            if (isCleanState)
                selected = ChatViewModel.PickBestModel(modelIds);

            ChatVM.AvailableModels.Clear();
            foreach (var id in modelIds)
                ChatVM.AvailableModels.Add(id);
            ChatVM.SelectedModel = selected;

            SettingsVM.UpdateAvailableModels(modelIds);
            if (isCleanState && selected is not null)
                SettingsVM.PreferredModel = selected;

            // Refresh account quota in background
            _ = ChatVM.RefreshQuotaAsync();

            // Refresh catalogs now that connection is established (discovers workspace agents)
            ChatVM.RefreshComposerCatalogs();
        }
        catch (Exception ex)
        {
            ConnectionStatus = string.Format(Loc.Status_ConnectionFailed, ex.Message);
            IsConnected = false;
        }
        finally
        {
            IsConnecting = false;
            _isRefreshingCopilotState = false;
        }
    }

    private void LoadProjects()
    {
        Projects.Clear();
        foreach (var p in _dataStore.Data.Projects.OrderBy(p => p.Name))
            Projects.Add(p);
    }

    private void SubscribeChatRunningState()
    {
        foreach (var chat in _dataStore.Data.Chats)
        {
            chat.PropertyChanged -= OnChatRunningChanged;
            chat.PropertyChanged += OnChatRunningChanged;
        }
    }

    private void OnChatRunningChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Chat.IsRunning))
            RefreshProjectRunningState();
    }

    /// <summary>Recalculates IsRunning for all projects based on current chat states.</summary>
    public void RefreshProjectRunningState()
    {
        var chats = _dataStore.Data.Chats;
        foreach (var project in Projects)
            project.IsRunning = chats.Any(c => c.ProjectId == project.Id && c.IsRunning);

        ProjectRunningStateChanged?.Invoke();
    }

    /// <summary>Fired when any project's IsRunning state may have changed.</summary>
    public event Action? ProjectRunningStateChanged;

    public event Action<Guid, string>? ChatTitleChanged;

    public void RefreshChatList()
    {
        var chats = _dataStore.Data.Chats.AsEnumerable();

        // Filter by project
        if (SelectedProjectFilter.HasValue)
            chats = chats.Where(c => c.ProjectId == SelectedProjectFilter.Value);

        var ordered = chats.OrderByDescending(c => c.UpdatedAt).Take(50).ToList();

        // Group by time period
        var now = DateTimeOffset.Now;
        var today = now.Date;
        var yesterday = today.AddDays(-1);
        var weekAgo = today.AddDays(-7);

        ChatGroups.Clear();

        var todayChats = ordered.Where(c => c.UpdatedAt.Date == today).ToList();
        var yesterdayChats = ordered.Where(c => c.UpdatedAt.Date == yesterday).ToList();
        var weekChats = ordered.Where(c => c.UpdatedAt.Date < yesterday && c.UpdatedAt.Date >= weekAgo).ToList();
        var olderChats = ordered.Where(c => c.UpdatedAt.Date < weekAgo).ToList();

        if (todayChats.Count > 0)
            ChatGroups.Add(new ChatGroup { Label = Loc.ChatGroup_Today, Chats = new(todayChats) });
        if (yesterdayChats.Count > 0)
            ChatGroups.Add(new ChatGroup { Label = Loc.ChatGroup_Yesterday, Chats = new(yesterdayChats) });
        if (weekChats.Count > 0)
            ChatGroups.Add(new ChatGroup { Label = Loc.ChatGroup_Previous7Days, Chats = new(weekChats) });
        if (olderChats.Count > 0)
            ChatGroups.Add(new ChatGroup { Label = Loc.ChatGroup_Older, Chats = new(olderChats) });
    }

    private void OnChatTitleChanged(Guid chatId, string newTitle)
    {
        // Update in-place without rebuilding the entire list
        ChatTitleChanged?.Invoke(chatId, newTitle);
    }

    [RelayCommand]
    private void NewChat()
    {
        // If the current chat is empty (no messages), just reuse it
        if (ChatVM.CurrentChat is not null && ChatVM.CurrentChat.Messages.Count == 0)
        {
            // Still update the project assignment if a filter is active
            if (SelectedProjectFilter.HasValue)
                ChatVM.SetProjectId(SelectedProjectFilter.Value);
            else if (ChatVM.CurrentChat.ProjectId.HasValue)
                ChatVM.ClearProjectId();

            SelectedNavIndex = 0;
            return;
        }

        ChatVM.ClearChat();

        // Auto-assign the active project filter to new chats
        if (SelectedProjectFilter.HasValue)
        {
            ChatVM.SetProjectId(SelectedProjectFilter.Value);
        }

        SelectedNavIndex = 0;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenChat(Chat chat)
    {
        try
        {
            await ChatVM.LoadChatAsync(chat);
            if (ChatVM.CurrentChat?.Id == chat.Id)
                SelectedNavIndex = 0;
        }
        catch (OperationCanceledException)
        {
            // A newer chat selection superseded this open request.
        }
    }

    [RelayCommand]
    private void DeleteChat(Chat chat)
    {
        // If the chat has a worktree, ask the user whether to clean it up
        if (chat.WorktreePath is { Length: > 0 } wt && Directory.Exists(wt))
        {
            _pendingDeleteChat = chat;
            IsWorktreeDeleteDialogOpen = true;
            return;
        }

        PerformDeleteChat(chat, removeWorktree: false);
    }

    // ── Worktree cleanup dialog ──

    private Chat? _pendingDeleteChat;
    [ObservableProperty] private bool _isWorktreeDeleteDialogOpen;

    [RelayCommand]
    private async Task ConfirmDeleteWithWorktree()
    {
        if (_pendingDeleteChat is not null)
        {
            var chat = _pendingDeleteChat;
            _pendingDeleteChat = null;
            IsWorktreeDeleteDialogOpen = false;
            PerformDeleteChat(chat, removeWorktree: true);

            // Clean up worktree + branch in background
            if (chat.WorktreePath is { Length: > 0 } wt)
            {
                var projectDir = GetProjectDirForChat(chat);
                if (projectDir is not null)
                    await GitService.RemoveWorktreeAsync(projectDir, wt);
            }
        }
    }

    [RelayCommand]
    private void ConfirmDeleteWithoutWorktree()
    {
        if (_pendingDeleteChat is not null)
        {
            var chat = _pendingDeleteChat;
            _pendingDeleteChat = null;
            IsWorktreeDeleteDialogOpen = false;
            PerformDeleteChat(chat, removeWorktree: false);
        }
    }

    [RelayCommand]
    private void CancelDeleteWorktreeDialog()
    {
        _pendingDeleteChat = null;
        IsWorktreeDeleteDialogOpen = false;
    }

    private void PerformDeleteChat(Chat chat, bool removeWorktree)
    {
        ChatVM.CleanupSession(chat.Id);
        _dataStore.Data.Chats.Remove(chat);
        _dataStore.MarkChatDeleted(chat.Id);
        _dataStore.DeleteChatFile(chat.Id);
        _ = _dataStore.SaveAsync();
        RefreshChatList();

        if (ChatVM.CurrentChat?.Id == chat.Id)
            ChatVM.ClearChat();
    }

    private string? GetProjectDirForChat(Chat chat)
    {
        if (chat.ProjectId.HasValue)
        {
            var project = _dataStore.Data.Projects.FirstOrDefault(p => p.Id == chat.ProjectId.Value);
            if (project?.WorkingDirectory is { Length: > 0 } dir)
                return dir;
        }
        return null;
    }

    [ObservableProperty] private Chat? _renamingChat;
    [ObservableProperty] private string _renamingTitle = "";

    [RelayCommand]
    private void StartRenameChat(Chat? chat)
    {
        if (chat is null) return;
        RenamingChat = chat;
        RenamingTitle = chat.Title;
    }

    [RelayCommand]
    private void CommitRenameChat()
    {
        if (RenamingChat is null) return;
        var newTitle = RenamingTitle?.Trim();
        if (!string.IsNullOrEmpty(newTitle))
        {
            RenamingChat.Title = newTitle;
            _dataStore.MarkChatChanged(RenamingChat);
            _ = _dataStore.SaveAsync();
            RefreshChatList();
        }
        RenamingChat = null;
        RenamingTitle = "";
    }

    [RelayCommand]
    private void CancelRenameChat()
    {
        RenamingChat = null;
        RenamingTitle = "";
    }

    [RelayCommand]
    private void SetNav(string indexStr)
    {
        if (int.TryParse(indexStr, out var idx))
            SelectedNavIndex = idx;
    }

    [RelayCommand]
    private void ClearProjectFilter()
    {
        SelectedProjectFilter = null;
        ChatVM.ActiveProjectFilterId = null;

        // Also clear draft/new-chat project context immediately, even if
        // SelectedProjectFilter was already null (no PropertyChanged event).
        if (ChatVM.CurrentChat is null || ChatVM.CurrentChat.Messages.Count == 0)
            ChatVM.ClearProjectId();
    }

    [RelayCommand]
    private void SelectProjectFilter(Project project)
    {
        SelectedProjectFilter = project.Id;
        ChatVM.ActiveProjectFilterId = project.Id;
    }

    [RelayCommand]
    private void AssignChatToProject(object? parameter)
    {
        // parameter is a two-element array: [Chat, Project]
        if (parameter is object[] args && args.Length == 2 && args[0] is Chat chat && args[1] is Project project)
        {
            chat.ProjectId = project.Id;
            _dataStore.MarkChatChanged(chat);
            _ = _dataStore.SaveAsync();
            RefreshChatList();
        }
    }

    [RelayCommand]
    private void RemoveChatFromProject(Chat? chat)
    {
        if (chat is null) return;
        chat.ProjectId = null;
        _dataStore.MarkChatChanged(chat);
        _ = _dataStore.SaveAsync();
        RefreshChatList();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenChatFromProject(Chat chat)
    {
        try
        {
            await ChatVM.LoadChatAsync(chat);
            if (ChatVM.CurrentChat?.Id == chat.Id)
                SelectedNavIndex = 0;
        }
        catch (OperationCanceledException)
        {
            // A newer chat selection superseded this open request.
        }
    }

    /// <summary>Returns the project name for a given project ID, or null.</summary>
    public string? GetProjectName(Guid? projectId)
    {
        if (!projectId.HasValue) return null;
        return _dataStore.Data.Projects.FirstOrDefault(p => p.Id == projectId.Value)?.Name;
    }

    public void RefreshProjects()
    {
        LoadProjects();
    }

    [RelayCommand]
    private async Task CompleteOnboarding()
    {
        if (string.IsNullOrWhiteSpace(OnboardingName)) return;

        var settings = _dataStore.Data.Settings;
        settings.UserName = OnboardingName.Trim();
        settings.UserSex = OnboardingSexIndex switch
        {
            0 => "male",
            1 => "female",
            _ => null
        };

        // Apply selected language
        var selectedLang = "en";
        if (OnboardingLanguageIndex >= 0 && OnboardingLanguageIndex < Loc.AvailableLanguages.Length)
        {
            selectedLang = Loc.AvailableLanguages[OnboardingLanguageIndex].Code;
            settings.Language = selectedLang;
        }

        settings.IsOnboarded = true;
        await _dataStore.SaveAsync();

        UserName = OnboardingName.Trim();
        IsOnboarded = true;

        // If a non-default language was selected, restart so the UI loads in that language
        if (selectedLang != "en")
        {
            SettingsVM.RestartAppCommand.Execute(null);
        }
    }

    partial void OnSelectedProjectFilterChanged(Guid? value)
    {
        RefreshChatList();
        ChatVM.ActiveProjectFilterId = value;

        // If the current chat already belongs to the target project, keep it.
        if (ChatVM.CurrentChat is not null
            && ChatVM.CurrentChat.Messages.Count > 0
            && ChatVM.CurrentChat.ProjectId == value)
            return;

        // If we're in a new/empty chat (draft), stay in new-chat mode —
        // just update the project assignment without navigating away.
        if (ChatVM.CurrentChat is null || ChatVM.CurrentChat.Messages.Count == 0)
        {
            if (value.HasValue)
                ChatVM.SetProjectId(value.Value);
            else
                ChatVM.ClearProjectId();
            return;
        }

        // Try to open the most recent chat in the new project.
        if (value.HasValue)
        {
            var recent = _dataStore.Data.Chats
                .Where(c => c.ProjectId == value.Value && c.Messages.Count > 0)
                .OrderByDescending(c => c.UpdatedAt)
                .FirstOrDefault();
            if (recent is not null)
            {
                _ = OpenChat(recent);
                return;
            }
        }

        // No existing chat for this project (or clearing filter) — start a new chat.
        NewChat();
    }

    partial void OnIsDarkThemeChanged(bool value)
    {
        _dataStore.Data.Settings.IsDarkTheme = value;
        _ = _dataStore.SaveAsync();
    }

    partial void OnIsCompactDensityChanged(bool value)
    {
        _dataStore.Data.Settings.IsCompactDensity = value;
        _ = _dataStore.SaveAsync();
    }
}
