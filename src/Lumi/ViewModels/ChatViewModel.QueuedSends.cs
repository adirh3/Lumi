using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Models;

using ChatMessage = Lumi.Models.ChatMessage;

namespace Lumi.ViewModels;

/// <summary>A message queued to be sent once Lumi's reply finishes, shown above the composer.</summary>
public sealed partial class QueuedSendItem : ObservableObject
{
    internal QueuedSendItem(ChatMessage message, ChatViewModel owner)
    {
        Message = message;
        Text = string.Join(' ', message.Content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        SteerNowCommand = new AsyncRelayCommand(() => owner.RunQueuedSendActionAsync(
            () => owner.SteerQueuedSendNowAsync(message)));
        EditCommand = new AsyncRelayCommand(() => owner.RunQueuedSendActionAsync(
            () => owner.EditQueuedSendWithRemoteSaveAsync(message)));
        RemoveCommand = new AsyncRelayCommand(() => owner.RunQueuedSendActionAsync(
            () => owner.RemoveQueuedSendWithRemoteSaveAsync(message)));
    }

    internal ChatMessage Message { get; }

    /// <summary>The message on one line.</summary>
    public string Text { get; }

    /// <summary>Its place in the queue, from 1: the order the messages will be sent in.</summary>
    [ObservableProperty]
    private int _position;

    public int AttachmentCount => Message.Attachments.Count;

    public bool HasAttachments => AttachmentCount > 0;

    public IAsyncRelayCommand SteerNowCommand { get; }

    public IRelayCommand EditCommand { get; }

    public IRelayCommand RemoveCommand { get; }
}

public partial class ChatViewModel
{
    // Messages queued to follow the running reply, per chat. They wait above the composer rather than in
    // the transcript, so the reply that is still streaming never shows beneath them; each one enters the
    // transcript when it is sent, as the next turn.
    private readonly Dictionary<Guid, List<ChatMessage>> _waitingSends = new();
    private readonly SemaphoreSlim _remoteQueueGate = new(1, 1);
    private Guid? _pendingRemoteQueueMessageId;

    /// <summary>The current chat's queued messages, oldest first.</summary>
    public ObservableCollection<QueuedSendItem> QueuedSends { get; } = [];

    public bool HasQueuedSends => QueuedSends.Count > 0;

    internal IReadOnlyList<ChatMessage> GetWaitingSends(Guid chatId)
        => _waitingSends.TryGetValue(chatId, out var waiting) ? waiting
            : _dataStore.Data.Chats.Find(chat => chat.Id == chatId)?.RemoteQueuedSends ?? [];

    internal bool CanQueueExternalMessage(Chat chat) =>
        !chat.IsPaused && IsChatRuntimeActive(chat.Id) && !CanStartTurnOnReadySession(chat);

    /// <summary>
    /// Queues the draft to be sent after the reply Lumi is writing, instead of steering it into that reply.
    /// When nothing is running it is an ordinary send.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task QueueMessage()
    {
        if (!await FinishVoiceBeforeSendAsync() || _editingUserMessage is not null)
            return;

        var prompt = PromptText?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        // A paused chat already holds every send until it resumes.
        if (CurrentChat is not { } chat
            || chat.IsPaused
            || !IsChatRuntimeActive(chat.Id)
            || CanStartTurnOnReadySession(chat))
        {
            await SendMessageCore(prompt, consumeComposerPrompt: true);
            return;
        }

        if (BlockSendForByokOnly(chat, ResolveSelectedModelForChat(chat), prompt, consumeComposerPrompt: true))
            return;

        var message = BuildQueuedSend(chat, prompt);
        if (message is null)
            return;

        if (!_waitingSends.TryGetValue(chat.Id, out var waiting))
            _waitingSends[chat.Id] = waiting = [];
        waiting.Add(message);
        PromptText = "";
        _chatDrafts.Remove(chat.Id);
        SyncQueuedSends();
    }

    /// <summary>Saves a phone-queued message before acknowledging it, without consuming the desktop draft.</summary>
    internal async Task<bool> QueueExternalMessageAsync(
        Chat chat,
        string prompt,
        string author,
        string? remoteRequestId = null,
        string? remoteDeviceId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (BlockSendForByokOnly(chat, ResolveSelectedModelForChat(chat), prompt, consumeComposerPrompt: false)
            || BuildQueuedSend(chat, prompt, author, explicitAttachmentPaths: []) is not { } message)
        {
            return false;
        }

        var wasActive = IsChatRuntimeActive(chat.Id);
        message.RemoteRequestId = remoteRequestId;
        message.RemoteDeviceId = remoteDeviceId;
        await _remoteQueueGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetOrRestoreWaitingSends(chat);
            _pendingRemoteQueueMessageId = message.Id;
            var previousDevice = chat.LastRemoteDeviceId;
            var previousRequest = chat.LastRemoteRequestId;
            if (!string.IsNullOrWhiteSpace(remoteDeviceId) && !string.IsNullOrWhiteSpace(remoteRequestId))
            {
                chat.LastRemoteDeviceId = remoteDeviceId;
                chat.LastRemoteRequestId = remoteRequestId;
            }
            try
            {
                // Once the write starts, wait for its actual outcome rather than cancelling an unknown commit.
                await SaveRemoteQueueAsync(chat, [.. chat.RemoteQueuedSends ?? [], message.Clone()]);
            }
            catch
            {
                chat.LastRemoteDeviceId = previousDevice;
                chat.LastRemoteRequestId = previousRequest;
                throw;
            }

            if (!_dataStore.Data.Chats.Exists(candidate => candidate.Id == chat.Id))
                return false;
            if (!_waitingSends.TryGetValue(chat.Id, out var waiting))
                _waitingSends[chat.Id] = waiting = [];
            waiting.Add(message);
        }
        finally
        {
            _pendingRemoteQueueMessageId = null;
            _remoteQueueGate.Release();
        }
        SyncQueuedSends();
        if (chat.IsPaused)
            SetChatPaused(chat, true);
        else if (wasActive && (!IsChatRuntimeActive(chat.Id) || CanStartTurnOnReadySession(chat)))
            ScheduleQueuedBusySendDrain(chat.Id);
        return true;
    }

    private async Task SaveRemoteQueueAsync(Chat chat, List<ChatMessage>? queue)
    {
        var previous = chat.RemoteQueuedSends;
        chat.RemoteQueuedSends = queue is { Count: > 0 } ? queue : null;
        _dataStore.MarkChatChanged(chat);
        try
        {
            await _dataStore.SaveChatAsync(chat);
        }
        catch
        {
            chat.RemoteQueuedSends = previous;
            throw;
        }
    }

    private void RestoreRemoteQueuedSends(Chat chat)
    {
        if (chat.RemoteQueuedSends is not { Count: > 0 })
            return;
        GetOrRestoreWaitingSends(chat);
        SyncQueuedSends();
    }

    private List<ChatMessage> GetOrRestoreWaitingSends(Chat chat)
    {
        if (!_waitingSends.TryGetValue(chat.Id, out var waiting))
            _waitingSends[chat.Id] = waiting = [];
        foreach (var message in chat.RemoteQueuedSends ?? [])
        {
            if (message.Id != _pendingRemoteQueueMessageId
                && !waiting.Any(candidate => candidate.Id == message.Id)
                && !chat.Messages.Any(candidate => candidate.Id == message.Id))
                waiting.Add(message.Clone());
        }
        return waiting;
    }

    private void ScheduleRestoredRemoteSends(Chat chat)
    {
        if (chat.RemoteQueuedSends is { Count: > 0 } && HasWaitingSends(chat.Id)
            && !chat.IsPaused && (!IsChatRuntimeActive(chat.Id) || CanStartTurnOnReadySession(chat)))
            ScheduleQueuedBusySendDrain(chat.Id);
    }

    private bool HasWaitingSends(Guid chatId)
        => _waitingSends.TryGetValue(chatId, out var waiting) && waiting.Count > 0;

    private bool HasPendingSends(Guid chatId)
        => _queuedBusySendPrompts.ContainsKey(chatId) || HasWaitingSends(chatId);

    /// <summary>Takes the oldest queued message and shows it at the end of the transcript, ready to send.</summary>
    private async Task<ChatMessage?> TakeWaitingSendAsync(Chat? chat)
    {
        if (chat is null || !_waitingSends.TryGetValue(chat.Id, out var waiting) || waiting.Count == 0)
            return null;

        var message = waiting[0];
        return await MoveWaitingSendToTranscriptAsync(chat, message) ? message : null;
    }

    private async Task<bool> MoveWaitingSendToTranscriptAsync(Chat chat, ChatMessage message)
    {
        await _remoteQueueGate.WaitAsync();
        try
        {
            var position = _waitingSends.GetValueOrDefault(chat.Id)?.IndexOf(message) ?? -1;
            if (!RemoveWaitingSend(chat.Id, message))
                return false;
            EnterTranscript(chat, message);
            if (chat.RemoteQueuedSends?.Any(candidate => candidate.Id == message.Id) != true)
                return true;
            try
            {
                await SaveRemoteQueueAsync(chat,
                    chat.RemoteQueuedSends?.Where(candidate => candidate.Id != message.Id).ToList());
            }
            catch
            {
                RemoveQueuedBusySend(chat.Id, message);
                if (!_waitingSends.TryGetValue(chat.Id, out var waiting))
                    _waitingSends[chat.Id] = waiting = [];
                waiting.Insert(Math.Min(position, waiting.Count), message);
                SyncQueuedSends();
                throw;
            }
            return true;
        }
        finally
        {
            _remoteQueueGate.Release();
        }
    }

    private void EnterTranscript(Chat chat, ChatMessage message)
    {
        message.Timestamp = DateTimeOffset.Now;
        message.SteerDelivery = MessageSteerState.Queued;
        message.CanSendNowWhenQueued = false;
        AddQueuedSendToTranscript(chat, message,
            save: chat.RemoteQueuedSends?.Any(candidate => candidate.Id == message.Id) != true);
        SyncQueuedSends();
    }

    private bool RemoveWaitingSend(Guid chatId, ChatMessage message)
    {
        if (!_waitingSends.TryGetValue(chatId, out var waiting) || !waiting.Remove(message))
            return false;

        if (waiting.Count == 0)
            _waitingSends.Remove(chatId);
        return true;
    }

    /// <summary>
    /// Gives up on a chat's queued messages wherever its deferred sends are given up on (an error, a lost
    /// session): each enters the transcript marked "not delivered", so its text is never lost.
    /// </summary>
    private void FailWaitingSends(Guid chatId)
    {
        if (!_waitingSends.Remove(chatId, out var waiting))
            return;

        if (_dataStore.Data.Chats.Find(chat => chat.Id == chatId) is { } owner)
        {
            foreach (var message in waiting)
            {
                EnterTranscript(owner, message);
                MarkQueuedBusySendFailed(message);
            }
            _ = PersistTransferredRemoteSendsAsync(owner);
        }

        SyncQueuedSends();
    }

    /// <summary>Drops a deleted chat's queued messages without writing them into it.</summary>
    private void DiscardWaitingSends(Guid chatId)
    {
        if (_waitingSends.Remove(chatId))
            SyncQueuedSends();
    }

    /// <summary>
    /// A paused chat holds its sends in the transcript, where they survive a restart; queued messages join
    /// that hold after the sends already deferred, the order they would have been sent in.
    /// </summary>
    private void HoldWaitingSendsForPause(Chat chat)
    {
        if (!_waitingSends.Remove(chat.Id, out var waiting))
            return;

        if (!_queuedBusySendPrompts.TryGetValue(chat.Id, out var deferred))
            _queuedBusySendPrompts[chat.Id] = deferred = [];

        foreach (var message in waiting)
        {
            EnterTranscript(chat, message);
            deferred.Add(message);
        }
    }

    internal void RemoveQueuedSend(ChatMessage message)
    {
        if (CurrentChat is { } chat && RemoveWaitingSend(chat.Id, message))
            SyncQueuedSends();
    }

    internal async Task RemoveQueuedSendWithRemoteSaveAsync(ChatMessage message)
    {
        if (CurrentChat is { } chat)
            await RemoveWaitingSendByIdAsync(chat.Id, message.Id);
    }

    internal async Task EditQueuedSendWithRemoteSaveAsync(ChatMessage message)
    {
        if (IsEditingMessage || CurrentChat is not { } chat)
            return;
        await _remoteQueueGate.WaitAsync();
        try
        {
            if (CurrentChat?.Id != chat.Id || FindWaitingSend(chat.Id, message.Id) is null)
                return;
            var saved = chat.RemoteQueuedSends?.Any(candidate => candidate.Id == message.Id) == true;
            EditQueuedSend(message);
            if (saved)
                await SaveRemoteQueueAsync(chat,
                    chat.RemoteQueuedSends?.Where(candidate => candidate.Id != message.Id).ToList());
        }
        finally
        {
            _remoteQueueGate.Release();
        }
    }

    internal async Task RunQueuedSendActionAsync(Func<Task> action)
    {
        var chatId = CurrentChat?.Id;
        try
        {
            await action();
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError($"[Remote queue] Queue action failed: {error}");
            if (CurrentChat?.Id == chatId)
                StatusText = error.Message;
        }
    }

    /// <summary>
    /// Takes a queued message back into the composer to change it. Whatever the composer already held is
    /// kept after it, so nothing typed is lost; sending again steers or queues it as the user chooses.
    /// </summary>
    internal void EditQueuedSend(ChatMessage message)
    {
        if (IsEditingMessage || CurrentChat is not { } chat || !RemoveWaitingSend(chat.Id, message))
            return;

        PromptText = string.IsNullOrWhiteSpace(PromptText)
            ? message.Content
            : message.Content + Environment.NewLine + Environment.NewLine + PromptText;
        foreach (var path in message.Attachments)
            AddAttachment(path);
        PendingReply ??= message.ReplyTo;
        if (message.ReferencedChats is { Count: > 0 } references)
            ReplaceReferencedChats(references.Concat(PeekReferencedChats() ?? []).DistinctBy(static reference => reference.ChatId));

        SyncQueuedSends();
        FocusComposerAtEndRequested?.Invoke();
    }

    /// <summary>Steers a queued message into the running reply now, instead of waiting for it to finish.</summary>
    internal async Task<bool> SteerQueuedSendNowAsync(ChatMessage message)
    {
        if (CurrentChat is not { } chat || !await MoveWaitingSendToTranscriptAsync(chat, message))
            return false;

        if (CurrentChat?.Id != chat.Id)
        {
            QueueBusySendPrompt(chat.Id, message.Content, message);
            ScheduleQueuedBusySendDrain(chat.Id);
            return true;
        }

        if (chat.IsPaused || !IsChatRuntimeActive(chat.Id) || CanStartTurnOnReadySession(chat))
        {
            // The reply already finished: send it next, as it was about to be.
            QueueBusySendPrompt(chat.Id, message.Content, message);
            ScheduleQueuedBusySendDrain(chat.Id);
            return true;
        }

        return await SteerActiveTurnAsync(chat, message.Content, consumeComposerPrompt: false, message);
    }

    /// <summary>The queued message with this id, while it still waits here.</summary>
    internal ChatMessage? FindWaitingSend(Guid chatId, Guid messageId)
        => _waitingSends.TryGetValue(chatId, out var waiting)
            ? waiting.Find(message => message.Id == messageId)
            : null;

    /// <summary>Drops a queued message another device chose to remove.</summary>
    internal async Task<bool> RemoveWaitingSendByIdAsync(Guid chatId, Guid messageId)
    {
        await _remoteQueueGate.WaitAsync();
        try
        {
            if (FindWaitingSend(chatId, messageId) is not { } message)
                return false;
            var chat = _dataStore.Data.Chats.Find(candidate => candidate.Id == chatId);
            var position = _waitingSends[chatId].IndexOf(message);
            if (!RemoveWaitingSend(chatId, message))
                return false;
            try
            {
                if (chat?.RemoteQueuedSends?.Any(candidate => candidate.Id == messageId) == true)
                    await SaveRemoteQueueAsync(chat,
                        chat.RemoteQueuedSends.Where(candidate => candidate.Id != messageId).ToList());
            }
            catch
            {
                if (!_waitingSends.TryGetValue(chatId, out var waiting))
                    _waitingSends[chatId] = waiting = [];
                waiting.Insert(Math.Min(position, waiting.Count), message);
                SyncQueuedSends();
                throw;
            }
            SyncQueuedSends();
            return true;
        }
        finally
        {
            _remoteQueueGate.Release();
        }
    }

    private async Task PersistTransferredRemoteSendsAsync(Chat chat)
    {
        if (chat.RemoteQueuedSends is not { Count: > 0 })
            return;
        await _remoteQueueGate.WaitAsync();
        try
        {
            var remaining = chat.RemoteQueuedSends?
                .Where(message => !chat.Messages.Any(candidate => candidate.Id == message.Id)).ToList();
            if (remaining?.Count != chat.RemoteQueuedSends?.Count)
                await SaveRemoteQueueAsync(chat, remaining);
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError($"[Remote queue] Saving transferred messages for {chat.Id} failed: {error}");
            if (CurrentChat?.Id == chat.Id)
                StatusText = error.Message;
        }
        finally
        {
            _remoteQueueGate.Release();
        }
    }

    /// <summary>Steers a queued message another device chose to send now; the chat must be open here.</summary>
    internal async Task<bool> SteerWaitingSendNowAsync(Chat chat, Guid messageId)
    {
        if (CurrentChat?.Id != chat.Id || FindWaitingSend(chat.Id, messageId) is not { } message)
            return false;

        return await SteerQueuedSendNowAsync(message);
    }

    /// <summary>Shows the current chat's queued messages, updating the list in place.</summary>
    private void SyncQueuedSends()
    {
        IReadOnlyList<ChatMessage> waiting = CurrentChat is { } chat && _waitingSends.TryGetValue(chat.Id, out var list)
            ? list
            : [];

        for (var i = QueuedSends.Count - 1; i >= 0; i--)
        {
            if (!waiting.Contains(QueuedSends[i].Message))
                QueuedSends.RemoveAt(i);
        }

        for (var i = 0; i < waiting.Count; i++)
        {
            if (i < QueuedSends.Count && ReferenceEquals(QueuedSends[i].Message, waiting[i]))
                continue;

            QueuedSends.Insert(i, new QueuedSendItem(waiting[i], this));
        }

        for (var i = 0; i < QueuedSends.Count; i++)
            QueuedSends[i].Position = i + 1;

        OnPropertyChanged(nameof(HasQueuedSends));
    }
}
