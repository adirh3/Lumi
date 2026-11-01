using System.Text.Json;
using Lumi.MemoryDiagnostics;
using Xunit;

namespace Lumi.Tests;

public sealed class MemoryRegressionTests
{
    private static MemoryCheckReport[] Runs(int[] values, int start = 20000)
        => values.Select((value, index) => MemoryCheckTests.Complete(ramMiB: value, processId: start + index)).ToArray();

    [Fact]
    public void IdenticalAndMixedNoiseDoesNotRegress()
    {
        var baseline = new MemoryBaseline(1, "fixture", Runs([490, 500, 510, 505, 495]));
        var result = MemoryRegression.Compare(baseline, Runs([505, 495, 500, 490, 510], 30000));
        Assert.Equal("NO REGRESSION", result.Status);
        Assert.DoesNotContain(result.Metrics, metric => metric.Regressed);
    }

    [Fact]
    public void ConsistentRamIncreaseIsDetectedButTheBudgetStaysSeparate()
    {
        var baseline = new MemoryBaseline(1, "fixture", Runs([590, 600, 610, 605, 595]));
        var result = MemoryRegression.Compare(baseline, Runs([650, 660, 670, 665, 655], 30000));
        Assert.Equal("REGRESSION", result.Status);
        Assert.Equal("FAIL", result.BudgetStatus);
        Assert.Contains(result.Metrics, metric => metric.Name == "chats-1 RAM" && metric.Regressed);
    }

    [Fact]
    public void OneOutlierDoesNotBecomeARegression()
    {
        var baseline = new MemoryBaseline(1, "fixture", Runs([500, 500, 500, 500, 500]));
        var result = MemoryRegression.Compare(baseline, Runs([500, 500, 500, 500, 700], 30000));
        Assert.Equal("NO REGRESSION", result.Status);
    }

    [Fact]
    public void KnownLifetimeFailureStaysVisibleAndAdditionalRetentionFails()
    {
        var before = Runs([200, 200, 200, 200, 200]);
        var after = Runs([200, 200, 200, 200, 200], 30000);
        foreach (var run in before.Concat(after))
            Retain(run, 1);
        var baseline = new MemoryBaseline(1, "fixture", before);
        var unchanged = MemoryRegression.Compare(baseline, after);
        Assert.Equal("NO REGRESSION", unchanged.Status);
        Assert.Contains(unchanged.Findings, finding => finding.Contains("not healthy", StringComparison.Ordinal));
        Retain(after[0], 2);
        Assert.Equal("REGRESSION", MemoryRegression.Compare(baseline, after).Status);

        static void Retain(MemoryCheckReport run, int count)
        {
            var original = run.RetentionCycles[0];
            run.RetentionCycles[0] = new()
            {
                Cycle = 0,
                ManagedBytes = original.ManagedBytes,
                PrivateBytes = original.PrivateBytes,
                CommittedBytes = original.CommittedBytes,
                TrackedCount = original.TrackedCount,
                RetainedCount = count,
                RetainedByKind = new Dictionary<string, int> { ["view:Library"] = count },
            };
        }
    }

    [Fact]
    public void RepeatedReportsAreNotIndependentProcesses()
    {
        var run = MemoryCheckTests.Complete();
        Assert.Throws<InvalidDataException>(() => MemoryRegression.ValidateRuns([run, run, run, run, run]));
    }

    [Fact]
    public void FastScreensCannotBeUsedAsCalibratedDeepRuns()
    {
        var fast = Enumerable.Range(0, 5).Select(index =>
            MemoryCheckTests.Complete(processId: 20000 + index, fast: true)).ToArray();
        Assert.All(fast, run => Assert.True(run.Complete));
        Assert.Throws<InvalidDataException>(() => MemoryRegression.ValidateRuns(fast));
    }

    [Fact]
    public void ChangedProfileIdentityIsNotARegressionVerdict()
    {
        var baseline = new MemoryBaseline(1, "fixture", Runs([200, 200, 200, 200, 200]));
        var after = Runs([200, 200, 200, 200, 200], 30000);
        after[0].ProfileFingerprint = new string('e', 64);
        Assert.Throws<InvalidDataException>(() => MemoryRegression.Compare(baseline, after));
    }

    [Fact]
    public void SourceGeneratedRoundTripKeepsSamplesAndCalibration()
    {
        var baseline = new MemoryBaseline(1, "fixture", Runs([200, 200, 200, 200, 200]));
        var json = JsonSerializer.Serialize(baseline, MemoryCheckJsonContext.Default.MemoryBaseline);
        var restored = JsonSerializer.Deserialize(json, MemoryCheckJsonContext.Default.MemoryBaseline)!;
        Assert.Equal(5, restored.Runs.Length);
        Assert.Equal(6, restored.Runs[0].Phases.Count);
        Assert.Equal(12, restored.Runs[0].TargetChats.Count);
        Assert.Equal(9, restored.Runs[0].VisitedPages.Count);
        Assert.Equal(4, restored.Runs[0].RetentionCycles.Count);
        Assert.Equal(21, restored.Runs[0].Phases[0].Samples.Count);
        Assert.NotNull(restored.Runs[0].Phases[0].Samples[0].Runtime);
        Assert.NotNull(restored.Runs[0].Phases[0].Samples[0].Machine);
        Assert.Equal(baseline.Runs[0].ProfileFingerprint, restored.Runs[0].ProfileFingerprint);
        Assert.True(restored.Runs[0].Complete, restored.Runs[0].ToConsole());
        MemoryRegression.ValidateRuns(restored.Runs);
    }

    [Fact]
    public void ExactTiesAndCompleteSeparationHaveKnownProbabilities()
    {
        Assert.Equal(1, MemoryRegression.ProbabilityOfIncrease([1, 1, 1, 1, 1], [1, 1, 1, 1, 1]));
        Assert.Equal(1.0 / 252, MemoryRegression.ProbabilityOfIncrease([1, 2, 3, 4, 5], [6, 7, 8, 9, 10]), 10);
    }

    [Fact]
    public void AnUnchangedOverBudgetRunIsNotReportedHealthy()
    {
        var baseline = new MemoryBaseline(1, "fixture", Runs([600, 600, 600, 600, 600]));
        var result = MemoryRegression.Compare(baseline, Runs([600, 600, 600, 600, 600], 30000));
        Assert.Equal("NO REGRESSION", result.Status);
        Assert.Equal("FAIL", result.BudgetStatus);
    }

    [Fact]
    public void IncompleteWorkCannotBeCompared()
    {
        var after = Runs([200, 200, 200, 200, 200], 30000);
        after[0].RuntimeHomeIsolated = false;
        Assert.Throws<InvalidDataException>(() => MemoryRegression.Compare(
            new(1, "fixture", Runs([200, 200, 200, 200, 200])), after));
    }

    [Fact]
    public void ManagedRetentionRegressionIsDetectedEvenWhenRamIsFlat()
    {
        var after = Enumerable.Range(0, 5).Select(index => MemoryCheckTests.Complete(
            managedMiB: 180, processId: 30000 + index)).ToArray();
        var result = MemoryRegression.Compare(new(1, "fixture", Runs([200, 200, 200, 200, 200])), after);
        Assert.Equal("REGRESSION", result.Status);
        Assert.Contains(result.Metrics, metric => metric.Name == "post-GC live managed" && metric.Regressed);
    }

    [Fact]
    public void MixedApplicationBinariesCannotCalibrateNoise()
    {
        var runs = Runs([200, 200, 200, 200, 200]);
        runs[1].ApplicationSha256 = new string('e', 64);
        Assert.Throws<InvalidDataException>(() => MemoryRegression.ValidateRuns(runs));
    }

    [Fact]
    public void RecordingAndComparingThroughFilesPreservesKnownBudgetFailuresAndNeverOverwritesBaseline()
    {
        var root = Path.Combine(TestProfile.Root, "memory-baseline-files", Guid.NewGuid().ToString("N"));
        var snapshot = Path.Combine(root, "snapshot");
        Directory.CreateDirectory(snapshot);
        File.WriteAllText(Path.Combine(snapshot, "data.json"), "fixed metadata");
        File.WriteAllText(Path.Combine(snapshot, "lumi.db"), "fixed database bytes");
        var manifest = MemoryCheckProfile.Seal(snapshot, "synthetic", "test");
        var baselinePath = Path.Combine(root, "baseline.json");
        var appDll = typeof(App).Assembly.Location;
        var referenceExe = Path.ChangeExtension(appDll, ".exe");
        string[] WriteRuns(string prefix, int start)
        {
            return Runs([600, 600, 600, 600, 600], start).Select((run, index) =>
            {
                run.ProfileFingerprint = manifest.Fingerprint;
                run.ApplicationSha256 = MemoryCheckProfile.FileHash(appDll);
                var path = Path.Combine(root, $"{prefix}-{index}.json");
                File.WriteAllText(path, JsonSerializer.Serialize(run, MemoryCheckJsonContext.Default.MemoryCheckReport));
                return path;
            }).ToArray();
        }
        var calibration = WriteRuns("baseline", 20000);
        var requestPath = Path.Combine(root, "request.json");
        var record = new MemoryRegressionRequest("record", baselinePath, snapshot, calibration,
            Path.Combine(root, "record"), referenceExe);
        File.WriteAllText(requestPath, JsonSerializer.Serialize(record, MemoryCheckJsonContext.Default.MemoryRegressionRequest));
        Assert.Equal(0, MemoryRegression.RunEvaluation(requestPath));
        var saved = File.ReadAllText(baselinePath);
        Assert.Equal(1, MemoryRegression.RunEvaluation(requestPath));
        Assert.Equal(saved, File.ReadAllText(baselinePath));
        var compare = record with
        {
            Mode = "compare",
            ReportFiles = WriteRuns("candidate", 30000),
            ReferenceReportFiles = WriteRuns("reference", 40000),
            OutputDirectory = Path.Combine(root, "compare"),
        };
        File.WriteAllText(requestPath, JsonSerializer.Serialize(compare, MemoryCheckJsonContext.Default.MemoryRegressionRequest));
        Assert.Equal(0, MemoryRegression.RunEvaluation(requestPath));
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "compare", "regression.json")));
        Assert.Equal("NO REGRESSION", result.RootElement.GetProperty("Status").GetString());
        Assert.Equal("FAIL", result.RootElement.GetProperty("BudgetStatus").GetString());
    }
}
