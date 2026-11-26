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

    private bool HasWaitingSends(Guid chatId)
        => _waitingSends.TryGetValue(chatId, out var waiting) && waiting.Count > 0;

    private bool HasPendingSends(Guid chatId)
        => _queuedBusySendPrompts.ContainsKey(chatId) || HasWaitingSends(chatId);

    /// <summary>Takes the oldest queued message and shows it at the end of the transcript, ready to send.</summary>
    private bool TryTakeWaitingSend(Chat? chat, out ChatMessage message)
    {
        message = null!;
        if (chat is null || !_waitingSends.TryGetValue(chat.Id, out var waiting) || waiting.Count == 0)
            return false;

        message = waiting[0];
        RemoveWaitingSend(chat.Id, message);
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
        if (CurrentChat is not { } chat || !RemoveWaitingSend(chat.Id, message))
            return;

        EnterTranscript(chat, message);

        if (chat.IsPaused || !IsChatRuntimeActive(chat.Id) || CanStartTurnOnReadySession(chat))
        {
            // The reply already finished: send it next, as it was about to be.
            QueueBusySendPrompt(chat.Id, message.Content, message);
            ScheduleQueuedBusySendDrain(chat.Id);
            return;
        }

        await SteerActiveTurnAsync(chat, message.Content, consumeComposerPrompt: false, message);
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
