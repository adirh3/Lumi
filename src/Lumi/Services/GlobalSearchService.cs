using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using StrataSearch;

namespace Lumi.Services;

public enum GlobalSearchCategory
{
    Chats,
    BackgroundJobs,
    Projects,
    Skills,
    Lumis,
    Memories,
    McpServers,
    Settings
}

public enum GlobalSearchExecutionMode
{
    Preview,
    Fast,
    Interactive,
    Full
}

public sealed class GlobalSearchMatch
{
    public GlobalSearchCategory Category { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public int NavIndex { get; init; }
    public object? Item { get; init; }
    public int SettingsPageIndex { get; init; } = -1;
    public double Score { get; init; }
    public bool IsContentMatch { get; init; }
    public DateTimeOffset? SortTimestamp { get; init; }
}

public sealed class ChatSearchMessage
{
    public string Text { get; init; } = "";
    public string Role { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
}

public sealed class ChatSearchSnapshot
{
    public string Version { get; init; } = "";
    public IReadOnlyList<ChatSearchMessage> Messages { get; init; } = Array.Empty<ChatSearchMessage>();
}

public sealed class GlobalSearchService
{
    private readonly Func<AppData> _getData;
    private readonly Func<DateTimeOffset> _nowProvider;
    private readonly ChatContentIndex _contentIndex;
    private const int InteractiveColdChatContentLimit = 16;
    private const double UserMessageSearchWeight = 3.0;
    private const double AssistantMessageSearchWeight = 1.0;

    // Chat titles are often long auto-generated sentences, so the fuzzy text engine can match a
    // query against an unrelated word (e.g. "exposes"/"expert" for "expenses") and the high primary
    // field weight then floats that noise above genuine hits. When a chat matches only fuzzily —
    // the query is neither a real substring/token of the title nor present in the content — its
    // relevance is damped so exact title and real content matches always rank first.
    private const double FuzzyChatRelevanceFactor = 0.2;

    // Browse base scores keep categories grouped (chats first) when listing by time/recency.
    private const double BrowseBaseChats = 1_000;
    private const double BrowseBaseJobs = 920;
    private const double BrowseBaseProjects = 900;
    private const double BrowseBaseSkills = 800;
    private const double BrowseBaseAgents = 780;
    private const double BrowseBaseMemories = 760;
    private const double BrowseBaseMcp = 740;

    private static readonly SearchSettingEntry[] SettingsIndex =
    [
        new("Your Name", "Profile", 0),
        new("Language", "Profile", 0),
        new("Launch at Startup", "General", 1),
        new("Start Minimized", "General", 1),
        new("Close to Tray", "General", 1),
        new("Enable Notifications", "General", 1),
        new("Global Hotkey", "General", 1),
        new("Dark Mode", "Appearance", 2),
        new("Compact Density", "Appearance", 2),
        new("Font Size", "Appearance", 2),
        new("Show Animations", "Appearance", 2),
        new("Send with Enter", "Chat", 3),
        new("Show Timestamps", "Chat", 3),
        new("Show Tool Calls", "Chat", 3),
        new("Show Reasoning", "Chat", 3),
        new("Expand Reasoning While Streaming", "Chat", 3),
        new("Auto Generate Titles", "Chat", 3),
        new("GitHub Account", "AI & Models", 4),
        new("Default Model & Reasoning", "AI & Models", 4),
        new("Auto Save Memories", "Privacy & Data", 5),
        new("Auto Save Chats", "Privacy & Data", 5),
        new("Import Browser Cookies", "Privacy & Data", 5),
        new("Clear All Chats", "Privacy & Data", 5),
        new("Clear All Memories", "Privacy & Data", 5),
        new("Reset All Settings", "Privacy & Data", 5),
        new("Version", "About", 6)
    ];

    public GlobalSearchService(
        Func<AppData> getData,
        Func<Chat, ChatSearchSnapshot> chatSnapshotProvider,
        Func<DateTimeOffset>? nowProvider = null,
        Action<Guid>? releaseChatSnapshot = null,
        Func<Guid, DateTimeOffset?>? chatFileTimestampProvider = null)
    {
        _getData = getData ?? throw new ArgumentNullException(nameof(getData));
        ArgumentNullException.ThrowIfNull(chatSnapshotProvider);
        _nowProvider = nowProvider ?? (() => DateTimeOffset.Now);
        _contentIndex = new ChatContentIndex(chatSnapshotProvider, releaseChatSnapshot, chatFileTimestampProvider);
    }

    /// <summary>Number of chats whose content is currently indexed (full-coverage progress).</summary>
    public int IndexedChatCount => _contentIndex.Count;

    /// <summary>
    /// Captures one consistent data snapshot and parses the query once so progressive UI phases
    /// can reuse that work instead of repeatedly copying the full application data.
    /// Must be called from the UI thread because <see cref="AppData"/> collections are UI-owned.
    /// </summary>
    public PreparedSearch PrepareSearch(string query)
    {
        var snapshot = CaptureSnapshot(_getData());
        var trimmedQuery = query?.Trim() ?? "";

        if (string.IsNullOrEmpty(trimmedQuery))
            return new PreparedSearch(
                snapshot,
                SearchQuery.Create(""),
                range: null,
                hasRecencyIntent: false,
                useDefaultResults: true);

        var now = _nowProvider();
        var timeQuery = SearchTimeQuery.Parse(trimmedQuery, now);
        var textQuery = SearchQuery.Create(timeQuery.ResidualText);

        return new PreparedSearch(
            snapshot,
            textQuery,
            timeQuery.Range,
            timeQuery.HasRecencyIntent,
            useDefaultResults: textQuery.IsEmpty && !timeQuery.HasAnyTimeSignal);
    }

    public Task<IReadOnlyList<GlobalSearchMatch>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
        => SearchAsync(query, GlobalSearchExecutionMode.Full, cancellationToken);

    public Task<IReadOnlyList<GlobalSearchMatch>> SearchAsync(
        string query,
        GlobalSearchExecutionMode executionMode,
        CancellationToken cancellationToken = default)
        => SearchAsync(PrepareSearch(query), executionMode, cancellationToken);

    public Task<IReadOnlyList<GlobalSearchMatch>> SearchAsync(
        PreparedSearch preparedSearch,
        GlobalSearchExecutionMode executionMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparedSearch);

        if (preparedSearch.UseDefaultResults)
        {
            return Task.Run<IReadOnlyList<GlobalSearchMatch>>(
                () => BuildDefaultResults(preparedSearch.Snapshot),
                cancellationToken);
        }

        var context = new SearchContext(
            preparedSearch.TextQuery,
            preparedSearch.Range,
            preparedSearch.HasRecencyIntent,
            executionMode);

        return Task.Run<IReadOnlyList<GlobalSearchMatch>>(
            () => SearchCore(preparedSearch.Snapshot, context, cancellationToken),
            cancellationToken);
    }

    private IReadOnlyList<GlobalSearchMatch> SearchCore(
        SearchSnapshot snapshot,
        SearchContext context,
        CancellationToken cancellationToken)
    {
        var results = new List<GlobalSearchMatch>();

        if (context.IsBrowse)
        {
            BuildBrowseResults(snapshot, context, results, cancellationToken);
        }
        else
        {
            SearchChats(snapshot, context, results, cancellationToken);
            SearchJobs(snapshot, context, results, cancellationToken);
            SearchProjects(snapshot, context, results, cancellationToken);
            SearchSkills(snapshot, context, results, cancellationToken);
            SearchAgents(snapshot, context, results, cancellationToken);
            SearchMemories(snapshot, context, results, cancellationToken);
            SearchMcpServers(snapshot, context, results, cancellationToken);

            // Settings have no timestamp, so they only make sense for plain text queries.
            if (!context.HasRange)
                SearchSettings(context.TextQuery, results, cancellationToken);
        }

        results.Sort(static (left, right) =>
        {
            var scoreComparison = right.Score.CompareTo(left.Score);
            if (scoreComparison != 0)
                return scoreComparison;

            var rightTimestamp = right.SortTimestamp ?? DateTimeOffset.MinValue;
            var leftTimestamp = left.SortTimestamp ?? DateTimeOffset.MinValue;
            var timestampComparison = rightTimestamp.CompareTo(leftTimestamp);
            if (timestampComparison != 0)
                return timestampComparison;

            return StringComparer.CurrentCultureIgnoreCase.Compare(left.Title, right.Title);
        });

        return LimitResultsPerCategory(results, context.Mode == GlobalSearchExecutionMode.Preview ? 12 : 32);
    }

    private void SearchChats(
        SearchSnapshot snapshot,
        SearchContext context,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        var interactiveColdChatIds = context.Mode == GlobalSearchExecutionMode.Interactive
            ? GetInteractiveColdChatIds(snapshot.Chats, context, cancellationToken)
            : null;

        foreach (var chat in snapshot.Chats)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!context.InRange(chat.UpdatedAt))
                continue;

            var titleField = new PreparedSearchField(chat.Title, 3.8, SearchFieldKind.Primary);
            var evaluation = SearchEngine.Evaluate(context.TextQuery, [titleField]);

            if (!evaluation.IsMatch && context.Mode != GlobalSearchExecutionMode.Preview)
            {
                var contentFields = GetChatContentFields(
                    chat,
                    context.Mode,
                    interactiveColdChatIds,
                    context.TextQuery,
                    cancellationToken);

                if (contentFields is not null)
                {
                    var fields = new List<PreparedSearchField>(1 + contentFields.Count) { titleField };
                    fields.AddRange(contentFields);
                    evaluation = SearchEngine.Evaluate(context.TextQuery, fields);
                }
            }

            if (!evaluation.IsMatch)
                continue;

            snapshot.ProjectNames.TryGetValue(chat.ProjectId ?? Guid.Empty, out var projectName);

            // A match is "genuine" when the query actually occurs in the content (content fields are
            // never fuzzy-matched) or every term is a real substring/token of the title. Pure fuzzy
            // title matches are damped so they cannot bury exact title or real content matches.
            var isGenuineMatch = evaluation.IsContentMatch
                                 || QueryTermsPresentInTitle(chat.Title, context.TextQuery);
            var relevance = isGenuineMatch
                ? evaluation.Score
                : evaluation.Score * FuzzyChatRelevanceFactor;

            var score = relevance
                        + GetTitleBonus(chat.Title, context.TextQuery)
                        + GetChatRecencyBoost(chat.UpdatedAt, context.HasRecencyIntent);

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Chats,
                Title = chat.Title,
                Subtitle = BuildChatSubtitle(projectName, chat.UpdatedAt, evaluation.BestContentSnippet),
                NavIndex = 0,
                Item = chat,
                Score = score,
                IsContentMatch = evaluation.IsContentMatch,
                SortTimestamp = chat.UpdatedAt
            });
        }
    }

    private void SearchProjects(
        SearchSnapshot snapshot,
        SearchContext context,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var project in snapshot.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            snapshot.ProjectChatCounts.TryGetValue(project.Id, out var chatCount);
            snapshot.ProjectLastActivity.TryGetValue(project.Id, out var latestActivity);
            var sortTimestamp = latestActivity == default ? project.CreatedAt : latestActivity;

            if (!context.InRange(sortTimestamp))
                continue;

            var evaluation = SearchEngine.Evaluate(
                context.TextQuery,
                IncludeContentFields(context.Mode)
                    ?
                    [
                        SearchField.Primary(project.Name, 3.5),
                        SearchField.Content(project.Instructions, 1.0)
                    ]
                    :
                    [
                        SearchField.Primary(project.Name, 3.5)
                    ]);

            if (!evaluation.IsMatch)
                continue;

            var defaultSubtitle = chatCount == 0
                ? "No chats"
                : $"{chatCount} chat{(chatCount == 1 ? "" : "s")} · {FormatRelativeTime(sortTimestamp)}";

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Projects,
                Title = project.Name,
                Subtitle = BuildSecondarySubtitle(defaultSubtitle, evaluation.BestContentSnippet),
                NavIndex = 2,
                Item = project,
                Score = evaluation.Score
                        + GetTitleBonus(project.Name, context.TextQuery)
                        + GetRecencyBoost(sortTimestamp, multiplier: 0.8),
                IsContentMatch = evaluation.IsContentMatch,
                SortTimestamp = sortTimestamp
            });
        }
    }

    private void SearchJobs(
        SearchSnapshot snapshot,
        SearchContext context,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var job in snapshot.BackgroundJobs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!context.InRange(job.UpdatedAt))
                continue;

            var evaluation = SearchEngine.Evaluate(
                context.TextQuery,
                IncludeContentFields(context.Mode)
                    ?
                    [
                        SearchField.Primary(job.Name, 3.5),
                        new SearchField(job.Description, 1.8),
                        SearchField.Content(job.Prompt, 1.1),
                        new SearchField(job.LastRunSummary, 0.9)
                    ]
                    :
                    [
                        SearchField.Primary(job.Name, 3.5),
                        new SearchField(job.Description, 1.8)
                    ]);

            if (!evaluation.IsMatch)
                continue;

            var status = job.IsEnabled ? "Enabled" : "Paused";
            var next = job.NextRunAt.HasValue ? $" · next {FormatRelativeTime(job.NextRunAt.Value)}" : "";
            var subtitle = $"{status}{next} · {BackgroundJobSchedule.Describe(job)}";

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.BackgroundJobs,
                Title = job.Name,
                Subtitle = BuildSecondarySubtitle(subtitle, evaluation.BestContentSnippet),
                NavIndex = 1,
                Item = job,
                Score = evaluation.Score
                        + GetTitleBonus(job.Name, context.TextQuery)
                        + GetRecencyBoost(job.UpdatedAt, multiplier: 0.6),
                IsContentMatch = evaluation.IsContentMatch,
                SortTimestamp = job.UpdatedAt
            });
        }
    }

    private void SearchSkills(
        SearchSnapshot snapshot,
        SearchContext context,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var skill in snapshot.Skills)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!context.InRange(skill.CreatedAt))
                continue;

            var evaluation = SearchEngine.Evaluate(
                context.TextQuery,
                IncludeContentFields(context.Mode)
                    ?
                    [
                        SearchField.Primary(skill.Name, 3.4),
                        new SearchField(skill.Description, 1.8),
                        SearchField.Content(skill.Content, 0.95)
                    ]
                    :
                    [
                        SearchField.Primary(skill.Name, 3.4),
                        new SearchField(skill.Description, 1.8)
                    ]);

            if (!evaluation.IsMatch)
                continue;

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Skills,
                Title = skill.Name,
                Subtitle = BuildSecondarySubtitle(skill.Description, evaluation.BestContentSnippet),
                NavIndex = 3,
                Item = skill,
                Score = evaluation.Score
                        + GetTitleBonus(skill.Name, context.TextQuery)
                        + GetRecencyBoost(skill.CreatedAt, multiplier: 0.45),
                IsContentMatch = evaluation.IsContentMatch,
                SortTimestamp = skill.CreatedAt
            });
        }
    }

    private void SearchAgents(
        SearchSnapshot snapshot,
        SearchContext context,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var agent in snapshot.Agents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!context.InRange(agent.CreatedAt))
                continue;

            var evaluation = SearchEngine.Evaluate(
                context.TextQuery,
                IncludeContentFields(context.Mode)
                    ?
                    [
                        SearchField.Primary(agent.Name, 3.4),
                        new SearchField(agent.Description, 1.8),
                        SearchField.Content(agent.SystemPrompt, 0.95)
                    ]
                    :
                    [
                        SearchField.Primary(agent.Name, 3.4),
                        new SearchField(agent.Description, 1.8)
                    ]);

            if (!evaluation.IsMatch)
                continue;

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Lumis,
                Title = agent.Name,
                Subtitle = BuildSecondarySubtitle(agent.Description, evaluation.BestContentSnippet),
                NavIndex = 4,
                Item = agent,
                Score = evaluation.Score
                        + GetTitleBonus(agent.Name, context.TextQuery)
                        + GetRecencyBoost(agent.CreatedAt, multiplier: 0.45),
                IsContentMatch = evaluation.IsContentMatch,
                SortTimestamp = agent.CreatedAt
            });
        }
    }

    private void SearchMemories(
        SearchSnapshot snapshot,
        SearchContext context,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var memory in snapshot.Memories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!context.InRange(memory.UpdatedAt))
                continue;

            var evaluation = SearchEngine.Evaluate(
                context.TextQuery,
                IncludeContentFields(context.Mode)
                    ?
                    [
                        SearchField.Primary(memory.Key, 3.3),
                        new SearchField(memory.Category, 1.5),
                        SearchField.Content(memory.Content, 1.1)
                    ]
                    :
                    [
                        SearchField.Primary(memory.Key, 3.3),
                        new SearchField(memory.Category, 1.5)
                    ]);

            if (!evaluation.IsMatch)
                continue;

            var defaultSubtitle = string.IsNullOrWhiteSpace(memory.Category)
                ? TrimForSubtitle(memory.Content)
                : $"[{memory.Category}] {TrimForSubtitle(memory.Content)}";

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Memories,
                Title = memory.Key,
                Subtitle = BuildSecondarySubtitle(defaultSubtitle, evaluation.BestContentSnippet),
                NavIndex = 5,
                Item = memory,
                Score = evaluation.Score
                        + GetTitleBonus(memory.Key, context.TextQuery)
                        + GetRecencyBoost(memory.UpdatedAt, multiplier: 0.7),
                IsContentMatch = evaluation.IsContentMatch,
                SortTimestamp = memory.UpdatedAt
            });
        }
    }

    private void SearchMcpServers(
        SearchSnapshot snapshot,
        SearchContext context,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var server in snapshot.McpServers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!context.InRange(server.CreatedAt))
                continue;

            var evaluation = SearchEngine.Evaluate(
                context.TextQuery,
                context.Mode == GlobalSearchExecutionMode.Preview
                    ?
                    [
                        SearchField.Primary(server.Name, 3.2),
                        new SearchField(server.Description, 1.7)
                    ]
                    :
                    [
                        SearchField.Primary(server.Name, 3.2),
                        new SearchField(server.Description, 1.7),
                        new SearchField(server.Command ?? "", 1.0),
                        new SearchField(string.Join(' ', server.Args), 0.9),
                        new SearchField(server.Url ?? "", 0.8),
                        new SearchField(string.Join(' ', server.Tools), 0.85)
                    ]);

            if (!evaluation.IsMatch)
                continue;

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.McpServers,
                Title = server.Name,
                Subtitle = BuildSecondarySubtitle(server.Description, evaluation.BestContentSnippet),
                NavIndex = 6,
                Item = server,
                Score = evaluation.Score
                        + GetTitleBonus(server.Name, context.TextQuery)
                        + GetRecencyBoost(server.CreatedAt, multiplier: 0.4),
                IsContentMatch = false,
                SortTimestamp = server.CreatedAt
            });
        }
    }

    private static void SearchSettings(
        SearchQuery query,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var setting in SettingsIndex)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var evaluation = SearchEngine.Evaluate(query,
            [
                SearchField.Primary(setting.Name, 3.2),
                new SearchField(setting.Page, 1.6)
            ]);

            if (!evaluation.IsMatch)
                continue;

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Settings,
                Title = setting.Name,
                Subtitle = setting.Page,
                NavIndex = 7,
                Score = evaluation.Score + GetTitleBonus(setting.Name, query),
                SettingsPageIndex = setting.PageIndex
            });
        }
    }

    /// <summary>
    /// Time-driven browse: lists items inside the requested window (or all when only a
    /// recency preference was given), ordered by recency. Used when the query was purely
    /// temporal ("yesterday", "last week", "recent").
    /// </summary>
    private void BuildBrowseResults(
        SearchSnapshot snapshot,
        SearchContext context,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var chat in snapshot.Chats)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.InRange(chat.UpdatedAt))
                continue;

            snapshot.ProjectNames.TryGetValue(chat.ProjectId ?? Guid.Empty, out var projectName);
            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Chats,
                Title = chat.Title,
                Subtitle = BuildChatSubtitle(projectName, chat.UpdatedAt, snippet: null),
                NavIndex = 0,
                Item = chat,
                Score = BrowseBaseChats + GetChatRecencyBoost(chat.UpdatedAt, context.HasRecencyIntent),
                SortTimestamp = chat.UpdatedAt
            });
        }

        foreach (var job in snapshot.BackgroundJobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.InRange(job.UpdatedAt))
                continue;

            var status = job.IsEnabled ? "Enabled" : "Paused";
            var next = job.NextRunAt.HasValue ? $" · next {FormatRelativeTime(job.NextRunAt.Value)}" : "";
            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.BackgroundJobs,
                Title = job.Name,
                Subtitle = $"{status}{next} · {BackgroundJobSchedule.Describe(job)}",
                NavIndex = 1,
                Item = job,
                Score = BrowseBaseJobs + GetRecencyBoost(job.UpdatedAt, multiplier: 0.7),
                SortTimestamp = job.UpdatedAt
            });
        }

        foreach (var project in snapshot.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot.ProjectChatCounts.TryGetValue(project.Id, out var chatCount);
            snapshot.ProjectLastActivity.TryGetValue(project.Id, out var latestActivity);
            var sortTimestamp = latestActivity == default ? project.CreatedAt : latestActivity;
            if (!context.InRange(sortTimestamp))
                continue;

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Projects,
                Title = project.Name,
                Subtitle = chatCount == 0
                    ? "No chats"
                    : $"{chatCount} chat{(chatCount == 1 ? "" : "s")} · {FormatRelativeTime(sortTimestamp)}",
                NavIndex = 2,
                Item = project,
                Score = BrowseBaseProjects + GetRecencyBoost(sortTimestamp, multiplier: 0.9),
                SortTimestamp = sortTimestamp
            });
        }

        foreach (var skill in snapshot.Skills)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.InRange(skill.CreatedAt))
                continue;

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Skills,
                Title = skill.Name,
                Subtitle = skill.Description,
                NavIndex = 3,
                Item = skill,
                Score = BrowseBaseSkills + GetRecencyBoost(skill.CreatedAt, multiplier: 0.45),
                SortTimestamp = skill.CreatedAt
            });
        }

        foreach (var agent in snapshot.Agents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.InRange(agent.CreatedAt))
                continue;

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Lumis,
                Title = agent.Name,
                Subtitle = agent.Description,
                NavIndex = 4,
                Item = agent,
                Score = BrowseBaseAgents + GetRecencyBoost(agent.CreatedAt, multiplier: 0.45),
                SortTimestamp = agent.CreatedAt
            });
        }

        foreach (var memory in snapshot.Memories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.InRange(memory.UpdatedAt))
                continue;

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Memories,
                Title = memory.Key,
                Subtitle = string.IsNullOrWhiteSpace(memory.Category)
                    ? TrimForSubtitle(memory.Content)
                    : $"[{memory.Category}] {TrimForSubtitle(memory.Content)}",
                NavIndex = 5,
                Item = memory,
                Score = BrowseBaseMemories + GetRecencyBoost(memory.UpdatedAt, multiplier: 0.7),
                SortTimestamp = memory.UpdatedAt
            });
        }

        foreach (var server in snapshot.McpServers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.InRange(server.CreatedAt))
                continue;

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.McpServers,
                Title = server.Name,
                Subtitle = server.Description,
                NavIndex = 6,
                Item = server,
                Score = BrowseBaseMcp + GetRecencyBoost(server.CreatedAt, multiplier: 0.4),
                SortTimestamp = server.CreatedAt
            });
        }
    }

    private IReadOnlyList<GlobalSearchMatch> BuildDefaultResults(SearchSnapshot snapshot)
    {
        var results = new List<GlobalSearchMatch>();

        foreach (var chat in snapshot.Chats.OrderByDescending(static chat => chat.UpdatedAt).Take(6))
        {
            snapshot.ProjectNames.TryGetValue(chat.ProjectId ?? Guid.Empty, out var projectName);
            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Chats,
                Title = chat.Title,
                Subtitle = BuildChatSubtitle(projectName, chat.UpdatedAt, snippet: null),
                NavIndex = 0,
                Item = chat,
                Score = BrowseBaseChats + GetChatRecencyBoost(chat.UpdatedAt, hasRecencyIntent: false),
                SortTimestamp = chat.UpdatedAt
            });
        }

        foreach (var job in snapshot.BackgroundJobs.OrderByDescending(static job => job.UpdatedAt).Take(4))
        {
            var status = job.IsEnabled ? "Enabled" : "Paused";
            var next = job.NextRunAt.HasValue ? $" · next {FormatRelativeTime(job.NextRunAt.Value)}" : "";
            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.BackgroundJobs,
                Title = job.Name,
                Subtitle = $"{status}{next} · {BackgroundJobSchedule.Describe(job)}",
                NavIndex = 1,
                Item = job,
                Score = BrowseBaseJobs + GetRecencyBoost(job.UpdatedAt, multiplier: 0.7),
                SortTimestamp = job.UpdatedAt
            });
        }

        foreach (var project in snapshot.Projects
                     .OrderByDescending(project => snapshot.ProjectLastActivity.TryGetValue(project.Id, out var activity)
                         ? activity
                         : project.CreatedAt)
                     .Take(4))
        {
            snapshot.ProjectChatCounts.TryGetValue(project.Id, out var chatCount);
            snapshot.ProjectLastActivity.TryGetValue(project.Id, out var latestActivity);
            var sortTimestamp = latestActivity == default ? project.CreatedAt : latestActivity;
            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Projects,
                Title = project.Name,
                Subtitle = chatCount == 0
                    ? "No chats"
                    : $"{chatCount} chat{(chatCount == 1 ? "" : "s")} · {FormatRelativeTime(sortTimestamp)}",
                NavIndex = 2,
                Item = project,
                Score = BrowseBaseProjects + GetRecencyBoost(sortTimestamp, multiplier: 0.9),
                SortTimestamp = sortTimestamp
            });
        }

        foreach (var skill in snapshot.Skills.OrderByDescending(static skill => skill.CreatedAt).Take(4))
        {
            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Skills,
                Title = skill.Name,
                Subtitle = skill.Description,
                NavIndex = 3,
                Item = skill,
                Score = BrowseBaseSkills + GetRecencyBoost(skill.CreatedAt, multiplier: 0.45),
                SortTimestamp = skill.CreatedAt
            });
        }

        foreach (var agent in snapshot.Agents.OrderByDescending(static agent => agent.CreatedAt).Take(4))
        {
            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Lumis,
                Title = agent.Name,
                Subtitle = agent.Description,
                NavIndex = 4,
                Item = agent,
                Score = BrowseBaseAgents + GetRecencyBoost(agent.CreatedAt, multiplier: 0.45),
                SortTimestamp = agent.CreatedAt
            });
        }

        foreach (var memory in snapshot.Memories.OrderByDescending(static memory => memory.UpdatedAt).Take(4))
        {
            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Memories,
                Title = memory.Key,
                Subtitle = string.IsNullOrWhiteSpace(memory.Category)
                    ? TrimForSubtitle(memory.Content)
                    : $"[{memory.Category}] {TrimForSubtitle(memory.Content)}",
                NavIndex = 5,
                Item = memory,
                Score = BrowseBaseMemories + GetRecencyBoost(memory.UpdatedAt, multiplier: 0.7),
                SortTimestamp = memory.UpdatedAt
            });
        }

        foreach (var server in snapshot.McpServers.OrderByDescending(static server => server.CreatedAt).Take(4))
        {
            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.McpServers,
                Title = server.Name,
                Subtitle = server.Description,
                NavIndex = 6,
                Item = server,
                Score = BrowseBaseMcp + GetRecencyBoost(server.CreatedAt, multiplier: 0.4),
                SortTimestamp = server.CreatedAt
            });
        }

        results.Sort(static (left, right) =>
        {
            var scoreComparison = right.Score.CompareTo(left.Score);
            if (scoreComparison != 0)
                return scoreComparison;

            var rightTimestamp = right.SortTimestamp ?? DateTimeOffset.MinValue;
            var leftTimestamp = left.SortTimestamp ?? DateTimeOffset.MinValue;
            return rightTimestamp.CompareTo(leftTimestamp);
        });

        return results;
    }

    /// <summary>
    /// Resolves the content field for a chat according to the execution mode. Cheap modes only use
    /// already-indexed content (no disk reads); Interactive additionally builds a bounded number of
    /// the most recent cold chats; Full builds everything. A cheap substring gate avoids running the
    /// fuzzy evaluator on chats whose content cannot contain the query.
    /// </summary>
    private IReadOnlyList<PreparedSearchField>? GetChatContentFields(
        Chat chat,
        GlobalSearchExecutionMode executionMode,
        IReadOnlySet<Guid>? interactiveColdChatIds,
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        if (executionMode == GlobalSearchExecutionMode.Preview)
            return null;

        cancellationToken.ThrowIfCancellationRequested();

        ChatContentIndex.Entry? entry;
        switch (executionMode)
        {
            case GlobalSearchExecutionMode.Fast:
                // Loaded chats rebuild cheaply from memory; cold chats use only indexed content.
                entry = _contentIndex.GetEntry(chat, allowBuild: false);
                break;

            case GlobalSearchExecutionMode.Interactive:
                if (chat.Messages.Count > 0 || _contentIndex.IsCached(chat.Id))
                    entry = _contentIndex.GetEntry(chat, allowBuild: false);
                else if (interactiveColdChatIds is not null && interactiveColdChatIds.Contains(chat.Id))
                    entry = _contentIndex.GetEntry(chat, allowBuild: true);
                else
                    entry = null;
                break;

            default: // Full
                entry = _contentIndex.GetEntry(chat, allowBuild: true);
                break;
        }

        if (entry is null || entry.IsEmpty || !entry.MayMatch(query))
            return null;

        return entry.ToContentFields(UserMessageSearchWeight, AssistantMessageSearchWeight);
    }

    private static IReadOnlyList<GlobalSearchMatch> LimitResultsPerCategory(
        IEnumerable<GlobalSearchMatch> sortedResults,
        int perCategoryLimit)
    {
        var counts = new Dictionary<GlobalSearchCategory, int>();
        var limited = new List<GlobalSearchMatch>();

        foreach (var result in sortedResults)
        {
            counts.TryGetValue(result.Category, out var count);
            if (count >= perCategoryLimit)
                continue;

            counts[result.Category] = count + 1;
            limited.Add(result);
        }

        return limited;
    }

    private static bool IncludeContentFields(GlobalSearchExecutionMode executionMode)
        => executionMode is GlobalSearchExecutionMode.Interactive or GlobalSearchExecutionMode.Full;

    private HashSet<Guid>? GetInteractiveColdChatIds(
        IReadOnlyList<Chat> chats,
        SearchContext context,
        CancellationToken cancellationToken)
    {
        var recentColdChats = new List<Chat>(InteractiveColdChatContentLimit);
        foreach (var chat in chats)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (chat.Messages.Count > 0 || _contentIndex.IsCached(chat.Id))
                continue;

            if (!context.InRange(chat.UpdatedAt))
                continue;

            if (recentColdChats.Count < InteractiveColdChatContentLimit)
            {
                recentColdChats.Add(chat);
                continue;
            }

            var oldestIndex = 0;
            for (var index = 1; index < recentColdChats.Count; index++)
            {
                if (recentColdChats[index].UpdatedAt < recentColdChats[oldestIndex].UpdatedAt)
                    oldestIndex = index;
            }

            if (chat.UpdatedAt > recentColdChats[oldestIndex].UpdatedAt)
                recentColdChats[oldestIndex] = chat;
        }

        return recentColdChats.Count == 0
            ? null
            : recentColdChats.Select(static chat => chat.Id).ToHashSet();
    }

    // ── Content index maintenance (called from the view model) ────────────────

    /// <summary>Builds content entries for all cold chats in the background so search covers the full history.</summary>
    public Task WarmChatContentAsync(CancellationToken cancellationToken = default)
        => _contentIndex.WarmAsync(_getData().Chats.ToArray(), cancellationToken);

    /// <summary>
    /// Builds content entries for the supplied cold chats. The caller captures the chat list on the
    /// UI thread (<see cref="AppData.Chats"/> is not thread-safe) so this can run off the UI thread.
    /// </summary>
    public Task WarmChatContentAsync(IReadOnlyList<Chat> chats, CancellationToken cancellationToken = default)
        => _contentIndex.WarmAsync(chats, cancellationToken);

    /// <summary>Forces a chat's content entry to rebuild on next access (after it was edited/saved).</summary>
    public void InvalidateChatContent(Guid chatId) => _contentIndex.Invalidate(chatId);

    /// <summary>Drops a deleted chat's content entry.</summary>
    public void RemoveChatContent(Guid chatId) => _contentIndex.Remove(chatId);

    /// <summary>Removes index entries for chats that no longer exist.</summary>
    public void PruneChatContent()
        => _contentIndex.Prune(_getData().Chats.Select(static chat => chat.Id));

    /// <summary>Removes index entries for chats not present in the supplied live id set (caller-captured).</summary>
    public void PruneChatContent(IEnumerable<Guid> liveChatIds)
        => _contentIndex.Prune(liveChatIds);

    /// <summary>Loads a previously persisted content index. Returns the number of entries restored.</summary>
    public int LoadChatContentIndex(string path) => _contentIndex.Load(path);

    /// <summary>Persists the content index so a restart can skip re-reading the whole history.</summary>
    public void SaveChatContentIndex(string path) => _contentIndex.Save(path);

    private double GetRecencyBoost(DateTimeOffset timestamp, double multiplier)
    {
        var age = _nowProvider() - timestamp;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        var days = age.TotalDays;
        var baseBoost = 52d / (1d + (days / 7d));
        return baseBoost * multiplier;
    }

    /// <summary>
    /// Recency signal for chats, where finding the right recent conversation matters most.
    /// Decays on a ~3-week half-life and is amplified when the query expressed a "recent" intent,
    /// so it meaningfully reorders near-ties without overriding clearly stronger relevance.
    /// </summary>
    private double GetChatRecencyBoost(DateTimeOffset timestamp, bool hasRecencyIntent)
    {
        var age = _nowProvider() - timestamp;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        var days = age.TotalDays;
        var boost = 240d / (1d + (days / 21d));
        return hasRecencyIntent ? boost * 2.5 : boost;
    }

    /// <summary>
    /// True when every query term occurs as a real substring/token of the title (separator-insensitive).
    /// Used to tell a genuine title match apart from a purely fuzzy one before applying the fuzzy damp.
    /// </summary>
    private static bool QueryTermsPresentInTitle(string title, SearchQuery query)
    {
        if (query.IsEmpty)
            return false;

        var titleText = SearchText.Create(title);
        if (titleText.IsEmpty)
            return false;

        foreach (var term in query.Terms)
        {
            if (term.IsEmpty)
                continue;

            if (!titleText.Compact.Contains(term.Compact, StringComparison.Ordinal)
                && !titleText.Normalized.Contains(term.Normalized, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static double GetTitleBonus(string title, SearchQuery query)
    {
        var preparedTitle = SearchText.Create(title);
        if (preparedTitle.IsEmpty || query.IsEmpty)
            return 0;

        if (string.Equals(preparedTitle.Normalized, query.Text.Normalized, StringComparison.Ordinal)
            || string.Equals(preparedTitle.Compact, query.Text.Compact, StringComparison.Ordinal))
        {
            return 260;
        }

        if (preparedTitle.Normalized.StartsWith(query.Text.Normalized, StringComparison.Ordinal)
            || preparedTitle.Compact.StartsWith(query.Text.Compact, StringComparison.Ordinal))
        {
            return 185;
        }

        if (preparedTitle.Normalized.Contains(query.Text.Normalized, StringComparison.Ordinal)
            || preparedTitle.Compact.Contains(query.Text.Compact, StringComparison.Ordinal))
        {
            return 135;
        }

        return string.Equals(preparedTitle.Initials, query.Text.Compact, StringComparison.Ordinal) ? 120 : 0;
    }

    private string BuildChatSubtitle(string? projectName, DateTimeOffset updatedAt, string? snippet)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(projectName))
            parts.Add(projectName);

        parts.Add(FormatRelativeTime(updatedAt));

        if (!string.IsNullOrWhiteSpace(snippet))
            parts.Add(snippet);

        return string.Join(" · ", parts);
    }

    private static string BuildSecondarySubtitle(string? primary, string? contentSnippet)
    {
        if (string.IsNullOrWhiteSpace(contentSnippet))
            return primary ?? "";

        if (string.IsNullOrWhiteSpace(primary))
            return contentSnippet;

        return $"{primary} · {contentSnippet}";
    }

    private string FormatRelativeTime(DateTimeOffset timestamp)
    {
        var age = _nowProvider() - timestamp;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        if (age < TimeSpan.FromMinutes(1))
            return "just now";
        if (age < TimeSpan.FromHours(1))
            return $"{Math.Max(1, (int)age.TotalMinutes)}m ago";
        if (age < TimeSpan.FromDays(1))
            return $"{Math.Max(1, (int)age.TotalHours)}h ago";
        if (age < TimeSpan.FromDays(7))
            return $"{Math.Max(1, (int)age.TotalDays)}d ago";
        if (age < TimeSpan.FromDays(365))
            return timestamp.ToString("MMM d", CultureInfo.CurrentCulture);

        return timestamp.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }

    private static string TrimForSubtitle(string? text)
    {
        var flattened = CollapseWhitespace(text);
        if (flattened.Length <= 80)
            return flattened;

        return flattened[..80] + "…";
    }

    private static string CollapseWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var builder = new StringBuilder(text.Length);
        var previousWasWhitespace = false;

        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                if (previousWasWhitespace)
                    continue;

                builder.Append(' ');
                previousWasWhitespace = true;
                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }

    private static SearchSnapshot CaptureSnapshot(AppData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var chats = data.Chats.ToArray();
        var projects = data.Projects.ToArray();
        var skills = data.Skills.ToArray();
        var agents = data.Agents.ToArray();
        var memories = data.Memories.ToArray();
        var servers = data.McpServers.ToArray();
        var backgroundJobs = data.BackgroundJobs.ToArray();

        var projectNames = projects.ToDictionary(static project => project.Id, static project => project.Name);
        var projectChatCounts = chats
            .Where(static chat => chat.ProjectId.HasValue)
            .GroupBy(static chat => chat.ProjectId!.Value)
            .ToDictionary(static group => group.Key, static group => group.Count());

        var projectLastActivity = chats
            .Where(static chat => chat.ProjectId.HasValue)
            .GroupBy(static chat => chat.ProjectId!.Value)
            .ToDictionary(static group => group.Key, static group => group.Max(static chat => chat.UpdatedAt));

        return new SearchSnapshot(
            chats,
            projects,
            skills,
            agents,
            backgroundJobs,
            memories,
            servers,
            projectNames,
            projectChatCounts,
            projectLastActivity);
    }

    private sealed class SearchContext(
        SearchQuery textQuery,
        SearchTimeRange? range,
        bool hasRecencyIntent,
        GlobalSearchExecutionMode mode)
    {
        public SearchQuery TextQuery { get; } = textQuery;
        public SearchTimeRange? Range { get; } = range;
        public bool HasRecencyIntent { get; } = hasRecencyIntent;
        public GlobalSearchExecutionMode Mode { get; } = mode;
        public bool HasRange => Range.HasValue;

        /// <summary>True when there is no residual text to match (list by time/recency instead).</summary>
        public bool IsBrowse => TextQuery.IsEmpty;

        public bool InRange(DateTimeOffset timestamp)
            => Range is not { } window || window.Contains(timestamp);
    }

    internal sealed class SearchSnapshot(
        Chat[] chats,
        Project[] projects,
        Skill[] skills,
        LumiAgent[] agents,
        BackgroundJob[] backgroundJobs,
        Memory[] memories,
        McpServer[] mcpServers,
        IReadOnlyDictionary<Guid, string> projectNames,
        IReadOnlyDictionary<Guid, int> projectChatCounts,
        IReadOnlyDictionary<Guid, DateTimeOffset> projectLastActivity)
    {
        public Chat[] Chats { get; } = chats;
        public Project[] Projects { get; } = projects;
        public Skill[] Skills { get; } = skills;
        public LumiAgent[] Agents { get; } = agents;
        public BackgroundJob[] BackgroundJobs { get; } = backgroundJobs;
        public Memory[] Memories { get; } = memories;
        public McpServer[] McpServers { get; } = mcpServers;
        public IReadOnlyDictionary<Guid, string> ProjectNames { get; } = projectNames;
        public IReadOnlyDictionary<Guid, int> ProjectChatCounts { get; } = projectChatCounts;
        public IReadOnlyDictionary<Guid, DateTimeOffset> ProjectLastActivity { get; } = projectLastActivity;
    }

    public sealed class PreparedSearch
    {
        internal PreparedSearch(
            SearchSnapshot snapshot,
            SearchQuery textQuery,
            SearchTimeRange? range,
            bool hasRecencyIntent,
            bool useDefaultResults)
        {
            Snapshot = snapshot;
            TextQuery = textQuery;
            Range = range;
            HasRecencyIntent = hasRecencyIntent;
            UseDefaultResults = useDefaultResults;
        }

        internal SearchSnapshot Snapshot { get; }
        internal SearchQuery TextQuery { get; }
        internal SearchTimeRange? Range { get; }
        internal bool HasRecencyIntent { get; }
        internal bool UseDefaultResults { get; }
    }

    private readonly record struct SearchSettingEntry(string Name, string Page, int PageIndex);
}
