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
    Fast,
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
    private readonly Func<Chat, ChatSearchSnapshot> _chatSnapshotProvider;
    private readonly Func<DateTimeOffset> _nowProvider;
    private readonly object _chatFieldCacheSync = new();
    private readonly Dictionary<Guid, CachedChatFields> _chatFieldCache = [];

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
        Func<DateTimeOffset>? nowProvider = null)
    {
        _getData = getData ?? throw new ArgumentNullException(nameof(getData));
        _chatSnapshotProvider = chatSnapshotProvider ?? throw new ArgumentNullException(nameof(chatSnapshotProvider));
        _nowProvider = nowProvider ?? (() => DateTimeOffset.Now);
    }

    public Task<IReadOnlyList<GlobalSearchMatch>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
        => SearchAsync(query, GlobalSearchExecutionMode.Full, cancellationToken);

    public Task<IReadOnlyList<GlobalSearchMatch>> SearchAsync(
        string query,
        GlobalSearchExecutionMode executionMode,
        CancellationToken cancellationToken = default)
    {
        var snapshot = CaptureSnapshot(_getData());
        var trimmedQuery = query?.Trim() ?? "";

        if (string.IsNullOrEmpty(trimmedQuery))
            return Task.FromResult((IReadOnlyList<GlobalSearchMatch>)BuildDefaultResults(snapshot));

        var searchQuery = SearchQuery.Create(trimmedQuery);
        if (searchQuery.IsEmpty)
            return Task.FromResult((IReadOnlyList<GlobalSearchMatch>)BuildDefaultResults(snapshot));

        return Task.Run<IReadOnlyList<GlobalSearchMatch>>(
            () => SearchCore(snapshot, searchQuery, executionMode, cancellationToken),
            cancellationToken);
    }

    private IReadOnlyList<GlobalSearchMatch> SearchCore(
        SearchSnapshot snapshot,
        SearchQuery query,
        GlobalSearchExecutionMode executionMode,
        CancellationToken cancellationToken)
    {
        var results = new List<GlobalSearchMatch>();

        SearchChats(snapshot, query, executionMode, results, cancellationToken);
        SearchJobs(snapshot, query, results, cancellationToken);
        SearchProjects(snapshot, query, results, cancellationToken);
        SearchSkills(snapshot, query, results, cancellationToken);
        SearchAgents(snapshot, query, results, cancellationToken);
        SearchMemories(snapshot, query, results, cancellationToken);
        SearchMcpServers(snapshot, query, results, cancellationToken);
        SearchSettings(query, results, cancellationToken);

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

        return results;
    }

    private void SearchChats(
        SearchSnapshot snapshot,
        SearchQuery query,
        GlobalSearchExecutionMode executionMode,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var chat in snapshot.Chats)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var titleField = new PreparedSearchField(chat.Title, 3.8, SearchFieldKind.Primary);
            var evaluation = SearchEngine.Evaluate(query, [titleField]);

            if (!evaluation.IsMatch)
            {
                var contentFields = GetChatContentFields(chat, executionMode);
                if (contentFields.Count > 0)
                {
                    var fields = new List<PreparedSearchField>(contentFields.Count + 1) { titleField };
                    fields.AddRange(contentFields);
                    evaluation = SearchEngine.Evaluate(query, fields);
                }
            }

            if (!evaluation.IsMatch)
                continue;

            snapshot.ProjectNames.TryGetValue(chat.ProjectId ?? Guid.Empty, out var projectName);
            var score = evaluation.Score
                        + GetTitleBonus(chat.Title, query)
                        + GetRecencyBoost(chat.UpdatedAt, multiplier: 1.6);

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
        SearchQuery query,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var project in snapshot.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var evaluation = SearchEngine.Evaluate(query,
            [
                SearchField.Primary(project.Name, 3.5),
                SearchField.Content(project.Instructions, 1.0)
            ]);

            if (!evaluation.IsMatch)
                continue;

            snapshot.ProjectChatCounts.TryGetValue(project.Id, out var chatCount);
            snapshot.ProjectLastActivity.TryGetValue(project.Id, out var latestActivity);
            var sortTimestamp = latestActivity == default ? project.CreatedAt : latestActivity;
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
                        + GetTitleBonus(project.Name, query)
                        + GetRecencyBoost(sortTimestamp, multiplier: 0.8),
                IsContentMatch = evaluation.IsContentMatch,
                SortTimestamp = sortTimestamp
            });
        }
    }

    private void SearchJobs(
        SearchSnapshot snapshot,
        SearchQuery query,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var job in snapshot.BackgroundJobs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var evaluation = SearchEngine.Evaluate(query,
            [
                SearchField.Primary(job.Name, 3.5),
                new SearchField(job.Description, 1.8),
                SearchField.Content(job.Prompt, 1.1),
                new SearchField(job.LastRunSummary, 0.9)
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
                        + GetTitleBonus(job.Name, query)
                        + GetRecencyBoost(job.UpdatedAt, multiplier: 0.6),
                IsContentMatch = evaluation.IsContentMatch,
                SortTimestamp = job.UpdatedAt
            });
        }
    }

    private void SearchSkills(
        SearchSnapshot snapshot,
        SearchQuery query,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var skill in snapshot.Skills)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var evaluation = SearchEngine.Evaluate(query,
            [
                SearchField.Primary(skill.Name, 3.4),
                new SearchField(skill.Description, 1.8),
                SearchField.Content(skill.Content, 0.95)
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
                        + GetTitleBonus(skill.Name, query)
                        + GetRecencyBoost(skill.CreatedAt, multiplier: 0.45),
                IsContentMatch = evaluation.IsContentMatch,
                SortTimestamp = skill.CreatedAt
            });
        }
    }

    private void SearchAgents(
        SearchSnapshot snapshot,
        SearchQuery query,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var agent in snapshot.Agents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var evaluation = SearchEngine.Evaluate(query,
            [
                SearchField.Primary(agent.Name, 3.4),
                new SearchField(agent.Description, 1.8),
                SearchField.Content(agent.SystemPrompt, 0.95)
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
                        + GetTitleBonus(agent.Name, query)
                        + GetRecencyBoost(agent.CreatedAt, multiplier: 0.45),
                IsContentMatch = evaluation.IsContentMatch,
                SortTimestamp = agent.CreatedAt
            });
        }
    }

    private void SearchMemories(
        SearchSnapshot snapshot,
        SearchQuery query,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var memory in snapshot.Memories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var evaluation = SearchEngine.Evaluate(query,
            [
                SearchField.Primary(memory.Key, 3.3),
                new SearchField(memory.Category, 1.5),
                SearchField.Content(memory.Content, 1.1)
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
                        + GetTitleBonus(memory.Key, query)
                        + GetRecencyBoost(memory.UpdatedAt, multiplier: 0.7),
                IsContentMatch = evaluation.IsContentMatch,
                SortTimestamp = memory.UpdatedAt
            });
        }
    }

    private void SearchMcpServers(
        SearchSnapshot snapshot,
        SearchQuery query,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var server in snapshot.McpServers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var evaluation = SearchEngine.Evaluate(query,
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
                        + GetTitleBonus(server.Name, query)
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
                Score = 1_000 + GetRecencyBoost(chat.UpdatedAt, multiplier: 2.0),
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
                Score = 920 + GetRecencyBoost(job.UpdatedAt, multiplier: 0.7),
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
                Score = 900 + GetRecencyBoost(sortTimestamp, multiplier: 0.9),
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
                Score = 800 + GetRecencyBoost(skill.CreatedAt, multiplier: 0.45),
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
                Score = 780 + GetRecencyBoost(agent.CreatedAt, multiplier: 0.45),
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
                Score = 760 + GetRecencyBoost(memory.UpdatedAt, multiplier: 0.7),
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
                Score = 740 + GetRecencyBoost(server.CreatedAt, multiplier: 0.4),
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

    private IReadOnlyList<PreparedSearchField> GetChatContentFields(
        Chat chat,
        GlobalSearchExecutionMode executionMode)
    {
        if (chat.Messages.Count > 0)
            return GetChatContentFields(chat, _chatSnapshotProvider(chat));

        if (executionMode == GlobalSearchExecutionMode.Fast)
            return TryGetCachedChatContentFields(chat.Id, out var cachedFields) ? cachedFields : [];

        return GetChatContentFields(chat, _chatSnapshotProvider(chat));
    }

    private IReadOnlyList<PreparedSearchField> GetChatContentFields(Chat chat, ChatSearchSnapshot snapshot)
    {
        lock (_chatFieldCacheSync)
        {
            if (_chatFieldCache.TryGetValue(chat.Id, out var cached)
                && string.Equals(cached.Version, snapshot.Version, StringComparison.Ordinal))
            {
                return cached.Fields;
            }
        }

        var fields = snapshot.Messages
            .Where(static message => !string.IsNullOrWhiteSpace(message.Text))
            .Select(static message => new PreparedSearchField(message.Text, 1.0, SearchFieldKind.Content))
            .ToArray();

        lock (_chatFieldCacheSync)
        {
            _chatFieldCache[chat.Id] = new CachedChatFields(snapshot.Version, fields);
        }

        return fields;
    }

    private bool TryGetCachedChatContentFields(Guid chatId, out IReadOnlyList<PreparedSearchField> fields)
    {
        lock (_chatFieldCacheSync)
        {
            if (_chatFieldCache.TryGetValue(chatId, out var cached))
            {
                fields = cached.Fields;
                return true;
            }
        }

        fields = Array.Empty<PreparedSearchField>();
        return false;
    }

    private double GetRecencyBoost(DateTimeOffset timestamp, double multiplier)
    {
        var age = _nowProvider() - timestamp;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        var days = age.TotalDays;
        var baseBoost = 52d / (1d + (days / 7d));
        return baseBoost * multiplier;
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

    private sealed class SearchSnapshot(
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

    private readonly record struct CachedChatFields(string Version, IReadOnlyList<PreparedSearchField> Fields);
    private readonly record struct SearchSettingEntry(string Name, string Page, int PageIndex);
}
