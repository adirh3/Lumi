using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumi.MemoryDiagnostics;

public sealed record MemoryProcessSample(int ProcessId, int ParentProcessId, string Name,
    long PrivateWorkingSetBytes, long WorkingSetBytes, long PrivateBytes);

public sealed record MemoryRamSample(double ElapsedSeconds, IReadOnlyList<MemoryProcessSample> Processes)
{
    public MemoryRuntimeSample? Runtime { get; init; }
    public MemoryMachineSample? Machine { get; init; }
    public long RamBytes => Processes.Sum(process => process.PrivateWorkingSetBytes);
    public long PrivateBytes => Processes.Sum(process => process.PrivateBytes);
}

public sealed record MemoryRuntimeSample(long AllocatedBytes, long CommittedBytes, long HeapBytes,
    long MemoryLoadBytes, long HighMemoryLoadThresholdBytes, int Gen0, int Gen1, int Gen2)
{
    public static MemoryRuntimeSample Capture()
    {
        var info = GC.GetGCMemoryInfo();
        return new(GC.GetTotalAllocatedBytes(), info.TotalCommittedBytes, info.HeapSizeBytes,
            info.MemoryLoadBytes, info.HighMemoryLoadThresholdBytes,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }
}

public sealed record MemoryMachineSample(uint MemoryLoadPercent, long TotalPhysicalBytes, long AvailablePhysicalBytes,
    long IdleCpuTicks, long TotalCpuTicks);

public sealed record MemoryCheckState(int OpenedChats, int LoadedTails, int LiveTailMessages,
    int CachedSurfaces, int MountedTurns, int RealizedHosts, int VisibleControls);
public sealed record MemoryCheckTiming(string Stage, double Milliseconds);

public sealed class MemoryCheckPhase
{
    public required string Id { get; init; }
    public required MemoryCheckState State { get; init; }
    public List<MemoryRamSample> Samples { get; init; } = [];
    public int RenderedFrames { get; init; }
    public double MeanRamBytes => Samples.Count == 0 ? 0 : Samples.Average(sample => (double)sample.RamBytes);
    public long PeakRamBytes => Samples.Count == 0 ? 0 : Samples.Max(sample => sample.RamBytes);
    public long PeakPrivateBytes => Samples.Count == 0 ? 0 : Samples.Max(sample => sample.PrivateBytes);
}

/// <summary>A complete workload can fail its budget; an incomplete workload has no valid verdict.</summary>
public sealed class MemoryCheckReport
{
    public int SchemaVersion => Options.Fast ? 3 : 2;
    public string Mode => Options.Fast ? "Fast" : "Deep";
    public string Policy => Options.CiGuardrails ? "CI large-regression guardrails" : "Strict developer budgets";
    public required MemoryCheckOptions Options { get; init; }
    public required IReadOnlyList<string> ExpectedPhases { get; init; }
    public string Build { get; init; } = "";
    public string OperatingSystem { get; init; } = RuntimeInformation.OSDescription;
    public string Architecture { get; init; } = RuntimeInformation.ProcessArchitecture.ToString();
    public int ProcessId { get; init; } = Environment.ProcessId;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public string ProfileKind { get; set; } = "";
    public int ProfileChats { get; init; }
    public long ProfileMessages { get; init; }
    public string CorpusVersion { get; set; } = "";
    public int MaximumCachedSurfaces { get; init; }
    public bool RuntimeConnected { get; set; }
    public bool RuntimeHomeIsolated { get; set; }
    public string RuntimeHome { get; set; } = "";
    public string CliSha256 { get; set; } = "";
    public string ProfileFingerprint { get; set; } = "";
    public string MachineName { get; init; } = Environment.MachineName;
    public int ProcessorCount { get; init; } = Environment.ProcessorCount;
    public string RuntimeVersion { get; init; } = RuntimeInformation.FrameworkDescription;
    public bool ServerGc { get; init; } = System.Runtime.GCSettings.IsServerGC;
    public string RuntimeConfigurationSha256 { get; set; } = "";
    public string ApplicationSha256 { get; set; } = "";
    public List<Guid> TargetChats { get; init; } = [];
    public bool LibraryScanCompleted { get; set; }
    public int LibraryScannedChats { get; set; }
    public int LibraryItems { get; set; }
    public int LibraryVisibleItems { get; set; }
    public bool StoredHistoryRendered { get; set; }
    public List<int> VisitedPages { get; init; } = [];
    public List<int> VisitedSettingsPages { get; init; } = [];
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public double RenderScaling { get; set; }
    public string Theme { get; set; } = "";
    public List<MemoryCheckPhase> Phases { get; init; } = [];
    public List<MemoryCycleSample> RetentionCycles { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    public List<string> BudgetFailures { get; init; } = [];
    public List<string> Advisories { get; init; } = [];
    public List<MemoryCheckTiming> Timings { get; init; } = [];
    public double MeanRamBytes => Phases.Count == 0 ? 0 : Phases.Average(phase => phase.MeanRamBytes);
    public bool Complete => Options.Enabled && RuntimeConnected && RuntimeHomeIsolated && CliSha256.Length == 64
        && (!Options.CiGuardrails || Options.Fast && ProfileKind == "synthetic")
        && ProfileFingerprint.Length == 64 && TargetChats.Count == Options.Chats && TargetChats.Distinct().Count() == Options.Chats
        && RuntimeConfigurationSha256.Length == 64 && ApplicationSha256.Length == 64 && StoredHistoryRendered
        && (!Options.Fast || LibraryScanCompleted && LibraryScannedChats == ProfileChats
            && (LibraryItems == 0 || LibraryVisibleItems > 0))
        && ProfileChats >= Options.Chats && ProfileMessages > 0 && WindowWidth > 0 && WindowHeight > 0 && RenderScaling > 0
        && VisitedPages.Order().SequenceEqual(Enumerable.Range(1, 9))
        && VisitedSettingsPages.Order().SequenceEqual(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 9, 10 })
        && Errors.Count == 0 && ExpectedPhases.Count == (Options.Fast ? 2 : Options.Rounds * 2)
        && ExpectedPhases.Distinct(StringComparer.Ordinal).Count() == ExpectedPhases.Count
        && ExpectedPhases.Order(StringComparer.Ordinal).SequenceEqual(Phases.Select(phase => phase.Id).Order(StringComparer.Ordinal))
        && Phases.All(phase => phase.Samples.Count >= Options.SampleSeconds * 2
            && phase.RenderedFrames > 0
            && phase.Samples[^1].ElapsedSeconds >= Options.SampleSeconds
            && phase.Samples.All(sample => sample.Processes.Any(process => process.ProcessId == ProcessId)
                && sample.Runtime is { AllocatedBytes: > 0, CommittedBytes: > 0 }
                && sample.Machine is { TotalPhysicalBytes: > 0, AvailablePhysicalBytes: >= 0 }
                && sample.Processes.Any(process => process.Name.Equals("copilot.exe", StringComparison.OrdinalIgnoreCase))
                && sample.Processes.All(process => process.PrivateBytes > 0 && process.WorkingSetBytes > 0 && process.PrivateWorkingSetBytes >= 0))
            && phase.State.OpenedChats == Options.Chats && phase.State.MountedTurns > 0
            && phase.State.RealizedHosts > 0 && phase.State.VisibleControls > 0)
        && RetentionCycles.Count == (Options.Fast ? 1 : Options.Rounds + 1)
        && RetentionCycles.Select(cycle => cycle.Cycle).SequenceEqual(Enumerable.Range(0, Options.Fast ? 1 : Options.Rounds + 1))
        && RetentionCycles.All(cycle => cycle.TrackedCount > 0 && cycle.ManagedBytes > 0
            && cycle.CommittedBytes > 0 && cycle.PrivateBytes >= cycle.CommittedBytes);
    public bool Passed => Complete && BudgetFailures.Count == 0;
    public int ExitCode => !Complete ? 1 : Passed ? 0 : 3;

    public void Evaluate()
    {
        BudgetFailures.Clear();
        Advisories.Clear();
        if (!Complete)
            return;
        foreach (var phase in Phases)
        {
            if (phase.MeanRamBytes > Options.MaxRamBytes)
                BudgetFailures.Add($"{phase.Id}: average private resident RAM {MiB(phase.MeanRamBytes):0.0} MiB exceeds {MiB(Options.MaxRamBytes):0.0} MiB.");
            if (phase.PeakPrivateBytes > Options.MaxPrivateBytes)
                BudgetFailures.Add($"{phase.Id}: private commitment {MiB(phase.PeakPrivateBytes):0.0} MiB exceeds {MiB(Options.MaxPrivateBytes):0.0} MiB.");
            if (phase.State.CachedSurfaces > MaximumCachedSurfaces || phase.State.LoadedTails > MaximumCachedSurfaces)
                LifetimeFinding($"{phase.Id}: idle reading retains more surfaces/tails than the configured active-plus-cache bound of {MaximumCachedSurfaces}.");
            if (Options.CiGuardrails && phase.MeanRamBytes > 500 * 1024L * 1024)
                Advisories.Add($"{phase.Id}: the 500 MiB developer RAM goal is not met. CI only fails its separately declared 1024 MiB large-regression guardrail.");
        }
        if (RetentionCycles.Max(cycle => cycle.ManagedBytes) > Options.MaxManagedBytes)
            BudgetFailures.Add($"Post-GC managed memory exceeds {MiB(Options.MaxManagedBytes):0.0} MiB.");
        if (RetentionCycles.Any(cycle => cycle.RetainedCount > 0))
            LifetimeFinding("Released management/settings views or evicted chat surfaces remain reachable after GC; inspect the named owners.");
        if (Options.Fast)
            return;
        var growth = RetentionCycles[^1].ManagedBytes - RetentionCycles[0].ManagedBytes;
        var slope = MemoryStressReport.LinearSlope(RetentionCycles.Select(cycle => (double)cycle.ManagedBytes));
        if (growth > 24 * 1024L * 1024 && slope > 2 * 1024L * 1024)
            BudgetFailures.Add($"Post-GC managed memory keeps growing: {MiB(growth):0.0} MiB, {MiB(slope):0.0} MiB/round.");
        // GC commitment can swing far beyond live managed bytes. It is capacity, not evidence that
        // non-GC resources leaked; the absolute commitment budget still includes it.
        var privateOutsideGc = RetentionCycles.Select(cycle => cycle.PrivateBytes - cycle.CommittedBytes).ToArray();
        var privateGrowth = privateOutsideGc[^1] - privateOutsideGc[0];
        var privateSlope = MemoryStressReport.LinearSlope(privateOutsideGc.Select(bytes => (double)bytes));
        if (privateGrowth > 32 * 1024L * 1024 && privateSlope > 4 * 1024L * 1024)
            BudgetFailures.Add($"Private commitment outside the GC heap keeps growing: {MiB(privateGrowth):0.0} MiB, {MiB(privateSlope):0.0} MiB/round.");

        void LifetimeFinding(string message)
        {
            if (Options.CiGuardrails) Advisories.Add(message);
            else BudgetFailures.Add(message);
        }
    }

    public string ToConsole()
    {
        var text = new StringBuilder();
        text.AppendLine($"Lumi memory check ({Mode}): {(Passed ? "PASS" : Complete ? "BUDGET FAIL" : "INCOMPLETE")} ({Build}, {Architecture})");
        text.AppendLine($"Policy: {Policy}; RAM {MiB(Options.MaxRamBytes):0} MiB, commitment {MiB(Options.MaxPrivateBytes):0} MiB, post-GC managed {MiB(Options.MaxManagedBytes):0} MiB.");
        if (Options.CiGuardrails)
            text.AppendLine("CI PASS means the coarse memory guardrails passed, not the 500 MiB goal or every lifetime invariant. Smaller owner/cache/goal findings stay advisory; missing coverage is always INCOMPLETE.");
        text.AppendLine($"Profile: {ProfileKind}, {ProfileChats:N0} chats / {ProfileMessages:N0} messages; opened {Options.Chats} distinct chats.");
        text.AppendLine($"Window: {WindowWidth:0} x {WindowHeight:0}, scaling {RenderScaling:0.##}, {Theme}.");
        text.AppendLine($"Copilot home isolated: {RuntimeHomeIsolated}; CLI SHA-256: {CliSha256}.");
        text.AppendLine($"Frozen profile: {ProfileFingerprint}; runtime {RuntimeVersion}; machine {MachineName}; server GC {ServerGc}.");
        text.AppendLine("RAM = Windows private working set of Lumi and its current child processes. Commitment and post-GC managed bytes are separate; do not add them.");
        foreach (var phase in Phases)
        {
            text.AppendLine($"  {phase.Id,-24} average {MiB(phase.MeanRamBytes),6:0.0} MiB; peak {MiB(phase.PeakRamBytes),6:0.0}; commitment {MiB(phase.PeakPrivateBytes),6:0.0}; tails {phase.State.LoadedTails}, surfaces {phase.State.CachedSurfaces}");
            if (phase.Samples.Count > 0 && phase.Samples[0].Runtime is { } first && phase.Samples[^1].Runtime is { } last)
                text.AppendLine($"    window allocations {MiB(last.AllocatedBytes - first.AllocatedBytes):0.0} MiB; Gen2 {first.Gen2}->{last.Gen2}; GC commitment {MiB(first.CommittedBytes):0.0}->{MiB(last.CommittedBytes):0.0} MiB; machine memory load {phase.Samples[0].Machine?.MemoryLoadPercent}->{phase.Samples[^1].Machine?.MemoryLoadPercent}%");
        }
        foreach (var cycle in RetentionCycles)
        {
            text.AppendLine($"  retention {cycle.Cycle}: live managed {MiB(cycle.ManagedBytes):0.0} MiB; GC committed {MiB(cycle.CommittedBytes):0.0} MiB; process committed {MiB(cycle.PrivateBytes):0.0} MiB; expected-dead objects retained {cycle.RetainedCount}/{cycle.TrackedCount}");
            foreach (var kind in cycle.RetainedByKind)
                text.AppendLine($"    retained {kind.Key}: {kind.Value}");
        }
        foreach (var failure in BudgetFailures.Concat(Errors))
            text.AppendLine("  FAIL: " + failure);
        foreach (var advisory in Advisories)
            text.AppendLine("  ADVISORY: " + advisory);
        foreach (var timing in Timings)
            text.AppendLine($"  timing {timing.Stage}: {timing.Milliseconds / 1000:0.0}s");
        if (Options.Fast)
        {
            text.AppendLine($"Fast Library coverage: completed scan of {LibraryScannedChats} chats; {LibraryItems} items, {LibraryVisibleItems} visible item controls.");
            text.AppendLine("Fast coverage: one complete chat/history/page pass and a post-GC owner/bloat check. No repeated-growth or statistical baseline verdict; use Deep / Compare for those.");
        }
        text.AppendLine("Scope: reading/switching chats, stored history and management/settings navigation. No model inference, online catalogs, browser pages, dictation or concurrent agents.");
        return text.ToString();
    }

    public string ToJson() => JsonSerializer.Serialize(this, MemoryCheckJsonContext.Default.MemoryCheckReport);
    private static double MiB(double bytes) => bytes / (1024 * 1024);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(MemoryCheckReport))]
[JsonSerializable(typeof(MemoryProfileManifest))]
[JsonSerializable(typeof(MemoryBaseline))]
[JsonSerializable(typeof(MemoryRegressionRequest))]
[JsonSerializable(typeof(MemoryRegressionResult))]
internal partial class MemoryCheckJsonContext : JsonSerializerContext;
