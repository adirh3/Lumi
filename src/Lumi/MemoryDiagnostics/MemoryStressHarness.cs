#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Lumi.Services;
using Lumi.UiPerf;
using Lumi.ViewModels;
using Lumi.Views;

namespace Lumi.MemoryDiagnostics;

/// <summary>
/// Drives deterministic real-app workloads, forces full collections between cycles, and fails on
/// expected-dead objects or sustained post-GC managed growth.
/// </summary>
internal sealed class MemoryStressHarness
{
    private const int GateFailureExitCode = 3;
    private const int MinimumSurfaceChurnActions = 10;

    private readonly MainViewModel _mainVm;
    private readonly DataStore _dataStore;
    private readonly ChatSessionStore _sessionStore;
    private readonly MemoryHarnessOptions _options;
    private readonly Func<IReadOnlyList<ChatWindow>> _snapshotDetachedWindows;
    private readonly Func<ChatWindow, bool> _isDetachedWindowTracked;
    private readonly Action<int> _requestShutdown;
    private readonly string _thumbnailDirectory;
    private int _surfaceCursor;
    private int _exitCode;

    private sealed record TrackedReference(string Kind, WeakReference Reference);

    private sealed record SurfaceReferenceBatch(
        WeakReference<ChatViewModel> Owner,
        IReadOnlyList<TrackedReference> References);

    private sealed record MemoryScenario(
        string Id,
        string DisplayName,
        int AllowedRetainedCount,
        Func<int, Task<IReadOnlyList<TrackedReference>>> RunCycleAsync,
        Func<Task>? PrepareAsync = null,
        string? Note = null);

    private sealed class DetachedChatWindowHost
    {
        public ChatWindow? Window { get; set; }
        public ChatWindowViewModel? ViewModel { get; set; }
        public ChatWorkspaceView? Workspace { get; set; }
    }

    public MemoryStressHarness(
        MainViewModel mainVm,
        DataStore dataStore,
        ChatSessionStore sessionStore,
        MemoryHarnessOptions options,
        Func<IReadOnlyList<ChatWindow>> snapshotDetachedWindows,
        Func<ChatWindow, bool> isDetachedWindowTracked,
        Action<int> requestShutdown)
    {
        _mainVm = mainVm;
        _dataStore = dataStore;
        _sessionStore = sessionStore;
        _options = options;
        _snapshotDetachedWindows = snapshotDetachedWindows;
        _isDetachedWindowTracked = isDetachedWindowTracked;
        _requestShutdown = requestShutdown;
        _thumbnailDirectory = Path.Combine(
            Path.GetTempPath(),
            "Lumi-memory-stress-thumbnails",
            Environment.ProcessId.ToString());
    }

    public async Task RunAsync()
    {
        var reportWriteAttempted = false;

        try
        {
            Console.WriteLine();
            Console.WriteLine(
                $"[memory] Starting memory stress harness - mode={_options.Mode}, " +
                $"cycles={_options.Cycles} (warmup {_options.WarmupCycles}), " +
                $"actions/cycle={_options.ActionsPerCycle}");

            var workloads = new UiWorkloadScenarios(_dataStore);
            await OnUiAsync(() =>
            {
                _dataStore.Data.Settings.AutoSaveChats = false;
                _dataStore.Data.Settings.EnableMemoryAutoSave = false;
                workloads.Seed(fillerChatCount: Math.Clamp(_options.ActionsPerCycle * 3, 48, 180));
                _mainVm.SelectedProjectFilter = null;
                _mainVm.SelectedNavIndex = 0;
                _mainVm.RefreshChatList();
            });

            await SettleAsync(Math.Max(600, _options.SettleMilliseconds * 4));

            var churnChatIds = _dataStore.Data.Chats
                .Where(static chat => chat.Title.StartsWith("Workload chat #", StringComparison.Ordinal))
                .Select(static chat => chat.Id)
                .ToArray();

            var availableScenarios = BuildScenarios(workloads, churnChatIds);
            var availableScenarioIds = availableScenarios
                .Select(static scenario => MemoryHarnessOptions.NormalizeScenario(scenario.Id))
                .ToHashSet(StringComparer.Ordinal);
            var unavailableScenarios = _options.RequestedScenarios
                .Where(requested => !availableScenarioIds.Contains(
                    MemoryHarnessOptions.NormalizeScenario(requested)))
                .ToArray();
            if (unavailableScenarios.Length > 0)
            {
                throw new InvalidOperationException(
                    "Unknown or unavailable memory scenario(s): " +
                    string.Join(", ", unavailableScenarios));
            }

            var scenarios = availableScenarios
                .Where(scenario => _options.IncludesScenario(scenario.Id))
                .ToList();

            if (scenarios.Count == 0)
                throw new InvalidOperationException("The memory scenario filter did not match any available scenario.");

            var samples = new List<MemoryScenarioSamples>(scenarios.Count);
            foreach (var scenario in scenarios)
                samples.Add(await RunScenarioAsync(scenario));

            var report = MemoryStressReport.Build(_options, samples);
            Console.WriteLine();
            Console.WriteLine(report.ToConsole());
            reportWriteAttempted = true;
            WriteJsonReport(report);

            if (report.HasHarnessErrors)
                _exitCode = 1;
            else if (report.GateFailed)
                _exitCode = GateFailureExitCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[memory] Harness failed: " + ex);
            _exitCode = 1;

            var failureReport = MemoryStressReport.BuildHarnessFailure(_options, ex);
            Console.WriteLine();
            Console.WriteLine(failureReport.ToConsole());

            if (!reportWriteAttempted)
            {
                try
                {
                    reportWriteAttempted = true;
                    WriteJsonReport(failureReport);
                }
                catch (Exception reportError)
                {
                    Console.WriteLine("[memory] Could not write harness failure report: " + reportError);
                }
            }
        }
        finally
        {
            TryDeleteThumbnailDirectory();
            Environment.ExitCode = _exitCode;
            if (_options.KeepOpen)
            {
                Console.WriteLine("[memory] --memory-keep-open set; leaving the debug window open for inspection.");
            }
            else
            {
                _requestShutdown(_exitCode);
            }
        }
    }

    private IReadOnlyList<MemoryScenario> BuildScenarios(
        UiWorkloadScenarios workloads,
        IReadOnlyList<Guid> churnChatIds)
    {
        var scenarios = new List<MemoryScenario>();

        if (OperatingSystem.IsWindows())
        {
            scenarios.Add(new MemoryScenario(
                "attachment-thumbnails",
                "Unique attachment thumbnail churn",
                AllowedRetainedCount: 0,
                RunCycleAsync: RunThumbnailChurnCycleAsync,
                Note: "Unique image paths must not remain rooted by a process-lifetime thumbnail cache."));
        }

        scenarios.Add(new MemoryScenario(
            "chat-surfaces",
            "Chat surface eviction and transcript graph churn",
            AllowedRetainedCount: 0,
            RunCycleAsync: cycle => RunSurfaceChurnCycleAsync(churnChatIds, cycle),
            PrepareAsync: () => OpenChatAsync(workloads.MediumChatId),
            Note: "Evicted ChatViewModel surfaces, message VMs, turns, and realized hosts must collect."));

        scenarios.Add(new MemoryScenario(
            "draft-surfaces",
            "New-chat draft surface replacement",
            AllowedRetainedCount: 0,
            RunCycleAsync: RunDraftChurnCycleAsync,
            PrepareAsync: () => OpenChatAsync(workloads.SmallChatId),
            Note: "Replaced unhosted draft surfaces cannot remain pinned by app-lifetime events or bindings."));

        scenarios.Add(new MemoryScenario(
            "transcript-rebuild",
            "Heavy transcript rebuild replacement",
            AllowedRetainedCount: 0,
            RunCycleAsync: RunTranscriptRebuildCycleAsync,
            PrepareAsync: () => OpenChatAsync(workloads.ToolHeavyChatId),
            Note: "Old transcript turns, items, and realized hosts must collect after a rebuild."));

        scenarios.Add(new MemoryScenario(
            "detached-chat-window",
            "Detached chat window open/close churn",
            AllowedRetainedCount: 8,
            RunCycleAsync: cycle => RunDetachedWindowChurnCycleAsync(workloads.CodeHeavyChatId, cycle),
            PrepareAsync: () => OpenChatAsync(workloads.CodeHeavyChatId),
            Note: "Production-managed closed window graphs must stay bounded; Avalonia may transiently retain up to two recent TopLevels."));

        return scenarios;
    }

    private async Task<MemoryScenarioSamples> RunScenarioAsync(MemoryScenario scenario)
    {
        Console.WriteLine($"[memory] Scenario: {scenario.DisplayName}");
        var samples = new MemoryScenarioSamples
        {
            ScenarioId = scenario.Id,
            DisplayName = scenario.DisplayName,
            AllowedRetainedCount = scenario.AllowedRetainedCount,
            Note = scenario.Note,
        };
        var references = new List<TrackedReference>();

        try
        {
            if (scenario.PrepareAsync is not null)
            {
                await scenario.PrepareAsync();
                await SettleAsync(_options.SettleMilliseconds);
            }

            for (var warmup = 0; warmup < _options.WarmupCycles; warmup++)
            {
                references.AddRange(await scenario.RunCycleAsync(-(warmup + 1)));
                await CollectGarbageAsync();
            }

            for (var cycle = 1; cycle <= _options.Cycles; cycle++)
            {
                references.AddRange(await scenario.RunCycleAsync(cycle));
                var sample = await CaptureAfterGcAsync(cycle, references);
                samples.Cycles.Add(sample);
                Console.WriteLine(
                    $"[memory]   cycle {cycle,2}: managed={ToMiB(sample.ManagedBytes),7:n1} MiB " +
                    $"heap={ToMiB(sample.HeapSizeBytes),7:n1} MiB " +
                    $"private={ToMiB(sample.PrivateBytes),7:n1} MiB " +
                    $"retained={sample.RetainedCount}/{sample.TrackedCount}");
            }
        }
        catch (Exception ex)
        {
            samples.Errors.Add(ex.ToString());
            Console.WriteLine($"[memory]   scenario failed: {ex.Message}");
        }

        return samples;
    }

    private async Task<IReadOnlyList<TrackedReference>> RunThumbnailChurnCycleAsync(int cycle)
    {
        Directory.CreateDirectory(_thumbnailDirectory);
        var source = ResolveThumbnailSource();
        var references = new List<TrackedReference>(_options.ActionsPerCycle);

        for (var action = 0; action < _options.ActionsPerCycle; action++)
        {
            var path = Path.Combine(
                _thumbnailDirectory,
                $"cycle-{cycle}-action-{action}-{Guid.NewGuid():N}.png");
            File.Copy(source, path, overwrite: false);
            references.Add(await OnUiAsync(() => CreateThumbnailReference(path)));
            File.Delete(path);
        }

        var cache = FileIconHelper.CaptureCacheDiagnostics();
        if (cache.ThumbnailEntries > FileIconHelper.ThumbnailCacheCapacity)
        {
            throw new InvalidOperationException(
                $"Thumbnail cache grew to {cache.ThumbnailEntries} entries " +
                $"(capacity {FileIconHelper.ThumbnailCacheCapacity}).");
        }

        return references;
    }

    private async Task<IReadOnlyList<TrackedReference>> RunSurfaceChurnCycleAsync(
        IReadOnlyList<Guid> chatIds,
        int cycle)
    {
        if (chatIds.Count == 0)
            throw new InvalidOperationException("No filler chats were seeded for surface churn.");

        var actions = Math.Max(MinimumSurfaceChurnActions, _options.ActionsPerCycle);
        var candidates = new List<SurfaceReferenceBatch>(actions);
        for (var action = 0; action < actions; action++)
        {
            var chatId = chatIds[_surfaceCursor++ % chatIds.Count];
            await OpenChatAsync(chatId);
            await EnsureTranscriptHostsRealizedAsync(_mainVm.ChatVM);
            candidates.Add(await OnUiAsync(() => CaptureSurfaceBatch(_mainVm.ChatVM)));
            await DrainAsync(DispatcherPriority.Render);
        }

        var references = await OnUiAsync(() => SelectExpectedDeadSurfaceReferences(candidates));
        return RequireTrackedReferences("chat surface eviction", references);
    }

    private async Task<IReadOnlyList<TrackedReference>> RunDraftChurnCycleAsync(int cycle)
    {
        var actions = Math.Max(2, _options.ActionsPerCycle);
        var candidates = new List<SurfaceReferenceBatch>(actions);
        for (var action = 0; action < actions; action++)
        {
            await OnUiAsync(() => _mainVm.NewChatCommand.Execute(null));
            candidates.Add(await OnUiAsync(() => CaptureSurfaceBatch(_mainVm.ChatVM)));
            await DrainAsync(DispatcherPriority.Render);
        }

        var references = await OnUiAsync(() => SelectExpectedDeadSurfaceReferences(candidates));
        return RequireTrackedReferences("draft surface replacement", references);
    }

    private async Task<IReadOnlyList<TrackedReference>> RunTranscriptRebuildCycleAsync(int cycle)
    {
        var actions = Math.Max(2, _options.ActionsPerCycle / 4);
        var references = new List<TrackedReference>();

        for (var action = 0; action < actions; action++)
        {
            await EnsureTranscriptHostsRealizedAsync(_mainVm.ChatVM);
            references.AddRange(await OnUiAsync(() => CaptureAndRebuildTranscript(_mainVm.ChatVM)));
            await EnsureTranscriptHostsRealizedAsync(_mainVm.ChatVM);
        }

        return references;
    }

    private async Task<IReadOnlyList<TrackedReference>> RunDetachedWindowChurnCycleAsync(
        Guid chatId,
        int cycle)
    {
        var actions = Math.Max(1, _options.ActionsPerCycle / 4);
        var references = new List<TrackedReference>(actions * 4);

        for (var action = 0; action < actions; action++)
        {
            DetachedChatWindowHost? host = await OpenProductionChatWindowAsync(chatId);
            await DrainAsync(DispatcherPriority.Render);
            await Task.Delay(25);
            references.AddRange(await CloseProductionChatWindowAsync(host));
            host = null;
            await DrainAsync(DispatcherPriority.Background);
        }

        // Avalonia can keep the most recently closed TopLevel reachable until another TopLevel takes
        // its place in dispatcher/platform bookkeeping. Close one untracked sentinel so that bounded
        // framework tail reference cannot masquerade as one leaked window per scenario.
        DetachedChatWindowHost? sentinel = await OpenProductionChatWindowAsync(chatId);
        await DrainAsync(DispatcherPriority.Render);
        _ = await CloseProductionChatWindowAsync(sentinel);
        sentinel = null;
        await DrainAsync(DispatcherPriority.Background);

        return references;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TrackedReference CreateThumbnailReference(string path)
    {
        var item = new FileAttachmentItem(path);
        var bitmap = item.IconImage
            ?? throw new InvalidOperationException($"Could not load an attachment thumbnail for '{path}'.");
        return new TrackedReference("attachment-thumbnail", new WeakReference(bitmap));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SurfaceReferenceBatch CaptureSurfaceBatch(ChatViewModel surface)
    {
        var references = new List<TrackedReference>
        {
            new("chat-surface", new WeakReference(surface)),
        };

        if (surface.Messages.FirstOrDefault() is { } message)
            references.Add(new TrackedReference("message-view-model", new WeakReference(message)));

        if (surface.MountedTranscriptTurns.LastOrDefault(
                static turn => turn.RealizedItemsHost is not null) is { } turn)
        {
            references.Add(new TrackedReference("transcript-turn", new WeakReference(turn)));
            if (turn.RealizedItemsHost is { } host)
                references.Add(new TrackedReference("transcript-host", new WeakReference(host)));
        }

        return new SurfaceReferenceBatch(new WeakReference<ChatViewModel>(surface), references);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private IReadOnlyList<TrackedReference> SelectExpectedDeadSurfaceReferences(
        IReadOnlyList<SurfaceReferenceBatch> candidates)
    {
        var liveSurfaces = new HashSet<ChatViewModel>(
            _sessionStore.SnapshotSurfaces(),
            ReferenceEqualityComparer.Instance);
        var references = new List<TrackedReference>();

        foreach (var candidate in candidates)
        {
            if (!candidate.Owner.TryGetTarget(out var surface) || !liveSurfaces.Contains(surface))
                references.AddRange(candidate.References);
        }

        return references;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IReadOnlyList<TrackedReference> CaptureAndRebuildTranscript(ChatViewModel surface)
    {
        var references = new List<TrackedReference>();
        var realizedHostCount = 0;
        foreach (var turn in surface.MountedTranscriptTurns.TakeLast(16))
        {
            references.Add(new TrackedReference("replaced-transcript-turn", new WeakReference(turn)));
            foreach (var item in turn.Items.Take(4))
                references.Add(new TrackedReference("replaced-transcript-item", new WeakReference(item)));
            if (turn.RealizedItemsHost is { } host)
            {
                references.Add(new TrackedReference("replaced-transcript-host", new WeakReference(host)));
                realizedHostCount++;
            }
        }

        if (realizedHostCount == 0)
            throw new InvalidOperationException("Transcript rebuild scenario did not find a realized transcript host.");

        surface.RebuildTranscript();
        return references;
    }

    private async Task EnsureTranscriptHostsRealizedAsync(ChatViewModel surface)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await DrainAsync(DispatcherPriority.Loaded);
            await OnUiAsync(() => TranscriptRealizationScheduler.Instance.FlushAll());
            await DrainAsync(DispatcherPriority.Render);

            var hasRealizedHost = await OnUiAsync(() =>
                surface.MountedTranscriptTurns.Any(static turn => turn.RealizedItemsHost is not null));
            if (hasRealizedHost)
                return;

            await Task.Delay(25);
        }

        throw new InvalidOperationException("Transcript controls did not realize within the harness timeout.");
    }

    private static IReadOnlyList<TrackedReference> RequireTrackedReferences(
        string scenario,
        IReadOnlyList<TrackedReference> references)
    {
        if (references.Count == 0)
            throw new InvalidOperationException($"{scenario} did not produce any expected-dead references.");

        return references;
    }

    private async Task<DetachedChatWindowHost> OpenProductionChatWindowAsync(Guid chatId)
    {
        var chat = _dataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == chatId)
            ?? throw new InvalidOperationException($"Could not find detached-window workload chat '{chatId}'.");
        var existingWindows = await OnUiAsync(() =>
            _snapshotDetachedWindows().ToHashSet(ReferenceEqualityComparer.Instance));

        await OnUiAsync(() => _mainVm.OpenChatInNewWindowCommand.ExecuteAsync(chat));

        for (var attempt = 0; attempt < 120; attempt++)
        {
            var host = await OnUiAsync(() =>
            {
                var window = _snapshotDetachedWindows()
                    .FirstOrDefault(candidate => !existingWindows.Contains(candidate));
                if (window?.DataContext is not ChatWindowViewModel viewModel
                    || viewModel.ChatVM.CurrentChat?.Id != chatId)
                {
                    return null;
                }

                var workspace = window.FindControl<ChatWorkspaceView>("DetachedChatView");
                if (workspace is null)
                    return null;

                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.Opacity = 0.01;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Position = new PixelPoint(-20000, -20000);

                return new DetachedChatWindowHost
                {
                    Window = window,
                    ViewModel = viewModel,
                    Workspace = workspace,
                };
            });

            if (host is not null)
                return host;

            await DrainAsync(DispatcherPriority.Loaded);
            await Task.Delay(25);
        }

        throw new InvalidOperationException("The production detached chat window did not open within the harness timeout.");
    }

    private async Task<IReadOnlyList<TrackedReference>> CloseProductionChatWindowAsync(
        DetachedChatWindowHost host)
    {
        ChatWindow? window = host.Window
            ?? throw new InvalidOperationException("Detached chat window was already released.");
        var references = await OnUiAsync(() => CaptureAndCloseProductionChatWindow(host));

        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (!await OnUiAsync(() => _isDetachedWindowTracked(window)))
            {
                window = null;
                return references;
            }

            await DrainAsync(DispatcherPriority.Background);
            await Task.Delay(25);
        }

        throw new InvalidOperationException("The closed detached chat window remained in App window registries.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IReadOnlyList<TrackedReference> CaptureAndCloseProductionChatWindow(
        DetachedChatWindowHost host)
    {
        var window = host.Window
            ?? throw new InvalidOperationException("Detached chat window was already released.");
        var viewModel = host.ViewModel
            ?? throw new InvalidOperationException("Detached chat window VM was already released.");
        var workspace = host.Workspace
            ?? throw new InvalidOperationException("Detached chat workspace was already released.");

        var references = new List<TrackedReference>
        {
            new("closed-chat-window", new WeakReference(window)),
            new("closed-window-view-model", new WeakReference(viewModel)),
            new("closed-chat-workspace", new WeakReference(workspace)),
        };
        if (workspace.ChatView is { } chatView)
            references.Add(new TrackedReference("closed-chat-view", new WeakReference(chatView)));

        window.Close();
        host.Window = null;
        host.ViewModel = null;
        host.Workspace = null;
        return references;
    }

    private Task OpenChatAsync(Guid chatId)
        => OnUiAsync(async () => await _mainVm.OpenChatByIdAsync(chatId));

    private async Task<MemoryCycleSample> CaptureAfterGcAsync(
        int cycle,
        IReadOnlyList<TrackedReference> references)
    {
        await CollectGarbageAsync();

        var retainedByKind = references
            .Where(static reference => reference.Reference.IsAlive)
            .GroupBy(static reference => reference.Kind, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Count(),
                StringComparer.Ordinal);
        var gcInfo = GC.GetGCMemoryInfo();

        using var process = Process.GetCurrentProcess();
        process.Refresh();

        return new MemoryCycleSample
        {
            Cycle = cycle,
            ManagedBytes = GC.GetTotalMemory(forceFullCollection: false),
            HeapSizeBytes = gcInfo.HeapSizeBytes,
            CommittedBytes = gcInfo.TotalCommittedBytes,
            FragmentedBytes = gcInfo.FragmentedBytes,
            WorkingSetBytes = process.WorkingSet64,
            PrivateBytes = process.PrivateMemorySize64,
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            ThreadCount = process.Threads.Count,
            HandleCount = TryGetHandleCount(process),
            TrackedCount = references.Count,
            RetainedCount = retainedByKind.Values.Sum(),
            RetainedByKind = retainedByKind,
        };
    }

    private async Task CollectGarbageAsync()
    {
        await SettleAsync(_options.SettleMilliseconds);
        for (var pass = 0; pass < _options.GcPasses; pass++)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            await Task.Delay(25);
            await DrainAsync(DispatcherPriority.Background);
        }
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    private string ResolveThumbnailSource()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, "src", "Lumi", "Assets", "lumi-icon.png"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "lumi-icon.png"),
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Could not find Lumi's PNG asset for thumbnail churn.");
    }

    private void WriteJsonReport(MemoryStressReport report)
    {
        var primaryPath = ResolveOutputPath();
        var directory = Path.GetDirectoryName(primaryPath)
            ?? throw new InvalidOperationException($"The report path '{primaryPath}' has no directory.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(primaryPath, report.ToJson());
        Console.WriteLine($"[memory] JSON report written to: {primaryPath}");

        var latestPath = Path.Combine(directory, "memory-report-latest.json");
        if (!string.Equals(primaryPath, latestPath, StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(latestPath, report.ToJson());
            Console.WriteLine($"[memory] Latest report copied to: {latestPath}");
        }
    }

    private string ResolveOutputPath()
    {
        if (!string.IsNullOrWhiteSpace(_options.OutputPath))
            return Path.GetFullPath(_options.OutputPath);

        var root = FindRepositoryRoot(Environment.CurrentDirectory)
            ?? FindRepositoryRoot(AppContext.BaseDirectory);
        var directory = root is null
            ? Path.Combine(Path.GetTempPath(), "Lumi-memory-stress")
            : Path.Combine(root, "diagnostics", "memory");
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(directory, $"memory-report-{timestamp}.json");
    }

    private static string? FindRepositoryRoot(string startPath)
    {
        var directory = new DirectoryInfo(startPath);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }

    private void TryDeleteThumbnailDirectory()
    {
        try
        {
            if (Directory.Exists(_thumbnailDirectory))
                Directory.Delete(_thumbnailDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[memory] Could not remove temporary thumbnail directory: {ex.Message}");
        }
    }

    private static int TryGetHandleCount(Process process)
    {
        try
        {
            return process.HandleCount;
        }
        catch (PlatformNotSupportedException)
        {
            return 0;
        }
    }

    private static async Task SettleAsync(int quietMilliseconds)
    {
        await DrainAsync(DispatcherPriority.Background);
        if (quietMilliseconds > 0)
            await Task.Delay(quietMilliseconds);
        await DrainAsync(DispatcherPriority.Background);
    }

    private static async Task DrainAsync(DispatcherPriority priority)
        => await Dispatcher.UIThread.InvokeAsync(() => { }, priority);

    private static async Task<T> OnUiAsync<T>(Func<T> func)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return func();
        return await Dispatcher.UIThread.InvokeAsync(func);
    }

    private static async Task<T> OnUiAsync<T>(Func<Task<T>> func)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return await func().ConfigureAwait(true);
        return await Dispatcher.UIThread.InvokeAsync(func);
    }

    private static Task OnUiAsync(Action action)
        => OnUiAsync(() =>
        {
            action();
            return true;
        });

    private static Task OnUiAsync(Func<Task> func)
        => OnUiAsync(async () =>
        {
            await func().ConfigureAwait(true);
            return true;
        });

    private static double ToMiB(long bytes) => bytes / (1024d * 1024d);
}
#endif
