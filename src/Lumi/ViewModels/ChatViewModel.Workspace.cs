using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Services;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

/// <summary>
/// Companion "Workspace" panel data: a first-class, tabbed, searchable index of the current chat's
/// key landmarks — user messages, deliverable files, assistant links, web sources, an activity
/// timeline (intents + searches) that can jump to the transcript, and the files Lumi changed.
/// Aggregated from the chat's messages so the transcript stays a clean conversation while these stay
/// glanceable.
/// </summary>
public partial class ChatViewModel
{
    // ── Tab identifiers ──
    public const int WorkspaceTabDeliverables = 0;
    public const int WorkspaceTabSources = 1;
    public const int WorkspaceTabActivity = 2;
    public const int WorkspaceTabChanges = 3;
    public const int WorkspaceTabMessages = 4;
    public const int WorkspaceTabLinks = 5;

    // ── Backing (unfiltered) sets ──
    private readonly List<FileAttachmentItem> _allDeliverables = [];
    private readonly List<SourceItem> _allSources = [];
    private readonly List<SourceItem> _allLinks = [];
    private readonly List<WorkspaceActivityItem> _allActivities = [];
    private readonly List<FileChangeItem> _allChanges = [];
    private readonly List<WorkspaceUserMessageItem> _allUserMessages = [];
    private readonly Dictionary<Guid, CachedAssistantLinks> _assistantLinkCache = [];

    // Reverse index (activity seed → owning turn StableId), rebuilt with the panel. The activity
    // timeline uses it to resolve each row's transcript jump target in O(1). Without it, every
    // activity rescans all turns and their items (allocating interpolated id strings per compare),
    // which is quadratic and froze the UI for many seconds when opening very large chats.
    private readonly Dictionary<string, string> _activitySeedToTurnId = new(StringComparer.Ordinal);

    private static readonly string[] TurnSeedPrefixes = { "turn:tool:", "turn:question:", "turn:message:" };
    private static readonly string[] ItemSeedPrefixes = { "tool:", "subagent:", "question:", "message:error:" };

    // ── Displayed (search-filtered) collections the view binds to ──
    public ObservableCollection<FileAttachmentItem> WorkspaceDeliverables { get; } = [];
    public ObservableCollection<SourceItem> WorkspaceSources { get; } = [];
    public ObservableCollection<SourceItem> WorkspaceLinks { get; } = [];
    public ObservableCollection<WorkspaceActivityItem> WorkspaceActivities { get; } = [];
    public ObservableCollection<FileChangeItem> WorkspaceChanges { get; } = [];
    public ObservableCollection<WorkspaceUserMessageItem> WorkspaceUserMessages { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkspaceContent))]
    private bool _hasWorkspaceDeliverables;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkspaceContent))]
    private bool _hasWorkspaceSources;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkspaceContent))]
    private bool _hasWorkspaceLinks;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkspaceContent))]
    private bool _hasWorkspaceActivities;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkspaceContent))]
    private bool _hasWorkspaceChanges;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkspaceContent))]
    private bool _hasWorkspaceUserMessages;

    [ObservableProperty] private string _workspaceDeliverablesCountLabel = "0";
    [ObservableProperty] private string _workspaceSourcesCountLabel = "0";
    [ObservableProperty] private string _workspaceLinksCountLabel = "0";
    [ObservableProperty] private string _workspaceActivitiesCountLabel = "0";
    [ObservableProperty] private string _workspaceChangesCountLabel = "0";
    [ObservableProperty] private string _workspaceUserMessagesCountLabel = "0";

    /// <summary>True when the panel has anything worth showing.</summary>
    public bool HasWorkspaceContent =>
        HasWorkspaceDeliverables || HasWorkspaceSources || HasWorkspaceLinks || HasWorkspaceActivities || HasWorkspaceChanges || HasWorkspaceUserMessages;

    // ── Active tab ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeliverablesTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsSourcesTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsLinksTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsActivityTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsChangesTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsMessagesTabSelected))]
    private int _workspaceSelectedTab = WorkspaceTabDeliverables;

    public bool IsDeliverablesTabSelected => WorkspaceSelectedTab == WorkspaceTabDeliverables;
    public bool IsSourcesTabSelected => WorkspaceSelectedTab == WorkspaceTabSources;
    public bool IsLinksTabSelected => WorkspaceSelectedTab == WorkspaceTabLinks;
    public bool IsActivityTabSelected => WorkspaceSelectedTab == WorkspaceTabActivity;
    public bool IsChangesTabSelected => WorkspaceSelectedTab == WorkspaceTabChanges;
    public bool IsMessagesTabSelected => WorkspaceSelectedTab == WorkspaceTabMessages;

    partial void OnWorkspaceSelectedTabChanged(int value) => UpdateWorkspaceSearchState();

    [RelayCommand]
    private void SelectWorkspaceTab(int index) => WorkspaceSelectedTab = index;

    // ── Search ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkspaceSearch))]
    private string _workspaceSearchText = "";

    public bool HasWorkspaceSearch => !string.IsNullOrWhiteSpace(WorkspaceSearchText);

    /// <summary>True when a search is active but the selected tab has no matching rows.</summary>
    [ObservableProperty] private bool _workspaceSearchHasNoMatches;

    partial void OnWorkspaceSearchTextChanged(string value) => ApplyWorkspaceFilter();

    [RelayCommand]
    private void ClearWorkspaceSearch() => WorkspaceSearchText = "";

    // ── Activity kind filter (chips inside the Activity tab) ──
    public const int ActivityFilterAll = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActivityFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsActivityFilterIntent))]
    [NotifyPropertyChangedFor(nameof(IsActivityFilterSearch))]
    [NotifyPropertyChangedFor(nameof(IsActivityFilterSubagent))]
    [NotifyPropertyChangedFor(nameof(IsActivityFilterQuestion))]
    [NotifyPropertyChangedFor(nameof(IsActivityFilterError))]
    private int _workspaceActivityFilter = ActivityFilterAll;

    public bool IsActivityFilterAll => WorkspaceActivityFilter == ActivityFilterAll;
    public bool IsActivityFilterIntent => WorkspaceActivityFilter == (int)WorkspaceActivityKind.Intent;
    public bool IsActivityFilterSearch => WorkspaceActivityFilter == (int)WorkspaceActivityKind.Search;
    public bool IsActivityFilterSubagent => WorkspaceActivityFilter == (int)WorkspaceActivityKind.Subagent;
    public bool IsActivityFilterQuestion => WorkspaceActivityFilter == (int)WorkspaceActivityKind.Question;
    public bool IsActivityFilterError => WorkspaceActivityFilter == (int)WorkspaceActivityKind.Error;

    // Which kinds exist in this chat (drives which filter chips are offered).
    [ObservableProperty] private bool _hasIntentActivities;
    [ObservableProperty] private bool _hasSearchActivities;
    [ObservableProperty] private bool _hasSubagentActivities;
    [ObservableProperty] private bool _hasQuestionActivities;
    [ObservableProperty] private bool _hasErrorActivities;

    /// <summary>Only show the filter strip when there is more than one kind to slice between.</summary>
    [ObservableProperty] private bool _showActivityFilters;

    partial void OnWorkspaceActivityFilterChanged(int value) => ApplyWorkspaceFilter();

    [RelayCommand]
    private void SelectActivityFilter(int kind) => WorkspaceActivityFilter = kind;

    // ── Open/closed toggle (first-class; persisted app-wide) ──

    /// <summary>The toggle is a core chat affordance, always offered when a chat is open.</summary>
    public bool ShowWorkspaceToggle => true;

    /// <summary>Effective visibility, pushed from the view so the toggle button can reflect state.</summary>
    [ObservableProperty] private bool _isWorkspacePanelOpen;

    [RelayCommand]
    private void ToggleWorkspacePanel()
    {
        var newOpen = !IsWorkspacePanelOpen;
        _dataStore.Data.Settings.WorkspacePanelOpen = newOpen;
        _dataStore.Save();
        WorkspacePanelPreferenceChanged?.Invoke();
    }

    /// <summary>Raised after the workspace collections change so the view can re-evaluate visibility/width gating.</summary>
    public event Action? WorkspaceContentChanged;

    /// <summary>
    /// Rebuilds the workspace panel from the current chat's messages and transcript turns. Cheap and
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
                    deliverables.Add(new FileAttachmentItem(filePath));
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

        var changes = CollectChangedFiles();
        PruneAssistantLinkCache(activeAssistantMessageIds);

        ReplaceAll(_allDeliverables, deliverables);
        ReplaceAll(_allSources, sources);
        ReplaceAll(_allLinks, links);
        ReplaceAll(_allActivities, activities);
        ReplaceAll(_allChanges, changes);
        ReplaceAll(_allUserMessages, userMessages);

        HasWorkspaceDeliverables = _allDeliverables.Count > 0;
        HasWorkspaceSources = _allSources.Count > 0;
        HasWorkspaceLinks = _allLinks.Count > 0;
        HasWorkspaceActivities = _allActivities.Count > 0;
        HasWorkspaceChanges = _allChanges.Count > 0;
        HasWorkspaceUserMessages = _allUserMessages.Count > 0;

        WorkspaceDeliverablesCountLabel = _allDeliverables.Count.ToString();
        WorkspaceSourcesCountLabel = _allSources.Count.ToString();
        WorkspaceLinksCountLabel = _allLinks.Count.ToString();
        WorkspaceActivitiesCountLabel = _allActivities.Count.ToString();
        WorkspaceChangesCountLabel = _allChanges.Count.ToString();
        WorkspaceUserMessagesCountLabel = _allUserMessages.Count.ToString();

        RecomputeActivityKindAvailability();
        EnsureValidSelectedTab();
        ApplyWorkspaceFilter();

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

    /// <summary>Tracks which activity kinds exist so the view shows only the relevant filter chips.</summary>
    private void RecomputeActivityKindAvailability()
    {
        HasIntentActivities = _allActivities.Any(a => a.Kind == WorkspaceActivityKind.Intent);
        HasSearchActivities = _allActivities.Any(a => a.Kind == WorkspaceActivityKind.Search);
        HasSubagentActivities = _allActivities.Any(a => a.Kind == WorkspaceActivityKind.Subagent);
        HasQuestionActivities = _allActivities.Any(a => a.Kind == WorkspaceActivityKind.Question);
        HasErrorActivities = _allActivities.Any(a => a.Kind == WorkspaceActivityKind.Error);

        var distinctKinds = _allActivities.Select(a => a.Kind).Distinct().Count();
        ShowActivityFilters = distinctKinds > 1;

        // Drop a filter that no longer has any rows.
        if (WorkspaceActivityFilter != ActivityFilterAll
            && !_allActivities.Any(a => (int)a.Kind == WorkspaceActivityFilter))
        {
            WorkspaceActivityFilter = ActivityFilterAll;
        }
    }

    /// <summary>Latest state of every file Lumi created or edited this chat, de-duplicated by path.</summary>
    private List<FileChangeItem> CollectChangedFiles()
    {
        var byPath = new Dictionary<string, FileChangeItem>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var turn in TranscriptTurns)
        {
            foreach (var summary in turn.Items.OfType<FileChangesSummaryItem>())
            {
                foreach (var change in summary.FileChanges)
                {
                    if (!byPath.ContainsKey(change.FilePath))
                        order.Add(change.FilePath);
                    byPath[change.FilePath] = change; // keep the most recent occurrence
                }
            }
        }

        return order.Select(path => byPath[path]).ToList();
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

    private void EnsureValidSelectedTab()
    {
        if (TabHasContent(WorkspaceSelectedTab))
            return;

        foreach (var tab in new[] { WorkspaceTabDeliverables, WorkspaceTabSources, WorkspaceTabLinks, WorkspaceTabMessages, WorkspaceTabActivity, WorkspaceTabChanges })
        {
            if (TabHasContent(tab))
            {
                WorkspaceSelectedTab = tab;
                return;
            }
        }

        WorkspaceSelectedTab = WorkspaceTabDeliverables;
    }

    private bool TabHasContent(int tab) => tab switch
    {
        WorkspaceTabDeliverables => HasWorkspaceDeliverables,
        WorkspaceTabSources => HasWorkspaceSources,
        WorkspaceTabLinks => HasWorkspaceLinks,
        WorkspaceTabMessages => HasWorkspaceUserMessages,
        WorkspaceTabActivity => HasWorkspaceActivities,
        WorkspaceTabChanges => HasWorkspaceChanges,
        _ => false,
    };

    private void ApplyWorkspaceFilter()
    {
        var q = WorkspaceSearchText?.Trim() ?? "";
        var hasQuery = q.Length > 0;

        ReplaceAll(WorkspaceDeliverables, hasQuery
            ? _allDeliverables.Where(d => Contains(d.FileName, q) || Contains(d.FilePath, q)).ToList()
            : _allDeliverables);
        ReplaceAll(WorkspaceSources, hasQuery
            ? _allSources.Where(s => Contains(s.Title, q) || Contains(s.Domain, q) || Contains(s.Url, q)).ToList()
            : _allSources);
        ReplaceAll(WorkspaceLinks, hasQuery
            ? _allLinks.Where(l => Contains(l.Title, q) || Contains(l.Domain, q) || Contains(l.Url, q)).ToList()
            : _allLinks);
        ReplaceAll(WorkspaceUserMessages, hasQuery
            ? _allUserMessages.Where(m => Contains(m.Preview, q) || Contains(m.MetaText, q) || Contains(m.NumberLabel, q)).ToList()
            : _allUserMessages);
        var activitySource = WorkspaceActivityFilter == ActivityFilterAll
            ? (IEnumerable<WorkspaceActivityItem>)_allActivities
            : _allActivities.Where(a => (int)a.Kind == WorkspaceActivityFilter);
        ReplaceAll(WorkspaceActivities, hasQuery
            ? activitySource.Where(a => Contains(a.Title, q) || Contains(a.Subtitle, q)).ToList()
            : activitySource.ToList());
        ReplaceAll(WorkspaceChanges, hasQuery
            ? _allChanges.Where(c => Contains(c.FileName, q) || Contains(c.FilePath, q)).ToList()
            : _allChanges);

        UpdateWorkspaceSearchState();
    }

    private void UpdateWorkspaceSearchState()
    {
        if (!HasWorkspaceSearch)
        {
            WorkspaceSearchHasNoMatches = false;
            return;
        }

        WorkspaceSearchHasNoMatches = WorkspaceSelectedTab switch
        {
            WorkspaceTabDeliverables => WorkspaceDeliverables.Count == 0,
            WorkspaceTabSources => WorkspaceSources.Count == 0,
            WorkspaceTabLinks => WorkspaceLinks.Count == 0,
            WorkspaceTabMessages => WorkspaceUserMessages.Count == 0,
            WorkspaceTabActivity => WorkspaceActivities.Count == 0,
            WorkspaceTabChanges => WorkspaceChanges.Count == 0,
            _ => false,
        };
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

    private static bool Contains(string? haystack, string needle)
        => !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static void ReplaceAll<T>(ICollection<T> target, IEnumerable<T> desired)
    {
        target.Clear();
        foreach (var item in desired)
            target.Add(item);
    }

    private readonly record struct AssistantLink(string Title, string Url);
    private sealed record CachedAssistantLinks(string Content, SourceItem[] Links);
}

/// <summary>A single user prompt indexed in the Workspace panel so long chats can jump between asks.</summary>
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
/// A single row in the Workspace "Activity" timeline — a meaningful step Lumi took this chat
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
    /// classes (see ChatWorkspaceView.axaml) so they track live theme switches; semantic color
    /// (accent / warning / danger) is reserved for the kinds that carry real meaning, so the
    /// timeline stays calm and professional rather than a rainbow of tiles.</summary>
    public Geometry KindGeometry { get; }

    public bool IsSearch => Kind == WorkspaceActivityKind.Search;
    public bool IsIntent => Kind == WorkspaceActivityKind.Intent;
    public bool IsSubagent => Kind == WorkspaceActivityKind.Subagent;
    public bool IsQuestion => Kind == WorkspaceActivityKind.Question;
    public bool IsError => Kind == WorkspaceActivityKind.Error;

    /// <summary>True for the accent-tinted kinds (intent), so the view can color the dot.</summary>
    public bool IsAccent => Kind == WorkspaceActivityKind.Intent;

    public bool CanJump => !string.IsNullOrEmpty(TargetTurnStableId);

    public WorkspaceActivityItem(WorkspaceActivityKind kind, string title, string? targetTurnStableId, Action<string?>? jumpAction)
    {
        Kind = kind;
        Title = title.Trim();
        Subtitle = KindLabel(kind);
        TargetTurnStableId = targetTurnStableId;
        _jumpAction = jumpAction;

        KindGeometry = SafeParse(KindGlyph(kind));
    }

    /// <summary>Parses glyph path data, falling back to a simple tile so a bad path can never crash the UI.</summary>
    private static Geometry SafeParse(string data)
    {
        try { return Geometry.Parse(data); }
        catch { return Geometry.Parse("M7 7h10v10H7z"); }
    }

    private static string KindLabel(WorkspaceActivityKind kind) => kind switch
    {
        WorkspaceActivityKind.Intent => "Intent",
        WorkspaceActivityKind.Search => "Web search",
        WorkspaceActivityKind.Subagent => "Subagent",
        WorkspaceActivityKind.Question => "Question",
        WorkspaceActivityKind.Error => "Error",
        _ => "",
    };

    // Material-grid glyphs (filled, 24-grid). Per-kind color is applied in XAML via theme-token styles.
    private const string IntentGlyph = "M14.4 6L14 4H5v17h2v-7h5.6l.4 2h7V6z";
    private const string SearchGlyph = "M15.5 14h-.79l-.28-.27a6.5 6.5 0 1 0-.7.7l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0A4.5 4.5 0 1 1 14 9.5 4.5 4.5 0 0 1 9.5 14z";
    private const string SubagentGlyph = "M22 11V3h-7v3H9V3H2v8h7V8h2v10h4v3h7v-8h-7v3h-2V8h2v3z";
    private const string QuestionGlyph = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 17h-2v-2h2v2zm2.07-7.75l-.9.92C13.45 12.9 13 13.5 13 15h-2v-.5c0-1.1.45-2.1 1.17-2.83l1.24-1.26c.37-.36.59-.86.59-1.41 0-1.1-.9-2-2-2s-2 .9-2 2H8c0-2.21 1.79-4 4-4s4 1.79 4 4c0 .88-.36 1.68-.93 2.25z";
    private const string ErrorGlyph = "M1 21h22L12 2 1 21zm12-3h-2v-2h2v2zm0-4h-2v-4h2v4z";

    private static string KindGlyph(WorkspaceActivityKind kind) => kind switch
    {
        WorkspaceActivityKind.Intent => IntentGlyph,
        WorkspaceActivityKind.Search => SearchGlyph,
        WorkspaceActivityKind.Subagent => SubagentGlyph,
        WorkspaceActivityKind.Question => QuestionGlyph,
        WorkspaceActivityKind.Error => ErrorGlyph,
        _ => SearchGlyph,
    };

    [RelayCommand]
    private void Jump()
    {
        if (CanJump)
            _jumpAction?.Invoke(TargetTurnStableId);
    }
}
