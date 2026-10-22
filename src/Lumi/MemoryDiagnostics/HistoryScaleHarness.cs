#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Storage;
using Lumi.UiPerf;
using Lumi.ViewModels;
using StrataTheme.Controls;

namespace Lumi.MemoryDiagnostics;

/// <summary>
/// Measures Lumi on a large, real-shaped profile: it opens the longest chats, scrolls through their
/// whole history and back, switches between them, and records open time, UI-thread latency while
/// scrolling, and post-GC memory after each step. The same harness runs against any build so
/// before/after numbers are comparable. It never sends messages and only runs on a profile the
/// caller names with <c>LUMI_APPDATA_DIR</c>.
/// </summary>
internal sealed class HistoryScaleHarness
{
    private readonly MainViewModel _mainVm;
    private readonly DataStore _dataStore;
    private readonly HistoryHarnessOptions _options;
    private readonly Action<int> _requestShutdown;
    private readonly List<Dictionary<string, object?>> _samples = [];
    private readonly List<Dictionary<string, object?>> _chats = [];
    private int _settleTimeouts;
    private readonly List<Dictionary<string, object?>> _fidelity = [];
    private readonly List<Dictionary<string, object?>> _costs = [];
    private Dictionary<string, object?>? _migrationAudit;
    private double? _migrationSeconds;

    public HistoryScaleHarness(
        MainViewModel mainVm,
        DataStore dataStore,
        HistoryHarnessOptions options,
        Action<int> requestShutdown)
    {
        _mainVm = mainVm;
        _dataStore = dataStore;
        _options = options;
        _requestShutdown = requestShutdown;
    }

    public async Task RunAsync()
    {
        var exitCode = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LUMI_APPDATA_DIR")))
                throw new InvalidOperationException("The history harness only runs with a caller-provided LUMI_APPDATA_DIR.");

            Console.WriteLine($"[history] Starting history-scale harness ({_options.Label}).");
            await OnUiAsync(() =>
            {
                _dataStore.Data.Settings.EnableMemoryAutoSave = false;
                _dataStore.Data.Settings.SuggestFollowUps = false;
            });
            await SettleAsync(1500);
            await CaptureAsync("startup");

            if (_options.WaitForMigration)
                await WaitForMigrationAsync();

            if (_options.VerifyMigration)
            {
                var mismatches = await VerifyMigrationAsync();
                WriteReport();
                exitCode = mismatches == 0 ? 0 : 4;
                return;
            }

            var targets = await OnUiAsync(ResolveTargets);
            Console.WriteLine($"[history] Targets: {string.Join(", ", targets.Select(static t => $"{t.Title} ({t.MessageCount} msgs)"))}");

            if (_options.CheckFidelity)
            {
                var different = await CheckFidelityAsync(targets);
                WriteReport();
                exitCode = different == 0 ? 0 : 3;
                return;
            }

            if (_options.MeasureCosts)
            {
                foreach (var target in targets)
                    await MeasureCostsAsync(target.Id, target.Title, target.MessageCount);
                WriteReport();
                return;
            }

            foreach (var target in targets)
                await MeasureChatAsync(target.Id, target.Title, target.MessageCount);

            // Leave every measured chat: what remains is what Lumi keeps resident for chats the
            // user is no longer looking at.
            await OnUiAsync(() => _mainVm.NewChatCommand.Execute(null));
            await SettleAsync(1000);
            await CaptureAsync("after-leaving-chats");

            // Revisit the first chat: a warm reopen of a chat opened earlier in the session.
            if (targets.Count > 0)
            {
                var first = targets[0];
                var (reopenMs, reopenSettledMs) = await OpenAndWaitAsync(first.Id);
                await CaptureAsync($"reopen:{first.Title}");
                Console.WriteLine($"[history] Reopened '{first.Title}' in {reopenMs:0} ms, settled in {reopenSettledMs:0} ms");
                _chats[0]["reopenMs"] = reopenMs;
                _chats[0]["reopenSettledMs"] = reopenSettledMs;
            }

            WriteReport();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[history] Harness failed: " + ex);
            exitCode = 1;
            try { WriteReport(ex.ToString()); } catch { /* best effort */ }
        }
        finally
        {
            if (_options.KeepOpen)
                Console.WriteLine("[history] --history-keep-open set; leaving the window open.");
            else
                _requestShutdown(exitCode);
        }
    }

    // Builds that store chats in a database migrate a legacy profile in the background; wait for it
    // so the measurements that follow are steady-state. The migration's state is read by reflection
    // so this file also builds into older Lumi versions for before/after runs; they have nothing to
    // wait for.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Debug-only harness.")]
    private async Task WaitForMigrationAsync()
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var type = _dataStore.GetType();
        if (type.GetProperty("LegacyImportCompletion", flags)?.GetValue(_dataStore) is not Task<TimeSpan> completion)
        {
            Console.WriteLine("[history] Nothing to migrate.");
            return;
        }

        var pending = type.GetProperty("PendingLegacyImports", flags);
        var stopwatch = Stopwatch.StartNew();
        while (!completion.IsCompleted)
        {
            await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(10)));
            Console.WriteLine($"[history] Migrating... {pending?.GetValue(_dataStore)} chats pending after {stopwatch.Elapsed.TotalSeconds:0}s");
        }

        var duration = await completion;
        Console.WriteLine($"[history] Migration finished: background pass {duration.TotalSeconds:0.0}s, waited {stopwatch.Elapsed.TotalSeconds:0.0}s");
        _migrationSeconds = duration.TotalSeconds;
        await SettleAsync(2000);
        await CaptureAsync("migrated");
    }

    /// <summary>
    /// Compares every chat the legacy index listed (kept as <c>data.pre-sqlite.json</c>) with what the
    /// migration stored: its metadata (all but the message count, which the legacy index let go
    /// stale) and each message, serialized as the legacy files store it, against its legacy file. A
    /// damaged file is compared up to the damage, which is what is kept. Returns how many chats differ.
    /// </summary>
    private async Task<int> VerifyMigrationAsync()
    {
        var appDirectory = DataStore.AppDirectory;
        var legacyIndex = Path.Combine(appDirectory, "data.pre-sqlite.json");
        if (!File.Exists(legacyIndex))
            throw new InvalidOperationException("There is no data.pre-sqlite.json: this profile was not migrated from the legacy layout.");

        AppData legacy;
        await using (var stream = File.OpenRead(legacyIndex))
            legacy = await JsonSerializer.DeserializeAsync(stream, AppDataJsonContext.Default.AppData) ?? new AppData();

        var stored = await OnUiAsync(() => _dataStore.Data.Chats.ToDictionary(static chat => chat.Id));
        var folders = new[] { DataStore.ChatsDir, Path.Combine(appDirectory, Lumi.Services.Storage.LegacyChatImporter.BackupFolderName) };
        var (matching, damaged, dropped, messages) = (0, 0, 0, 0L);
        var differences = new List<Dictionary<string, object?>>();
        var stopwatch = Stopwatch.StartNew();
        foreach (var legacyChat in legacy.Chats)
        {
            var file = folders.Select(folder => Path.Combine(folder, $"{legacyChat.Id}.json")).FirstOrDefault(File.Exists);
            var (expected, damage) = file is null ? (new List<string>(), (string?)null) : await HashLegacyTranscriptAsync(file);
            damaged += damage is null ? 0 : 1;
            if (!stored.TryGetValue(legacyChat.Id, out var chat))
            {
                // Startup leaves out index entries without a transcript, as the legacy layout did.
                if (expected.Count == 0)
                    dropped++;
                else
                    differences.Add(new() { ["id"] = legacyChat.Id.ToString(), ["title"] = legacyChat.Title, ["missing"] = true, ["expected"] = expected.Count });
                continue;
            }

            var actual = (await _dataStore.ReadPersistedChatMessagesAsync(chat.Id, CancellationToken.None)).Select(HashLegacy).ToList();
            messages += actual.Count;
            var metadataMatches = string.Equals(MetadataJson(legacyChat), MetadataJson(chat), StringComparison.Ordinal);
            var firstDifference = Enumerable.Range(0, Math.Max(expected.Count, actual.Count))
                .FirstOrDefault(i => i >= expected.Count || i >= actual.Count || expected[i] != actual[i], -1);
            if (metadataMatches && firstDifference < 0)
            {
                matching++;
                continue;
            }

            differences.Add(new()
            {
                ["id"] = chat.Id.ToString(),
                ["title"] = chat.Title,
                ["metadataMatches"] = metadataMatches,
                ["expected"] = expected.Count,
                ["actual"] = actual.Count,
                ["firstDifference"] = firstDifference,
                ["damage"] = damage,
            });
        }

        _migrationAudit = new()
        {
            ["legacyChats"] = legacy.Chats.Count,
            ["matching"] = matching,
            ["withoutTranscript"] = dropped,
            ["damagedLegacyFiles"] = damaged,
            ["storedMessages"] = messages,
            ["differences"] = differences,
            ["seconds"] = stopwatch.Elapsed.TotalSeconds,
        };
        Console.WriteLine($"[history] Migration audit: {matching} of {legacy.Chats.Count} chats match ({messages} messages), " +
                          $"{dropped} had no transcript, {damaged} legacy files are damaged, {differences.Count} differ.");
        return differences.Count;
    }

    // Each message as the legacy files store it, hashed so a long chat's whole transcript fits.
    private static string HashLegacy(ChatMessage message)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, AppDataJsonContext.Default.ChatMessage))));

    private static async Task<(List<string> Messages, string? Damage)> HashLegacyTranscriptAsync(string file)
    {
        var messages = new List<string>();
        try
        {
            await using var stream = File.OpenRead(file);
            await foreach (var message in JsonSerializer.DeserializeAsyncEnumerable(stream, AppDataJsonContext.Default.ChatMessage))
            {
                if (message is not null)
                    messages.Add(HashLegacy(message));
            }

            return (messages, null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return (messages, ex.Message);
        }
    }

    private static string MetadataJson(Chat chat)
    {
        var node = JsonSerializer.SerializeToNode(chat, AppDataJsonContext.Default.Chat)!.AsObject();
        node.Remove("messageCount");
        node.Remove("messages");
        return node.ToJsonString();
    }

    private List<(Guid Id, string Title, int MessageCount)> ResolveTargets()
    {
        var chats = _dataStore.Data.Chats;
        if (_options.ChatIds.Count > 0)
        {
            return _options.ChatIds
                .Select(id => chats.FirstOrDefault(chat => chat.Id == id))
                .Where(static chat => chat is not null)
                .Select(static chat => (chat!.Id, chat.Title, chat.MessageCount))
                .ToList();
        }

        return chats
            .OrderByDescending(static chat => chat.MessageCount)
            .Take(_options.TopChats)
            .Select(static chat => (chat.Id, chat.Title, chat.MessageCount))
            .ToList();
    }

    private async Task MeasureChatAsync(Guid chatId, string title, int messageCount)
    {
        Console.WriteLine($"[history] Opening '{title}' ({messageCount} messages)...");
        var settleTimeouts = _settleTimeouts;
        var (openMs, openSettledMs) = await OpenAndWaitAsync(chatId);
        Console.WriteLine($"[history]   open: {openMs:0} ms, settled: {openSettledMs:0} ms");
        var afterOpen = await CaptureAsync($"open:{title}");

        if (_options.Touchpad)
        {
            await MeasureTouchpadAsync(chatId, title, messageCount, openMs, openSettledMs, afterOpen, settleTimeouts);
            return;
        }

        var (scrollStats, upExtent, steps, topReached) = await ScrollThroughHistoryAsync(toTop: true);
        Console.WriteLine(
            $"[history]   scroll to top: steps={steps} reached={topReached} p50={scrollStats.P50:0.0} " +
            $"p95={scrollStats.P95:0.0} max={scrollStats.Max:0.0} ms, stalls={scrollStats.Over50Ms} ({scrollStats.StallMs:0} ms) " +
            $"over {scrollStats.DurationMs:0} ms, extent {upExtent.StartPx:0} -> {upExtent.EndPx:0} px");
        var afterScroll = await CaptureAsync($"scrolled:{title}");

        var (backStats, downExtent, backSteps, bottomReached) = await ScrollThroughHistoryAsync(toTop: false);
        Console.WriteLine(
            $"[history]   scroll back: steps={backSteps} reached={bottomReached} p95={backStats.P95:0.0} max={backStats.Max:0.0} ms, " +
            $"stalls={backStats.Over50Ms} ({backStats.StallMs:0} ms), extent {downExtent.StartPx:0} -> {downExtent.EndPx:0} px");
        if (DescribeHistoryDiagnostics() is { } diagnostics)
            Console.WriteLine($"[history]   history: {diagnostics}");
        var afterReturn = await CaptureAsync($"returned:{title}");

        _chats.Add(new Dictionary<string, object?>
        {
            ["id"] = chatId,
            ["title"] = title,
            ["messageCount"] = messageCount,
            ["openMs"] = openMs,
            ["openSettledMs"] = openSettledMs,
            ["afterOpen"] = afterOpen,
            ["scrollUp"] = scrollStats.ToDictionary(steps, topReached, upExtent),
            ["afterScrollUp"] = afterScroll,
            ["scrollDown"] = backStats.ToDictionary(backSteps, bottomReached, downExtent),
            ["afterScrollDown"] = afterReturn,
            ["settleTimeouts"] = _settleTimeouts - settleTimeouts,
        });
    }

    // Scrolls up and back down with touchpad gestures (TouchpadScroll), instead of page steps.
    private async Task MeasureTouchpadAsync(
        Guid chatId, string title, int messageCount, double openMs, double openSettledMs,
        Dictionary<string, object?> afterOpen, int settleTimeouts)
    {
        var scrollViewer = await OnUiAsync(FindTranscriptScrollViewer)
            ?? throw new InvalidOperationException("The transcript ScrollViewer was not found.");
        var up = await TouchpadScroll.RunAsync(scrollViewer, toTop: true, _options.Gestures);
        Console.WriteLine($"[history]   touchpad up: {up.Describe()}");
        var afterScroll = await CaptureAsync($"scrolled:{title}");
        var down = await TouchpadScroll.RunAsync(scrollViewer, toTop: false, _options.Gestures);
        Console.WriteLine($"[history]   touchpad down: {down.Describe()}");
        var afterReturn = await CaptureAsync($"returned:{title}");

        _chats.Add(new Dictionary<string, object?>
        {
            ["id"] = chatId,
            ["title"] = title,
            ["messageCount"] = messageCount,
            ["openMs"] = openMs,
            ["openSettledMs"] = openSettledMs,
            ["afterOpen"] = afterOpen,
            ["touchUp"] = up.ToDictionary(),
            ["afterScrollUp"] = afterScroll,
            ["touchDown"] = down.ToDictionary(),
            ["afterScrollDown"] = afterReturn,
            ["settleTimeouts"] = _settleTimeouts - settleTimeouts,
        });
    }

    // What one chat's history costs to hold: the Workspace's rows of its older exchanges (dropped, then
    // read and shown again, three times) and its largest older exchange built — the post-GC memory each
    // holds, the bytes allocated to get there and the time taken.
    private async Task MeasureCostsAsync(Guid chatId, string title, int messageCount)
    {
        Console.WriteLine($"[history] Measuring '{title}' ({messageCount} messages)...");
        var (openMs, openSettledMs) = await OpenAndWaitAsync(chatId);
        var afterOpen = await CaptureAsync($"open:{title}");
        var vm = _mainVm.ChatVM;

        var workspace = new List<Dictionary<string, object?>>();
        for (var pass = 0; pass < 3; pass++)
        {
            await OnUiAsync(() => vm.ReloadWorkspaceHistoryAsync(load: false));
            var without = await MeasureManagedAsync();
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var (readMs, showMs) = await OnUiAsync(() => vm.ReloadWorkspaceHistoryAsync(load: true));
            allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            var with = await MeasureManagedAsync();
            workspace.Add(new Dictionary<string, object?>
            {
                ["heldMiB"] = Round(ToMiB(with - without)),
                ["allocatedMiB"] = Round(ToMiB(allocated)),
                ["readMs"] = Round(readMs),
                ["showMs"] = Round(showMs),
            });
            Console.WriteLine(
                $"[history]   workspace: held {ToMiB(with - without):0.0} MiB, allocated {ToMiB(allocated):0.0} MiB, " +
                $"read {readMs:0} ms, shown in {showMs:0} ms");
        }

        Dictionary<string, object?>? exchange = null;
        var largest = await OnUiAsync(() => vm.HasTranscriptHistory
            ? vm.TranscriptHistory.Segments.MaxBy(static segment => segment.EndSeq - Math.Max(1, segment.StartSeq))
            : null);
        if (largest is { IsHydrated: false })
        {
            var before = await MeasureManagedAsync();
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();
            await OnUiAsync(() => vm.TranscriptHistory.EnsureHydratedAsync(largest));
            var buildMs = stopwatch.Elapsed.TotalMilliseconds;
            allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            var after = await MeasureManagedAsync();
            var held = await OnUiAsync(() => largest.HydratedMessageCount);
            exchange = new Dictionary<string, object?>
            {
                ["startSeq"] = largest.StartSeq,
                ["storedRows"] = largest.EndSeq - Math.Max(1, largest.StartSeq),
                ["heldMessages"] = held,
                ["heldMiB"] = Round(ToMiB(after - before)),
                ["allocatedMiB"] = Round(ToMiB(allocated)),
                ["buildMs"] = Round(buildMs),
            };
            Console.WriteLine(
                $"[history]   largest exchange (from {largest.StartSeq}, {exchange["storedRows"]} rows): holds {held} messages, " +
                $"{ToMiB(after - before):0.0} MiB; allocated {ToMiB(allocated):0.0} MiB, built in {buildMs:0} ms");
        }

        _costs.Add(new Dictionary<string, object?>
        {
            ["id"] = chatId,
            ["title"] = title,
            ["messageCount"] = messageCount,
            ["openMs"] = openMs,
            ["openSettledMs"] = openSettledMs,
            ["afterOpen"] = afterOpen,
            ["workspace"] = workspace,
            ["largestExchange"] = exchange,
        });
    }

    // Managed memory after full, compacting collections.
    private static async Task<long> MeasureManagedAsync()
    {
        await CollectAsync();
        return GC.GetTotalMemory(false);
    }

    private static async Task CollectAsync()
    {
        await SettleAsync(400);
        for (var pass = 0; pass < 3; pass++)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            await Task.Delay(30);
            await DrainAsync(DispatcherPriority.Background);
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    // Open time stops once the chat shows; settled time also covers what its first viewport builds.
    private async Task<(double OpenMs, double SettledMs)> OpenAndWaitAsync(Guid chatId)
    {
        var stopwatch = Stopwatch.StartNew();
        await OnUiAsync(async () => await _mainVm.OpenChatByIdAsync(chatId));
        var deadline = TimeSpan.FromSeconds(_options.OpenTimeoutSeconds);
        while (stopwatch.Elapsed < deadline)
        {
            var ready = await OnUiAsync(() =>
                _mainVm.ChatVM.CurrentChat?.Id == chatId && !_mainVm.ChatVM.IsChatSurfaceLoading);
            if (ready)
                break;
            await Task.Delay(8);
        }

        await DrainAsync(DispatcherPriority.Render);
        await DrainAsync(DispatcherPriority.Loaded);
        var elapsed = stopwatch.Elapsed.TotalMilliseconds;
        await SettleViewportAsync();
        return (elapsed, stopwatch.Elapsed.TotalMilliseconds);
    }

    private async Task<(LatencyStats Stats, ScrollExtent Extent, int Steps, bool Reached)> ScrollThroughHistoryAsync(bool toTop)
    {
        var scrollViewer = await OnUiAsync(FindTranscriptScrollViewer)
            ?? throw new InvalidOperationException("The transcript ScrollViewer was not found.");
        var startExtent = await OnUiAsync(() => scrollViewer.Extent.Height);
        using var probe = new UiResponsivenessProbe(intervalMs: 4);
        probe.Start();
        var startMs = probe.NowMs;
        var steps = 0;
        var reached = false;
        var stableAtEdge = 0;
        var maxSteps = _options.ScrollSteps * 6;

        while (steps < maxSteps)
        {
            var atEdge = await OnUiAsync(() =>
            {
                var viewport = scrollViewer.Viewport.Height;
                var maxOffset = Math.Max(0d, scrollViewer.Extent.Height - viewport);
                var current = scrollViewer.Offset.Y;
                var target = toTop
                    ? Math.Max(0d, current - viewport * 0.85)
                    : Math.Min(maxOffset, current + viewport * 0.85);
                scrollViewer.Offset = scrollViewer.Offset.WithY(target);
                return toTop ? target <= 0.5 : target >= maxOffset - 0.5;
            });
            steps++;
            await DrainAsync(DispatcherPriority.Render);
            if (steps % 3 == 0 || atEdge)
                await SettleViewportAsync();

            // Content may grow above the viewport as history materializes; only stop once the edge
            // holds after the viewport settles.
            if (atEdge)
            {
                var stillAtEdge = await OnUiAsync(() =>
                {
                    var maxOffset = Math.Max(0d, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
                    return toTop ? scrollViewer.Offset.Y <= 0.5 : scrollViewer.Offset.Y >= maxOffset - 0.5;
                });
                if (stillAtEdge && ++stableAtEdge >= 2)
                {
                    reached = true;
                    break;
                }
            }
            else
            {
                stableAtEdge = 0;
            }
        }

        var endMs = probe.NowMs;
        var stats = LatencyStats.From(probe.LatenciesInWindow(startMs, endMs), endMs - startMs);
        var extent = await OnUiAsync(() => new ScrollExtent(startExtent, scrollViewer.Extent.Height, scrollViewer.Viewport.Height));
        return (stats, extent, steps, reached);
    }

    /// <summary>The transcript's height when a scroll started and ended, and the viewport's.</summary>
    private readonly record struct ScrollExtent(double StartPx, double EndPx, double ViewportPx);

    private async Task<Dictionary<string, object?>> CaptureAsync(string label)
    {
        await CollectAsync();
        var info = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var sample = new Dictionary<string, object?>
        {
            ["label"] = label,
            ["managedMiB"] = Round(ToMiB(GC.GetTotalMemory(false))),
            ["heapMiB"] = Round(ToMiB(info.HeapSizeBytes)),
            ["committedMiB"] = Round(ToMiB(info.TotalCommittedBytes)),
            ["privateMiB"] = Round(ToMiB(process.PrivateMemorySize64)),
            ["workingSetMiB"] = Round(ToMiB(process.WorkingSet64)),
            ["gen2"] = GC.CollectionCount(2),
        };
        _samples.Add(sample);
        Console.WriteLine(
            $"[history] {label,-48} managed={sample["managedMiB"],8} MiB  private={sample["privateMiB"],8} MiB  ws={sample["workingSetMiB"],8} MiB");
        return sample;
    }

    // ── Fidelity ────────────────────────────────────────────────────────────
    //
    // Builds each target chat whole — in one pass, as Lumi built every chat before history was stored
    // — and as it is shown now: each older exchange built on its own (with its collapsed rows opened)
    // and the live tail. Both use the surface's own builder, so any difference comes from building an
    // exchange on its own. The Agents index and the Workspace's rows of older exchanges, which are
    // read from storage, are compared with what the whole messages give.

    private async Task<int> CheckFidelityAsync(IReadOnlyList<(Guid Id, string Title, int MessageCount)> targets)
    {
        var different = 0;
        var stopwatch = Stopwatch.StartNew();
        foreach (var (id, title, messageCount) in targets)
        {
            var result = await CompareChatAsync(id, title, messageCount);
            _fidelity.Add(result);
            if (result["same"] is false)
                different++;
        }

        Console.WriteLine(
            $"[history] Fidelity: {targets.Count - different}/{targets.Count} chats read the same " +
            $"built exchange by exchange as built whole ({stopwatch.Elapsed.TotalSeconds:0} s).");
        return different;
    }

    private async Task<Dictionary<string, object?>> CompareChatAsync(Guid chatId, string title, int messageCount)
    {
        var stopwatch = Stopwatch.StartNew();
        await OpenAndWaitAsync(chatId);
        var (whole, shown, exchanges) = await OnUiAsync(async () =>
        {
            var vm = _mainVm.ChatVM;
            var chat = vm.CurrentChat ?? throw new InvalidOperationException($"'{title}' did not open.");
            var shownLines = new List<string>();
            var history = vm.TranscriptHistory;
            var segments = vm.HasTranscriptHistory ? history.Segments.ToList() : new List<TranscriptHistorySegment>();
            foreach (var segment in segments)
            {
                // Building one exchange can release another under the history budget; build again
                // if this one was released while its collapsed rows were being opened.
                for (var attempt = 0; attempt < 3 && (segment.Turns is null || attempt == 0); attempt++)
                {
                    await history.EnsureHydratedAsync(segment);
                    await history.BuildFoldedStepsAsync(segment);
                }

                if (segment.Turns is { } turns)
                    DescribeTurns(turns, shownLines);
                else
                    shownLines.Add($"(exchange at {segment.StartSeq} was not built)");
            }

            DescribeTurns(vm.TranscriptTurns.Where(static turn => turn.HistorySegment is null), shownLines);

            var messages = (await _dataStore.ReadChatMessagesAsync(chat)).ToList();
            InteractionHistory.SettleInterrupted(messages);
            var builder = vm.CreateTranscriptBuilder(subagentRunsChanged: null);
            var built = builder.Rebuild(
                messages
                    .Where(static message => message.Role != "assistant" || !string.IsNullOrWhiteSpace(message.Content))
                    .Select(static message => new ChatMessageViewModel(message)),
                ChatViewModel.GetForkOrigin(chat));
            // The surface shows a chat's saved plan after its transcript, however it was built.
            if (!string.IsNullOrWhiteSpace(chat.PlanContent))
                builder.AppendPlanCardToLastTurn("Plan", static () => { });
            var wholeLines = new List<string>();
            DescribeTurns(built, wholeLines);

            // The Agents index: each run as its row reads (older runs are read from storage after
            // the chat opens) against its card in the whole build.
            DescribeRuns(builder.SubagentRuns, wholeLines);
            var expectedRuns = builder.SubagentRuns.Count;
            var waited = Stopwatch.StartNew();
            while (vm.AllSubagentRuns().Count < expectedRuns && waited.Elapsed < TimeSpan.FromSeconds(15))
                await Task.Delay(25);
            DescribeRuns(vm.AllSubagentRuns(), shownLines);

            // The Workspace's rows of older exchanges as it reads them (answers that could hold a link,
            // runs as summaries, file edits counted by SQLite) against the stored older messages read
            // whole, their edits counted as the transcript counts them.
            if (vm.HasTranscriptHistory)
            {
                var tailStart = _dataStore.GetTranscriptWindow(chat).TailStartSeq;
                var overview = await _dataStore.ReadHistoryOverviewAsync(chat, ChatViewModel.WorkspaceHistoryKinds, tailStart);
                DescribeWorkspace(ChatViewModel.DeriveWorkspaceHistory(overview.Messages, overview.FileEdits), shownLines);
                var stored = await _dataStore.ReadPersistedChatMessagesAsync(chat.Id, CancellationToken.None);
                DescribeWorkspace(WholeWorkspaceHistory(stored.Where(message => message.StorageSeq < tailStart)), wholeLines);
            }

            builder.ResetState();
            return (wholeLines, shownLines, segments.Count);
        });

        var difference = -1;
        for (var i = 0; i < Math.Max(whole.Count, shown.Count); i++)
        {
            if (i >= whole.Count || i >= shown.Count || !string.Equals(whole[i], shown[i], StringComparison.Ordinal))
            {
                difference = i;
                break;
            }
        }

        var same = difference < 0;
        Console.WriteLine(
            $"[history] {(same ? "same     " : "DIFFERENT")} '{title}' ({messageCount} msgs, {exchanges} older exchanges, " +
            $"{whole.Count} lines, {stopwatch.Elapsed.TotalSeconds:0.0} s)");
        string? Line(List<string> lines) => difference >= 0 && difference < lines.Count ? Truncate(lines[difference]) : null;
        if (!same)
            Console.WriteLine($"[history]   line {difference}: whole \"{Line(whole)}\" / shown \"{Line(shown)}\"");

        return new Dictionary<string, object?>
        {
            ["id"] = chatId,
            ["title"] = title,
            ["messageCount"] = messageCount,
            ["olderExchanges"] = exchanges,
            ["lines"] = whole.Count,
            ["shownLines"] = shown.Count,
            ["same"] = same,
            ["firstDifference"] = same ? null : difference,
            ["whole"] = Line(whole),
            ["shown"] = Line(shown),
            ["seconds"] = Round(stopwatch.Elapsed.TotalSeconds),
        };

        static string Truncate(string text) => text.Length <= 400 ? text : text[..400] + "…";
    }

    // The Workspace's older rows from whole stored messages: the conversation and the kinds of steps
    // the panel lists, and each file edit counted from its text as the transcript counts it.
    private static WorkspaceHistoryRows WholeWorkspaceHistory(IEnumerable<ChatMessage> stored)
    {
        var listed = new List<ChatMessage>();
        var edits = new List<StoredFileEdit>();
        foreach (var message in stored)
        {
            var kind = ChatMessageKinds.Classify(message);
            if (message.Role is "user" or "assistant" or "system" or "error")
            {
                listed.Add(message);
            }
            else if (kind == ChatMessageKinds.Edit)
            {
                if (message.ToolStatus == "Failed")
                    continue;
                foreach (var diff in ToolDisplayHelper.ExtractAllDiffs(message.ToolName!, message.Content))
                {
                    edits.Add(new StoredFileEdit(
                        message.StorageSeq,
                        diff.FilePath,
                        FileChangeItem.CountLines(diff.NewText),
                        FileChangeItem.CountLines(diff.OldText),
                        TranscriptBuilder.IsCreateDiff(message.ToolName!, diff.OldText)));
                }
            }
            else if (kind is not null && ChatViewModel.WorkspaceHistoryKinds.Contains(kind))
            {
                listed.Add(message);
            }
        }

        return ChatViewModel.DeriveWorkspaceHistory(listed, edits);
    }

    private static void DescribeWorkspace(WorkspaceHistoryRows rows, List<string> lines)
    {
        lines.Add("workspace");
        foreach (var path in rows.Deliverables)
            lines.Add($"  file {path}");
        foreach (var change in rows.FileChanges)
        {
            lines.Add(
                $"  edit {change.FilePath} create={change.IsCreate} +{change.LinesAdded} -{change.LinesRemoved} " +
                $"exchange={change.Exchange} at [{string.Join(", ", change.Seqs)}]");
        }

        foreach (var (title, url) in rows.Links)
            lines.Add($"  link \"{title}\" {url}");
        foreach (var source in rows.Sources)
            lines.Add($"  source \"{source.Title}\" {source.Url} \"{source.Snippet}\"");
        foreach (var activity in rows.Activities)
            lines.Add($"  activity {activity.Kind} \"{activity.Title}\" {activity.Target}");
        foreach (var message in rows.UserMessages)
            lines.Add($"  user {message.Id}");
        foreach (var skill in rows.Skills)
            lines.Add($"  skill \"{skill.Name}\"");
        foreach (var run in rows.AgentMessages)
            lines.Add($"  run {run.ToolCallId}");
    }

    private static void DescribeRuns(IEnumerable<SubagentToolCallItem> runs, List<string> lines)
    {
        // A run that recorded no model shows the model selected when its card was built, which
        // depends on timing even in a whole build (the chat's own model is restored after its
        // transcript is built), so the model is not compared.
        lines.Add("agents index");
        foreach (var run in runs.OrderBy(static run => run.ToolCallId, StringComparer.Ordinal))
        {
            lines.Add(
                $"  run {run.ToolCallId} {run.DisplayName} title=\"{run.Title}\" latest=\"{run.LatestActivityText}\" " +
                $"{run.Status} started={run.StartedAt:O} duration={run.DurationMs:0}");
        }
    }

    private static void DescribeTurns(IEnumerable<TranscriptTurn> turns, List<string> lines)
    {
        foreach (var turn in turns)
        {
            lines.Add($"turn {Stable(turn.StableId)}");
            foreach (var item in turn.Items)
                DescribeItem(item, "  ", lines);
        }
    }

    private static void DescribeItem(TranscriptItem item, string indent, List<string> lines)
    {
        switch (item)
        {
            case TurnSummaryItem summary:
                lines.Add($"{indent}summary {Stable(summary.StableId)} \"{summary.Label}\" failures={summary.HasFailures}");
                foreach (var inner in summary.InnerItems)
                    DescribeItem(inner, indent + "  ", lines);
                break;
            case SubagentGroupItem group:
                lines.Add($"{indent}agents {Stable(group.StableId)} \"{group.HeaderLabel}\" meta=\"{group.Meta}\"");
                foreach (var run in group.Subagents)
                    DescribeItem(run, indent + "  ", lines);
                break;
            case SubagentToolCallItem run:
                // What a run's card and page headline show. Its steps, their count and its progress line
                // are on its page, which reads them from storage when it opens.
                lines.Add(
                    $"{indent}run {Stable(run.StableId)} title=\"{run.Title}\" " +
                    $"latest=\"{run.LatestActivityText}\" {run.Status}");
                break;
            case ToolGroupItem group:
                lines.Add(
                    $"{indent}group {Stable(group.StableId)} \"{group.Label}\" meta=\"{group.Meta}\" " +
                    $"[{string.Join(", ", group.ToolCalls.Select(static tool => tool.GetType().Name))}]");
                break;
            case SingleToolItem single:
                lines.Add($"{indent}single {Stable(single.StableId)} \"{single.Label}\"");
                break;
            case TurnModelItem model:
                lines.Add($"{indent}model {Stable(model.StableId)} \"{model.ModelName}\"");
                break;
            case SkillLoadedItem skill:
                lines.Add($"{indent}skill {Stable(skill.StableId)} \"{skill.Chip.Name}\"");
                break;
            case LinkedChatItem linked:
                lines.Add($"{indent}linked {Stable(linked.StableId)} {linked.Chip.ChatId} \"{linked.Chip.Title}\"");
                break;
            case FileChangesSummaryItem files:
                lines.Add(
                    $"{indent}files {Stable(files.StableId)} \"{files.Label}\" " +
                    $"[{string.Join(", ", files.FileChanges.Select(static change => change.FilePath))}]");
                break;
            case PlanCardItem plan:
                lines.Add($"{indent}plan {Stable(plan.StableId)} \"{plan.StatusText}\"");
                break;
            default:
                lines.Add($"{indent}{item.GetType().Name} {Stable(item.StableId)}");
                break;
        }
    }

    // Some items get a fresh id each time they are built ("turn-model:{guid}"); what they show is
    // compared instead.
    private static string Stable(string stableId)
        => stableId.Length > 33 && stableId[^33] == ':' && stableId[^32..].All(Uri.IsHexDigit)
            ? stableId[..^32] + "*"
            : stableId;

    private void WriteReport(string? error = null)
    {
        var report = new Dictionary<string, object?>
        {
            ["label"] = _options.Label,
            ["timestamp"] = DateTimeOffset.Now.ToString("O"),
            ["appDataDir"] = Environment.GetEnvironmentVariable("LUMI_APPDATA_DIR"),
            ["chatCount"] = _dataStore.Data.Chats.Count,
            ["migrationSeconds"] = _migrationSeconds,
            ["samples"] = _samples,
            ["chats"] = _chats,
            ["fidelity"] = _fidelity,
            ["costs"] = _costs,
            ["migrationAudit"] = _migrationAudit,
            ["error"] = error,
        };
        var path = _options.OutputPath is { Length: > 0 } output
            ? Path.GetFullPath(output)
            : Path.Combine(Path.GetTempPath(), "Lumi-history-harness", $"history-{_options.Label}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = File.Create(path))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            WriteValue(writer, report);
        Console.WriteLine($"[history] Report written to {path}");
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case Guid id:
                writer.WriteStringValue(id);
                break;
            case IDictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var (key, item) in map)
                {
                    writer.WritePropertyName(key);
                    WriteValue(writer, item);
                }
                writer.WriteEndObject();
                break;
            case System.Collections.IEnumerable items:
                writer.WriteStartArray();
                foreach (var item in items)
                    WriteValue(writer, item);
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    // Builds that load history on demand report their counters; older builds have none.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Debug-only harness.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Debug-only harness.")]
    private static string? DescribeHistoryDiagnostics()
        => typeof(HistoryScaleHarness).Assembly
            .GetType("Lumi.ViewModels.TranscriptHistoryDiagnostics")
            ?.GetMethod("Describe", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?.Invoke(null, null) as string;

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

    // A settled step shows what is on screen built: its turns realized and, in builds that load older
    // exchanges on demand, those exchanges read and built, so every build shows the same at each stop.
    private async Task SettleViewportAsync()
    {
        await Task.Delay(TranscriptItemsControl.ScrollIdleRealizationDelay + TimeSpan.FromMilliseconds(30));
        var waited = Stopwatch.StartNew();
        var settled = false;
        while (!settled && waited.ElapsedMilliseconds < 5000)
        {
            await DrainAsync(DispatcherPriority.Background);
            settled = !await OnUiAsync(() => TranscriptRealizationScheduler.Instance.HasPendingWork || HasPendingHistoryWork());
            if (!settled)
                await Task.Delay(4);
        }

        if (!settled)
            _settleTimeouts++;
        await DrainAsync(DispatcherPriority.Render);
        await DrainAsync(DispatcherPriority.Loaded);
        await DrainAsync(DispatcherPriority.Background);
    }

    // Read by reflection, like the diagnostics above: older builds have no history to wait for.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Debug-only harness.")]
    private bool HasPendingHistoryWork()
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var chat = _mainVm.ChatVM;
        var history = chat.GetType().GetProperty("TranscriptHistory", flags)?.GetValue(chat);
        return history?.GetType().GetProperty("HasPendingWork", flags)?.GetValue(history) is true;
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
        => Dispatcher.UIThread.CheckAccess() ? func() : await Dispatcher.UIThread.InvokeAsync(func);

    private static async Task<T> OnUiAsync<T>(Func<Task<T>> func)
        => Dispatcher.UIThread.CheckAccess() ? await func() : await Dispatcher.UIThread.InvokeAsync(func);

    private static async Task OnUiAsync(Func<Task> func)
    {
        if (Dispatcher.UIThread.CheckAccess())
            await func();
        else
            await Dispatcher.UIThread.InvokeAsync(func);
    }

    private static Task OnUiAsync(Action action) => OnUiAsync(() => { action(); return true; });

    private static double ToMiB(long bytes) => bytes / (1024d * 1024d);
    private static double Round(double value) => Math.Round(value, 1);

    private readonly record struct LatencyStats(double P50, double P95, double P99, double Max, int Samples, double DurationMs, int Over50Ms, double StallMs)
    {
        public static LatencyStats From(IReadOnlyList<double> latencies, double durationMs)
        {
            if (latencies.Count == 0)
                return new LatencyStats(0, 0, 0, 0, 0, durationMs, 0, 0);

            var sorted = latencies.OrderBy(static value => value).ToArray();
            double Percentile(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
            // The probe posts one sample at a time at Background priority: the waits over 50 ms, and what
            // they add up to, measure how starved the UI thread was (input outranks the probe), not how
            // many freezes a user saw.
            var stalls = sorted.Where(static value => value > 50).ToArray();
            return new LatencyStats(
                Percentile(0.50), Percentile(0.95), Percentile(0.99), sorted[^1], sorted.Length, durationMs,
                stalls.Length, stalls.Sum());
        }

        public Dictionary<string, object?> ToDictionary(int steps, bool reached, ScrollExtent extent) => new()
        {
            ["steps"] = steps,
            ["reachedEdge"] = reached,
            ["durationMs"] = Math.Round(DurationMs),
            ["p50Ms"] = Math.Round(P50, 1),
            ["p95Ms"] = Math.Round(P95, 1),
            ["p99Ms"] = Math.Round(P99, 1),
            ["maxMs"] = Math.Round(Max, 1),
            ["samples"] = Samples,
            ["samplesOver50Ms"] = Over50Ms,
            ["stallMs"] = Math.Round(StallMs),
            ["extentStartPx"] = Math.Round(extent.StartPx),
            ["extentEndPx"] = Math.Round(extent.EndPx),
            ["viewportPx"] = Math.Round(extent.ViewportPx),
        };
    }
}
#endif
