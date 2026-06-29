using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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
    private bool _suppressActiveMcpCollectionSync;
    private CancellationTokenSource? _fileSearchCts;
    private readonly VoiceInputService _voiceService = new();
    private Chat? _currentChatTitleSource;
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
    [ObservableProperty] private string? _selectedQuality;
    [ObservableProperty] private string[]? _contextWindowTiers;
    [ObservableProperty] private string? _selectedContextWindowTier;
    private bool _suppressModelSelectionSideEffects;
    private bool _suppressSelectedQualitySync;
    private bool _suppressSelectedContextWindowTierSync;

    // ── Plan (server may still generate plans) ──
    [ObservableProperty] private bool _hasPlan;
    [ObservableProperty] private string? _planContent;
    [ObservableProperty] private bool _isPlanOpen;

    partial void OnPlanContentChanged(string? value)
    {
        if (CurrentChat is null || string.Equals(CurrentChat.PlanContent, value, StringComparison.Ordinal))
            return;

        CurrentChat.PlanContent = value;
        QueueSaveChat(CurrentChat, saveIndex: true);
    }

    // ── Skill preview (opened from a transcript skill chip) ──
    [ObservableProperty] private bool _isSkillOpen;
    [ObservableProperty] private string? _skillPreviewContent;
    [ObservableProperty] private string? _skillPreviewTitle;

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
    private string? _gitStatusDirectory;
    private CancellationTokenSource? _gitRefreshThrottleCts;
    private DateTime _lastLiveGitRefreshUtc = DateTime.MinValue;
    /// <summary>Minimum interval between live git status refreshes triggered by agent
    /// file edits, so the coding strip updates promptly without thrashing during bursts.</summary>
    private static readonly TimeSpan GitRefreshMinInterval = TimeSpan.FromMilliseconds(700);
    public ObservableCollection<GitFileChangeViewModel> GitChangedFiles { get; } = [];
    /// <summary>Existing worktrees available for selection (excludes main repo).</summary>
    public ObservableCollection<WorktreeInfo> AvailableWorktrees { get; } = [];
    public bool HasAvailableWorktrees => AvailableWorktrees.Count > 0;
    public bool HasGitChanges => GitChangedFileCount > 0;
    public bool ShowGitStatusBadge => IsRefreshingGitStatus || HasGitChanges;
    public string GitBranchLabel => !string.IsNullOrWhiteSpace(GitBranch)
        ? GitBranch
        : IsRefreshingGitStatus
            ? Loc.Git_Refreshing
            : "Git";
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
    public bool HasHeaderSubtitle => HasProjectBadge || HasAgentBadge;
    public bool HasHeaderSubtitleSeparator => HasProjectBadge && HasAgentBadge;
    public bool ShowBrowserToggle => HasUsedBrowser;

    public event Action<Guid?>? ComposerProjectFilterRequested;

    [RelayCommand]
    private void ToggleBrowserVisibility()
    {
        ToggleBrowser();
    }

    // Model reasoning effort capabilities from SDK
    private Dictionary<string, List<string>> _modelReasoningEfforts = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _modelDefaultEfforts = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, long> _modelContextTokenLimits = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, long> _modelLongContextTokenLimits = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _modelsWithLongContext = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Updates the model capabilities cache from SDK ModelInfo list.
    /// Called by MainViewModel after fetching models from the SDK.
    /// </summary>
    public void UpdateModelCapabilities(
        List<GitHub.Copilot.ModelInfo> models,
        IReadOnlySet<string>? longContextModelIds = null,
        IReadOnlyDictionary<string, ModelContextWindowLimits>? contextWindowLimits = null)
    {
        ModelSelectionHelper.ApplyModelCapabilities(
            models,
            _modelReasoningEfforts,
            _modelDefaultEfforts,
            _modelContextTokenLimits);
        ApplyContextWindowLimits(contextWindowLimits);
        _modelsWithLongContext = CopyModelIdSet(longContextModelIds);
        UpdateQualityLevels(SelectedModel);
        UpdateContextWindowTiers(SelectedModel);

        if (CurrentChat is { } chat)
        {
            var runtime = GetOrCreateRuntimeState(chat.Id);
            ApplyKnownContextTokenLimit(chat, runtime, ResolveSelectedModelForChat(chat), updateDisplayed: true);
        }
    }

    public void CopyModelCatalogFrom(ChatViewModel source)
    {
        ArgumentNullException.ThrowIfNull(source);

        AvailableModels.Clear();
        foreach (var model in source.AvailableModels)
            AvailableModels.Add(model);

        _modelReasoningEfforts = source._modelReasoningEfforts.ToDictionary(
            static kvp => kvp.Key,
            static kvp => kvp.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);
        _modelDefaultEfforts = new Dictionary<string, string>(
            source._modelDefaultEfforts,
            StringComparer.OrdinalIgnoreCase);
        _modelContextTokenLimits = new Dictionary<string, long>(
            source._modelContextTokenLimits,
            StringComparer.OrdinalIgnoreCase);
        _modelLongContextTokenLimits = new Dictionary<string, long>(
            source._modelLongContextTokenLimits,
            StringComparer.OrdinalIgnoreCase);
        _modelsWithLongContext = new HashSet<string>(
            source._modelsWithLongContext,
            StringComparer.OrdinalIgnoreCase);
        UpdateQualityLevels(SelectedModel);
        UpdateContextWindowTiers(SelectedModel);
    }

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

    private static HashSet<string> CopyModelIdSet(IEnumerable<string>? modelIds)
        => modelIds?
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private void ApplyContextWindowLimits(IReadOnlyDictionary<string, ModelContextWindowLimits>? contextWindowLimits)
    {
        _modelLongContextTokenLimits.Clear();
        if (contextWindowLimits is null)
            return;

        foreach (var (modelId, limits) in contextWindowLimits)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                continue;

            if (limits.Default > 0)
                _modelContextTokenLimits[modelId] = limits.Default;
            else if (limits.LongContext is > 0)
                _modelContextTokenLimits.Remove(modelId);

            if (limits.LongContext is > 0 and var longContextLimit)
                _modelLongContextTokenLimits[modelId] = longContextLimit;
        }
    }

    private string? GetStoredReasoningEffortPreference()
    {
        if (CurrentChat is { LastReasoningEffortUsed: { Length: > 0 } chatEffort })
            return chatEffort;

        return string.IsNullOrWhiteSpace(_dataStore.Data.Settings.ReasoningEffort)
            ? null
            : _dataStore.Data.Settings.ReasoningEffort;
    }

    internal string? GetSelectedReasoningEffort()
    {
        var explicitEffort = ModelSelectionHelper.DisplayToEffort(SelectedQuality);
        if (!string.IsNullOrWhiteSpace(explicitEffort))
        {
            return ModelSelectionHelper.NormalizeEffort(
                explicitEffort,
                SelectedModel,
                _modelReasoningEfforts,
                _modelDefaultEfforts);
        }

        return ModelSelectionHelper.NormalizeEffort(
            GetStoredReasoningEffortPreference(),
            SelectedModel,
            _modelReasoningEfforts,
            _modelDefaultEfforts);
    }

    internal string? GetPersistedReasoningEffortPreference()
    {
        var normalizedEffort = GetSelectedReasoningEffort();
        if (!string.IsNullOrWhiteSpace(normalizedEffort))
            return normalizedEffort;

        var explicitEffort = ModelSelectionHelper.DisplayToEffort(SelectedQuality);
        if (!string.IsNullOrWhiteSpace(explicitEffort))
            return explicitEffort;

        return GetStoredReasoningEffortPreference();
    }

    private string? GetStoredContextWindowTierPreference()
    {
        if (CurrentChat is { LastContextWindowTierUsed: { Length: > 0 } chatTier })
            return chatTier;

        return string.IsNullOrWhiteSpace(_dataStore.Data.Settings.ContextWindowTier)
            ? ModelContextWindowTiers.Default
            : _dataStore.Data.Settings.ContextWindowTier;
    }

    internal string? GetSelectedContextWindowTier()
    {
        var explicitTier = ModelSelectionHelper.DisplayToContextWindowTier(SelectedContextWindowTier);
        if (!string.IsNullOrWhiteSpace(explicitTier))
            return ModelSelectionHelper.NormalizeContextWindowTier(explicitTier, SelectedModel, _modelsWithLongContext);

        return ModelSelectionHelper.NormalizeContextWindowTier(
            GetStoredContextWindowTierPreference(),
            SelectedModel,
            _modelsWithLongContext);
    }

    internal string? ResolveSelectedModelForChat(Chat chat)
    {
        if (!string.IsNullOrWhiteSpace(chat.LastModelUsed))
            return chat.LastModelUsed;

        if (CurrentChat?.Id == chat.Id && !string.IsNullOrWhiteSpace(SelectedModel))
            return SelectedModel;

        return string.IsNullOrWhiteSpace(_dataStore.Data.Settings.PreferredModel)
            ? null
            : _dataStore.Data.Settings.PreferredModel;
    }

    internal string? ResolvePersistedReasoningEffortForChat(Chat chat, string? modelId)
    {
        if (CurrentChat?.Id == chat.Id)
            return GetPersistedReasoningEffortPreference();

        var storedEffort = !string.IsNullOrWhiteSpace(chat.LastReasoningEffortUsed)
            ? chat.LastReasoningEffortUsed
            : _dataStore.Data.Settings.ReasoningEffort;

        if (string.IsNullOrWhiteSpace(storedEffort))
            return null;

        return ModelSelectionHelper.NormalizeEffort(
            storedEffort,
            modelId,
            _modelReasoningEfforts,
            _modelDefaultEfforts) ?? storedEffort;
    }

    internal string? ResolveSelectedContextWindowTierForChat(Chat chat, string? modelId)
    {
        var storedTier = CurrentChat?.Id == chat.Id
            ? GetSelectedContextWindowTier()
            : !string.IsNullOrWhiteSpace(chat.LastContextWindowTierUsed)
                ? chat.LastContextWindowTierUsed
                : _dataStore.Data.Settings.ContextWindowTier;

        return ModelSelectionHelper.NormalizeContextWindowTier(storedTier, modelId, _modelsWithLongContext);
    }

    private void SyncSelectedQualityFromState(string? modelId = null, string? preferredEffort = null)
    {
        if (QualityLevels is null)
        {
            SetSelectedQualityValue(null);
            return;
        }

        var display = ModelSelectionHelper.ResolveSelectedQualityDisplay(
            preferredEffort ?? GetStoredReasoningEffortPreference(),
            modelId ?? SelectedModel,
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
            preferredTier ?? GetStoredContextWindowTierPreference(),
            modelId ?? SelectedModel,
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

    internal void ApplyModelSelection(string? modelId, string? reasoningEffort, string? contextWindowTier = null)
    {
        _suppressModelSelectionSideEffects = true;
        try
        {
            SetSelectedModelValue(modelId);
            SyncSelectedQualityFromState(modelId, reasoningEffort);
            SyncSelectedContextWindowTierFromState(modelId, contextWindowTier);
        }
        finally
        {
            _suppressModelSelectionSideEffects = false;
        }
    }

    partial void OnSelectedQualityChanged(string? value)
    {
        if (_suppressSelectedQualitySync || _suppressModelSelectionSideEffects)
            return;

        var effort = GetPersistedReasoningEffortPreference();
        var persistedEffort = effort ?? string.Empty;

        if (CurrentChat is null || CurrentChat.Messages.Count == 0)
        {
            if (_dataStore.Data.Settings.ReasoningEffort != persistedEffort)
            {
                _dataStore.Data.Settings.ReasoningEffort = persistedEffort;
                _dataStore.Save();
            }

            DefaultModelSelectionChanged?.Invoke(SelectedModel ?? string.Empty, effort, GetSelectedContextWindowTier());
            return;
        }

        if (CurrentChat.LastReasoningEffortUsed != effort)
            CurrentChat.LastReasoningEffortUsed = effort;

        QueueModelSelectionSave();
        QueueMidSessionModelSelectionSync();
    }

    partial void OnSelectedContextWindowTierChanged(string? value)
    {
        if (_suppressSelectedContextWindowTierSync || _suppressModelSelectionSideEffects)
            return;

        var contextTier = GetSelectedContextWindowTier();
        if (contextTier is null)
            return;

        if (CurrentChat is null || CurrentChat.Messages.Count == 0)
        {
            if (_dataStore.Data.Settings.ContextWindowTier != contextTier)
            {
                _dataStore.Data.Settings.ContextWindowTier = contextTier;
                _dataStore.Save();
            }

            DefaultModelSelectionChanged?.Invoke(SelectedModel ?? string.Empty, GetPersistedReasoningEffortPreference(), contextTier);
            return;
        }

        if (CurrentChat.LastContextWindowTierUsed != contextTier)
            CurrentChat.LastContextWindowTierUsed = contextTier;

        QueueModelSelectionSave();
        QueueMidSessionModelSelectionSync();
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
        UpdateContextWindowTiers(SelectedModel);
        QueueRefreshCodingProjectState();
    }

    public void RefreshComposerCatalogs(bool syncProjectContextMcpSelections = true)
    {
        // Start with Lumi agents
        var agentChips = _dataStore.Data.Agents
            .OrderBy(a => a.Name)
            .Select(a => new StrataComposerChip(
                a.Name,
                a.IconGlyph,
                SecondaryText: BuildChipSearchText(a.Description, a.SystemPrompt)))
            .ToList();

        // Start with Lumi skills
        var skillChips = _dataStore.Data.Skills
            .OrderBy(s => s.Name)
            .Select(s => new StrataComposerChip(
                s.Name,
                s.IconGlyph,
                SecondaryText: BuildChipSearchText(s.Description, s.Content)))
            .ToList();

        // Discover project-scoped and user-level Copilot agents. Skills remain either Lumi-owned
        // or SDK-native through SkillDirectories; Lumi does not advertise file skills itself.
        var projectContextCatalog = GetProjectContextCatalog();
        DiscoverCopilotAgents(projectContextCatalog, agentChips);

        ReplaceCollection(AvailableAgentChips, agentChips);
        ReplaceCollection(AvailableSkillChips, skillChips);

        // Build MCP chips: Lumi-configured MCPs + project-scoped MCPs from .vscode/mcp.json
        var mcpChips = _dataStore.Data.McpServers
            .Where(s => s.IsEnabled)
            .OrderBy(s => s.Name)
            .Select(s => new StrataComposerChip(s.Name))
            .ToList();
        var projectContextMcpNames = AddProjectContextMcpChips(projectContextCatalog.McpServers, mcpChips);
        ReplaceCollection(AvailableMcpChips, mcpChips);

        // Remove stale project-context MCPs from the previous project, then add current ones.
        List<StrataComposerChip> staleProjectContextMcps;
        var addedProjectContextMcps = false;
        _suppressActiveMcpCollectionSync = true;
        try
        {
            staleProjectContextMcps = ActiveMcpChips.OfType<StrataComposerChip>().Where(c => c.Glyph == "🔌").ToList();
            foreach (var stale in staleProjectContextMcps)
            {
                ActiveMcpServerNames.Remove(stale.Name);
                ActiveMcpChips.Remove(stale);
            }

            foreach (var name in projectContextMcpNames)
            {
                if (!ActiveMcpServerNames.Contains(name))
                {
                    ActiveMcpServerNames.Add(name);
                    ActiveMcpChips.Add(new StrataComposerChip(name, "🔌"));
                    addedProjectContextMcps = true;
                }
            }
        }
        finally
        {
            _suppressActiveMcpCollectionSync = false;
        }

        // Persist project-context MCPs to the chat so they survive reload.
        if (syncProjectContextMcpSelections
            && (addedProjectContextMcps || staleProjectContextMcps.Count > 0)
            && !IsLoadingChat)
            SyncActiveMcpsToChat();

        ReplaceCollection(AvailableProjectChips,
            _dataStore.Data.Projects
                .OrderBy(p => p.Name)
                .Select(p => new StrataComposerChip(
                    p.Name,
                    "📁",
                    SecondaryText: BuildProjectInlineCompletionSecondaryText(p))));

        SyncComposerProjectSelectionFromState();
        RefreshProjectBadge();
    }

    /// <summary>
    /// Discovers file-based Copilot agents from the workspace and the user's
    /// <c>~\.copilot</c> directory.
    /// </summary>
    private static void DiscoverCopilotAgents(
        ProjectContextCatalogSnapshot catalog,
        List<StrataComposerChip> agentChips)
    {
        var existingAgentNames = agentChips.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var agent in catalog.Agents)
        {
            if (existingAgentNames.Contains(agent.Name))
                continue;

            agentChips.Add(new StrataComposerChip(
                agent.Name,
                ExternalAgentGlyph,
                SecondaryText: BuildChipSearchText(agent.Description, agent.Content)));
            existingAgentNames.Add(agent.Name);
        }
    }

    private static string? BuildChipSearchText(string? summary, string? fallback, int maxLength = 140)
    {
        if (!string.IsNullOrWhiteSpace(summary))
            return CollapseInlineCompletionText(summary, maxLength);

        if (string.IsNullOrWhiteSpace(fallback))
            return null;

        return CollapseInlineCompletionText(fallback, maxLength);
    }

    private static string? CollapseInlineCompletionText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var collapsed = string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (collapsed.Length <= maxLength)
            return collapsed;

        return collapsed[..(maxLength - 3)] + "...";
    }

    /// <summary>
    /// Discovers MCP servers from .vscode/mcp.json in project context directories
    /// and adds them to the MCP chip list. Returns the names of discovered project MCPs.
    /// </summary>
    private static List<string> AddProjectContextMcpChips(
        IReadOnlyList<ProjectContextMcpServerDefinition> contextMcpServers,
        List<StrataComposerChip> mcpChips)
    {
        var discovered = new List<string>();
        var existingNames = mcpChips.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var server in contextMcpServers)
        {
            if (existingNames.Contains(server.Name))
                continue;

            mcpChips.Add(new StrataComposerChip(server.Name, "🔌"));
            discovered.Add(server.Name);
            existingNames.Add(server.Name);
        }

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
            var result = await _activeSession.Rpc.Agent.ListAsync();
            var agents = result.Agents;

            var lumiAgentNames = _dataStore.Data.Agents.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var currentChips = AvailableAgentChips.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sdkOnly = agents.Where(a => !lumiAgentNames.Contains(a.Name) && !currentChips.Contains(a.Name)).ToList();

            if (sdkOnly.Count > 0)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var agent in sdkOnly.OrderBy(a => a.DisplayName ?? a.Name))
                        AvailableAgentChips.Add(new StrataComposerChip(agent.DisplayName ?? agent.Name, ExternalAgentGlyph));
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

                var results = SearchFiles(query, cancellationToken: token);
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
        if (_currentChatTitleSource is not null)
            _currentChatTitleSource.PropertyChanged -= OnCurrentChatPropertyChanged;

        _currentChatTitleSource = value;
        if (_currentChatTitleSource is not null)
            _currentChatTitleSource.PropertyChanged += OnCurrentChatPropertyChanged;

        OnPropertyChanged(nameof(IsWelcomeVisible));
        OnPropertyChanged(nameof(IsChatVisible));
        OnPropertyChanged(nameof(IsWorktreeLocked));
        SyncComposerProjectSelectionFromState();
        RefreshProjectBadge();

        if (value is null)
        {
            _suggestionDisplayChatId = null;
            // Returning to welcome — clear transcript suggestions
            ClearSuggestions();
            RefreshComposerCatalogs(); // Re-scan for welcome state (no project)
        }
        else
        {
            _suggestionDisplayChatId = value.Id;
        }
    }

    private void OnCurrentChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Chat.Title))
            OnPropertyChanged(nameof(CurrentChatTitle));
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
        OnPropertyChanged(nameof(HasHeaderSubtitle));
        OnPropertyChanged(nameof(HasHeaderSubtitleSeparator));
    }

    partial void OnAgentBadgeTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasAgentBadge));
        OnPropertyChanged(nameof(HasHeaderSubtitle));
        OnPropertyChanged(nameof(HasHeaderSubtitleSeparator));
    }

    partial void OnGitChangedFileCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasGitChanges));
        OnPropertyChanged(nameof(ShowGitStatusBadge));
        OnPropertyChanged(nameof(GitChangesLabel));
    }

    partial void OnGitBranchChanged(string? value)
    {
        OnPropertyChanged(nameof(GitBranchLabel));
    }

    partial void OnIsRefreshingGitStatusChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowGitStatusBadge));
        OnPropertyChanged(nameof(GitChangesLabel));
        OnPropertyChanged(nameof(GitBranchLabel));
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

        // Not a Lumi agent — check if it's an SDK/workspace agent
        // (identified by presence in AvailableAgentChips with the external-agent glyph)
        var isSdkAgent = AvailableAgentChips.Any(c => c.Name == value && c.Glyph == ExternalAgentGlyph);
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
            SelectedAgentGlyph = SelectedSdkAgentName is not null ? ExternalAgentGlyph : (ActiveAgent?.IconGlyph ?? "◉");
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
        ProjectBadgeText = string.IsNullOrWhiteSpace(projectName) ? null : projectName;
    }

    private void RefreshAgentBadge()
    {
        if (SelectedSdkAgentName is not null)
            AgentBadgeText = SelectedSdkAgentName;
        else
            AgentBadgeText = ActiveAgent?.Name;
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }

    private static string? BuildProjectInlineCompletionSecondaryText(Project project)
    {
        if (!string.IsNullOrWhiteSpace(project.WorkingDirectory))
            return CollapseInlineCompletionText(project.WorkingDirectory, 80);

        if (project.AdditionalContextDirectories.Count > 0)
            return CollapseInlineCompletionText(ProjectContextDirectoryHelper.FormatFolderList(project.AdditionalContextDirectories), 80);

        return BuildChipSearchText(project.Instructions, null);
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
        if (IsLoadingChat || _suppressActiveMcpCollectionSync)
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
            if (string.Equals(CurrentChat.SdkAgentName, value, StringComparison.Ordinal))
                return;

            var previousValue = CurrentChat.SdkAgentName;
            CurrentChat.SdkAgentName = value;
            QueueSaveChat(CurrentChat, saveIndex: true);

            var projectContextCatalog = GetProjectContextCatalog();
            var previousExternalAgent = !string.IsNullOrWhiteSpace(previousValue)
                && FindExternalAgentByName(projectContextCatalog, previousValue) is not null;
            var currentExternalAgent = !string.IsNullOrWhiteSpace(value)
                && FindExternalAgentByName(projectContextCatalog, value) is not null;

            if (CurrentChat.CopilotSessionId is not null && (previousExternalAgent || currentExternalAgent))
            {
                InvalidateCurrentSession();
                _pendingSkillInjections.Clear();
            }
            else if (_activeSession is not null)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    _ = SelectAgentOnSessionAsync(value);
                else if (ActiveAgent is null)
                    _ = DeselectAgentOnSessionAsync();
            }
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
                settings.QuotaResetDate = reset?.ToString("O");
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

        try
        {
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

            if (!isGit)
            {
                GitBranch = null;
                GitChangedFileCount = 0;
                GitChangedFiles.Clear();
                _gitStatusDirectory = null;
                AvailableWorktrees.Clear();
                OnPropertyChanged(nameof(HasAvailableWorktrees));
                return;
            }

            // Use the effective dir (worktree or project) for status
            var workDir = savedWorktreePath ?? projectDir;
            var normalizedWorkDir = Path.GetFullPath(workDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(_gitStatusDirectory, normalizedWorkDir, StringComparison.OrdinalIgnoreCase))
                GitBranch = null;
            _gitStatusDirectory = normalizedWorkDir;

            // Reset stale change data immediately; keep the branch visible while refreshing
            // the same coding context so the strip does not flicker blank.
            GitChangedFileCount = 0;
            GitChangedFiles.Clear();
            IsRefreshingGitStatus = true;

            var branchTask = GitService.GetCurrentBranchAsync(workDir);
            var changesTask = GitService.GetChangedFilesAsync(workDir);
            var worktreesTask = GitService.ListWorktreeInfoAsync(projectDir);

            await Task.WhenAll(branchTask, changesTask, worktreesTask).ConfigureAwait(false);

            var branch = await branchTask;
            var changes = await changesTask;
            var worktrees = await worktreesTask;

            // Exclude the main repo worktree (it's the "Local" option). git reports the main
            // worktree as the repository root, which differs from the project working directory
            // when that directory is a subfolder — so exclude by git root, not just project dir.
            // Normalize paths to handle forward/backward slash differences from git output.
            static string NormalizePath(string p) =>
                Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedProjectDir = NormalizePath(projectDir);
            var gitRoot = GitService.FindRepoRoot(projectDir);
            var normalizedGitRoot = gitRoot is not null ? NormalizePath(gitRoot) : normalizedProjectDir;
            worktrees.RemoveAll(w =>
            {
                var normalizedWorktree = NormalizePath(w.Path);
                return string.Equals(normalizedWorktree, normalizedProjectDir, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(normalizedWorktree, normalizedGitRoot, StringComparison.OrdinalIgnoreCase);
            });

            // A newer refresh was started while we were awaiting — discard these stale results.
            if (version != Volatile.Read(ref _gitRefreshVersion))
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Double-check inside the UI dispatch in case another refresh snuck in.
                if (version != Volatile.Read(ref _gitRefreshVersion))
                    return;

                GitBranch = branch;
                GitChangedFileCount = changes.Count;
                GitChangedFiles.Clear();
                foreach (var c in changes)
                    GitChangedFiles.Add(new GitFileChangeViewModel(c));

                AvailableWorktrees.Clear();
                foreach (var wt in worktrees)
                    AvailableWorktrees.Add(wt);
                OnPropertyChanged(nameof(HasAvailableWorktrees));
            });
        }
        finally
        {
            if (version == Volatile.Read(ref _gitRefreshVersion))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (version == Volatile.Read(ref _gitRefreshVersion))
                        IsRefreshingGitStatus = false;
                });
            }
        }
    }

    private void QueueRefreshCodingProjectState()
    {
        _ = RefreshCodingProjectStateSafelyAsync();
    }

    /// <summary>Refreshes the coding strip's git status shortly after the agent edits
    /// workspace files, so the change count stays live while a turn is still running —
    /// independent of the IsBusy turn lifecycle. Throttled with a leading edge plus a
    /// trailing refresh: rapid successive edits update promptly but at most once per
    /// <see cref="GitRefreshMinInterval"/>. Must be called on the UI thread.</summary>
    private void QueueLiveGitRefresh()
    {
        if (!IsCodingProject)
            return;

        _gitRefreshThrottleCts?.Cancel();
        _gitRefreshThrottleCts?.Dispose();
        _gitRefreshThrottleCts = null;

        var sinceLast = DateTime.UtcNow - _lastLiveGitRefreshUtc;
        if (sinceLast >= GitRefreshMinInterval)
        {
            // Leading edge: nothing refreshed recently, so update immediately.
            _lastLiveGitRefreshUtc = DateTime.UtcNow;
            QueueRefreshCodingProjectState();
            return;
        }

        // Within the throttle window: schedule a single trailing refresh.
        var delay = GitRefreshMinInterval - sinceLast;
        var cts = new CancellationTokenSource();
        _gitRefreshThrottleCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Marshal back to the UI thread so the synchronous prefix of the refresh
            // (which mutates bound observable state) does not run off-thread.
            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested)
                    return;
                _lastLiveGitRefreshUtc = DateTime.UtcNow;
                QueueRefreshCodingProjectState();
            });
        }, token);
    }

    private async Task RefreshCodingProjectStateSafelyAsync()
    {
        try
        {
            await RefreshCodingProjectState().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Lumi] Git status refresh failed: {ex}");
        }
    }

    /// <summary>Toggles worktree mode. Only works before a chat is created (on the welcome screen).
    /// The actual worktree is created lazily when the first message is sent.</summary>
    [RelayCommand]
    private async Task ToggleWorktreePreChat()
    {
        await SetWorktreeModePreChatAsync(!IsWorktreeMode);
    }

    [RelayCommand]
    private async Task SwitchToLocalPreChat()
    {
        await SetWorktreeModePreChatAsync(false);
    }

    [RelayCommand]
    private async Task SwitchToWorktreePreChat()
    {
        await SetWorktreeModePreChatAsync(true);
    }

    private async Task SetWorktreeModePreChatAsync(bool useWorktree)
    {
        // Locked once a chat exists
        if (CurrentChat is not null) return;

        var projectDir = GetProjectWorkingDirectory();
        if (useWorktree && !GitService.IsGitRepo(projectDir)) return;

        if (IsWorktreeMode == useWorktree) return;

        IsWorktreeMode = useWorktree;
        if (!IsWorktreeMode)
        {
            WorktreePath = null;
            // Refresh branch display back to the main repo
            var branch = await GitService.GetCurrentBranchAsync(projectDir).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => GitBranch = branch);
        }
    }

    /// <summary>Selects an existing worktree. Sets worktree mode with the given path
    /// so no new worktree is created on first message.</summary>
    [RelayCommand]
    private async Task SelectExistingWorktree(string path)
    {
        if (CurrentChat is not null) return;
        if (!Directory.Exists(path)) return;

        IsWorktreeMode = true;
        WorktreePath = path;

        // Update branch display to reflect the selected worktree
        var branch = await GitService.GetCurrentBranchAsync(path).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() => GitBranch = branch);
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
