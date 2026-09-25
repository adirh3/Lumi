using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Services;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

/// <summary>
/// What the Workspace panel is showing. <see cref="Overview"/> is the at-a-glance home; every other
/// page is a focused view that opens beside the chat inside the same panel.
/// </summary>
public enum WorkspacePage
{
    Overview,
    Plan,
    Agents,
    GitChanges,
    Diff,
    Skill,
    FilePreview,
    Browser,
}

/// <summary>
/// The chat's Workspace: one side panel that shows everything Lumi did in the chat at a glance —
/// the plan, agents, git changes, delivered files, edits, skills, links, sources, your messages and
/// the activity timeline — searchable in one place, each row jumping to the transcript or opening a
/// focused page. A category bar keeps every kind one click away: it narrows the overview to that
/// kind's full list. Aggregated from the chat so the transcript stays a clean conversation.
/// <para>The panel's pages and header are owned by the view (<c>WorkspacePanelController</c>), which
/// mirrors only the open state and current page here, for the header toggle and the presence layer.</para>
/// </summary>
public partial class ChatViewModel
{
    private const int PlanPreviewSteps = 3;

    private readonly Dictionary<Guid, CachedAssistantLinks> _assistantLinkCache = [];

    // Reverse index (activity seed → owning turn StableId), rebuilt with the panel. The activity
    // timeline uses it to resolve each row's transcript jump target in O(1). Without it, every
    // activity rescans all turns and their items (allocating interpolated id strings per compare),
    // which is quadratic and froze the UI for many seconds when opening very large chats.
    private readonly Dictionary<string, string> _activitySeedToTurnId = new(StringComparer.Ordinal);

    private static readonly string[] TurnSeedPrefixes = { "turn:tool:", "turn:question:", "turn:message:" };
    private static readonly string[] ItemSeedPrefixes = { "tool:", "subagent:", "question:", "message:error:" };

    // ── Overview sections (newest first) ──
    public WorkspaceSection<SubagentToolCallItem> WorkspaceAgents { get; } = new(WorkspaceCategory.Agents, 2,
        static (run, q) => Contains(run.DisplayName, q) || Contains(run.Title, q) || Contains(run.ModelDisplayName, q));
    public WorkspaceSection<GitFileChangeViewModel> WorkspaceGitFiles { get; } = new(WorkspaceCategory.Changes, 3,
        static (file, q) => Contains(file.RelativePath, q));
    public WorkspaceSection<FileAttachmentItem> WorkspaceFiles { get; } = new(WorkspaceCategory.Files, 3,
        static (file, q) => Contains(file.FileName, q) || Contains(file.FilePath, q));
    public WorkspaceSection<FileChangeItem> WorkspaceEdits { get; } = new(WorkspaceCategory.Edits, 3,
        static (change, q) => Contains(change.FileName, q) || Contains(change.FilePath, q));
    public WorkspaceSection<SkillChipItem> WorkspaceSkills { get; } = new(WorkspaceCategory.Skills, 3,
        static (skill, q) => Contains(skill.Name, q) || Contains(skill.Description, q));
    public WorkspaceSection<SourceItem> WorkspaceLinks { get; } = new(WorkspaceCategory.Links, 3, MatchesSource);
    public WorkspaceSection<SourceItem> WorkspaceSources { get; } = new(WorkspaceCategory.Sources, 3, MatchesSource);
    public WorkspaceSection<WorkspaceUserMessageItem> WorkspaceMessages { get; } = new(WorkspaceCategory.Messages, 3,
        static (message, q) => Contains(message.Preview, q) || Contains(message.MetaText, q) || Contains(message.NumberLabel, q));
    public WorkspaceSection<WorkspaceActivityItem> WorkspaceActivity { get; } = new(WorkspaceCategory.Activity, 3,
        static (activity, q) => Contains(activity.Title, q) || Contains(activity.Subtitle, q));

    /// <summary>Sections holding what this chat produced. Git is the repository's state, so it is
    /// searched with the rest but never counts as chat content (it must not auto-open the panel).</summary>
    private IWorkspaceSection[] ChatContentSections => _chatContentSections ??=
    [
        WorkspaceAgents, WorkspaceFiles, WorkspaceEdits, WorkspaceSkills,
        WorkspaceLinks, WorkspaceSources, WorkspaceMessages, WorkspaceActivity,
    ];

    /// <summary>Every section the search runs over. A new section only needs to be listed here.</summary>
    private IWorkspaceSection[] WorkspaceSections => _workspaceSections ??= [.. ChatContentSections, WorkspaceGitFiles];

    private IWorkspaceSection[]? _chatContentSections;
    private IWorkspaceSection[]? _workspaceSections;

    /// <summary>True when this chat produced anything the overview lists (drives auto-open).</summary>
    public bool HasWorkspaceContent => HasPlan || ChatContentSections.Any(static section => section.TotalCount > 0);

    /// <summary>Nothing to show at all: not from this chat, not from the repository, no browser.</summary>
    public bool ShowWorkspaceEmptyState => !HasWorkspaceContent && !HasWorkspaceGit && !ShowBrowserToggle;

    /// <summary>True when an error was recorded this chat (the presence layer answers new errors).</summary>
    [ObservableProperty] private bool _hasErrorActivities;

    /// <summary>Raised after the workspace sections change so the view can re-evaluate visibility.</summary>
    public event Action? WorkspaceContentChanged;

    // ── Category bar: every kind of content one click away ──

    /// <summary>The chips above the overview, in display order. A new kind of content is one chip
    /// here plus its availability and count in <see cref="RefreshWorkspaceCategories"/>.</summary>
    public IReadOnlyList<WorkspaceCategoryChip> WorkspaceCategories { get; } =
    [
        new(WorkspaceCategory.All, Loc.Workspace_All),
        new(WorkspaceCategory.Plan, Loc.Workspace_Plan, opensPage: true),
        new(WorkspaceCategory.Agents, Loc.Workspace_Agents),
        new(WorkspaceCategory.Changes, Loc.Workspace_Changes),
        new(WorkspaceCategory.Files, Loc.Workspace_Files),
        new(WorkspaceCategory.Edits, Loc.Workspace_Edits),
        new(WorkspaceCategory.Skills, Loc.Workspace_Skills),
        new(WorkspaceCategory.Links, Loc.Workspace_Links),
        new(WorkspaceCategory.Sources, Loc.Workspace_Sources),
        new(WorkspaceCategory.Messages, Loc.Workspace_Messages),
        new(WorkspaceCategory.Activity, Loc.Workspace_Activity),
        new(WorkspaceCategory.Browser, Loc.Browser_Title, opensPage: true),
    ];

    /// <summary>The kind the overview is narrowed to; <see cref="WorkspaceCategory.All"/> shows everything.</summary>
    [ObservableProperty] private WorkspaceCategory _workspaceCategory = WorkspaceCategory.All;

    partial void OnWorkspaceCategoryChanged(WorkspaceCategory value)
    {
        foreach (var section in WorkspaceSections)
            section.SetFocus(value);

        NotifyWorkspaceVisibilityChanged();
    }

    /// <summary>A chip click: narrow to that kind (again for everything), or open the plan / browser.</summary>
    [RelayCommand]
    private void SelectWorkspaceCategory(WorkspaceCategory category)
    {
        switch (category)
        {
            case WorkspaceCategory.Plan:
                OpenWorkspacePlan();
                break;
            case WorkspaceCategory.Browser:
                RequestShowBrowser();
                break;
            default:
                WorkspaceCategory = category == WorkspaceCategory ? WorkspaceCategory.All : category;
                break;
        }
    }

    private void RefreshWorkspaceCategories()
    {
        // A kind that emptied (a cleared or rebuilt chat) hands the overview back to everything.
        if (WorkspaceCategory != WorkspaceCategory.All && !IsWorkspaceCategoryAvailable(WorkspaceCategory))
        {
            WorkspaceCategory = WorkspaceCategory.All; // re-enters through NotifyWorkspaceVisibilityChanged
            return;
        }

        foreach (var chip in WorkspaceCategories)
        {
            chip.IsAvailable = IsWorkspaceCategoryAvailable(chip.Category);
            chip.IsSelected = chip.Category == WorkspaceCategory;
            chip.CountLabel = SectionFor(chip.Category)?.CountLabel;
            chip.IsLive = chip.Category == WorkspaceCategory.Agents && HasRunningSubagents;
        }
    }

    private bool IsWorkspaceCategoryAvailable(WorkspaceCategory category) => category switch
    {
        WorkspaceCategory.All => true,
        WorkspaceCategory.Plan => HasPlan,
        WorkspaceCategory.Changes => HasWorkspaceGit,
        WorkspaceCategory.Browser => ShowBrowserToggle,
        _ => SectionFor(category)?.TotalCount > 0,
    };

    private IWorkspaceSection? SectionFor(WorkspaceCategory category)
        => Array.Find(WorkspaceSections, section => section.Category == category);

    // ── Plan at a glance ──
    [ObservableProperty] private string _planTitle = "";
    [ObservableProperty] private string? _planSummaryText;
    [ObservableProperty] private string? _planProgressLabel;
    [ObservableProperty] private double _planProgress;
    [ObservableProperty] private bool _hasPlanProgress;
    public ObservableCollection<WorkspacePlanStep> PlanSteps { get; } = [];
    public bool HasPlanSteps => PlanSteps.Count > 0;

    /// <summary>A plan written as prose (no bullets) previews its first line instead.</summary>
    public bool HasPlanSummaryText => !HasPlanSteps && !string.IsNullOrWhiteSpace(PlanSummaryText);

    public bool ShowWorkspacePlan => HasPlan
        && WorkspaceCategory == WorkspaceCategory.All
        && (!HasWorkspaceSearch || Contains(PlanContent, WorkspaceSearchText.Trim()));

    private void RefreshWorkspacePlan()
    {
        var summary = WorkspacePlanSummary.Parse(HasPlan ? PlanContent : null);
        PlanTitle = summary.Title ?? Loc.Plan_Title;
        PlanSummaryText = summary.Summary;
        HasPlanProgress = summary.HasTasks;
        PlanProgress = summary.HasTasks ? summary.CompletedCount * 100d / summary.TaskCount : 0;
        PlanProgressLabel = summary.HasTasks
            ? string.Format(Loc.Culture, Loc.Workspace_PlanProgress, summary.CompletedCount, summary.TaskCount)
            : null;

        PlanSteps.Clear();
        foreach (var step in summary.UpcomingSteps(PlanPreviewSteps))
            PlanSteps.Add(step);

        OnPropertyChanged(nameof(HasPlanSteps));
        OnPropertyChanged(nameof(HasPlanSummaryText));
        NotifyWorkspaceVisibilityChanged();
    }

    partial void OnHasPlanChanged(bool value) => RefreshWorkspacePlan();

    // ── Git changes at a glance ──
    /// <summary>Summary of the repository's working-tree changes (branch, totals, kinds).</summary>
    [ObservableProperty] private GitChangesViewModel? _workspaceGit;

    public bool HasWorkspaceGit => IsCodingProject && WorkspaceGit is not null;
    public bool ShowWorkspaceGit => HasWorkspaceGit && WorkspaceGitFiles.IsShown;

    private void OnGitChangedFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A git refresh empties the list, waits for git and refills it. Summarize once it settles
        // (see OnIsRefreshingGitStatusChanged) so the card and its chip don't blink on every refresh.
        if (!IsRefreshingGitStatus)
            RefreshWorkspaceGit();
    }

    private void RefreshWorkspaceGit()
    {
        var files = GitChangedFiles.ToList();
        WorkspaceGitFiles.SetItems(files);
        WorkspaceGit = files.Count == 0
            ? null
            : new GitChangesViewModel(files.Select(static file => file.Change), ResolveGitRootPath(), GitBranch, IsWorktreeMode);
        NotifyWorkspaceVisibilityChanged();
    }

    private string ResolveGitRootPath()
        => GitService.FindRepoRoot(GetEffectiveWorkingDirectory()) ?? GetEffectiveWorkingDirectory();

    // ── Agents at a glance ──
    private void RefreshWorkspaceAgents()
    {
        // Running agents lead so live work is visible without scrolling; then newest first.
        var runs = SubagentRuns;
        var ordered = new List<SubagentToolCallItem>(runs.Count);
        for (var i = runs.Count - 1; i >= 0; i--)
        {
            if (runs[i].IsInProgress)
                ordered.Add(runs[i]);
        }

        for (var i = runs.Count - 1; i >= 0; i--)
        {
            if (!runs[i].IsInProgress)
                ordered.Add(runs[i]);
        }

        // Runs update many times a second while agents stream; only a first/last run can change what
        // the rest of the workspace shows. The category bar still follows the count and live dot.
        var hadAgents = WorkspaceAgents.TotalCount > 0;
        WorkspaceAgents.SetItems(ordered);
        if (hadAgents != (WorkspaceAgents.TotalCount > 0) || HasWorkspaceSearch)
            NotifyWorkspaceVisibilityChanged();
        else
            RefreshWorkspaceCategories();
    }

    // ── Search ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkspaceSearch))]
    private string _workspaceSearchText = "";

    public bool HasWorkspaceSearch => !string.IsNullOrWhiteSpace(WorkspaceSearchText);

    /// <summary>True when a search is active but nothing in the workspace matches it.</summary>
    [ObservableProperty] private bool _workspaceSearchHasNoMatches;

    partial void OnWorkspaceSearchTextChanged(string value) => ApplyWorkspaceSearch();

    [RelayCommand]
    private void ClearWorkspaceSearch() => WorkspaceSearchText = "";

    private void ApplyWorkspaceSearch()
    {
        foreach (var section in WorkspaceSections)
            section.SetQuery(WorkspaceSearchText);

        NotifyWorkspaceVisibilityChanged();
    }

    private void NotifyWorkspaceVisibilityChanged()
    {
        RefreshWorkspaceCategories();

        WorkspaceSearchHasNoMatches = HasWorkspaceSearch
            && !ShowWorkspacePlan
            && !ShowWorkspaceGit
            && !ChatContentSections.Any(static section => section.IsShown);

        OnPropertyChanged(nameof(HasWorkspaceContent));
        OnPropertyChanged(nameof(ShowWorkspaceEmptyState));
        OnPropertyChanged(nameof(ShowWorkspacePlan));
        OnPropertyChanged(nameof(HasWorkspaceGit));
        OnPropertyChanged(nameof(ShowWorkspaceGit));
    }

    // ── Panel state (mirrored from the view) ──

    /// <summary>Effective visibility of the panel, pushed from the view so the toggle reflects it.</summary>
    [ObservableProperty] private bool _isWorkspacePanelOpen;

    /// <summary>
    /// The page the panel last showed, pushed from the view for ambient layers (the presence glow).
    /// The header itself reads the view's own <see cref="WorkspaceHeader"/>, since two windows can
    /// show this chat on different pages.
    /// </summary>
    [ObservableProperty] private WorkspacePage _workspacePage = WorkspacePage.Overview;

    /// <summary>The header toggle stands in for the old agents button, so it names live agents too.</summary>
    public string WorkspaceToggleToolTip => HasRunningSubagents
        ? $"{Loc.Workspace_Title} · {SubagentRunsSummary}"
        : Loc.Workspace_Title;

    /// <summary>Raised when the header toggle is clicked; the view decides between open and close.</summary>
    public event Action? WorkspaceToggleRequested;

    [RelayCommand]
    private void ToggleWorkspacePanel() => WorkspaceToggleRequested?.Invoke();

    /// <summary>Persists whether the user keeps the workspace open (app-wide).</summary>
    internal void SaveWorkspacePanelPreference(bool open)
    {
        if (_dataStore.Data.Settings.WorkspacePanelOpen == open)
            return;

        _dataStore.Data.Settings.WorkspacePanelOpen = open;
        _dataStore.Save();
    }

    // ── Overview → pages ──
    [RelayCommand]
    private void OpenWorkspacePlan()
    {
        if (HasPlan)
            PlanShowRequested?.Invoke();
    }

    [RelayCommand]
    private void OpenWorkspaceAgents() => ShowSubagentIndex();

    [RelayCommand]
    private void OpenWorkspaceGitFile(GitFileChangeViewModel? file) => RequestGitChanges(file?.FullPath);

    private void InitializeWorkspace()
    {
        GitChangedFiles.CollectionChanged += OnGitChangedFilesChanged;
        foreach (var section in WorkspaceSections)
            section.ShowAllRequested = category => WorkspaceCategory = category;

        RefreshWorkspacePlan();
    }

    /// <summary>
    /// Rebuilds the workspace sections from the current chat's messages and transcript turns. Cheap and
    /// idempotent — safe to call after a transcript rebuild and whenever a turn completes.
    /// </summary>
    internal void RebuildWorkspacePanel()
    {
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deliverables = new List<FileAttachmentItem>();
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<SourceItem>();
        var seenLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<SourceItem>();
        var activeAssistantMessageIds = new HashSet<Guid>();
        var activities = new List<WorkspaceActivityItem>();
        var userMessages = new List<WorkspaceUserMessageItem>();

        BuildActivitySeedIndex();

        for (var i = 0; i < Messages.Count; i++)
        {
            var messageVm = Messages[i];
            var message = messageVm.Message;

            TryAppendUserMessage(userMessages, messageVm);

            if (string.Equals(message.ToolName, "announce_file", StringComparison.OrdinalIgnoreCase))
            {
                var filePath = ToolDisplayHelper.ExtractJsonField(message.Content, "filePath");
                if (!string.IsNullOrWhiteSpace(filePath)
                    && File.Exists(filePath)
                    && seenFiles.Add(filePath))
                {
                    deliverables.Add(new FileAttachmentItem(filePath, isPreviewable: true));
                }
            }

            TryAppendActivity(activities, i);

            if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                activeAssistantMessageIds.Add(message.Id);
                foreach (var link in GetAssistantLinks(messageVm))
                {
                    if (seenLinks.Add(link.Url))
                        links.Add(link);
                }
            }

            foreach (var source in message.Sources)
            {
                if (string.IsNullOrWhiteSpace(source.Url) || !seenSources.Add(source.Url))
                    continue;
                sources.Add(new SourceItem(source));
            }
        }

        PruneAssistantLinkCache(activeAssistantMessageIds);

        // The overview answers "what just happened", so every list reads newest first.
        deliverables.Reverse();
        sources.Reverse();
        links.Reverse();
        activities.Reverse();
        userMessages.Reverse();

        WorkspaceFiles.SetItems(deliverables);
        WorkspaceEdits.SetItems(CollectChangedFiles());
        WorkspaceSkills.SetItems(CollectSkills());
        WorkspaceLinks.SetItems(links);
        WorkspaceSources.SetItems(sources);
        WorkspaceMessages.SetItems(userMessages);
        WorkspaceActivity.SetItems(activities);
        HasErrorActivities = activities.Any(static activity => activity.Kind == WorkspaceActivityKind.Error);

        NotifyWorkspaceVisibilityChanged();
        WorkspaceContentChanged?.Invoke();
    }

    private static bool IsWorkspaceUserMessage(ChatMessageViewModel message)
        => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
           && !JobWakeItem.IsJobWakeMessage(message);

    private void TryAppendUserMessage(List<WorkspaceUserMessageItem> userMessages, ChatMessageViewModel message)
    {
        if (!IsWorkspaceUserMessage(message))
            return;

        userMessages.Add(new WorkspaceUserMessageItem(
            userMessages.Count + 1,
            message.Content,
            message.TimestampText,
            message.Message.Attachments.Count,
            message.Message.ActiveSkills.Count,
            ResolveMessageTurn(message.Message.Id),
            RaiseWorkspaceJump));
    }

    private string? ResolveMessageTurn(Guid messageId)
        => _activitySeedToTurnId.TryGetValue(messageId.ToString(), out var turnStableId)
            ? turnStableId
            : null;

    /// <summary>Latest state of every file Lumi created or edited this chat, most recently touched first.</summary>
    private List<FileChangeItem> CollectChangedFiles()
        => LatestFirst(
            TranscriptTurns
                .SelectMany(static turn => turn.Items.OfType<FileChangesSummaryItem>())
                .SelectMany(static summary => summary.FileChanges),
            static change => change.FilePath);

    /// <summary>Skills attached to a message or loaded mid-turn, newest first, one row per skill.</summary>
    private List<SkillChipItem> CollectSkills()
        => LatestFirst(
            TranscriptTurns.SelectMany(static turn => turn.Items).SelectMany(static item => item switch
            {
                UserMessageItem user => user.SkillChips,
                SkillLoadedItem loaded => [loaded.Chip],
                _ => Enumerable.Empty<SkillChipItem>(),
            }),
            static chip => chip.Name);

    /// <summary>The last occurrence of each key (paths and names compare case-insensitively), most recent first.</summary>
    private static List<T> LatestFirst<T>(IEnumerable<T> items, Func<T, string?> keyOf)
    {
        var latest = new Dictionary<string, (T Item, int Touched)>(StringComparer.OrdinalIgnoreCase);
        var touched = 0;
        foreach (var item in items)
        {
            if (keyOf(item) is { } key && !string.IsNullOrWhiteSpace(key))
                latest[key] = (item, touched++);
        }

        return latest.Values
            .OrderByDescending(static entry => entry.Touched)
            .Select(static entry => entry.Item)
            .ToList();
    }

    /// <summary>
    /// Inspects one message and, if it represents a meaningful step (intent, web search, subagent
    /// delegation, a question to the user, or an error), appends a timeline row that can jump to it.
    /// </summary>
    private void TryAppendActivity(List<WorkspaceActivityItem> activities, int index)
    {
        var message = Messages[index].Message;

        if (string.Equals(message.Role, "error", StringComparison.OrdinalIgnoreCase))
        {
            AddActivity(activities, WorkspaceActivityKind.Error, FirstMeaningfulLine(message.Content), index);
            return;
        }

        var tool = message.ToolName;
        if (string.IsNullOrWhiteSpace(tool))
            return;

        if (string.Equals(tool, "report_intent", StringComparison.OrdinalIgnoreCase))
        {
            AddActivity(activities, WorkspaceActivityKind.Intent,
                ToolDisplayHelper.ExtractJsonField(message.Content, "intent"), index);
        }
        else if (string.Equals(tool, "web_search", StringComparison.OrdinalIgnoreCase))
        {
            AddActivity(activities, WorkspaceActivityKind.Search,
                ToolDisplayHelper.ExtractJsonField(message.Content, "query"), index);
        }
        else if (string.Equals(tool, "ask_question", StringComparison.OrdinalIgnoreCase))
        {
            // Persisted ask-question messages keep the prompt in QuestionText with Content="";
            // only the legacy/fixture form embeds it as JSON in Content. Prefer the former.
            var question = !string.IsNullOrWhiteSpace(message.QuestionText)
                ? message.QuestionText
                : ToolDisplayHelper.ExtractJsonField(message.Content, "question");
            AddActivity(activities, WorkspaceActivityKind.Question, question, index);
        }
        else if (string.Equals(tool, "task", StringComparison.OrdinalIgnoreCase)
                 || tool.StartsWith("agent:", StringComparison.Ordinal))
        {
            var name = ToolDisplayHelper.GetSubagentDisplayName(tool, message.Content, message.Author);
            var task = ToolDisplayHelper.GetSubagentTaskDescription(tool, message.Content);
            AddActivity(activities, WorkspaceActivityKind.Subagent,
                !string.IsNullOrWhiteSpace(task) ? task : name, index);
        }
    }

    private void AddActivity(List<WorkspaceActivityItem> activities, WorkspaceActivityKind kind, string? title, int index)
    {
        if (string.IsNullOrWhiteSpace(title) || IsDuplicateActivity(activities, kind, title))
            return;

        activities.Add(new WorkspaceActivityItem(kind, title, ResolveActivityTurn(index), RaiseWorkspaceJump));
    }

    private static string FirstMeaningfulLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0)
                return line.Length > 140 ? line[..140] + "…" : line;
        }

        return "";
    }

    private static bool IsDuplicateActivity(List<WorkspaceActivityItem> activities, WorkspaceActivityKind kind, string title)
    {
        var last = activities.Count > 0 ? activities[^1] : null;
        return last is not null && last.Kind == kind
            && string.Equals(last.Title, title, StringComparison.Ordinal);
    }

    private void RaiseWorkspaceJump(string? turnStableId)
    {
        if (!string.IsNullOrEmpty(turnStableId))
            WorkspaceJumpToTurnRequested?.Invoke(turnStableId);
    }

    /// <summary>
    /// Finds the transcript turn an activity row should scroll to. For tool calls that render an item
    /// (e.g. web_search) this is the turn that hosts it; for label-only intents we fall forward to the
    /// next tool action's turn — the place that intent corresponds to. Returns null when unresolved.
    /// Resolution is an O(1) lookup against the seed index built by <see cref="BuildActivitySeedIndex"/>.
    /// </summary>
    private string? ResolveActivityTurn(int messageIndex)
    {
        for (var i = messageIndex; i < Messages.Count; i++)
        {
            var message = Messages[i].Message;
            if (i != messageIndex && !string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
                continue;

            // Questions render under a `question:{QuestionId}` stable id, so seed with QuestionId
            // first; regular tool calls fall back to ToolCallId, then the message id.
            var seed = !string.IsNullOrEmpty(message.QuestionId) ? message.QuestionId
                : !string.IsNullOrEmpty(message.ToolCallId) ? message.ToolCallId
                : message.Id.ToString();
            if (_activitySeedToTurnId.TryGetValue(seed, out var turnStableId))
                return turnStableId;
        }

        return null;
    }

    /// <summary>
    /// Builds the reverse map from activity <c>seed</c> to the owning turn's <see cref="TranscriptTurn.StableId"/>.
    /// Each turn is visited once and every id it renders (its own StableId plus its items' and grouped tool
    /// calls' StableIds) is decomposed into the seed an activity would search for. The first turn that owns a
    /// seed wins, mirroring the original first-match-in-order scan. This replaces a per-activity rescan of all
    /// turns/items that was quadratic and froze the UI for seconds on very large chats.
    /// </summary>
    private void BuildActivitySeedIndex()
    {
        _activitySeedToTurnId.Clear();

        foreach (var turn in TranscriptTurns)
        {
            var turnStableId = turn.StableId;
            RegisterSeeds(turnStableId, TurnSeedPrefixes, turnStableId);

            foreach (var item in turn.Items)
            {
                RegisterSeeds(item.StableId, ItemSeedPrefixes, turnStableId);

                switch (item)
                {
                    case ToolGroupItem group:
                        foreach (var toolCall in group.ToolCalls)
                            RegisterSeeds(toolCall.StableId, ItemSeedPrefixes, turnStableId);
                        break;
                    case SingleToolItem single:
                        RegisterSeeds(single.Inner.StableId, ItemSeedPrefixes, turnStableId);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// If <paramref name="stableId"/> starts with one of the activity <paramref name="prefixes"/>, records the
    /// remainder (the seed) as owned by <paramref name="turnStableId"/>, keeping the first owner seen.
    /// </summary>
    private void RegisterSeeds(string? stableId, string[] prefixes, string turnStableId)
    {
        if (string.IsNullOrEmpty(stableId))
            return;

        foreach (var prefix in prefixes)
        {
            if (stableId.Length > prefix.Length && stableId.StartsWith(prefix, StringComparison.Ordinal))
            {
                _activitySeedToTurnId.TryAdd(stableId.Substring(prefix.Length), turnStableId);
                return;
            }
        }
    }

    private IReadOnlyList<SourceItem> GetAssistantLinks(ChatMessageViewModel message)
    {
        var messageId = message.Message.Id;
        var content = message.Content;

        // Strings are immutable, and a message keeps the same instance until its content changes.
        if (_assistantLinkCache.TryGetValue(messageId, out var cached)
            && ReferenceEquals(cached.Content, content))
        {
            return cached.Links;
        }

        var links = ExtractAssistantLinks(content)
            .Select(link => new SourceItem(link.Title, link.Url))
            .ToArray();
        _assistantLinkCache[messageId] = new CachedAssistantLinks(content, links);
        return links;
    }

    private void PruneAssistantLinkCache(HashSet<Guid> activeMessageIds)
    {
        List<Guid>? removedMessageIds = null;
        foreach (var messageId in _assistantLinkCache.Keys)
        {
            if (!activeMessageIds.Contains(messageId))
                (removedMessageIds ??= []).Add(messageId);
        }

        if (removedMessageIds is null)
            return;

        foreach (var messageId in removedMessageIds)
            _assistantLinkCache.Remove(messageId);
    }

    private static IEnumerable<AssistantLink> ExtractAssistantLinks(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in MarkdownParser.Parse(markdown))
        {
            if (block.Kind is MdBlockKind.CodeBlock
                or MdBlockKind.Chart
                or MdBlockKind.Mermaid
                or MdBlockKind.Confidence
                or MdBlockKind.Comparison
                or MdBlockKind.Card
                or MdBlockKind.Sources)
            {
                continue;
            }

            foreach (var link in ExtractInlineLinks(block.Content))
            {
                if (seen.Add(link.Url))
                    yield return link;
            }
        }
    }

    private static List<AssistantLink> ExtractInlineLinks(string text)
    {
        var links = new List<AssistantLink>();
        var span = text.AsSpan();

        for (var position = 0; position < span.Length; position++)
        {
            if (span[position] == '`')
            {
                var relativeClose = span[(position + 1)..].IndexOf('`');
                if (relativeClose >= 0)
                {
                    position += relativeClose + 1;
                    continue;
                }
            }

            if (span[position] == '!' && position + 1 < span.Length && span[position + 1] == '[')
            {
                var imageBracketClose = FindClosingBracket(span, position + 2);
                if (imageBracketClose >= 0
                    && imageBracketClose + 1 < span.Length
                    && span[imageBracketClose + 1] == '(')
                {
                    var imageParenClose = FindClosingParen(span, imageBracketClose + 2);
                    if (imageParenClose >= 0)
                    {
                        position = imageParenClose;
                        continue;
                    }
                }
            }

            if (span[position] != '[')
                continue;

            var bracketClose = FindClosingBracket(span, position + 1);
            if (bracketClose < 0
                || bracketClose + 1 >= span.Length
                || span[bracketClose + 1] != '(')
            {
                continue;
            }

            var parenClose = FindClosingParen(span, bracketClose + 2);
            if (parenClose < 0)
                continue;

            var label = text[(position + 1)..bracketClose].Trim();
            var url = text[(bracketClose + 2)..parenClose].Trim();
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && !string.IsNullOrWhiteSpace(uri.Host))
            {
                links.Add(new AssistantLink(label, url));
            }

            position = parenClose;
        }

        return links;
    }

    private static int FindClosingBracket(ReadOnlySpan<char> span, int start)
    {
        for (var i = start; i < span.Length; i++)
        {
            if (span[i] == ']')
                return i;
            if (span[i] == '[')
                return -1;
        }

        return -1;
    }

    private static int FindClosingParen(ReadOnlySpan<char> span, int start)
    {
        var depth = 1;
        for (var i = start; i < span.Length; i++)
        {
            if (span[i] == '(')
            {
                depth++;
            }
            else if (span[i] == ')' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool MatchesSource(SourceItem source, string query)
        => Contains(source.Title, query) || Contains(source.Domain, query) || Contains(source.Url, query);

    private static bool Contains(string? haystack, string needle)
        => !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private readonly record struct AssistantLink(string Title, string Url);
    private sealed record CachedAssistantLinks(string Content, SourceItem[] Links);
}

/// <summary>A single user prompt indexed in the Workspace so long chats can jump between asks.</summary>
public partial class WorkspaceUserMessageItem : ObservableObject
{
    private readonly Action<string?>? _jumpAction;

    public int Number { get; }
    public string NumberLabel { get; }
    public string Preview { get; }
    public string MetaText { get; }
    public string? TargetTurnStableId { get; }
    public bool CanJump => !string.IsNullOrEmpty(TargetTurnStableId);

    public WorkspaceUserMessageItem(
        int number,
        string content,
        string timestampText,
        int attachmentCount,
        int skillCount,
        string? targetTurnStableId,
        Action<string?>? jumpAction)
    {
        Number = number;
        NumberLabel = $"#{number}";
        Preview = BuildPreview(content, attachmentCount);
        MetaText = BuildMetaText(timestampText, attachmentCount, skillCount);
        TargetTurnStableId = targetTurnStableId;
        _jumpAction = jumpAction;
    }

    private static string BuildPreview(string content, int attachmentCount)
    {
        var preview = CollapseWhitespace(content);
        if (!string.IsNullOrEmpty(preview))
            return preview.Length > 150 ? preview[..150] + "…" : preview;

        return attachmentCount > 0
            ? Pluralize(attachmentCount, "attached file", "attached files")
            : "Empty message";
    }

    private static string BuildMetaText(string timestampText, int attachmentCount, int skillCount)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(timestampText))
            parts.Add(timestampText);
        if (attachmentCount > 0)
            parts.Add(Pluralize(attachmentCount, "file", "files"));
        if (skillCount > 0)
            parts.Add(Pluralize(skillCount, "skill", "skills"));

        return parts.Count > 0 ? string.Join(" · ", parts) : "User message";
    }

    private static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var parts = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }

    private static string Pluralize(int count, string singular, string plural)
        => count == 1 ? $"1 {singular}" : $"{count} {plural}";

    [RelayCommand]
    private void Jump()
    {
        if (CanJump)
            _jumpAction?.Invoke(TargetTurnStableId);
    }
}

/// <summary>What kind of step an activity timeline row represents.</summary>
public enum WorkspaceActivityKind
{
    Intent,
    Search,
    Subagent,
    Question,
    Error,
}

/// <summary>
/// A single row in the Workspace activity timeline — a meaningful step Lumi took this chat
/// (stated an intent, ran a web search, delegated to a subagent, asked the user, or hit an error).
/// Every row can jump to the matching point in the transcript.
/// </summary>
public partial class WorkspaceActivityItem : ObservableObject
{
    private readonly Action<string?>? _jumpAction;

    public WorkspaceActivityKind Kind { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string? TargetTurnStableId { get; }

    /// <summary>Per-kind glyph. Icon and tile colors are applied in XAML via theme-token style
    /// classes (see WorkspaceOverview.axaml) so they track live theme switches; semantic color
    /// (accent / warning / danger) is reserved for the kinds that carry real meaning, so the
    /// timeline stays calm and professional rather than a rainbow of tiles.</summary>
    public Geometry KindGeometry => _kindGeometry ??= WorkspaceIcons.For(Kind);

    private Geometry? _kindGeometry;

    public bool IsIntent => Kind == WorkspaceActivityKind.Intent;
    public bool IsQuestion => Kind == WorkspaceActivityKind.Question;
    public bool IsError => Kind == WorkspaceActivityKind.Error;

    public bool CanJump => !string.IsNullOrEmpty(TargetTurnStableId);

    public WorkspaceActivityItem(WorkspaceActivityKind kind, string title, string? targetTurnStableId, Action<string?>? jumpAction)
    {
        Kind = kind;
        Title = title.Trim();
        Subtitle = KindLabel(kind);
        TargetTurnStableId = targetTurnStableId;
        _jumpAction = jumpAction;
    }

    private static string KindLabel(WorkspaceActivityKind kind) => kind switch
    {
        WorkspaceActivityKind.Intent => Loc.Workspace_ActivityIntent,
        WorkspaceActivityKind.Search => Loc.Workspace_ActivitySearch,
        WorkspaceActivityKind.Subagent => Loc.Workspace_ActivitySubagent,
        WorkspaceActivityKind.Question => Loc.Workspace_ActivityQuestion,
        WorkspaceActivityKind.Error => Loc.Workspace_ActivityError,
        _ => "",
    };

    [RelayCommand]
    private void Jump()
    {
        if (CanJump)
            _jumpAction?.Invoke(TargetTurnStableId);
    }
}
