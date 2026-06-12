using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Lumi.UiPerf;

/// <summary>Raw per-action measurements collected by the harness, fed into the report builder.</summary>
public sealed class UiActionSamples
{
    public required string ActionId { get; init; }
    public required string Category { get; init; }
    public required string DisplayName { get; init; }
    public string? Note { get; init; }
    public int Iterations { get; set; }

    /// <summary>Wall-clock duration of the action delegate per iteration (ms).</summary>
    public List<double> RunDurationsMs { get; } = new();

    /// <summary>
    /// Per-iteration post-action work time (ms): how long the UI thread stayed busy draining
    /// deferred work after the action delegate returned. Excludes the fixed quiet padding.
    /// </summary>
    public List<double> PostActionDurationsMs { get; } = new();

    /// <summary>Per-iteration worst stall (ms) — the largest single latency sample in that run.</summary>
    public List<double> IterationMaxMs { get; } = new();

    /// <summary>UI-thread latency samples captured from action start through the post-action drain.</summary>
    public List<double> LatenciesMs { get; } = new();
}

/// <summary>Aggregated responsiveness result for a single UX action.</summary>
public sealed class UiActionResult
{
    public required string ActionId { get; init; }
    public required string Category { get; init; }
    public required string DisplayName { get; init; }
    public string? Note { get; init; }
    public int Iterations { get; init; }
    public double MeanRunMs { get; init; }

    /// <summary>Mean post-action drain time (ms): UI work that kept running after the action returned.</summary>
    public double MeanPostActionMs { get; init; }
    public double MaxPostActionMs { get; init; }

    /// <summary>Iterations whose worst stall reached the High-stall threshold — a consistency signal.</summary>
    public int IterationsWithStall { get; init; }
    public LatencyStats Latency { get; init; } = LatencyStats.Empty;
    public ImpactLevel Impact { get; init; }
    public double ImpactScore { get; init; }
}

/// <summary>Aggregated responsiveness for a UX category (worst action drives the rating).</summary>
public sealed class UiCategoryRollup
{
    public required string Category { get; init; }
    public int ActionCount { get; init; }
    public LatencyStats Latency { get; init; } = LatencyStats.Empty;
    public double WorstActionP99Ms { get; init; }
    public double WorstActionMaxMs { get; init; }
    public ImpactLevel Impact { get; init; }
    public double ImpactScore { get; init; }
}

/// <summary>The full responsiveness report: per-action results, category rollups and a summary.</summary>
public sealed class UiResponsivenessReport
{
    public DateTimeOffset GeneratedAt { get; init; }
    public string Mode { get; init; } = "full";
    public int Iterations { get; init; }
    public int WarmupIterations { get; init; }
    public int SampleIntervalMs { get; init; }
    public int SettleQuietMs { get; init; }
    public IReadOnlyList<UiActionResult> Actions { get; init; } = Array.Empty<UiActionResult>();
    public IReadOnlyList<UiCategoryRollup> Categories { get; init; } = Array.Empty<UiCategoryRollup>();
    public LatencyStats Overall { get; init; } = LatencyStats.Empty;
    public int CriticalCount { get; init; }
    public int HighCount { get; init; }
    public int ModerateCount { get; init; }
    public int GoodCount { get; init; }

    /// <summary>Regression-gate threshold (null = no gate); set from the harness options.</summary>
    public ImpactLevel? FailOnLevel { get; init; }

    /// <summary>Number of actions at or above <see cref="FailOnLevel"/> (the gate offenders).</summary>
    public int GateOffenderCount { get; init; }

    /// <summary>True when a gate is configured and at least one action breached it.</summary>
    public bool GateFailed => FailOnLevel is not null && GateOffenderCount > 0;

    public static UiResponsivenessReport Build(UiHarnessOptions options, IEnumerable<UiActionSamples> samples)
    {
        var samplesList = samples?.ToList() ?? new List<UiActionSamples>();
        var allLatencies = new List<double>();

        var actions = new List<UiActionResult>(samplesList.Count);
        foreach (var sample in samplesList)
        {
            var stats = LatencyStats.FromLatencies(sample.LatenciesMs);
            allLatencies.AddRange(sample.LatenciesMs);
            actions.Add(new UiActionResult
            {
                ActionId = sample.ActionId,
                Category = sample.Category,
                DisplayName = sample.DisplayName,
                Note = sample.Note,
                Iterations = sample.Iterations,
                MeanRunMs = Mean(sample.RunDurationsMs),
                MeanPostActionMs = Mean(sample.PostActionDurationsMs),
                MaxPostActionMs = sample.PostActionDurationsMs.Count > 0 ? sample.PostActionDurationsMs.Max() : 0d,
                IterationsWithStall = sample.IterationMaxMs.Count(m => m >= UiResponsivenessThresholds.HighStallMs),
                Latency = stats,
                Impact = UiImpactClassifier.Classify(stats),
                ImpactScore = UiImpactClassifier.Score(stats),
            });
        }

        actions.Sort((a, b) =>
        {
            var byImpact = b.Impact.CompareTo(a.Impact);
            return byImpact != 0 ? byImpact : b.ImpactScore.CompareTo(a.ImpactScore);
        });

        var categories = new List<UiCategoryRollup>();
        foreach (var group in actions.GroupBy(a => a.Category))
        {
            var categoryRaw = samplesList
                .Where(s => string.Equals(s.Category, group.Key, StringComparison.Ordinal))
                .SelectMany(s => s.LatenciesMs)
                .ToList();
            var combined = LatencyStats.FromLatencies(categoryRaw);
            var worstP99 = group.Max(a => a.Latency.P99Ms);
            var worstMax = group.Max(a => a.Latency.MaxMs);
            categories.Add(new UiCategoryRollup
            {
                Category = group.Key,
                ActionCount = group.Count(),
                Latency = combined,
                WorstActionP99Ms = worstP99,
                WorstActionMaxMs = worstMax,
                Impact = UiImpactClassifier.Classify(worstP99, worstMax),
                ImpactScore = UiImpactClassifier.Score(worstP99, worstMax),
            });
        }

        categories.Sort((a, b) =>
        {
            var byImpact = b.Impact.CompareTo(a.Impact);
            return byImpact != 0 ? byImpact : b.ImpactScore.CompareTo(a.ImpactScore);
        });

        return new UiResponsivenessReport
        {
            GeneratedAt = DateTimeOffset.Now,
            Mode = options?.Mode ?? "full",
            Iterations = options?.Iterations ?? 0,
            WarmupIterations = options?.WarmupIterations ?? 0,
            SampleIntervalMs = options?.SampleIntervalMs ?? 0,
            SettleQuietMs = options?.SettleQuietMs ?? 0,
            Actions = actions,
            Categories = categories,
            Overall = LatencyStats.FromLatencies(allLatencies),
            CriticalCount = actions.Count(a => a.Impact == ImpactLevel.Critical),
            HighCount = actions.Count(a => a.Impact == ImpactLevel.High),
            ModerateCount = actions.Count(a => a.Impact == ImpactLevel.Moderate),
            GoodCount = actions.Count(a => a.Impact == ImpactLevel.Good),
            FailOnLevel = options?.FailOnLevel,
            GateOffenderCount = options?.FailOnLevel is { } gate ? actions.Count(a => a.Impact >= gate) : 0,
        };
    }

    public string ToConsole()
    {
        var sb = new StringBuilder();
        const string rule = "========================================================================================";
        sb.AppendLine(rule);
        sb.AppendLine(" Lumi UI Responsiveness Report");
        sb.AppendLine($" Generated : {GeneratedAt:yyyy-MM-dd HH:mm:ss}    Mode: {Mode}    Iterations: {Iterations} (warmup {WarmupIterations})");
        sb.AppendLine($" Probe     : {SampleIntervalMs}ms sampling of Background-priority dispatcher latency.");
        sb.AppendLine("             Higher = UI thread busy / less idle headroom — a proxy for lag and freezes.");
        sb.AppendLine("             Debug build: absolute ms are pessimistic; use for ranking/regressions, not UX certification.");
        sb.AppendLine(rule);
        sb.AppendLine();
        sb.AppendLine(" SUMMARY");
        sb.AppendLine($"   Actions measured : {Actions.Count}");
        sb.AppendLine($"   Critical / High / Moderate / Good : {CriticalCount} / {HighCount} / {ModerateCount} / {GoodCount}");
        sb.AppendLine($"   Overall latency  : p95 {F(Overall.P95Ms)}ms   p99 {F(Overall.P99Ms)}ms   worst stall {F(Overall.MaxMs)}ms");
        sb.AppendLine($"   Impact scale     : Good <{F(UiResponsivenessThresholds.ModerateP99Ms)}  " +
                      $"Moderate <{F(UiResponsivenessThresholds.HighP99Ms)}  " +
                      $"High <{F(UiResponsivenessThresholds.CriticalP99Ms)}  " +
                      $"Critical >={F(UiResponsivenessThresholds.CriticalP99Ms)}ms p99 (or stall >={F(UiResponsivenessThresholds.CriticalStallMs)}ms)");
        sb.AppendLine();

        sb.AppendLine(" WORST UX ACTIONS (ranked by responsiveness impact)");
        sb.AppendLine($"   {"#",-3} {"IMPACT",-9} {"p99(ms)",9} {"max(ms)",9} {"run(ms)",9} {"post(ms)",9}  action [category]");
        sb.AppendLine($"   {new string('-', 78)}");
        var rank = 1;
        foreach (var action in Actions)
        {
            sb.AppendLine(
                $"   {rank,-3} {UiImpactClassifier.Label(action.Impact),-9} {F(action.Latency.P99Ms),9} {F(action.Latency.MaxMs),9} " +
                $"{F(action.MeanRunMs),9} {F(action.MeanPostActionMs),9}  {action.DisplayName} [{action.Category}]");
            rank++;
        }
        sb.AppendLine();

        sb.AppendLine(" BY CATEGORY (worst action drives the rating)");
        sb.AppendLine($"   {"IMPACT",-9} {"p99(ms)",9} {"max(ms)",9} {"jank>100",9} {"freeze",7}  category (#actions)");
        sb.AppendLine($"   {new string('-', 78)}");
        foreach (var category in Categories)
        {
            sb.AppendLine(
                $"   {UiImpactClassifier.Label(category.Impact),-9} {F(category.WorstActionP99Ms),9} {F(category.WorstActionMaxMs),9} " +
                $"{category.Latency.BadJankCount,9} {category.Latency.FreezeCount,7}  {category.Category} ({category.ActionCount})");
        }
        sb.AppendLine();

        var flagged = Actions.Where(a => a.Impact >= ImpactLevel.High && !string.IsNullOrWhiteSpace(a.Note)).ToList();
        if (flagged.Count > 0)
        {
            sb.AppendLine(" OPTIMIZATION HINTS (high-impact actions)");
            foreach (var action in flagged)
            {
                var consistency = action.Iterations > 0
                    ? $" [stalled in {action.IterationsWithStall}/{action.Iterations} runs]"
                    : string.Empty;
                sb.AppendLine($"   - {action.DisplayName}{consistency}: {action.Note}");
            }
            sb.AppendLine();
        }

        if (FailOnLevel is { } gateLevel)
        {
            sb.AppendLine(GateFailed
                ? $" GATE: FAIL — {GateOffenderCount} action(s) at or above {UiImpactClassifier.Label(gateLevel)} " +
                  $"(--ui-perf-fail-on {gateLevel.ToString().ToLowerInvariant()})"
                : $" GATE: PASS — no action at or above {UiImpactClassifier.Label(gateLevel)}");
            sb.AppendLine();
        }

        sb.AppendLine(rule);
        return sb.ToString();
    }

    public string ToJson()
    {
        var root = new JsonObject
        {
            ["generatedAt"] = GeneratedAt.ToString("o", CultureInfo.InvariantCulture),
            ["mode"] = Mode,
            ["iterations"] = Iterations,
            ["warmupIterations"] = WarmupIterations,
            ["sampleIntervalMs"] = SampleIntervalMs,
            ["settleQuietMs"] = SettleQuietMs,
            ["summary"] = new JsonObject
            {
                ["actionsMeasured"] = Actions.Count,
                ["critical"] = CriticalCount,
                ["high"] = HighCount,
                ["moderate"] = ModerateCount,
                ["good"] = GoodCount,
                ["overall"] = StatsToJson(Overall),
            },
        };

        if (FailOnLevel is { } gateLevel && root["summary"] is JsonObject summary)
        {
            summary["gate"] = new JsonObject
            {
                ["level"] = UiImpactClassifier.Label(gateLevel),
                ["failed"] = GateFailed,
                ["offenders"] = GateOffenderCount,
            };
        }

        var categories = new JsonArray();
        foreach (var category in Categories)
        {
            categories.Add(new JsonObject
            {
                ["category"] = category.Category,
                ["actionCount"] = category.ActionCount,
                ["impact"] = UiImpactClassifier.Label(category.Impact),
                ["impactScore"] = Round(category.ImpactScore),
                ["worstActionP99Ms"] = Round(category.WorstActionP99Ms),
                ["worstActionMaxMs"] = Round(category.WorstActionMaxMs),
                ["latency"] = StatsToJson(category.Latency),
            });
        }
        root["categories"] = categories;

        var actions = new JsonArray();
        foreach (var action in Actions)
        {
            actions.Add(new JsonObject
            {
                ["actionId"] = action.ActionId,
                ["category"] = action.Category,
                ["displayName"] = action.DisplayName,
                ["note"] = action.Note,
                ["iterations"] = action.Iterations,
                ["impact"] = UiImpactClassifier.Label(action.Impact),
                ["impactScore"] = Round(action.ImpactScore),
                ["meanRunMs"] = Round(action.MeanRunMs),
                ["meanPostActionMs"] = Round(action.MeanPostActionMs),
                ["maxPostActionMs"] = Round(action.MaxPostActionMs),
                ["iterationsWithStall"] = action.IterationsWithStall,
                ["latency"] = StatsToJson(action.Latency),
            });
        }
        root["actions"] = actions;

        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject StatsToJson(LatencyStats stats) => new()
    {
        ["count"] = stats.Count,
        ["meanMs"] = Round(stats.MeanMs),
        ["p50Ms"] = Round(stats.P50Ms),
        ["p95Ms"] = Round(stats.P95Ms),
        ["p99Ms"] = Round(stats.P99Ms),
        ["maxMs"] = Round(stats.MaxMs),
        ["jankCount"] = stats.JankCount,
        ["badJankCount"] = stats.BadJankCount,
        ["freezeCount"] = stats.FreezeCount,
    };

    private static double Mean(IReadOnlyCollection<double> values)
        => values.Count == 0 ? 0d : values.Sum() / values.Count;

    private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string F(double value) => value.ToString("F1", CultureInfo.InvariantCulture);
}
