using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

/// <summary>
/// Going back to an earlier message. An edit discards the conversation from the edited message on;
/// it can also put back the files the agent changed since, which the session captured as it worked.
/// The edit bar offers them, with the list of files, before anything changes; the message sent in
/// place of the edited one then says what happened to them.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>The files the edit can put back, or null when it can't put back any.</summary>
    [ObservableProperty] private FileRewindOffer? _editFileRewind;

    /// <summary>Why the edit can't offer to put files back right now, when that is worth saying.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditRewindNote))]
    private string? _editRewindNote;

    public bool HasEditRewindNote => EditRewindNote is not null;

    /// <summary>True while the edit is still finding out which files it can put back, once that takes long enough to say so.</summary>
    [ObservableProperty] private bool _isCheckingEditFiles;

    /// <summary>
    /// How long the edit bar waits to learn which files it can put back before going without. A live
    /// session answers at once and a bare resume in a fraction of a second; this only bounds a runtime
    /// that has stopped answering.
    /// </summary>
    private static readonly TimeSpan EditRewindReadBudget = TimeSpan.FromSeconds(10);

    /// <summary>How long the read runs before the bar says it's looking, so a quick one never flashes the line.</summary>
    private static readonly TimeSpan EditRewindCheckingDelay = TimeSpan.FromMilliseconds(400);

    private CancellationTokenSource? _editRewindCts;

    /// <summary>Reads, for the edit bar, which files going back to before <paramref name="message"/> would put back.</summary>
    private void OfferEditFileRewind(Chat chat, ChatMessage message)
    {
        ClearEditFileRewind();
        // The edit is sent with the model the composer has for it: the edited message's own, unless another is
        // picked while editing (see OnSelectedModelChanged). The chat's last model may be on another account.
        if (chat.CopilotSessionId is not { Length: > 0 } sessionId
            || !EditResumesSession(chat, SelectedModel, out var provider))
        {
            return;
        }

        var cts = new CancellationTokenSource(EditRewindReadBudget);
        _editRewindCts = cts;
        _ = ReadAsync(cts.Token);

        async Task ReadAsync(CancellationToken ct)
        {
            try
            {
                var userMessagesBefore = await _dataStore.CountUserMessagesBeforeAsync(chat, message);
                var read = ReadFilesToPutBackAsync(chat, sessionId, provider, userMessagesBefore, ct);
                if (await Task.WhenAny(read, Task.Delay(EditRewindCheckingDelay, ct)) != read
                    && ReferenceEquals(_editRewindCts, cts))
                {
                    IsCheckingEditFiles = true;
                }

                // The token ends the read at its budget, but a runtime that has stopped answering can ignore it (see
                // ForkSessionAtTurnAsync): the wait is bounded too, so the bar never stays checking.
                var preview = await read.WaitAsync(EditRewindReadBudget);
                if (preview is null || !ReferenceEquals(_editRewindCts, cts))
                    return;

                EditFileRewind = FileRewindOffer.From(preview);
                EditRewindNote = preview.IsBusy ? Loc.Rewind_FilesBusy : null;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // The edit still works; it just leaves the files as they are.
                Trace.TraceWarning("[Rewind] Could not read which files an edit can put back: {0}", ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_editRewindCts, cts))
                {
                    _editRewindCts = null;
                    IsCheckingEditFiles = false;
                }

                cts.Dispose();
            }
        }
    }

    /// <summary>
    /// Which files going back to the turn after <paramref name="userMessagesBefore"/> user messages would
    /// put back, or null when the session can't say. <paramref name="provider"/> is the account the edit
    /// resumes the session with (see <see cref="EditResumesSession"/>).
    /// </summary>
    /// <remarks>
    /// A session that isn't live is read through a bare handle, as a fork reads it: a full resume
    /// would also build the prompt, run capability discovery and wait for connectors, seconds the
    /// bar shouldn't spend on a read. The edit resumes the session properly when it is sent.
    /// </remarks>
    private async Task<RewindFilesPreview?> ReadFilesToPutBackAsync(
        Chat chat, string sessionId, ProviderConfig? provider, int userMessagesBefore, CancellationToken ct)
    {
        // A release of the chat's session still in flight is waited out, as a resume would wait for it.
        await AwaitPendingSessionReleaseAsync(chat.Id, ct);
        ct.ThrowIfCancellationRequested();
        switch (GetForkSessionHold(sessionId, out var live))
        {
            case ForkSessionHold.Live:
                return await PreviewAsync(live!, ct);
            case ForkSessionHold.Recovering:
                return null;
        }

        var read = _copilotService.UseResumedSessionReadOnlyAsync(sessionId, PreviewAsync, ct, provider);
        // The bare handle is released when the read ends. Tracked as a pending release, it keeps this
        // surface from resuming the chat's own session (a send, say) while the handle is still out.
        TrackPendingSessionRelease(chat.Id, read.ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default));
        return await read;

        async Task<RewindFilesPreview?> PreviewAsync(CopilotSession session, CancellationToken token)
            => await FindSessionTurnAsync(session, userMessagesBefore, token) is { } turn
                ? await SessionRewind.PreviewFilesAsync(session, turn, token)
                : null;
    }

    /// <summary>
    /// Whether an edit sent with <paramref name="model"/> goes back in the chat's saved session, and the
    /// account it resumes the session with. A send resumes the session only with the account the session
    /// was made with (<c>EnsureSessionAsync</c>), and only while nothing has queued a new session for the
    /// chat (a model of another account picked since, or an error the session can't recover from, which
    /// <c>ConsumePendingSessionInvalidation</c> acts on first); otherwise it starts a new session, which has
    /// nothing to put back.
    /// </summary>
    private bool EditResumesSession(Chat chat, string? model, out ProviderConfig? provider)
    {
        var route = ResolveModelRouteForChat(model, chat, allowLegacyByWireId: true);
        provider = route.Provider;
        return !_pendingSessionInvalidations.Contains(chat.Id)
            && !route.IsInvalidByok
            && (!_dataStore.Data.Settings.UseBYOKOnly || route.IsByok)
            && string.Equals(
                chat.SessionProviderSignature,
                ByokConfigHelper.BuildProviderSignature(route.Provider, route.ByokModel),
                StringComparison.Ordinal);
    }

    /// <summary>
    /// A model picked while editing is the one the edit is sent with. When it moves the edit into or out
    /// of the chat's session, the bar's offer follows; another model on the same account keeps the offer,
    /// and the person's choice in it.
    /// </summary>
    partial void OnSelectedModelChanged(string? oldValue, string? newValue)
    {
        if (IsEditingMessage
            && !_suppressModelSelectionSideEffects
            && CurrentChat is { } chat
            && _editingUserMessage is { } message
            && EditResumesSession(chat, oldValue, out _) != EditResumesSession(chat, newValue, out _))
        {
            OfferEditFileRewind(chat, message);
        }
    }

    /// <summary>Withdraws the edit bar's offer, stopping a read still running.</summary>
    private void ClearEditFileRewind()
    {
        _editRewindCts?.Cancel();
        _editRewindCts = null;
        EditFileRewind = null;
        EditRewindNote = null;
        IsCheckingEditFiles = false;
    }

    /// <summary>
    /// The session's <c>user.message</c> event that starts the turn of the chat's user message with
    /// <paramref name="userMessagesBefore"/> user messages before it, or null when the chat and its
    /// session don't line up (a session rebuilt from a replayed transcript, say).
    /// </summary>
    private static async Task<string?> FindSessionTurnAsync(
        CopilotSession session, int userMessagesBefore, CancellationToken ct)
    {
        var events = await session.GetEventsAsync(ct);
        return PendingTurnRecoveryAnalyzer.SelectEditTruncationTarget(events, userMessagesBefore)?.Id.ToString();
    }

    /// <summary>Shows what going back did to the files under <paramref name="message"/>, wherever it is shown.</summary>
    private void ShowFileRewind(ChatMessage message, RewindFilesOutcome outcome)
    {
        message.FileRewind = outcome;
        foreach (var shown in Messages)
        {
            if (ReferenceEquals(shown.Message, message))
                shown.ShowFileRewind(outcome);
        }
    }
}
