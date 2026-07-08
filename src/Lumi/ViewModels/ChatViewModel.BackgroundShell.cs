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
/// specific "Running in background · elapsed" readout. Cards resolve to their final state the moment
/// their shell leaves the running set (or when the session goes idle).
/// </summary>
public partial class ChatViewModel
{
    private static readonly TimeSpan BackgroundShellPollInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>Shared empty map for <see cref="RebuildTranscript"/> when there is no current chat.</summary>
    private static readonly IReadOnlyDictionary<string, DateTimeOffset> EmptyRunningBackgroundShells =
        new Dictionary<string, DateTimeOffset>(0);

    private DispatcherTimer? _backgroundShellMonitor;
    private bool _backgroundShellPollInFlight;

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
    }

    private void CompleteBackgroundShell(TrackedBackgroundShell tracked)
    {
        _trackedBackgroundShells.Remove(tracked.RootToolCallId);
        ForgetRunningBackgroundShell(tracked.RootToolCallId);
        var durationMs = Math.Max(0, (DateTime.UtcNow - tracked.StartedUtc).TotalMilliseconds);
        _transcriptBuilder.SetTerminalRunningInBackground(tracked.RootToolCallId, false, durationMs);
    }

    private async Task PollBackgroundShellsAsync()
    {
        if (_backgroundShellPollInFlight)
            return;

        _backgroundShellPollInFlight = true;
        try
        {
            var session = _activeSession;
            var chat = CurrentChat;
            if (session is null || chat is null)
            {
                // No active session to poll (chat switch mid-flight, remote shutdown, or CLI reconnect
                // nulled it). Stop the timer unconditionally — it is always re-armed by
                // EnsureBackgroundShellMonitorRunning when new async work appears (TrackBackgroundShell,
                // a background-tasks-changed event, or switching back to a chat with pending work), so
                // stopping here just avoids a timer firing forever against a dead session.
                StopBackgroundShellMonitor();
                return;
            }

            List<TaskInfoShell> runningShells;
            try
            {
                var list = await session.Rpc.Tasks.ListAsync(CancellationToken.None);
                runningShells = list.Tasks
                    .OfType<TaskInfoShell>()
                    .Where(IsRunningBackgroundShell)
                    .ToList();
            }
            catch
            {
                // Transient RPC failure — keep the current UI and try again next tick.
                return;
            }

            // The chat may have been switched (or the session torn down) while the RPC was in flight;
            // if so the shared transcript builder now reflects a different chat, so abandon this stale
            // poll rather than marking another chat's cards from this chat's shell list.
            if (!ReferenceEquals(_activeSession, session) || CurrentChat?.Id != chat.Id)
                return;

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
                await UpdateBackgroundShellOutputAsync(session, shell, toolCallId);
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
            if (_trackedBackgroundShells.Count == 0 && !runtime.HasPendingBackgroundWork)
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

    private async Task UpdateBackgroundShellOutputAsync(CopilotSession session, TaskInfoShell shell, string toolCallId)
    {
        try
        {
            var progress = await session.Rpc.Tasks.GetProgressAsync(shell.Id, CancellationToken.None);
            if (progress.Progress is TasksGetProgressResultProgressShell shellProgress
                && !string.IsNullOrWhiteSpace(shellProgress.RecentOutput))
            {
                _transcriptBuilder.UpdateTerminalOutput(toolCallId, shellProgress.RecentOutput.TrimEnd(), true);
            }
        }
        catch
        {
            // Best-effort live tail; ignore transient progress failures.
        }
    }

    /// <summary>Replaces the generic "Generating…" spinner with a specific, live "Running in background ·
    /// elapsed" readout once the assistant's turn has ended but a background shell is still running.</summary>
    private void UpdateBackgroundStatusLine(Guid chatId, IReadOnlyList<TaskInfoShell> runningShells)
    {
        if (runningShells.Count == 0)
            return;

        var runtime = GetOrCreateRuntimeState(chatId);
        // While the model is actively streaming, leave its own status label alone.
        if (runtime.IsStreaming)
            return;

        var earliest = runningShells.Min(static s => s.StartedAt.UtcDateTime);
        var elapsed = DateTime.UtcNow - earliest;
        var text = string.Format(Loc.Status_BackgroundRunning, FormatCompactElapsed(elapsed));

        runtime.StatusText = text;
        if (CurrentChat?.Id == chatId)
            StatusText = text;
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
