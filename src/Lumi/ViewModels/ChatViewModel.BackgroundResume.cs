using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using GitHub.Copilot.SDK;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    /// <summary>
    /// Debounce delay before auto-triggering a turn after background task completion.
    /// Multiple tasks completing close together are batched into a single turn.
    /// </summary>
    private static readonly TimeSpan BackgroundResumeDebounceDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Schedules an auto-triggered LLM turn after a background task completes.
    /// Uses a debounce: if multiple tasks complete within the delay window, only one turn is triggered.
    /// </summary>
    private void ScheduleBackgroundTaskAutoResume(
        CopilotSession session,
        Chat chat,
        ChatRuntimeState runtime,
        string agentName,
        ref CancellationTokenSource? debounceCts,
        HashSet<string> completedIds)
    {
        if (!_dataStore.Data.Settings.AutoResumeBackgroundTasks)
            return;

        // Cancel any pending debounce — we'll restart the timer
        debounceCts?.Cancel();
        debounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        debounceCts = cts;

        // Clear completed IDs and release the session-keep-alive count (caller holds bgResumeGate)
        var count = completedIds.Count;
        completedIds.Clear();
        Interlocked.Add(ref runtime.PendingBackgroundTaskCount, -count);

        // Prevent session release while the debounce timer is active
        runtime.HasPendingAutoResume = true;

        _ = ExecuteBackgroundResumeAsync(session, chat, runtime, agentName, cts.Token);
    }

    private async Task ExecuteBackgroundResumeAsync(
        CopilotSession session,
        Chat chat,
        ChatRuntimeState runtime,
        string agentName,
        CancellationToken ct)
    {
        try
        {
            // Debounce — wait for more completions to batch
            await Task.Delay(BackgroundResumeDebounceDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            runtime.HasPendingAutoResume = false;
            return; // Debounce was reset by a newer completion
        }

        // All checks run on UI thread for thread safety
        await Dispatcher.UIThread.InvokeAsync((Func<Task>)(async () =>
        {
            // Guard: session still valid and idle
            if (runtime.IsBusy || runtime.IsStreaming)
            {
                runtime.HasPendingAutoResume = false;
                return;
            }

            // Guard: setting still enabled
            if (!_dataStore.Data.Settings.AutoResumeBackgroundTasks)
            {
                runtime.HasPendingAutoResume = false;
                return;
            }

            // Guard: session still in cache (not disconnected or deleted)
            if (!_sessionCache.TryGetValue(chat.Id, out var cachedSession) || cachedSession != session)
            {
                runtime.HasPendingAutoResume = false;
                return;
            }

            // Guard: Copilot is connected
            if (!_copilotService.IsConnected)
            {
                runtime.HasPendingAutoResume = false;
                return;
            }

            runtime.HasPendingAutoResume = false;

            // Set busy state so the UI shows activity
            runtime.IsBusy = true;
            runtime.IsStreaming = true;
            runtime.StatusText = Loc.Status_Thinking;
            if (_activeSession == session)
            {
                IsBusy = runtime.IsBusy;
                IsStreaming = runtime.IsStreaming;
                StatusText = runtime.StatusText;
            }

            // Send the auto-trigger prompt
            runtime.LastTurnWasAutoResume = true;
            try
            {
                var sendOptions = new MessageOptions
                {
                    Prompt = "[System: One or more background tasks you started earlier have now completed. Review the results and provide a brief summary to the user. Mention what finished and whether it succeeded or failed.]"
                };
                await session.SendAsync(sendOptions, CancellationToken.None);
            }
            catch (Exception)
            {
                // Auto-resume failed — reset state, don't crash
                runtime.LastTurnWasAutoResume = false;
                runtime.IsBusy = false;
                runtime.IsStreaming = false;
                runtime.StatusText = "";
                if (_activeSession == session)
                {
                    IsBusy = false;
                    IsStreaming = false;
                    StatusText = "";
                    _transcriptBuilder.HideTypingIndicator();
                }
            }
        }));

        // After the auto-triggered turn completes, set unread + OS notification
        // (only if user is NOT viewing this chat).
        // The AssistantTurnEndEvent/SessionIdleEvent handlers will handle the response rendering.
        // We rely on OnAssistantTurnEndForBackgroundResume via the already-subscribed event handlers.
        // The unread flag is set in SessionIdleEvent since that's when the response is fully done.
    }

    /// <summary>
    /// Called from SessionIdleEvent when a turn was auto-triggered. Fires an OS toast
    /// with the response preview. Returns true if this was a background resume turn.
    /// </summary>
    private bool HandleBackgroundResumeCompleted(Chat chat, CopilotSession session, string agentName)
    {
        var runtime = GetOrCreateRuntimeState(chat.Id);
        if (!runtime.LastTurnWasAutoResume)
            return false;

        runtime.LastTurnWasAutoResume = false;

        // OS toast notification with response preview
        if (_dataStore.Data.Settings.NotificationsEnabled)
        {
            var lastAssistant = chat.Messages.LastOrDefault(m => m.Role == "assistant");
            var preview = lastAssistant?.Content;
            if (preview is { Length: > 100 })
                preview = preview[..100] + "…";

            var body = !string.IsNullOrWhiteSpace(preview)
                ? $"{chat.Title} — {preview}"
                : $"{chat.Title} — {Loc.Notification_ResponseReady}";

            NotificationService.ShowIfInactive(agentName, body);
        }

        return true;
    }
}
