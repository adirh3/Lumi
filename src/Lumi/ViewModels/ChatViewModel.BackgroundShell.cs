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

public sealed record SessionActivityItem(string Id, string Title, string Kind, string Detail, string Elapsed)
{
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

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
/// specific "Running in background · elapsed" readout. The same task snapshot supplies the compact
/// activity list, including agents and client-owned tasks. Cards resolve when their shell finishes.
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
        RunningSessionActivities = [];
        BackgroundActivityNotice = Loc.Get("Chat_BackgroundActivityLoading");
    }

    internal void ApplyBackgroundActivitySnapshot(IEnumerable<TaskInfo> tasks, DateTimeOffset now)
    {
        var items = BuildBackgroundActivityItems(tasks, now);
        if (!RunningSessionActivities.SequenceEqual(items))
            RunningSessionActivities = items;
        BackgroundActivityNotice = items.Count == 0 ? Loc.Get("Chat_BackgroundActivityEmpty") : null;
    }

    internal static IReadOnlyList<SessionActivityItem> BuildBackgroundActivityItems(
        IEnumerable<TaskInfo> tasks,
        DateTimeOffset now)
    {
        return tasks.Select(task => task switch
        {
            TaskInfoShell shell when IsRunningBackgroundShell(shell) => Create(
                shell.Id, Loc.Get("Chat_BackgroundCommand"),
                string.IsNullOrWhiteSpace(shell.Description) ? shell.Command : shell.Description,
                shell.Command, shell.StartedAt),
            TaskInfoAgent agent when agent.Status == GitHub.Copilot.Rpc.TaskStatus.Running => Create(
                agent.Id, Loc.Get("Chat_BackgroundAgent"),
                string.IsNullOrWhiteSpace(agent.DisplayName) ? agent.Description : agent.DisplayName,
                agent.Description, agent.ActiveStartedAt ?? agent.StartedAt),
            TaskInfoClient client when client.Status == TaskClientStatus.Running => Create(
                client.Id, Loc.Get("Chat_BackgroundTask"),
                string.IsNullOrWhiteSpace(client.DisplayName) ? client.Description : client.DisplayName,
                client.Description, client.ActiveStartedAt ?? client.StartedAt),
            _ => null
        }).OfType<SessionActivityItem>().ToArray();

        SessionActivityItem Create(string id, string kind, string? title, string? detail, DateTimeOffset startedAt)
        {
            title = string.IsNullOrWhiteSpace(title) ? kind : title.Trim();
            detail = detail?.Trim() ?? string.Empty;
            return new SessionActivityItem(
                id, title, kind, detail == title ? string.Empty : detail,
                FormatCompactElapsed(now - startedAt));
        }
    }

    private static async Task StopRemainingSessionTasksAsync(CopilotSession session, ChatRuntimeState runtime)
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
                    throw new InvalidOperationException($"Copilot could not stop background task {id}.");
            }
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
                if (toolCallId is null)
                    continue;

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
                await UpdateBackgroundShellOutputAsync(session, chat, shell, toolCallId);
            }

            // The final per-shell await may also have spanned a chat switch; re-check before mutating
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

            UpdateBackgroundStatusLine(chat.Id, runningShells);

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

    private async Task UpdateBackgroundShellOutputAsync(
        CopilotSession session,
        Chat chat,
        TaskInfoShell shell,
        string toolCallId)
    {
        try
        {
            var progress = await session.Rpc.Tasks.GetProgressAsync(shell.Id, CancellationToken.None);
            if (progress.Progress is TaskProgressShell shellProgress
                && !string.IsNullOrWhiteSpace(shellProgress.RecentOutput))
            {
                var output = shellProgress.RecentOutput.TrimEnd();
                ApplyTerminalOutputAndNotify(chat, toolCallId, output, replaceExistingOutput: true);
                _transcriptBuilder.UpdateTerminalOutput(toolCallId, output, true);
            }
        }
        catch
        {
            // Best-effort live tail; ignore transient progress failures.
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
    private void UpdateBackgroundStatusLine(Guid chatId, IReadOnlyList<TaskInfoShell> runningShells)
    {
        if (runningShells.Count == 0)
        {
            if (CurrentChat?.Id == chatId)
                BackgroundActivityText = Loc.Get("Chat_BackgroundActivity");
            return;
        }

        var earliest = runningShells.Min(static s => s.StartedAt.UtcDateTime);
        var elapsed = DateTime.UtcNow - earliest;
        var text = string.Format(Loc.Status_BackgroundRunning, FormatCompactElapsed(elapsed));

        if (CurrentChat?.Id == chatId)
            BackgroundActivityText = text;
    }

    /// <summary>Compact, human-friendly elapsed readout: "8s", "1m 04s", "1h 12m".</summary>
    private static string FormatCompactElapsed(TimeSpan elapsed)
    {
        var totalSeconds = (long)Math.Max(0, Math.Floor(elapsed.TotalSeconds));

        if (totalSeconds < 60)
            return $"{totalSeconds}s";

        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;

        if (minutes < 60)
            return $"{minutes}m {seconds:D2}s";

        var hours = minutes / 60;
        minutes %= 60;
        return $"{hours}h {minutes:D2}m";
    }
}
