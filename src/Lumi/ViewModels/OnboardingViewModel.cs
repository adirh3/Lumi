using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Onboarding;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

public enum OnboardingStep { Hello, Capabilities, Autonomy, Connect, Workspace, KnowYou, Ready }

public enum OnboardingWorkspacePhase { Consent, Scanning, Results }

public enum OnboardingPronoun { Unset, He, She, Unspecified }

/// <summary>What onboarding hands back to the shell: optionally a first message to send, in a project.</summary>
public sealed record OnboardingCompletion(string? StarterPrompt, Guid? StarterProjectId);

/// <summary>
/// Lumi's first run, in seven calm steps: say hello, see what Lumi can do, choose how much it may do
/// on its own, connect an AI, let Lumi look around and set up projects, answer a few questions that
/// become memories, and start a first task.
/// Discovery is local and per-source opt-in; one prefetched, tool-free model call personalizes the
/// conversation and the first tasks, with deterministic fallbacks when no AI is available.
/// </summary>
public partial class OnboardingViewModel : ObservableObject
{
    public const int StepCount = 7;
    private const int PreselectedProjects = 3;
    private static readonly TimeSpan CapabilityInterval = TimeSpan.FromSeconds(6.5);
    private static readonly TimeSpan PersonalizationTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan RecentProject = TimeSpan.FromDays(30);

    private readonly DataStore _dataStore;
    private readonly CopilotService _copilotService;
    private readonly MemoryAgentService _memoryAgentService;
    private readonly IWorkspaceDiscovery _discovery;
    private readonly Func<string, WorkspaceSnapshot, CancellationToken, Task<OnboardingPersonalization>> _personalize;
    private readonly string _activeLanguage;
    private readonly List<OnboardingProjectSummary> _setUpProjects = [];
    private readonly List<Memory> _savedMemories = [];

    private DispatcherTimer? _capabilityTimer;
    private bool _capabilityPinned;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _personalizationCts;
    private Task<OnboardingPersonalization>? _personalizationTask;
    private OnboardingPersonalization? _personalization;
    private WorkspaceSnapshot _workspace = WorkspaceSnapshot.Empty;
    private OnboardingChatItem? _pendingQuestion;
    private bool _conversationStarted;
    private bool _conversationSkipped;
    private int _questionIndex;
    private bool _isCompleting;

    public OnboardingViewModel(
        DataStore dataStore,
        CopilotService copilotService,
        IWorkspaceDiscovery? discovery = null,
        Func<string, WorkspaceSnapshot, CancellationToken, Task<OnboardingPersonalization>>? personalize = null)
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
        _memoryAgentService = new MemoryAgentService(dataStore, copilotService);
        _discovery = discovery ?? new WorkspaceDiscovery();
        _personalize = personalize ?? PersonalizeWithCopilotAsync;

        var settings = dataStore.Data.Settings;
        _isDarkTheme = settings.IsDarkTheme;
        _userName = settings.UserName ?? "";
        _pronoun = settings.UserSex switch
        {
            "male" => OnboardingPronoun.He,
            "female" => OnboardingPronoun.She,
            _ => OnboardingPronoun.Unset,
        };
        _activeLanguage = string.IsNullOrWhiteSpace(settings.Language) ? "en" : settings.Language;
        _languageIndex = Math.Max(0, Array.FindIndex(Loc.AvailableLanguages,
            language => string.Equals(language.Code, _activeLanguage, StringComparison.OrdinalIgnoreCase)));

        for (var index = 0; index < StepCount; index++)
            StepDots.Add(new OnboardingStepDot(index));

        Capabilities =
        [
            OnboardingCapability.FromStrings("pc", static () => WorkspaceIcons.Desktop, "Pc"),
            OnboardingCapability.FromStrings("web", static () => WorkspaceIcons.Browser, "Web"),
            OnboardingCapability.FromStrings("documents", static () => WorkspaceIcons.FileAdded, "Docs"),
            OnboardingCapability.FromStrings("memory", static () => WorkspaceIcons.Sparkle, "Memory"),
            OnboardingCapability.FromStrings("projects", static () => WorkspaceIcons.Workspace, "Projects"),
            OnboardingCapability.FromStrings("background", static () => WorkspaceIcons.Clock, "Background"),
        ];
        _selectedCapability = Capabilities[0];
        Capabilities[0].IsSelected = true;

        Sources =
        [
            new OnboardingSource("projects", static () => WorkspaceIcons.Workspace, Loc.Onboarding_SourceProjects, Loc.Onboarding_SourceProjectsDescription),
            new OnboardingSource("apps", static () => WorkspaceIcons.Desktop, Loc.Onboarding_SourceApps, Loc.Onboarding_SourceAppsDescription),
            new OnboardingSource("sites", static () => WorkspaceIcons.Browser, Loc.Onboarding_SourceSites, Loc.Onboarding_SourceSitesDescription),
            new OnboardingSource("files", static () => WorkspaceIcons.Files, Loc.Onboarding_SourceFiles, Loc.Onboarding_SourceFilesDescription),
        ];

        UpdateStepDots();
    }

    public event Action<OnboardingCompletion>? OnboardingCompleted;

    public SetupSuggestionsViewModel? Setups { get; init; }

    /// <summary>Raised when a replayed welcome is closed early. Whatever it already set up stays.</summary>
    public event Action? Dismissed;

    public event Action<bool>? ThemeChanged;

    /// <summary>Raised when the user picks another language; the shell saves it and restarts Lumi in it.</summary>
    public event Action<string>? LanguageChangeRequested;

    /// <summary>One-shot moments for the ambient presence behind onboarding.</summary>
    public event Action<PresencePulse>? PresencePulseRequested;

    /// <summary>Pause between Lumi's lines, so the conversation reads like one. Tests set it to zero.</summary>
    internal TimeSpan Pacing { get; set; } = TimeSpan.FromMilliseconds(450);

    /// <summary>The shortest scan, so a fast one still reads as a moment rather than a flash. Tests set it to zero.</summary>
    internal TimeSpan MinimumScanDuration { get; set; } = TimeSpan.FromMilliseconds(900);

    // ═══════════════════════════════════════════════════════════════
    // Steps and navigation
    // ═══════════════════════════════════════════════════════════════

    [ObservableProperty] private int _currentStep;

    public ObservableCollection<OnboardingStepDot> StepDots { get; } = [];

    public OnboardingStep Step => (OnboardingStep)CurrentStep;
    public bool IsHelloStep => Step == OnboardingStep.Hello;
    public bool IsCapabilitiesStep => Step == OnboardingStep.Capabilities;
    public bool IsAutonomyStep => Step == OnboardingStep.Autonomy;
    public bool IsConnectStep => Step == OnboardingStep.Connect;
    public bool IsWorkspaceStep => Step == OnboardingStep.Workspace;
    public bool IsKnowYouStep => Step == OnboardingStep.KnowYou;
    public bool IsReadyStep => Step == OnboardingStep.Ready;

    public bool ShowBack => Step is OnboardingStep.Capabilities or OnboardingStep.Autonomy or OnboardingStep.Connect or OnboardingStep.KnowYou
                            || (IsWorkspaceStep && !IsScanningPhase);

    /// <summary>
    /// Someone who has been through the welcome opened it again, so they can close it at any step.
    /// The language stays as it is: changing it restarts Lumi, which would end the replay.
    /// </summary>
    public bool IsReplay { get; init; }

    public bool ShowLanguagePicker => IsHelloStep && !IsReplay;

    public bool CanContinue => Step switch
    {
        OnboardingStep.Hello => HasName,
        OnboardingStep.Workspace => !IsScanningPhase,
        OnboardingStep.KnowYou => IsConversationDone,
        _ => true,
    };

    public string PrimaryLabel => Step switch
    {
        OnboardingStep.Workspace when IsConsentPhase => Loc.Onboarding_LookAround,
        OnboardingStep.Workspace when IsScanningPhase => Loc.Onboarding_ScanningTitle,
        OnboardingStep.Workspace when NewlySelectedCount > 0 =>
            ManagementText.Count(NewlySelectedCount, Loc.Onboarding_SetUpProjectsOne, Loc.Onboarding_SetUpProjectsMany),
        OnboardingStep.Ready => Loc.Onboarding_GetStarted,
        _ => Loc.Onboarding_Continue,
    };

    public bool ShowSecondary => (IsWorkspaceStep && !IsResultsPhase) || (IsKnowYouStep && !IsConversationDone);

    public string SecondaryLabel => IsKnowYouStep ? Loc.Onboarding_SkipQuestions : Loc.Onboarding_Skip;

    partial void OnCurrentStepChanged(int value)
    {
        if (value is < 0 or >= StepCount)
        {
            CurrentStep = Math.Clamp(value, 0, StepCount - 1);
            return;
        }

        UpdateStepDots();
        NotifyNavigation();

        if (IsCapabilitiesStep)
            StartCapabilityTour();
        else
            StopCapabilityTour();

        if (IsKnowYouStep)
            _ = StartConversationAsync();
        if (IsReadyStep)
            PrepareReady();

        PresencePulseRequested?.Invoke(IsReadyStep ? PresencePulse.Bloom : PresencePulse.Awaken);
    }

    private void UpdateStepDots()
    {
        foreach (var dot in StepDots)
        {
            dot.IsCurrent = dot.Index == CurrentStep;
            dot.IsDone = dot.Index < CurrentStep;
        }
    }

    private void NotifyNavigation()
    {
        OnPropertyChanged(nameof(Step));
        OnPropertyChanged(nameof(IsHelloStep));
        OnPropertyChanged(nameof(IsCapabilitiesStep));
        OnPropertyChanged(nameof(IsAutonomyStep));
        OnPropertyChanged(nameof(IsConnectStep));
        OnPropertyChanged(nameof(IsWorkspaceStep));
        OnPropertyChanged(nameof(IsKnowYouStep));
        OnPropertyChanged(nameof(IsReadyStep));
        OnPropertyChanged(nameof(ShowBack));
        OnPropertyChanged(nameof(ShowLanguagePicker));
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(PrimaryLabel));
        OnPropertyChanged(nameof(ShowSecondary));
        OnPropertyChanged(nameof(SecondaryLabel));
        OnPropertyChanged(nameof(PresenceState));
        OnPropertyChanged(nameof(ShowHalo));
        OnPropertyChanged(nameof(PresenceFocus));
        ContinueCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task Continue()
    {
        switch (Step)
        {
            case OnboardingStep.Workspace when IsConsentPhase:
                await ScanWorkspaceAsync();
                break;
            case OnboardingStep.Workspace when IsResultsPhase:
                await SetUpSelectedProjectsAsync();
                StartPersonalization();
                CurrentStep++;
                break;
            case OnboardingStep.Ready:
                await CompleteAsync(null);
                break;
            case OnboardingStep.Workspace:
                break;
            default:
                if (CanContinue)
                    CurrentStep++;
                break;
        }
    }

    [RelayCommand]
    private void GoBack()
    {
        if (!ShowBack)
            return;
        if (IsWorkspaceStep && IsResultsPhase)
        {
            WorkspacePhase = OnboardingWorkspacePhase.Consent;
            return;
        }

        CurrentStep--;
    }

    [RelayCommand]
    private void Secondary()
    {
        if (IsWorkspaceStep)
            SkipWorkspace();
        else if (IsKnowYouStep)
            SkipQuestions();
    }

    // ═══════════════════════════════════════════════════════════════
    // Hello
    // ═══════════════════════════════════════════════════════════════

    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private bool _isDarkTheme = true;
    [ObservableProperty] private int _languageIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHe), nameof(IsShe), nameof(IsPronounUnspecified))]
    private OnboardingPronoun _pronoun;

    public bool HasName => !string.IsNullOrWhiteSpace(UserName);
    public string NiceToMeetText => HasName ? string.Format(Loc.Culture, Loc.Onboarding_NiceToMeet, UserName.Trim()) : "";
    public string CapabilitiesTitle => string.Format(Loc.Culture, Loc.Onboarding_CapabilitiesTitle, UserName.Trim());
    public string ReadyTitle => string.Format(Loc.Culture, Loc.Onboarding_ReadyTitle, UserName.Trim());
    public bool IsHe => Pronoun == OnboardingPronoun.He;
    public bool IsShe => Pronoun == OnboardingPronoun.She;
    public bool IsPronounUnspecified => Pronoun == OnboardingPronoun.Unspecified;
    public IReadOnlyList<string> LanguageNames { get; } = Loc.AvailableLanguages.Select(language => language.DisplayName).ToList();

    partial void OnUserNameChanged(string value)
    {
        OnPropertyChanged(nameof(HasName));
        OnPropertyChanged(nameof(NiceToMeetText));
        OnPropertyChanged(nameof(CapabilitiesTitle));
        OnPropertyChanged(nameof(ReadyTitle));
        NotifyNavigation();
    }

    partial void OnIsDarkThemeChanged(bool value) => ThemeChanged?.Invoke(value);

    partial void OnLanguageIndexChanged(int value)
    {
        if (value < 0 || value >= Loc.AvailableLanguages.Length)
            return;
        var code = Loc.AvailableLanguages[value].Code;
        if (!string.Equals(code, _activeLanguage, StringComparison.OrdinalIgnoreCase))
            LanguageChangeRequested?.Invoke(code);
    }

    [RelayCommand]
    private void ChoosePronoun(string? pronoun) => Pronoun = pronoun switch
    {
        "he" => OnboardingPronoun.He,
        "she" => OnboardingPronoun.She,
        _ => OnboardingPronoun.Unspecified,
    };

    [RelayCommand]
    private void ToggleTheme() => IsDarkTheme = !IsDarkTheme;

    // ═══════════════════════════════════════════════════════════════
    // What Lumi can do
    // ═══════════════════════════════════════════════════════════════

    public ObservableCollection<OnboardingCapability> Capabilities { get; }

    [ObservableProperty] private OnboardingCapability? _selectedCapability;

    partial void OnSelectedCapabilityChanged(OnboardingCapability? oldValue, OnboardingCapability? newValue)
    {
        if (oldValue is not null)
            oldValue.IsSelected = false;
        if (newValue is not null)
            newValue.IsSelected = true;
    }

    [RelayCommand]
    private void SelectCapability(OnboardingCapability? capability)
    {
        if (capability is null)
            return;
        // Once the user picks one, the tour stops moving on by itself.
        _capabilityPinned = true;
        StopCapabilityTour();
        SelectedCapability = capability;
    }

    private void StartCapabilityTour()
    {
        if (_capabilityPinned || _capabilityTimer is not null)
            return;
        _capabilityTimer = new DispatcherTimer(CapabilityInterval, DispatcherPriority.Normal, (_, _) => AdvanceCapability());
        _capabilityTimer.Start();
    }

    private void StopCapabilityTour()
    {
        _capabilityTimer?.Stop();
        _capabilityTimer = null;
    }

    private void AdvanceCapability()
    {
        var index = SelectedCapability is null ? 0 : Capabilities.IndexOf(SelectedCapability) + 1;
        SelectedCapability = Capabilities[index % Capabilities.Count];
    }

    // ═══════════════════════════════════════════════════════════════
    // Connect an AI
    // ═══════════════════════════════════════════════════════════════

    private GitHubLoginViewModel? _loginVM;
    private EasyConnectViewModel? _easyConnect;

    /// <summary>Shared GitHub login ViewModel — set by MainViewModel.</summary>
    public GitHubLoginViewModel? LoginVM
    {
        get => _loginVM;
        set
        {
            if (_loginVM is not null)
                _loginVM.AuthenticationChanged -= OnAuthChanged;
            _loginVM = value;
            if (_loginVM is not null)
                _loginVM.AuthenticationChanged += OnAuthChanged;
            OnAiChanged();
        }
    }

    /// <summary>AI accounts shared with Settings; set by MainViewModel.</summary>
    public EasyConnectViewModel? EasyConnect
    {
        get => _easyConnect;
        set
        {
            if (_easyConnect is not null)
                _easyConnect.PropertyChanged -= OnEasyConnectPropertyChanged;
            _easyConnect = value;
            if (_easyConnect is not null)
                _easyConnect.PropertyChanged += OnEasyConnectPropertyChanged;
            OnPropertyChanged();
            OnAiChanged();
        }
    }

    public bool IsAuthenticated => LoginVM?.IsAuthenticated == true;

    /// <summary>Whether Lumi has an AI to think with: GitHub Copilot or another connected account.</summary>
    public bool CanLearn => (IsAuthenticated && !_dataStore.Data.Settings.UseBYOKOnly)
                            || EasyConnect?.HasUsableConnections == true;

    public string ConnectStatus => CanLearn ? Loc.Onboarding_ConnectReady : Loc.Onboarding_ConnectMissing;
    public string AiStatus => CanLearn ? Loc.Onboarding_ReadyAiReady : Loc.Onboarding_ReadyAiMissing;

    private void OnAuthChanged(bool _) => OnAiChanged();

    private void OnEasyConnectPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EasyConnectViewModel.HasUsableConnections))
            OnAiChanged();
    }

    private void OnAiChanged()
    {
        OnPropertyChanged(nameof(IsAuthenticated));
        OnPropertyChanged(nameof(CanLearn));
        OnPropertyChanged(nameof(ConnectStatus));
        OnPropertyChanged(nameof(AiStatus));
    }

    // ═══════════════════════════════════════════════════════════════
    // Workspace discovery and projects
    // ═══════════════════════════════════════════════════════════════

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConsentPhase), nameof(IsScanningPhase), nameof(IsResultsPhase),
        nameof(WorkspaceTitle), nameof(WorkspaceSubtitle))]
    private OnboardingWorkspacePhase _workspacePhase;

    public bool IsConsentPhase => WorkspacePhase == OnboardingWorkspacePhase.Consent;
    public bool IsScanningPhase => WorkspacePhase == OnboardingWorkspacePhase.Scanning;
    public bool IsResultsPhase => WorkspacePhase == OnboardingWorkspacePhase.Results;

    public ObservableCollection<OnboardingSource> Sources { get; }
    public ObservableCollection<OnboardingProjectCandidate> Candidates { get; } = [];

    public bool HasCandidates => Candidates.Count > 0;
    public bool ShowSourceStatus => !IsConsentPhase;

    /// <summary>Selected candidates that are not projects yet.</summary>
    public int NewlySelectedCount => Candidates.Count(candidate => candidate.IsSelected && !candidate.IsAdded);

    public string NoticedApps => string.Join(" · ", _workspace.Apps.Take(8));
    public string NoticedSites => string.Join(" · ", _workspace.Sites.Take(8));
    public bool HasNoticedApps => _workspace.Apps.Count > 0;
    public bool HasNoticedSites => _workspace.Sites.Count > 0;

    public string WorkspaceTitle => WorkspacePhase switch
    {
        OnboardingWorkspacePhase.Scanning => Loc.Onboarding_ScanningTitle,
        OnboardingWorkspacePhase.Results => HasCandidates ? Loc.Onboarding_ResultsTitle : Loc.Onboarding_ResultsEmptyTitle,
        _ => Loc.Onboarding_WorkspaceTitle,
    };

    public string WorkspaceSubtitle => WorkspacePhase switch
    {
        OnboardingWorkspacePhase.Scanning => Loc.Onboarding_ScanningSubtitle,
        OnboardingWorkspacePhase.Results => HasCandidates ? Loc.Onboarding_ResultsSubtitle : Loc.Onboarding_ResultsEmptySubtitle,
        _ => Loc.Onboarding_WorkspaceSubtitle,
    };

    partial void OnWorkspacePhaseChanged(OnboardingWorkspacePhase value)
    {
        OnPropertyChanged(nameof(ShowSourceStatus));
        NotifyNavigation();
    }

    private async Task ScanWorkspaceAsync()
    {
        Setups?.RememberAppDiscoveryChoice(Sources.Single(source => source.Key == "apps").IsEnabled);
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        var scan = _scanCts = new CancellationTokenSource();
        var ct = scan.Token;

        foreach (var source in Sources)
        {
            source.State = source.IsEnabled ? OnboardingSourceState.Waiting : OnboardingSourceState.Skipped;
            source.Summary = source.IsEnabled ? "" : Loc.Onboarding_SourceSkipped;
        }
        WorkspacePhase = OnboardingWorkspacePhase.Scanning;

        IReadOnlyList<DiscoveredProject> projects = [];
        IReadOnlyList<string> apps = [];
        IReadOnlyList<string> sites = [];
        var documents = WorkspaceDocuments.Empty;
        try
        {
            await Task.WhenAll(
                ScanSourceAsync(Sources[0], _discovery.FindProjectsAsync, found => projects = found,
                    found => Found(found.Count, Loc.Onboarding_FoundProjectsOne, Loc.Onboarding_FoundProjectsMany), ct),
                ScanSourceAsync(Sources[1], _discovery.FindAppsAsync, found => apps = found,
                    found => Found(found.Count, Loc.Onboarding_FoundAppsOne, Loc.Onboarding_FoundAppsMany), ct),
                ScanSourceAsync(Sources[2], _discovery.FindSitesAsync, found => sites = found,
                    found => Found(found.Count, Loc.Onboarding_FoundSitesOne, Loc.Onboarding_FoundSitesMany), ct),
                ScanSourceAsync(Sources[3], _discovery.FindDocumentsAsync, found => documents = found,
                    found => Found(found.Recent, Loc.Onboarding_FoundFilesOne, Loc.Onboarding_FoundFilesMany), ct),
                MinimumScanDuration > TimeSpan.Zero ? Task.Delay(MinimumScanDuration, ct) : Task.CompletedTask);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (ReferenceEquals(_scanCts, scan))
                WorkspacePhase = OnboardingWorkspacePhase.Consent;
            return;
        }

        _workspace = new WorkspaceSnapshot(projects, apps, sites, documents);
        Setups?.ConsiderWorkspace(_workspace);
        BuildCandidates();
        WorkspacePhase = OnboardingWorkspacePhase.Results;

        // Prefetch Lumi's first words while the user looks over the projects.
        StartPersonalization(restart: true);
    }

    private static string Found(int count, string one, string many)
        => count == 0 ? Loc.Onboarding_FoundNothing : ManagementText.Count(count, one, many);

    private static async Task ScanSourceAsync<T>(
        OnboardingSource source,
        Func<CancellationToken, Task<T>> find,
        Action<T> apply,
        Func<T, string> describe,
        CancellationToken ct)
    {
        if (!source.IsEnabled)
            return;

        source.State = OnboardingSourceState.Scanning;
        try
        {
            var found = await find(ct);
            ct.ThrowIfCancellationRequested();
            apply(found);
            source.Summary = describe(found);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Onboarding] {source.Key} discovery failed: {ex.Message}");
            source.Summary = Loc.Onboarding_SourceUnavailable;
        }

        source.State = OnboardingSourceState.Done;
    }

    private void BuildCandidates()
    {
        foreach (var candidate in Candidates)
            candidate.PropertyChanged -= OnCandidatePropertyChanged;
        Candidates.Clear();

        var now = DateTimeOffset.Now;
        var preselected = 0;
        foreach (var project in _workspace.Projects)
        {
            var added = FindProjectByFolder(project.Path) is not null;
            var recent = (project.LastActive is { } active && now - active < RecentProject)
                         || project.EditorRank is >= 0 and < 5;
            var select = !added && recent && preselected < PreselectedProjects;
            if (select)
                preselected++;

            var candidate = new OnboardingProjectCandidate(project, added, select, now);
            candidate.PropertyChanged += OnCandidatePropertyChanged;
            Candidates.Add(candidate);
        }

        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(NewlySelectedCount));
        OnPropertyChanged(nameof(NoticedApps));
        OnPropertyChanged(nameof(NoticedSites));
        OnPropertyChanged(nameof(HasNoticedApps));
        OnPropertyChanged(nameof(HasNoticedSites));
        OnPropertyChanged(nameof(WorkspaceTitle));
        OnPropertyChanged(nameof(WorkspaceSubtitle));
        NotifyNavigation();
    }

    private void OnCandidatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OnboardingProjectCandidate.IsSelected) or nameof(OnboardingProjectCandidate.IsAdded))
        {
            OnPropertyChanged(nameof(NewlySelectedCount));
            OnPropertyChanged(nameof(PrimaryLabel));
        }
    }

    private void SkipWorkspace()
    {
        var wasScanning = IsScanningPhase;
        _scanCts?.Cancel();
        if (wasScanning || IsConsentPhase)
        {
            Setups?.RememberAppDiscoveryChoice(false);
            _workspace = WorkspaceSnapshot.Empty;
            // A scan the user backed out of and skipped no longer suggests anything.
            Setups?.ConsiderWorkspace(_workspace);
            WorkspacePhase = OnboardingWorkspacePhase.Consent;
        }

        StartPersonalization(restart: !_conversationStarted);
        CurrentStep = (int)OnboardingStep.KnowYou;
    }

    /// <summary>Creates a Lumi project for every selected folder that isn't one yet.</summary>
    internal async Task SetUpSelectedProjectsAsync()
    {
        var created = false;
        foreach (var candidate in Candidates.Where(candidate => candidate.IsSelected && !candidate.IsAdded).ToList())
        {
            if (FindProjectByFolder(candidate.Path) is null)
            {
                var project = new Project
                {
                    Name = UniqueProjectName(candidate),
                    WorkingDirectory = candidate.Path,
                    Instructions = BuildInstructions(candidate.Project),
                };
                _dataStore.Data.Projects.Add(project);
                _setUpProjects.Add(new OnboardingProjectSummary(project, candidate));
                created = true;
            }

            candidate.IsAdded = true;
        }

        if (created)
            await _dataStore.SaveAsync();
    }

    private Project? FindProjectByFolder(string folder)
    {
        var normalized = WorkspaceDiscovery.NormalizePath(folder);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return _dataStore.Data.Projects.FirstOrDefault(project =>
            !string.IsNullOrWhiteSpace(project.WorkingDirectory)
            && string.Equals(WorkspaceDiscovery.NormalizePath(project.WorkingDirectory), normalized, comparison));
    }

    private string UniqueProjectName(OnboardingProjectCandidate candidate)
    {
        bool Taken(string name) => _dataStore.Data.Projects.Any(project =>
            string.Equals(project.Name, name, StringComparison.CurrentCultureIgnoreCase));

        var name = candidate.Name;
        if (!Taken(name))
            return name;

        var parent = Path.GetFileName(Path.GetDirectoryName(candidate.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        if (!string.IsNullOrWhiteSpace(parent) && !Taken($"{name} ({parent})"))
            return $"{name} ({parent})";

        for (var number = 2; ; number++)
        {
            if (!Taken($"{name} {number}"))
                return $"{name} {number}";
        }
    }

    /// <summary>A short, editable description of the project, written into its instructions.</summary>
    internal static string BuildInstructions(DiscoveredProject project)
    {
        if (project.Kind == DiscoveredProjectKind.Folder)
            return Loc.Onboarding_InstructionsFolder;

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(project.Description))
            lines.Add(string.Format(Loc.Culture, Loc.Onboarding_InstructionsAbout, project.Description));
        if (!string.IsNullOrWhiteSpace(project.Stack))
            lines.Add(string.Format(Loc.Culture, Loc.Onboarding_InstructionsStack, project.Stack));
        if (!string.IsNullOrWhiteSpace(project.Remote))
            lines.Add(string.Format(Loc.Culture, Loc.Onboarding_InstructionsRepository, project.Remote));
        return string.Join('\n', lines);
    }

    // ═══════════════════════════════════════════════════════════════
    // Personalization
    // ═══════════════════════════════════════════════════════════════

    private void StartPersonalization(bool restart = false)
    {
        if (_personalizationTask is not null && (!restart || _conversationStarted))
            return;

        _personalizationCts?.Cancel();
        _personalizationCts?.Dispose();
        _personalizationCts = new CancellationTokenSource();
        var name = UserName.Trim();
        var workspace = _workspace;
        _personalizationTask = CanLearn
            ? PersonalizeSafelyAsync(name, workspace, _personalizationCts.Token)
            : Task.FromResult(OnboardingPersonalizer.Fallback(name, workspace));
    }

    private async Task<OnboardingPersonalization> PersonalizeSafelyAsync(string name, WorkspaceSnapshot workspace, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PersonalizationTimeout);
        try
        {
            return await _personalize(name, workspace, timeout.Token);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Onboarding] Personalization fell back: {ex.Message}");
            return OnboardingPersonalizer.Fallback(name, workspace);
        }
    }

    private async Task<OnboardingPersonalization> PersonalizeWithCopilotAsync(string name, WorkspaceSnapshot workspace, CancellationToken ct)
    {
        if (!_copilotService.IsConnected)
            await _copilotService.ConnectAsync(ct);
        return await OnboardingPersonalizer.GenerateAsync(_copilotService, name, workspace, ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // Getting to know you
    // ═══════════════════════════════════════════════════════════════

    public ObservableCollection<OnboardingChatItem> Conversation { get; } = [];

    [ObservableProperty] private bool _isConversationDone;
    [ObservableProperty] private bool _isLumiTyping;

    partial void OnIsConversationDoneChanged(bool value) => NotifyNavigation();
    partial void OnIsLumiTypingChanged(bool value) => OnPropertyChanged(nameof(PresenceState));

    private async Task StartConversationAsync()
    {
        if (_conversationStarted)
            return;
        _conversationStarted = true;

        StartPersonalization();
        var typing = OnboardingChatItem.Typing();
        Conversation.Add(typing);
        IsLumiTyping = true;
        var personalization = await _personalizationTask!;
        Conversation.Remove(typing);
        IsLumiTyping = false;
        _personalization = personalization;
        if (_conversationSkipped)
            return;

        Conversation.Add(OnboardingChatItem.FromLumi(personalization.Observation));
        await PauseAsync();
        AskNextQuestion();
    }

    private void AskNextQuestion()
    {
        if (_conversationSkipped || IsConversationDone)
            return;

        var questions = _personalization?.Questions ?? [];
        if (_questionIndex >= questions.Count)
        {
            Conversation.Add(OnboardingChatItem.FromLumi(string.Format(Loc.Culture, Loc.Onboarding_Closing, UserName.Trim())));
            IsConversationDone = true;
            return;
        }

        _pendingQuestion = OnboardingChatItem.Ask(questions[_questionIndex], (item, answer) => _ = AnswerAsync(item, answer));
        Conversation.Add(_pendingQuestion);
    }

    /// <summary>Records an answer: it joins the conversation and, when it is a lasting fact, becomes a memory.</summary>
    internal async Task AnswerAsync(OnboardingChatItem item, string answer)
    {
        if (item.Question is null || item.IsAnswered || !ReferenceEquals(item, _pendingQuestion))
            return;

        item.IsAnswered = true;
        _pendingQuestion = null;
        Conversation.Add(OnboardingChatItem.FromAnswer(answer));
        PresencePulseRequested?.Invoke(PresencePulse.Ripple);

        if (await RememberAsync(item.Question.Slot, answer) is { } memory)
            Conversation.Add(OnboardingChatItem.FromMemory(memory.Content));

        _questionIndex++;
        await PauseAsync();
        AskNextQuestion();
    }

    private async Task<Memory?> RememberAsync(string slot, string answer)
    {
        if (OnboardingPersonalizer.BuildDurableMemory(slot, UserName, answer) is not { } memory)
            return null;

        try
        {
            var result = await _memoryAgentService.SaveMemoryAsync(memory.Key, memory.Content, memory.Category, source: "onboarding");
            if (!result.StartsWith("Memory saved:", StringComparison.Ordinal)
                && !result.StartsWith("Memory already saved:", StringComparison.Ordinal))
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Onboarding] Could not save memory: {ex.Message}");
            return null;
        }

        var saved = _dataStore.Data.Memories.LastOrDefault(candidate =>
            string.Equals(candidate.Key, memory.Key, StringComparison.OrdinalIgnoreCase)
            && candidate.Scope == MemoryScopes.Global);
        if (saved is not null && !_savedMemories.Contains(saved))
            _savedMemories.Add(saved);
        return saved;
    }

    private void SkipQuestions()
    {
        _conversationSkipped = true;
        if (_pendingQuestion is not null)
        {
            _pendingQuestion.IsExpired = true;
            _pendingQuestion = null;
        }

        IsConversationDone = true;
        CurrentStep = (int)OnboardingStep.Ready;
    }

    private Task PauseAsync() => Pacing > TimeSpan.Zero ? Task.Delay(Pacing) : Task.CompletedTask;

    // ═══════════════════════════════════════════════════════════════
    // Ready
    // ═══════════════════════════════════════════════════════════════

    public ObservableCollection<OnboardingProjectSummary> ReadyProjects { get; } = [];
    public ObservableCollection<OnboardingMemoryItem> ReadyMemories { get; } = [];
    public ObservableCollection<OnboardingStarterItem> Starters { get; } = [];

    public bool HasReadyProjects => ReadyProjects.Count > 0;
    public bool HasReadyMemories => ReadyMemories.Count > 0;
    public bool HasStarters => Starters.Count > 0;

    private void PrepareReady()
    {
        ReadyProjects.Clear();
        foreach (var summary in _setUpProjects.Where(summary => _dataStore.Data.Projects.Contains(summary.Project)))
            ReadyProjects.Add(summary);

        ReadyMemories.Clear();
        foreach (var memory in _savedMemories.Where(_dataStore.Data.Memories.Contains))
            ReadyMemories.Add(new OnboardingMemoryItem(memory, ForgetMemory));

        Starters.Clear();
        if (CanLearn)
        {
            var projects = new Dictionary<string, Project>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in Candidates.Where(candidate => candidate.IsAdded))
            {
                if (FindProjectByFolder(candidate.Path) is { } project)
                    projects.TryAdd(candidate.Name, project);
            }

            var personalization = _personalization
                                  ?? (_personalizationTask is { IsCompletedSuccessfully: true } done ? done.Result : null)
                                  ?? OnboardingPersonalizer.Fallback(UserName, _workspace);
            foreach (var idea in OnboardingPersonalizer.SelectStarters(personalization.Starters, _workspace, projects.Keys.ToList()))
            {
                var project = idea.ProjectName is { } name && projects.TryGetValue(name, out var match) ? match : null;
                Starters.Add(new OnboardingStarterItem(idea, project, StartWith));
            }
        }

        OnPropertyChanged(nameof(HasReadyProjects));
        OnPropertyChanged(nameof(HasReadyMemories));
        OnPropertyChanged(nameof(HasStarters));
        OnPropertyChanged(nameof(AiStatus));
        OnPropertyChanged(nameof(ReadyApprovalText));
    }

    private void ForgetMemory(OnboardingMemoryItem item)
    {
        _dataStore.Data.Memories.Remove(item.Memory);
        _savedMemories.Remove(item.Memory);
        ReadyMemories.Remove(item);
        _ = _dataStore.SaveAsync();
        OnPropertyChanged(nameof(HasReadyMemories));
    }

    private void StartWith(OnboardingStarterItem starter) => _ = CompleteAsync(starter);

    private async Task CompleteAsync(OnboardingStarterItem? starter)
    {
        if (_isCompleting)
            return;
        _isCompleting = true;
        try
        {
            StopCapabilityTour();
            _scanCts?.Cancel();
            _personalizationCts?.Cancel();
            LoginVM?.CancelSignIn();

            var settings = _dataStore.Data.Settings;
            settings.UserName = UserName.Trim();
            settings.UserSex = Pronoun switch
            {
                OnboardingPronoun.He => "male",
                OnboardingPronoun.She => "female",
                _ => null,
            };
            if (LanguageIndex >= 0 && LanguageIndex < Loc.AvailableLanguages.Length)
                settings.Language = Loc.AvailableLanguages[LanguageIndex].Code;
            settings.IsDarkTheme = IsDarkTheme;
            settings.DefaultApprovalMode = Autonomy;
            settings.IsOnboarded = true;
            await _dataStore.SaveAsync();

            OnboardingCompleted?.Invoke(new OnboardingCompletion(starter?.Prompt, starter?.ProjectId));
        }
        finally
        {
            _isCompleting = false;
        }
    }

    [RelayCommand]
    private void Dismiss()
    {
        if (!IsReplay || _isCompleting)
            return;

        StopCapabilityTour();
        _scanCts?.Cancel();
        _personalizationCts?.Cancel();
        LoginVM?.CancelSignIn();
        Dismissed?.Invoke();
    }

    // ═══════════════════════════════════════════════════════════════
    // Ambient presence
    // ═══════════════════════════════════════════════════════════════

    public bool ShowAmbientPresence => _dataStore.Data.Settings.ShowAmbientPresence;

    public PresenceState PresenceState
        => (IsWorkspaceStep && IsScanningPhase) || (IsKnowYouStep && IsLumiTyping)
            ? PresenceState.Thinking
            : IsHelloStep ? PresenceState.Dormant : PresenceState.Idle;

    /// <summary>The glow gathers around Lumi's mark when the mark is the focus: hello and ready.</summary>
    public bool ShowHalo => IsHelloStep || IsReadyStep;

    public Point PresenceFocus => ShowHalo ? new Point(0.5, 0.26) : new Point(0.5, 0.42);
}
