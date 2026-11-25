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
        SteerNowCommand = new AsyncRelayCommand(() => owner.SteerQueuedSendNowAsync(message));
        EditCommand = new RelayCommand(() => owner.EditQueuedSend(message));
        RemoveCommand = new RelayCommand(() => owner.RemoveQueuedSend(message));
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

    /// <summary>The current chat's queued messages, oldest first.</summary>
    public ObservableCollection<QueuedSendItem> QueuedSends { get; } = [];

    public bool HasQueuedSends => QueuedSends.Count > 0;

    internal int GetWaitingSendCount(Guid chatId) =>
        _waitingSends.TryGetValue(chatId, out var waiting) ? waiting.Count : 0;

    internal bool ShouldQueueSend(Chat chat) =>
        !chat.IsPaused && (HasWaitingSends(chat.Id)
                           || IsChatRuntimeActive(chat.Id) && !CanStartTurnOnReadySession(chat));

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
        if (CurrentChat is not { } chat || !ShouldQueueSend(chat))
        {
            await SendMessageCore(prompt, consumeComposerPrompt: true);
            return;
        }

        if (BlockSendForByokOnly(chat, ResolveSelectedModelForChat(chat), prompt, consumeComposerPrompt: true))
            return;

        var message = BuildQueuedSend(chat, prompt);
        if (message is null)
            return;

        AddWaitingSend(chat.Id, message);
        PromptText = "";
        _chatDrafts.Remove(chat.Id);
        SyncQueuedSends();
    }

    /// <summary>
    /// Queues a message from another device. With a receipt, the queue is saved with the receipt before the
    /// message counts as accepted, so a restart keeps it and a retried request finds it already queued.
    /// </summary>
    internal async Task<bool> QueueExternalMessageAsync(
        Chat chat,
        string prompt,
        string author,
        string? remoteDeviceId = null,
        string? remoteRequestId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (BlockSendForByokOnly(chat, ResolveSelectedModelForChat(chat), prompt, consumeComposerPrompt: false)
            || BuildQueuedSend(chat, prompt, author, explicitAttachmentPaths: []) is not { } message)
        {
            return false;
        }

        message.RemoteRequestId = remoteRequestId;
        var saveRequired = true;
        if (!string.IsNullOrWhiteSpace(remoteDeviceId) && !string.IsNullOrWhiteSpace(remoteRequestId))
        {
            var previousDeviceId = chat.LastRemoteDeviceId;
            var previousRequestId = chat.LastRemoteRequestId;
            chat.LastRemoteDeviceId = remoteDeviceId;
            chat.LastRemoteRequestId = remoteRequestId;
            // Saved before it joins the queue, so a drain can never send a message whose receipt failed.
            var saved = chat.QueuedSends = [.. chat.QueuedSends ?? [], message.Clone()];
            try
            {
                _dataStore.MarkChatChanged(chat);
                await _dataStore.SaveChatAsync(chat, cancellationToken).ConfigureAwait(true);
                await _dataStore.SaveAsync(cancellationToken).ConfigureAwait(true);
            }
            catch
            {
                chat.LastRemoteDeviceId = previousDeviceId;
                chat.LastRemoteRequestId = previousRequestId;
                PersistWaitingSends(chat.Id, save: false);
                throw;
            }

            // Unless the queue changed while it was saving, what was saved is already the queue.
            saveRequired = !ReferenceEquals(chat.QueuedSends, saved);
        }

        AddWaitingSend(chat.Id, message, saveRequired);
        SyncQueuedSends();
        if (!IsChatRuntimeActive(chat.Id) || CanStartTurnOnReadySession(chat))
            ScheduleQueuedBusySendDrain(chat.Id);
        return true;
    }

    private void AddWaitingSend(Guid chatId, ChatMessage message, bool save = true)
    {
        if (!_waitingSends.TryGetValue(chatId, out var waiting))
            _waitingSends[chatId] = waiting = [];
        waiting.Add(message);
        PersistWaitingSends(chatId, save);
    }

    /// <summary>
    /// Keeps the chat's saved queue in step with the messages waiting here, as copies, so a restart or
    /// another window finds them in the same order with the same ids.
    /// </summary>
    private void PersistWaitingSends(Guid chatId, bool save)
    {
        var chat = CurrentChat?.Id == chatId
            ? CurrentChat
            : _dataStore.Data.Chats.Find(candidate => candidate.Id == chatId);
        if (chat is null)
            return;

        var waiting = _waitingSends.GetValueOrDefault(chatId);
        if (chat.QueuedSends is null && waiting is not { Count: > 0 })
            return;

        chat.QueuedSends = waiting is { Count: > 0 }
            ? waiting.ConvertAll(static message => message.Clone())
            : null;
        if (save)
            QueueSaveChat(chat, saveIndex: false);
    }

    /// <summary>Brings back the queue a chat saved, unless its messages are already waiting here.</summary>
    private void RestoreWaitingSends(Chat chat)
    {
        if (_waitingSends.ContainsKey(chat.Id) || chat.QueuedSends is not { Count: > 0 } saved)
            return;

        _waitingSends[chat.Id] = saved.ConvertAll(static message =>
        {
            var restored = message.Clone();
            restored.SteerDelivery = MessageSteerState.Queued;
            restored.CanSendNowWhenQueued = false;
            return restored;
        });
    }

    /// <summary>Sends a restored queue's first message when nothing is running for it to follow.</summary>
    private void ScheduleRestoredWaitingSends(Chat chat)
    {
        if (HasWaitingSends(chat.Id)
            && !chat.IsPaused
            && (!IsChatRuntimeActive(chat.Id) || CanStartTurnOnReadySession(chat)))
        {
            ScheduleQueuedBusySendDrain(chat.Id);
        }
    }

    private bool HasWaitingSends(Guid chatId)
        => GetWaitingSendCount(chatId) > 0;

    private bool HasPendingSends(Guid chatId)
        => _queuedBusySendPrompts.ContainsKey(chatId) || HasWaitingSends(chatId);

    /// <summary>Takes the oldest queued message and shows it at the end of the transcript, ready to send.</summary>
    private bool TryTakeWaitingSend(Chat? chat, out ChatMessage message)
    {
        message = null!;
        if (chat is null || !_waitingSends.TryGetValue(chat.Id, out var waiting) || waiting.Count == 0)
            return false;

        message = waiting[0];
        // Entering the transcript saves the chat, and with it the shorter queue.
        RemoveWaitingSend(chat.Id, message, save: false);
        EnterTranscript(chat, message);
        return true;
    }

    private void EnterTranscript(Chat chat, ChatMessage message)
    {
        message.Timestamp = DateTimeOffset.Now;
        message.SteerDelivery = MessageSteerState.Queued;
        message.CanSendNowWhenQueued = false;
        AddQueuedSendToTranscript(chat, message);
        SyncQueuedSends();
    }

    private bool RemoveWaitingSend(Guid chatId, ChatMessage message, bool save = true)
    {
        if (!_waitingSends.TryGetValue(chatId, out var waiting) || !waiting.Remove(message))
            return false;

        if (waiting.Count == 0)
            _waitingSends.Remove(chatId);
        PersistWaitingSends(chatId, save);
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

        // Each message entering the transcript saves the chat, and with it the emptied queue.
        PersistWaitingSends(chatId, save: false);
        if (_dataStore.Data.Chats.Find(chat => chat.Id == chatId) is { } owner)
        {
            foreach (var message in waiting)
            {
                EnterTranscript(owner, message);
                MarkQueuedBusySendFailed(message);
            }
        }

        SyncQueuedSends();
    }

    /// <summary>Drops a deleted chat's queued messages without writing them into it.</summary>
    private void DiscardWaitingSends(Guid chatId)
    {
        if (!_waitingSends.Remove(chatId))
            return;

        PersistWaitingSends(chatId, save: false);
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

        // They are saved again as held sends when they enter the transcript.
        PersistWaitingSends(chat.Id, save: false);
        if (!_queuedBusySendPrompts.TryGetValue(chat.Id, out var deferred))
            _queuedBusySendPrompts[chat.Id] = deferred = [];

        foreach (var message in waiting)
        {
            EnterTranscript(chat, message);
            deferred.Add(message);
        }
    }

    internal bool RemoveQueuedSend(ChatMessage message)
    {
        if (CurrentChat is not { } chat || !RemoveWaitingSend(chat.Id, message))
            return false;

        SyncQueuedSends();
        return true;
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
    internal async Task SteerQueuedSendNowAsync(ChatMessage message)
    {
        if (CurrentChat is { } chat)
            await SteerWaitingSendNowAsync(chat, message);
    }

    private async Task<bool> SteerWaitingSendNowAsync(Chat chat, ChatMessage message)
    {
        if (!RemoveWaitingSend(chat.Id, message, save: false))
            return false;

        EnterTranscript(chat, message);

        if (chat.IsPaused || !IsChatRuntimeActive(chat.Id) || CanStartTurnOnReadySession(chat))
        {
            // The reply already finished: send it next, as it was about to be.
            QueueBusySendPrompt(chat.Id, message.Content, message);
            ScheduleQueuedBusySendDrain(chat.Id);
            return true;
        }

        await SteerActiveTurnAsync(chat, message.Content, consumeComposerPrompt: false, message);
        return true;
    }

    /// <summary>
    /// Whether a phone can take a queued message back into its composer: only plain text it can hold. Files a
    /// phone attached travel in the text; files, a reply or chats picked on the PC would be lost.
    /// </summary>
    internal static bool CanEditQueuedSendRemotely(ChatMessage message)
        => message.Attachments.Count == 0
           && message.ReplyTo is null
           && message.ReferencedChats is not { Count: > 0 }
           && message.Content.Length <= Lumi.Remote.Protocol.RemoteProtocol.MobileUserTextLimit;

    /// <summary>The queued message with this id, while it still waits here.</summary>
    internal ChatMessage? FindWaitingSend(Guid chatId, Guid messageId)
        => _waitingSends.TryGetValue(chatId, out var waiting)
            ? waiting.Find(message => message.Id == messageId)
            : null;

    /// <summary>Drops a queued message another device chose to remove.</summary>
    internal bool RemoveWaitingSendById(Guid chatId, Guid messageId)
    {
        if (FindWaitingSend(chatId, messageId) is not { } message || !RemoveWaitingSend(chatId, message))
            return false;

        SyncQueuedSends();
        return true;
    }

    /// <summary>Steers a queued message another device chose to send now; the chat must be open here.</summary>
    internal Task<bool> SteerWaitingSendNowAsync(Chat chat, Guid messageId)
        => CurrentChat?.Id == chat.Id && FindWaitingSend(chat.Id, messageId) is { } message
            ? SteerWaitingSendNowAsync(chat, message)
            : Task.FromResult(false);

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
