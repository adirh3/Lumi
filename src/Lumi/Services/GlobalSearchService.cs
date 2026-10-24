using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Localization;
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

/// <summary>
/// Where some text occurs in a chat: in the user's messages, in Lumi's, and one such message to quote
/// (a stored message's row in <see cref="MessageKey"/>, or its text in <see cref="Text"/>).
/// </summary>
public readonly record struct ChatContentHit(bool InUserMessage, bool InAnswer, long MessageKey, string? Text);

/// <summary>Finds chats by what was said in them (<see cref="DataStore"/> looks in the chat database).</summary>
public interface IChatContentSearch
{
    /// <summary>
    /// The chats whose messages contain <paramref name="text"/>: a run of three or more characters,
    /// anywhere, ignoring case and accents. Shorter text finds nothing.
    /// </summary>
    IReadOnlyDictionary<Guid, ChatContentHit> FindChatsContaining(string text);

    /// <summary>The text of the message a hit points at, to quote it; null when it is gone.</summary>
    string? ReadHitText(ChatContentHit hit);
}

public sealed class GlobalSearchService
{
    private readonly Func<AppData> _getData;
    private readonly Func<DateTimeOffset> _nowProvider;
    private readonly IChatContentSearch? _chatContent;
    private const double UserMessageSearchWeight = 3.0;
    private const double AssistantMessageSearchWeight = 1.0;

    // What a query word found in a chat's messages scores, before its weight: a real occurrence,
    // so about what the text engine gives a word it finds whole (1,600, less a share for where).
    private const double ContentTermScore = 1_500;

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
        new(nameof(Loc.Setting_YourName), nameof(Loc.Settings_Profile), 0),
        new(nameof(Loc.FieldLabel_Language), nameof(Loc.Settings_Profile), 0),
        new(nameof(Loc.Setting_LaunchAtStartup), nameof(Loc.Settings_General), 1),
        new(nameof(Loc.Setting_StartMinimized), nameof(Loc.Settings_General), 1),
        new(nameof(Loc.Setting_MinimizeToTray), nameof(Loc.Settings_General), 1),
        new(nameof(Loc.Setting_EnableNotifications), nameof(Loc.Settings_General), 1),
        new(nameof(Loc.Setting_OfferCopiedCapabilities), nameof(Loc.Settings_General), 1),
        new(nameof(Loc.Setting_GlobalHotkey), nameof(Loc.Settings_General), 1),
        new(nameof(Loc.Setting_KeyboardShortcuts), nameof(Loc.Settings_General), 1),
        new(nameof(Loc.Search_SettingMobile), nameof(Loc.Settings_Mobile), 2),
        new(nameof(Loc.Onboarding_DarkMode), nameof(Loc.Settings_Appearance), 3),
        new(nameof(Loc.Search_SettingTheme), nameof(Loc.Settings_Appearance), 3),
        new(nameof(Loc.Setting_AccentColor), nameof(Loc.Settings_Appearance), 3),
        new(nameof(Loc.Setting_WindowMaterial), nameof(Loc.Settings_Appearance), 3),
        new(nameof(Loc.Search_SettingCompactDensity), nameof(Loc.Settings_Appearance), 3),
        new(nameof(Loc.Search_SettingFontSize), nameof(Loc.Settings_Appearance), 3),
        new(nameof(Loc.Setting_AmbientPresence), nameof(Loc.Settings_Appearance), 3),
        new(nameof(Loc.Setting_AnimatePresenceWhileWorking), nameof(Loc.Settings_Appearance), 3),
        new(nameof(Loc.Setting_ShowAnimations), nameof(Loc.Settings_Appearance), 3),
        new(nameof(Loc.Search_SettingSendWithEnter), nameof(Loc.Settings_Chat), 4),
        new(nameof(Loc.Setting_ShowTimestamps), nameof(Loc.Settings_Chat), 4),
        new(nameof(Loc.Setting_ShowToolCalls), nameof(Loc.Settings_Chat), 4),
        new(nameof(Loc.Setting_ShowReasoning), nameof(Loc.Settings_Chat), 4),
        new(nameof(Loc.Setting_ExpandReasoningWhileStreaming), nameof(Loc.Settings_Chat), 4),
        new(nameof(Loc.Settings_AiFeatures), nameof(Loc.Settings_AiFeatures), 10),
        new(nameof(Loc.Search_SettingAiDefaultModel), nameof(Loc.Settings_AiFeatures), 10),
        new(nameof(Loc.Search_SettingBackgroundModel), nameof(Loc.Settings_AiFeatures), 10),
        new(nameof(Loc.AiFeature_ChatTitles), nameof(Loc.Settings_AiFeatures), 10),
        new(nameof(Loc.Search_SettingAutoTitles), nameof(Loc.Settings_AiFeatures), 10),
        new(nameof(Loc.AiFeature_FollowUps), nameof(Loc.Settings_AiFeatures), 10),
        new(nameof(Loc.Search_SettingSuggestFollowUps), nameof(Loc.Settings_AiFeatures), 10),
        new(nameof(Loc.AiFeature_Memories), nameof(Loc.Settings_AiFeatures), 10),
        new(nameof(Loc.Search_SettingAutoMemories), nameof(Loc.Settings_AiFeatures), 10),
        new(nameof(Loc.AiFeature_DailyBriefs), nameof(Loc.Settings_AiFeatures), 10),
        new(nameof(Loc.Search_SettingGitHubAccount), nameof(Loc.Settings_AIModels), 5),
        new(nameof(Loc.EasyConnect_AccountsTitle), nameof(Loc.Settings_AIModels), 5),
        new(nameof(Loc.Settings_Byok_UseByokOnly), nameof(Loc.Settings_AIModels), 5),
        new(nameof(Loc.EasyConnect_Advanced), nameof(Loc.Settings_AIModels), 5),
        new(nameof(Loc.Setting_PreferredModel), nameof(Loc.Settings_AIModels), 5),
        new(nameof(Loc.Search_SettingAutoSaveChats), nameof(Loc.Settings_PrivacyData), 6),
        new(nameof(Loc.Setting_ImportCookies), nameof(Loc.Settings_PrivacyData), 6),
        new(nameof(Loc.Setting_ClearAllChats), nameof(Loc.Settings_PrivacyData), 6),
        new(nameof(Loc.Setting_ClearAllMemories), nameof(Loc.Settings_PrivacyData), 6),
        new(nameof(Loc.Setting_ResetAllSettings), nameof(Loc.Settings_PrivacyData), 6),
        new(nameof(Loc.Setting_Version), nameof(Loc.Settings_About), 7)
    ];

    /// <param name="chatContent">Finds chats by their messages; without it, chats are found by their titles.</param>
    public GlobalSearchService(
        Func<AppData> getData,
        IChatContentSearch? chatContent,
        Func<DateTimeOffset>? nowProvider = null)
    {
        _getData = getData ?? throw new ArgumentNullException(nameof(getData));
        _chatContent = chatContent;
        _nowProvider = nowProvider ?? (() => DateTimeOffset.Now);
    }

    /// <summary>
    /// Captures one consistent data snapshot and parses the query once so progressive UI phases
    /// can reuse that work instead of repeatedly copying the full application data.
    /// Must be called from the UI thread because <see cref="AppData"/> collections are UI-owned.
    /// </summary>
    /// <param name="chats">When set, the only chats searched, chosen before results are ranked and limited.</param>
    public PreparedSearch PrepareSearch(string query, Func<Chat, bool>? chats = null)
    {
        var snapshot = CaptureSnapshot(_getData(), chats);
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
            executionMode,
            executionMode == GlobalSearchExecutionMode.Preview || _chatContent is null
                ? null
                : () => preparedSearch.GetContent(_chatContent));

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
        var quotes = new Dictionary<GlobalSearchMatch, ContentQuote>(ReferenceEqualityComparer.Instance);

        if (context.IsBrowse)
        {
            BuildBrowseResults(snapshot, context, results, cancellationToken);
        }
        else
        {
            SearchChats(snapshot, context, results, quotes, cancellationToken);
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

        var limited = LimitResultsPerCategory(results, context.Mode == GlobalSearchExecutionMode.Preview ? 12 : 32);
        return quotes.Count == 0 ? limited : Quote(limited, quotes, cancellationToken);
    }

    /// <summary>
    /// Gives the chats found by what was said in them, among the results shown, the words around it:
    /// one message read for each, rather than one for every chat that matched.
    /// </summary>
    private IReadOnlyList<GlobalSearchMatch> Quote(
        IReadOnlyList<GlobalSearchMatch> results,
        Dictionary<GlobalSearchMatch, ContentQuote> quotes,
        CancellationToken cancellationToken)
    {
        var quoted = new List<GlobalSearchMatch>(results.Count);
        foreach (var match in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!quotes.TryGetValue(match, out var quote) || match.Item is not Chat chat)
            {
                quoted.Add(match);
                continue;
            }

            var snippet = BuildSnippet(quote.Hit.Text ?? _chatContent!.ReadHitText(quote.Hit), quote.Text);
            quoted.Add(new GlobalSearchMatch
            {
                Category = match.Category,
                Title = match.Title,
                Subtitle = BuildChatSubtitle(quote.ProjectName, chat.UpdatedAt, snippet),
                NavIndex = match.NavIndex,
                Item = match.Item,
                Score = match.Score,
                IsContentMatch = match.IsContentMatch,
                SortTimestamp = match.SortTimestamp
            });
        }

        return quoted;
    }

    private void SearchChats(
        SearchSnapshot snapshot,
        SearchContext context,
        ICollection<GlobalSearchMatch> results,
        Dictionary<GlobalSearchMatch, ContentQuote> quotes,
        CancellationToken cancellationToken)
    {
        foreach (var chat in snapshot.Chats)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!context.InRange(chat.UpdatedAt))
                continue;

            var titleField = new PreparedSearchField(chat.Title, 3.8, SearchFieldKind.Primary);
            var evaluation = SearchEngine.Evaluate(context.TextQuery, [titleField]);
            snapshot.ProjectNames.TryGetValue(chat.ProjectId ?? Guid.Empty, out var projectName);

            double relevance;
            ContentQuote? quote = null;
            if (evaluation.IsMatch)
            {
                // A pure fuzzy title match is damped so it cannot bury exact title or real content
                // matches; one where every term is a real substring/token of the title is not.
                relevance = QueryTermsPresentInTitle(chat.Title, context.TextQuery)
                    ? evaluation.Score
                    : evaluation.Score * FuzzyChatRelevanceFactor;
            }
            else if (context.Content?.Invoke().Match(chat.Id, titleField, projectName) is { } contentMatch)
            {
                relevance = contentMatch.Score;
                quote = contentMatch.Quote;
            }
            else
            {
                continue;
            }

            var score = relevance
                        + GetTitleBonus(chat.Title, context.TextQuery)
                        + GetChatRecencyBoost(chat.UpdatedAt, context.HasRecencyIntent);

            var match = new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Chats,
                Title = chat.Title,
                Subtitle = BuildChatSubtitle(projectName, chat.UpdatedAt, snippet: null),
                NavIndex = 0,
                Item = chat,
                Score = score,
                IsContentMatch = quote is not null,
                SortTimestamp = chat.UpdatedAt
            };
            results.Add(match);
            if (quote is not null)
                quotes[match] = quote;
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
                ? Loc.Search_NoChats
                : $"{string.Format(chatCount == 1 ? Loc.Project_ChatCount : Loc.Project_ChatCounts, chatCount)} · {FormatRelativeTime(sortTimestamp)}";

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

            var status = job.IsEnabled ? Loc.Jobs_StateEnabled : Loc.Jobs_StatePaused;
            var next = job.NextRunAt.HasValue ? $" · {string.Format(Loc.Search_JobNext, FormatRelativeTime(job.NextRunAt.Value))}" : "";
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
                        new SearchField(skill.DisplayDescription, 1.8),
                        SearchField.Content(skill.Content, 0.95)
                    ]
                    :
                    [
                        SearchField.Primary(skill.Name, 3.4),
                        new SearchField(skill.DisplayDescription, 1.8)
                    ]);

            if (!evaluation.IsMatch)
                continue;

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Skills,
                Title = skill.Name,
                Subtitle = BuildSecondarySubtitle(skill.DisplayDescription, evaluation.BestContentSnippet),
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
                        new SearchField(agent.DisplayDescription, 1.8),
                        SearchField.Content(agent.SystemPrompt, 0.95)
                    ]
                    :
                    [
                        SearchField.Primary(agent.Name, 3.4),
                        new SearchField(agent.DisplayDescription, 1.8)
                    ]);

            if (!evaluation.IsMatch)
                continue;

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Lumis,
                Title = agent.Name,
                Subtitle = BuildSecondarySubtitle(agent.DisplayDescription, evaluation.BestContentSnippet),
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
                        new SearchField(Loc.GetMemoryCategoryDisplay(memory.Category), 1.5),
                        SearchField.Content(memory.Content, 1.1)
                    ]
                    :
                    [
                        SearchField.Primary(memory.Key, 3.3),
                        new SearchField(Loc.GetMemoryCategoryDisplay(memory.Category), 1.5)
                    ]);

            if (!evaluation.IsMatch)
                continue;

            var defaultSubtitle = string.IsNullOrWhiteSpace(memory.Category)
                ? TrimForSubtitle(memory.Content)
                : $"[{Loc.GetMemoryCategoryDisplay(memory.Category)}] {TrimForSubtitle(memory.Content)}";

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

            var status = job.IsEnabled ? Loc.Jobs_StateEnabled : Loc.Jobs_StatePaused;
            var next = job.NextRunAt.HasValue ? $" · {string.Format(Loc.Search_JobNext, FormatRelativeTime(job.NextRunAt.Value))}" : "";
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
                    ? Loc.Search_NoChats
                    : $"{string.Format(chatCount == 1 ? Loc.Project_ChatCount : Loc.Project_ChatCounts, chatCount)} · {FormatRelativeTime(sortTimestamp)}",
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
                Subtitle = skill.DisplayDescription,
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
                Subtitle = agent.DisplayDescription,
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
                    : $"[{Loc.GetMemoryCategoryDisplay(memory.Category)}] {TrimForSubtitle(memory.Content)}",
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
            var status = job.IsEnabled ? Loc.Jobs_StateEnabled : Loc.Jobs_StatePaused;
            var next = job.NextRunAt.HasValue ? $" · {string.Format(Loc.Search_JobNext, FormatRelativeTime(job.NextRunAt.Value))}" : "";
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
                    ? Loc.Search_NoChats
                    : $"{string.Format(chatCount == 1 ? Loc.Project_ChatCount : Loc.Project_ChatCounts, chatCount)} · {FormatRelativeTime(sortTimestamp)}",
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
                Subtitle = skill.DisplayDescription,
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
                Subtitle = agent.DisplayDescription,
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
                    : $"[{Loc.GetMemoryCategoryDisplay(memory.Category)}] {TrimForSubtitle(memory.Content)}",
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
            return Loc.Library_Time_JustNow;
        if (age < TimeSpan.FromHours(1))
            return string.Format(Loc.Library_Time_MinutesAgo, Math.Max(1, (int)age.TotalMinutes));
        if (age < TimeSpan.FromDays(1))
            return string.Format(Loc.Library_Time_HoursAgo, Math.Max(1, (int)age.TotalHours));
        if (age < TimeSpan.FromDays(7))
            return string.Format(Loc.Library_Time_DaysAgo, Math.Max(1, (int)age.TotalDays));
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

    private static SearchSnapshot CaptureSnapshot(AppData data, Func<Chat, bool>? chatFilter)
    {
        ArgumentNullException.ThrowIfNull(data);

        var chats = data.Chats.ToArray();
        if (chatFilter is not null)
            chats = Array.FindAll(chats, chat => chatFilter(chat));
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
        GlobalSearchExecutionMode mode,
        Func<ContentLookup>? content)
    {
        public SearchQuery TextQuery { get; } = textQuery;
        public SearchTimeRange? Range { get; } = range;
        public bool HasRecencyIntent { get; } = hasRecencyIntent;
        public GlobalSearchExecutionMode Mode { get; } = mode;
        public bool HasRange => Range.HasValue;

        /// <summary>What the query found in chats' messages; null when they are not searched (Preview).</summary>
        public Func<ContentLookup>? Content { get; } = content;

        /// <summary>True when there is no residual text to match (list by time/recency instead).</summary>
        public bool IsBrowse => TextQuery.IsEmpty;

        public bool InRange(DateTimeOffset timestamp)
            => Range is not { } window || window.Contains(timestamp);
    }

    /// <summary>A chat found by what was said in it: the hit to quote, the text to quote around, and its project.</summary>
    internal sealed record ContentQuote(ChatContentHit Hit, string Text, string? ProjectName);

    /// <summary>
    /// What one query found in chats' messages, looked up once and shared by its search phases. Every
    /// query word must occur in the chat's title or messages, as the text engine matches words across
    /// fields; a word of one or two letters, which the messages cannot be searched for, only in the
    /// title, unless the whole query, long enough to look up, occurs in a message as typed. Messages
    /// are searched for the words as typed (<see cref="ChatSearchText.Words"/>); titles are compared
    /// the text engine's way.
    /// </summary>
    internal sealed class ContentLookup
    {
        private readonly string[] _words;
        private readonly SearchQuery[] _wordQueries;
        private readonly IReadOnlyDictionary<Guid, ChatContentHit>?[] _wordHits;
        private readonly string? _phrase;
        private readonly IReadOnlyDictionary<Guid, ChatContentHit>? _phraseHits;

        public ContentLookup(IChatContentSearch search, SearchQuery query)
        {
            _words = ChatSearchText.Words(query.Original);
            _wordQueries = Array.ConvertAll(_words, static word => SearchQuery.Create(word));
            _wordHits = Array.ConvertAll(_words, word => ChatSearchText.CanLookUp(word)
                ? search.FindChatsContaining(word)
                : null);
            if (Array.Exists(_wordHits, static hits => hits is null) && ChatSearchText.CanLookUp(query.Original))
            {
                _phrase = query.Original;
                _phraseHits = search.FindChatsContaining(query.Original);
            }
        }

        public (double Score, ContentQuote Quote)? Match(Guid chatId, PreparedSearchField title, string? projectName)
        {
            // As the text engine scores words across fields: each word's best field, plus a share for
            // every word and for the words the title holds.
            var total = 0d;
            var inTitle = 0;
            ContentQuote? quote = null;
            for (var index = 0; index < _words.Length; index++)
            {
                var titleScore = SearchEngine.Evaluate(_wordQueries[index], [title]).Score;
                var contentScore = 0d;
                var hit = default(ChatContentHit);
                if (_wordHits[index]?.TryGetValue(chatId, out hit) == true)
                    contentScore = ContentScore(hit);

                if (titleScore <= 0 && contentScore <= 0)
                    return PhraseMatch(chatId, projectName);

                if (titleScore >= contentScore)
                {
                    total += titleScore;
                    inTitle++;
                }
                else
                {
                    total += contentScore;
                    quote ??= new ContentQuote(hit, _words[index], projectName);
                }
            }

            // Every word in the title is a title match, found before the messages are looked at.
            if (quote is null)
                return null;

            if (_words.Length > 1)
                total += _words.Length * 110 + inTitle * 70;
            return (total, quote);
        }

        private (double Score, ContentQuote Quote)? PhraseMatch(Guid chatId, string? projectName)
            => _phraseHits?.TryGetValue(chatId, out var hit) == true
                ? (ContentScore(hit), new ContentQuote(hit, _phrase!, projectName))
                : null;

        private static double ContentScore(ChatContentHit hit)
            => ContentTermScore * (hit.InUserMessage ? UserMessageSearchWeight : AssistantMessageSearchWeight);
    }

    /// <summary>The words around the first place <paramref name="text"/> occurs in <paramref name="message"/>.</summary>
    private static string? BuildSnippet(string? message, string text)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        const int radius = 72;
        var start = Math.Max(0, ChatSearchText.IndexOf(message, text) - radius);
        var length = Math.Min(message.Length - start, radius * 2);
        var snippet = message.Substring(start, length).Trim();
        if (start > 0)
            snippet = "..." + snippet;
        if (start + length < message.Length)
            snippet += "...";
        return snippet;
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

        private readonly object _contentSync = new();
        private ContentLookup? _content;

        /// <summary>What this query finds in chats' messages: looked up by its first phase that searches them.</summary>
        internal ContentLookup GetContent(IChatContentSearch search)
        {
            lock (_contentSync)
                return _content ??= new ContentLookup(search, TextQuery);
        }
    }

    private readonly record struct SearchSettingEntry(string NameKey, string PageKey, int PageIndex)
    {
        public string Name => Loc.Get(NameKey);
        public string Page => Loc.Get(PageKey);
    }
}
