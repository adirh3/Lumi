using System.Linq;
using System.Text.Json;
using Lumi.UiPerf;
using Xunit;

namespace Lumi.Tests;

public class UiResponsivenessReportTests
{
    private static UiActionSamples Samples(
        string id,
        string category,
        string display,
        double[] latencies,
        double runMs = 10d,
        double postMs = 20d,
        string? note = null)
    {
        var samples = new UiActionSamples
        {
            ActionId = id,
            Category = category,
            DisplayName = display,
            Note = note,
        };
        samples.LatenciesMs.AddRange(latencies);
        samples.RunDurationsMs.Add(runMs);
        samples.PostActionDurationsMs.Add(postMs);
        samples.IterationMaxMs.Add(latencies.Length > 0 ? latencies.Max() : 0d);
        samples.Iterations = 1;
        return samples;
    }

    [Fact]
    public void Build_RanksActionsWorstFirst()
    {
        var report = UiResponsivenessReport.Build(
            UiHarnessOptions.Parse(new[] { "--ui-perf-harness" }),
            new[]
            {
                Samples("good", "Composer", "Type", new[] { 1d, 2d, 3d }),
                Samples("bad", "Transcript scroll", "Scroll", new[] { 5d, 300d, 900d }, note: "Heavy"),
                Samples("mid", "Navigation", "Open Settings", new[] { 40d, 60d, 70d }),
            });

        Assert.Equal("bad", report.Actions[0].ActionId);
        Assert.Equal(ImpactLevel.Critical, report.Actions[0].Impact);
        Assert.Equal("good", report.Actions[^1].ActionId);
        Assert.Equal(ImpactLevel.Good, report.Actions[^1].Impact);

        Assert.Equal(1, report.CriticalCount);
        Assert.Equal(3, report.Actions.Count);
        Assert.Equal("full", report.Mode);
    }

    [Fact]
    public void Build_AggregatesActionAndOverallStats()
    {
        var report = UiResponsivenessReport.Build(
            UiHarnessOptions.Parse(new[] { "--ui-perf-harness" }),
            new[] { Samples("a", "Chat open", "Open", new[] { 10d, 20d, 30d }, runMs: 100d, postMs: 50d) });

        var action = report.Actions.Single();
        Assert.Equal(100d, action.MeanRunMs, 3);
        Assert.Equal(50d, action.MeanPostActionMs, 3);
        Assert.Equal(50d, action.MaxPostActionMs, 3);
        Assert.Equal(3, action.Latency.Count);
        Assert.Equal(3, report.Overall.Count);
    }

    [Fact]
    public void Build_CategoryRollup_WorstActionDrivesRating()
    {
        var report = UiResponsivenessReport.Build(
            UiHarnessOptions.Parse(new[] { "--ui-perf-harness" }),
            new[]
            {
                Samples("nav-good", "Navigation", "Warm switch", new[] { 1d, 2d }),
                Samples("nav-bad", "Navigation", "Cold open", new[] { 10d, 400d }),
            });

        var navigation = report.Categories.Single(c => c.Category == "Navigation");
        Assert.Equal(2, navigation.ActionCount);
        Assert.Equal(ImpactLevel.Critical, navigation.Impact); // 400ms stall in worst action
        Assert.Equal(400d, navigation.WorstActionMaxMs);
    }

    [Fact]
    public void ToJson_ProducesParseableStructuredOutput()
    {
        var report = UiResponsivenessReport.Build(
            UiHarnessOptions.Parse(new[] { "--ui-perf-harness", "--ui-perf-filter", "composer" }),
            new[] { Samples("c", "Composer", "Type", new[] { 1d, 2d, 3d }) });

        using var doc = JsonDocument.Parse(report.ToJson());
        var root = doc.RootElement;

        Assert.Equal("filtered", root.GetProperty("mode").GetString());
        Assert.Equal(1, root.GetProperty("actions").GetArrayLength());
        Assert.Equal(1, root.GetProperty("categories").GetArrayLength());
        Assert.Equal(3, root.GetProperty("summary").GetProperty("overall").GetProperty("count").GetInt32());
        Assert.Equal("Composer", root.GetProperty("actions")[0].GetProperty("category").GetString());
    }

    [Fact]
    public void ToConsole_IncludesHeadingsAndActions()
    {
        var report = UiResponsivenessReport.Build(
            UiHarnessOptions.Parse(new[] { "--ui-perf-harness" }),
            new[] { Samples("a", "Navigation", "Open Settings", new[] { 10d, 20d }) });

        var text = report.ToConsole();

        Assert.Contains("Lumi UI Responsiveness Report", text);
        Assert.Contains("WORST UX ACTIONS", text);
        Assert.Contains("BY CATEGORY", text);
        Assert.Contains("Open Settings", text);
    }

    [Fact]
    public void Build_NoGate_ByDefault()
    {
        var report = UiResponsivenessReport.Build(
            UiHarnessOptions.Parse(new[] { "--ui-perf-harness" }),
            new[] { Samples("bad", "Navigation", "Cold open", new[] { 10d, 400d }) });

        Assert.Null(report.FailOnLevel);
        Assert.False(report.GateFailed);
        Assert.Equal(0, report.GateOffenderCount);
    }

    [Fact]
    public void Build_Gate_FailsWhenActionBreachesThreshold()
    {
        var report = UiResponsivenessReport.Build(
            UiHarnessOptions.Parse(new[] { "--ui-perf-harness", "--ui-perf-fail-on", "critical" }),
            new[]
            {
                Samples("good", "Composer", "Type", new[] { 1d, 2d, 3d }),
                Samples("bad", "Transcript scroll", "Scroll", new[] { 5d, 300d, 900d }),
            });

        Assert.Equal(ImpactLevel.Critical, report.FailOnLevel);
        Assert.True(report.GateFailed);
        Assert.Equal(1, report.GateOffenderCount);
        Assert.Contains("GATE: FAIL", report.ToConsole());
    }

    [Fact]
    public void Build_Gate_PassesWhenAllBelowThreshold()
    {
        var report = UiResponsivenessReport.Build(
            UiHarnessOptions.Parse(new[] { "--ui-perf-harness", "--ui-perf-fail-on", "critical" }),
            new[] { Samples("good", "Composer", "Type", new[] { 1d, 2d, 3d }) });

        Assert.False(report.GateFailed);
        Assert.Equal(0, report.GateOffenderCount);
        Assert.Contains("GATE: PASS", report.ToConsole());
    }

    [Fact]
    public void Build_Gate_AppearsInJsonSummary()
    {
        var report = UiResponsivenessReport.Build(
            UiHarnessOptions.Parse(new[] { "--ui-perf-harness", "--ui-perf-fail-on", "high" }),
            new[] { Samples("bad", "Navigation", "Cold open", new[] { 10d, 400d }) });

        using var doc = JsonDocument.Parse(report.ToJson());
        var gate = doc.RootElement.GetProperty("summary").GetProperty("gate");

        Assert.Equal("HIGH", gate.GetProperty("level").GetString());
        Assert.True(gate.GetProperty("failed").GetBoolean());
        Assert.Equal(1, gate.GetProperty("offenders").GetInt32());
    }

    [Fact]
    public void Build_IterationsWithStall_CountsHighStallRuns()
    {
        var samples = new UiActionSamples
        {
            ActionId = "scroll",
            Category = "Transcript scroll",
            DisplayName = "Scroll",
        };
        // Three iterations; two have worst stalls at/above the High-stall threshold (150ms).
        samples.IterationMaxMs.AddRange(new[] { 200d, 30d, 160d });
        samples.LatenciesMs.AddRange(new[] { 200d, 30d, 160d });
        samples.RunDurationsMs.AddRange(new[] { 10d, 10d, 10d });
        samples.PostActionDurationsMs.AddRange(new[] { 20d, 20d, 20d });
        samples.Iterations = 3;

        var report = UiResponsivenessReport.Build(
            UiHarnessOptions.Parse(new[] { "--ui-perf-harness" }),
            new[] { samples });

        Assert.Equal(2, report.Actions.Single().IterationsWithStall);
    }

    [Fact]
    public void ToJson_UsesPostActionKeys()
    {
        var report = UiResponsivenessReport.Build(
            UiHarnessOptions.Parse(new[] { "--ui-perf-harness" }),
            new[] { Samples("a", "Chat open", "Open", new[] { 10d, 20d, 30d }, runMs: 100d, postMs: 50d) });

        using var doc = JsonDocument.Parse(report.ToJson());
        var action = doc.RootElement.GetProperty("actions")[0];

        Assert.Equal(50d, action.GetProperty("meanPostActionMs").GetDouble(), 3);
        Assert.Equal(50d, action.GetProperty("maxPostActionMs").GetDouble(), 3);
        Assert.True(action.TryGetProperty("iterationsWithStall", out _));
    }
}
