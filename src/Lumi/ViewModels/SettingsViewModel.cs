using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

public sealed record ByokApiKeyModeOption(ByokApiKeyMode Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    public const int AboutPageIndex = 6;

    private readonly DataStore _dataStore;
    private readonly CopilotService _copilotService;
    private readonly BrowserService _browserService;
    private readonly UpdateService _updateService;
    /// <summary>OS credential store for BYOK CredentialStore mode. May be unsupported on this platform.</summary>
    private readonly Lumi.Services.Byok.ISecureKeyStore? _secureKeyStore;

    // ── Page navigation ──
    [ObservableProperty] private int _selectedPageIndex;

    // ── Search ──
    [ObservableProperty] private string _searchQuery = "";

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

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

        if (value == AboutPageIndex)
            ShouldAutoNavigateToUpdateCenter = false;
    }

    public ObservableCollection<string> Pages { get; } =
    [
        Loc.Settings_Profile,
        Loc.Settings_General,
        Loc.Settings_Appearance,
        Loc.Settings_Chat,
        Loc.Settings_AIModels,
        Loc.Settings_PrivacyData,
        Loc.Settings_About
    ];

    // ── General ──
    [ObservableProperty] private string _userName;
    [ObservableProperty] private int _userSexIndex; // 0=Male, 1=Female, 2=Prefer not to say
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
    [ObservableProperty] private bool _expandReasoningWhileStreaming;
    [ObservableProperty] private bool _autoGenerateTitles;

    // ── AI & Models ──
    [ObservableProperty] private string _preferredModel;
    [ObservableProperty] private string _reasoningEffort;
    [ObservableProperty] private string[]? _qualityLevels;
    [ObservableProperty] private string? _selectedQuality;
    [ObservableProperty] private string _contextWindowTier;
    [ObservableProperty] private string[]? _contextWindowTiers;
    [ObservableProperty] private string? _selectedContextWindowTier;
    private bool _suppressSelectedQualitySync;
    private bool _suppressSelectedContextWindowTierSync;
    private readonly Dictionary<string, List<string>> _modelReasoningEfforts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _modelDefaultEfforts = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _modelsWithLongContext = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<string> AvailableModels { get; } = [];

    // ── BYOK (Bring Your Own Key) ──
    public ObservableCollection<ByokEndpoint> ByokEndpoints { get; } = [];
    public ObservableCollection<ByokModel> ByokModels { get; } = [];

    [ObservableProperty] private ByokEndpoint? _selectedByokEndpoint;
    [ObservableProperty] private ByokModel? _selectedByokModel;

    [ObservableProperty] private string? _byokValidationMessage;
    [ObservableProperty] private bool _isByokValidationVisible;

    public static IReadOnlyList<string> ByokProviderTypes { get; } = ["openai", "azure", "anthropic"];
    public static IReadOnlyList<string> ByokWireApiOptions { get; } = ["completions", "responses"];

    public IReadOnlyList<ByokApiKeyModeOption> ByokApiKeyModeOptions { get; }

    public ByokApiKeyModeOption? SelectedByokEndpointApiKeyModeOption
        => ByokApiKeyModeOptions.FirstOrDefault(option => option.Value == SelectedByokEndpoint?.ApiKeyMode);

    public bool IsByokEndpointSelected => SelectedByokEndpoint is not null;
    public bool IsSelectedByokAzure => SelectedByokEndpoint?.ProviderType == "azure";

    /// <summary>
    /// Wire API format only applies to openai/azure providers; anthropic omits it.
    /// Drives the visibility of the API Format combo box in the BYOK editor.
    /// </summary>
    public bool IsSelectedByokWireApiVisible => SelectedByokEndpoint?.ProviderType != "anthropic";
    public bool UsesEnvVarApiKey => SelectedByokEndpoint?.ApiKeyMode == ByokApiKeyMode.EnvVar;
    public bool UsesStoredApiKey => SelectedByokEndpoint?.ApiKeyMode == ByokApiKeyMode.Stored;
    public bool UsesNoApiKey => SelectedByokEndpoint?.ApiKeyMode == ByokApiKeyMode.None;
    public bool UsesCredentialStoreApiKey => SelectedByokEndpoint?.ApiKeyMode == ByokApiKeyMode.CredentialStore;

    /// <summary>
    /// <c>true</c> when the OS credential store is available on this platform. Drives whether the
    /// <see cref="ByokApiKeyMode.CredentialStore"/> mode is offered in the UI (and whether it is the
    /// default for new endpoints). When <c>false</c>, the mode is hidden and endpoints fall back to EnvVar.
    /// </summary>
    public bool IsByokCredentialStoreSupported => _secureKeyStore?.IsSupported ?? false;

    /// <summary>
    /// Transient entry field for the OS-store API key. <b>Never</b> displays the existing secret —
    /// it is intentionally left blank on endpoint selection (you cannot read back what is stored,
    /// only overwrite or clear). A non-empty value here is written to the OS store on save; an
    /// empty value leaves the stored secret untouched (use the Clear button to remove it).
    /// </summary>
    [ObservableProperty] private string? _byokCredentialStoreKeyEntry;

    /// <summary>True when an OS-store entry already exists for the selected endpoint (shows a 'stored' chip).</summary>
    [ObservableProperty] private bool _hasByokCredentialStoreEntry;

    // ── BYOK privacy guard ──
    /// <summary>
    /// When on, Lumi only uses configured BYOK providers, disabling built-in Copilot
    /// authentication so no conversation content reaches GitHub's internal Copilot endpoints.
    /// </summary>
    [ObservableProperty] private bool _useBYOKOnly;

    // ── MCP ──
    [ObservableProperty] private bool _useMcpProxy;

    // ── GitHub Account ──
    [ObservableProperty] private bool _isAuthenticated;
    [ObservableProperty] private string _gitHubLogin = "";
    [ObservableProperty] private bool _isSigningIn;
    [ObservableProperty] private string? _gitHubAuthErrorText;
    [ObservableProperty] private string? _quotaDisplayText;

    /// <summary>Shared login ViewModel — set by MainViewModel after construction.</summary>
    private GitHubLoginViewModel? _loginVM;
    public GitHubLoginViewModel? LoginVM
    {
        get => _loginVM;
        set
        {
            if (_loginVM is not null)
                _loginVM.AuthenticationChanged -= OnLoginAuthChanged;
            _loginVM = value;
            if (_loginVM is not null)
                _loginVM.AuthenticationChanged += OnLoginAuthChanged;
        }
    }

    private void OnLoginAuthChanged(bool isAuth)
    {
        IsAuthenticated = isAuth;
        GitHubLogin = _loginVM?.GitHubLogin ?? "";
    }

    // ── Privacy & Data ──
    [ObservableProperty] private bool _enableMemoryAutoSave;
    [ObservableProperty] private bool _enableMemoryAutoMaintenance;
    [ObservableProperty] private bool _isMemoryCleanupRunning;
    [ObservableProperty] private string _memoryCleanupStatus = "Memory cleanup has not run yet.";
    [ObservableProperty] private bool _autoSaveChats;
    [ObservableProperty] private string _browserCookieStatus = "";

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
    public string AppVersion { get; } =
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "dev";
    public string DotNetVersion => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
    public string OsVersion => System.Runtime.InteropServices.RuntimeInformation.OSDescription;

    // ── Updates ──
    [ObservableProperty] private string _updateStatusText = "";
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private bool _isUpdateDownloading;
    [ObservableProperty] private bool _isUpdateReadyToRestart;
    [ObservableProperty] private bool _isUpdatePreparingToRestart;
    [ObservableProperty] private bool _isUpdateBlocked;
    [ObservableProperty] private bool _isClosingUpdateBlockers;
    [ObservableProperty] private bool _isCheckingForUpdate;
    [ObservableProperty] private bool _isUpdateUpToDate;
    [ObservableProperty] private bool _isUpdateUnavailableInDev;
    [ObservableProperty] private bool _hasUpdateError;
    [ObservableProperty] private bool _isUpdateStatusIdle = true;
    [ObservableProperty] private int _updateDownloadProgress;
    [ObservableProperty] private string _availableUpdateVersion = "";
    [ObservableProperty] private string _updateReleaseNotesMarkdown = "";
    [ObservableProperty] private string _updateReleaseTitle = "";
    [ObservableProperty] private string _updateReleasePageUrl = UpdateService.ReleasesPageUrl;
    [ObservableProperty] private string _updatePublishedText = "";
    [ObservableProperty] private string _updateLastCheckedText = "";
    [ObservableProperty] private bool _shouldAutoNavigateToUpdateCenter;
    [ObservableProperty] private string _updateBlockerErrorText = "";
    private string _lastUpdateNotificationToken = string.Empty;
    public ObservableCollection<UpdateBlockingProcess> UpdateBlockingProcesses { get; } = [];

    /// <summary>Check Now button should only show when not downloading/ready.</summary>
    public bool IsCheckButtonVisible => !IsUpdateAvailable
        && !IsUpdateDownloading
        && !IsRestartStage;

    public bool IsRestartStage => IsUpdateReadyToRestart
        || IsUpdatePreparingToRestart
        || IsUpdateBlocked
        || IsClosingUpdateBlockers;
    public bool HasPendingUpdate => IsUpdateAvailable || IsUpdateDownloading || IsRestartStage;
    public bool HasUpdateAttention => IsUpdateAvailable || IsUpdateReadyToRestart || IsUpdateBlocked;
    public bool ShouldShowUpdateBanner => HasPendingUpdate && !IsUpdateBannerDismissed;
    public bool ShouldShowUpdateBadge => HasPendingUpdate;
    public bool IsUpdateBlockerDialogOpen => IsUpdateBlocked;
    public bool CanCloseAnyUpdateBlocker => UpdateBlockingProcesses.Any(static process => process.CanClose);
    public string UpdateBlockerDialogDescription => Loc.Get(
        "Update_BlockersDescription",
        UpdateBlockingProcesses.Count);
    public bool HasAvailableUpdateVersion => !string.IsNullOrWhiteSpace(AvailableUpdateVersion);
    public bool HasReleaseNotes => !string.IsNullOrWhiteSpace(UpdateReleaseNotesMarkdown);
    public bool IsUpdateReleaseNotesVisible => HasPendingUpdate || HasReleaseNotes;
    public bool CanOpenReleasePage => !string.IsNullOrWhiteSpace(UpdateReleasePageUrl);
    public string UpdateReleaseNotesHeading => HasAvailableUpdateVersion
        ? Loc.Get("Update_WhatsNewVersion", AvailableUpdateVersion)
        : Loc.Update_WhatsNew;
    public string UpdateReleaseNotesDisplayMarkdown => HasReleaseNotes
        ? UpdateReleaseNotesMarkdown
        : Loc.Update_ReleaseNotesFallback;
    public string OpenReleasePageButtonText => HasPendingUpdate
        ? Loc.Update_ViewRelease
        : Loc.Update_ViewReleases;
    public string UpdateStatusBadgeText => IsUpdateStatusIdle
        ? Loc.Update_StatusIdle
        : IsUpdateBlocked
            ? Loc.Update_StatusBlocked
            : IsClosingUpdateBlockers || IsUpdatePreparingToRestart
                ? Loc.Update_StatusPreparing
                : HasUpdateError
                    ? Loc.Update_StatusError
                    : IsUpdateReadyToRestart
                        ? Loc.Update_StatusRestartRequired
                        : IsUpdateDownloading
                            ? Loc.Update_StatusDownloading
                            : IsUpdateAvailable
                                ? Loc.Update_StatusAvailable
                                : IsCheckingForUpdate
                                    ? Loc.Update_StatusChecking
                                    : IsUpdateUnavailableInDev
                                        ? Loc.Update_StatusDev
                                        : Loc.Update_StatusCurrent;
    public string UpdateSidebarBadgeText => HasUpdateError
        ? Loc.Update_BadgeAction
        : IsUpdateReadyToRestart
            ? Loc.Update_BadgeRestart
            : IsUpdateBlocked
            ? Loc.Update_BadgeAction
            : IsUpdateDownloading
                ? Loc.Update_BadgeUpdating
                : Loc.Update_BadgeNew;
    public string UpdateHeroTitle => IsUpdateStatusIdle
        ? Loc.Update_HeroIdleTitle
        : IsUpdateBlocked
            ? Loc.Update_HeroBlockedTitle
            : IsClosingUpdateBlockers
                ? Loc.Update_HeroClosingTitle
                : IsUpdatePreparingToRestart
                    ? Loc.Update_HeroPreparingTitle
                    : HasUpdateError
                        ? Loc.Update_HeroErrorTitle
                        : IsUpdateReadyToRestart
                            ? Loc.Get("Update_BannerReadyTitle", GetUpdateVersionDisplay())
                            : IsUpdateDownloading
                                ? Loc.Get("Update_BannerDownloadingTitle", GetUpdateVersionDisplay())
                                : IsUpdateAvailable
                                    ? Loc.Get("Update_BannerAvailableTitle", GetUpdateVersionDisplay())
                                    : IsCheckingForUpdate
                                        ? Loc.Update_HeroCheckingTitle
                                        : IsUpdateUnavailableInDev
                                            ? Loc.Update_HeroDevTitle
                                            : Loc.Update_HeroUpToDateTitle;
    public string UpdateHeroDescription => IsUpdateStatusIdle
        ? Loc.Update_HeroIdleBody
        : IsUpdateBlocked
            ? Loc.Update_HeroBlockedBody
            : IsClosingUpdateBlockers
                ? Loc.Update_HeroClosingBody
                : IsUpdatePreparingToRestart
                    ? Loc.Update_HeroPreparingBody
                    : HasUpdateError
                        ? HasAvailableUpdateVersion
                            ? Loc.Get("Update_HeroErrorWithDetailsBody", GetUpdateVersionDisplay())
                            : Loc.Update_HeroErrorBody
                        : IsUpdateReadyToRestart
                            ? Loc.Get("Update_HeroReadyBody", AppVersion)
                            : IsUpdateDownloading
                                ? Loc.Get("Update_HeroDownloadingBody", AppVersion)
                                : IsUpdateAvailable
                                    ? Loc.Get("Update_HeroAvailableBody", AppVersion)
                                    : IsCheckingForUpdate
                                        ? Loc.Update_HeroCheckingBody
                                        : IsUpdateUnavailableInDev
                                            ? Loc.Update_HeroDevBody
                                            : Loc.Update_HeroUpToDateBody;

    partial void OnIsUpdateAvailableChanged(bool value) => OnUpdateStateInputsChanged();
    partial void OnIsUpdateDownloadingChanged(bool value) => OnUpdateStateInputsChanged();
    partial void OnIsUpdateReadyToRestartChanged(bool value) => OnUpdateStateInputsChanged();
    partial void OnIsUpdatePreparingToRestartChanged(bool value) => OnUpdateStateInputsChanged();
    partial void OnIsUpdateBlockedChanged(bool value) => OnUpdateStateInputsChanged();
    partial void OnIsClosingUpdateBlockersChanged(bool value) => OnUpdateStateInputsChanged();

    [RelayCommand]
    private async Task CheckForUpdateAsync() => await _updateService.CheckForUpdateAsync();

    [RelayCommand]
    private async Task DownloadUpdateAsync() => await _updateService.DownloadUpdateAsync();

    [RelayCommand]
    private async Task ApplyUpdateAndRestartAsync() => await _updateService.ApplyUpdateAndRestartAsync();

    [RelayCommand]
    private async Task RetryUpdateRestartAsync() => await _updateService.ApplyUpdateAndRestartAsync();

    [RelayCommand]
    private async Task CloseUpdateBlockersAndRestartAsync()
        => await _updateService.CloseBlockingProcessesAndRestartAsync();

    [RelayCommand]
    private void CancelUpdateRestart() => _updateService.CancelBlockedRestart();

    [RelayCommand]
    private void DismissUpdateBanner()
    {
        var token = GetUpdateBannerDismissToken();
        if (string.IsNullOrWhiteSpace(token))
            return;

        _dataStore.Data.Settings.DismissedUpdateBannerToken = token;
        Save();
        RaiseUpdatePresentationPropertiesChanged();
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        var url = string.IsNullOrWhiteSpace(UpdateReleasePageUrl)
            ? UpdateService.ReleasesPageUrl
            : UpdateReleasePageUrl;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Settings] Failed to open release page: {ex.Message}");
        }
    }

    /// <summary>Raised when a setting that affects other ViewModels changes.</summary>
    public event Action? SettingsChanged;
    public event Action? CookieImportDialogRequested;

    /// <summary>Raised when the BYOK endpoint/model configuration changes. Consumers re-inject picker tokens and clear stale selections.</summary>
    public event Action? ByokConfigurationChanged;

    public BrowserService BrowserService => _browserService;

    public SettingsViewModel(DataStore dataStore, CopilotService copilotService, BrowserService browserService, UpdateService updateService, Lumi.Services.Byok.ISecureKeyStore? secureKeyStore = null)
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
        _browserService = browserService;
        _updateService = updateService;
        _secureKeyStore = secureKeyStore;
        ByokApiKeyModeOptions =
        [
            new(ByokApiKeyMode.None, Loc.Settings_Byok_ApiKeyMode_None),
            new(ByokApiKeyMode.EnvVar, Loc.Settings_Byok_ApiKeyMode_EnvVar),
            new(ByokApiKeyMode.Stored, Loc.Settings_Byok_ApiKeyMode_Stored),
            .. IsByokCredentialStoreSupported
                ? [new ByokApiKeyModeOption(ByokApiKeyMode.CredentialStore, Loc.Settings_Byok_ApiKeyMode_CredentialStore)]
                : Array.Empty<ByokApiKeyModeOption>(),
        ];
        var s = dataStore.Data.Settings;

        // General
        _userName = s.UserName ?? "";
        _userSexIndex = s.UserSex switch { "male" => 0, "female" => 1, _ => 2 };
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
        _expandReasoningWhileStreaming = s.ExpandReasoningWhileStreaming;
        _autoGenerateTitles = s.AutoGenerateTitles;

        // AI
        _preferredModel = s.PreferredModel;
        _reasoningEffort = s.ReasoningEffort;
        _contextWindowTier = string.IsNullOrWhiteSpace(s.ContextWindowTier)
            ? ModelContextWindowTiers.Default
            : s.ContextWindowTier;
        if (!string.IsNullOrWhiteSpace(_preferredModel))
            AvailableModels.Add(_preferredModel);
        // Seed the picker with any BYOK tokens saved in settings so they're visible immediately,
        // before the first refresh from Copilot. Without this, the picker shows only the seeded
        // PreferredModel until RefreshCopilotStateAsync completes.
        foreach (var byokToken in (s.ByokModels ?? new List<ByokModel>())
                     .Where(m => m.IsEnabled)
                     .Select(ByokConfigHelper.BuildModelToken)
                     .Where(token => !string.IsNullOrWhiteSpace(token) && !AvailableModels.Contains(token)))
        {
            AvailableModels.Add(byokToken);
        }
        UpdateQualityLevels(_preferredModel);
        UpdateContextWindowTiers(_preferredModel);

        // BYOK
        var coercedUnsupportedCredentialStore = false;
        foreach (var e in s.ByokEndpoints ?? new List<ByokEndpoint>())
        {
            ByokConfigHelper.NormalizeEndpoint(e);
            if (e.ApiKeyMode == ByokApiKeyMode.CredentialStore && !IsByokCredentialStoreSupported)
            {
                e.ApiKeyMode = ByokApiKeyMode.EnvVar;
                coercedUnsupportedCredentialStore = true;
            }
            ByokEndpoints.Add(e);
        }
        foreach (var m in s.ByokModels ?? new List<ByokModel>())
        {
            ByokConfigHelper.NormalizeModel(m);
            ByokModels.Add(m);
        }
        // Sync back any normalization to the data store so persisted data is canonical.
        SyncByokToDataStore();
        ByokValidationMessage = coercedUnsupportedCredentialStore
            ? Loc.Settings_Byok_CredentialStoreUnsupported
            : null;
        IsByokValidationVisible = coercedUnsupportedCredentialStore;
        if (coercedUnsupportedCredentialStore)
            Save();

        // BYOK privacy guard
        _useBYOKOnly = s.UseBYOKOnly;

        // MCP
        _useMcpProxy = s.UseMcpProxy;

        // Privacy
        _enableMemoryAutoSave = s.EnableMemoryAutoSave;
        _enableMemoryAutoMaintenance = s.EnableMemoryAutoMaintenance;
        _autoSaveChats = s.AutoSaveChats;
        RefreshBrowserCookieStatus();

        // Language
        var langEntry = Loc.AvailableLanguages.FirstOrDefault(l => l.Code == s.Language);
        _selectedLanguage = langEntry.Code is not null
            ? $"{langEntry.DisplayName} ({langEntry.Code})"
            : $"English (en)";

        // Wire update status changes
        _updateService.StatusChanged += OnUpdateStatusChanged;
        ApplyUpdateStatus(_updateService.CurrentStatus);
    }

    public void Dispose()
    {
        _updateService.StatusChanged -= OnUpdateStatusChanged;
    }

    private void OnUpdateStatusChanged(UpdateStatus status)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            ApplyUpdateStatus(status);
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyUpdateStatus(status));
    }

    private void ApplyUpdateStatus(UpdateStatus status)
    {
        IsUpdateStatusIdle = status == UpdateStatus.Idle;
        IsCheckingForUpdate = status == UpdateStatus.Checking;
        IsUpdateAvailable = status == UpdateStatus.UpdateAvailable;
        IsUpdateDownloading = status == UpdateStatus.Downloading;
        IsUpdateReadyToRestart = status == UpdateStatus.ReadyToRestart;
        IsUpdatePreparingToRestart = status == UpdateStatus.PreparingToRestart;
        IsUpdateBlocked = status == UpdateStatus.BlockedByProcesses;
        IsClosingUpdateBlockers = status == UpdateStatus.ClosingBlockingProcesses;
        IsUpdateUpToDate = status == UpdateStatus.UpToDate;
        IsUpdateUnavailableInDev = status == UpdateStatus.NotInstalled;
        HasUpdateError = status == UpdateStatus.Error
            || !string.IsNullOrWhiteSpace(_updateService.ErrorMessage);
        UpdateDownloadProgress = _updateService.DownloadProgress;
        AvailableUpdateVersion = _updateService.AvailableVersion ?? string.Empty;
        UpdateReleaseNotesMarkdown = _updateService.ReleaseNotesMarkdown;
        UpdateReleaseTitle = _updateService.ReleaseTitle;
        UpdateReleasePageUrl = string.IsNullOrWhiteSpace(_updateService.ReleasePageUrl)
            ? UpdateService.ReleasesPageUrl
            : _updateService.ReleasePageUrl;
        UpdatePublishedText = FormatUpdateTimestamp(_updateService.ReleasePublishedAt);
        UpdateLastCheckedText = FormatUpdateTimestamp(_updateService.LastCheckedAt);
        UpdateBlockingProcesses.Clear();
        foreach (var blocker in _updateService.BlockingProcesses)
            UpdateBlockingProcesses.Add(blocker);
        UpdateBlockerErrorText = string.IsNullOrWhiteSpace(_updateService.BlockerErrorMessage)
            ? string.Empty
            : Loc.Update_BlockerCloseFailed;
        ShouldAutoNavigateToUpdateCenter =
            (status is UpdateStatus.UpdateAvailable
                or UpdateStatus.ReadyToRestart
                or UpdateStatus.BlockedByProcesses)
            && SelectedPageIndex != AboutPageIndex;

        UpdateStatusText = status switch
        {
            UpdateStatus.Idle => string.Empty,
            UpdateStatus.Checking => Loc.Update_Checking,
            UpdateStatus.UpToDate => Loc.Update_UpToDate,
            UpdateStatus.UpdateAvailable => Loc.Get("Update_Available", _updateService.AvailableVersion ?? ""),
            UpdateStatus.Downloading => Loc.Get("Update_Downloading", _updateService.DownloadProgress),
            UpdateStatus.ReadyToRestart => string.IsNullOrWhiteSpace(_updateService.ErrorMessage)
                ? Loc.Update_ReadyToRestart
                : $"{Loc.Update_Error}: {_updateService.ErrorMessage}",
            UpdateStatus.PreparingToRestart => Loc.Update_PreparingRestart,
            UpdateStatus.BlockedByProcesses => Loc.Update_BlockedByProcesses,
            UpdateStatus.ClosingBlockingProcesses => Loc.Update_ClosingBlockers,
            UpdateStatus.Error => string.IsNullOrWhiteSpace(_updateService.ErrorMessage)
                ? Loc.Update_Error
                : $"{Loc.Update_Error}: {_updateService.ErrorMessage}",
            UpdateStatus.NotInstalled => Loc.Update_DevMode,
            _ => ""
        };

        RaiseUpdatePresentationPropertiesChanged();

        if (!_dataStore.Data.Settings.NotificationsEnabled)
            return;

        // System notification when an update needs attention and the window is minimized/inactive
        if (status == UpdateStatus.UpdateAvailable)
        {
            if (!TryMarkUpdateNotificationShown("available"))
                return;

            NotificationService.ShowIfInactive(
                Loc.Update_NotificationTitle,
                Loc.Get("Update_NotificationBody", _updateService.AvailableVersion ?? ""));
        }
        else if (status == UpdateStatus.ReadyToRestart)
        {
            if (!TryMarkUpdateNotificationShown("ready"))
                return;

            NotificationService.ShowIfInactive(
                Loc.Update_NotificationReadyTitle,
                Loc.Get("Update_NotificationReadyBody", _updateService.AvailableVersion ?? ""));
        }
    }

    public void OpenUpdateCenter()
    {
        ShouldAutoNavigateToUpdateCenter = false;
        SelectedPageIndex = AboutPageIndex;
    }

    private void OnUpdateStateInputsChanged()
    {
        OnPropertyChanged(nameof(IsCheckButtonVisible));
        RaiseUpdatePresentationPropertiesChanged();
    }

    private void RaiseUpdatePresentationPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasPendingUpdate));
        OnPropertyChanged(nameof(HasUpdateAttention));
        OnPropertyChanged(nameof(IsRestartStage));
        OnPropertyChanged(nameof(ShouldShowUpdateBanner));
        OnPropertyChanged(nameof(ShouldShowUpdateBadge));
        OnPropertyChanged(nameof(IsUpdateBlockerDialogOpen));
        OnPropertyChanged(nameof(CanCloseAnyUpdateBlocker));
        OnPropertyChanged(nameof(UpdateBlockerDialogDescription));
        OnPropertyChanged(nameof(HasAvailableUpdateVersion));
        OnPropertyChanged(nameof(HasReleaseNotes));
        OnPropertyChanged(nameof(IsUpdateReleaseNotesVisible));
        OnPropertyChanged(nameof(CanOpenReleasePage));
        OnPropertyChanged(nameof(UpdateReleaseNotesHeading));
        OnPropertyChanged(nameof(UpdateReleaseNotesDisplayMarkdown));
        OnPropertyChanged(nameof(OpenReleasePageButtonText));
        OnPropertyChanged(nameof(UpdateStatusBadgeText));
        OnPropertyChanged(nameof(UpdateSidebarBadgeText));
        OnPropertyChanged(nameof(UpdateHeroTitle));
        OnPropertyChanged(nameof(UpdateHeroDescription));
    }

    private string GetUpdateVersionDisplay()
        => HasAvailableUpdateVersion ? AvailableUpdateVersion : AppVersion;

    private bool TryMarkUpdateNotificationShown(string stage)
    {
        var version = GetUpdateVersionDisplay();
        var token = $"{stage}:{version}";
        if (string.Equals(_lastUpdateNotificationToken, token, StringComparison.Ordinal))
            return false;

        _lastUpdateNotificationToken = token;
        return true;
    }

    private bool IsUpdateBannerDismissed
        => string.Equals(
            _dataStore.Data.Settings.DismissedUpdateBannerToken,
            GetUpdateBannerDismissToken(),
            StringComparison.Ordinal);

    private string GetUpdateBannerDismissToken()
    {
        if (!HasPendingUpdate)
            return string.Empty;

        var stage = IsRestartStage
            ? "ready"
            : "pending";
        var version = HasAvailableUpdateVersion ? AvailableUpdateVersion.Trim() : AppVersion;
        return $"{stage}:{version}";
    }

    private static string FormatUpdateTimestamp(DateTimeOffset? timestamp)
        => timestamp?.ToLocalTime().ToString("g", Loc.Culture) ?? string.Empty;

    // ── Auto-save on every property change + notify IsModified ──

    partial void OnUserNameChanged(string value) { _dataStore.Data.Settings.UserName = value.Trim(); Save(); }
    partial void OnUserSexIndexChanged(int value) { _dataStore.Data.Settings.UserSex = value switch { 0 => "male", 1 => "female", _ => null }; Save(); }
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
    partial void OnExpandReasoningWhileStreamingChanged(bool value) { _dataStore.Data.Settings.ExpandReasoningWhileStreaming = value; Save(); NotifyModified(); }
    partial void OnAutoGenerateTitlesChanged(bool value) { _dataStore.Data.Settings.AutoGenerateTitles = value; Save(); NotifyModified(); }

    private void UpdateQualityLevels(string? modelId)
    {
        QualityLevels = ModelSelectionHelper.GetQualityLevels(modelId, _modelReasoningEfforts);
        SyncSelectedQualityFromState(modelId);
    }

    private void UpdateContextWindowTiers(string? modelId)
    {
        ContextWindowTiers = ModelSelectionHelper.GetContextWindowTiers(modelId, _modelsWithLongContext);
        SyncSelectedContextWindowTierFromState(modelId);
    }

    private string? GetSelectedReasoningEffort()
    {
        var explicitEffort = ModelSelectionHelper.DisplayToEffort(SelectedQuality);
        if (!string.IsNullOrWhiteSpace(explicitEffort))
        {
            return ModelSelectionHelper.NormalizeEffort(
                explicitEffort,
                PreferredModel,
                _modelReasoningEfforts,
                _modelDefaultEfforts);
        }

        return ModelSelectionHelper.NormalizeEffort(
            string.IsNullOrWhiteSpace(ReasoningEffort) ? null : ReasoningEffort,
            PreferredModel,
            _modelReasoningEfforts,
            _modelDefaultEfforts);
    }

    private string? GetSelectedContextWindowTier()
    {
        var explicitTier = ModelSelectionHelper.DisplayToContextWindowTier(SelectedContextWindowTier);
        if (!string.IsNullOrWhiteSpace(explicitTier))
            return ModelSelectionHelper.NormalizeContextWindowTier(explicitTier, PreferredModel, _modelsWithLongContext);

        return ModelSelectionHelper.NormalizeContextWindowTier(
            string.IsNullOrWhiteSpace(ContextWindowTier) ? ModelContextWindowTiers.Default : ContextWindowTier,
            PreferredModel,
            _modelsWithLongContext);
    }

    private void SyncSelectedQualityFromState(string? modelId = null, string? preferredEffort = null)
    {
        if (QualityLevels is null)
        {
            SetSelectedQualityValue(null);
            return;
        }

        var display = ModelSelectionHelper.ResolveSelectedQualityDisplay(
            preferredEffort ?? (string.IsNullOrWhiteSpace(ReasoningEffort) ? null : ReasoningEffort),
            modelId ?? PreferredModel,
            _modelReasoningEfforts,
            _modelDefaultEfforts);

        SetSelectedQualityValue(display);
    }

    private void SyncSelectedContextWindowTierFromState(string? modelId = null, string? preferredTier = null)
    {
        if (ContextWindowTiers is null)
        {
            SetSelectedContextWindowTierValue(null);
            return;
        }

        var display = ModelSelectionHelper.ResolveSelectedContextWindowTierDisplay(
            preferredTier ?? (string.IsNullOrWhiteSpace(ContextWindowTier) ? ModelContextWindowTiers.Default : ContextWindowTier),
            modelId ?? PreferredModel,
            _modelsWithLongContext);

        SetSelectedContextWindowTierValue(display);
    }

    private void SetSelectedQualityValue(string? value)
    {
        if (SelectedQuality == value)
            return;

        _suppressSelectedQualitySync = true;
        SelectedQuality = value;
        _suppressSelectedQualitySync = false;
    }

    private void SetSelectedContextWindowTierValue(string? value)
    {
        if (SelectedContextWindowTier == value)
            return;

        _suppressSelectedContextWindowTierSync = true;
        SelectedContextWindowTier = value;
        _suppressSelectedContextWindowTierSync = false;
    }

    partial void OnPreferredModelChanged(string value)
    {
        _dataStore.Data.Settings.PreferredModel = value;
        if (!string.IsNullOrWhiteSpace(value) && !AvailableModels.Contains(value))
            AvailableModels.Add(value);

        UpdateQualityLevels(value);
        UpdateContextWindowTiers(value);
        var resolvedEffort = GetSelectedReasoningEffort() ?? string.Empty;
        if (ReasoningEffort != resolvedEffort)
        {
            ReasoningEffort = resolvedEffort;
            return;
        }

        Save();
        NotifyModified();
    }

    partial void OnSelectedQualityChanged(string? value)
    {
        if (_suppressSelectedQualitySync)
            return;

        var resolvedEffort = GetSelectedReasoningEffort() ?? string.Empty;
        if (ReasoningEffort != resolvedEffort)
        {
            ReasoningEffort = resolvedEffort;
            return;
        }

        Save();
        NotifyModified();
    }

    partial void OnSelectedContextWindowTierChanged(string? value)
    {
        if (_suppressSelectedContextWindowTierSync)
            return;

        var resolvedTier = GetSelectedContextWindowTier();
        if (resolvedTier is null)
            return;

        if (ContextWindowTier != resolvedTier)
        {
            ContextWindowTier = resolvedTier;
            return;
        }

        Save();
        NotifyModified();
    }

    partial void OnReasoningEffortChanged(string value)
    {
        _dataStore.Data.Settings.ReasoningEffort = value;
        SyncSelectedQualityFromState(PreferredModel, value);
        Save();
        NotifyModified();
    }

    partial void OnUseMcpProxyChanged(bool value) { _dataStore.Data.Settings.UseMcpProxy = value; Save(); NotifyModified(); }

    partial void OnUseBYOKOnlyChanged(bool value)
    {
        _dataStore.Data.Settings.UseBYOKOnly = value;
        // Keep the CopilotService chokepoint in sync with the live flag.
        _copilotService.SetSettingsProvider(() => _dataStore.Data.Settings);
        Save();
        NotifyModified();
    }

    partial void OnContextWindowTierChanged(string value)
    {
        var tier = string.IsNullOrWhiteSpace(value) ? ModelContextWindowTiers.Default : value;
        _dataStore.Data.Settings.ContextWindowTier = tier;
        SyncSelectedContextWindowTierFromState(PreferredModel, tier);
        Save();
        NotifyModified();
    }

    public void SyncDefaultModelSelectionFromChat(string model, string? reasoningEffort, string? contextWindowTier)
    {
        if (string.IsNullOrWhiteSpace(model))
            return;

        var normalizedEffort = ModelSelectionHelper.NormalizeEffort(
            reasoningEffort,
            model,
            _modelReasoningEfforts,
            _modelDefaultEfforts) ?? string.Empty;

        if (PreferredModel != model)
            PreferredModel = model;

        if (ReasoningEffort != normalizedEffort)
            ReasoningEffort = normalizedEffort;

        if (!string.IsNullOrWhiteSpace(contextWindowTier) && ContextWindowTier != contextWindowTier)
            ContextWindowTier = contextWindowTier;
    }

    public void UpdateAvailableModels(System.Collections.Generic.List<string> models)
    {
        AvailableModels.Clear();
        foreach (var m in models)
            AvailableModels.Add(m);

        if (!string.IsNullOrWhiteSpace(PreferredModel) && !AvailableModels.Contains(PreferredModel))
            AvailableModels.Add(PreferredModel);

        UpdateQualityLevels(PreferredModel);
        UpdateContextWindowTiers(PreferredModel);
    }

    public void UpdateModelCapabilities(
        List<GitHub.Copilot.ModelInfo> models,
        IReadOnlySet<string>? longContextModelIds = null)
    {
        ModelSelectionHelper.ApplyModelCapabilities(models, _modelReasoningEfforts, _modelDefaultEfforts);
        _modelsWithLongContext = CopyModelIdSet(longContextModelIds);
        UpdateQualityLevels(PreferredModel);
        UpdateContextWindowTiers(PreferredModel);
    }

    private static HashSet<string> CopyModelIdSet(IEnumerable<string>? modelIds)
        => modelIds?
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // ── BYOK Commands ──

    [RelayCommand]
    private void AddByokEndpoint()
    {
        var endpoint = new ByokEndpoint
        {
            Name = Loc.Settings_Byok_EndpointName,
            ProviderType = "openai",
            BaseUrl = "https://api.openai.com/v1",
            WireApi = "completions",
            // CredentialStore is the secure default when the OS store is available; otherwise fall
            // back to EnvVar (the next most secure option) since CredentialStore would be a no-op.
            ApiKeyMode = IsByokCredentialStoreSupported ? ByokApiKeyMode.CredentialStore : ByokApiKeyMode.EnvVar,
            IsEnabled = true,
        };
        ByokEndpoints.Add(endpoint);
        SelectedByokEndpoint = endpoint;
        SyncByokToDataStore();
        Save();
        NotifyModified();
        ByokConfigurationChanged?.Invoke();
        OnPropertyChanged(nameof(IsByokEndpointSelected));
    }

    [RelayCommand]
    private async Task DeleteByokEndpointAsync()
    {
        var endpoint = SelectedByokEndpoint;
        if (endpoint is null) return;

        // Cascade: remove all models pointing to this endpoint.
        var cascade = ByokModels.Where(m => m.EndpointId == endpoint.Id).ToList();
        foreach (var m in cascade)
            ByokModels.Remove(m);

        // Also drop the OS-store entry so deleting an endpoint does not leave an orphan secret.
        if (_secureKeyStore is { IsSupported: true })
        {
            try { await _secureKeyStore.DeleteAsync(Lumi.Services.Byok.SecureKeyStoreFactory.EndpointKey(endpoint.Id)); }
            catch { /* best-effort cleanup */ }
        }

        ByokEndpoints.Remove(endpoint);
        SelectedByokEndpoint = null;
        SyncByokToDataStore();
        Save();
        NotifyModified();
        ByokConfigurationChanged?.Invoke();
        OnPropertyChanged(nameof(IsByokEndpointSelected));
    }

    [RelayCommand]
    private void DuplicateByokEndpoint()
    {
        var source = SelectedByokEndpoint;
        if (source is null) return;

        var copy = new ByokEndpoint
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = source.Name + " (copy)",
            ProviderType = source.ProviderType,
            BaseUrl = source.BaseUrl,
            WireApi = source.WireApi,
            AzureApiVersion = source.AzureApiVersion,
            IsEnabled = source.IsEnabled,
            ApiKeyMode = source.ApiKeyMode,
            ApiKeyEnvVar = source.ApiKeyEnvVar,
            ApiKey = source.ApiKey,
            Headers = source.Headers is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(source.Headers, StringComparer.OrdinalIgnoreCase),
        };
        ByokEndpoints.Add(copy);
        SelectedByokEndpoint = copy;
        SyncByokToDataStore();
        Save();
        NotifyModified();
        ByokConfigurationChanged?.Invoke();
        OnPropertyChanged(nameof(IsByokEndpointSelected));
    }

    /// <summary>
    /// Closes the endpoint editor without deleting the endpoint: simply deselects it
    /// so the detail panel collapses. Pending edits are already persisted to the data store.
    /// </summary>
    [RelayCommand]
    private void CloseByokEndpoint()
    {
        SelectedByokEndpoint = null;
    }

    /// <summary>
    /// Closes the model editor without deleting the model: simply deselects it so the
    /// detail panel collapses. Pending edits are already persisted to the data store.
    /// </summary>
    [RelayCommand]
    private void CloseByokModel()
    {
        SelectedByokModel = null;
    }

    [RelayCommand]
    private void TestByokEndpoint()
    {
        var endpoint = SelectedByokEndpoint;
        if (endpoint is null)
        {
            ByokValidationMessage = null;
            IsByokValidationVisible = false;
            return;
        }

        var error = ByokConfigHelper.TestEndpoint(endpoint, _secureKeyStore);
        ByokValidationMessage = error is null
            ? Loc.Settings_Byok_TestOk
            : string.Format(Loc.Settings_Byok_TestFail, error);
        IsByokValidationVisible = true;
    }

    [RelayCommand]
    private void AddByokModel()
    {
        var endpointId = SelectedByokEndpoint?.Id ?? ByokEndpoints.FirstOrDefault()?.Id ?? "";
        var model = new ByokModel
        {
            EndpointId = endpointId,
            ModelId = "",
            DisplayName = Loc.Settings_Byok_ModelName,
            IsEnabled = true,
        };
        ByokModels.Add(model);
        SelectedByokModel = model;
        SyncByokToDataStore();
        Save();
        NotifyModified();
        ByokConfigurationChanged?.Invoke();
    }

    [RelayCommand]
    private void DeleteByokModel()
    {
        var model = SelectedByokModel;
        if (model is null) return;
        ByokModels.Remove(model);
        SelectedByokModel = null;
        SyncByokToDataStore();
        Save();
        NotifyModified();
        ByokConfigurationChanged?.Invoke();
    }

    [RelayCommand]
    private void DuplicateByokModel()
    {
        var source = SelectedByokModel;
        if (source is null) return;

        var copy = new ByokModel
        {
            Id = Guid.NewGuid().ToString("N"),
            EndpointId = source.EndpointId,
            ModelId = source.ModelId,
            DisplayName = source.DisplayName + " (copy)",
            IsEnabled = source.IsEnabled,
            // Carry over advanced inference/rate-limit settings so a duplicate is a true clone.
            MaxOutputTokens = source.MaxOutputTokens,
            MaxPromptTokens = source.MaxPromptTokens,
            MaxRequestsPerMinute = source.MaxRequestsPerMinute,
        };
        ByokModels.Add(copy);
        SelectedByokModel = copy;
        SyncByokToDataStore();
        Save();
        NotifyModified();
        ByokConfigurationChanged?.Invoke();
    }

    /// <summary>
    /// Copies the BYOK collections back into <c>_dataStore.Data.Settings</c> so they persist.
    /// The observable collections hold the canonical state while the user is editing.
    /// </summary>
    private void SyncByokToDataStore()
    {
        _dataStore.Data.Settings.ByokEndpoints = ByokEndpoints.ToList();
        _dataStore.Data.Settings.ByokModels = ByokModels.ToList();
    }

    private ByokEndpoint? _subscribedByokEndpoint;
    private bool _suppressByokEndpointPersistence;

    partial void OnSelectedByokEndpointChanged(ByokEndpoint? value)
    {
        // Unsubscribe from the old endpoint's property changes.
        if (_subscribedByokEndpoint is not null)
            _subscribedByokEndpoint.PropertyChanged -= OnSelectedByokEndpointPropertyChanged;

        _subscribedByokEndpoint = value;

        // Subscribe to the new endpoint so computed visibility flags (Azure? Stored key? Env var?)
        // re-evaluate live when the user edits ProviderType / ApiKeyMode in the editor.
        if (_subscribedByokEndpoint is not null)
            _subscribedByokEndpoint.PropertyChanged += OnSelectedByokEndpointPropertyChanged;

        // Recompute visibility flags and reset the test result banner when switching endpoints.
        OnPropertyChanged(nameof(IsByokEndpointSelected));
        RaiseByokEndpointVisibilityFlags();
        ByokValidationMessage = null;
        IsByokValidationVisible = false;
        // Reset the OS-store entry box (never echoes a stored secret) and probe the store for the
        // "key present" chip on the newly selected endpoint.
        ByokCredentialStoreKeyEntry = null;
        _ = RefreshByokCredentialStoreEntryFlagAsync();
    }

    private void OnSelectedByokEndpointPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only the fields that affect conditional UI sections need to trigger re-evaluation.
        if (e.PropertyName is nameof(ByokEndpoint.ProviderType)
                              or nameof(ByokEndpoint.ApiKeyMode))
        {
            RaiseByokEndpointVisibilityFlags();
        }

        if (e.PropertyName == nameof(ByokEndpoint.ApiKeyMode))
            OnPropertyChanged(nameof(SelectedByokEndpointApiKeyModeOption));

        if (_suppressByokEndpointPersistence)
            return;

        // Persist any edit to the endpoint back to the data store + notify consumers.
        SyncByokToDataStore();
        Save();
        NotifyModified();
        ByokConfigurationChanged?.Invoke();
    }

    private void RaiseByokEndpointVisibilityFlags()
    {
        OnPropertyChanged(nameof(IsSelectedByokAzure));
        OnPropertyChanged(nameof(IsSelectedByokWireApiVisible));
        OnPropertyChanged(nameof(UsesEnvVarApiKey));
        OnPropertyChanged(nameof(UsesStoredApiKey));
        OnPropertyChanged(nameof(UsesNoApiKey));
        OnPropertyChanged(nameof(UsesCredentialStoreApiKey));
        OnPropertyChanged(nameof(SelectedByokEndpointApiKeyModeOption));
    }

    [RelayCommand]
    private async Task ChangeByokApiKeyModeAsync(ByokApiKeyModeOption? option)
    {
        var endpoint = SelectedByokEndpoint;
        if (endpoint is null || option is null || endpoint.ApiKeyMode == option.Value)
            return;

        if (option.Value == ByokApiKeyMode.CredentialStore
            && _secureKeyStore is not { IsSupported: true })
        {
            ByokValidationMessage = Loc.Settings_Byok_CredentialStoreUnsupported;
            IsByokValidationVisible = true;
            OnPropertyChanged(nameof(SelectedByokEndpointApiKeyModeOption));
            return;
        }

        var movePlaintextToCredentialStore = option.Value == ByokApiKeyMode.CredentialStore
            && !string.IsNullOrEmpty(endpoint.ApiKey);
        if (movePlaintextToCredentialStore)
        {
            try
            {
                await _secureKeyStore!.SetAsync(
                    Lumi.Services.Byok.SecureKeyStoreFactory.EndpointKey(endpoint.Id),
                    endpoint.ApiKey);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[Settings] Failed to migrate BYOK key to credential store: {ex.Message}");
                ByokValidationMessage = Loc.Settings_Byok_CredentialStoreMigrationFailed;
                IsByokValidationVisible = true;
                OnPropertyChanged(nameof(SelectedByokEndpointApiKeyModeOption));
                return;
            }
        }

        _suppressByokEndpointPersistence = true;
        try
        {
            if (movePlaintextToCredentialStore)
                endpoint.ApiKey = null;
            endpoint.ApiKeyMode = option.Value;
        }
        finally
        {
            _suppressByokEndpointPersistence = false;
        }

        ByokValidationMessage = null;
        IsByokValidationVisible = false;
        RaiseByokEndpointVisibilityFlags();
        await RefreshByokCredentialStoreEntryFlagAsync();
        SyncByokToDataStore();
        Save();
        NotifyModified();
        ByokConfigurationChanged?.Invoke();
    }

    /// <summary>
    /// Refreshes <see cref="HasByokCredentialStoreEntry"/> for the currently selected endpoint by
    /// probing the OS store. Best-effort; failures leave the flag false.
    /// </summary>
    private async Task RefreshByokCredentialStoreEntryFlagAsync()
    {
        if (_secureKeyStore is not { IsSupported: true } || SelectedByokEndpoint is null)
        {
            HasByokCredentialStoreEntry = false;
            return;
        }

        try
        {
            var existing = await _secureKeyStore.GetAsync(
                Lumi.Services.Byok.SecureKeyStoreFactory.EndpointKey(SelectedByokEndpoint.Id));
            HasByokCredentialStoreEntry = !string.IsNullOrEmpty(existing);
        }
        catch
        {
            HasByokCredentialStoreEntry = false;
        }
    }

    /// <summary>
    /// Writes the <see cref="ByokCredentialStoreKeyEntry"/> value to the OS store for the selected
    /// endpoint. An empty entry is a no-op (use <see cref="ClearByokCredentialStoreKey"/> to remove).
    /// Clears the transient entry after writing so the box returns to its secure 'blank' state.
    /// </summary>
    [RelayCommand]
    private async Task SaveByokCredentialStoreKeyAsync()
    {
        if (SelectedByokEndpoint is null || _secureKeyStore is not { IsSupported: true })
            return;

        var value = ByokCredentialStoreKeyEntry;
        if (string.IsNullOrEmpty(value))
            return;

        await _secureKeyStore.SetAsync(
            Lumi.Services.Byok.SecureKeyStoreFactory.EndpointKey(SelectedByokEndpoint.Id), value);
        ByokCredentialStoreKeyEntry = null;
        await RefreshByokCredentialStoreEntryFlagAsync();
        NotifyModified();
        ByokConfigurationChanged?.Invoke();
    }

    /// <summary>Removes the OS-store entry for the selected endpoint (no-op if absent).</summary>
    [RelayCommand]
    private async Task ClearByokCredentialStoreKeyAsync()
    {
        if (SelectedByokEndpoint is null || _secureKeyStore is not { IsSupported: true })
            return;

        await _secureKeyStore.DeleteAsync(
            Lumi.Services.Byok.SecureKeyStoreFactory.EndpointKey(SelectedByokEndpoint.Id));
        ByokCredentialStoreKeyEntry = null;
        await RefreshByokCredentialStoreEntryFlagAsync();
        NotifyModified();
        ByokConfigurationChanged?.Invoke();
    }

    private ByokModel? _subscribedByokModel;

    partial void OnSelectedByokModelChanged(ByokModel? value)
    {
        if (_subscribedByokModel is not null)
            _subscribedByokModel.PropertyChanged -= OnSelectedByokModelPropertyChanged;

        _subscribedByokModel = value;

        if (_subscribedByokModel is not null)
            _subscribedByokModel.PropertyChanged += OnSelectedByokModelPropertyChanged;

        // Keep the endpoint dropdown in sync with the selected model so editing a model in the
        // UI immediately shows which endpoint it belongs to. Without this, the dropdown keeps
        // showing the previously-selected endpoint, which is confusing when you open a model
        // belonging to a different endpoint.
        var desiredEndpoint = value is null
            ? null
            : ByokEndpoints.FirstOrDefault(e => e.Id == value.EndpointId);
        if (!ReferenceEquals(desiredEndpoint, SelectedByokEndpoint))
            SelectedByokEndpoint = desiredEndpoint;
    }

    private void OnSelectedByokModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        SyncByokToDataStore();
        Save();
        NotifyModified();
        ByokConfigurationChanged?.Invoke();
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        IsSigningIn = true;
        GitHubAuthErrorText = null;
        try
        {
            var signInResult = await _copilotService.SignInAsync();
            if (signInResult != CopilotSignInResult.Success)
            {
                GitHubAuthErrorText = signInResult switch
                {
                    CopilotSignInResult.CliNotFound => Loc.Status_GitHubSignInCliMissing,
                    _ => Loc.Status_GitHubSignInFailed,
                };
                return;
            }

            await RefreshAuthStatusAsync();

            if (!IsAuthenticated)
                GitHubAuthErrorText = Loc.Status_GitHubSignInRefreshFailed;
        }
        catch (OperationCanceledException)
        {
            GitHubAuthErrorText = Loc.Status_GitHubSignInFailed;
        }
        catch (Exception ex)
        {
            GitHubAuthErrorText = string.Format(Loc.Status_GitHubSignInUnexpectedError, ex.Message);
        }
        finally { IsSigningIn = false; }
    }

    public async Task RefreshAuthStatusAsync()
    {
        try
        {
            var status = await _copilotService.GetAuthStatusAsync();
            IsAuthenticated = status.IsAuthenticated == true;
            GitHubLogin = status.Login ?? _copilotService.GetStoredLogin() ?? "";
            if (IsAuthenticated)
                GitHubAuthErrorText = null;
        }
        catch
        {
            // A failed status RPC is INDETERMINATE: it is not proof of a logout (a brief backend or
            // transport hiccup throws here) and not proof of a valid session. Do not flip the UI
            // either way, and never promote to authenticated from the stored config identity —
            // GetStoredLogin reads `lastLoggedInUser`, which only records the last user who ever
            // signed in, not live auth state, so trusting it would falsely show "signed in" after a
            // real logout. Preserving the last LIVE-confirmed value (set only by the try block
            // above) also avoids spurious connect/disconnect churn in MainViewModel, which mirrors
            // this flag.
            return;
        }

        if (!IsAuthenticated)
        {
            QuotaDisplayText = null;
            return;
        }

        await RefreshQuotaAsync();
    }

    public async Task RefreshQuotaAsync()
    {
        try
        {
            var quota = await _copilotService.GetAccountQuotaAsync();
            if (quota?.QuotaSnapshots is not { Count: > 0 })
            {
                QuotaDisplayText = null;
                return;
            }

            var snapshot = quota.QuotaSnapshots.Values.First();
            var used = snapshot.UsedRequests;
            var total = snapshot.EntitlementRequests;
            var remaining = snapshot.RemainingPercentage;

            if (total > 0)
                QuotaDisplayText = $"{used:N0} / {total:N0} requests ({remaining:N0}% remaining)";
            else
                QuotaDisplayText = $"{remaining:N0}% remaining";
        }
        catch
        {
            QuotaDisplayText = null;
        }
    }

    partial void OnEnableMemoryAutoSaveChanged(bool value) { _dataStore.Data.Settings.EnableMemoryAutoSave = value; Save(); SettingsChanged?.Invoke(); NotifyModified(); }
    partial void OnEnableMemoryAutoMaintenanceChanged(bool value) { _dataStore.Data.Settings.EnableMemoryAutoMaintenance = value; Save(); SettingsChanged?.Invoke(); NotifyModified(); }
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
    public bool IsExpandReasoningWhileStreamingModified => ExpandReasoningWhileStreaming != _defaults.ExpandReasoningWhileStreaming;
    public bool IsAutoGenerateTitlesModified => AutoGenerateTitles != _defaults.AutoGenerateTitles;
    public bool IsDefaultModelSelectionModified => PreferredModel != _defaults.PreferredModel
        || ReasoningEffort != _defaults.ReasoningEffort
        || ContextWindowTier != _defaults.ContextWindowTier;
    public bool IsPreferredModelModified=> PreferredModel != _defaults.PreferredModel;
    public bool IsReasoningEffortModified => ReasoningEffort != _defaults.ReasoningEffort;
    public bool IsUseMcpProxyModified => UseMcpProxy != _defaults.UseMcpProxy;
    public bool IsContextWindowTierModified => ContextWindowTier != _defaults.ContextWindowTier;
    public bool IsEnableMemoryAutoSaveModified => EnableMemoryAutoSave != _defaults.EnableMemoryAutoSave;
    public bool IsEnableMemoryAutoMaintenanceModified => EnableMemoryAutoMaintenance != _defaults.EnableMemoryAutoMaintenance;
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
        OnPropertyChanged(nameof(IsExpandReasoningWhileStreamingModified));
        OnPropertyChanged(nameof(IsAutoGenerateTitlesModified));
        OnPropertyChanged(nameof(IsDefaultModelSelectionModified));
        OnPropertyChanged(nameof(IsPreferredModelModified));
        OnPropertyChanged(nameof(IsReasoningEffortModified));
        OnPropertyChanged(nameof(IsUseMcpProxyModified));
        OnPropertyChanged(nameof(IsContextWindowTierModified));
        OnPropertyChanged(nameof(IsEnableMemoryAutoSaveModified));
        OnPropertyChanged(nameof(IsEnableMemoryAutoMaintenanceModified));
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
    [RelayCommand] private void RevertExpandReasoningWhileStreaming() => ExpandReasoningWhileStreaming = _defaults.ExpandReasoningWhileStreaming;
    [RelayCommand] private void RevertAutoGenerateTitles() => AutoGenerateTitles = _defaults.AutoGenerateTitles;
    [RelayCommand]
    private void RevertDefaultModelSelection()
    {
        PreferredModel = _defaults.PreferredModel;
        ReasoningEffort = _defaults.ReasoningEffort;
        ContextWindowTier = _defaults.ContextWindowTier;
    }
    [RelayCommand] private void RevertUseMcpProxy() => UseMcpProxy = _defaults.UseMcpProxy;
    [RelayCommand] private void RevertEnableMemoryAutoSave() => EnableMemoryAutoSave = _defaults.EnableMemoryAutoSave;
    [RelayCommand] private void RevertEnableMemoryAutoMaintenance() => EnableMemoryAutoMaintenance = _defaults.EnableMemoryAutoMaintenance;
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

    private void Save() => _ = _dataStore.SaveAsync();

    [RelayCommand]
    private void ClearAllChats()
    {
        _dataStore.Data.Chats.Clear();
        _dataStore.DeleteAllChatFiles();
        Save();
        SettingsChanged?.Invoke();
    }

    [RelayCommand]
    private void ClearAllMemories()
    {
        _dataStore.Data.Memories.Clear();
        Save();
        MemoryCleanupStatus = "All memories were cleared.";
        SettingsChanged?.Invoke();
    }

    [RelayCommand]
    private async Task CleanUpMemoriesNowAsync()
    {
        if (IsMemoryCleanupRunning)
            return;

        IsMemoryCleanupRunning = true;
        MemoryCleanupStatus = "Cleaning up memories...";
        try
        {
            var result = await new MemoryMaintenanceService(_dataStore).RunAsync();
            MemoryCleanupStatus = result.ToDisplayText();
            SettingsChanged?.Invoke();
            RefreshStats();
        }
        finally
        {
            IsMemoryCleanupRunning = false;
        }
    }

    [RelayCommand]
    private void ImportBrowserCookiesAgain()
    {
        CookieImportDialogRequested?.Invoke();
    }

    public void MarkCookiesImported()
    {
        _dataStore.Data.Settings.HasImportedBrowserCookies = true;
        Save();
        RefreshBrowserCookieStatus();
    }

    [RelayCommand]
    private async Task ResetBrowserCookiesAsync()
    {
        try
        {
            await _browserService.ClearCookiesAsync();
            _dataStore.Data.Settings.HasImportedBrowserCookies = false;
            Save();
            RefreshBrowserCookieStatus();
        }
        catch (Exception ex)
        {
            BrowserCookieStatus = $"Could not reset browser cookies: {ex.Message}";
        }
    }

    public void RefreshBrowserCookieStatus()
    {
        BrowserCookieStatus = _dataStore.Data.Settings.HasImportedBrowserCookies
            ? "Cookies are imported for Lumi browser."
            : "Cookies are not imported yet.";
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
        PreferredModel = defaults.PreferredModel;
        ReasoningEffort = defaults.ReasoningEffort;
        UseMcpProxy = defaults.UseMcpProxy;
        ContextWindowTier = defaults.ContextWindowTier;
        EnableMemoryAutoSave = defaults.EnableMemoryAutoSave;
        EnableMemoryAutoMaintenance = defaults.EnableMemoryAutoMaintenance;
        AutoSaveChats = defaults.AutoSaveChats;
        NeedsRestart = false;
    }

    /// <summary>
    /// Stats for About page.
    /// </summary>
    public int TotalChats => _dataStore.Data.Chats.Count;
    public int TotalMemories => _dataStore.Data.Memories.Count(m =>
        string.Equals(m.Status, Models.MemoryStatuses.Active, StringComparison.OrdinalIgnoreCase));
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
