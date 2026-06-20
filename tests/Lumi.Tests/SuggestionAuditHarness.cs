using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Xunit;
using Xunit.Abstractions;

namespace Lumi.Tests;

/// <summary>
/// READ-ONLY audit harness for the follow-up-suggestion pipeline, run against the user's real chat
/// corpus so behaviour is validated on real data rather than synthetic fixtures. Never writes to the
/// real data (shared read access only). Gated behind LUMI_SUGGESTION_AUDIT=1 so it never runs in CI.
///
///   AuditFrequentRequestList         — deterministic: prints the cleaned global "frequent requests"
///                                       list the model would receive. No model calls.
///   ValidateSuggestionsWithRealModel — also needs LUMI_INTEGRATION_TESTS=1: drives the REAL model
///                                       across representative real chats and measures leak rate.
/// </summary>
public class SuggestionAuditHarness
{
    private const int ScanLimit = 1000;                     // matches ChatViewModel.SuggestionHistoryScanLimit
    private const int FrequentRequestMaxItems = 8;          // matches ChatViewModel.SuggestionFrequentRequestMaxItems

    private static readonly string LumiRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumi");

    private static readonly Regex CodingTerms = new(
        @"\b(commit|push to main|push|git|pull request|pr|deploy|rebase|refactor|merge|branch|code review|unit test)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ITestOutputHelper _output;

    public SuggestionAuditHarness(ITestOutputHelper output) => _output = output;

    private sealed record ChatMeta(Guid Id, string Title);

    private sealed record ChatContext(Guid ChatId, string Title, string? LastUser, Guid? LastUserId, string Assistant, string Excerpt);

    private sealed record QualityScore(int Relevance, int Grounded, int Actionable, int Diversity, bool Redundant, string Verdict, bool Ok);

    // ───────────────────────────────────────────────────────────────────────────
    // Deterministic: what clean global list does the ranker hand to the model?
    // ───────────────────────────────────────────────────────────────────────────
    [SkippableFact]
    public void AuditFrequentRequestList()
    {
        Skip.If(Environment.GetEnvironmentVariable("LUMI_SUGGESTION_AUDIT") != "1",
            "Set LUMI_SUGGESTION_AUDIT=1 to run against real chat data.");

        var (pool, contexts) = LoadCorpus();
        _output.WriteLine($"Global user prompts: {pool.Count(all: true)}, pool (top {ScanLimit}): {pool.Top.Count}, eligible chats: {contexts.Count}");
        _output.WriteLine("");

        var ranked = SuggestionHistoryRanker.RankFrequentRequests(pool.Top, FrequentRequestMaxItems);
        _output.WriteLine($"=== GLOBAL FREQUENT-REQUESTS BLOCK the model receives (top {FrequentRequestMaxItems}) ===");
        foreach (var r in ranked)
            _output.WriteLine($"  [{r.Count,4}x] {r.Content}");

        _output.WriteLine("");
        _output.WriteLine("=== Next 20 candidates (for inspection) ===");
        foreach (var r in SuggestionHistoryRanker.RankFrequentRequests(pool.Top, 28).Skip(FrequentRequestMaxItems))
            _output.WriteLine($"  [{r.Count,4}x] {r.Content}");

        Assert.NotEmpty(ranked);
        Assert.True(ranked.Count <= FrequentRequestMaxItems);
        Assert.DoesNotContain(ranked, r => r.Content.StartsWith("Background job triggered", StringComparison.OrdinalIgnoreCase));
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Real model: does the global list leak into unrelated chats?
    // ───────────────────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task ValidateSuggestionsWithRealModel()
    {
        Skip.If(Environment.GetEnvironmentVariable("LUMI_SUGGESTION_AUDIT") != "1",
            "Set LUMI_SUGGESTION_AUDIT=1 to run against real chat data.");
        Skip.If(Environment.GetEnvironmentVariable("LUMI_INTEGRATION_TESTS") != "1",
            "Set LUMI_INTEGRATION_TESTS=1 to run the real model.");

        var (pool, contexts) = LoadCorpus();
        var block = SuggestionHistoryRanker.BuildFrequentRequestsBlock(pool.Top, FrequentRequestMaxItems);
        _output.WriteLine("=== GLOBAL FREQUENT-REQUESTS BLOCK ===");
        _output.WriteLine(block ?? "(null)");
        _output.WriteLine("");

        var sample = SelectRepresentative(contexts);
        _output.WriteLine($"Selected {sample.Count} representative chats.\n");

        await using var service = new CopilotService();
        await service.ConnectAsync();

        var leaks = 0;
        var nonCoding = 0;
        foreach (var ctx in sample)
        {
            var suggestions = await service.GenerateSuggestionsAsync(ctx.Assistant, ctx.LastUser, block);
            var joined = string.Join(" | ", suggestions ?? new List<string>());

            var chatIsCoding = CodingTerms.IsMatch($"{ctx.Title} {ctx.LastUser} {ctx.Assistant}");
            var suggestionLeaksCode = suggestions is not null && suggestions.Any(s => CodingTerms.IsMatch(s));
            var leaked = !chatIsCoding && suggestionLeaksCode;
            if (!chatIsCoding) nonCoding++;
            if (leaked) leaks++;

            _output.WriteLine($"[{(chatIsCoding ? "CODE" : "    ")}{(leaked ? " LEAK!" : "")}] {Clip(ctx.Title, 60)}");
            _output.WriteLine($"   user: {Clip(ctx.LastUser, 110)}");
            _output.WriteLine($"   sugg: {joined}");
            _output.WriteLine("");
        }

        _output.WriteLine($"=== Leak rate: {leaks}/{nonCoding} non-coding chats leaked coding suggestions ===");
        Assert.True(leaks == 0, $"{leaks} non-coding chats leaked coding suggestions (see output).");
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Real model: are the suggestions actually GOOD on nuanced, non-obvious chats?
    // Generates with AND without the frequent block, scores with an LLM judge, and
    // dumps full conversation context to a temp report for human assessment.
    // ───────────────────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task EvaluateSuggestionQuality()
    {
        Skip.If(Environment.GetEnvironmentVariable("LUMI_SUGGESTION_AUDIT") != "1",
            "Set LUMI_SUGGESTION_AUDIT=1 to run against real chat data.");
        Skip.If(Environment.GetEnvironmentVariable("LUMI_INTEGRATION_TESTS") != "1",
            "Set LUMI_INTEGRATION_TESTS=1 to run the real model.");

        var (pool, contexts) = LoadCorpus();
        var block = SuggestionHistoryRanker.BuildFrequentRequestsBlock(pool.Top, FrequentRequestMaxItems);

        var sample = SelectDiverseNuanced(contexts);
        _output.WriteLine($"Selected {sample.Count} diverse, non-trivial chats for quality eval.\n");

        await using var service = new CopilotService();
        await service.ConnectAsync();
        var judgeModel = await service.GetFastestModelIdAsync();

        var report = new StringBuilder();
        report.AppendLine($"# Suggestion quality eval — {DateTime.Now:yyyy-MM-dd HH:mm}");
        report.AppendLine();
        report.AppendLine("## Global frequent-requests block sent to the model");
        report.AppendLine("```");
        report.AppendLine(block ?? "(null)");
        report.AppendLine("```");
        report.AppendLine();

        var scores = new List<QualityScore>();
        var n = 0;
        foreach (var ctx in sample)
        {
            n++;
            var withBlock = await SafeGenerateAsync(service, ctx, block);
            var noBlock = await SafeGenerateAsync(service, ctx, null);
            var judged = await JudgeAsync(service, judgeModel, ctx.Excerpt, withBlock);
            if (judged.Ok && withBlock.Count > 0) scores.Add(judged);

            report.AppendLine($"## {n}. {Clip(ctx.Title, 80)}");
            report.AppendLine();
            report.AppendLine("**Conversation (tail):**");
            report.AppendLine("```");
            report.AppendLine(ctx.Excerpt);
            report.AppendLine("```");
            report.AppendLine($"**Suggestions (WITH block):** {Render(withBlock)}");
            report.AppendLine();
            report.AppendLine($"**Suggestions (WITHOUT block):** {Render(noBlock)}");
            report.AppendLine();
            report.AppendLine($"**Judge (WITH):** relevance={judged.Relevance} grounded={judged.Grounded} " +
                              $"actionable={judged.Actionable} diversity={judged.Diversity} redundant={judged.Redundant} — _{judged.Verdict}_");
            report.AppendLine();
            report.AppendLine("---");
            report.AppendLine();

            _output.WriteLine($"[{n,2}] r{judged.Relevance} g{judged.Grounded} a{judged.Actionable} d{judged.Diversity}" +
                              $"{(judged.Redundant ? " REDUNDANT" : "")} | {Clip(ctx.Title, 48)}");
            _output.WriteLine($"      WITH:    {Render(withBlock)}");
            _output.WriteLine($"      WITHOUT: {Render(noBlock)}");
        }

        // Aggregate
        double Avg(Func<QualityScore, int> f) => scores.Count == 0 ? 0 : scores.Average(f);
        var avgRel = Avg(s => s.Relevance);
        var avgGrd = Avg(s => s.Grounded);
        var avgAct = Avg(s => s.Actionable);
        var avgDiv = Avg(s => s.Diversity);
        var redundantRate = scores.Count == 0 ? 0 : (double)scores.Count(s => s.Redundant) / scores.Count;

        var summary =
            $"=== QUALITY (n={scores.Count}) avg relevance={avgRel:F2} grounded={avgGrd:F2} " +
            $"actionable={avgAct:F2} diversity={avgDiv:F2} redundantRate={redundantRate:P0} ===";
        report.Insert(0, summary + "\n\n");
        _output.WriteLine("");
        _output.WriteLine(summary);

        var dir = Path.Combine(Path.GetTempPath(), "lumi-suggestion-quality");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"report-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        File.WriteAllText(path, report.ToString());
        _output.WriteLine($"REPORT: {path}");

        Assert.NotEmpty(scores);
    }

    private static string Render(IReadOnlyList<string>? suggestions)
        => suggestions is null || suggestions.Count == 0 ? "(none)" : string.Join("  |  ", suggestions);

    private static async Task<IReadOnlyList<string>> SafeGenerateAsync(CopilotService service, ChatContext ctx, string? block)
    {
        try
        {
            var s = await service.GenerateSuggestionsAsync(ctx.Assistant, ctx.LastUser, block);
            return s ?? new List<string>();
        }
        catch (Exception ex)
        {
            return new List<string> { $"(generation error: {ex.GetType().Name})" };
        }
    }

    private const string JudgePrompt =
        "You are a strict evaluator of follow-up chat suggestions. You receive a conversation excerpt ending with the " +
        "assistant's latest reply, plus three SUGGESTED next user messages. Judge the three suggestions as a set for how " +
        "well they help the user continue THIS specific conversation. Return ONLY a JSON object, no prose: " +
        "{\"relevance\":1-5,\"grounded\":1-5,\"actionable\":1-5,\"diversity\":1-5,\"redundant\":true|false,\"verdict\":\"<=14 words\"}. " +
        "relevance: do they fit naturally as the user's next message here? grounded: are they specific to the actual content " +
        "discussed (names, topics, details) rather than generic filler? actionable: would a real user plausibly send at least " +
        "two of them? diversity: are the three meaningfully different from each other? redundant: true if any suggestion merely " +
        "re-asks something the assistant already fully answered. 1=terrible, 3=mediocre, 5=excellent. Be critical.";

    private static async Task<QualityScore> JudgeAsync(
        CopilotService service, string? model, string excerpt, IReadOnlyList<string> suggestions)
    {
        if (suggestions.Count == 0 || suggestions[0].StartsWith("(generation error", StringComparison.Ordinal))
            return new QualityScore(0, 0, 0, 0, false, "(no suggestions to judge)", false);

        var numbered = string.Join("\n", suggestions.Select((s, i) => $"{i + 1}. {s}"));
        var prompt = $"""
            CONVERSATION EXCERPT (ends with the assistant's latest reply):
            {excerpt}

            SUGGESTED NEXT USER MESSAGES:
            {numbered}

            Score them now as a single JSON object.
            """;
        try
        {
            var raw = await service.UseLightweightSessionAsync(
                new LightweightSessionOptions { SystemPrompt = JudgePrompt, Model = model, Streaming = false },
                async (session, ct) =>
                {
                    var r = await session.SendAndWaitAsync(
                        new MessageOptions { Prompt = prompt }, TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
                    return r?.Data?.Content;
                });
            return ParseScore(raw);
        }
        catch (Exception ex)
        {
            return new QualityScore(0, 0, 0, 0, false, $"(judge error: {ex.GetType().Name})", false);
        }
    }

    private static QualityScore ParseScore(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new(0, 0, 0, 0, false, "(empty judge response)", false);
        var s = raw.IndexOf('{');
        var e = raw.LastIndexOf('}');
        if (s < 0 || e <= s) return new(0, 0, 0, 0, false, "(no json in judge response)", false);
        try
        {
            using var doc = JsonDocument.Parse(raw[s..(e + 1)]);
            var root = doc.RootElement;
            int GetI(string name) => root.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;
            bool GetB(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            string GetS(string name) => root.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
            return new(GetI("relevance"), GetI("grounded"), GetI("actionable"), GetI("diversity"),
                GetB("redundant"), GetS("verdict"), true);
        }
        catch (JsonException)
        {
            return new(0, 0, 0, 0, false, "(judge json parse error)", false);
        }
    }

    // ── Diverse, non-trivial chat selection (for quality, not leak stress) ───────
    private static readonly Regex TrivialLastUser = new(
        @"^\s*(ok\b|okay\b|nice\b|thanks|continue$|go on|go ahead|do it|keep going|carry on|next\b|same again|" +
        @"one more|another one|run code review|commit|push|ping$|test message|debug fixture|reply exactly|" +
        @"did you (check|finish|run)|think (carefully|for a few))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<ChatContext> SelectDiverseNuanced(List<ChatContext> contexts)
    {
        bool Eligible(ChatContext c) =>
            !string.IsNullOrWhiteSpace(c.LastUser)
            && c.LastUser!.Trim().Length is >= 20 and <= 600
            && c.Assistant.Trim().Length >= 200          // substantive reply, real next-step worth suggesting
            && !TrivialLastUser.IsMatch(c.LastUser!);

        var eligible = contexts.Where(Eligible).ToList();
        var picked = new List<ChatContext>();
        var seen = new HashSet<Guid>();

        void Take(string label, string pattern, int max)
        {
            var re = new Regex(pattern, RegexOptions.IgnoreCase);
            var added = 0;
            foreach (var c in eligible)
            {
                if (added >= max) break;
                if (seen.Contains(c.ChatId)) continue;
                if (!re.IsMatch($"{c.Title} {c.LastUser}")) continue;
                seen.Add(c.ChatId);
                picked.Add(c);
                added++;
            }
        }

        Take("advice",    @"\b(should i|recommend|which (one|should)|better|worth it|worth buying|pros and cons|advice|help me (choose|decide))\b", 3);
        Take("research",  @"\b(how does|how do|what is|what are|why (is|are|do|does)|explain|difference between|deep (research|dive)|compare)\b", 3);
        Take("creative",  @"\b(story|poem|essay|write me|journal|lyrics|script|novel|character)\b", 3);
        Take("planning",  @"\b(plan|organi[sz]e|schedule|my day|my week|trip|travel|itinerary|reminder|vacation|holiday)\b", 3);
        Take("shopping",  @"\b(buy|price|deal|cheapest|budget|tv|laptop|phone|headphone|monitor|fridge|washing)\b", 3);
        Take("food",      @"\b(recipe|cook|dinner|lunch|bread|meal|food|bake|sourdough)\b", 2);
        Take("life",      @"\b(workout|exercise|sleep|diet|habit|learn|study|language|book|read|motivat)\b", 2);
        Take("technical", @"\b(architecture|design (a|the|pattern)|approach|debug|error|exception|performance|best way to|how should i|trade-?off)\b", 3);
        Take("anything",  @".", 4);   // catch-all for genuinely varied chats not matched above

        return picked;
    }

    // ── Representative-chat selection ───────────────────────────────────────────
    private static List<ChatContext> SelectRepresentative(List<ChatContext> contexts)
    {
        var picked = new List<ChatContext>();
        var seen = new HashSet<Guid>();

        void AddMatches(string pattern, int max)
        {
            var re = new Regex(pattern, RegexOptions.IgnoreCase);
            foreach (var c in contexts.Where(c => re.IsMatch($"{c.Title} {c.LastUser}")))
            {
                if (seen.Add(c.ChatId)) picked.Add(c);
                if (picked.Count(x => re.IsMatch($"{x.Title} {x.LastUser}")) >= max) break;
            }
        }

        // Known leak cases + a spread of non-coding topics, then genuine coding chats.
        AddMatches(@"show me some charts|can you help me with|m365 tools|which tools do you have", 4);
        AddMatches(@"\b(tv|oled|buy|laptop)\b", 3);
        AddMatches(@"\b(recipe|cook|sourdough|dinner|food)\b", 2);
        AddMatches(@"\b(trip|travel|hotel|flight|vacation|lisbon)\b", 2);
        AddMatches(@"write me a story|essay|plan my day", 2);
        AddMatches(@"\b(email|gmail|mail)\b", 2);
        AddMatches(@"\b(commit|refactor|implement|bug|code review|unit test)\b", 4);

        return picked;
    }

    // ── Corpus loading (read-only) ──────────────────────────────────────────────
    private sealed class Pool
    {
        public required List<UserPromptHistoryItem> All { get; init; }
        public required List<UserPromptHistoryItem> Top { get; init; }
        public int Count(bool all) => all ? All.Count : Top.Count;
    }

    private (Pool pool, List<ChatContext> contexts) LoadCorpus()
    {
        var sw = Stopwatch.StartNew();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var metas = LoadChatMetas();
        var chatsDir = Path.Combine(LumiRoot, "chats");

        var all = new List<UserPromptHistoryItem>();
        var contexts = new List<ChatContext>();

        foreach (var meta in metas)
        {
            var messages = ReadMessages(Path.Combine(chatsDir, $"{meta.Id}.json"), opts);
            if (messages.Count == 0) continue;

            foreach (var m in messages)
            {
                if (string.Equals(m.Role, "user", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(m.Content))
                    all.Add(new UserPromptHistoryItem(meta.Id, m.Id, m.Content, m.Timestamp));
            }

            var ctx = BuildContext(meta, messages);
            if (ctx is not null) contexts.Add(ctx);
        }

        var top = all.OrderByDescending(static i => i.Timestamp).Take(ScanLimit).ToList();
        _output.WriteLine($"Loaded corpus in {sw.ElapsedMilliseconds} ms ({metas.Count} chats).");
        return (new Pool { All = all, Top = top }, contexts);
    }

    private static List<ChatMeta> LoadChatMetas()
    {
        var dataFile = Path.Combine(LumiRoot, "data.json");
        using var stream = new FileStream(dataFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = JsonDocument.Parse(stream);

        var metas = new List<ChatMeta>();
        if (!doc.RootElement.TryGetProperty("chats", out var chats))
            return metas;

        foreach (var chat in chats.EnumerateArray())
        {
            if (!chat.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var id))
                continue;
            var title = chat.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            metas.Add(new ChatMeta(id, title));
        }
        return metas;
    }

    private static List<ChatMessage> ReadMessages(string file, JsonSerializerOptions opts)
    {
        if (!File.Exists(file)) return [];
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<List<ChatMessage>>(stream, opts) ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Replicates ChatViewModel's latest-eligible-assistant + preceding-user selection.</summary>
    private static ChatContext? BuildContext(ChatMeta meta, List<ChatMessage> messages)
    {
        var assistantIndex = messages.FindLastIndex(static m =>
            m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content));
        if (assistantIndex < 0) return null;

        var lastUserIndex = messages.FindLastIndex(static m =>
            m.Role == "user" && !string.IsNullOrWhiteSpace(m.Content));
        if (lastUserIndex > assistantIndex) return null;

        var assistant = messages[assistantIndex];
        ChatMessage? lastUser = null;
        for (var i = assistantIndex - 1; i >= 0; i--)
        {
            if (messages[i].Role == "user" && !string.IsNullOrWhiteSpace(messages[i].Content))
            {
                lastUser = messages[i];
                break;
            }
        }

        return new ChatContext(meta.Id, meta.Title, lastUser?.Content, lastUser?.Id, assistant.Content,
            BuildExcerpt(messages, assistantIndex));
    }

    /// <summary>The last few user/assistant turns ending at the eligible assistant reply, for human + judge context.</summary>
    private static string BuildExcerpt(List<ChatMessage> messages, int assistantIndex)
    {
        var turns = new List<string>();
        for (var i = assistantIndex; i >= 0 && turns.Count < 6; i--)
        {
            var m = messages[i];
            if (string.IsNullOrWhiteSpace(m.Content)) continue;
            if (m.Role != "user" && m.Role != "assistant") continue;
            var speaker = m.Role == "user" ? "User" : "Lumi";
            turns.Add($"{speaker}: {Clip(m.Content, 500)}");
        }
        turns.Reverse();
        return string.Join("\n", turns);
    }

    private static string Clip(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var collapsed = Regex.Replace(s.Trim(), @"\s+", " ");
        return collapsed.Length <= max ? collapsed : collapsed[..max] + "…";
    }
}
