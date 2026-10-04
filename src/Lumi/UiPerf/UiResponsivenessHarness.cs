#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;

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

    private readonly record struct RawMeasurement(
        double RunMs,
        double PostActionMs,
        IReadOnlyList<double> Latencies,
        long UiAllocatedBytes,
        int Gen2Collections);

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
                _mainVm.RefreshProjects();
                _mainVm.ProjectsVM.RefreshFromStore();
                _mainVm.SkillsVM.RefreshFromStore();
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
            Console.WriteLine($"[ui-perf][diag] StrataMarkdown instances={mdDelta.InstanceCount} rebuilds={mdDelta.RebuildCount} fullParse={mdDelta.FullParseCount} totalRebuildMs={mdDelta.TotalRebuildMilliseconds:n0} avgRebuildMs={mdDelta.AverageRebuildMilliseconds:n2} | TTX instances={ttxDelta.InstanceCount} mdBranch={ttxDelta.MarkdownBranchCount} | TTC created={ttcAfter.ControlCreateCount - ttcBefore.ControlCreateCount} itemHosts={ttcAfter.ItemHostCreateCount - ttcBefore.ItemHostCreateCount} activeHosts={ttcAfter.ActiveRealizedHostCount} peakHosts={ttcAfter.PeakActiveRealizedHostCount}");

            var report = UiResponsivenessReport.Build(_options, results, failed);
            Console.WriteLine();
            Console.WriteLine(report.ToConsole());

            if (failed.Count > 0)
                Console.WriteLine($"[ui-perf] {failed.Count} action(s) failed and were skipped: {string.Join(", ", failed)}");

            WriteJsonReport(report);

            if (!report.IsComplete)
                _exitCode = 1;
            else if (report.GateFailed)
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

    private static readonly (int Index, string Name, string ControlName)[] NavPages =
    {
        (2, "Projects", "NavProjects"),
        (3, "Skills", "NavSkills"),
        (4, "Lumis", "NavAgents"),
        (5, "Memories", "NavMemories"),
        (6, "MCP servers", "NavMcpServers"),
        (1, "Jobs", "NavJobs"),
        (7, "Settings", "NavSettings"),
        (8, "Library", "LibraryEntryButton"),
    };

    private IEnumerable<UiAction> BuildColdNavigationActions()
    {
        foreach (var (index, name, controlName) in NavPages)
        {
            yield return new UiAction(
                $"nav-cold-{index}",
                "Navigation",
                $"Open {name} page (first time)",
                RunAsync: () => ClickNavigationAsync(index, controlName),
                Prepare: () => OnUiAsync(() => _mainVm.SelectedNavIndex = 0),
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
        foreach (var (index, name, _) in NavPages)
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
            RunAsync: () => ClickNavigationAsync(7, "NavSettings"),
            Prepare: () => OnUiAsync(() => _mainVm.SelectedNavIndex = 0));
        yield return new UiAction(
            "nav-warm-projects", "Navigation", "Switch to Projects (warm)",
            RunAsync: () => ClickNavigationAsync(2, "NavProjects"),
            Prepare: () => OnUiAsync(() => _mainVm.SelectedNavIndex = 0));
        yield return new UiAction(
            "nav-warm-chat", "Navigation", "Return to Chat page (warm)",
            RunAsync: () => ClickNavigationAsync(0, "NavChat"),
            Prepare: () => OnUiAsync(() => _mainVm.SelectedNavIndex = 5));

        yield return new UiAction(
            "project-switcher-open", "Project selection", "Open the chat project switcher",
            RunAsync: () => SetProjectSwitcherAsync(true),
            Prepare: PrepareProjectSelectionAsync,
            Note: "Invokes the real switcher button and observes layout throughout its drawer animation.");
        yield return new UiAction(
            "project-select-draft", "Project selection", "Select a project in a new chat",
            RunAsync: async () =>
            {
                await SelectProjectRowAsync(scenarios.ProjectId);
                if (await OnUiAsync(() => _mainVm.ChatVM.CurrentChat is { Messages.Count: > 0 }
                    || _mainVm.ChatVM.ActiveProjectFilterId != scenarios.ProjectId))
                    throw new InvalidOperationException("Project selection did not preserve the draft context.");
            },
            Prepare: async () =>
            {
                await PrepareProjectSelectionAsync();
                await SetProjectSwitcherAsync(true);
            });
        yield return new UiAction(
            "project-select-chat", "Project selection", "Select a project from an existing chat",
            RunAsync: async () =>
            {
                await SelectProjectRowAsync(scenarios.ProjectId);
                await WaitForUiAsync(() => _mainVm.ActiveChatId == scenarios.ProjectChatId,
                    "the project's most recent chat to open");
            },
            Prepare: async () =>
            {
                await PrepareProjectSelectionAsync();
                await OpenChatAsync(scenarios.LargeChatId);
                await SetProjectSwitcherAsync(true);
            },
            Note: "Includes the fire-and-forget project navigation, not just assigning the filter.");

        yield return new UiAction(
            "sidebar-chat-select", "Chat open", "Select a chat in the sidebar",
            RunAsync: () => SelectSidebarChatAsync(scenarios.MediumChatId),
            Prepare: async () =>
            {
                await OnUiAsync(() => _mainVm.SelectedProjectFilter = null);
                await NewChatAsync();
            },
            Note: "Exercises the real ListBox selection handler, command and transcript mount.");
        yield return new UiAction(
            "unread-inbox-open", "Unread inbox", "Open the unread inbox drawer",
            RunAsync: () => SetUnreadDrawerAsync(true),
            Prepare: () => PrepareUnreadAsync(scenarios, outsideFilter: true, openDrawer: false),
            Note: "Measures the actual inbox button and animated drawer layout.");
        foreach (var outsideFilter in new[] { false, true })
        {
            yield return new UiAction(
                outsideFilter ? "unread-select-other-project" : "unread-select-current-project",
                "Unread inbox",
                outsideFilter ? "Click an unread chat in another project" : "Click an unread chat in the current project",
                RunAsync: () => SelectUnreadChatAsync(scenarios.UnreadChatId),
                Prepare: () => PrepareUnreadAsync(scenarios, outsideFilter, openDrawer: true),
                Note: "Verifies the chosen reply is opened and marked read, with the correct project filter.");
        }

        // Chat open (cold load + transcript rebuild each time).
        yield return OpenChatAction("chat-open-tiny", "Open tiny chat", scenarios.TinyChatId,
            "Baseline: opening a near-empty chat.");
        yield return OpenChatAction("chat-open-medium", "Open medium chat (~80 msgs)", scenarios.MediumChatId,
            "Transcript rebuild for a medium history.");
        yield return OpenChatAction("chat-open-large", "Open large chat (~240 msgs)", scenarios.LargeChatId,
            "Transcript rebuild + stable placeholder creation for a large history.");
        yield return OpenChatAction("chat-open-huge", "Open huge chat (~600 msgs)", scenarios.HugeChatId,
            "Heaviest transcript rebuild; first-screen mount cost dominates perceived open latency.");
        yield return OpenChatAction("chat-open-mega", "Open mega chat (~1,000 turns)", scenarios.MegaChatId,
            "Creates 1,000 stable lightweight turn placeholders and realizes only the tail viewport.");
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
            Note: "Scrolls through stable placeholders while nearby heavy transcript turns realize.");
        yield return new UiAction(
            "scroll-markdown", "Transcript scroll", "Scroll up through markdown-heavy chat",
            RunAsync: DriveScrollToTopAsync,
            Prepare: () => OpenChatAsync(scenarios.MarkdownHeavyChatId),
            Note: "Scrolls through stable placeholders while nearby large markdown turns realize.");
        yield return new UiAction(
            "scroll-mega-fast", "Transcript scroll", "Fast-traverse 1,000-turn chat",
            RunAsync: DriveScrollToTopAsync,
            Prepare: () => OpenChatAsync(scenarios.MegaChatId),
            Note: "Rapidly traverses the full stable geometry, then realizes only the stopped viewport.");
        yield return new UiAction(
            "scroll-mega-stops", "Transcript scroll", "Pause through 1,000-turn chat",
            RunAsync: DriveScrollWithStopsAsync,
            Prepare: () => OpenChatAsync(scenarios.MegaChatId),
            Note: "Realizes and releases sixteen evenly-spaced viewports while preserving the native anchor.");
        yield return new UiAction(
            "scroll-mega-roundtrip", "Transcript scroll", "Repeat top/bottom jumps in 1,000-turn chat",
            RunAsync: DriveScrollRoundTripAsync,
            Prepare: () => OpenChatAsync(scenarios.MegaChatId),
            Note: "Exercises scrollbar-style long-distance jumps without realizing skipped content.");

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
        => OnUiAsync(async () =>
        {
            if (!await _mainVm.OpenChatByIdAsync(chatId))
                throw new InvalidOperationException($"Chat {chatId} did not open.");
        });

    private Task NewChatAsync()
        => OnUiAsync(() => _mainVm.NewChatCommand.Execute(null));

    private MainWindow GetWindow()
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
            .OfType<MainWindow>().SingleOrDefault(window => ReferenceEquals(window.DataContext, _mainVm))
            ?? throw new InvalidOperationException("The harness window was not found.");

    private async Task ClickButtonAsync(Func<MainWindow, Button?> find)
    {
        await OnUiAsync(() =>
        {
            var button = find(GetWindow())
                ?? throw new InvalidOperationException("The requested interaction button was not found.");
            if (!button.IsEffectivelyVisible || !button.IsEffectivelyEnabled)
                throw new InvalidOperationException($"Button '{button.Name}' is not available for interaction.");
            new ButtonAutomationPeer(button).Invoke();
        });
        await DrainAsync(DispatcherPriority.Background);
    }

    private async Task ClickNavigationAsync(int index, string controlName)
    {
        await ClickButtonAsync(window => window.FindControl<Button>(controlName));
        await WaitForUiAsync(() => _mainVm.SelectedNavIndex == index, $"navigation to {controlName}");
        if (index == MainViewModel.LibraryNavIndex)
            await OnUiAsync(() => _mainVm.LibraryVM.EnsureLoadedAsync());
    }

    private async Task PrepareProjectSelectionAsync()
    {
        await SetProjectSwitcherAsync(false);
        await OnUiAsync(() => _mainVm.SelectedProjectFilter = null);
        await NewChatAsync();
    }

    private async Task SetProjectSwitcherAsync(bool open)
    {
        var isOpen = await OnUiAsync(() =>
            GetWindow().FindControl<Button>("ProjectSwitchButton")!.Classes.Contains("open"));
        if (isOpen != open)
            await ClickButtonAsync(window => window.FindControl<Button>("ProjectSwitchButton"));
        await WaitForUiAsync(() =>
        {
            var host = GetWindow().FindControl<Border>("ProjectSwitchRevealHost")!;
            return open ? host.IsVisible && double.IsNaN(host.Height) : !host.IsVisible;
        }, open ? "the project drawer to open" : "the project drawer to close");
    }

    private async Task SelectProjectRowAsync(Guid projectId)
    {
        await ClickButtonAsync(window =>
        {
            var title = _mainVm.Projects.Single(project => project.Id == projectId).Name;
            return window.FindControl<StackPanel>("ProjectFilterResults")!.Children.OfType<Button>()
                .SingleOrDefault(button => button.GetVisualDescendants().OfType<TextBlock>()
                    .Any(text => text.Text == title));
        });
        await WaitForUiAsync(() => _mainVm.SelectedProjectFilter == projectId, "the project filter to change");
        await SetProjectSwitcherAsync(false);
    }

    private async Task SelectSidebarChatAsync(Guid chatId)
    {
        await OnUiAsync(() =>
        {
            var chat = _dataStore.Data.Chats.Single(candidate => candidate.Id == chatId);
            var list = GetWindow().FindControl<ItemsControl>("ChatGroupsHost")!.GetVisualDescendants()
                .OfType<ListBox>().SingleOrDefault(list => list.Items.Contains(chat))
                ?? throw new InvalidOperationException("The requested sidebar chat row was not found.");
            list.SelectedItem = chat;
        });
        await WaitForUiAsync(() => _mainVm.ActiveChatId == chatId, "the selected sidebar chat to open");
        await OnUiAsync(async () =>
        {
            if (_mainVm.OpenChatCommand.ExecutionTask is { } task)
                await task;
        });
    }

    private async Task PrepareUnreadAsync(UiWorkloadScenarios scenarios, bool outsideFilter, bool openDrawer)
    {
        await SetProjectSwitcherAsync(false);
        await SetUnreadDrawerAsync(false);
        await NewChatAsync();
        await OnUiAsync(() =>
            _mainVm.SelectedProjectFilter = outsideFilter ? scenarios.OtherProjectId : scenarios.ProjectId);
        await OpenChatAsync(outsideFilter ? scenarios.OtherProjectChatId : scenarios.ProjectChatId);
        await OnUiAsync(() => _mainVm.MarkChatUnreadCommand.Execute(
            _dataStore.Data.Chats.Single(chat => chat.Id == scenarios.UnreadChatId)));
        if (openDrawer)
            await SetUnreadDrawerAsync(true);
    }

    private async Task SetUnreadDrawerAsync(bool open)
    {
        if (await OnUiAsync(() => _mainVm.IsUnreadPanelOpen != open))
            await ClickButtonAsync(window => window.FindControl<Button>("UnreadInboxToggle"));
        await WaitForUiAsync(() =>
        {
            var host = GetWindow().FindControl<Border>("UnreadRevealHost")!;
            return open ? _mainVm.IsUnreadPanelOpen && host.IsVisible && double.IsNaN(host.Height) : !host.IsVisible;
        }, open ? "the unread drawer to open" : "the unread drawer to close");
    }

    private async Task SelectUnreadChatAsync(Guid chatId)
    {
        await ClickButtonAsync(window => window.FindControl<Border>("UnreadPanel")!.GetVisualDescendants()
            .OfType<Button>().SingleOrDefault(button =>
                button.CommandParameter is UnreadChatEntry entry && entry.Chat.Id == chatId));
        await OnUiAsync(async () =>
        {
            if (_mainVm.OpenUnreadChatCommand.ExecutionTask is { } task)
                await task;
        });
        await WaitForUiAsync(() =>
            _mainVm.ActiveChatId == chatId
            && _mainVm.SelectedProjectFilter == _mainVm.ChatVM.CurrentChat?.ProjectId
            && _mainVm.ChatVM.CurrentChat is { HasUnreadMessages: false },
            "the chosen unread chat to open and become read");
        await SetUnreadDrawerAsync(false);
    }

    private static async Task WaitForUiAsync(Func<bool> condition, string description)
    {
        var timeout = Stopwatch.StartNew();
        while (!await OnUiAsync(condition))
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException($"Timed out waiting for {description}.");
            await Task.Delay(10);
        }
        await DrainAsync(DispatcherPriority.Background);
    }

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
        var scrollViewer = await OnUiAsync(FindTranscriptScrollViewer);
        if (scrollViewer is null)
            throw new InvalidOperationException("The active chat transcript ScrollViewer was not found.");

        for (var step = 0; step < MaxScrollSteps; step++)
        {
            var moved = await OnUiAsync(() =>
            {
                var before = scrollViewer.Offset.Y;
                var stepPixels = Math.Max(1800d, scrollViewer.Viewport.Height * 6d);
                var target = Math.Max(0d, before - stepPixels);
                scrollViewer.Offset = scrollViewer.Offset.WithY(target);
                return before - target > 0.5d;
            });

            await DrainAsync(DispatcherPriority.Background);
            if (!moved)
                break;
        }

        // Keep this action's measurement open through the scroll-idle debounce and the resulting
        // viewport realization/layout. Otherwise the report would measure placeholder movement only.
        await SettleStoppedTranscriptViewportAsync();
    }

    private async Task DriveScrollWithStopsAsync()
    {
        var scrollViewer = await OnUiAsync(FindTranscriptScrollViewer);
        if (scrollViewer is null)
            throw new InvalidOperationException("The active chat transcript ScrollViewer was not found.");

        var maxOffset = await OnUiAsync(() =>
            Math.Max(0d, scrollViewer.Extent.Height - scrollViewer.Viewport.Height));
        const int stopCount = 16;
        for (var stop = 1; stop <= stopCount; stop++)
        {
            var target = maxOffset * (1d - (stop / (double)stopCount));
            await OnUiAsync(() => scrollViewer.Offset = scrollViewer.Offset.WithY(target));
            await SettleStoppedTranscriptViewportAsync();
        }
    }

    private async Task DriveScrollRoundTripAsync()
    {
        var scrollViewer = await OnUiAsync(FindTranscriptScrollViewer);
        if (scrollViewer is null)
            throw new InvalidOperationException("The active chat transcript ScrollViewer was not found.");

        for (var cycle = 0; cycle < 12; cycle++)
        {
            await OnUiAsync(() =>
            {
                var maxOffset = Math.Max(0d, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
                scrollViewer.Offset = scrollViewer.Offset.WithY(cycle % 2 == 0 ? 0d : maxOffset);
            });
            await DrainAsync(DispatcherPriority.Render);
        }

        await SettleStoppedTranscriptViewportAsync();
    }

    private static async Task SettleStoppedTranscriptViewportAsync()
    {
        await Task.Delay(TranscriptItemsControl.ScrollIdleRealizationDelay + TimeSpan.FromMilliseconds(30));
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await DrainAsync(DispatcherPriority.Background);
            if (!TranscriptRealizationScheduler.Instance.HasPendingWork)
                break;
        }

        await DrainAsync(DispatcherPriority.Render);
        await DrainAsync(DispatcherPriority.Loaded);
        await DrainAsync(DispatcherPriority.Background);
    }

    private static ScrollViewer? FindTranscriptScrollViewer()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        return desktop.Windows
            .Where(static window => window.IsVisible)
            .SelectMany(static window => window.GetVisualDescendants())
            .OfType<StrataChatShell>()
            .FirstOrDefault(static shell => shell.IsVisible)
            ?.TranscriptScrollViewer;
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
                samples.InteractionDurationsMs.Add(measurement.RunMs + measurement.PostActionMs);
                samples.LatenciesMs.AddRange(measurement.Latencies);
                samples.IterationMaxMs.Add(measurement.Latencies.Count > 0 ? measurement.Latencies.Max() : 0d);
                samples.UiAllocatedBytes.Add(measurement.UiAllocatedBytes);
                samples.Gen2CollectionsByIteration.Add(measurement.Gen2Collections);
                ok++;
            }
            catch (Exception ex)
            {
                samples.FailedIterations++;
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
        var gen2Before = GC.CollectionCount(2);
        var stopwatch = Stopwatch.StartNew();
        // Arm a probe on the UI thread before starting the action, so even a click shorter than
        // the periodic sampling interval produces a sample of its synchronous/deferred work.
        var execution = await OnUiAsync(() =>
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var probe = _probe.SampleAsync();
            return (Probe: probe, Run: action.RunAsync(), AllocatedBefore: allocatedBefore);
        });
        await execution.Run;
        var runMs = stopwatch.Elapsed.TotalMilliseconds;

        // A dispatcher drain alone can run before deferred layout's next frame is queued.
        // Flush actual layout without waiting for idle/throttled animation ticks to inflate the window.
        var postStopwatch = Stopwatch.StartNew();
        await DrainLayoutAsync();
        await DrainAsync(DispatcherPriority.Background);
        await execution.Probe;
        var allocatedAfter = await OnUiAsync(GC.GetAllocatedBytesForCurrentThread);
        var postActionMs = postStopwatch.Elapsed.TotalMilliseconds;
        var end = _probe.NowMs;

        var latencies = _probe.LatenciesInWindow(start, end);
        if (latencies.Count == 0)
            throw new InvalidOperationException("No dispatcher samples were captured for the action.");
        return new RawMeasurement(runMs, postActionMs, latencies,
            allocatedAfter - execution.AllocatedBefore, GC.CollectionCount(2) - gen2Before);
    }

    private async Task DrainLayoutAsync()
    {
        for (var pass = 0; pass < 2; pass++)
        {
            await OnUiAsync(async () =>
            {
                var probe = _probe.SampleAsync();
                GetWindow().UpdateLayout();
                await probe;
            });
            await DrainAsync(DispatcherPriority.Loaded);
        }
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
