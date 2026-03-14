using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    private bool _suppressComposerAgentSync;
    private bool _suppressComposerProjectSync;
    private CancellationTokenSource? _fileSearchCts;
    private readonly VoiceInputService _voiceService = new();
    private string _textBeforeVoice = "";
    private bool _voiceStarting;

    [ObservableProperty] private bool _sendWithEnter = true;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string? _selectedAgentName;
    [ObservableProperty] private string _selectedAgentGlyph = "◉";
    [ObservableProperty] private string? _selectedProjectName;
    [ObservableProperty] private string? _projectBadgeText;
    [ObservableProperty] private string? _agentBadgeText;
    [ObservableProperty] private string[]? _qualityLevels;

    // ── Plan (server may still generate plans) ──
    [ObservableProperty] private bool _hasPlan;
    [ObservableProperty] private string? _planContent;
    [ObservableProperty] private bool _isPlanOpen;

    partial void OnPlanContentChanged(string? value)
    {
        if (CurrentChat is not null)
        {
            CurrentChat.PlanContent = value;
            QueueSaveChat(CurrentChat, saveIndex: true);
        }
    }

    // ── SDK-discovered agents ──
    [ObservableProperty] private string? _selectedSdkAgentName;
    public ObservableCollection<StrataComposerChip> SdkAgentChips { get; } = [];

    // ── Account Quota ──
    [ObservableProperty] private string? _quotaDisplayText;
    [ObservableProperty] private double _quotaRemainingPercent = 100;
    [ObservableProperty] private bool _isQuotaLow;

    // ── Coding Project / Git ──
    [ObservableProperty] private bool _isCodingProject;
    partial void OnIsCodingProjectChanged(bool value) => OnPropertyChanged(nameof(ShowInfoStrip));
    [ObservableProperty] private string? _gitBranch;
    [ObservableProperty] private int _gitChangedFileCount;
    [ObservableProperty] private bool _isRefreshingGitStatus;
    [ObservableProperty] private bool _isWorktreeMode;
    [ObservableProperty] private string? _worktreePath;
    /// <summary>True when a chat exists (toggle is locked).</summary>
    public bool IsWorktreeLocked => CurrentChat is not null;
    private int _gitRefreshVersion;
    public ObservableCollection<GitFileChangeViewModel> GitChangedFiles { get; } = [];
    public bool HasGitChanges => GitChangedFileCount > 0;
    public bool ShowGitStatusBadge => IsRefreshingGitStatus || HasGitChanges;
    public string GitChangesLabel => GitChangedFileCount switch
    {
        _ when IsRefreshingGitStatus => Loc.Git_Refreshing,
        0 => Loc.Git_NoChanges,
        1 => Loc.Git_OneChange,
        _ => string.Format(Loc.Git_NChanges, GitChangedFileCount)
    };

    public event Action<List<GitFileChangeViewModel>>? GitChangesShowRequested;

    public ObservableCollection<StrataComposerChip> AvailableAgentChips { get; } = [];
    public ObservableCollection<StrataComposerChip> AvailableSkillChips { get; } = [];
    public ObservableCollection<StrataComposerChip> AvailableMcpChips { get; } = [];
    public ObservableCollection<StrataComposerChip> AvailableProjectChips { get; } = [];
    [ObservableProperty] private IEnumerable<StrataComposerChip>? _availableFileSuggestions;
    public ObservableCollection<FileAttachmentItem> PendingAttachmentItems { get; } = [];

    public bool IsWelcomeVisible => CurrentChat is null;
    public bool IsChatVisible => CurrentChat is not null;
    public bool HasPendingAttachments => PendingAttachmentItems.Count > 0;
    public bool HasProjectBadge => !string.IsNullOrWhiteSpace(ProjectBadgeText);
    public bool HasAgentBadge => !string.IsNullOrWhiteSpace(AgentBadgeText);
    public bool ShowBrowserToggle => HasUsedBrowser;

    public event Action<Guid?>? ComposerProjectFilterRequested;

    [RelayCommand]
    private void ToggleBrowserVisibility()
    {
        ToggleBrowser();
    }

    private static readonly string[] ReasoningLevels = [Loc.Quality_Low, Loc.Quality_Medium, Loc.Quality_High];

    private static bool IsReasoningModel(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId))
            return false;

        return modelId.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("o4", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("think", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateQualityLevels(string? modelId)
    {
        QualityLevels = IsReasoningModel(modelId) ? ReasoningLevels : null;
    }

    private void InitializeMvvmUiState()
    {
        SendWithEnter = _dataStore.Data.Settings.SendWithEnter;

        ActiveSkillChips.CollectionChanged += OnActiveSkillChipsCollectionChanged;
        ActiveMcpChips.CollectionChanged += OnActiveMcpChipsCollectionChanged;
        PendingAttachmentItems.CollectionChanged += OnPendingAttachmentItemsCollectionChanged;

        RefreshComposerCatalogs();
        SyncComposerAgentSelectionFromState();
        SyncComposerProjectSelectionFromState();
        RefreshProjectBadge();
        RefreshAgentBadge();
        UpdateQualityLevels(SelectedModel);
        _ = RefreshCodingProjectState();
    }

    public void RefreshComposerCatalogs()
    {
        // Start with Lumi agents
        var agentChips = _dataStore.Data.Agents
            .OrderBy(a => a.Name)
            .Select(a => new StrataComposerChip(a.Name, a.IconGlyph))
            .ToList();

        // Start with Lumi skills
        var skillChips = _dataStore.Data.Skills
            .OrderBy(s => s.Name)
            .Select(s => new StrataComposerChip(s.Name, s.IconGlyph))
            .ToList();

        // Discover workspace agents/skills from .github directory
        var workDir = GetEffectiveWorkingDirectory();
        DiscoverWorkspaceItems(workDir, agentChips, skillChips);

        ReplaceCollection(AvailableAgentChips, agentChips);
        ReplaceCollection(AvailableSkillChips, skillChips);

        // Build MCP chips: Lumi-configured MCPs + workspace MCPs from .vscode/mcp.json
        var mcpChips = _dataStore.Data.McpServers
            .Where(s => s.IsEnabled)
            .OrderBy(s => s.Name)
            .Select(s => new StrataComposerChip(s.Name))
            .ToList();
        var workspaceMcpNames = DiscoverWorkspaceMcps(workDir, mcpChips);
        ReplaceCollection(AvailableMcpChips, mcpChips);

        // Remove stale workspace MCPs from the previous project, then add current ones
        var staleWorkspaceMcps = ActiveMcpChips.OfType<StrataComposerChip>().Where(c => c.Glyph == "🔌").ToList();
        foreach (var stale in staleWorkspaceMcps)
        {
            ActiveMcpServerNames.Remove(stale.Name);
            ActiveMcpChips.Remove(stale);
        }
        foreach (var name in workspaceMcpNames)
        {
            if (!ActiveMcpServerNames.Contains(name))
            {
                ActiveMcpServerNames.Add(name);
                ActiveMcpChips.Add(new StrataComposerChip(name, "🔌"));
            }
        }

        ReplaceCollection(AvailableProjectChips,
            _dataStore.Data.Projects
                .OrderBy(p => p.Name)
                .Select(p => new StrataComposerChip(p.Name, "📁")));

        SyncComposerProjectSelectionFromState();
        RefreshProjectBadge();
    }

    /// <summary>
    /// Discovers workspace agents and skills from the .github directory.
    /// Supports two GitHub Copilot skill conventions:
    ///   1. Flat: .github/skills/*.md (e.g., travel-planner.md)
    ///   2. Folder: .github/Skills/&lt;name&gt;/SKILL.md (e.g., fluentsearch-translate/SKILL.md)
    /// Agent convention: .github/agents/*.md
    /// The SDK discovers these via ConfigDir when creating a session.
    /// </summary>
    private static void DiscoverWorkspaceItems(
        string workDir, List<StrataComposerChip> agentChips, List<StrataComposerChip> skillChips)
    {
        var githubDir = Path.Combine(workDir, ".github");
        if (!Directory.Exists(githubDir)) return;

        var existingAgentNames = agentChips.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingSkillNames = skillChips.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Discover agents — .github/agents/*.md (case-insensitive lookup)
        var agentsDir = FindSubdirectory(githubDir, "agents");
        if (agentsDir is not null)
        {
            foreach (var file in Directory.GetFiles(agentsDir, "*.md"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!existingAgentNames.Contains(name))
                    agentChips.Add(new StrataComposerChip(name, "🤖"));
            }
        }

        // Discover skills (case-insensitive lookup for both "skills" and "Skills")
        var skillsDir = FindSubdirectory(githubDir, "skills");
        if (skillsDir is not null)
        {
            // Pattern 1: flat files — .github/skills/*.md
            foreach (var file in Directory.GetFiles(skillsDir, "*.md"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!existingSkillNames.Contains(name))
                    skillChips.Add(new StrataComposerChip(name, "⚡"));
            }

            // Pattern 2: subdirectories with SKILL.md — .github/Skills/<name>/SKILL.md
            foreach (var dir in Directory.GetDirectories(skillsDir))
            {
                var skillFile = Path.Combine(dir, "SKILL.md");
                if (File.Exists(skillFile))
                {
                    var name = Path.GetFileName(dir);
                    if (!existingSkillNames.Contains(name))
                        skillChips.Add(new StrataComposerChip(name, "⚡"));
                }
            }
        }
    }

    /// <summary>Finds a subdirectory by name, case-insensitive.</summary>
    private static string? FindSubdirectory(string parentDir, string name)
    {
        var exact = Path.Combine(parentDir, name);
        if (Directory.Exists(exact)) return exact;

        // Case-insensitive fallback
        try
        {
            foreach (var dir in Directory.GetDirectories(parentDir))
            {
                if (string.Equals(Path.GetFileName(dir), name, StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
        }
        catch { /* best effort */ }

        return null;
    }

    /// <summary>
    /// Discovers MCP servers from .vscode/mcp.json in the workspace directory
    /// and adds them to the MCP chip list. Returns the names of discovered workspace MCPs.
    /// </summary>
    private static List<string> DiscoverWorkspaceMcps(string workDir, List<StrataComposerChip> mcpChips)
    {
        var discovered = new List<string>();
        var mcpJsonPath = Path.Combine(workDir, ".vscode", "mcp.json");
        if (!File.Exists(mcpJsonPath)) return discovered;

        var existingNames = mcpChips.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(mcpJsonPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("servers", out var servers)) return discovered;

            foreach (var server in servers.EnumerateObject())
            {
                if (!existingNames.Contains(server.Name))
                {
                    mcpChips.Add(new StrataComposerChip(server.Name, "🔌"));
                    discovered.Add(server.Name);
                }
            }
        }
        catch { /* best effort */ }

        return discovered;
    }

    /// <summary>
    /// After a real session is created, queries the SDK to discover additional agents
    /// and merges them into the composer pickers.
    /// </summary>
    private async Task PopulateFromSessionAsync()
    {
        if (_activeSession is null) return;

        try
        {
            var agents = await _copilotService.ListSessionAgentsAsync(_activeSession);

            var lumiAgentNames = _dataStore.Data.Agents.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var currentChips = AvailableAgentChips.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sdkOnly = agents.Where(a => !lumiAgentNames.Contains(a.Name) && !currentChips.Contains(a.Name)).ToList();

            if (sdkOnly.Count > 0)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var agent in sdkOnly.OrderBy(a => a.DisplayName ?? a.Name))
                        AvailableAgentChips.Add(new StrataComposerChip(agent.DisplayName ?? agent.Name, "🤖"));
                });
            }
        }
        catch { /* best effort */ }
    }

    public void HandleFileQueryChanged(string query)
    {
        _fileSearchCts?.Cancel();
        _fileSearchCts?.Dispose();

        var cts = new CancellationTokenSource();
        _fileSearchCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                // A small debounce avoids filesystem churn while the user is still typing.
                await Task.Delay(90, token);
                if (token.IsCancellationRequested)
                    return;

                var results = SearchFiles(query);
                if (token.IsCancellationRequested)
                    return;

                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested)
                        return;

                    AvailableFileSuggestions = results;
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException)
            {
                // Expected when query changes quickly.
            }
        }, token);
    }

    public void HandleFileSelected(string filePath)
    {
        AddAttachment(filePath);
    }

    partial void OnCurrentChatChanged(Chat? value)
    {
        OnPropertyChanged(nameof(IsWelcomeVisible));
        OnPropertyChanged(nameof(IsChatVisible));
        OnPropertyChanged(nameof(IsWorktreeLocked));
        SyncComposerProjectSelectionFromState();
        RefreshProjectBadge();

        if (value is null)
        {
            // Returning to welcome — show static welcome suggestions
            SuggestionA = Loc.Chat_SuggestionA;
            SuggestionB = Loc.Chat_SuggestionB;
            SuggestionC = Loc.Chat_SuggestionC;
            IsSuggestionsGenerating = false;
        }
    }

    partial void OnActiveAgentChanged(LumiAgent? value)
    {
        SyncComposerAgentSelectionFromState();
        RefreshAgentBadge();
    }

    partial void OnHasUsedBrowserChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBrowserToggle));
    }

    partial void OnProjectBadgeTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasProjectBadge));
    }

    partial void OnAgentBadgeTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasAgentBadge));
    }

    partial void OnGitChangedFileCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasGitChanges));
        OnPropertyChanged(nameof(ShowGitStatusBadge));
        OnPropertyChanged(nameof(GitChangesLabel));
    }

    partial void OnIsRefreshingGitStatusChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowGitStatusBadge));
        OnPropertyChanged(nameof(GitChangesLabel));
    }

    partial void OnSelectedAgentNameChanged(string? value)
    {
        if (_suppressComposerAgentSync)
            return;

        ApplyComposerAgentSelection(value);
    }

    public void ApplyComposerAgentSelection(string? value)
    {
        if (IsLoadingChat)
        {
            SyncComposerAgentSelectionFromState();
            return;
        }

        if (string.Equals(ActiveAgent?.Name, value, StringComparison.Ordinal)
            && string.Equals(SelectedSdkAgentName, value, StringComparison.Ordinal))
            return;

        if (string.IsNullOrWhiteSpace(value))
        {
            SetActiveAgent(null);
            SelectedSdkAgentName = null;
            return;
        }

        // First check Lumi agents
        var agent = _dataStore.Data.Agents.FirstOrDefault(a => a.Name == value);
        if (agent is not null)
        {
            SelectedSdkAgentName = null; // Clear SDK agent when switching to Lumi agent
            SetActiveAgent(agent);
            return;
        }

        // Not a Lumi agent — check if it's an SDK workspace agent
        // (identified by presence in AvailableAgentChips with 🤖 glyph)
        var isSdkAgent = AvailableAgentChips.Any(c => c.Name == value && c.Glyph == "🤖");
        if (isSdkAgent)
        {
            SetActiveAgent(null); // Clear Lumi agent when switching to SDK agent
            SelectedSdkAgentName = value;
            return;
        }

        SyncComposerAgentSelectionFromState();
    }

    partial void OnSelectedProjectNameChanged(string? value)
    {
        if (_suppressComposerProjectSync)
            return;

        if (string.IsNullOrWhiteSpace(value))
        {
            ClearProjectId();
            ComposerProjectFilterRequested?.Invoke(null);
            return;
        }

        var project = _dataStore.Data.Projects.FirstOrDefault(p => p.Name == value);
        if (project is null)
        {
            SyncComposerProjectSelectionFromState();
            return;
        }

        var isExistingChat = CurrentChat is not null && CurrentChat.Messages.Count > 0;
        if (!isExistingChat)
            SetProjectId(project.Id);

        ComposerProjectFilterRequested?.Invoke(project.Id);
    }

    private void SyncComposerAgentSelectionFromState()
    {
        _suppressComposerAgentSync = true;
        try
        {
            // Prefer SDK agent name if set, otherwise use Lumi agent
            SelectedAgentName = SelectedSdkAgentName ?? ActiveAgent?.Name;
            SelectedAgentGlyph = SelectedSdkAgentName is not null ? "🤖" : (ActiveAgent?.IconGlyph ?? "◉");
        }
        finally
        {
            _suppressComposerAgentSync = false;
        }
    }

    public void SyncComposerProjectSelectionFromState()
    {
        _suppressComposerProjectSync = true;
        try
        {
            SelectedProjectName = GetCurrentProjectName();
        }
        finally
        {
            _suppressComposerProjectSync = false;
        }
    }

    private void RefreshProjectBadge()
    {
        var projectName = GetCurrentProjectName();
        ProjectBadgeText = string.IsNullOrWhiteSpace(projectName) ? null : $"📁 {projectName}";
    }

    private void RefreshAgentBadge()
    {
        if (SelectedSdkAgentName is not null)
            AgentBadgeText = $"🤖 {SelectedSdkAgentName}";
        else
            AgentBadgeText = ActiveAgent is null ? null : $"{ActiveAgent.IconGlyph} {ActiveAgent.Name}";
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }

    private void OnPendingAttachmentItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasPendingAttachments));
    }

    private void OnActiveSkillChipsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (IsLoadingChat)
            return;

        if (args.Action == NotifyCollectionChangedAction.Add && args.NewItems is not null)
        {
            foreach (var item in args.NewItems)
            {
                if (item is StrataComposerChip chip)
                    RegisterSkillIdByName(chip.Name);
            }
        }
    }

    private void OnActiveMcpChipsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (IsLoadingChat)
            return;

        if (args.Action == NotifyCollectionChangedAction.Add && args.NewItems is not null)
        {
            foreach (var item in args.NewItems)
            {
                if (item is StrataComposerChip chip)
                    RegisterMcpByName(chip.Name);
            }
            return;
        }

        if (args.Action == NotifyCollectionChangedAction.Remove && args.OldItems is not null)
        {
            foreach (var item in args.OldItems)
            {
                if (item is StrataComposerChip chip)
                    ActiveMcpServerNames.Remove(chip.Name);
            }
            SyncActiveMcpsToChat();
            return;
        }

        if (args.Action == NotifyCollectionChangedAction.Reset)
        {
            ActiveMcpServerNames.Clear();
            SyncActiveMcpsToChat();
        }
    }

    // ── Voice input ──────────────────────────────────────

    [RelayCommand]
    private async Task ToggleVoice()
    {
        if (!_voiceService.IsAvailable || _voiceStarting)
            return;

        if (_voiceService.IsRecording)
        {
            await _voiceService.StopAsync();
            IsRecording = false;
            return;
        }

        _voiceStarting = true;
        _textBeforeVoice = PromptText ?? "";

        _voiceService.HypothesisGenerated += OnVoiceHypothesis;
        _voiceService.ResultGenerated += OnVoiceResult;
        _voiceService.Stopped += OnVoiceStopped;
        _voiceService.Error += OnVoiceError;

        var culture = CultureInfo.CurrentUICulture;
        var language = culture.Name.Contains('-') ? culture.Name : culture.IetfLanguageTag;
        if (string.IsNullOrEmpty(language) || !language.Contains('-'))
            language = "en-US";

        await _voiceService.StartAsync(language);

        _voiceStarting = false;
        if (_voiceService.IsRecording)
            IsRecording = true;

        FocusComposerRequested?.Invoke();
    }

    private void OnVoiceHypothesis(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var baseText = _textBeforeVoice;
            if (!string.IsNullOrEmpty(baseText) && !baseText.EndsWith(' '))
                baseText += " ";
            PromptText = baseText + text;
        });
    }

    private void OnVoiceResult(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var baseText = _textBeforeVoice;
            if (!string.IsNullOrEmpty(baseText) && !baseText.EndsWith(' '))
                baseText += " ";
            _textBeforeVoice = baseText + text;
            PromptText = _textBeforeVoice;
        });
    }

    private void OnVoiceError(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText = message == "speech_privacy"
                ? Loc.Voice_SpeechPrivacyRequired
                : $"{Loc.Voice_Error}: {message}";
        });
    }

    private void OnVoiceStopped()
    {
        _voiceService.HypothesisGenerated -= OnVoiceHypothesis;
        _voiceService.ResultGenerated -= OnVoiceResult;
        _voiceService.Stopped -= OnVoiceStopped;
        _voiceService.Error -= OnVoiceError;

        Dispatcher.UIThread.Post(() => IsRecording = false);
    }

    /// <summary>Cleans up voice resources. Called when the view is being detached.</summary>
    public void StopVoiceIfRecording()
    {
        if (_voiceService.IsRecording)
        {
            _ = _voiceService.StopAsync();
            IsRecording = false;
        }
    }

    /// <summary>Raised when the composer should receive focus (e.g., after attaching files or voice toggle).</summary>
    public event Action? FocusComposerRequested;

    // ── Attach files (requires view interaction for file picker) ──

    /// <summary>Raised when user requests file attachment. The view handles the file picker dialog.</summary>
    public event Action? AttachFilesRequested;

    [RelayCommand]
    private void RequestAttachFiles()
    {
        AttachFilesRequested?.Invoke();
    }

    // ── Chip removal commands (bound via Strata ICommand properties) ──

    [RelayCommand]
    private void RemoveAgent()
    {
        ApplyComposerAgentSelection(null);
    }

    [RelayCommand]
    private void RemoveProject()
    {
        SelectedProjectName = null;
    }

    [RelayCommand]
    private void RemoveSkill(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            RemoveSkillByName(name);
    }

    [RelayCommand]
    private void RemoveMcp(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            RemoveMcpByName(name);
    }

    // ── File autocomplete commands ──

    [RelayCommand]
    private void HandleFileQuery(string? query)
    {
        HandleFileQueryChanged(query ?? "");
    }

    [RelayCommand]
    private void HandleFileSelection(string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            HandleFileSelected(filePath);
            FocusComposerRequested?.Invoke();
        }
    }

    // ── Clipboard paste (View handles actual clipboard access, ViewModel handles attachment) ──

    /// <summary>Raised when the composer detects a clipboard image paste.
    /// The view should read the clipboard, save the image, and call <see cref="AddAttachment"/>.</summary>
    public event Action? ClipboardPasteRequested;

    [RelayCommand]
    private void RequestClipboardPaste()
    {
        ClipboardPasteRequested?.Invoke();
    }

    // ── Session Mode commands ──

    [RelayCommand]
    private async Task RefreshPlan()
    {
        if (_activeSession is null) return;
        try
        {
            var (exists, content) = await _copilotService.ReadSessionPlanAsync(_activeSession);
            HasPlan = exists;
            PlanContent = content;
        }
        catch { /* best effort */ }
    }

    // ── SDK-discovered agent commands ──

    partial void OnSelectedSdkAgentNameChanged(string? value)
    {
        SyncComposerAgentSelectionFromState();
        RefreshAgentBadge();

        if (CurrentChat is not null)
        {
            CurrentChat.SdkAgentName = value;
            QueueSaveChat(CurrentChat, saveIndex: false);
        }
    }

    [RelayCommand]
    private void ClearSdkAgent()
    {
        SelectedSdkAgentName = null;
    }

    // ── Account Quota ──

    public async Task RefreshQuotaAsync()
    {
        try
        {
            var quota = await _copilotService.GetAccountQuotaAsync();
            if (quota?.QuotaSnapshots is not { Count: > 0 }) return;

            // Use the first (primary) quota snapshot
            var snapshot = quota.QuotaSnapshots.Values.First();

            Dispatcher.UIThread.Post(() =>
            {
                QuotaRemainingPercent = snapshot.RemainingPercentage;
                IsQuotaLow = QuotaRemainingPercent < 20;

                var used = snapshot.UsedRequests;
                var total = snapshot.EntitlementRequests;
                var reset = snapshot.ResetDate;

                if (total > 0)
                    QuotaDisplayText = $"{used:N0} / {total:N0} requests ({QuotaRemainingPercent:N0}% remaining)";
                else
                    QuotaDisplayText = $"{QuotaRemainingPercent:N0}% remaining";

                // Cache in settings for display
                var settings = _dataStore.Data.Settings;
                settings.QuotaRemainingPercentage = snapshot.RemainingPercentage;
                settings.QuotaUsedRequests = snapshot.UsedRequests;
                settings.QuotaEntitlementRequests = snapshot.EntitlementRequests;
                settings.QuotaResetDate = reset;
            });
        }
        catch { /* best effort */ }
    }

    // ── Git / Coding project helpers ──────────────────────

    /// <summary>Gets the project's original working directory (ignoring worktree override).</summary>
    private string GetProjectWorkingDirectory()
    {
        var pid = CurrentChat?.ProjectId ?? _pendingProjectId ?? ActiveProjectFilterId;
        if (pid.HasValue)
        {
            var project = _dataStore.Data.Projects.FirstOrDefault(p => p.Id == pid.Value);
            if (project is { WorkingDirectory: { Length: > 0 } dir } && Directory.Exists(dir))
                return dir;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>Detects whether the current project is a coding project and refreshes git state.</summary>
    public async Task RefreshCodingProjectState()
    {
        // Increment version so any in-flight async refresh is discarded on completion.
        var version = Interlocked.Increment(ref _gitRefreshVersion);

        // Always use the original project dir for git detection (not worktree)
        var projectDir = GetProjectWorkingDirectory();
        var isGit = GitService.IsGitRepo(projectDir);
        IsCodingProject = isGit;

        // Worktree state comes exclusively from the current chat's persisted data.
        // On welcome screen (no chat), always reset to local.
        string? savedWorktreePath = null;
        if (CurrentChat?.WorktreePath is { Length: > 0 } savedWt && Directory.Exists(savedWt))
        {
            savedWorktreePath = savedWt;
            WorktreePath = savedWt;
            IsWorktreeMode = true;
        }
        else
        {
            WorktreePath = null;
            IsWorktreeMode = false;
        }

        // Reset stale branch/change data immediately when switching chats.
        GitBranch = null;
        GitChangedFileCount = 0;
        GitChangedFiles.Clear();
        IsRefreshingGitStatus = isGit;

        if (!isGit)
        {
            return;
        }

        // Use the effective dir (worktree or project) for status
        var workDir = savedWorktreePath ?? projectDir;
        var branchTask = GitService.GetCurrentBranchAsync(workDir);
        var changesTask = GitService.GetChangedFilesAsync(workDir);

        await Task.WhenAll(branchTask, changesTask).ConfigureAwait(false);

        var branch = await branchTask;
        var changes = await changesTask;

        // A newer refresh was started while we were awaiting — discard these stale results.
        if (version != Volatile.Read(ref _gitRefreshVersion))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            // Double-check inside the UI dispatch in case another refresh snuck in.
            if (version != Volatile.Read(ref _gitRefreshVersion))
                return;

            GitBranch = branch;
            GitChangedFileCount = changes.Count;
            GitChangedFiles.Clear();
            foreach (var c in changes)
                GitChangedFiles.Add(new GitFileChangeViewModel(c));
            IsRefreshingGitStatus = false;
        });
    }

    /// <summary>Toggles worktree mode. Only works before a chat is created (on the welcome screen).
    /// The actual worktree is created lazily when the first message is sent.</summary>
    [RelayCommand]
    private void ToggleWorktreePreChat()
    {
        // Locked once a chat exists
        if (CurrentChat is not null) return;

        var projectDir = GetProjectWorkingDirectory();
        if (!IsWorktreeMode && !GitService.IsGitRepo(projectDir)) return;

        IsWorktreeMode = !IsWorktreeMode;
        if (!IsWorktreeMode)
            WorktreePath = null;
    }

    [RelayCommand]
    private void ShowGitChanges()
    {
        if (GitChangedFiles.Count > 0)
            GitChangesShowRequested?.Invoke(GitChangedFiles.ToList());
    }

    [RelayCommand]
    private async Task RefreshGitStatus()
    {
        await RefreshCodingProjectState();
    }

    // ── Branch flyout actions ──────────────────────────────

    /// <summary>Raised when text needs to be copied to clipboard. View handles actual clipboard access.</summary>
    public event Action<string>? CopyToClipboardRequested;

    [RelayCommand]
    private void OpenInTerminal()
    {
        var dir = GetEffectiveWorkingDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "wt",
                Arguments = $"-d \"{dir}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // Fallback to cmd if Windows Terminal is not installed
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    WorkingDirectory = dir,
                    UseShellExecute = true,
                });
            }
            catch { /* ignore */ }
        }
    }

    [RelayCommand]
    private void OpenInExplorer()
    {
        var dir = GetEffectiveWorkingDirectory();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch { /* ignore */ }
    }

    [RelayCommand]
    private void OpenInIDE()
    {
        var dir = GetEffectiveWorkingDirectory();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "code",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true,
            });
        }
        catch { /* ignore */ }
    }

    [RelayCommand]
    private void CopyBranchName()
    {
        if (GitBranch is { Length: > 0 } branch)
            CopyToClipboardRequested?.Invoke(branch);
    }

    [RelayCommand]
    private void CopyDirectoryPath()
    {
        var dir = GetEffectiveWorkingDirectory();
        CopyToClipboardRequested?.Invoke(dir);
    }
}
