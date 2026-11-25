using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Remote.Protocol;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.ViewModels;

/// <summary>A message the PC holds to send once the reply finishes, as a row above the phone's composer.</summary>
public sealed partial class QueuedMessageItemViewModel : ObservableObject
{
    private readonly MobileChatViewModel _owner;

    internal QueuedMessageItemViewModel(MobileChatViewModel owner, Guid id)
    {
        _owner = owner;
        Id = id;
    }

    public Guid Id { get; }

    [ObservableProperty] private int _position;

    [ObservableProperty] private string _text = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachments))]
    private int _attachmentCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    private bool _canEdit;

    /// <summary>The PC is still answering an action on this message.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SteerNowCommand), nameof(EditCommand), nameof(RemoveCommand))]
    private bool _isPending;

    public bool HasAttachments => AttachmentCount > 0;

    /// <summary>The PC's last status still lists this message.</summary>
    internal bool IsListed { get; set; } = true;

    private bool CanAct() => !IsPending;

    private bool CanEditNow() => CanEdit && !IsPending;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task SteerNowAsync() => _owner.SteerQueuedMessageAsync(this);

    [RelayCommand(CanExecute = nameof(CanEditNow))]
    private Task EditAsync() => _owner.EditQueuedMessageAsync(this);

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task RemoveAsync() => _owner.RemoveQueuedMessageAsync(this);
}

public sealed partial class MobileChatViewModel
{
    // A request is sent again with its id only after a failure that may have reached the PC.
    private readonly Dictionary<(Guid ChatId, Guid MessageId, string Action), string> _queuedActionRequestIds = [];

    /// <summary>The messages the PC holds for after this reply, in the order it will send them.</summary>
    public ObservableCollection<QueuedMessageItemViewModel> QueuedMessages { get; } = [];

    public bool HasQueuedMessageItems => QueuedMessages.Count > 0;

    public string QueuedMessagesCountText => QueuedMessages.Count.ToString(CultureInfo.CurrentCulture);

    /// <summary>Edit put a queued message back in the composer, so the caret should follow it.</summary>
    public event Action? ComposerFocusRequested;

    /// <summary>
    /// A queue request the PC turned down put its text back ahead of the draft, so typing should go on
    /// after it, without focusing the composer.
    /// </summary>
    public event Action? ComposerCaretToEndRequested;

    private void OnQueuedMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasQueuedMessageItems));
        OnPropertyChanged(nameof(QueuedMessagesCountText));
    }

    /// <summary>
    /// Matches the rows to the PC's queue in place, so a row that stays keeps its state. A row
    /// whose Edit may have reached the PC stays until that Edit is answered, because the PC then
    /// holds its text only in the answer, which tapping Edit again fetches.
    /// </summary>
    private void SyncQueuedMessages(IEnumerable<RemoteQueuedMessage>? queued)
    {
        var messages = (queued ?? []).DistinctBy(message => message.Id).ToList();
        var ids = messages.Select(message => message.Id).ToHashSet();
        for (var index = QueuedMessages.Count - 1; index >= 0; index--)
        {
            var item = QueuedMessages[index];
            item.IsListed = ids.Contains(item.Id);
            if (!item.IsListed && !HasUnansweredEdit(item.Id))
                QueuedMessages.RemoveAt(index);
        }

        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var existing = -1;
            for (var search = index; search < QueuedMessages.Count; search++)
            {
                if (QueuedMessages[search].Id == message.Id)
                {
                    existing = search;
                    break;
                }
            }

            QueuedMessageItemViewModel item;
            if (existing < 0)
            {
                item = new QueuedMessageItemViewModel(this, message.Id);
                QueuedMessages.Insert(index, item);
            }
            else
            {
                item = QueuedMessages[existing];
                if (existing != index)
                    QueuedMessages.Move(existing, index);
            }

            item.IsListed = true;
            item.Text = message.Text;
            item.AttachmentCount = message.AttachmentCount;
            item.CanEdit = message.CanEdit;
        }

        RenumberQueuedMessages();
        foreach (var key in _queuedActionRequestIds.Keys
                     .Where(key => key.ChatId == ChatId && !QueuedMessages.Any(item => item.Id == key.MessageId))
                     .ToList())
        {
            _queuedActionRequestIds.Remove(key);
        }
    }

    private bool HasUnansweredEdit(Guid messageId) =>
        _queuedActionRequestIds.ContainsKey((ChatId, messageId, RemoteProtocol.Actions.EditQueuedMessage));

    private void RenumberQueuedMessages()
    {
        for (var index = 0; index < QueuedMessages.Count; index++)
            QueuedMessages[index].Position = index + 1;
    }

    internal async Task SteerQueuedMessageAsync(QueuedMessageItemViewModel item)
    {
        if (await RunQueuedMessageActionAsync(item, RemoteProtocol.Actions.SteerQueuedMessage, "SteerFailed") is not null)
            ForgetQueuedMessage(item);
    }

    internal async Task RemoveQueuedMessageAsync(QueuedMessageItemViewModel item)
    {
        if (await RunQueuedMessageActionAsync(item, RemoteProtocol.Actions.RemoveQueuedMessage, "QueuedRemoveFailed") is not null)
            ForgetQueuedMessage(item);
    }

    internal async Task EditQueuedMessageAsync(QueuedMessageItemViewModel item)
    {
        var surface = CurrentSurface;
        if (await RunQueuedMessageActionAsync(item, RemoteProtocol.Actions.EditQueuedMessage, "MessageEditFailed") is not { } result)
            return;

        ForgetQueuedMessage(item);
        if (!string.IsNullOrEmpty(result.Text))
            ReturnQueuedMessageToComposer(surface, result.Text);
    }

    /// <summary>
    /// Sends one action on a queued message and returns the PC's answer when it succeeded, even if
    /// the user has opened another chat meanwhile. A failure is shown in the chat it was for.
    /// </summary>
    private async Task<RemoteCommandResult?> RunQueuedMessageActionAsync(
        QueuedMessageItemViewModel item,
        string action,
        string failureKey)
    {
        if (item.IsPending || ChatId == Guid.Empty || !QueuedMessages.Contains(item))
            return null;

        var surface = CurrentSurface;
        var activation = _surfaceActivationGeneration;
        var host = Volatile.Read(ref _hostGeneration);
        var key = (surface.ChatId, item.Id, action);
        if (!_queuedActionRequestIds.TryGetValue(key, out var requestId))
            _queuedActionRequestIds[key] = requestId = Guid.NewGuid().ToString("N");

        item.IsPending = true;
        RemoteCommandResult result;
        try
        {
            result = await _sink.SendCommandAsync(new RemoteCommand(action)
            {
                RequestId = requestId
            }.With("chatId", surface.ChatId.ToString()).With("messageId", item.Id.ToString()));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Mobile] Queued message action {action} failed: {ex}");
            result = new RemoteCommandResult { Error = Strings.Get(failureKey), IsOutcomeUnknown = true };
        }

        item.IsPending = false;
        if (!IsCurrentHost(host))
            return null;

        if (!IsAmbiguousFailure(result))
        {
            _queuedActionRequestIds.Remove(key);
            // A row kept only for an unanswered Edit goes once the PC has answered.
            if (!result.Ok && !item.IsListed)
                ForgetQueuedMessage(item);
        }

        if (IsCurrentSurfaceActivation(surface, activation))
            ErrorText = result.Ok ? null : result.Error ?? Strings.Get(failureKey);
        return result.Ok ? result : null;
    }

    /// <summary>Takes a row out as soon as the PC answers; its next status says the same.</summary>
    private void ForgetQueuedMessage(QueuedMessageItemViewModel item)
    {
        if (QueuedMessages.Remove(item))
            RenumberQueuedMessages();
    }

    /// <summary>
    /// Puts a queued message back in front of the draft, with its files, as Edit does on the PC.
    /// A chat the user has left meanwhile keeps it in that chat's draft.
    /// </summary>
    private void ReturnQueuedMessageToComposer(ChatSurfaceIdentity surface, string content)
    {
        var (text, files) = RemoteUserMessageContent.Parse(content, "Lumi Mobile");
        var returned = files.Select(file => new PendingAttachment(file.FileName, file.Path)).ToList();
        surface = ResolveSurface(surface);

        if (IsCurrentSurface(surface))
        {
            PromptText = PrependReturnedText(text, PromptText);
            foreach (var attachment in returned)
            {
                if (!Attachments.Contains(attachment))
                    Attachments.Add(attachment);
            }

            ClearPendingRetryIfPayloadChanged();
            OnPropertyChanged(nameof(HasAttachments));
            NotifySendCommandsCanExecuteChanged();
            StoreDraft(surface, CaptureCurrentDraft());
            ComposerFocusRequested?.Invoke();
            return;
        }

        var draft = GetDraft(surface);
        var prompt = PrependReturnedText(text, draft.PromptText);
        var attachments = draft.Attachments.ToList();
        foreach (var attachment in returned)
        {
            if (!attachments.Contains(attachment))
                attachments.Add(attachment);
        }

        var retry = draft.PendingRetry;
        if (retry is not null && !retry.Payload.Matches(prompt, attachments))
            retry = retry with { PayloadEdited = true };

        StoreDraft(surface, draft with
        {
            PromptText = prompt,
            Attachments = [.. attachments],
            PendingRetry = retry
        });
    }

    private static string PrependReturnedText(string returned, string draft) =>
        string.IsNullOrWhiteSpace(draft) ? returned
        : string.IsNullOrWhiteSpace(returned) || string.Equals(returned, draft, StringComparison.Ordinal) ? draft
        : returned + "\n\n" + draft;
}
