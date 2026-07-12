#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using Lumi.Services;
using Lumi.ViewModels;

namespace Lumi.UiPerf;

/// <summary>
/// Drives a catalog of real UX actions through Lumi's actual ViewModels/Views on a debug instance
/// preloaded with heavy scenario chats, while a background probe samples UI-thread responsiveness.
/// Produces a report ranking UX actions/categories by how slow or non-responsive they feel to the
/// user. Supports full mode (every category) and filtered mode (an explicit subset).
/// </summary>
internal sealed class UiResponsivenessHarness
{
    private const int MaxScrollSteps = 40;
    private const int GateFailureExitCode = 3;

    private readonly MainViewModel _mainVm;
    private readonly DataStore _dataStore;
    private readonly UiHarnessOptions _options;
    private readonly Action<int> _requestShutdown;
    private readonly UiResponsivenessProbe _probe;
    private int _exitCode;

    /// <summary>Describes one measurable UX action.</summary>
    private sealed record UiAction(
        string Id,
        string Category,
        string DisplayName,
        Func<Task> RunAsync,
        Func<Task>? Prepare = null,
        string? Note = null);

    private readonly record struct RawMeasurement(double RunMs, double PostActionMs, IReadOnlyList<double> Latencies);

    public UiResponsivenessHarness(
        MainViewModel mainVm,
        DataStore dataStore,
        UiHarnessOptions options,
        Action<int> requestShutdown)
    {
        _mainVm = mainVm;
        _dataStore = dataStore;
        _options = options;
        _requestShutdown = requestShutdown;
        _probe = new UiResponsivenessProbe(options.SampleIntervalMs);
    }

    public async Task RunAsync()
    {
        UiStreamingLoad? load = null;
        try
        {
            Console.WriteLine();
            Console.WriteLine($"[ui-perf] Starting UI responsiveness harness — mode={_options.Mode}, " +
                              $"iterations={_options.Iterations} (warmup {_options.WarmupIterations})");
            if (!_options.IsFull)
                Console.WriteLine($"[ui-perf] Filtered categories: {string.Join(", ", _options.RequestedCategories)}");

            var scenarios = new UiWorkloadScenarios(_dataStore);
            await OnUiAsync(() =>
            {
                scenarios.Seed();
                _mainVm.SelectedProjectFilter = null;
                _mainVm.SelectedNavIndex = 0;
                _mainVm.RefreshChatList();
            });
            Console.WriteLine($"[ui-perf] Seeded {scenarios.TotalChats} scenario chats. Warming up the probe...");
            VerifyNavIndices();

            _probe.Start();
            await SettleAsync(Math.Max(400, _options.SettleQuietMs * 3));

            var results = new List<UiActionSamples>();
            var failed = new List<string>();

            // Cold one-shot realizations must run before anything else realizes those page views.
            foreach (var action in BuildColdNavigationActions())
            {
                if (!_options.IncludesCategory(action.Category))
                    continue;
                var samples = await TryMeasureActionAsync(action, iterations: 1, warmup: 0, failed);
                if (samples is not null)
                    results.Add(samples);
            }

            // Spin up concurrent "running chats" load (if requested) AFTER the cold one-shot nav
            // realizations, so every iterated action below is measured under the same realistic load
            // a user feels when several agents are streaming at once.
            if (_options.RunningChats > 0)
            {
                await OnUiAsync(() =>
                {
                    scenarios.SeedActiveWorkChats(_options.RunningChats);
                    _mainVm.RefreshChatList();
                });
                load = new UiStreamingLoad(_mainVm);
                await load.StartAsync(scenarios.ActiveWorkChatIds);
                Console.WriteLine($"[ui-perf] Concurrent load: {load.Count} running (streaming) chat(s) active during measurement.");
                await SettleAsync(Math.Max(400, _options.SettleQuietMs * 3));
            }

            var mdBefore = StrataTheme.Controls.StrataMarkdown.CaptureDiagnostics();
            var ttcBefore = TranscriptTurnControl.CaptureDiagnostics();
            var ttxBefore = Lumi.Views.Controls.TranscriptTextContent.CaptureDiagnostics();

            foreach (var action in BuildIteratedActions(scenarios))
            {
                if (!_options.IncludesCategory(action.Category))
                    continue;
                var samples = await TryMeasureActionAsync(action, _options.Iterations, _options.WarmupIterations, failed);
                if (samples is not null)
                    results.Add(samples);
            }

            var mdDelta = StrataTheme.Controls.StrataMarkdown.CaptureDiagnostics() - mdBefore;
            var ttcAfter = TranscriptTurnControl.CaptureDiagnostics();
            var ttxDelta = Lumi.Views.Controls.TranscriptTextContent.CaptureDiagnostics() - ttxBefore;
            Console.WriteLine($"[ui-perf][diag] StrataMarkdown instances={mdDelta.InstanceCount} rebuilds={mdDelta.RebuildCount} fullParse={mdDelta.FullParseCount} totalRebuildMs={mdDelta.TotalRebuildMilliseconds:n0} avgRebuildMs={mdDelta.AverageRebuildMilliseconds:n2} | TTX instances={ttxDelta.InstanceCount} mdBranch={ttxDelta.MarkdownBranchCount} | TTC created={ttcAfter.ControlCreateCount - ttcBefore.ControlCreateCount} itemHosts={ttcAfter.ItemHostCreateCount - ttcBefore.ItemHostCreateCount}");

            var report = UiResponsivenessReport.Build(_options, results);
            Console.WriteLine();
            Console.WriteLine(report.ToConsole());

            if (failed.Count > 0)
                Console.WriteLine($"[ui-perf] {failed.Count} action(s) failed and were skipped: {string.Join(", ", failed)}");

            WriteJsonReport(report);

            if (report.GateFailed)
                _exitCode = GateFailureExitCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[ui-perf] Harness failed: " + ex);
            _exitCode = 1;
        }
        finally
        {
            if (load is not null && !_options.KeepOpen)
                await load.StopAsync();
            _probe.Dispose();
            if (_options.KeepOpen)
                Console.WriteLine("[ui-perf] --ui-perf-keep-open set; leaving the window and simulated streams active for inspection.");
            else
                _requestShutdown(_exitCode);
        }
    }

    /// <summary>Wraps a single action measurement so one failing action never aborts the whole run.</summary>
    private async Task<UiActionSamples?> TryMeasureActionAsync(UiAction action, int iterations, int warmup, List<string> failed)
    {
        try
        {
            return await MeasureActionAsync(action, iterations, warmup);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ui-perf] Action '{action.Id}' ({action.DisplayName}) failed and was skipped: {ex.Message}");
            failed.Add(action.Id);
            return null;
        }
    }

    // ---- Action catalogs -------------------------------------------------

    private static readonly (int Index, string Name)[] NavPages =
    {
        (2, "Projects"),
        (3, "Skills"),
        (4, "Lumis"),
        (5, "Memories"),
        (6, "MCP servers"),
        (1, "Jobs"),
        (7, "Settings"),
    };

    private IEnumerable<UiAction> BuildColdNavigationActions()
    {
        foreach (var (index, name) in NavPages)
        {
            yield return new UiAction(
                $"nav-cold-{index}",
                "Navigation",
                $"Open {name} page (first time)",
                RunAsync: () => OnUiAsync(() => _mainVm.SelectedNavIndex = index),
                Note: "First realization of the page view — heavy XAML/template inflation happens here.");
        }
    }

    /// <summary>
    /// Guards against silent rot: if MainViewModel's nav-index→page mapping ever changes, the harness
    /// would otherwise mislabel pages. We compare our labels against the VM's single source of truth
    /// (<see cref="MainViewModel.DescribeNavPage"/>) and warn on drift instead of failing.
    /// </summary>
    private static void VerifyNavIndices()
    {
        foreach (var (index, name) in NavPages)
        {
            var actual = MainViewModel.DescribeNavPage(index);
            if (actual.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0)
                Console.WriteLine(
                    $"[ui-perf][warn] Nav index {index} expected '{name}' but MainViewModel maps it to '{actual}'. " +
                    "Update UiResponsivenessHarness.NavPages to match.");
        }
    }

    private IEnumerable<UiAction> BuildIteratedActions(UiWorkloadScenarios scenarios)
    {
        // Navigation (warm switching between already-realized pages).
        yield return new UiAction(
            "nav-warm-settings", "Navigation", "Switch to Settings (warm)",
            RunAsync: () => OnUiAsync(() => _mainVm.SelectedNavIndex = 7),
            Prepare: () => OnUiAsync(() => _mainVm.SelectedNavIndex = 0));
        yield return new UiAction(
            "nav-warm-projects", "Navigation", "Switch to Projects (warm)",
            RunAsync: () => OnUiAsync(() => _mainVm.SelectedNavIndex = 2),
            Prepare: () => OnUiAsync(() => _mainVm.SelectedNavIndex = 0));
        yield return new UiAction(
            "nav-warm-chat", "Navigation", "Return to Chat page (warm)",
            RunAsync: () => OnUiAsync(() => _mainVm.SelectedNavIndex = 0),
            Prepare: () => OnUiAsync(() => _mainVm.SelectedNavIndex = 5));

        // Chat open (cold load + transcript rebuild each time).
        yield return OpenChatAction("chat-open-tiny", "Open tiny chat", scenarios.TinyChatId,
            "Baseline: opening a near-empty chat.");
        yield return OpenChatAction("chat-open-medium", "Open medium chat (~80 msgs)", scenarios.MediumChatId,
            "Transcript rebuild for a medium history.");
        yield return OpenChatAction("chat-open-large", "Open large chat (~240 msgs)", scenarios.LargeChatId,
            "Transcript rebuild + paging window mount for a large history.");
        yield return OpenChatAction("chat-open-huge", "Open huge chat (~600 msgs)", scenarios.HugeChatId,
            "Heaviest transcript rebuild; first-screen mount cost dominates perceived open latency.");
        yield return OpenChatAction("chat-open-tool-heavy", "Open tool-heavy chat", scenarios.ToolHeavyChatId,
            "Many tool-call cards and subagent groups inflate the transcript.");
        yield return OpenChatAction("chat-open-markdown", "Open markdown-heavy chat", scenarios.MarkdownHeavyChatId,
            "Large markdown documents (tables + code) per message stress the markdown renderer.");

        // Chat switch (alternate between two heavy chats).
        yield return new UiAction(
            "chat-switch-heavy", "Chat switch", "Alternate between two heavy chats",
            RunAsync: async () =>
            {
                await OpenChatAsync(scenarios.LargeChatId);
                await OpenChatAsync(scenarios.ToolHeavyChatId);
            },
            Prepare: () => OpenChatAsync(scenarios.MediumChatId),
            Note: "Switching tears down and rebuilds the transcript twice.");

        // Chat switch between two power-user "mega" chats whose mounted tail turns each carry a large
        // assistant payload (big code blocks / wide tables / long prose, ~15-25KB per turn). This is
        // the faithful reproduction of the heavy switch lag a real coding user feels: the paging
        // weight model caps per-message weight, so these mount like a normal chat yet cost far more
        // to re-realize than the moderate "heavy" chats above.
        yield return new UiAction(
            "chat-switch-mega", "Chat switch", "Alternate between two large real-content chats",
            RunAsync: async () =>
            {
                await OpenChatAsync(scenarios.CodeHeavyChatId);
                await OpenChatAsync(scenarios.DocHeavyChatId);
            },
            Prepare: () => OpenChatAsync(scenarios.MediumChatId),
            Note: "Re-realizes a transcript tail of large code blocks / tables / prose on every switch.");

        // Chat switch under concurrent agent load: alternate between two chats that are themselves
        // actively streaming, while the rest of the running-chats load streams in the background. This
        // is the scenario users actually feel as slow — a static two-chat switch badly understates it.
        if (_options.RunningChats >= 2 && scenarios.ActiveWorkChatIds.Count >= 2)
        {
            var liveA = scenarios.ActiveWorkChatIds[0];
            var liveB = scenarios.ActiveWorkChatIds[1];
            yield return new UiAction(
                "chat-switch-live", "Chat switch", "Alternate between two live (streaming) chats",
                RunAsync: async () =>
                {
                    await OpenChatAsync(liveA);
                    await OpenChatAsync(liveB);
                },
                Prepare: () => OpenChatAsync(scenarios.MediumChatId),
                Note: "Switching between actively-streaming chats while other agents stream concurrently.");
        }

        // Transcript scroll (load older history on scroll-up).
        yield return new UiAction(
            "scroll-huge", "Transcript scroll", "Scroll up through huge chat history",
            RunAsync: DriveScrollToTopAsync,
            Prepare: () => OpenChatAsync(scenarios.HugeChatId),
            Note: "Each scroll-up prepends an older transcript page and re-renders mounted turns.");
        yield return new UiAction(
            "scroll-markdown", "Transcript scroll", "Scroll up through markdown-heavy chat",
            RunAsync: DriveScrollToTopAsync,
            Prepare: () => OpenChatAsync(scenarios.MarkdownHeavyChatId),
            Note: "Prepending pages of large markdown documents while scrolling.");

        // Composer (per-keystroke latency).
        yield return new UiAction(
            "composer-type-heavy", "Composer", "Type while a large transcript is mounted",
            RunAsync: () => DriveComposerTypingAsync(
                "The quick brown fox jumps over the lazy dog while Lumi measures composer responsiveness."),
            Prepare: () => OpenChatAsync(scenarios.LargeChatId),
            Note: "Per-keystroke latency in the composer with a heavy transcript visible.");
        yield return new UiAction(
            "composer-type-newchat", "Composer", "Type into composer on a new chat",
            RunAsync: () => DriveComposerTypingAsync(
                "Hello Lumi, this is a quick composer responsiveness probe message."),
            Prepare: NewChatAsync);

        // Chat list (sidebar).
        yield return new UiAction(
            "chatlist-refresh", "Chat list", "Rebuild the chat sidebar list",
            RunAsync: () => OnUiAsync(() => _mainVm.RefreshChatList()),
            Prepare: () => OnUiAsync(() => _mainVm.SelectedProjectFilter = null),
            Note: "Rebuilding the grouped chat sidebar with many chats.");
        yield return new UiAction(
            "chatlist-project-filter", "Chat list", "Apply a project filter to the chat list",
            RunAsync: () => OnUiAsync(() => _mainVm.SelectedProjectFilter = scenarios.ProjectId),
            Prepare: () => OnUiAsync(() => _mainVm.SelectedProjectFilter = null),
            Note: "Filtering the sidebar rebuilds the grouped list.");
        yield return new UiAction(
            "chatlist-load-more", "Chat list", "Load more chats (paging)",
            RunAsync: () => OnUiAsync(() => _mainVm.LoadMoreChats()),
            Prepare: () => OnUiAsync(() =>
            {
                _mainVm.SelectedProjectFilter = null;
                _mainVm.RefreshChatList();
            }),
            Note: "Growing the sidebar page size and re-grouping.");

        // New chat.
        yield return new UiAction(
            "new-chat", "New chat", "Start a new chat from a heavy chat",
            RunAsync: NewChatAsync,
            Prepare: () => OpenChatAsync(scenarios.LargeChatId),
            Note: "Clearing a heavy transcript and showing the welcome composer.");

        // Search.
        yield return new UiAction(
            "search-open-type", "Search", "Open global search and type a query",
            RunAsync: () => DriveSearchAsync("responsive latency dispatcher transcript"),
            Prepare: () => OnUiAsync(() => _mainVm.SearchOverlayVM.Close()),
            Note: "Global search overlay open + incremental query across indexed chats.");
    }

    private UiAction OpenChatAction(string id, string display, Guid chatId, string note) => new(
        id,
        "Chat open",
        display,
        RunAsync: () => OpenChatAsync(chatId),
        Prepare: NewChatAsync,
        Note: note);

    // ---- Action drivers --------------------------------------------------

    private Task OpenChatAsync(Guid chatId)
        => OnUiAsync(async () => await _mainVm.OpenChatByIdAsync(chatId));

    private Task NewChatAsync()
        => OnUiAsync(() => _mainVm.NewChatCommand.Execute(null));

    private async Task DriveComposerTypingAsync(string text)
    {
        await OnUiAsync(() => _mainVm.ChatVM.PromptText = string.Empty);
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(ch);
            var snapshot = builder.ToString();
            await OnUiAsync(() => _mainVm.ChatVM.PromptText = snapshot);
            await DrainAsync(DispatcherPriority.Render);
        }

        await OnUiAsync(() => _mainVm.ChatVM.PromptText = string.Empty);
    }

    private async Task DriveScrollToTopAsync()
    {
        var chatVm = _mainVm.ChatVM;
        // Drive off the paging mutation, not the mounted-turn count: the mounted window is capped
        // (MaxMountedPages) and trims its tail on every prepend, so the turn count plateaus long
        // before the head is reached. A Prepend means an older page actually loaded; once the
        // viewport update stops returning Prepend, we've reached the top of the history.
        for (var step = 0; step < MaxScrollSteps; step++)
        {
            var mutation = await OnUiAsync(() => chatVm.UpdateTranscriptViewport(
                offsetY: 8d,
                viewportHeight: 720d,
                extentHeight: 12000d,
                isPinnedToBottom: false,
                distanceFromBottom: 11000d));
            await DrainAsync(DispatcherPriority.Render);
            if (mutation.Kind != TranscriptWindowMutationKind.Prepend)
                break;
        }
    }

    private async Task DriveSearchAsync(string query)
    {
        await OnUiAsync(() => _mainVm.SearchOverlayVM.Open());
        await DrainAsync(DispatcherPriority.Render);

        var builder = new StringBuilder(query.Length);
        foreach (var ch in query)
        {
            builder.Append(ch);
            var snapshot = builder.ToString();
            await OnUiAsync(() => _mainVm.SearchOverlayVM.SearchQuery = snapshot);
            await DrainAsync(DispatcherPriority.Render);
        }

        // Let the debounced search settle, then close.
        await SettleAsync(Math.Max(200, _options.SettleQuietMs));
        await OnUiAsync(() => _mainVm.SearchOverlayVM.Close());
    }

    // ---- Measurement core ------------------------------------------------

    private async Task<UiActionSamples> MeasureActionAsync(UiAction action, int iterations, int warmup)
    {
        Console.WriteLine($"[ui-perf] Measuring: {action.DisplayName} [{action.Category}]");

        for (var i = 0; i < warmup; i++)
        {
            try
            {
                await MeasureOnceAsync(action);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ui-perf]   warmup {i + 1} failed: {ex.Message}");
            }
        }

        var samples = new UiActionSamples
        {
            ActionId = action.Id,
            Category = action.Category,
            DisplayName = action.DisplayName,
            Note = action.Note,
        };

        var ok = 0;
        for (var i = 0; i < iterations; i++)
        {
            try
            {
                var measurement = await MeasureOnceAsync(action);
                samples.RunDurationsMs.Add(measurement.RunMs);
                samples.PostActionDurationsMs.Add(measurement.PostActionMs);
                samples.LatenciesMs.AddRange(measurement.Latencies);
                samples.IterationMaxMs.Add(measurement.Latencies.Count > 0 ? measurement.Latencies.Max() : 0d);
                ok++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ui-perf]   iteration {i + 1} failed: {ex.Message}");
            }
        }

        // A fully-failing action would otherwise masquerade as a fast/Good result. Surface it instead.
        if (ok == 0)
            throw new InvalidOperationException($"all {iterations} measured iteration(s) failed");

        samples.Iterations = ok;
        return samples;
    }

    private async Task<RawMeasurement> MeasureOnceAsync(UiAction action)
    {
        if (action.Prepare is not null)
        {
            await action.Prepare();
            await SettleAsync(_options.SettleQuietMs);
        }

        // Establish a quiet baseline so pre-action work isn't attributed to this measurement.
        await SettleAsync(_options.SettleQuietMs);

        var start = _probe.NowMs;
        var stopwatch = Stopwatch.StartNew();
        await action.RunAsync();
        var runMs = stopwatch.Elapsed.TotalMilliseconds;

        // Drain the deferred UI work the action queued. A Background-priority drain only completes once
        // the UI thread reaches idle, so any in-flight stall fully resolves and is captured by the probe.
        // The window CLOSES here — before SettleAsync's fixed quiet delay — so that deliberate idle
        // padding never floods the pool with near-zero samples and dilutes the percentiles.
        var postStopwatch = Stopwatch.StartNew();
        await DrainAsync(DispatcherPriority.Background);
        var postActionMs = postStopwatch.Elapsed.TotalMilliseconds;
        var end = _probe.NowMs;

        var latencies = _probe.LatenciesInWindow(start, end);
        return new RawMeasurement(runMs, postActionMs, latencies);
    }

    /// <summary>Waits until the UI thread is quiescent (drains below render, then a quiet gap).</summary>
    private static async Task SettleAsync(int quietMs)
    {
        await DrainAsync(DispatcherPriority.Background);
        if (quietMs > 0)
            await Task.Delay(quietMs);
        await DrainAsync(DispatcherPriority.Background);
    }

    private static async Task DrainAsync(DispatcherPriority priority)
        => await Dispatcher.UIThread.InvokeAsync(() => { }, priority);

    // ---- Report output ---------------------------------------------------

    private void WriteJsonReport(UiResponsivenessReport report)
    {
        try
        {
            var json = report.ToJson();
            var primaryPath = ResolveOutputPath();
            Directory.CreateDirectory(Path.GetDirectoryName(primaryPath)!);
            File.WriteAllText(primaryPath, json);
            Console.WriteLine($"[ui-perf] JSON report written to: {primaryPath}");

            var latestPath = Path.Combine(Path.GetDirectoryName(primaryPath)!, "report-latest.json");
            if (!string.Equals(latestPath, primaryPath, StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(latestPath, json);
                Console.WriteLine($"[ui-perf] Latest report copied to: {latestPath}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[ui-perf] Failed to write JSON report: " + ex.Message);
        }
    }

    private string ResolveOutputPath()
    {
        if (!string.IsNullOrWhiteSpace(_options.OutputPath))
            return Path.GetFullPath(_options.OutputPath);

        var dir = Path.Combine(Path.GetTempPath(), "Lumi-ui-perf");
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(dir, $"report-{timestamp}.json");
    }

    // ---- UI-thread marshaling --------------------------------------------

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
        => OnUiAsync(() => { action(); return true; });

    private static Task OnUiAsync(Func<Task> func)
        => OnUiAsync(async () => { await func().ConfigureAwait(true); return true; });
}
#endif
