using System.Text;
using System.Text.Json;

namespace Lumi.MemoryDiagnostics;

public sealed record MemoryBaseline(int Version, string SnapshotDirectory, MemoryCheckReport[] Runs, string ReferenceExecutable = "");
public sealed record MemoryRegressionRequest(string Mode, string BaselinePath, string SnapshotDirectory,
    string[] ReportFiles, string OutputDirectory, string ReferenceExecutable = "", string[]? ReferenceReportFiles = null);
public sealed record MemoryRegressionMetric(string Name, double BaselineMedianBytes, double CurrentMedianBytes,
    double BaselineMinBytes, double BaselineMaxBytes, double CurrentMinBytes, double CurrentMaxBytes,
    double Probability, bool Regressed);

public sealed class MemoryRegressionResult
{
    public string Status { get; set; } = "INCOMPLETE";
    public string BudgetStatus { get; set; } = "UNKNOWN";
    public List<MemoryRegressionMetric> Metrics { get; } = [];
    public List<string> Findings { get; } = [];
    public List<string> Errors { get; } = [];
    public int ExitCode => Status is "RECORDED" or "NO REGRESSION" ? 0 : Status == "REGRESSION" ? 3 : 1;

    public string ToConsole()
    {
        var text = new StringBuilder();
        text.AppendLine($"Lumi memory regression: {Status}; absolute workload budgets: {BudgetStatus}.");
        text.AppendLine("Regression exit 0 means no new regression / baseline recorded, not that the 500 MiB goal is met.");
        foreach (var metric in Metrics)
            text.AppendLine($"  {metric.Name}: baseline median {metric.BaselineMedianBytes / 1048576:0.0} MiB " +
                $"[{metric.BaselineMinBytes / 1048576:0.0}, {metric.BaselineMaxBytes / 1048576:0.0}]; " +
                $"current {metric.CurrentMedianBytes / 1048576:0.0} MiB " +
                $"[{metric.CurrentMinBytes / 1048576:0.0}, {metric.CurrentMaxBytes / 1048576:0.0}]; " +
                $"one-sided exact p={metric.Probability:0.00000}{(metric.Regressed ? " REGRESSION" : "")}");
        foreach (var finding in Findings) text.AppendLine("  " + finding);
        foreach (var error in Errors) text.AppendLine("  INCOMPLETE: " + error);
        text.AppendLine("Scope: frozen-profile headed reading/navigation. Five independent processes, exact rank comparison with family-wise correction; not a universal small-delta or inference/browser/voice certificate.");
        return text.ToString();
    }
}

public static class MemoryRegression
{
    public const int RunsRequired = 5;

    public static void ValidateRuns(IReadOnlyList<MemoryCheckReport> runs)
    {
        if (runs.Count != RunsRequired || runs.Any(run => !run.Complete || run.Options.Fast))
            throw new InvalidDataException("Exactly five complete fresh-process reports are required.");
        if (runs[0].Options.Rounds > 5)
            throw new InvalidDataException("Five-run calibration supports at most five workload rounds; more comparisons would exceed this exact test's resolution.");
        if (runs.Select(run => (run.ProcessId, run.StartedAt)).Distinct().Count() != RunsRequired)
            throw new InvalidDataException("Repeated reports / rounds in one process are not independent runs.");
        var first = runs[0];
        foreach (var run in runs)
        {
            if (run.ApplicationSha256 != first.ApplicationSha256 || run.CliSha256 != first.CliSha256)
                throw new InvalidDataException("A calibration/candidate group contains different application or Copilot binaries.");
            ValidateComparable(first, run);
            run.Evaluate();
        }
    }

    private static void ValidateComparable(MemoryCheckReport baseline, MemoryCheckReport current)
    {
        if (baseline.SchemaVersion != current.SchemaVersion || baseline.ProfileFingerprint != current.ProfileFingerprint
            || !baseline.TargetChats.SequenceEqual(current.TargetChats) || !baseline.ExpectedPhases.SequenceEqual(current.ExpectedPhases)
            || baseline.ProfileChats != current.ProfileChats || baseline.ProfileMessages != current.ProfileMessages
            || baseline.WindowWidth != current.WindowWidth || baseline.WindowHeight != current.WindowHeight
            || baseline.RenderScaling != current.RenderScaling || baseline.Theme != current.Theme
            || baseline.MachineName != current.MachineName || baseline.Architecture != current.Architecture
            || baseline.OperatingSystem != current.OperatingSystem || baseline.ProcessorCount != current.ProcessorCount
            || baseline.RuntimeVersion != current.RuntimeVersion || baseline.ServerGc != current.ServerGc
            || baseline.RuntimeConfigurationSha256 != current.RuntimeConfigurationSha256
            || baseline.Options.Chats != current.Options.Chats || baseline.Options.Rounds != current.Options.Rounds
            || baseline.Options.SampleSeconds != current.Options.SampleSeconds
            || baseline.Options.MaxRamBytes != current.Options.MaxRamBytes
            || baseline.Options.MaxPrivateBytes != current.Options.MaxPrivateBytes
            || baseline.Options.MaxManagedBytes != current.Options.MaxManagedBytes)
            throw new InvalidDataException("Baseline and candidate must match snapshot/chat order, workload, machine, runtime configuration, geometry and budgets.");
        bool Pressure(MemoryRamSample sample) => sample.Runtime!.HighMemoryLoadThresholdBytes > 0
            && sample.Machine!.TotalPhysicalBytes - sample.Machine.AvailablePhysicalBytes >= sample.Runtime.HighMemoryLoadThresholdBytes;
        if (baseline.Phases.SelectMany(phase => phase.Samples).Any(Pressure)
            != current.Phases.SelectMany(phase => phase.Samples).Any(Pressure))
            throw new InvalidDataException("Baseline/candidate crossed the runtime's high-memory-pressure boundary; do not call paging/GC pressure a code regression.");
    }

    public static MemoryRegressionResult Compare(MemoryBaseline baseline, IReadOnlyList<MemoryCheckReport> current)
    {
        if (baseline.Version != 1) throw new InvalidDataException("Unsupported memory baseline version.");
        ValidateRuns(baseline.Runs);
        ValidateRuns(current);
        ValidateComparable(baseline.Runs[0], current[0]);
        var result = new MemoryRegressionResult
        {
            Status = "NO REGRESSION",
            BudgetStatus = current.All(run => run.Passed) ? "PASS" : "FAIL",
        };
        var selectors = baseline.Runs[0].ExpectedPhases.Select(id =>
            (Name: id + " RAM", Read: (Func<MemoryCheckReport, double>)(run => run.Phases.Single(phase => phase.Id == id).MeanRamBytes)))
            .Append(("peak private commitment", run => run.Phases.Max(phase => (double)phase.PeakPrivateBytes)))
            .Append(("post-GC live managed", run => run.RetentionCycles.Max(cycle => (double)cycle.ManagedBytes))).ToArray();
        foreach (var (name, read) in selectors)
        {
            var before = baseline.Runs.Select(read).ToArray();
            var after = current.Select(read).ToArray();
            var probability = ProbabilityOfIncrease(before, after);
            var regressed = Median(after) > Median(before) && probability <= 0.05 / selectors.Length;
            result.Metrics.Add(new(name, Median(before), Median(after), before.Min(), before.Max(),
                after.Min(), after.Max(), probability, regressed));
        }
        var known = baseline.Runs.SelectMany(run => run.RetentionCycles).SelectMany(cycle => cycle.RetainedByKind)
            .GroupBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Max(pair => pair.Value));
        var observed = current.SelectMany(run => run.RetentionCycles).SelectMany(cycle => cycle.RetainedByKind)
            .GroupBy(pair => pair.Key, StringComparer.Ordinal);
        foreach (var group in observed)
        {
            var count = group.Max(pair => pair.Value);
            var prior = known.GetValueOrDefault(group.Key);
            if (count > prior)
            {
                result.Findings.Add($"New/increased expected-dead retention: {group.Key} {prior}->{count}.");
                result.Status = "REGRESSION";
            }
            else if (count > 0)
                result.Findings.Add($"Known baseline lifetime failure remains: {group.Key} ({count}). It is not healthy.");
        }
        if (result.Metrics.Any(metric => metric.Regressed)) result.Status = "REGRESSION";
        if (baseline.Runs[0].CliSha256 != current[0].CliSha256)
            result.Findings.Add("Copilot binary changed: this is a whole-product comparison, not app-only attribution.");
        return result;
    }

    public static double Median(IEnumerable<double> samples)
    {
        var values = samples.Order().ToArray();
        if (values.Length == 0) throw new ArgumentException("A median needs samples.", nameof(samples));
        return values.Length % 2 == 0 ? (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2 : values[values.Length / 2];
    }

    public static double ProbabilityOfIncrease(IReadOnlyList<double> before, IReadOnlyList<double> after)
    {
        if (before.Count != RunsRequired || after.Count != RunsRequired)
            throw new ArgumentException("The exact comparison requires five independent runs in each group.");
        var combined = before.Select(value => (Value: value, Current: false))
            .Concat(after.Select(value => (Value: value, Current: true))).OrderBy(item => item.Value).ToArray();
        var ranks = new double[combined.Length];
        for (var index = 0; index < combined.Length;)
        {
            var end = index + 1;
            while (end < combined.Length && combined[end].Value == combined[index].Value) end++;
            var rank = (index + 1 + end) / 2.0;
            for (; index < end; index++) ranks[index] = rank;
        }
        var observed = Enumerable.Range(0, combined.Length).Where(index => combined[index].Current).Sum(index => ranks[index]);
        var total = 0;
        var tail = 0;
        Count(0, RunsRequired, 0);
        return (double)tail / total;

        void Count(int offset, int remaining, double sum)
        {
            if (remaining == 0)
            {
                total++;
                if (sum >= observed) tail++;
                return;
            }
            for (var index = offset; index <= ranks.Length - remaining; index++)
                Count(index + 1, remaining - 1, sum + ranks[index]);
        }
    }

    public static int RunEvaluation(string requestPath)
    {
        var result = new MemoryRegressionResult();
        string? output = null;
        try
        {
            var request = JsonSerializer.Deserialize(File.ReadAllText(requestPath), MemoryCheckJsonContext.Default.MemoryRegressionRequest)
                ?? throw new InvalidDataException("No regression request.");
            output = request.OutputDirectory;
            var runs = request.ReportFiles.Select(path => JsonSerializer.Deserialize(File.ReadAllText(path),
                MemoryCheckJsonContext.Default.MemoryCheckReport) ?? throw new InvalidDataException($"No report: {path}")).ToArray();
            ValidateRuns(runs);
            if (request.Mode == "record")
            {
                if (runs.Any(run => run.Options.InjectRetainedMiB != 0))
                    throw new InvalidDataException("Diagnostic memory-fault injection cannot be recorded as a baseline.");
                if (File.Exists(request.BaselinePath))
                    throw new IOException("The baseline already exists. It is never automatically overwritten or ratcheted upward.");
                var snapshot = MemoryCheckProfile.VerifySnapshot(request.SnapshotDirectory);
                if (snapshot.Fingerprint != runs[0].ProfileFingerprint)
                    throw new InvalidDataException("The baseline snapshot and measured profiles do not match.");
                if (MemoryCheckProfile.FileHash(Path.ChangeExtension(request.ReferenceExecutable, ".dll")) != runs[0].ApplicationSha256)
                    throw new InvalidDataException("The archived reference binary is not the calibrated application.");
                var baseline = new MemoryBaseline(1, Path.GetFullPath(request.SnapshotDirectory), runs, request.ReferenceExecutable);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.BaselinePath))!);
                using (var file = new FileStream(request.BaselinePath, FileMode.CreateNew, FileAccess.Write))
                    JsonSerializer.Serialize(file, baseline, MemoryCheckJsonContext.Default.MemoryBaseline);
                result.Status = "RECORDED";
                result.BudgetStatus = runs.All(run => run.Passed) ? "PASS" : "FAIL";
                result.Findings.Add($"Recorded five fresh runs. Normal RAM median {Median(runs.Select(run => run.MeanRamBytes)) / 1048576:0.0} MiB; min/max {runs.Min(run => run.MeanRamBytes) / 1048576:0.0}/{runs.Max(run => run.MeanRamBytes) / 1048576:0.0} MiB.");
            }
            else if (request.Mode == "compare")
            {
                var baseline = JsonSerializer.Deserialize(File.ReadAllText(request.BaselinePath),
                    MemoryCheckJsonContext.Default.MemoryBaseline) ?? throw new InvalidDataException("No saved baseline.");
                if (MemoryCheckProfile.VerifySnapshot(baseline.SnapshotDirectory).Fingerprint != runs[0].ProfileFingerprint)
                    throw new InvalidDataException("The saved baseline snapshot changed.");
                var reference = request.ReferenceReportFiles?.Select(path => JsonSerializer.Deserialize(File.ReadAllText(path),
                    MemoryCheckJsonContext.Default.MemoryCheckReport) ?? throw new InvalidDataException($"No reference report: {path}")).ToArray()
                    ?? throw new InvalidDataException("Fresh interleaved reference runs are required; a historical RAM number alone cannot control today's environment.");
                ValidateRuns(reference);
                ValidateComparable(baseline.Runs[0], reference[0]);
                if (reference[0].ApplicationSha256 != baseline.Runs[0].ApplicationSha256 || reference[0].CliSha256 != baseline.Runs[0].CliSha256)
                    throw new InvalidDataException("The reference runs did not use the saved calibrated binaries.");
                result = Compare(new MemoryBaseline(1, baseline.SnapshotDirectory, reference), runs);
                result.Findings.Add($"Historical reference RAM median {Median(baseline.Runs.Select(run => run.MeanRamBytes)) / 1048576:0.0} MiB; fresh reference median {Median(reference.Select(run => run.MeanRamBytes)) / 1048576:0.0} MiB. The regression decision uses fresh interleaved reference processes, not an unpaired old number.");
            }
            else throw new InvalidDataException("Regression mode must be record or compare.");
        }
        catch (Exception error) { result.Errors.Add(error.ToString()); result.Status = "INCOMPLETE"; }
        Console.WriteLine(result.ToConsole());
        try
        {
            if (output is not null)
            {
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "regression.json"), JsonSerializer.Serialize(result, MemoryCheckJsonContext.Default.MemoryRegressionResult));
                File.WriteAllText(Path.Combine(output, "regression.txt"), result.ToConsole());
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Cannot write regression evidence: " + error);
            return 1;
        }
        return result.ExitCode;
    }
}
