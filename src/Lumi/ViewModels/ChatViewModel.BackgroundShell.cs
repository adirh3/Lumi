using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

/// <summary>
/// Honest UI for background shells. When the agent launches an <c>async</c> shell and ends its turn,
/// the SDK reports the <em>tool call</em> as completed within a fraction of a second while the OS
/// process keeps running — and the session stays non-idle until it finishes. Without special handling
/// the terminal card reads "Completed" and the composer shows a generic "Generating…" spinner, so a
/// long-lived process looks either finished or stuck.
///
/// This monitor keeps the picture truthful: it polls the authoritative Tasks API for the displayed
/// chat's session, marks the matching terminal card as "Running in background" (live pulse + elapsed
/// clock), streams the live output tail onto the card, and replaces the bottom status line with a
/// specific "Running in background · elapsed" readout. The same task snapshot supplies the activity
/// list — commands, agents and client-owned tasks, each with what it is doing right now — and links
/// each agent to its run so the list can open it. Cards resolve when their shell finishes.
/// </summary>
public partial class ChatViewModel
{
    internal Task RefreshBackgroundActivityAsync()
    {
        if (_isDisposed || !IsSessionActive)
            return Task.CompletedTask;

        EnsureBackgroundShellMonitorRunning();
        return PollBackgroundShellsAsync();
    }

    private void ResetBackgroundActivityItems()
    {
        if (RunningSessionActivities.Count > 0)
        {
            RunningSessionActivities.Clear();
            OnPropertyChanged(nameof(HasRunningSessionActivities));
        }

        BackgroundActivityNotice = Loc.Get("Chat_BackgroundActivityLoading");
    }

    internal void ApplyBackgroundActivitySnapshot(IEnumerable<TaskInfo> tasks, DateTimeOffset now)
    {
        SyncRunningSessionActivities(BuildBackgroundActivityItems(tasks, now));

        // Re-linked on every snapshot: a run can appear after its task does, and a transcript rebuild
        // replaces every run instance.
        foreach (var item in RunningSessionActivities)
        {
            if (item.Kind == SessionActivityKind.Agent)
                item.Run = _transcriptBuilder.FindSubagentRun(item.ToolCallId);
        }

        BackgroundActivityNotice = RunningSessionActivities.Count == 0 ? Loc.Get("Chat_BackgroundActivityEmpty") : null;
    }

    /// <summary>Patches the list to the snapshot's tasks and order, keeping each task's row instance.</summary>
    private void SyncRunningSessionActivities(IReadOnlyList<SessionActivityItem> snapshot)
    {
        var items = RunningSessionActivities;
        var hadItems = items.Count > 0;
        var wanted = new HashSet<string>(snapshot.Select(item => item.Id), StringComparer.Ordinal);
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(items[i].Id))
                items.RemoveAt(i);
        }

        for (var i = 0; i < snapshot.Count; i++)
        {
            var next = snapshot[i];
            var existing = -1;
            for (var j = i; j < items.Count; j++)
            {
                if (string.Equals(items[j].Id, next.Id, StringComparison.Ordinal))
                {
                    existing = j;
                    break;
                }
            }

            if (existing < 0)
            {
                items.Insert(i, next);
                continue;
            }

            if (existing != i)
                items.Move(existing, i);
            items[i].UpdateFrom(next);
        }

        if (hadItems != items.Count > 0)
            OnPropertyChanged(nameof(HasRunningSessionActivities));
    }

    private SessionActivityItem? FindSessionActivity(string id)
        => RunningSessionActivities.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));

    internal static IReadOnlyList<SessionActivityItem> BuildBackgroundActivityItems(
        IEnumerable<TaskInfo> tasks,
        DateTimeOffset now)
    {
        return tasks.Select(task => task switch
        {
            TaskInfoShell shell when IsRunningBackgroundShell(shell) => CreateCommandActivity(shell, now),
            TaskInfoAgent agent when agent.Status == GitHub.Copilot.Rpc.TaskStatus.Running => CreateAgentActivity(agent, now),
            TaskInfoClient client when client.Status == TaskClientStatus.Running => CreateClientActivity(client, now),
            _ => null
        }).OfType<SessionActivityItem>().ToArray();
    }

    private static SessionActivityItem CreateCommandActivity(TaskInfoShell shell, DateTimeOffset now)
    {
        var kind = Loc.Get("Chat_BackgroundCommand");
        var command = TrimmedOrNull(shell.Command);
        var title = TrimmedOrNull(shell.Description) ?? command ?? kind;
        return new SessionActivityItem(shell.Id, SessionActivityKind.Command)
        {
            Title = title,
            Owner = kind,
            Detail = command is not null && command != title ? command : "",
            StartedAt = ClampActivityStart(shell.StartedAt, now),
        };
    }

    private static SessionActivityItem CreateAgentActivity(TaskInfoAgent agent, DateTimeOffset now)
    {
        var owner = TrimmedOrNull(agent.DisplayName) ?? TrimmedOrNull(agent.AgentType) ?? Loc.Get("Chat_BackgroundAgent");
        return new SessionActivityItem(agent.Id, SessionActivityKind.Agent)
        {
            ToolCallId = TrimmedOrNull(agent.ToolCallId),
            Title = TrimmedOrNull(agent.Description) ?? owner,
            Owner = owner,
            Detail = FormatModelDisplay(TrimmedOrNull(agent.ResolvedModel) ?? TrimmedOrNull(agent.Model)) ?? "",
            StartedAt = ClampActivityStart(agent.ActiveStartedAt ?? agent.StartedAt, now),
        };
    }

    private static SessionActivityItem CreateClientActivity(TaskInfoClient client, DateTimeOffset now)
    {
        var kind = Loc.Get("Chat_BackgroundTask");
        var description = TrimmedOrNull(client.Description);
        var title = TrimmedOrNull(client.DisplayName) ?? description ?? kind;
        return new SessionActivityItem(client.Id, SessionActivityKind.Task)
        {
            Title = title,
            Owner = kind,
            Detail = description is not null && description != title ? description : "",
            StartedAt = ClampActivityStart(client.ActiveStartedAt ?? client.StartedAt, now),
        };
    }

    private static string? TrimmedOrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A start reported slightly ahead of this clock reads as just started, never negative.</summary>
    private static DateTimeOffset ClampActivityStart(DateTimeOffset startedAt, DateTimeOffset now)
        => startedAt > now ? now : startedAt;

    private static async Task StopRemainingSessionTasksAsync(
        CopilotSession session, ChatRuntimeState runtime, bool confirmStopped = false)
    {
        // session.abort stops the agent loop, but intentionally leaves attached shells alive.
        var tasks = await session.Rpc.Tasks.ListAsync();
        foreach (var id in tasks.Tasks.Select(GetRunningTaskId).OfType<string>())
        {
            MarkSessionBackgroundActive(runtime);
            var result = await session.Rpc.Tasks.CancelAsync(id);
            if (!result.Cancelled)
            {
                // A task can finish between listing it and cancellation.
                var remaining = await session.Rpc.Tasks.ListAsync();
                if (remaining.Tasks.Any(task => GetRunningTaskId(task) == id))
                    throw new InvalidOperationException(Loc.Get(nameof(Loc.Chat_BackgroundStopFailed), id));
            }
        }

        if (confirmStopped)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while ((await session.Rpc.Tasks.ListAsync(timeout.Token)).Tasks.Any(task => GetRunningTaskId(task) is not null))
                await Task.Delay(100, timeout.Token);
        }

        static string? GetRunningTaskId(TaskInfo task) => task switch
        {
            TaskInfoShell shell when shell.Status == GitHub.Copilot.Rpc.TaskStatus.Running => shell.Id,
            TaskInfoAgent agent when agent.Status == GitHub.Copilot.Rpc.TaskStatus.Running => agent.Id,
            TaskInfoClient client when client.Status == TaskClientStatus.Running => client.Id,
            _ => null
        };
    }

    private static readonly TimeSpan BackgroundShellPollInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>Shared empty map for <see cref="RebuildTranscript"/> when there is no current chat.</summary>
    private static readonly IReadOnlyDictionary<string, DateTimeOffset> EmptyRunningBackgroundShells =
        new Dictionary<string, DateTimeOffset>(0);

    private DispatcherTimer? _backgroundShellMonitor;
    private bool _backgroundShellPollInFlight;

    internal IReadOnlySet<string> GetRunningBackgroundShellIds(Guid chatId) =>
        _runtimeStates.TryGetValue(chatId, out var runtime)
            ? new HashSet<string>(runtime.RunningBackgroundShells.Keys, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Terminal cards (keyed by root tool-call id) whose async shell may still be running.</summary>
    private readonly Dictionary<string, TrackedBackgroundShell> _trackedBackgroundShells = new();

    private sealed class TrackedBackgroundShell
    {
        public required string RootToolCallId { get; init; }
        public required string Command { get; init; }
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Authoritative Tasks-API shell identity, pinned on first correlation so identical-command
        /// siblings can never swap cards on a later poll. Null until the monitor first observes the shell.</summary>
        public string? ShellId { get; set; }
    }

    /// <summary>Detects, from a powershell tool call's raw arguments, whether it launches a shell that
    /// keeps running after the call returns (<c>mode: async|background</c>, or <c>detach/background:true</c>).
    /// Best-effort and flicker-free; the Tasks-API monitor is the authority and backfills any misses.</summary>
    private static bool LooksLikeBackgroundShellArgs(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var root = doc.RootElement;

            if (root.TryGetProperty("mode", out var mode)
                && mode.ValueKind == JsonValueKind.String)
            {
                var value = mode.GetString();
                if (string.Equals(value, "async", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "background", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (root.TryGetProperty("detach", out var detach) && detach.ValueKind == JsonValueKind.True)
                return true;

            if (root.TryGetProperty("background", out var background) && background.ValueKind == JsonValueKind.True)
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Immediately marks a terminal card as running-in-background (flicker-free, synchronous)
    /// and starts the live monitor. Called from the tool-complete handler for a detected async shell.</summary>
    private void TrackBackgroundShell(string rootToolCallId, string command)
    {
        if (string.IsNullOrEmpty(rootToolCallId))
            return;

        if (!_trackedBackgroundShells.TryGetValue(rootToolCallId, out var tracked))
        {
            tracked = new TrackedBackgroundShell
            {
                RootToolCallId = rootToolCallId,
                Command = command ?? string.Empty,
            };
            _trackedBackgroundShells[rootToolCallId] = tracked;
        }

        var startedUtc = new DateTimeOffset(tracked.StartedUtc);
        RememberRunningBackgroundShell(rootToolCallId, startedUtc);
        _transcriptBuilder.SetTerminalRunningInBackground(rootToolCallId, true, startedUtc: startedUtc);
        EnsureBackgroundShellMonitorRunning();
    }

    /// <summary>Records a running background shell on the owning chat's persisted runtime state so a
    /// transcript rebuild (chat switch, virtualization) can recreate the terminal card already-running
    /// with a stable elapsed clock, instead of it flashing "finished" or folding into a summary.</summary>
    private void RememberRunningBackgroundShell(string rootToolCallId, DateTimeOffset startedUtc)
    {
        if (CurrentChat is { } chat)
            GetOrCreateRuntimeState(chat.Id).RunningBackgroundShells[rootToolCallId] = startedUtc;
    }

    private void ForgetRunningBackgroundShell(string rootToolCallId)
    {
        if (CurrentChat is { } chat)
            GetOrCreateRuntimeState(chat.Id).RunningBackgroundShells.Remove(rootToolCallId);
    }

    private void EnsureBackgroundShellMonitorRunning()
    {
        _backgroundShellMonitor ??= CreateBackgroundShellMonitor();
        if (!_backgroundShellMonitor.IsEnabled)
            _backgroundShellMonitor.Start();
    }

    private DispatcherTimer CreateBackgroundShellMonitor()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = BackgroundShellPollInterval,
        };
        timer.Tick += (_, _) => _ = PollBackgroundShellsAsync();
        return timer;
    }

    private void StopBackgroundShellMonitor() => _backgroundShellMonitor?.Stop();

    /// <summary>Called on session-idle for the displayed chat: all background work has drained, so any
    /// remaining background cards are finished. Resolves them and stops polling.</summary>
    private void CompleteAllBackgroundShellsAndStop()
    {
        foreach (var tracked in _trackedBackgroundShells.Values.ToList())
            CompleteBackgroundShell(tracked);

        _trackedBackgroundShells.Clear();
        if (CurrentChat is { } chat)
            GetOrCreateRuntimeState(chat.Id).RunningBackgroundShells.Clear();
        StopBackgroundShellMonitor();
        ResetBackgroundActivityItems();
    }

    private void CompleteBackgroundShell(TrackedBackgroundShell tracked)
    {
        _trackedBackgroundShells.Remove(tracked.RootToolCallId);
        ForgetRunningBackgroundShell(tracked.RootToolCallId);
        var durationMs = Math.Max(0, (DateTime.UtcNow - tracked.StartedUtc).TotalMilliseconds);

        // The shell outlived its tool call, so the Tasks-API lifetime — not the launch call — is the
        // command's real duration. Persist it so a rebuild shows the same number instead of falling
        // back to the misleadingly short launch time.
        var toolMsg = CurrentChat?.Messages.LastOrDefault(m => m.ToolCallId == tracked.RootToolCallId);
        if (toolMsg is not null)
        {
            toolMsg.ToolStartedAt = new DateTimeOffset(tracked.StartedUtc);
            toolMsg.ToolDurationMs = durationMs;
            Messages.LastOrDefault(message =>
                    message.Message.ToolCallId == tracked.RootToolCallId)
                ?.NotifyToolDetailsChanged();
        }

        _transcriptBuilder.SetTerminalRunningInBackground(tracked.RootToolCallId, false, durationMs);
    }

    private async Task PollBackgroundShellsAsync()
    {
        if (_isDisposed || _backgroundShellPollInFlight)
            return;

        _backgroundShellPollInFlight = true;
        try
        {
            var session = _activeSession;
            var chat = CurrentChat;
            if (session is null || chat is null || !IsSessionActive)
            {
                // No active session to poll (chat switch mid-flight, remote shutdown, or CLI reconnect
                // nulled it). Stop the timer unconditionally — it is always re-armed by
                // EnsureBackgroundShellMonitorRunning when new async work appears (TrackBackgroundShell,
                // a background-tasks-changed event, or switching back to a chat with pending work), so
                // stopping here just avoids a timer firing forever against a dead session.
                StopBackgroundShellMonitor();
                return;
            }

            TaskList tasks;
            try
            {
                tasks = await session.Rpc.Tasks.ListAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"[Chat] Could not refresh session activity: {ex.Message}");
                if (ReferenceEquals(_activeSession, session) && CurrentChat?.Id == chat.Id && IsSessionActive)
                    BackgroundActivityNotice = Loc.Get("Chat_BackgroundActivityUnavailable");
                return;
            }

            // The chat may have been switched (or the session torn down) while the RPC was in flight;
            // if so the shared transcript builder now reflects a different chat, so abandon this stale
            // poll rather than marking another chat's cards from this chat's shell list.
            if (!ReferenceEquals(_activeSession, session) || CurrentChat?.Id != chat.Id || !IsSessionActive)
                return;

            ApplyBackgroundActivitySnapshot(tasks.Tasks, DateTimeOffset.UtcNow);
            var runningShells = tasks.Tasks.OfType<TaskInfoShell>().Where(IsRunningBackgroundShell).ToList();

            // Map each still-running shell to a DISTINCT terminal card. First observation correlates by
            // command text (excluding cards already claimed this poll, so N identical-command shells map
            // to N cards); the matched shell id is then pinned so later polls re-bind by authoritative
            // shell identity and identical-command siblings can never swap cards when one finishes.
            var claimedToolCallIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var shell in runningShells)
            {
                // A chat switch during a previous iteration's await resets the shared transcript builder
                // and tracked-shell map to a different chat; abandon rather than binding this chat's
                // shells onto another chat's cards. The next tick re-polls the now-current chat.
                if (!ReferenceEquals(_activeSession, session) || CurrentChat?.Id != chat.Id)
                    return;

                var command = shell.Command?.Trim() ?? string.Empty;

                // Prefer the pinned shell id for a shell we've already mapped; fall back to command
                // correlation on first observation, then pin the id below.
                string? toolCallId = null;
                if (!string.IsNullOrEmpty(shell.Id))
                    toolCallId = _trackedBackgroundShells.Values
                        .FirstOrDefault(t => t.ShellId == shell.Id)?.RootToolCallId;
                toolCallId ??= _transcriptBuilder.FindTerminalToolCallIdByCommand(command, claimedToolCallIds);

                if (toolCallId is not null)
                {
                    claimedToolCallIds.Add(toolCallId);

                    if (!_trackedBackgroundShells.TryGetValue(toolCallId, out var tracked))
                    {
                        tracked = new TrackedBackgroundShell
                        {
                            RootToolCallId = toolCallId,
                            Command = command,
                            StartedUtc = shell.StartedAt.UtcDateTime,
                        };
                        _trackedBackgroundShells[toolCallId] = tracked;
                    }
                    tracked.ShellId = shell.Id;

                    RememberRunningBackgroundShell(toolCallId, shell.StartedAt);
                    _transcriptBuilder.SetTerminalRunningInBackground(toolCallId, true, startedUtc: shell.StartedAt);
                }

                // One progress read feeds both the shell's activity row and its terminal card.
                var progress = await TryGetTaskProgressAsync(session, shell.Id);
                if (!ReferenceEquals(_activeSession, session) || CurrentChat?.Id != chat.Id)
                    return;

                FindSessionActivity(shell.Id)?.ApplyProgress(progress);
                if (toolCallId is not null
                    && progress is TaskProgressShell { RecentOutput: { } recentOutput }
                    && !string.IsNullOrWhiteSpace(recentOutput))
                {
                    var output = recentOutput.TrimEnd();
                    ApplyTerminalOutputAndNotify(chat, toolCallId, output, replaceExistingOutput: true);
                    _transcriptBuilder.UpdateTerminalOutput(toolCallId, output, true);
                }
            }

            // Agents and client-owned tasks report what they are doing through the same progress API. A
            // linked agent's row is its run, which session events already keep live.
            foreach (var item in RunningSessionActivities.Where(item => !item.IsCommand && !item.HasRun).ToList())
            {
                var progress = await TryGetTaskProgressAsync(session, item.Id);
                if (!ReferenceEquals(_activeSession, session) || CurrentChat?.Id != chat.Id)
                    return;

                item.ApplyProgress(progress);
            }

            // The final per-task await may also have spanned a chat switch; re-check before mutating
            // completion / status state so a stale poll cannot complete or restyle another chat's cards.
            if (!ReferenceEquals(_activeSession, session) || CurrentChat?.Id != chat.Id)
                return;

            // Any tracked shell whose card was not re-observed running this poll has finished. Keying on
            // the claimed tool-call ids (not command text) means an identical-command sibling that is
            // still running no longer keeps a finished card marked "running".
            foreach (var tracked in _trackedBackgroundShells.Values.ToList())
            {
                if (!claimedToolCallIds.Contains(tracked.RootToolCallId))
                    CompleteBackgroundShell(tracked);
            }

            UpdateBackgroundStatusLine(chat.Id);

            var runtime = GetOrCreateRuntimeState(chat.Id);
            if (_trackedBackgroundShells.Count == 0 && !HasRunningSessionActivities && !runtime.HasPendingBackgroundWork)
                StopBackgroundShellMonitor();
        }
        finally
        {
            _backgroundShellPollInFlight = false;
        }
    }

    private static bool IsRunningBackgroundShell(TaskInfoShell shell)
        => shell.Status == GitHub.Copilot.Rpc.TaskStatus.Running
           && shell.ExecutionMode is { } mode
           && mode == TaskExecutionMode.Background;

    /// <summary>
    /// Best-effort live progress: a transient failure returns null so rows and cards keep their last
    /// known state instead of blanking between polls.
    /// </summary>
    private static async Task<TaskProgress?> TryGetTaskProgressAsync(CopilotSession session, string taskId)
    {
        try
        {
            return (await session.Rpc.Tasks.GetProgressAsync(taskId, CancellationToken.None)).Progress;
        }
        catch
        {
            return null;
        }
    }

    private void ApplyTerminalOutputAndNotify(
        Chat chat,
        string toolCallId,
        string output,
        bool replaceExistingOutput)
    {
        var toolMessage = chat.Messages.LastOrDefault(message => message.ToolCallId == toolCallId);
        var previous = toolMessage?.ToolOutput;
        ToolDisplayHelper.ApplyTerminalOutput(
            chat,
            toolCallId,
            output,
            replaceExistingOutput);
        if (toolMessage is not null
            && !string.Equals(previous, toolMessage.ToolOutput, StringComparison.Ordinal))
        {
            Messages.LastOrDefault(message => ReferenceEquals(message.Message, toolMessage))
                ?.NotifyToolDetailsChanged();
        }
    }

    /// <summary>Background lifetime is shown independently of the assistant's typing status.</summary>
    private void UpdateBackgroundStatusLine(Guid chatId)
    {
        if (CurrentChat?.Id != chatId)
            return;

        BackgroundActivityText = RunningSessionActivities.Count == 0
            ? Loc.Get("Chat_BackgroundActivity")
            : string.Format(
                Loc.Status_BackgroundRunning,
                FormatCompactElapsed(DateTimeOffset.UtcNow - RunningSessionActivities.Min(static item => item.StartedAt)));
    }

    /// <summary>Compact, human-friendly elapsed readout: "8s", "1m 04s", "1h 12m".</summary>
    private static string FormatCompactElapsed(TimeSpan elapsed)
    {
        var totalSeconds = (long)Math.Max(0, Math.Floor(elapsed.TotalSeconds));

        if (totalSeconds < 60)
            return Loc.Get(nameof(Loc.Chat_DurationSeconds), totalSeconds);

        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;

        if (minutes < 60)
            return Loc.Get(nameof(Loc.Chat_ElapsedMinutesSeconds), minutes, seconds);

        var hours = minutes / 60;
        minutes %= 60;
        return Loc.Get(nameof(Loc.Chat_ElapsedHoursMinutes), hours, minutes);
    }
}
