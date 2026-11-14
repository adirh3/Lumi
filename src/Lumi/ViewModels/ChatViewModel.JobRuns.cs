using System;
using System.Linq;
using Avalonia.Threading;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

/// <summary>
/// Change-only job runs. A job whose <see cref="BackgroundJob.NoChangeBehavior"/> is not "reply" runs
/// its turn as usual, but the turn surfaces only if the agent reports a change (or the run fails, is
/// stopped, or the user joins in). Until then nothing marks the chat unread, moves it up the list,
/// notifies, or fires its lifecycle events; a run that found nothing new keeps all of that quiet and
/// collapses to its job's status line (or to nothing).
/// </summary>
public partial class ChatViewModel
{
    /// <summary>
    /// The chat's change-only job run, if it still covers the chat's current turn. A run the chat has
    /// moved past is dropped here; one moved past while still undecided (the user sent a message into
    /// it) is resolved as news first.
    /// </summary>
    private BackgroundJobRunState? CurrentJobRun(Chat chat)
    {
        if (chat.ActiveJobRun is not { } run)
            return null;

        if (IsJobRunCurrent(chat, run))
            return run;

        if (run.Outcome == BackgroundJobRunOutcome.Pending)
            ResolveJobRunAsChanged(chat, run);
        if (ReferenceEquals(chat.ActiveJobRun, run))
            chat.ActiveJobRun = null;
        return null;
    }

    private bool IsJobRunCurrent(Chat chat, BackgroundJobRunState run)
    {
        if (run.TurnSequence >= 0)
        {
            var runtime = GetOrCreateRuntimeState(chat.Id);
            long turnSequence;
            lock (runtime)
                turnSequence = runtime.LifecycleTurnSequence;
            if (turnSequence != run.TurnSequence)
                return false;
        }

        // Anything the user sent after the wake makes the turn theirs as well.
        var lastUserMessage = chat.Messages.LastOrDefault(message => message.Role == "user");
        return lastUserMessage is null || lastUserMessage.Id == run.UserMessageId;
    }

    /// <summary>Whether the chat's current turn is a change-only job run that has not (yet) turned up news.</summary>
    private bool IsHoldingJobRunNews(Chat chat)
        => CurrentJobRun(chat) is { Outcome: not BackgroundJobRunOutcome.Changed };

    /// <summary>
    /// Holds a lifecycle event back while the chat's change-only job run is undecided, and drops it
    /// once the run turned out quiet. Returns false when the event should be published now.
    /// </summary>
    private bool TryHoldJobRunLifecycleEvent(Chat chat, ChatLifecycleEvent chatEvent)
    {
        if (CurrentJobRun(chat) is not { } run)
            return false;

        switch (run.Outcome)
        {
            case BackgroundJobRunOutcome.Changed:
                return false;
            case BackgroundJobRunOutcome.Quiet:
                return true;
        }

        // A run that fails or is stopped is news, and so is every event it held back.
        if (chatEvent.EventType is ChatLifecycleEventTypes.Error or ChatLifecycleEventTypes.Aborted)
        {
            ResolveJobRunAsChanged(chat, run);
            QueueSaveChat(chat, saveIndex: true);
            return false;
        }

        run.HeldEvents.Add(chatEvent);
        return true;
    }

    /// <summary>
    /// Records what the agent reported for the chat's change-only job run (via report_job_outcome).
    /// Returns false when the chat has no undecided run to report on.
    /// </summary>
    internal bool RecordJobRunOutcome(Guid chatId, bool changed, string? status)
    {
        var chat = _dataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == chatId);
        if (chat is null || CurrentJobRun(chat) is not { Outcome: BackgroundJobRunOutcome.Pending } run)
            return false;

        run.ReportedChanged = changed;
        run.ReportedStatus = BackgroundJobRunPresentation.NormalizeStatus(status);
        return true;
    }

    /// <summary>
    /// The run is news: it surfaces like any reply. Releases the lifecycle events it held, marks the
    /// chat unread when unseen and moves it up the list. The caller saves the chat.
    /// </summary>
    private void ResolveJobRunAsChanged(Chat chat, BackgroundJobRunState run)
    {
        if (run.Outcome != BackgroundJobRunOutcome.Pending)
            return;

        run.Outcome = BackgroundJobRunOutcome.Changed;
        if (FindBackgroundJob(run.JobId) is { } job)
        {
            lock (job.SyncRoot)
            {
                if (!string.IsNullOrEmpty(run.ReportedStatus))
                    job.LastReportedStatus = run.ReportedStatus;
                job.QuietRunCount = 0;
                // Its news now sits below the old status line; the next quiet run starts a new one.
                job.LastQuietRunMessageId = null;
            }
            SaveBackgroundJobRunState();
        }

        foreach (var held in run.HeldEvents)
            _chatEvents.Publish(held);
        run.HeldEvents.Clear();

        chat.UpdatedAt = DateTimeOffset.Now;
        if (!IsChatOnScreen(chat.Id))
            chat.HasUnreadMessages = true;
        _dataStore.MarkChatChanged(chat);
        ChatUpdated?.Invoke();
    }

    /// <summary>
    /// Decides the chat's change-only job run when its reply completes. Returns true when the run found
    /// nothing new: it is then recorded on its job and folded away, and the caller skips everything a
    /// finished reply would otherwise announce.
    /// </summary>
    private bool TryCompleteJobRunQuietly(Chat chat, bool stoppedByUser)
    {
        if (CurrentJobRun(chat) is not { } run)
            return false;
        if (run.Outcome != BackgroundJobRunOutcome.Pending)
            return run.Outcome == BackgroundJobRunOutcome.Quiet;

        if (stoppedByUser || run.ReportedChanged != false)
        {
            ResolveJobRunAsChanged(chat, run);
            return false;
        }

        run.Outcome = BackgroundJobRunOutcome.Quiet;
        run.HeldEvents.Clear();

        var wake = chat.Messages.FirstOrDefault(message => message.Id == run.UserMessageId);
        if (wake is not null)
        {
            wake.JobRunQuiet = true;
            wake.JobRunStatus = string.IsNullOrEmpty(run.ReportedStatus) ? null : run.ReportedStatus;
        }

        if (FindBackgroundJob(run.JobId) is { } job)
        {
            lock (job.SyncRoot)
            {
                if (!string.IsNullOrEmpty(run.ReportedStatus))
                    job.LastReportedStatus = run.ReportedStatus;
                job.QuietRunCount++;
                job.LastQuietRunAt = DateTimeOffset.Now;
                job.LastQuietRunMessageId = run.UserMessageId;
            }
            SaveBackgroundJobRunState();
        }

        _dataStore.MarkChatChanged(chat);
        if (CurrentChat?.Id == chat.Id)
        {
            // Let the rest of the turn's completion settle before the transcript folds the run away.
            Dispatcher.UIThread.Post(() =>
            {
                if (CurrentChat?.Id == chat.Id)
                    RebuildTranscript();
            });
        }

        return true;
    }

    private BackgroundJob? FindBackgroundJob(Guid jobId)
        => _dataStore.SnapshotBackgroundJobs().FirstOrDefault(job => job.Id == jobId);

    private void SaveBackgroundJobRunState()
    {
        _dataStore.MarkBackgroundJobsChanged();
        _ = _dataStore.SaveAsync();
        FeatureManagementStateChanged?.Invoke();
    }
}
