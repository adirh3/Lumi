using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Context;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Lumi.Tests;

/// <summary>
/// Replays follow-up suggestion engines over real chat history and scores them against what the user
/// actually sent next. Every moment where an assistant reply finished and the user then wrote again is a
/// <i>decision point</i>; an engine sees only what existed at that moment (the conversation so far and the
/// prompts sent before it) and is scored by whether one of its chips says what the user went on to type.
///
/// Gated by <c>LUMI_SUGGESTION_EVAL=1</c> (model steps also need <c>LUMI_INTEGRATION_TESTS=1</c>) so it never
/// runs in CI. The profile is opened read-only and only from an explicit path:
/// <list type="bullet">
/// <item><c>LUMI_SUGGESTION_EVAL_PROFILE</c> — a Lumi profile folder (with <c>lumi.db</c> and <c>data.json</c>).</item>
/// <item><c>LUMI_SUGGESTION_EVAL_DIR</c> — where the corpus, runs, judgments and reports go (default: %TEMP%\lumi-suggestion-eval).
/// Use a folder per profile: the corpus and caches in it belong to one profile.</item>
/// <item><c>LUMI_SUGGESTION_EVAL_ENGINES</c> — engines to run (default <c>legacy,current</c>; see <see cref="CreateEngines"/>).</item>
/// <item><c>LUMI_SUGGESTION_JUDGE_MODEL</c> — the judge's model id (required, so every report is graded by a known judge).</item>
/// <item><c>LUMI_SUGGESTION_EVAL_SAMPLE</c>, <c>_NOPROJECT</c>, <c>_FROM</c>, <c>_SEED</c> — how a new sample is drawn.</item>
/// <item><c>LUMI_SUGGESTION_EVAL_SLICE=unfinished</c> — a sample of only replies the work went on after (file
/// <c>sample-unfinished.json</c>, report <c>report-sample-unfinished.md</c>); <c>LUMI_SUGGESTION_EVAL_SAMPLE_FILE</c>
/// names any other sample.</item>
/// <item><c>LUMI_SUGGESTION_EVAL_REPORT_ENGINES</c> — which run files to report (default: all in <c>runs</c>);
/// <c>LUMI_SUGGESTION_EVAL_LIMIT</c> — judge only the first N decision points; <c>LUMI_SUGGESTION_EVAL_WORKERS</c> —
/// parallel model sessions; <c>LUMI_SUGGESTION_EVAL_JUDGMENTS</c> — a judgments file other than the judge's own.</item>
/// </list>
/// Steps: <see cref="BuildCorpus"/>, then <see cref="RunEngines"/>, then <see cref="JudgeAndReport"/>. A run is cached per
/// decision point with a fingerprint of the model and the exact question, so after a change only the decision points
/// whose question changed run again. Judgments are cached per judge (its model and how it is asked). Every file in
/// <c>runs</c> is reported as its own engine: to keep a baseline, or to get a second run of the same engine (two runs
/// differ by about a point on 400 decision points), rename <c>runs/&lt;engine&gt;.jsonl</c> before running it again.
/// </summary>
public class SuggestionEvalHarness
{
    private const int HistoryScanLimit = DataStore.SuggestionHistoryScanLimit;

    /// <summary>Earlier exchanges kept per decision point: more than the engine shows, so that showing more needs no new corpus.</summary>
    private const int CorpusEarlierTurns = 4;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ITestOutputHelper _output;

    public SuggestionEvalHarness(ITestOutputHelper output) => _output = output;

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);
    private static int EnvInt(string name, int fallback) => int.TryParse(Env(name), out var value) ? value : fallback;
    private static string EvalDir => Env("LUMI_SUGGESTION_EVAL_DIR") is { Length: > 0 } dir
        ? dir
        : Path.Combine(Path.GetTempPath(), "lumi-suggestion-eval");

    private static void SkipUnlessEnabled(bool needsModel)
    {
        Skip.If(Env("LUMI_SUGGESTION_EVAL") != "1", "Set LUMI_SUGGESTION_EVAL=1 to replay suggestion engines over real history.");
        if (needsModel)
            Skip.If(Env("LUMI_INTEGRATION_TESTS") != "1", "Set LUMI_INTEGRATION_TESTS=1 to call the real model.");
    }

    // ── Corpus ──────────────────────────────────────────────────────────────

    public sealed record Turn(string User, string Assistant);

    public sealed record DecisionPoint
    {
        public required string Id { get; init; }
        public required Guid ChatId { get; init; }
        public Guid? ProjectId { get; init; }
        public string? ProjectName { get; init; }
        public string Title { get; init; } = "";
        public required Guid UserMessageId { get; init; }
        public required string UserMessage { get; init; }
        public required string AssistantMessage { get; init; }
        public required DateTimeOffset AssistantAt { get; init; }
        /// <summary>The reply was a progress update: the main agent went on calling tools after it (other than a file-change
        /// notice), and the turn ended before it wrote another reply.</summary>
        public bool ReplyUnfinished { get; init; }
        public List<Turn> EarlierTurns { get; init; } = [];
        public required string Next { get; init; }
        public required DateTimeOffset NextAt { get; init; }
    }

    public sealed record PromptRecord(Guid ChatId, Guid MessageId, string Content, DateTimeOffset Timestamp, string? Author, bool Receipt = false);

    public sealed record ChatRecord(Guid Id, Guid? ProjectId, string Title);

    private sealed record Message(string Role, Guid Id, string Content, DateTimeOffset At, string? Author,
        bool IsProgress, bool IsReceipt, string? ToolName = null, bool IsRootTool = false);

    [SkippableFact]
    public void BuildCorpus()
    {
        SkipUnlessEnabled(needsModel: false);
        var profile = Env("LUMI_SUGGESTION_EVAL_PROFILE");
        Skip.If(string.IsNullOrWhiteSpace(profile), "Set LUMI_SUGGESTION_EVAL_PROFILE to a Lumi profile folder (opened read-only).");

        var sw = Stopwatch.StartNew();
        var projects = ReadProjects(Path.Combine(profile!, "data.json"));
        var decisions = new List<DecisionPoint>();
        var prompts = new List<PromptRecord>();
        var chats = new List<ChatRecord>();

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(profile!, "lumi.db"),
            Mode = SqliteOpenMode.ReadOnly,
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);

        var chatRows = new List<(long Key, ChatRecord Chat)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT chat_key, metadata FROM chats;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                using var metadata = JsonDocument.Parse(reader.GetString(1));
                var root = metadata.RootElement;
                if (!root.TryGetProperty("id", out var idElement) || !Guid.TryParse(idElement.GetString(), out var id))
                    continue;
                Guid? projectId = root.TryGetProperty("projectId", out var p) && Guid.TryParse(p.GetString(), out var pid) ? pid : null;
                var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                chatRows.Add((reader.GetInt64(0), new ChatRecord(id, projectId, title)));
            }
        }

        foreach (var (key, chat) in chatRows)
        {
            chats.Add(chat);
            var messages = ReadChatMessages(connection, transaction, key);
            foreach (var message in messages.Where(static m => m.Role == "user" && !string.IsNullOrWhiteSpace(m.Content)))
                prompts.Add(new PromptRecord(chat.Id, message.Id, message.Content, message.At, message.Author, message.IsReceipt));
            decisions.AddRange(ExtractDecisionPoints(chat, chat.ProjectId is { } pid && projects.TryGetValue(pid, out var name) ? name : null, messages));
        }

        Directory.CreateDirectory(EvalDir);
        WriteJsonl(Path.Combine(EvalDir, "corpus.jsonl"), decisions.OrderBy(static d => d.NextAt));
        WriteJsonl(Path.Combine(EvalDir, "prompts.jsonl"), prompts.OrderBy(static p => p.Timestamp));
        WriteJsonl(Path.Combine(EvalDir, "chats.jsonl"), chats);
        _output.WriteLine($"{decisions.Count} decision points, {prompts.Count} prompts, {chats.Count} chats in {sw.Elapsed.TotalSeconds:F1}s → {EvalDir}");
        Assert.NotEmpty(decisions);
    }

    private static Dictionary<Guid, string> ReadProjects(string dataFile)
    {
        var projects = new Dictionary<Guid, string>();
        if (!File.Exists(dataFile))
            return projects;
        using var stream = new FileStream(dataFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("projects", out var list))
            return projects;
        foreach (var project in list.EnumerateArray())
        {
            if (project.TryGetProperty("id", out var id) && Guid.TryParse(id.GetString(), out var guid))
                projects[guid] = project.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        }

        return projects;
    }

    private static List<Message> ReadChatMessages(SqliteConnection connection, SqliteTransaction transaction, long chatKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT role, tool_name, CASE WHEN role IN ('user', 'assistant') THEN body END, parent_tool_call_id IS NULL
            FROM messages WHERE chat_key = $chat AND role IN ('user', 'assistant', 'tool')
            ORDER BY seq;
            """;
        command.Parameters.AddWithValue("$chat", chatKey);
        using var reader = command.ExecuteReader();
        var messages = new List<Message>();
        while (reader.Read())
        {
            var role = reader.GetString(0);
            if (reader.IsDBNull(2))
            {
                messages.Add(new Message(role, Guid.Empty, "", default, null, false, false,
                    reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetBoolean(3)));
                continue;
            }

            using var body = JsonDocument.Parse(reader.GetString(2));
            var root = body.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool Truthy(string name) => root.TryGetProperty(name, out var v)
                && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase)));
            messages.Add(new Message(
                role,
                Guid.TryParse(Text("id"), out var id) ? id : Guid.Empty,
                Text("content") ?? "",
                DateTimeOffset.TryParse(Text("timestamp"), out var at) ? at : default,
                Text("author"),
                Truthy("isProgress"),
                root.TryGetProperty("interactionReceipt", out var receipt) && receipt.ValueKind == JsonValueKind.Object));
        }

        return messages;
    }

    private static IEnumerable<DecisionPoint> ExtractDecisionPoints(ChatRecord chat, string? projectName, List<Message> messages)
    {
        var exchanges = new List<(Message User, List<Message> Replies, bool WorkedOn)>();
        foreach (var message in messages)
        {
            if (message.Role == "user" && !string.IsNullOrWhiteSpace(message.Content))
            {
                exchanges.Add((message, [], false));
                continue;
            }

            if (exchanges.Count == 0)
                continue;
            var current = exchanges[^1];
            if (message.Role == "assistant" && !string.IsNullOrWhiteSpace(message.Content))
            {
                current.Replies.Add(message);
                exchanges[^1] = current with { WorkedOn = false };
            }
            else if (message.Role == "tool" && current.Replies.Count > 0 && message.IsRootTool
                && message.ToolName != ToolDisplayHelper.WorkspaceFileChangedToolName)
            {
                exchanges[^1] = current with { WorkedOn = true };
            }
        }

        for (var i = 0; i + 1 < exchanges.Count; i++)
        {
            var (user, replies, workedOn) = exchanges[i];
            var next = exchanges[i + 1].User;
            if (replies.Count == 0 || next.IsReceipt || ChatDigest.IsAutomatedAuthor(next.Author)
                || next.Content.StartsWith("Background job triggered", StringComparison.OrdinalIgnoreCase))
                continue;

            var reply = replies[^1];
            yield return new DecisionPoint
            {
                Id = $"{chat.Id:N}:{reply.Id:N}",
                ChatId = chat.Id,
                ProjectId = chat.ProjectId,
                ProjectName = projectName,
                Title = chat.Title,
                UserMessageId = user.Id,
                UserMessage = user.Content,
                AssistantMessage = reply.Content,
                AssistantAt = reply.At,
                ReplyUnfinished = reply.IsProgress || workedOn,
                EarlierTurns = exchanges
                    .Take(i)
                    .TakeLast(CorpusEarlierTurns)
                    .Select(static e => new Turn(Clip(e.User.Content, 1500), e.Replies.Count > 0 ? e.Replies[^1].Content : ""))
                    .ToList(),
                Next = next.Content,
                NextAt = next.At,
            };
        }
    }

    private static string Clip(string? text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text ?? "" : text[..max];

    // ── Corpus loading and history replay ───────────────────────────────────

    private sealed class Corpus
    {
        public required List<DecisionPoint> Decisions { get; init; }
        public required List<PromptRecord> Prompts { get; init; } // oldest first
        public required Dictionary<Guid, ChatRecord> Chats { get; init; }
        private DateTimeOffset[]? _promptTimes;

        /// <summary>The prompts sent before <paramref name="at"/>, newest first, at most <paramref name="limit"/> of them.</summary>
        public IEnumerable<PromptRecord> PromptsBefore(DateTimeOffset at, int limit = int.MaxValue)
        {
            _promptTimes ??= Prompts.Select(static p => p.Timestamp).ToArray();
            var end = Array.BinarySearch(_promptTimes, at);
            if (end < 0)
                end = ~end;
            else
                while (end > 0 && _promptTimes[end - 1] >= at) end--;
            for (var i = end - 1; i >= 0 && end - i <= limit; i--)
                yield return Prompts[i];
        }
    }

    private static Corpus LoadCorpus()
    {
        var corpusFile = Path.Combine(EvalDir, "corpus.jsonl");
        Skip.If(!File.Exists(corpusFile), $"Run BuildCorpus first ({corpusFile} is missing).");
        return new Corpus
        {
            Decisions = ReadJsonl<DecisionPoint>(corpusFile),
            Prompts = ReadJsonl<PromptRecord>(Path.Combine(EvalDir, "prompts.jsonl")).OrderBy(static p => p.Timestamp).ToList(),
            Chats = ReadJsonl<ChatRecord>(Path.Combine(EvalDir, "chats.jsonl")).ToDictionary(static c => c.Id),
        };
    }

    /// <summary>The scored decision points: a seeded random sample of recent ones plus extra chats outside projects,
    /// saved once so every engine and judgment works on the same set. <c>LUMI_SUGGESTION_EVAL_SAMPLE_FILE</c> names
    /// another sample; with <c>LUMI_SUGGESTION_EVAL_SLICE=unfinished</c> it holds only replies the assistant left
    /// unfinished, which are too rare in a random sample to measure.</summary>
    private static bool UnfinishedSlice
        => string.Equals(Env("LUMI_SUGGESTION_EVAL_SLICE"), "unfinished", StringComparison.OrdinalIgnoreCase);

    private static string SampleFile
        => Env("LUMI_SUGGESTION_EVAL_SAMPLE_FILE") ?? (UnfinishedSlice ? "sample-unfinished.json" : "sample.json");

    private static List<DecisionPoint> LoadOrCreateSample(Corpus corpus)
    {
        var sampleFile = Path.Combine(EvalDir, SampleFile);
        var byId = corpus.Decisions.ToDictionary(static d => d.Id);
        if (File.Exists(sampleFile))
        {
            var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(sampleFile), Json) ?? [];
            return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }

        var from = DateTimeOffset.TryParse(Env("LUMI_SUGGESTION_EVAL_FROM"), out var parsed) ? parsed : DateTimeOffset.Now.AddDays(-60);
        var random = new Random(EnvInt("LUMI_SUGGESTION_EVAL_SEED", 42));
        var unfinishedOnly = UnfinishedSlice;
        var recent = corpus.Decisions
            .Where(d => d.NextAt >= from && (!unfinishedOnly || d.ReplyUnfinished))
            .OrderBy(_ => random.Next())
            .ToList();
        var sample = recent.Take(EnvInt("LUMI_SUGGESTION_EVAL_SAMPLE", 300)).ToList();
        if (unfinishedOnly)
        {
            File.WriteAllText(sampleFile, JsonSerializer.Serialize(sample.Select(static d => d.Id).ToList(), Json));
            return sample;
        }

        var taken = sample.Select(static d => d.Id).ToHashSet();
        sample.AddRange(corpus.Decisions
            .Where(d => d.ProjectId is null && d.NextAt >= from.AddDays(-120) && !taken.Contains(d.Id))
            .OrderBy(_ => random.Next())
            .Take(EnvInt("LUMI_SUGGESTION_EVAL_NOPROJECT", 100)));
        File.WriteAllText(sampleFile, JsonSerializer.Serialize(sample.Select(static d => d.Id).ToList(), Json));
        return sample;
    }

    // ── Engines ─────────────────────────────────────────────────────────────

    /// <summary>One way to produce chips for a decision point. <see cref="SystemPrompt"/> is null for engines that
    /// need no model; otherwise <see cref="Question"/> is sent as an ephemeral query on a session with that prompt.</summary>
    private sealed record Engine(
        string Name,
        string? SystemPrompt,
        Func<DecisionPoint, Corpus, string>? Question,
        Func<string?, DecisionPoint, Corpus, IReadOnlyList<string>> Finish);

    private static IReadOnlyDictionary<string, Engine> Engines { get; } = CreateEngines();

    private static Dictionary<string, Engine> CreateEngines()
    {
        var engines = new Dictionary<string, Engine>(StringComparer.OrdinalIgnoreCase);
        void Add(Engine engine) => engines[engine.Name] = engine;

        // The pipeline as it shipped before this evaluation: first 1,400 characters of the reply, the latest
        // prompt, and the global frequent-request list as low-priority reference.
        Add(new Engine(
            "legacy",
            LegacySuggestionPrompt.SystemPrompt,
            static (d, corpus) => LegacySuggestionPrompt.BuildQuestion(
                d.AssistantMessage,
                d.UserMessage,
                LegacySuggestionPrompt.BuildFrequentRequestsBlock(corpus.PromptsBefore(d.AssistantAt, LegacySuggestionPrompt.HistoryScanLimit)
                    .Where(p => p.MessageId != d.UserMessageId)
                    .Select(static p => new UserPromptHistoryItem(p.ChatId, p.MessageId, p.Content, p.Timestamp)))),
            static (raw, _, _) => LegacySuggestionPrompt.Parse(raw)));

        // The user's own recurring requests alone, ranked for the chat and project: what history can do without a model.
        Add(new Engine(
            "history-only",
            null,
            null,
            static (_, d, corpus) => UsualReplies(d, corpus).Take(FollowUpSuggestions.MaxSuggestions).Select(static r => r.Text).ToList()));

        // The production engine with parts of its context left out, to measure what each part is worth.
        AddProduction("current-lean", earlier: false, usual: false, project: false);
        AddProduction("current-ctx", earlier: true, usual: false, project: true);
        AddProduction("current", earlier: true, usual: true, project: true);

        return engines;

        void AddProduction(string name, bool earlier, bool usual, bool project)
            => Add(new Engine(
                name,
                FollowUpSuggestions.SystemPrompt,
                (d, corpus) => FollowUpSuggestions.BuildQuestion(Context(d, corpus, earlier, usual, project)),
                static (raw, _, _) => FollowUpSuggestions.Parse(raw) ?? []));
    }

    /// <summary>The context <c>ChatViewModel</c> would have built at this decision point, from what existed then.</summary>
    private static FollowUpSuggestionContext Context(DecisionPoint d, Corpus corpus, bool earlier, bool usual, bool project = true)
        => new()
        {
            AssistantMessage = d.AssistantMessage,
            UserMessage = d.UserMessage,
            ProjectName = project ? d.ProjectName : null,
            EarlierTurns = earlier ? d.EarlierTurns.Select(static t => new FollowUpTurn(t.User, t.Assistant)).ToList() : [],
            UsualReplies = usual ? UsualReplies(d, corpus) : [],
        };

    /// <summary>The usual replies as the app ranks them then: from the newest prompts the user typed (the store leaves out
    /// jobs, managers and receipts before taking them), without the message the reply answered.</summary>
    private static IReadOnlyList<SuggestionHistoryRanker.UsualReply> UsualReplies(DecisionPoint d, Corpus corpus)
        => SuggestionHistoryRanker.RankUsualReplies(
            corpus.PromptsBefore(d.AssistantAt)
                .Where(static p => !ChatDigest.IsAutomatedAuthor(p.Author) && !p.Receipt)
                .Take(HistoryScanLimit)
                .Where(p => SuggestionHistoryRanker.IsCandidate(p.Content) && p.MessageId != d.UserMessageId)
                .Select(static p => new UserPromptHistoryItem(p.ChatId, p.MessageId, p.Content, p.Timestamp)),
            new SuggestionHistoryRanker.Scope(
                d.ChatId,
                d.ProjectId,
                chatId => corpus.Chats.TryGetValue(chatId, out var chat) ? chat.ProjectId : null,
                d.AssistantAt),
            FollowUpSuggestions.UsualRepliesShown);

    // ── Running engines ─────────────────────────────────────────────────────

    /// <summary>One engine's chips for one decision point. <see cref="Fingerprint"/> identifies what produced them (the model
    /// and the exact question), so a cached run is reused only while both are unchanged.</summary>
    public sealed record RunRecord(string Id, List<string> Suggestions, long Ms, string? Error = null, string? Raw = null,
        string? Fingerprint = null);

    [SkippableFact]
    public async Task RunEngines()
    {
        SkipUnlessEnabled(needsModel: true);
        var corpus = LoadCorpus();
        var sample = LoadOrCreateSample(corpus);
        var names = (Env("LUMI_SUGGESTION_EVAL_ENGINES") ?? "legacy,current")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var workers = Math.Max(1, EnvInt("LUMI_SUGGESTION_EVAL_WORKERS", 6));
        Directory.CreateDirectory(Path.Combine(EvalDir, "runs"));
        Assert.True(FollowUpSuggestions.EarlierTurnsShown <= CorpusEarlierTurns,
            $"The corpus keeps {CorpusEarlierTurns} earlier exchanges; raise {nameof(CorpusEarlierTurns)} and run BuildCorpus again.");

        await using var service = new CopilotService();
        await service.ConnectAsync();
        var model = await service.GetBackgroundModelIdAsync(AiFeatureIds.FollowUps);
        _output.WriteLine($"Suggestion model: {model}");

        foreach (var name in names)
        {
            Assert.True(Engines.TryGetValue(name, out var engine), $"Unknown engine '{name}'. Known: {string.Join(", ", Engines.Keys)}");
            var runFile = Path.Combine(EvalDir, "runs", $"{engine!.Name}.jsonl");
            // An engine without a model is instant, so it always runs; a model engine reuses what the same model
            // answered to the same question.
            var done = engine.SystemPrompt is not null && File.Exists(runFile)
                ? ReadJsonl<RunRecord>(runFile).Where(static r => r.Error is null && r.Fingerprint is not null)
                    .Select(static r => (r.Id, r.Fingerprint!)).ToHashSet()
                : [];
            var questions = sample.Select(d =>
            {
                var question = engine.Question?.Invoke(d, corpus);
                return (Decision: d, Question: question, Fingerprint: question is null ? null : Hash($"{model}\n{engine.SystemPrompt}\n{question}"));
            }).ToList();
            var pending = new ConcurrentQueue<(DecisionPoint Decision, string? Question, string? Fingerprint)>(
                questions.Where(q => q.Fingerprint is null || !done.Contains((q.Decision.Id, q.Fingerprint))));
            var reused = questions.Count - pending.Count;
            var writeLock = new object();
            var sw = Stopwatch.StartNew();
            var completed = 0;

            async Task Work()
            {
                CopilotSession? session = null;
                try
                {
                    while (pending.TryDequeue(out var item))
                    {
                        var (decision, question, fingerprint) = item;
                        var started = Stopwatch.StartNew();
                        RunRecord record;
                        try
                        {
                            string? raw = null;
                            if (question is not null)
                            {
                                session ??= await service.CreateSessionAsync(await service.BuildLightweightSessionConfigAsync(
                                    new LightweightSessionOptions { SystemPrompt = engine.SystemPrompt!, Model = model, Feature = AiFeatureIds.FollowUps }));
                                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                                raw = (await session.Rpc.Ui.EphemeralQueryAsync(question, timeout.Token)).Answer;
                            }

                            record = new RunRecord(decision.Id, [.. engine.Finish(raw, decision, corpus)], started.ElapsedMilliseconds,
                                Raw: raw, Fingerprint: fingerprint);
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            record = new RunRecord(decision.Id, [], started.ElapsedMilliseconds, ex.GetType().Name + ": " + ex.Message);
                            if (session is not null)
                            {
                                try { await session.DisposeAsync(); } catch { }
                                session = null;
                            }
                        }

                        lock (writeLock)
                        {
                            File.AppendAllText(runFile, JsonSerializer.Serialize(record, Json) + "\n");
                            completed++;
                        }
                    }
                }
                finally
                {
                    if (session is not null)
                    {
                        try { await session.DisposeAsync(); } catch { }
                    }
                }
            }

            await Task.WhenAll(Enumerable.Range(0, engine.SystemPrompt is null ? 1 : workers).Select(_ => Task.Run(Work)));
            var errors = File.Exists(runFile) ? ReadJsonl<RunRecord>(runFile).Count(static r => r.Error is not null) : 0;
            _output.WriteLine($"{engine.Name}: {completed} decision points run in {sw.Elapsed.TotalSeconds:F0}s, {reused} unchanged ({errors} errors recorded)");
        }
    }

    // ── Judging ─────────────────────────────────────────────────────────────

    public sealed record Judgment(string Id, string Suggestion, string Match, bool Sensible);

    private const string JudgeInstructions = """
        You grade suggestion chips in a chat app. After the assistant's reply, the app showed chips the user could click to send instead of typing. You know what the user ACTUALLY sent next.

        For each chip decide:
        - match: "yes" if sending the chip would have asked the assistant for the same thing as the actual message. Wording, politeness and small extra details that would not change what the assistant does don't matter. A chip that names the offered action ("Fix the failing tests") matches an actual reply that just accepts the assistant's offer ("yes", "do it"). "partial" if it points the same way but misses or changes something the actual message clearly asked for (a different target, branch, scope or step). "no" otherwise.
        - sensible: true if the chip is a reasonable thing for this user to say right after this reply even if it is not what they sent; false if it is unrelated to the conversation, re-asks what the reply already answered, or makes no sense here.

        Return ONLY a JSON array with one object per chip, in order: [{"chip":1,"match":"yes|partial|no","sensible":true}]
        """;

    /// <summary>The judge's identity besides its model: exactly how it is asked (instructions, layout and where texts are
    /// cut), taken from a fixed probe long enough to show every cut. Changing any of it starts a new judgments file.</summary>
    private static readonly string JudgeFingerprint = Hash(BuildJudgeQuestion(new DecisionPoint
    {
        Id = "probe",
        ChatId = Guid.Empty,
        UserMessageId = Guid.Empty,
        UserMessage = new string('u', 4000),
        AssistantMessage = new string('a', 8000),
        AssistantAt = default,
        Next = new string('n', 4000),
        NextAt = default,
    }, ["chip"]))[..8];

    [SkippableFact]
    public async Task JudgeAndReport()
    {
        SkipUnlessEnabled(needsModel: true);
        var judgeModel = Env("LUMI_SUGGESTION_JUDGE_MODEL");
        Skip.If(string.IsNullOrWhiteSpace(judgeModel), "Set LUMI_SUGGESTION_JUDGE_MODEL to the judge's model id (e.g. gpt-6.1-terra).");
        var corpus = LoadCorpus();
        var sample = LoadOrCreateSample(corpus);
        if (EnvInt("LUMI_SUGGESTION_EVAL_LIMIT", 0) is > 0 and var limit)
            sample = sample.Take(limit).ToList();
        var runs = LoadRuns();
        var judge = $"{string.Concat(judgeModel!.Select(static c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c))}-{JudgeFingerprint}";
        var judgmentsFile = Path.Combine(EvalDir, Env("LUMI_SUGGESTION_EVAL_JUDGMENTS") ?? $"judgments-{judge}.jsonl");
        var judged = File.Exists(judgmentsFile)
            ? ReadJsonl<Judgment>(judgmentsFile).GroupBy(static j => (j.Id, Key(j.Suggestion))).ToDictionary(static g => g.Key, static g => g.Last())
            : [];

        var pending = new ConcurrentQueue<(DecisionPoint Decision, List<string> Chips)>(sample
            .Select(d => (d, runs.Values
                .SelectMany(r => r.TryGetValue(d.Id, out var run) ? run.Suggestions : [])
                .DistinctBy(Key)
                .Where(s => !judged.ContainsKey((d.Id, Key(s))))
                .ToList()))
            .Where(static x => x.Item2.Count > 0));
        _output.WriteLine($"Judging {pending.Count} decision points…");

        if (!pending.IsEmpty)
        {
            await using var service = new CopilotService();
            await service.ConnectAsync();
            var writeLock = new object();

            async Task Work()
            {
                CopilotSession? session = null;
                try
                {
                    while (pending.TryDequeue(out var item))
                    {
                        for (var attempt = 0; attempt < 3; attempt++)
                        {
                            try
                            {
                                session ??= await service.CreateSessionAsync(await service.BuildLightweightSessionConfigAsync(
                                    new LightweightSessionOptions { SystemPrompt = JudgeInstructions, Model = judgeModel }));
                                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                                var answer = (await session.Rpc.Ui.EphemeralQueryAsync(BuildJudgeQuestion(item.Decision, item.Chips), timeout.Token)).Answer;
                                var labels = ParseJudgment(answer, item.Chips.Count);
                                if (labels is null)
                                    continue;
                                lock (writeLock)
                                {
                                    for (var i = 0; i < item.Chips.Count; i++)
                                    {
                                        var judgment = new Judgment(item.Decision.Id, item.Chips[i], labels[i].Match, labels[i].Sensible);
                                        judged[(judgment.Id, Key(judgment.Suggestion))] = judgment;
                                        File.AppendAllText(judgmentsFile, JsonSerializer.Serialize(judgment, Json) + "\n");
                                    }
                                }

                                break;
                            }
                            catch (Exception ex) when (ex is not OutOfMemoryException)
                            {
                                Debug.WriteLine($"[eval] judge failed: {ex.Message}");
                                if (session is not null)
                                {
                                    try { await session.DisposeAsync(); } catch { }
                                    session = null;
                                }
                            }
                        }
                    }
                }
                finally
                {
                    if (session is not null)
                    {
                        try { await session.DisposeAsync(); } catch { }
                    }
                }
            }

            await Task.WhenAll(Enumerable.Range(0, Math.Max(1, EnvInt("LUMI_SUGGESTION_EVAL_WORKERS", 6))).Select(_ => Task.Run(Work)));
        }

        var report = BuildReport(sample, runs, judged, judge);
        var sampleName = Path.GetFileNameWithoutExtension(SampleFile);
        var reportFile = Path.Combine(EvalDir, sampleName == "sample" ? "report.md" : $"report-{sampleName}.md");
        File.WriteAllText(reportFile, report);
        _output.WriteLine(report);
        _output.WriteLine($"REPORT: {reportFile}");
    }

    private static string BuildJudgeQuestion(DecisionPoint decision, IReadOnlyList<string> chips)
    {
        var reply = decision.AssistantMessage.Trim();
        var builder = new StringBuilder(JudgeInstructions).AppendLine().AppendLine();
        builder.AppendLine("The user's previous message:").AppendLine(Clip(decision.UserMessage.Trim(), 400)).AppendLine();
        builder.AppendLine("The assistant's reply (its ending):").AppendLine(reply.Length > 1600 ? "…" + reply[^1600..] : reply).AppendLine();
        builder.AppendLine("What the user ACTUALLY sent next:").AppendLine(Clip(decision.Next.Trim(), 700)).AppendLine();
        builder.AppendLine("Chips:");
        for (var i = 0; i < chips.Count; i++)
            builder.Append(i + 1).Append(". ").AppendLine(chips[i]);
        return builder.ToString();
    }

    private static List<(string Match, bool Sensible)>? ParseJudgment(string? raw, int count)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start)
            return null;
        try
        {
            using var document = JsonDocument.Parse(raw[start..(end + 1)]);
            var labels = new (string, bool)[count];
            var seen = 0;
            var index = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var chip = element.TryGetProperty("chip", out var c) && c.TryGetInt32(out var n) ? n - 1 : index;
                index++;
                if (chip < 0 || chip >= count)
                    continue;
                var match = element.TryGetProperty("match", out var m) ? (m.GetString() ?? "no").Trim().ToLowerInvariant() : "no";
                var sensible = element.TryGetProperty("sensible", out var s) && s.ValueKind == JsonValueKind.True;
                labels[chip] = (match is "yes" or "partial" ? match : "no", sensible);
                seen++;
            }

            return seen == count ? labels.ToList() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Report ──────────────────────────────────────────────────────────────

    private static Dictionary<string, Dictionary<string, RunRecord>> LoadRuns()
    {
        var folder = Path.Combine(EvalDir, "runs");
        var only = Env("LUMI_SUGGESTION_EVAL_REPORT_ENGINES")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return !Directory.Exists(folder)
            ? []
            : Directory.EnumerateFiles(folder, "*.jsonl")
                .Where(file => only is null || only.Contains(Path.GetFileNameWithoutExtension(file), StringComparer.OrdinalIgnoreCase))
                .ToDictionary(
                    static file => Path.GetFileNameWithoutExtension(file),
                    static file => ReadJsonl<RunRecord>(file).GroupBy(static r => r.Id).ToDictionary(static g => g.Key, static g => g.Last()));
    }

    private static readonly Regex OfferPattern = new(
        @"(want me to|should i\b|shall i\b|would you like|do you want me|if you(?:'d)? (?:want|like)|let me know if|i can (?:also|then|now)|ready to|happy to|say the word|or should i)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HashLike = new(@"\b[0-9a-f]{7,40}\b", RegexOptions.Compiled);

    private static readonly Regex LumiBlock = new(
        @"```(card|confidence|chart|comparison|sources|form|checklist|results|calculator|quiz)\b", RegexOptions.Compiled);

    private static string BuildReport(
        List<DecisionPoint> sample,
        Dictionary<string, Dictionary<string, RunRecord>> runs,
        Dictionary<(string, string), Judgment> judged,
        string judge)
    {
        var slices = new (string Name, Func<DecisionPoint, bool> Filter)[]
        {
            ("all", static _ => true),
            ("in a project", static d => d.ProjectId is not null),
            ("outside projects", static d => d.ProjectId is null),
            ("reply ends with an offer", d => OfferPattern.IsMatch(Tail(d.AssistantMessage, 400))),
            ("reply unfinished (work went on after it)", static d => d.ReplyUnfinished),
            ("reply shows Lumi blocks (cards, charts, forms…)", d => LumiBlock.IsMatch(d.AssistantMessage)),
            ("next message ≤ 60 chars", static d => d.Next.Trim().Length <= 60),
            ("next message > 60 chars", static d => d.Next.Trim().Length > 60),
        };

        var builder = new StringBuilder();
        builder.AppendLine($"# Suggestion engines on real history — {DateTime.Now:yyyy-MM-dd HH:mm}");
        builder.AppendLine();
        builder.AppendLine($"Decision points: {sample.Count} ({SampleFile}). Judge: {judge}. Hit = one of the engine's chips says what the user actually sent next. Soft hit counts partial matches too. Sensible = share of chips that are a reasonable thing to say there.");
        builder.AppendLine();
        foreach (var (sliceName, filter) in slices)
        {
            var points = sample.Where(filter).ToList();
            if (points.Count == 0)
                continue;
            builder.AppendLine($"## {sliceName} (n={points.Count})");
            builder.AppendLine();
            builder.AppendLine("| engine | scored | hit@1 | hit@3 | soft@3 | chips/pt | sensible | hash-like chips | avg len |");
            builder.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var (engine, records) in runs.OrderBy(static r => r.Key))
            {
                int scored = 0, hit1 = 0, hit3 = 0, soft3 = 0, chips = 0, sensible = 0, labelled = 0, hashes = 0;
                long length = 0;
                foreach (var point in points)
                {
                    if (!records.TryGetValue(point.Id, out var run) || run.Error is not null)
                        continue;
                    var labels = run.Suggestions.Select(s => judged.TryGetValue((point.Id, Key(s)), out var j) ? j : null).ToList();
                    if (labels.Any(static l => l is null))
                        continue;
                    scored++;
                    chips += run.Suggestions.Count;
                    hit1 += labels.Count > 0 && labels[0]!.Match == "yes" ? 1 : 0;
                    hit3 += labels.Any(static l => l!.Match == "yes") ? 1 : 0;
                    soft3 += labels.Any(static l => l!.Match is "yes" or "partial") ? 1 : 0;
                    sensible += labels.Count(static l => l!.Sensible);
                    labelled += labels.Count;
                    hashes += run.Suggestions.Count(s => HashLike.IsMatch(s));
                    length += run.Suggestions.Sum(static s => (long)s.Length);
                }

                if (scored == 0)
                    continue;
                builder.AppendLine(
                    $"| {engine} | {scored} | {Pct(hit1, scored)} | {Pct(hit3, scored)} | {Pct(soft3, scored)} | {chips / (double)scored:F1} | " +
                    $"{Pct(sensible, Math.Max(1, labelled))} | {hashes} | {(chips == 0 ? 0 : length / chips)} |");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string Pct(int value, int total) => total == 0 ? "–" : $"{100.0 * value / total:F1}%";

    private static string Tail(string text, int length)
    {
        var trimmed = text.TrimEnd();
        return trimmed.Length <= length ? trimmed : trimmed[^length..];
    }

    private static string Key(string suggestion)
        => Regex.Replace(suggestion.Trim().ToLowerInvariant(), @"\s+", " ");

    // ── Model probe ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ListModels()
    {
        SkipUnlessEnabled(needsModel: true);
        await using var service = new CopilotService();
        await service.ConnectAsync();
        foreach (var model in (await service.GetModelsAsync()).OrderBy(static m => m.Id))
        {
            _output.WriteLine($"{model.Id,-36} multiplier={CopilotService.GetModelCostMultiplier(model)?.ToString() ?? "-",-6} " +
                              $"tokenCost={CopilotService.GetModelTokenCost(model)?.ToString("F4") ?? "-",-8} enabled={CopilotService.IsModelEnabled(model)}");
        }

        _output.WriteLine($"Background (Auto) model: {await service.GetBackgroundModelIdAsync(AiFeatureIds.FollowUps)}");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    private static void WriteJsonl<T>(string path, IEnumerable<T> items)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        foreach (var item in items)
            writer.WriteLine(JsonSerializer.Serialize(item, Json));
    }

    private static List<T> ReadJsonl<T>(string path)
        => File.ReadLines(path)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<T>(line, Json)!)
            .ToList();
}
