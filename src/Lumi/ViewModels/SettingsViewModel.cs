using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Services;

namespace Lumi.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly DataStore _dataStore;
    private readonly CopilotService _copilotService;

    // ── Page navigation ──
    [ObservableProperty] private int _selectedPageIndex;

    // ── Search ──
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string _searchResultSummary = "";

    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchQuery);

    partial void OnSearchQueryChanged(string value)
    {
        OnPropertyChanged(nameof(IsSearching));
    }

    partial void OnSelectedPageIndexChanged(int value)
    {
        if (IsSearching)
            SearchQuery = "";
    }

    public ObservableCollection<string> Pages { get; } =
    [
        Loc.Settings_General,
        Loc.Settings_Appearance,
        Loc.Settings_Chat,
        Loc.Settings_AIModels,
        Loc.Settings_PrivacyData,
        Loc.Settings_About
    ];

    // ── General ──
    [ObservableProperty] private string _userName;
    [ObservableProperty] private bool _launchAtStartup;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private string _globalHotkey;
    [ObservableProperty] private bool _notificationsEnabled;

    // ── Appearance ──
    [ObservableProperty] private bool _isDarkTheme;
    [ObservableProperty] private bool _isCompactDensity;
    [ObservableProperty] private int _fontSize;
    [ObservableProperty] private bool _showAnimations;

    // ── Chat ──
    [ObservableProperty] private bool _sendWithEnter;
    [ObservableProperty] private bool _showTimestamps;
    [ObservableProperty] private bool _showToolCalls;
    [ObservableProperty] private bool _showReasoning;
    [ObservableProperty] private bool _autoGenerateTitles;
    [ObservableProperty] private int _maxContextMessages;

    // ── AI & Models ──
    [ObservableProperty] private string _preferredModel;
    public ObservableCollection<string> AvailableModels { get; } = [];

    // ── GitHub Account ──
    [ObservableProperty] private bool _isAuthenticated;
    [ObservableProperty] private string _gitHubLogin = "";
    [ObservableProperty] private bool _isSigningIn;

    // ── Privacy & Data ──
    [ObservableProperty] private bool _enableMemoryAutoSave;
    [ObservableProperty] private bool _autoSaveChats;

    // ── Language ──
    public ObservableCollection<string> LanguageOptions { get; } = new(
        Loc.AvailableLanguages.Select(l => $"{l.DisplayName} ({l.Code})"));

    [ObservableProperty] private string _selectedLanguage;

    partial void OnSelectedLanguageChanged(string value)
    {
        var code = Loc.AvailableLanguages
            .FirstOrDefault(l => $"{l.DisplayName} ({l.Code})" == value).Code;
        if (code is not null && code != _dataStore.Data.Settings.Language)
        {
            _dataStore.Data.Settings.Language = code;
            Save();
            NeedsRestart = true;
            OnPropertyChanged(nameof(NeedsRestart));
        }
    }

    // ── About (read-only) ──
    public string AppVersion => "0.1.0";
    public string DotNetVersion => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
    public string OsVersion => System.Runtime.InteropServices.RuntimeInformation.OSDescription;

    /// <summary>Raised when a setting that affects other ViewModels changes.</summary>
    public event Action? SettingsChanged;

    public SettingsViewModel(DataStore dataStore, CopilotService copilotService)
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
        var s = dataStore.Data.Settings;

        // General
        _userName = s.UserName ?? "";
        _launchAtStartup = s.LaunchAtStartup;
        _startMinimized = s.StartMinimized;
        _minimizeToTray = s.MinimizeToTray;
        _globalHotkey = s.GlobalHotkey;
        _notificationsEnabled = s.NotificationsEnabled;

        // Appearance
        _isDarkTheme = s.IsDarkTheme;
        _isCompactDensity = s.IsCompactDensity;
        _fontSize = s.FontSize;
        _showAnimations = s.ShowAnimations;

        // Chat
        _sendWithEnter = s.SendWithEnter;
        _showTimestamps = s.ShowTimestamps;
        _showToolCalls = s.ShowToolCalls;
        _showReasoning = s.ShowReasoning;
        _autoGenerateTitles = s.AutoGenerateTitles;
        _maxContextMessages = s.MaxContextMessages;

        // AI
        _preferredModel = s.PreferredModel;
        if (!string.IsNullOrWhiteSpace(_preferredModel))
            AvailableModels.Add(_preferredModel);

        // Privacy
        _enableMemoryAutoSave = s.EnableMemoryAutoSave;
        _autoSaveChats = s.AutoSaveChats;

        // Language
        var langEntry = Loc.AvailableLanguages.FirstOrDefault(l => l.Code == s.Language);
        _selectedLanguage = langEntry.Code is not null
            ? $"{langEntry.DisplayName} ({langEntry.Code})"
            : $"English (en)";
    }

    // ── Auto-save on every property change + notify IsModified ──

    partial void OnUserNameChanged(string value) { _dataStore.Data.Settings.UserName = value.Trim(); Save(); }
    partial void OnLaunchAtStartupChanged(bool value) { _dataStore.Data.Settings.LaunchAtStartup = value; Save(); Views.MainWindow.ApplyLaunchAtStartup(value); NotifyModified(); }
    partial void OnStartMinimizedChanged(bool value) { _dataStore.Data.Settings.StartMinimized = value; Save(); NotifyModified(); }
    partial void OnMinimizeToTrayChanged(bool value)
    {
        _dataStore.Data.Settings.MinimizeToTray = value;
        Save();
        NotifyModified();
        if (Avalonia.Application.Current is App app)
            app.SetupTrayIcon(value);
    }
    partial void OnGlobalHotkeyChanged(string value)
    {
        _dataStore.Data.Settings.GlobalHotkey = value;
        Save();
        NotifyModified();
        if (Avalonia.Application.Current is App app)
            app.UpdateGlobalHotkey(value);
    }
    partial void OnNotificationsEnabledChanged(bool value) { _dataStore.Data.Settings.NotificationsEnabled = value; Save(); NotifyModified(); }

    partial void OnIsDarkThemeChanged(bool value) { _dataStore.Data.Settings.IsDarkTheme = value; Save(); SettingsChanged?.Invoke(); NotifyModified(); }
    partial void OnIsCompactDensityChanged(bool value) { _dataStore.Data.Settings.IsCompactDensity = value; Save(); SettingsChanged?.Invoke(); NotifyModified(); }
    partial void OnFontSizeChanged(int value) { _dataStore.Data.Settings.FontSize = value; Save(); SettingsChanged?.Invoke(); NotifyModified(); }
    partial void OnShowAnimationsChanged(bool value) { _dataStore.Data.Settings.ShowAnimations = value; Save(); SettingsChanged?.Invoke(); NotifyModified(); NeedsRestart = true; }

    partial void OnSendWithEnterChanged(bool value) { _dataStore.Data.Settings.SendWithEnter = value; Save(); SettingsChanged?.Invoke(); NotifyModified(); }
    partial void OnShowTimestampsChanged(bool value) { _dataStore.Data.Settings.ShowTimestamps = value; Save(); SettingsChanged?.Invoke(); NotifyModified(); }
    partial void OnShowToolCallsChanged(bool value) { _dataStore.Data.Settings.ShowToolCalls = value; Save(); SettingsChanged?.Invoke(); NotifyModified(); }
    partial void OnShowReasoningChanged(bool value) { _dataStore.Data.Settings.ShowReasoning = value; Save(); SettingsChanged?.Invoke(); NotifyModified(); }
    partial void OnAutoGenerateTitlesChanged(bool value) { _dataStore.Data.Settings.AutoGenerateTitles = value; Save(); NotifyModified(); }
    partial void OnMaxContextMessagesChanged(int value) { _dataStore.Data.Settings.MaxContextMessages = value; Save(); SettingsChanged?.Invoke(); NotifyModified(); }

    partial void OnPreferredModelChanged(string value) { _dataStore.Data.Settings.PreferredModel = value; Save(); SettingsChanged?.Invoke(); NotifyModified(); }

    public void UpdateAvailableModels(System.Collections.Generic.List<string> models)
    {
        AvailableModels.Clear();
        foreach (var m in models)
            AvailableModels.Add(m);
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        IsSigningIn = true;
        try
        {
            await _copilotService.SignInAsync();
            await RefreshAuthStatusAsync();
        }
        catch { /* login cancelled or failed */ }
        finally { IsSigningIn = false; }
    }

    public async Task RefreshAuthStatusAsync()
    {
        try
        {
            var status = await _copilotService.GetAuthStatusAsync();
            IsAuthenticated = status.IsAuthenticated == true;
            GitHubLogin = status.Login ?? "";
        }
        catch
        {
            IsAuthenticated = false;
            GitHubLogin = "";
        }
    }

    partial void OnEnableMemoryAutoSaveChanged(bool value) { _dataStore.Data.Settings.EnableMemoryAutoSave = value; Save(); SettingsChanged?.Invoke(); NotifyModified(); }
    partial void OnAutoSaveChatsChanged(bool value) { _dataStore.Data.Settings.AutoSaveChats = value; Save(); SettingsChanged?.Invoke(); NotifyModified(); }

    // ── IsModified properties (compare to defaults) ──
    private static readonly Models.UserSettings _defaults = new();

    public bool IsLaunchAtStartupModified => LaunchAtStartup != _defaults.LaunchAtStartup;
    public bool IsStartMinimizedModified => StartMinimized != _defaults.StartMinimized;
    public bool IsMinimizeToTrayModified => MinimizeToTray != _defaults.MinimizeToTray;
    public bool IsGlobalHotkeyModified => GlobalHotkey != _defaults.GlobalHotkey;
    public bool IsNotificationsEnabledModified => NotificationsEnabled != _defaults.NotificationsEnabled;
    public bool IsDarkThemeModified => IsDarkTheme != _defaults.IsDarkTheme;
    public bool IsCompactDensityModified => IsCompactDensity != _defaults.IsCompactDensity;
    public bool IsFontSizeModified => FontSize != _defaults.FontSize;
    public bool IsShowAnimationsModified => ShowAnimations != _defaults.ShowAnimations;
    public bool IsSendWithEnterModified => SendWithEnter != _defaults.SendWithEnter;
    public bool IsShowTimestampsModified => ShowTimestamps != _defaults.ShowTimestamps;
    public bool IsShowToolCallsModified => ShowToolCalls != _defaults.ShowToolCalls;
    public bool IsShowReasoningModified => ShowReasoning != _defaults.ShowReasoning;
    public bool IsAutoGenerateTitlesModified => AutoGenerateTitles != _defaults.AutoGenerateTitles;
    public bool IsMaxContextMessagesModified => MaxContextMessages != _defaults.MaxContextMessages;
    public bool IsPreferredModelModified => PreferredModel != _defaults.PreferredModel;
    public bool IsEnableMemoryAutoSaveModified => EnableMemoryAutoSave != _defaults.EnableMemoryAutoSave;
    public bool IsAutoSaveChatsModified => AutoSaveChats != _defaults.AutoSaveChats;

    private void NotifyModified()
    {
        OnPropertyChanged(nameof(IsLaunchAtStartupModified));
        OnPropertyChanged(nameof(IsStartMinimizedModified));
        OnPropertyChanged(nameof(IsMinimizeToTrayModified));
        OnPropertyChanged(nameof(IsGlobalHotkeyModified));
        OnPropertyChanged(nameof(IsNotificationsEnabledModified));
        OnPropertyChanged(nameof(IsDarkThemeModified));
        OnPropertyChanged(nameof(IsCompactDensityModified));
        OnPropertyChanged(nameof(IsFontSizeModified));
        OnPropertyChanged(nameof(IsShowAnimationsModified));
        OnPropertyChanged(nameof(IsSendWithEnterModified));
        OnPropertyChanged(nameof(IsShowTimestampsModified));
        OnPropertyChanged(nameof(IsShowToolCallsModified));
        OnPropertyChanged(nameof(IsShowReasoningModified));
        OnPropertyChanged(nameof(IsAutoGenerateTitlesModified));
        OnPropertyChanged(nameof(IsMaxContextMessagesModified));
        OnPropertyChanged(nameof(IsPreferredModelModified));
        OnPropertyChanged(nameof(IsEnableMemoryAutoSaveModified));
        OnPropertyChanged(nameof(IsAutoSaveChatsModified));
    }

    // ── Revert commands ──
    [RelayCommand] private void RevertLaunchAtStartup() => LaunchAtStartup = _defaults.LaunchAtStartup;
    [RelayCommand] private void RevertStartMinimized() => StartMinimized = _defaults.StartMinimized;
    [RelayCommand] private void RevertMinimizeToTray() => MinimizeToTray = _defaults.MinimizeToTray;
    [RelayCommand] private void RevertGlobalHotkey() => GlobalHotkey = _defaults.GlobalHotkey;
    [RelayCommand] private void RevertNotificationsEnabled() => NotificationsEnabled = _defaults.NotificationsEnabled;
    [RelayCommand] private void RevertIsDarkTheme() => IsDarkTheme = _defaults.IsDarkTheme;
    [RelayCommand] private void RevertIsCompactDensity() => IsCompactDensity = _defaults.IsCompactDensity;
    [RelayCommand] private void RevertFontSize() => FontSize = _defaults.FontSize;
    [RelayCommand] private void RevertShowAnimations() => ShowAnimations = _defaults.ShowAnimations;
    [RelayCommand] private void RevertSendWithEnter() => SendWithEnter = _defaults.SendWithEnter;
    [RelayCommand] private void RevertShowTimestamps() => ShowTimestamps = _defaults.ShowTimestamps;
    [RelayCommand] private void RevertShowToolCalls() => ShowToolCalls = _defaults.ShowToolCalls;
    [RelayCommand] private void RevertShowReasoning() => ShowReasoning = _defaults.ShowReasoning;
    [RelayCommand] private void RevertAutoGenerateTitles() => AutoGenerateTitles = _defaults.AutoGenerateTitles;
    [RelayCommand] private void RevertMaxContextMessages() => MaxContextMessages = _defaults.MaxContextMessages;
    [RelayCommand] private void RevertPreferredModel() => PreferredModel = _defaults.PreferredModel;
    [RelayCommand] private void RevertEnableMemoryAutoSave() => EnableMemoryAutoSave = _defaults.EnableMemoryAutoSave;
    [RelayCommand] private void RevertAutoSaveChats() => AutoSaveChats = _defaults.AutoSaveChats;

    // ── Restart indicator ──
    [ObservableProperty] private bool _needsRestart;

    [RelayCommand]
    private void RestartApp()
    {
        var exePath = System.Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            System.Diagnostics.Process.Start(exePath);
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
    }

    private void Save() => _dataStore.Save();

    [RelayCommand]
    private void ClearAllChats()
    {
        _dataStore.Data.Chats.Clear();
        Save();
        SettingsChanged?.Invoke();
    }

    [RelayCommand]
    private void ClearAllMemories()
    {
        _dataStore.Data.Memories.Clear();
        Save();
        SettingsChanged?.Invoke();
    }

    [RelayCommand]
    private void ResetSettings()
    {
        var defaults = new Models.UserSettings
        {
            UserName = _dataStore.Data.Settings.UserName,
            UserSex = _dataStore.Data.Settings.UserSex,
            IsOnboarded = _dataStore.Data.Settings.IsOnboarded,
            DefaultsSeeded = _dataStore.Data.Settings.DefaultsSeeded
        };

        // Apply defaults to this VM
        LaunchAtStartup = defaults.LaunchAtStartup;
        StartMinimized = defaults.StartMinimized;
        MinimizeToTray = defaults.MinimizeToTray;
        GlobalHotkey = defaults.GlobalHotkey;
        NotificationsEnabled = defaults.NotificationsEnabled;
        IsDarkTheme = defaults.IsDarkTheme;
        IsCompactDensity = defaults.IsCompactDensity;
        FontSize = defaults.FontSize;
        ShowAnimations = defaults.ShowAnimations;
        SendWithEnter = defaults.SendWithEnter;
        ShowTimestamps = defaults.ShowTimestamps;
        ShowToolCalls = defaults.ShowToolCalls;
        ShowReasoning = defaults.ShowReasoning;
        AutoGenerateTitles = defaults.AutoGenerateTitles;
        MaxContextMessages = defaults.MaxContextMessages;
        PreferredModel = defaults.PreferredModel;
        EnableMemoryAutoSave = defaults.EnableMemoryAutoSave;
        AutoSaveChats = defaults.AutoSaveChats;
        NeedsRestart = false;
    }

    /// <summary>
    /// Stats for About page.
    /// </summary>
    public int TotalChats => _dataStore.Data.Chats.Count;
    public int TotalMemories => _dataStore.Data.Memories.Count;
    public int TotalSkills => _dataStore.Data.Skills.Count;
    public int TotalAgents => _dataStore.Data.Agents.Count;
    public int TotalProjects => _dataStore.Data.Projects.Count;

    public void RefreshStats()
    {
        OnPropertyChanged(nameof(TotalChats));
        OnPropertyChanged(nameof(TotalMemories));
        OnPropertyChanged(nameof(TotalSkills));
        OnPropertyChanged(nameof(TotalAgents));
        OnPropertyChanged(nameof(TotalProjects));
    }
}
