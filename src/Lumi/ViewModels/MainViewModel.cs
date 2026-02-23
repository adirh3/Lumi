using System;
using System.Collections.ObjectModel;
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
    private readonly BrowserService _browserService;

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
    [ObservableProperty] private string _chatSearchQuery = "";
    [ObservableProperty] private Guid? _activeChatId;

    // Sub-ViewModels
    public ChatViewModel ChatVM { get; }
    public SkillsViewModel SkillsVM { get; }
    public AgentsViewModel AgentsVM { get; }
    public ProjectsViewModel ProjectsVM { get; }
    public MemoriesViewModel MemoriesVM { get; }
    public McpServersViewModel McpServersVM { get; }
    public SettingsViewModel SettingsVM { get; }

    /// <summary>The shared browser service for embedded WebView2 automation.</summary>
    public BrowserService BrowserService => _browserService;

    /// <summary>The application data store.</summary>
    public DataStore DataStore => _dataStore;

    // Grouped chat list for sidebar
    public ObservableCollection<ChatGroup> ChatGroups { get; } = [];

    // Project list for filter
    public ObservableCollection<Project> Projects { get; } = [];

    public MainViewModel(DataStore dataStore, CopilotService copilotService)
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
        _browserService = new BrowserService();

        var settings = _dataStore.Data.Settings;
        _isDarkTheme = settings.IsDarkTheme;
        _isCompactDensity = settings.IsCompactDensity;
        _userName = settings.UserName ?? "";
        _isOnboarded = settings.IsOnboarded;

        ChatVM = new ChatViewModel(dataStore, copilotService, _browserService);
        SkillsVM = new SkillsViewModel(dataStore);
        AgentsVM = new AgentsViewModel(dataStore);
        ProjectsVM = new ProjectsViewModel(dataStore);
        MemoriesVM = new MemoriesViewModel(dataStore);
        McpServersVM = new McpServersViewModel(dataStore);
        SettingsVM = new SettingsViewModel(dataStore, copilotService);

        // Sync settings changes back to MainViewModel
        SettingsVM.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.IsDarkTheme))
                IsDarkTheme = SettingsVM.IsDarkTheme;
            else if (args.PropertyName == nameof(SettingsViewModel.IsCompactDensity))
                IsCompactDensity = SettingsVM.IsCompactDensity;
            else if (args.PropertyName == nameof(SettingsViewModel.PreferredModel)
                     && !string.IsNullOrWhiteSpace(SettingsVM.PreferredModel))
                ChatVM.SelectedModel = SettingsVM.PreferredModel;
            else if (args.PropertyName == nameof(SettingsViewModel.UserName))
                UserName = SettingsVM.UserName;
        };

        SettingsVM.SettingsChanged += () =>
        {
            RefreshChatList();
        };

        ChatVM.ChatUpdated += () => RefreshChatList();
        ChatVM.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatViewModel.CurrentChat))
                ActiveChatId = ChatVM.CurrentChat?.Id;
        };

        ProjectsVM.ProjectsChanged += () =>
        {
            LoadProjects();
            RefreshChatList();
        };

        McpServersVM.McpConfigChanged += () =>
        {
            ChatVM.InvalidateMcpSession();
            ChatVM.PopulateDefaultMcps();
        };

        LoadProjects();
        RefreshChatList();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            IsConnecting = true;
            ConnectionStatus = Loc.Status_Connecting;
            await _copilotService.ConnectAsync();
            IsConnected = true;
            ConnectionStatus = Loc.Status_Connected;

            // Check GitHub auth status
            await SettingsVM.RefreshAuthStatusAsync();

            var models = await _copilotService.GetModelsAsync();
            var modelIds = models.Select(m => m.Id).ToList();

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
        }
        catch (Exception ex)
        {
            ConnectionStatus = string.Format(Loc.Status_ConnectionFailed, ex.Message);
            IsConnected = false;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private void LoadProjects()
    {
        Projects.Clear();
        foreach (var p in _dataStore.Data.Projects.OrderBy(p => p.Name))
            Projects.Add(p);
    }

    public void RefreshChatList()
    {
        var query = ChatSearchQuery?.Trim();
        var chats = _dataStore.Data.Chats.AsEnumerable();

        // Filter by project
        if (SelectedProjectFilter.HasValue)
            chats = chats.Where(c => c.ProjectId == SelectedProjectFilter.Value);

        // Filter by search
        if (!string.IsNullOrEmpty(query))
            chats = chats.Where(c => c.Title.Contains(query, StringComparison.OrdinalIgnoreCase));

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

    [RelayCommand]
    private void NewChat()
    {
        // If the current chat is empty (no messages), just reuse it
        if (ChatVM.CurrentChat is not null && ChatVM.CurrentChat.Messages.Count == 0)
        {
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

    [RelayCommand]
    private async Task OpenChat(Chat chat)
    {
        await ChatVM.LoadChatAsync(chat);
        SelectedNavIndex = 0;
    }

    [RelayCommand]
    private void DeleteChat(Chat chat)
    {
        ChatVM.CleanupSession(chat.Id);
        _dataStore.Data.Chats.Remove(chat);
        _dataStore.DeleteChatFile(chat.Id);
        _ = _dataStore.SaveAsync();
        RefreshChatList();

        if (ChatVM.CurrentChat?.Id == chat.Id)
            ChatVM.ClearChat();
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
    }

    [RelayCommand]
    private void SelectProjectFilter(Project project)
    {
        SelectedProjectFilter = project.Id;
    }

    [RelayCommand]
    private void AssignChatToProject(object? parameter)
    {
        // parameter is a two-element array: [Chat, Project]
        if (parameter is object[] args && args.Length == 2 && args[0] is Chat chat && args[1] is Project project)
        {
            chat.ProjectId = project.Id;
            chat.UpdatedAt = DateTimeOffset.Now;
            _ = _dataStore.SaveAsync();
            RefreshChatList();
        }
    }

    [RelayCommand]
    private void RemoveChatFromProject(Chat? chat)
    {
        if (chat is null) return;
        chat.ProjectId = null;
        chat.UpdatedAt = DateTimeOffset.Now;
        _ = _dataStore.SaveAsync();
        RefreshChatList();
    }

    [RelayCommand]
    private async Task OpenChatFromProject(Chat chat)
    {
        await ChatVM.LoadChatAsync(chat);
        SelectedNavIndex = 0;
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

    partial void OnChatSearchQueryChanged(string value) => RefreshChatList();

    partial void OnSelectedProjectFilterChanged(Guid? value) => RefreshChatList();

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
