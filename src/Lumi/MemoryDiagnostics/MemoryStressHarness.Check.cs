#if DEBUG || MEMORY_CHECK
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.Qa;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;

namespace Lumi.MemoryDiagnostics;

internal sealed partial class MemoryStressHarness
{
    private static readonly (int Index, string Host)[] CheckPages =
    [
        (1, "PageJobsHost"), (2, "PageProjectsHost"), (3, "PageSkillsHost"),
        (4, "PageAgentsHost"), (5, "PageMemoriesHost"), (6, "PageMcpServersHost"),
        (7, "PageSettingsHost"), (8, "PageLibraryHost"), (9, "PageDiscoverHost"),
    ];
    private static readonly string[] CheckSettingsPages =
    [
        "PageProfile", "PageGeneral", "PageMobile", "PageAppearance", "PageChat",
        "PageAI", "PagePrivacy", "PageAbout", "PagePermissions", "PageAiFeatures",
    ];
    private readonly HashSet<Guid> _checkOpenedChats = [];
    private readonly HashSet<int> _checkVisitedPages = [];
    private readonly HashSet<int> _checkVisitedSettings = [];
    private bool _checkStoredHistoryRendered;
    private CheckRenderProbe? _checkRenderProbe;
    private Compositor? _checkCompositor;
    private byte[]? _checkInjectedMemory;
    private bool _checkLibraryScanned;
    private int _checkLibraryScannedChats, _checkLibraryItems, _checkLibraryVisibleItems;

    private async Task RunCheckAsync()
    {
        var options = _options.Check;
        var rounds = options.Fast ? 1 : options.Rounds;
        var expected = Enumerable.Range(1, rounds)
            .SelectMany(round => new[] { $"chats-{round}", $"after-pages-{round}" }).ToArray();
        var report = new MemoryCheckReport
        {
            Options = options,
            ExpectedPhases = expected,
#if DEBUG
            Build = "Debug (not comparable to Release)",
#else
            Build = "Release with opt-in memory diagnostics (not a packaged-release certification)",
#endif
            MaximumCachedSurfaces = _sessionStore.MaxIdleCachedSurfaces + 1,
            ProfileChats = _dataStore.Data.Chats.Count,
            ProfileMessages = _dataStore.Data.Chats.Sum(chat => (long)chat.MessageCount),
        };
        var output = options.OutputDirectory ?? Path.Combine(DataStore.AppDirectory, "memory-check");
        try
        {
            var profile = Program.MemoryProfile
                ?? throw new InvalidOperationException("The launched profile has no verified preparation identity.");
            report.ProfileKind = profile.Kind;
            report.CorpusVersion = profile.CorpusVersion;
            report.ProfileFingerprint = profile.Fingerprint;
            report.RuntimeConfigurationSha256 = MemoryCheckProfile.FileHash(Path.ChangeExtension(typeof(App).Assembly.Location, ".runtimeconfig.json"));
            report.ApplicationSha256 = MemoryCheckProfile.FileHash(typeof(App).Assembly.Location);
            Console.WriteLine($"[memory-check] {report.ProfileChats:N0} persisted chats; starting the local runtime (no inference).");
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("The private-resident RAM budget check currently supports Windows. The existing stress/history diagnostics remain cross-platform.");
            if (_dataStore.PendingLegacyImports > 0 || !_dataStore.PersistsTranscripts)
                throw new InvalidOperationException("The memory check requires completed SQLite transcripts.");
            report.RuntimeHome = CopilotService.RuntimeHomeDirectory;
            report.RuntimeHomeIsolated = string.Equals(Path.GetFullPath(report.RuntimeHome),
                Path.Combine(DataStore.AppDirectory, "copilot-home"), StringComparison.OrdinalIgnoreCase);
            if (!report.RuntimeHomeIsolated)
                throw new InvalidOperationException("The Copilot runtime home is not isolated; refusing to benchmark shared user state.");
            using (var executable = File.OpenRead(CopilotService.BundledCliPath
                ?? throw new FileNotFoundException("The bundled Copilot executable was not found.")))
                report.CliSha256 = Convert.ToHexString(SHA256.HashData(executable));
            await _copilotService.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(45));
            if (!_copilotService.IsConnected)
                throw new InvalidOperationException("The local Copilot runtime did not start; its memory must not be silently omitted.");
            report.RuntimeConnected = true;
            var targets = await OnUiAsync(() => ResolveCheckChats(options, profile.Kind == "synthetic"));
            report.TargetChats.AddRange(targets.Select(chat => chat.Id));
            var window = await OnUiAsync(CheckWindow);
            await OnUiAsync(() =>
            {
                window.Width = 1000;
                window.Height = 820;
                _mainVm.SelectedProjectFilter = null;
                report.WindowWidth = window.ClientSize.Width;
                report.WindowHeight = window.ClientSize.Height;
                report.RenderScaling = window.RenderScaling;
                report.Theme = window.ActualThemeVariant.ToString();
            });
            var released = new List<TrackedReference>();
            if (!options.Fast)
            {
                // Deep warmup and natural RAM windows precede all forced GC.
                Console.WriteLine("[memory-check] Warming the declared chats, stored history and every management/Settings surface.");
                await TimeAsync("warmup", async () =>
                {
                    await ReplayCheckChatsAsync(targets, released);
                    await VisitCheckPagesAsync(window, released);
                    await OpenCheckChatAsync(targets[^1]);
                });
            }
            else
                Console.WriteLine("[memory-check] Fast: one complete pass; no extra warmup/repeated retention workload.");
            if (options.InjectRetainedMiB > 0)
            {
                _checkInjectedMemory = new byte[options.InjectRetainedMiB * 1024 * 1024];
                for (var offset = 0; offset < _checkInjectedMemory.Length; offset += 4096)
                    _checkInjectedMemory[offset] = 1;
                Console.WriteLine($"[memory-check] Diagnostic regression control: retaining {options.InjectRetainedMiB} MiB. Never use this run to record a baseline.");
            }
            for (var round = 1; round <= rounds; round++)
            {
                await TimeAsync($"chats-{round}", () => ReplayCheckChatsAsync(targets, released));
                report.Phases.Add(await SampleCheckPhaseAsync($"chats-{round}", options, window));
                await TimeAsync($"pages-{round}", async () =>
                {
                    await VisitCheckPagesAsync(window, released);
                    await OpenCheckChatAsync(targets[^1]);
                });
                report.Phases.Add(await SampleCheckPhaseAsync($"after-pages-{round}", options, window));
            }
            await TimeAsync("retention-check", async () =>
                report.RetentionCycles.Add(await CaptureAfterGcAsync(0, RequireTrackedReferences("memory-check views", released))));
            for (var round = 1; !options.Fast && round <= options.Rounds; round++)
            {
                await TimeAsync($"retention-round-{round}", async () =>
                {
                    await ReplayCheckChatsAsync(targets, released);
                    await VisitCheckPagesAsync(window, released);
                    await OpenCheckChatAsync(targets[^1]);
                    report.RetentionCycles.Add(await CaptureAfterGcAsync(round, released));
                });
                Console.WriteLine($"[memory-check] Retention round {round}/{options.Rounds}: {report.RetentionCycles[^1].RetainedCount} expected-dead objects remain.");
            }
            await OnUiAsync(() =>
            {
                foreach (var chat in targets)
                    if (_dataStore.ChatStore!.GetStoredMessageCount(chat.Id) != chat.MessageCount)
                        throw new InvalidOperationException("The memory workload changed the stored transcript count.");
            });
            report.StoredHistoryRendered = _checkStoredHistoryRendered;
            report.VisitedPages.AddRange(_checkVisitedPages);
            report.VisitedSettingsPages.AddRange(_checkVisitedSettings);
            report.LibraryScanCompleted = _checkLibraryScanned;
            report.LibraryScannedChats = _checkLibraryScannedChats;
            report.LibraryItems = _checkLibraryItems;
            report.LibraryVisibleItems = _checkLibraryVisibleItems;

            async Task TimeAsync(string stage, Func<Task> action)
            {
                var watch = Stopwatch.StartNew();
                await action();
                report.Timings.Add(new(stage, watch.Elapsed.TotalMilliseconds));
            }
        }
        catch (Exception error)
        {
            report.Errors.Add(error.ToString());
        }
        report.Evaluate();
        Console.WriteLine(report.ToConsole());
        _exitCode = report.ExitCode;
        try
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "results.json"), report.ToJson());
            File.WriteAllText(Path.Combine(output, "report.txt"), report.ToConsole());
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("[memory-check] Could not write results: " + error);
            _exitCode = 1;
        }
        Console.WriteLine($"[RESULT] LUMI_MEMORY_CHECK_{(_exitCode == 0 ? "OK" : "FAIL")}");
        Environment.ExitCode = _exitCode;
        if (_options.KeepOpen)
            Console.WriteLine("[memory-check] Leaving this isolated window open for inspection.");
        else
            _requestShutdown(_exitCode);
    }

    private IReadOnlyList<Chat> ResolveCheckChats(MemoryCheckOptions options, bool synthetic)
    {
        var chats = _dataStore.Data.Chats.Where(chat => chat.MessageCount > 0);
        if (synthetic)
        {
            var names = new[]
            {
                "Tiny chat", "Small chat", "Medium chat", "Large chat", "Huge chat", "Mega chat",
                "Tool-heavy chat", "Markdown-heavy chat", "Code-heavy session", "Doc-heavy session",
                "Project selection target", "Other project chat",
            };
            var selected = names.Select(name => chats.Single(chat => chat.Title.StartsWith(name, StringComparison.Ordinal))).ToList();
            selected.AddRange(chats.Where(chat => chat.Title.StartsWith("Workload chat #", StringComparison.Ordinal))
                .OrderBy(chat => chat.Title, StringComparer.Ordinal));
            return selected.Take(options.Chats).ToArray();
        }
        var targets = chats.OrderByDescending(chat => chat.UpdatedAt).Take(options.Chats).ToArray();
        if (targets.Length != options.Chats)
            throw new InvalidOperationException($"The profile has fewer than {options.Chats} nonempty chats.");
        return targets;
    }

    private async Task ReplayCheckChatsAsync(IReadOnlyList<Chat> targets, List<TrackedReference> released)
    {
        var candidates = new List<SurfaceReferenceBatch>();
        foreach (var chat in targets)
        {
            await OpenCheckChatAsync(chat);
            candidates.Add(await OnUiAsync(() => CaptureSurfaceBatch(_mainVm.ChatVM)));
        }
        released.AddRange(await OnUiAsync(() => SelectExpectedDeadSurfaceReferences(candidates)));
        var longChat = targets.MaxBy(chat => chat.MessageCount)!;
        await OpenCheckChatAsync(longChat);
        var scroll = await OnUiAsync(FindTranscriptScrollViewer)
            ?? throw new InvalidOperationException("The transcript scroll viewer was not realized.");
        for (var step = 0; step < 6; step++)
        {
            await OnUiAsync(() => scroll.Offset = scroll.Offset.WithY(Math.Max(0, scroll.Offset.Y - scroll.Viewport.Height * 0.85)));
            await WaitCheckViewportAsync();
            _checkStoredHistoryRendered |= await OnUiAsync(() => _mainVm.ChatVM.TranscriptHistory.Segments
                .Any(segment => segment.IsHydrated && segment.IsVisible && segment.HydratedMessageCount > 0));
        }
        await OnUiAsync(() => scroll.ScrollToEnd());
        await WaitCheckViewportAsync();
        await OpenCheckChatAsync(targets[^1]);
    }

    private async Task OpenCheckChatAsync(Chat chat)
    {
        await OpenChatAsync(chat.Id);
        await UntilCheckAsync(() => _mainVm.ChatVM.CurrentChat?.Id == chat.Id
            && !_mainVm.ChatVM.IsChatSurfaceLoading && !_mainVm.ChatVM.IsRefreshingGitStatus,
            "the requested chat and its queued reads");
        await WaitCheckViewportAsync();
        await OnUiAsync(() =>
        {
            var window = CheckWindow();
            if (_mainVm.ChatVM.IsSessionActive || _mainVm.ChatVM.Messages.Count == 0
                || !_mainVm.ChatVM.MountedTranscriptTurns.Any(turn => turn.RealizedItemsHost is { } host
                    && QaUiVisibility.IsVisibleWithin(host, window)))
                throw new InvalidOperationException("The requested transcript did not render, or unexpected session work started.");
            _checkOpenedChats.Add(chat.Id);
        });
    }

    private async Task VisitCheckPagesAsync(MainWindow window, List<TrackedReference> released)
    {
        await OnUiAsync(() => _mainVm.ChatVM.PromptText = "Memory-check draft; never sent.");
        var draft = await OnUiAsync(() => _mainVm.ChatVM.PromptText);
        foreach (var page in CheckPages)
        {
            await OnUiAsync(() =>
            {
                if (page.Index == 7)
                    _mainVm.SettingsVM.SelectedPageIndex = 0;
                _mainVm.SelectedNavIndex = page.Index;
            });
            await UntilCheckAsync(() => window.FindControl<ContentControl>(page.Host)?.Content is Control view
                && QaUiVisibility.IsVisibleWithin(view, window), $"management page {page.Index}");
            await WaitCheckRenderAsync();
            if (_options.Check.Fast && page.Index == 8)
            {
                var library = await OnUiAsync(() => window.FindControl<ContentControl>(page.Host)?.Content is Control
                { DataContext: LibraryViewModel model } ? model
                    : throw new InvalidOperationException("The Library page did not bind its viewmodel."));
                await UntilCheckAsync(() => library.HasScanned && !library.IsLoading
                    && library.ScannedChats == library.TotalChatsToScan, "the completed Library scan");
                if (library.HasResults)
                    await UntilCheckAsync(() => window.GetVisualDescendants().OfType<Control>()
                        .Any(control => control.DataContext is LibraryItemViewModel
                            && QaUiVisibility.IsVisibleWithin(control, window)), "populated Library item controls");
                await WaitCheckRenderAsync();
                await OnUiAsync(() =>
                {
                    _checkLibraryScanned = true;
                    _checkLibraryScannedChats = library.ScannedChats;
                    _checkLibraryItems = library.Items.Count;
                    _checkLibraryVisibleItems = window.GetVisualDescendants().OfType<Control>()
                        .Count(control => control.DataContext is LibraryItemViewModel
                            && QaUiVisibility.IsVisibleWithin(control, window));
                });
            }
            _checkVisitedPages.Add(page.Index);
            if (page.Index == 7)
            {
                for (var index = 0; index < _mainVm.SettingsVM.Pages.Count; index++)
                {
                    if (index == SettingsViewModel.MemoriesPageIndex)
                        continue; // Memories is the dedicated page visited above, not a Settings template.
                    var name = index > SettingsViewModel.MemoriesPageIndex ? CheckSettingsPages[index - 1] : CheckSettingsPages[index];
                    await OnUiAsync(() => _mainVm.SettingsVM.SelectedPageIndex = index);
                    await UntilCheckAsync(() => window.GetVisualDescendants().OfType<Control>()
                        .Any(control => control.Name == name
                            && QaUiVisibility.IsVisibleWithin(control, window)), $"Settings page {index}");
                    await WaitCheckRenderAsync();
                    _checkVisitedSettings.Add(index);
                    released.Add(await OnUiAsync(() => TrackCheckSettingsPage(window)));
                }
            }
            released.Add(await OnUiAsync(() => TrackCheckPage(window, page.Host)));
        }
        await OnUiAsync(() => _mainVm.SelectedNavIndex = 0);
        await WaitCheckViewportAsync();
        await OnUiAsync(() =>
        {
            if (!string.Equals(draft, _mainVm.ChatVM.PromptText, StringComparison.Ordinal))
                throw new InvalidOperationException("Page navigation lost the composer draft.");
            foreach (var page in CheckPages)
                if (window.FindControl<ContentControl>(page.Host)?.Content is not null)
                    throw new InvalidOperationException($"Inactive page {page.Index} still has UI content.");
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TrackedReference TrackCheckPage(MainWindow window, string name)
        => new("view:" + name, new WeakReference(window.FindControl<ContentControl>(name)!.Content));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TrackedReference TrackCheckSettingsPage(MainWindow window)
    {
        var page = window.GetVisualDescendants().OfType<Control>()
            .Single(control => CheckSettingsPages.Contains(control.Name, StringComparer.Ordinal)
                && QaUiVisibility.IsVisibleWithin(control, window));
        return new("settings:" + page.Name, new WeakReference(page));
    }

    private async Task<MemoryCheckPhase> SampleCheckPhaseAsync(string id, MemoryCheckOptions options, MainWindow window)
    {
        await WaitCheckViewportAsync();
        var state = await OnUiAsync(() => new MemoryCheckState(_checkOpenedChats.Count,
            _dataStore.Data.Chats.Count(chat => _dataStore.GetTranscriptWindow(chat).IsLoaded),
            _dataStore.Data.Chats.Sum(chat => chat.Messages.Count), _sessionStore.SnapshotSurfaces().Count,
            _mainVm.ChatVM.MountedTranscriptTurns.Count,
            _mainVm.ChatVM.MountedTranscriptTurns.Count(turn => turn.RealizedItemsHost is not null),
            window.GetVisualDescendants().OfType<Control>().Count(control => QaUiVisibility.IsVisibleWithin(control, window))));
        var phase = new MemoryCheckPhase { Id = id, State = state, RenderedFrames = _checkRenderProbe?.Frames ?? 0 };
        var watch = Stopwatch.StartNew();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Private working set sampling requires Windows.");
        do
        {
            phase.Samples.Add(CaptureSample());
            await Task.Delay(250);
        } while (watch.Elapsed.TotalSeconds < options.SampleSeconds);
        phase.Samples.Add(CaptureSample());
        Console.WriteLine($"[memory-check] {id}: {phase.MeanRamBytes / 1048576:0.0} MiB average private resident (no forced GC).");
        return phase;

        MemoryRamSample CaptureSample() => new(watch.Elapsed.TotalSeconds, MemoryProcessProbe.Capture(Environment.ProcessId))
        {
            Runtime = MemoryRuntimeSample.Capture(),
            Machine = MemoryProcessProbe.CaptureMachine(),
        };
    }

    private async Task WaitCheckViewportAsync()
    {
        await SettleStoppedTranscriptViewportAsync();
        await UntilCheckAsync(() => !TranscriptRealizationScheduler.Instance.HasPendingWork
            && !_mainVm.ChatVM.TranscriptHistory.HasPendingWork, "transcript realization and stored history");
        await DrainAsync(DispatcherPriority.Render);
        await DrainAsync(DispatcherPriority.Loaded);
        await WaitCheckRenderAsync();
    }

    private async Task WaitCheckRenderAsync()
    {
        var (probe, compositor) = await OnUiAsync(() =>
        {
            if (_checkRenderProbe is null)
            {
                var shell = CheckWindow().GetVisualDescendants().OfType<StrataChatShell>().First();
                _checkCompositor = ElementComposition.GetElementVisual(shell)?.Compositor
                    ?? throw new InvalidOperationException("The viewport is not attached to a compositor.");
                var layer = AdornerLayer.GetAdornerLayer(shell)
                    ?? throw new InvalidOperationException("The headed memory check has no adorner layer.");
                _checkRenderProbe = new CheckRenderProbe();
                layer.Children.Add(_checkRenderProbe);
            }
            CheckWindow().UpdateLayout();
            return (_checkRenderProbe, _checkCompositor
                ?? throw new InvalidOperationException("The render probe has no compositor."));
        });
        var before = probe.Frames;
        await RenderAsync(compositor, [probe], "The memory-check viewport");
        if (probe.Frames <= before)
            throw new InvalidOperationException("The viewport did not draw; minimized/unready windows cannot pass a RAM budget.");
    }

    private sealed class CheckRenderProbe : Control
    {
        private int _frames;
        public int Frames => Volatile.Read(ref _frames);
        public CheckRenderProbe() { Width = Height = 1; IsHitTestVisible = false; }
        public override void Render(DrawingContext context) => context.Custom(new Operation(this));

        private sealed class Operation(CheckRenderProbe owner) : ICustomDrawOperation
        {
            public Rect Bounds => new(0, 0, 1, 1);
            public bool HitTest(Point point) => false;
            public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
            public void Render(ImmediateDrawingContext context) => Interlocked.Increment(ref owner._frames);
            public void Dispose() { }
        }
    }

    private static async Task UntilCheckAsync(Func<bool> predicate, string description)
    {
        var deadline = Stopwatch.StartNew();
        while (!await OnUiAsync(predicate))
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException($"Timed out waiting for {description}; the workload is incomplete.");
            await Task.Delay(25);
        }
    }

    private static MainWindow CheckWindow()
        => (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow
           ?? throw new InvalidOperationException("The memory check has no main window.");
}
#endif
