using Lumi.MemoryDiagnostics;
using Xunit;

namespace Lumi.Tests;

public sealed class MemoryCheckTests
{
    [Theory]
    [InlineData("--memory-check-chats", "0")]
    [InlineData("--memory-check-rounds", "1")]
    [InlineData("--memory-check-sample-seconds", "-1")]
    [InlineData("--memory-check-max-ram-mib", "NaN")]
    [InlineData("--memory-check-unknown", "10")]
    public void InvalidOptionsFailInsteadOfSilentlyUsingDefaults(string flag, string value)
        => Assert.Throws<ArgumentException>(() => MemoryCheckOptions.Parse(["--memory-check", flag, value]));

    [Fact]
    public void CheckOptionsRequireTheCheckFlag()
        => Assert.Throws<ArgumentException>(() => MemoryCheckOptions.Parse(["--memory-check-chats", "12"]));

    [Fact]
    public void OrdinaryStartupDoesNotEnableMemoryDiagnostics()
        => Assert.False(MemoryCheckOptions.Parse([]).Enabled);

    [Fact]
    public void FastIsExplicitInTheAppProtocolAndDeepRemainsBackwardCompatible()
    {
        Assert.False(MemoryCheckOptions.Parse(["--memory-check"]).Fast);
        Assert.True(MemoryCheckOptions.Parse(["--memory-check", "--memory-check-fast", "true"]).Fast);
        Assert.Throws<ArgumentException>(() => MemoryCheckOptions.Parse(["--memory-check", "--memory-check-fast", "maybe"]));
    }

    [Fact]
    public void CiPolicyDeclaresFixedLargeGuardrailsAndDoesNotChangeStrictDefaults()
    {
        var ci = MemoryCheckOptions.Parse(["--memory-check", "--memory-check-fast", "true", "--memory-check-ci", "true"]);
        Assert.Equal(1024 * 1024L * 1024, ci.MaxRamBytes);
        Assert.Equal(2048 * 1024L * 1024, ci.MaxPrivateBytes);
        Assert.Equal(200 * 1024L * 1024, ci.MaxManagedBytes);
        Assert.Equal(500 * 1024L * 1024, MemoryCheckOptions.Parse(["--memory-check"]).MaxRamBytes);
        Assert.Throws<ArgumentException>(() => MemoryCheckOptions.Parse(["--memory-check", "--memory-check-ci", "true"]));
        Assert.Throws<ArgumentException>(() => MemoryCheckOptions.Parse(["--memory-check", "--memory-check-fast", "true",
            "--memory-check-ci", "true", "--memory-check-max-ram-mib", "500"]));
    }

    [Fact]
    public void CiReportsSmallerGoalAndLifetimeFindingsWithoutFailingItsCoarseGuardrails()
    {
        var report = Complete(ramMiB: 700, privateMiB: 1200, retained: 1, fast: true, ci: true);
        report.Evaluate();
        Assert.True(report.Passed);
        Assert.NotEmpty(report.Advisories);
        Assert.Contains(report.Advisories, advisory => advisory.Contains("500 MiB developer RAM goal", StringComparison.Ordinal));
        Assert.Contains(report.Advisories, advisory => advisory.Contains("remain reachable", StringComparison.Ordinal));
        Assert.Contains("not the 500 MiB goal", report.ToConsole(), StringComparison.Ordinal);
        var original = report.Phases[0];
        var oversized = new MemoryCheckPhase
        {
            Id = original.Id,
            State = original.State with { CachedSurfaces = 12 },
            RenderedFrames = original.RenderedFrames,
        };
        oversized.Samples.AddRange(original.Samples);
        report.Phases[0] = oversized;
        report.Evaluate();
        Assert.True(report.Passed);
        Assert.Contains(report.Advisories, advisory => advisory.Contains("active-plus-cache bound", StringComparison.Ordinal));
        report.VisitedPages.Remove(8);
        report.Evaluate();
        Assert.Equal(1, report.ExitCode);
    }

    [Fact]
    public void CiPolicyCannotCertifyAPrivateProfile()
    {
        var report = Complete(fast: true, ci: true);
        report.ProfileKind = "private profile copy";
        report.Evaluate();
        Assert.Equal(1, report.ExitCode);
    }

    [Theory]
    [InlineData(1100, 1200, 100)]
    [InlineData(700, 2300, 100)]
    [InlineData(700, 1200, 250)]
    public void CiFailsLargeMemoryBloat(int ramMiB, int privateMiB, int managedMiB)
    {
        var report = Complete(ramMiB: ramMiB, privateMiB: privateMiB, managedMiB: managedMiB, fast: true, ci: true);
        report.Evaluate();
        Assert.True(report.Complete);
        Assert.Equal(3, report.ExitCode);
    }

    [Fact]
    public void FastRequiresTheFullDeclaredCoverageButOnlyOneRetentionCheckpoint()
    {
        var report = Complete(fast: true);
        report.Evaluate();
        Assert.True(report.Passed);
        Assert.Equal(3, report.SchemaVersion);
        Assert.Equal(2, report.Phases.Count);
        Assert.Single(report.RetentionCycles);
        Assert.Equal(12, report.TargetChats.Count);
        Assert.Equal(9, report.VisitedPages.Count);
        Assert.Equal(10, report.VisitedSettingsPages.Count);
        Assert.Contains("No repeated-growth or statistical baseline verdict", report.ToConsole(), StringComparison.Ordinal);
        report.VisitedPages.Remove(8);
        report.Evaluate();
        Assert.Equal(1, report.ExitCode);
    }

    [Fact]
    public void FastCannotClaimLibraryCoverageFromALoadingOrUnpopulatedPage()
    {
        var report = Complete(fast: true);
        report.LibraryScanCompleted = false;
        report.Evaluate();
        Assert.Equal(1, report.ExitCode);
        report.LibraryScanCompleted = true;
        report.LibraryItems = 10;
        report.LibraryVisibleItems = 0;
        report.Evaluate();
        Assert.Equal(1, report.ExitCode);
    }

    [Theory]
    [InlineData("ram")]
    [InlineData("managed")]
    [InlineData("retention")]
    public void FastDoesNotWeakenTheAbsoluteOrLifetimeGates(string failure)
    {
        var report = Complete(ramMiB: failure == "ram" ? 600 : 200,
            managedMiB: failure == "managed" ? 250 : 100, retained: failure == "retention" ? 1 : 0, fast: true);
        report.Evaluate();
        Assert.True(report.Complete);
        Assert.Equal(3, report.ExitCode);
    }

    [Fact]
    public void ACompleteHealthyRunPasses()
    {
        var report = Complete();
        report.Evaluate();
        Assert.True(report.Complete);
        Assert.True(report.Passed);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void StableHighRamFailsEvenWithoutGrowthOrRetainedViews()
    {
        var report = Complete(ramMiB: 600);
        report.Evaluate();
        Assert.True(report.Complete);
        Assert.False(report.Passed);
        Assert.Equal(3, report.ExitCode);
        Assert.Equal(6, report.BudgetFailures.Count);
    }

    [Fact]
    public void CommitmentIsNotAddedToResidentRam()
    {
        var report = Complete(ramMiB: 200, privateMiB: 800);
        report.Evaluate();
        Assert.True(report.Passed);
        Assert.Equal(200 * 1024L * 1024, report.MeanRamBytes);
    }

    [Fact]
    public void StableManagedBloatFails()
    {
        var report = Complete(managedMiB: 250);
        report.Evaluate();
        Assert.Equal(3, report.ExitCode);
        Assert.Contains(report.BudgetFailures, error => error.Contains("Post-GC managed memory exceeds", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExpectedDeadViewFailsEvenWithFlatMemory()
    {
        var report = Complete(retained: 1);
        report.Evaluate();
        Assert.Equal(3, report.ExitCode);
        Assert.Contains(report.BudgetFailures, error => error.Contains("remain reachable", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("phase")]
    [InlineData("samples")]
    [InlineData("runtime")]
    [InlineData("runtime-home")]
    [InlineData("page")]
    [InlineData("history")]
    [InlineData("retention")]
    [InlineData("error")]
    public void MissingWorkIsIncompleteNotAHealthyMemoryResult(string missing)
    {
        var report = Complete();
        switch (missing)
        {
            case "phase": report.Phases.RemoveAt(0); break;
            case "samples": report.Phases[0].Samples.Clear(); break;
            case "runtime": report.RuntimeConnected = false; break;
            case "runtime-home": report.RuntimeHomeIsolated = false; break;
            case "page": report.VisitedSettingsPages.RemoveAt(0); break;
            case "history": report.StoredHistoryRendered = false; break;
            case "retention": report.RetentionCycles.Clear(); break;
            case "error": report.Errors.Add("A page did not render."); break;
        }
        report.Evaluate();
        Assert.False(report.Complete);
        Assert.False(report.Passed);
        Assert.Equal(1, report.ExitCode);
    }

    [Fact]
    public void DuplicatePhasesCannotHideAMissingPhase()
    {
        var report = Complete();
        report.Phases[0] = report.Phases[1];
        report.Evaluate();
        Assert.Equal(1, report.ExitCode);
    }

    [Fact]
    public void AViewportThatNeverDrewCannotPass()
    {
        var report = Complete();
        var old = report.Phases[0];
        var undrawn = new MemoryCheckPhase { Id = old.Id, State = old.State };
        undrawn.Samples.AddRange(old.Samples);
        report.Phases[0] = undrawn;
        report.Evaluate();
        Assert.Equal(1, report.ExitCode);
    }

    [Fact]
    public void AnUnboundedCacheFailsEvenIfItsOwnersWouldOtherwiseBeConsideredLive()
    {
        var report = Complete();
        var phase = report.Phases[0];
        var oversized = new MemoryCheckPhase
        {
            Id = phase.Id,
            State = phase.State with { CachedSurfaces = 12 },
            RenderedFrames = 1,
        };
        oversized.Samples.AddRange(phase.Samples);
        report.Phases[0] = oversized;
        report.Evaluate();
        Assert.Equal(3, report.ExitCode);
        Assert.Contains(report.BudgetFailures, error => error.Contains("active-plus-cache bound", StringComparison.Ordinal));
    }

    [Fact]
    public void GcCapacityOscillationIsNotReportedAsNonGcGrowth()
    {
        var report = Complete();
        report.RetentionCycles.Clear();
        long[] gc = [200, 280, 190, 275];
        for (var cycle = 0; cycle < gc.Length; cycle++)
            report.RetentionCycles.Add(new()
            {
                Cycle = cycle,
                TrackedCount = 10,
                ManagedBytes = 100 * 1024L * 1024,
                CommittedBytes = gc[cycle] * 1024 * 1024,
                PrivateBytes = (gc[cycle] + 365) * 1024 * 1024,
            });
        report.Evaluate();
        Assert.True(report.Passed);
    }

    [Fact]
    public void NonGcGrowthStillFailsWhenManagedCommitmentIsStable()
    {
        var report = Complete();
        report.RetentionCycles.Clear();
        for (var cycle = 0; cycle <= report.Options.Rounds; cycle++)
            report.RetentionCycles.Add(new()
            {
                Cycle = cycle,
                TrackedCount = 10,
                ManagedBytes = 100 * 1024L * 1024,
                CommittedBytes = 200 * 1024L * 1024,
                PrivateBytes = (500 + cycle * 40) * 1024L * 1024,
            });
        report.Evaluate();
        Assert.Equal(3, report.ExitCode);
        Assert.Contains(report.BudgetFailures, failure => failure.Contains("outside the GC heap", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, 400)]
    [InlineData(-1, 400)]
    [InlineData(200, 0)]
    [InlineData(500, 400)]
    public void MissingOrInconsistentGcCommitmentIsIncomplete(int gcMiB, int privateMiB)
    {
        var report = Complete();
        report.RetentionCycles[0] = new()
        {
            Cycle = 0,
            TrackedCount = 10,
            ManagedBytes = 100 * 1024L * 1024,
            CommittedBytes = gcMiB * 1024L * 1024,
            PrivateBytes = privateMiB * 1024L * 1024,
        };
        report.Evaluate();
        Assert.Equal(1, report.ExitCode);
        Assert.False(report.Complete);
    }

    [Fact]
    public void JsonRetainsScopeAndRawEvidence()
    {
        var report = Complete();
        report.Evaluate();
        using var document = System.Text.Json.JsonDocument.Parse(report.ToJson());
        Assert.True(document.RootElement.GetProperty("Complete").GetBoolean());
        Assert.Equal(6, document.RootElement.GetProperty("Phases").GetArrayLength());
        Assert.Equal("copilot.exe", document.RootElement.GetProperty("Phases")[0].GetProperty("Samples")[0]
            .GetProperty("Processes")[1].GetProperty("Name").GetString());
    }

    internal static MemoryCheckReport Complete(int ramMiB = 200, int privateMiB = 400, int managedMiB = 100, int retained = 0, int? processId = null, bool fast = false, bool ci = false)
    {
        var options = new MemoryCheckOptions
        {
            Enabled = true,
            SampleSeconds = 5,
            Fast = fast,
            CiGuardrails = ci,
            MaxRamBytes = ci ? MemoryCheckOptions.CiRamBytes : 500 * 1024L * 1024,
            MaxPrivateBytes = ci ? MemoryCheckOptions.CiPrivateBytes : 1024 * 1024L * 1024,
        };
        var expected = Enumerable.Range(1, fast ? 1 : options.Rounds).SelectMany(round => new[] { $"chats-{round}", $"after-pages-{round}" }).ToArray();
        var report = new MemoryCheckReport
        {
            ProcessId = processId ?? Environment.ProcessId,
            Options = options,
            ExpectedPhases = expected,
            RuntimeConnected = true,
            RuntimeHomeIsolated = true,
            CliSha256 = new string('a', 64),
            StoredHistoryRendered = true,
            ProfileFingerprint = new string('b', 64),
            RuntimeConfigurationSha256 = new string('c', 64),
            ApplicationSha256 = new string('d', 64),
            ProfileKind = ci ? "synthetic" : "",
            ProfileChats = 2000,
            ProfileMessages = 8000,
            WindowWidth = 1000,
            WindowHeight = 820,
            RenderScaling = 2,
            LibraryScanCompleted = fast,
            LibraryScannedChats = 2000,
            MaximumCachedSurfaces = 9,
        };
        report.VisitedPages.AddRange(Enumerable.Range(1, 9));
        report.VisitedSettingsPages.AddRange([0, 1, 2, 3, 4, 5, 6, 7, 9, 10]);
        report.TargetChats.AddRange(Enumerable.Range(1, 12).Select(index => new Guid(index, 0, 0, new byte[8])));
        foreach (var id in expected)
        {
            var phase = new MemoryCheckPhase { Id = id, State = new(12, 9, 30, 9, 3, 3, 200), RenderedFrames = 1 };
            for (var sample = 0; sample <= 20; sample++)
                phase.Samples.Add(new(sample * 0.25,
                [
                    new(report.ProcessId, 1, "Lumi.exe", (ramMiB - 50) * 1024L * 1024, ramMiB * 1024L * 1024, (privateMiB - 100) * 1024L * 1024),
                    new(12345, report.ProcessId, "copilot.exe", 50 * 1024L * 1024, 100 * 1024L * 1024, 100 * 1024L * 1024),
                ])
                {
                    Runtime = new(500 * 1024L * 1024, 125 * 1024L * 1024, 100 * 1024L * 1024, 0, 0, 1, 1, 1),
                    Machine = new(50, 16 * 1024L * 1024 * 1024, 8 * 1024L * 1024 * 1024, 0, 1),
                });
            report.Phases.Add(phase);
        }
        for (var cycle = 0; cycle < (fast ? 1 : options.Rounds + 1); cycle++)
            report.RetentionCycles.Add(new()
            {
                Cycle = cycle,
                ManagedBytes = managedMiB * 1024L * 1024,
                CommittedBytes = (managedMiB + 25) * 1024L * 1024,
                PrivateBytes = privateMiB * 1024L * 1024,
                TrackedCount = 10,
                RetainedCount = retained,
            });
        return report;
    }
}
