using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Microsoft.Extensions.AI;
using StrataTheme.Controls;
using Xunit;
using Xunit.Abstractions;

namespace Lumi.Tests;

/// <summary>
/// Measures which presentation block the model picks for an answer — the option explorer, results, a comparison, a
/// chart, a card, a checklist, a calculator, a quiz, a diagram or plain markdown — so a change to how the system
/// prompt routes between them can be checked for regressions before it ships.
///
/// Each case in <c>RoutingEval/routing-cases.json</c> is a request with the blocks an answer may use (<c>expect</c>:
/// <c>plain</c> means no native block, <c>any</c> only reports what was chosen) and the blocks it must not use
/// (<c>forbid</c>). Requests that need research carry their own sources: the session gets stand-ins for
/// <c>web_search</c> and <c>lumi_fetch</c> that return them, so every run sees the same facts, nothing touches the
/// web, and the links, images and prices in an explorer can be checked against what the research said.
///
/// Sessions run like a chat outside projects: Lumi's system prompt (<see cref="CurrentPrompt"/>) appended to the
/// runtime's own, with only the two research tools. Answers go to <c>runs/current.jsonl</c>. To check a change, run the
/// eval before it, rename that file to keep it as a baseline (say <c>runs/before.jsonl</c>), make the change and run
/// again: every other file in <c>runs</c> is reported next to <c>current</c>, with a verdict on what changed.
///
/// Gated by <c>LUMI_ROUTING_EVAL=1</c> and <c>LUMI_INTEGRATION_TESTS=1</c>, so it never runs in CI. Settings:
/// <list type="bullet">
/// <item><c>LUMI_ROUTING_EVAL_MODEL</c> — the model (default <c>gpt-6-luna</c>: cheap, and the eval measures the prompt).</item>
/// <item><c>LUMI_ROUTING_EVAL_RUNS</c> — answers per case (default 2; a model's choices vary between runs).</item>
/// <item><c>LUMI_ROUTING_EVAL_CASES</c> — case ids or categories to run (default all).</item>
/// <item><c>LUMI_ROUTING_EVAL_EFFORT</c> — reasoning effort (default: the model's own).</item>
/// <item><c>LUMI_ROUTING_EVAL_WORKERS</c> — parallel sessions (default 6).</item>
/// <item><c>LUMI_ROUTING_EVAL_DIR</c> — runs, the prompt, answers and <c>report.md</c> (default %TEMP%\lumi-routing-eval).</item>
/// </list>
/// Answers are cached per case and run with a fingerprint of the model, the effort, the prompt and the case, so after a
/// change only what it touched runs again. Delete <c>runs</c> to start over.
/// </summary>
public class PresentationRoutingEval
{
    private const string DefaultModel = "gpt-6-luna";

    /// <summary>The run file today's prompt answers into; every other run file is a baseline kept from before a change.</summary>
    private const string Current = "current";

    /// <summary>The category of cases whose choice is only reported, never scored.</summary>
    private const string Borderline = "borderline";

    /// <summary>Part of every fingerprint: bump it when the research tools or how a case is asked change.</summary>
    private const string HarnessVersion = "1";

    private const int MaxToolCalls = 8;
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The prompt says what time it is; a fixed time keeps fingerprints, and the dates in the cases, stable.</summary>
    internal const string FixedNow = "It is currently Monday, November 9, 2026 at 2:00 PM (afternoon).";

    /// <summary>The blocks Lumi renders natively. Tables and code blocks are plain markdown.</summary>
    internal static readonly string[] NativeBlocks =
        ["explorer", "results", "comparison", "chart", "card", "confidence", "mermaid", "form", "checklist", "calculator", "quiz"];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ITestOutputHelper _output;

    public PresentationRoutingEval(ITestOutputHelper output) => _output = output;

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);
    private static int EnvInt(string name, int fallback) => int.TryParse(Env(name), out var value) ? value : fallback;
    private static string EvalDir => Env("LUMI_ROUTING_EVAL_DIR") is { Length: > 0 } dir
        ? dir
        : Path.Combine(Path.GetTempPath(), "lumi-routing-eval");
    private static string RunFile(string engine) => Path.Combine(EvalDir, "runs", engine + ".jsonl");

    // ── Cases ───────────────────────────────────────────────────────────────

    public sealed record Source(string Title, string Url, string Text);

    public sealed record RoutingCase
    {
        public required string Id { get; init; }
        public required string Category { get; init; }
        public required string Prompt { get; init; }
        public List<string> Expect { get; init; } = [];
        public List<string> Forbid { get; init; } = [];
        public List<Source> Sources { get; init; } = [];
    }

    private sealed record Corpus(List<RoutingCase> Cases);

    internal static List<RoutingCase> LoadCases()
        => JsonSerializer.Deserialize<Corpus>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RoutingEval", "routing-cases.json")), Json)!.Cases;

    private static List<RoutingCase> SelectCases(List<RoutingCase> cases)
    {
        var filter = Env("LUMI_ROUTING_EVAL_CASES")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return filter is { Length: > 0 }
            ? cases.Where(c => filter.Contains(c.Id, StringComparer.OrdinalIgnoreCase)
                               || filter.Contains(c.Category, StringComparer.OrdinalIgnoreCase)).ToList()
            : cases;
    }

    // ── Prompt ──────────────────────────────────────────────────────────────

    /// <summary>Lumi's prompt for a chat outside projects, without memories or skills, at a fixed time.</summary>
    internal static string CurrentPrompt()
    {
        var prompt = SystemPromptBuilder.Build(new UserSettings { UserName = "Alex" }, null, null, [], [], [],
            SystemPromptBuilder.PromptPlatform.Windows);
        return Regex.Replace(prompt, @"^It is currently [^\r\n]*", FixedNow, RegexOptions.Multiline);
    }

    // ── Research tools ──────────────────────────────────────────────────────

    // The description of Copilot's web_search, which Lumi's chats get through the GitHub MCP server.
    private const string WebSearchDescription = """
        This tool performs an AI-powered web search to provide intelligent, contextual answers with citations.
        Use this tool when:
        - The user's query pertains to recent events or information that is frequently updated
        - The user's query is about new developments, trends, or technologies
        - The user's query is extremely specific, detailed, or pertains to a niche subject not likely to be covered in your knowledge base
        - The user explicitly requests a web search
        - You need current, factual information with verifiable sources

        Returns an AI-generated response with inline citations and a list of sources.
        """;

    private const string ToolBudgetSpent = "No more web requests are available in this conversation. Answer with what you already found.";

    /// <summary>Stand-ins for the chat's research tools that answer from the case's sources (and record each call).</summary>
    private static List<AIFunction> ResearchTools(RoutingCase routingCase, List<string> calls)
    {
        bool Allow(string call)
        {
            lock (calls)
            {
                calls.Add(call);
                return calls.Count <= MaxToolCalls;
            }
        }

        return
        [
            AIFunctionFactory.Create(
                ([Description("A clear, specific question or prompt that requires up-to-date information from the web. Formulate a concise, standalone question based on the user's request; the tool can be called several times for several questions.")] string query)
                    => Allow("web_search: " + query) ? SearchResults(routingCase) : ToolBudgetSpent,
                "web_search",
                WebSearchDescription),
            AIFunctionFactory.Create(
                ([Description("The full URL to fetch (must start with http:// or https://)")] string url)
                    => Allow("lumi_fetch: " + url) ? FetchPage(routingCase, url) : ToolBudgetSpent,
                "lumi_fetch",
                "Fetch a webpage and return its text content. For long pages, returns a preview and saves the full content to a temp file you can read with Get-Content. If this fails, do NOT retry the same URL — try a different source instead."),
        ];
    }

    internal static string SearchResults(RoutingCase routingCase)
    {
        if (routingCase.Sources.Count == 0)
            return "No relevant web results were found for this query.";

        var text = new StringBuilder("Web results:\n");
        for (var i = 0; i < routingCase.Sources.Count; i++)
        {
            var source = routingCase.Sources[i];
            text.Append(CultureInfo.InvariantCulture, $"\n[{i + 1}] {source.Title}\n{source.Url}\n{source.Text}\n");
        }

        return text.ToString();
    }

    private static string FetchPage(RoutingCase routingCase, string url)
    {
        var source = routingCase.Sources.FirstOrDefault(s =>
            string.Equals(s.Url.TrimEnd('/'), url.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        return source is null ? $"Failed to fetch {url}: 404 Not Found." : $"{source.Title}\n{source.Url}\n\n{source.Text}";
    }

    /// <summary>One answer. <see cref="Fingerprint"/> identifies what produced it: the answer is reused only while that is unchanged.</summary>
    public sealed record RunRecord(string Id, int Run, string Fingerprint, long Ms, string? Answer = null,
        List<string>? Tools = null, long InputTokens = 0, long OutputTokens = 0, string? Error = null);

    private static async Task<RunRecord> AskAsync(CopilotService service, string prompt, string model, string? effort,
        RoutingCase routingCase, int run, string fingerprint)
    {
        var started = Stopwatch.StartNew();
        var calls = new List<string>();
        var messages = new List<string>();
        long inputTokens = 0, outputTokens = 0;
        CopilotSession? session = null;
        try
        {
            var config = await service.BuildLightweightSessionConfigAsync(new LightweightSessionOptions
            {
                SystemPrompt = prompt,
                Model = model,
                WorkingDirectory = Path.Combine(EvalDir, "workspace"),
                Tools = ResearchTools(routingCase, calls),
            });
            // A chat appends Lumi's prompt to the runtime's own instead of replacing it, so the eval does too.
            config.SystemMessage = new SystemMessageConfig { Content = prompt, Mode = SystemMessageMode.Append };
            // Only the stand-ins: the runtime's own web_search would otherwise answer from the live web.
            config.AvailableTools = new ToolSet().AddCustom("web_search").AddCustom("lumi_fetch");
            config.ExcludedTools = new ToolSet().AddBuiltIn("*").AddMcp("*");
            if (effort is not null)
                config.ReasoningEffort = effort;

            session = await service.CreateSessionAsync(config);
            using var subscription = session.On<SessionEvent>(evt =>
            {
                lock (messages)
                {
                    switch (evt)
                    {
                        case AssistantMessageEvent { Data.Content: { Length: > 0 } content }:
                            messages.Add(content);
                            break;
                        case AssistantUsageEvent usage:
                            inputTokens += (long)(usage.Data.InputTokens ?? 0);
                            outputTokens += (long)(usage.Data.OutputTokens ?? 0);
                            break;
                    }
                }
            });

            await session.SendAndWaitAsync(new MessageOptions { Prompt = routingCase.Prompt }, AnswerTimeout);
            lock (messages)
            lock (calls)
            {
                var answer = string.Join("\n\n", messages);
                return new RunRecord(routingCase.Id, run, fingerprint, started.ElapsedMilliseconds, answer, [.. calls],
                    inputTokens, outputTokens, answer.Trim().Length == 0 ? "The model sent no answer." : null);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new RunRecord(routingCase.Id, run, fingerprint, started.ElapsedMilliseconds,
                Error: ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (session is not null)
            {
                try
                {
                    await service.DisposeAndDeleteSessionAsync(session);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A session that fails to clean up has still answered.
                }
            }
        }
    }

    // ── Scoring ─────────────────────────────────────────────────────────────

    private static string Normalize(string markdown) => markdown.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>The native blocks and tables in an answer, in order, as Lumi's markdown renderer reads them.</summary>
    internal static List<string> BlocksIn(string answer)
    {
        var blocks = new List<string>();
        foreach (var block in MarkdownParser.Parse(Normalize(answer)))
        {
            var kind = block.Kind switch
            {
                MdBlockKind.Interactive => block.Language.ToLowerInvariant(),
                MdBlockKind.Chart => "chart",
                MdBlockKind.Mermaid => "mermaid",
                MdBlockKind.Confidence => "confidence",
                MdBlockKind.Comparison => "comparison",
                MdBlockKind.Card => "card",
                MdBlockKind.Table => "table",
                _ => null,
            };
            if (kind is not null)
                blocks.Add(kind);
        }

        return blocks;
    }

    /// <summary>What an answer used, such as <c>explorer</c>, <c>comparison + table</c> or <c>plain</c>.</summary>
    internal static string Label(IReadOnlyCollection<string> blocks)
    {
        var used = blocks.Where(static block => block != "table").Distinct().ToList();
        if (blocks.Contains("table"))
            used.Add("table");
        return used.Count == 0 ? "plain" : string.Join(" + ", used);
    }

    internal static bool Passes(RoutingCase routingCase, IReadOnlyCollection<string> blocks)
        => !routingCase.Forbid.Any(blocks.Contains)
           && (routingCase.Expect.Contains("any")
               || routingCase.Expect.Any(blocks.Contains)
               || (routingCase.Expect.Contains("plain") && !blocks.Any(NativeBlocks.Contains)));

    /// <summary>
    /// How an explorer turned out: whether Lumi can show it, its size, how many options link a page
    /// (where their picture comes from), number attributes without a direction to rank them by, and
    /// links, images and prices that the case's research never mentioned.
    /// </summary>
    internal sealed record ExplorerCheck(string? Error, int Items, int Linked, int Attributes, int UnrankedNumbers,
        List<string> UnsupportedLinks, List<string> UnsupportedPrices);

    internal static List<ExplorerCheck> CheckExplorers(string answer, RoutingCase routingCase)
    {
        var research = string.Join("\n", routingCase.Sources.Select(static s => $"{s.Title}\n{s.Url}\n{s.Text}"));
        var numbers = NumbersIn(research);
        var checks = new List<ExplorerCheck>();
        foreach (var block in MarkdownParser.Parse(Normalize(answer)))
        {
            if (block.Kind != MdBlockKind.Interactive || !block.Language.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                continue;

            string? error = null;
            try
            {
                InteractiveDefinition.Parse("explorer", block.Content);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                error = ex.Message;
            }

            int items = 0, linked = 0, attributes = 0, unranked = 0;
            List<string> links = [], prices = [];
            try
            {
                using var document = JsonDocument.Parse(block.Content);
                var money = new HashSet<string>(StringComparer.Ordinal);
                foreach (var attribute in Elements(document.RootElement, "attributes"))
                {
                    attributes++;
                    var type = Text(attribute, "type");
                    if (type == "number" && Text(attribute, "better") is null)
                        unranked++;
                    if (type == "money" && Text(attribute, "id") is { } id)
                        money.Add(id);
                }

                foreach (var item in Elements(document.RootElement, "items"))
                {
                    items++;
                    if (Text(item, "url") is { Length: > 0 })
                        linked++;
                    foreach (var key in (string[])["url", "image"])
                    {
                        if (Text(item, key) is { Length: > 0 } link && !research.Contains(link, StringComparison.OrdinalIgnoreCase))
                            links.Add(link);
                    }

                    if (item.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var value in values.EnumerateObject())
                        {
                            if (money.Contains(value.Name) && value.Value.ValueKind == JsonValueKind.Number
                                && !numbers.Contains(Math.Round(value.Value.GetDouble(), 2)))
                                prices.Add($"{Text(item, "label")}: {value.Value.GetRawText()}");
                        }
                    }
                }
            }
            catch (JsonException)
            {
            }

            checks.Add(new ExplorerCheck(error, items, linked, attributes, unranked, links, prices));
        }

        return checks;
    }

    private static IEnumerable<JsonElement> Elements(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var array)
           && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : [];

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Every number the research states ("$1,599.99", "€218", "₪2,490"), to the cent.</summary>
    private static HashSet<double> NumbersIn(string text)
        => Regex.Matches(Regex.Replace(text, @"(?<=\d),(?=\d{3}\b)", ""), @"\d+(?:\.\d+)?")
            .Select(static match => Math.Round(double.Parse(match.Value, CultureInfo.InvariantCulture), 2))
            .ToHashSet();

    // ── Running ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task RunAndReport()
    {
        Skip.If(Env("LUMI_ROUTING_EVAL") != "1", "Set LUMI_ROUTING_EVAL=1 to run the presentation routing eval.");
        Skip.If(Env("LUMI_INTEGRATION_TESTS") != "1", "Set LUMI_INTEGRATION_TESTS=1 to call the real model.");

        var cases = SelectCases(LoadCases());
        Assert.NotEmpty(cases);
        var prompt = CurrentPrompt();
        var model = Env("LUMI_ROUTING_EVAL_MODEL") is { Length: > 0 } chosen ? chosen : DefaultModel;
        var effort = Env("LUMI_ROUTING_EVAL_EFFORT") is { Length: > 0 } level ? level : null;
        var runs = Math.Max(1, EnvInt("LUMI_ROUTING_EVAL_RUNS", 2));
        var workers = Math.Max(1, EnvInt("LUMI_ROUTING_EVAL_WORKERS", 6));
        foreach (var folder in (string[])["runs", "answers", "workspace"])
            Directory.CreateDirectory(Path.Combine(EvalDir, folder));
        File.WriteAllText(Path.Combine(EvalDir, "prompt.md"), prompt);

        await using var service = new CopilotService();
        await service.ConnectAsync();
        var available = await service.GetModelsAsync();
        var info = available.FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase));
        Assert.True(info is not null, $"Model '{model}' isn't available. Available: {string.Join(", ", available.Select(static m => m.Id).Order())}");
        var billing = CopilotService.GetModelCostMultiplier(info!) is { } multiplier
            ? I($"×{multiplier:0.##} premium requests")
            : CopilotService.GetModelTokenCost(info!) is not null ? "billed by tokens" : null;
        _output.WriteLine($"Model: {model} ({billing ?? "billing unknown"})");

        string Fingerprint(RoutingCase routingCase) => Hash(string.Join("\n",
            HarnessVersion, model, effort, prompt, routingCase.Prompt, JsonSerializer.Serialize(routingCase.Sources, Json)));

        var answered = Successful(RunFile(Current)).Select(static r => (r.Id, r.Run, r.Fingerprint)).ToHashSet();
        var pending = new ConcurrentQueue<(RoutingCase Case, int Run, string Fingerprint)>(
            from run in Enumerable.Range(1, runs)
            from routingCase in cases
            let fingerprint = Fingerprint(routingCase)
            where !answered.Contains((routingCase.Id, run, fingerprint))
            select (routingCase, run, fingerprint));
        _output.WriteLine($"{pending.Count} answers to get, {runs * cases.Count - pending.Count} unchanged");
        var writeLock = new object();
        var clock = Stopwatch.StartNew();

        async Task Work()
        {
            while (pending.TryDequeue(out var item))
            {
                var record = await AskAsync(service, prompt, model, effort, item.Case, item.Run, item.Fingerprint);
                lock (writeLock)
                    File.AppendAllText(RunFile(Current), JsonSerializer.Serialize(record, Json) + "\n");
            }
        }

        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(Work)));
        _output.WriteLine($"Answered in {clock.Elapsed.TotalMinutes:F1} min");

        // Baselines are reported as they were kept; today's answers only while the prompt and case are unchanged.
        var baselines = Directory.GetFiles(Path.Combine(EvalDir, "runs"), "*.jsonl")
            .Select(static file => Path.GetFileNameWithoutExtension(file))
            .Where(static name => !name.Equals(Current, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var answers = baselines.ToDictionary(name => name, name => Latest(RunFile(name), cases, runs, fingerprint: null),
            StringComparer.OrdinalIgnoreCase);
        answers[Current] = Latest(RunFile(Current), cases, runs, Fingerprint);
        var report = BuildReport(cases, baselines, answers, prompt, model, billing, effort, runs);
        File.WriteAllText(Path.Combine(EvalDir, "report.md"), report);
        foreach (var engine in answers.Keys)
            File.WriteAllText(Path.Combine(EvalDir, "answers", engine + ".md"), BuildAnswers(cases, answers[engine]));
        _output.WriteLine(report.Split("## By category")[0]);
        _output.WriteLine($"Report: {Path.Combine(EvalDir, "report.md")}");

        var missing = cases.Sum(routingCase => runs - answers[Current][routingCase.Id].Count);
        Assert.True(missing == 0, $"{missing} answers are missing; run again to retry them (the others are reused).");
    }

    /// <summary>
    /// Each case's answers, one per run: the latest kept, or with a <paramref name="fingerprint"/> only those from
    /// today's model, prompt and case.
    /// </summary>
    private static Dictionary<string, List<RunRecord>> Latest(string runFile, List<RoutingCase> cases, int runs,
        Func<RoutingCase, string>? fingerprint)
    {
        var records = Successful(runFile);
        return cases.ToDictionary(static routingCase => routingCase.Id, routingCase =>
        {
            var current = fingerprint?.Invoke(routingCase);
            return records.Where(r => r.Id == routingCase.Id && (current is null || r.Fingerprint == current) && r.Run <= runs)
                .GroupBy(static r => r.Run)
                .Select(static group => group.Last())
                .OrderBy(static r => r.Run)
                .ToList();
        });
    }

    private static List<RunRecord> Successful(string runFile)
        => File.Exists(runFile)
            ? File.ReadLines(runFile)
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .Select(static line => JsonSerializer.Deserialize<RunRecord>(line, Json)!)
                .Where(static r => r.Error is null && r.Answer is not null)
                .ToList()
            : [];

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    // ── Report ──────────────────────────────────────────────────────────────

    private sealed record Scored(RunRecord Record, List<string> Blocks, bool Pass);

    private static string I(FormattableString text) => FormattableString.Invariant(text);

    private static string Distribution(IEnumerable<Scored> answers)
        => string.Join(", ", answers.GroupBy(static s => Label(s.Blocks))
            .OrderByDescending(static group => group.Count())
            .Select(static group => group.Count() == 1 ? group.Key : I($"{group.Key} ×{group.Count()}")));

    private static string Excerpt(string answer)
    {
        var flat = Regex.Replace(answer.Replace('`', '\''), @"\s+", " ").Trim();
        return flat.Length <= 220 ? flat : flat[..220] + "…";
    }

    private static string BuildReport(List<RoutingCase> cases, IReadOnlyList<string> baselines,
        Dictionary<string, Dictionary<string, List<RunRecord>>> answers, string prompt,
        string model, string? billing, string? effort, int runs)
    {
        var engines = baselines.Append(Current).ToList();
        var scored = engines.ToDictionary(engine => engine, engine => cases.ToDictionary(static c => c.Id, c =>
            answers[engine][c.Id].Select(record =>
            {
                var blocks = BlocksIn(record.Answer!);
                return new Scored(record, blocks, Passes(c, blocks));
            }).ToList()), StringComparer.OrdinalIgnoreCase);
        var text = new StringBuilder();

        text.AppendLine("# Presentation routing eval").AppendLine();
        text.AppendLine(I($"Model `{model}`{(billing is null ? "" : $" ({billing})")} · reasoning effort {effort ?? "default"} · {runs} answer(s) per case · {cases.Count} cases · prompt {prompt.Length:N0} chars · {DateTime.Now:yyyy-MM-dd HH:mm}"));
        text.AppendLine();
        text.AppendLine("| Run | Answers | Input tokens | Output tokens | Median time |");
        text.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var engine in engines)
        {
            var records = scored[engine].Values.SelectMany(static list => list).Select(static s => s.Record).ToList();
            var median = records.Count == 0 ? 0 : records.Select(static r => r.Ms).Order().ElementAt(records.Count / 2) / 1000.0;
            text.AppendLine(I($"| {engine} | {records.Count} | {records.Sum(static r => r.InputTokens):N0} | {records.Sum(static r => r.OutputTokens):N0} | {median:F0} s |"));
        }

        text.AppendLine();
        foreach (var baseline in baselines)
            AppendVerdict(text, cases, baseline, scored[baseline], scored[Current]);
        if (baselines.Count == 0)
            text.AppendLine("No baseline to compare with: before a change, rename `runs/current.jsonl` (to `before.jsonl`, say).").AppendLine();

        text.AppendLine("## By category").AppendLine();
        text.AppendLine("| Category | Cases | " + string.Join(" | ", engines) + " |");
        text.AppendLine("|---|---:|" + string.Concat(engines.Select(static _ => "---:|")));
        foreach (var group in cases.GroupBy(static c => c.Category))
        {
            var cells = engines.Select(engine => group.Key == Borderline
                ? "not scored"
                : I($"{group.Sum(c => scored[engine][c.Id].Count(static s => s.Pass))}/{group.Sum(c => scored[engine][c.Id].Count)}"));
            text.AppendLine(I($"| {group.Key} | {group.Count()} | {string.Join(" | ", cells)} |"));
        }

        text.AppendLine();
        foreach (var engine in engines)
            AppendExplorerQuality(text, engine, cases, scored[engine]);

        text.AppendLine("## Every case").AppendLine();
        text.AppendLine("| Case | Category | Expected | " + string.Join(" | ", engines) + " |");
        text.AppendLine("|---|---|---|" + string.Concat(engines.Select(static _ => "---|")));
        foreach (var c in cases)
        {
            var expected = string.Join(", ", c.Expect) + (c.Forbid.Count > 0 ? "; not " + string.Join(", ", c.Forbid) : "");
            var cells = engines.Select(engine => scored[engine][c.Id] is { Count: > 0 } list
                ? (c.Category == Borderline ? "" : I($"{list.Count(static s => s.Pass)}/{list.Count} · ")) + Distribution(list)
                : "–");
            text.AppendLine($"| `{c.Id}` | {c.Category} | {expected} | {string.Join(" | ", cells)} |");
        }

        text.AppendLine();
        var misses = cases.Where(static c => c.Category != Borderline)
            .SelectMany(c => scored[Current][c.Id].Where(static s => !s.Pass).Select(s => (Case: c, Answer: s)))
            .ToList();
        if (misses.Count > 0)
        {
            text.AppendLine($"## Answers that missed (`{Current}`)").AppendLine();
            foreach (var (c, s) in misses)
                text.AppendLine(I($"- `{c.Id}` run {s.Record.Run}, {Label(s.Blocks)}: {Excerpt(s.Record.Answer!)}"));
            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>What changed from a baseline: explorer use where options were researched, explorers where they don't
    /// belong, and the cases that pass more or less often.</summary>
    private static void AppendVerdict(StringBuilder text, List<RoutingCase> cases, string baseline,
        Dictionary<string, List<Scored>> before, Dictionary<string, List<Scored>> current)
    {
        var research = cases.Where(static c => c.Category == "explorer").ToList();
        var others = cases.Where(static c => c.Category is not "explorer" and not Borderline).ToList();
        static int Explorers(Dictionary<string, List<Scored>> answers, IEnumerable<RoutingCase> cases) =>
            cases.Sum(c => answers[c.Id].Count(static s => s.Blocks.Contains("explorer")));
        static int Answers(Dictionary<string, List<Scored>> answers, IEnumerable<RoutingCase> cases) =>
            cases.Sum(c => answers[c.Id].Count);
        static int Passed(Dictionary<string, List<Scored>> answers, RoutingCase c) => answers[c.Id].Count(static s => s.Pass);
        // Runs may differ in how many answers they hold, so cases compare by pass rate.
        static double Rate(Dictionary<string, List<Scored>> answers, RoutingCase c) =>
            (double)Passed(answers, c) / answers[c.Id].Count;

        text.AppendLine($"## Verdict: `{baseline}` → `{Current}`").AppendLine();
        if (research.Count > 0)
            text.AppendLine(I($"- **Option research:** an explorer in {Explorers(before, research)}/{Answers(before, research)} answers with `{baseline}` and {Explorers(current, research)}/{Answers(current, research)} with `{Current}`."));

        if (others.Count > 0)
        {
            var overTriggered = others.Where(c => current[c.Id].Any(static s => s.Blocks.Contains("explorer"))).ToList();
            text.AppendLine(overTriggered.Count == 0
                ? I($"- **Over-triggering:** no explorer in any of the {Answers(current, others)} answers to the other scenarios.")
                : I($"- **Over-triggering:** an explorer appeared in {string.Join(", ", overTriggered.Select(c => I($"`{c.Id}` ({Explorers(current, [c])}/{current[c.Id].Count})")))}."));
        }

        var compared = cases.Where(c => c.Category != Borderline && before[c.Id].Count > 0 && current[c.Id].Count > 0).ToList();
        var worse = compared.Where(c => Rate(current, c) < Rate(before, c)).ToList();
        var better = compared.Where(c => Rate(current, c) > Rate(before, c)).ToList();
        string Changes(IEnumerable<RoutingCase> changed) => string.Join(", ", changed.Select(c =>
            I($"`{c.Id}` ({Passed(before, c)}/{before[c.Id].Count} → {Passed(current, c)}/{current[c.Id].Count})")));
        text.AppendLine(I($"- **Passes:** {compared.Sum(c => Passed(before, c))}/{Answers(before, compared)} with `{baseline}` and {compared.Sum(c => Passed(current, c))}/{Answers(current, compared)} with `{Current}`. ")
            + (worse.Count == 0 ? "No case passed less often" : "Passed less often: " + Changes(worse))
            + (better.Count == 0 ? "." : "; more often: " + Changes(better) + "."));
        text.AppendLine();
    }

    private static void AppendExplorerQuality(StringBuilder text, string engine, List<RoutingCase> cases,
        Dictionary<string, List<Scored>> scored)
    {
        var rows = new List<string>();
        var notes = new List<string>();
        foreach (var c in cases)
        {
            var checks = scored[c.Id].SelectMany(s => CheckExplorers(s.Record.Answer!, c)).ToList();
            if (checks.Count == 0)
                continue;

            rows.Add(I($"| `{c.Id}` | {checks.Count} | {checks.Count(static k => k.Error is null)} | {checks.Average(static k => k.Items):0.#} | {checks.Sum(static k => k.Linked)}/{checks.Sum(static k => k.Items)} | {checks.Average(static k => k.Attributes):0.#} | {checks.Sum(static k => k.UnrankedNumbers)} | {checks.Sum(static k => k.UnsupportedLinks.Count)} | {checks.Sum(static k => k.UnsupportedPrices.Count)} |"));
            notes.AddRange(checks.Where(static k => k.Error is not null).Select(k => $"`{c.Id}` can't be shown: {k.Error}"));
            notes.AddRange(checks.SelectMany(static k => k.UnsupportedLinks).Distinct().Select(link => $"`{c.Id}` link or image not in the research: {link}"));
            notes.AddRange(checks.SelectMany(static k => k.UnsupportedPrices).Distinct().Select(price => $"`{c.Id}` price not in the research: {price}"));
        }

        if (rows.Count == 0)
            return;

        text.AppendLine($"## Explorers from `{engine}`").AppendLine();
        text.AppendLine("| Case | Explorers | Valid | Items (avg) | Options with a link | Attributes (avg) | Numbers without `better` | Links/images not in research | Prices not in research |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var row in rows)
            text.AppendLine(row);
        text.AppendLine();
        foreach (var note in notes)
            text.AppendLine("- " + note);
        if (notes.Count > 0)
            text.AppendLine();
    }

    private static string BuildAnswers(List<RoutingCase> cases, Dictionary<string, List<RunRecord>> answers)
    {
        var text = new StringBuilder();
        foreach (var c in cases)
        {
            text.AppendLine($"# {c.Id} ({c.Category})").AppendLine().AppendLine("> " + c.Prompt).AppendLine();
            foreach (var record in answers[c.Id])
            {
                var blocks = BlocksIn(record.Answer!);
                text.AppendLine(I($"## Run {record.Run}: {Label(blocks)}, {(Passes(c, blocks) ? "pass" : "miss")}, {record.Ms / 1000.0:F0} s"));
                text.AppendLine().AppendLine("Tools: " + (record.Tools is { Count: > 0 } tools ? string.Join(" · ", tools) : "none")).AppendLine();
                text.AppendLine("````markdown").AppendLine(record.Answer).AppendLine("````").AppendLine();
            }
        }

        return text.ToString();
    }

    // ── Checks that run everywhere ──────────────────────────────────────────

    [Fact]
    public void Cases_AreWellFormed()
    {
        var cases = LoadCases();
        string[] known = [.. NativeBlocks, "plain", "any"];

        Assert.True(cases.Count >= 30, "The eval needs enough cases to compare runs.");
        Assert.Equal(cases.Count, cases.Select(static c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var c in cases)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Prompt), c.Id);
            Assert.NotEmpty(c.Expect);
            Assert.All(c.Expect.Concat(c.Forbid), kind => Assert.Contains(kind, known));
            Assert.Empty(c.Expect.Intersect(c.Forbid));
            Assert.All(c.Sources, source => Assert.StartsWith("https://", source.Url, StringComparison.Ordinal));
        }

        Assert.All(cases.Where(static c => c.Category == "explorer"), c => Assert.True(c.Sources.Count > 0, c.Id));
        Assert.All(cases.Where(static c => c.Category is not "explorer" and not Borderline),
            c => Assert.Contains("explorer", c.Forbid));
    }

    [Fact]
    public void CurrentPrompt_StaysTheSameBetweenRuns()
    {
        // Answers are cached by the prompt, so it must not carry the time of day.
        var prompt = CurrentPrompt();

        Assert.Contains(FixedNow, prompt);
        Assert.Equal(prompt, CurrentPrompt());
        Assert.Contains("```explorer", prompt);
    }

    [Fact]
    public void Scoring_ReadsBlocksAsLumiRendersThem()
    {
        const string answer = """
            Here's what I found.

            | TV | Price |
            |---|---|
            | TV A | $1,599.99 |

            ```explorer
            {"id":"tvs","title":"TVs","attributes":[{"id":"price","label":"Price","type":"money","unit":"USD"},{"id":"hz","label":"Refresh rate","type":"number","unit":"Hz"}],"items":[{"id":"a","label":"TV A","url":"https://shop.example/a","values":{"price":1599.99,"hz":144}},{"id":"b","label":"TV B","image":"https://images.example/b.jpg","values":{"price":1600,"hz":120}},{"id":"c","label":"TV C","values":{"price":249,"hz":60}}]}
            ```

            ```chart
            {"type":"bar","labels":["TV A"],"series":[{"name":"Price","values":[1599.99]}]}
            ```
            """;
        var research = new RoutingCase
        {
            Id = "tvs",
            Category = "explorer",
            Prompt = "Which TV?",
            Expect = ["explorer"],
            Sources = [new Source("TV A", "https://shop.example/a", "TV A costs $1,599.99 and TV C $249.00.")],
        };

        var blocks = BlocksIn(answer);
        Assert.Equal(["table", "explorer", "chart"], blocks);
        Assert.Equal("explorer + chart + table", Label(blocks));
        Assert.True(Passes(research, blocks));
        Assert.False(Passes(research with { Forbid = ["chart"] }, blocks));

        var plain = research with { Expect = ["plain"], Forbid = ["explorer"] };
        Assert.True(Passes(plain, BlocksIn("Canberra.\n\n| City | Country |\n|---|---|\n| Canberra | Australia |")));
        Assert.False(Passes(plain, BlocksIn("```card\n{\"header\":\"Canberra\",\"summary\":\"Capital\"}\n```")));
        Assert.True(Passes(research with { Expect = ["any"] }, BlocksIn("Anything.")));

        var check = Assert.Single(CheckExplorers(answer, research));
        Assert.Null(check.Error);
        Assert.Equal(3, check.Items);
        Assert.Equal(1, check.Linked);
        Assert.Equal(2, check.Attributes);
        Assert.Equal(1, check.UnrankedNumbers);
        Assert.Equal(["https://images.example/b.jpg"], check.UnsupportedLinks);
        Assert.Equal(["TV B: 1600"], check.UnsupportedPrices);
        Assert.NotNull(Assert.Single(CheckExplorers("```explorer\n{\"id\":\"x\",\"title\":\"X\",\"items\":[]}\n```", research)).Error);
    }

    [Fact]
    public void ResearchTools_AnswerFromTheCaseSources()
    {
        var withSources = LoadCases().First(static c => c.Id == "tv-65-gaming");
        var results = SearchResults(withSources);

        Assert.Contains("[1] The best 65-inch TVs of 2026", results);
        Assert.Contains("https://www.amazon.com/dp/B0CVFW9QZ8", results);
        Assert.Contains("No relevant web results", SearchResults(withSources with { Sources = [] }));
    }
}
