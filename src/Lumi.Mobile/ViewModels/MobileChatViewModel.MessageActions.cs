using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Remote.Protocol;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.ViewModels;

public sealed partial class MobileChatViewModel
{
    private long _messageActionsVersion;
    private string? _messageEditRequestId;
    private string? _submittedMessageEditText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUserMessageSelected), nameof(SelectedMessageText),
        nameof(HasSelectedMessageText), nameof(CanEditSelectedMessage),
        nameof(MessageEditUnavailableReason), nameof(HasMessageEditUnavailableReason))]
    private TranscriptItemViewModel? _selectedMessage;

    /// <summary>A message's actions are in use: its menu, or the editor its Edit opened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSheet), nameof(IsMessageMenuOpen), nameof(IsMessageEditorOpen))]
    [NotifyCanExecuteChangedFor(nameof(SaveMessageEditCommand))]
    private bool _isMessageActionsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMessageMenuOpen), nameof(IsMessageEditorOpen))]
    [NotifyCanExecuteChangedFor(nameof(SaveMessageEditCommand))]
    private bool _isEditingMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveMessageEditCommand))]
    private string _editedMessageText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSelectedMessage))]
    [NotifyCanExecuteChangedFor(nameof(SaveMessageEditCommand))]
    private bool _isSavingMessageEdit;

    [ObservableProperty] private string? _messageActionError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSheet))]
    private bool _isMessageSelectionOpen;

    [ObservableProperty] private string _messageSelectionText = "";

    public bool IsUserMessageSelected => SelectedMessage is UserTurnItemViewModel;

    public string SelectedMessageText => SelectedMessage switch
    {
        UserTurnItemViewModel user => Strings.LocalizeMobileTruncation(user.Text) ?? "",
        AssistantItemViewModel assistant => assistant.SelectionText,
        _ => ""
    };

    public bool HasSelectedMessageText => !string.IsNullOrWhiteSpace(SelectedMessageText);

    /// <summary>The floating menu over the pressed message.</summary>
    public bool IsMessageMenuOpen => IsMessageActionsOpen && !IsEditingMessage;

    /// <summary>The editor sheet; dismissing it ends the message's actions.</summary>
    public bool IsMessageEditorOpen
    {
        get => IsMessageActionsOpen && IsEditingMessage;
        set
        {
            if (!value && IsMessageEditorOpen)
                CloseMessageActions();
        }
    }

    public bool HasMessageEditUnavailableReason => MessageEditUnavailableReason is not null;

    public bool CanEditSelectedMessage => SelectedMessage is UserTurnItemViewModel user
        && _sink.SupportsMessageEditing
        && Guid.TryParse(user.Id, out _)
        && !user.Text.Contains(RemoteProtocol.MobileTruncationMarker, StringComparison.Ordinal)
        && !IsLoading && !IsSavingMessageEdit;

    public string? MessageEditUnavailableReason => SelectedMessage is not UserTurnItemViewModel user ? null
        : !_sink.SupportsMessageEditing ? Strings.Get("MessageEditUnavailable")
        : user.Text.Contains(RemoteProtocol.MobileTruncationMarker, StringComparison.Ordinal)
            ? Strings.Get("MessageEditTruncated")
        : !Guid.TryParse(user.Id, out _) ? Strings.Get("MessageEditPending")
        : null;

    [RelayCommand]
    private void OpenMessageActions(TranscriptItemViewModel item)
    {
        if (item is not (UserTurnItemViewModel or AssistantItemViewModel) || HasOpenSheet
            || !Turns.Any(turn => turn.Items.Contains(item)))
            return;

        SelectedMessage = item;
        MessageActionError = null;
        IsMessageActionsOpen = true;
    }

    [RelayCommand]
    private void CloseMessageActions() => IsMessageActionsOpen = false;

    [RelayCommand]
    private void EditSelectedMessage()
    {
        if (!CanEditSelectedMessage)
            return;
        EditedMessageText = SelectedMessageText;
        MessageActionError = null;
        IsEditingMessage = true;
    }

    [RelayCommand]
    private void CancelMessageEdit() => CloseMessageActions();

    private bool CanSaveMessageEdit() => IsMessageActionsOpen && IsEditingMessage
        && CanEditSelectedMessage && EditedMessageText.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSaveMessageEdit))]
    private async Task SaveMessageEditAsync()
    {
        if (!CanSaveMessageEdit() || SelectedMessage is not UserTurnItemViewModel user)
            return;

        var chatId = ChatId;
        var host = Volatile.Read(ref _hostGeneration);
        var activation = _surfaceActivationGeneration;
        var version = _messageActionsVersion;
        var text = EditedMessageText.Trim();
        var requestId = _submittedMessageEditText == text
            ? _messageEditRequestId ?? Guid.NewGuid().ToString("N")
            : Guid.NewGuid().ToString("N");
        _messageEditRequestId = requestId;
        _submittedMessageEditText = text;
        IsSavingMessageEdit = true;
        MessageActionError = null;

        RemoteCommandResult result;
        try
        {
            result = await _sink.SendCommandAsync(new RemoteCommand(RemoteProtocol.Actions.EditMessage)
            {
                RequestId = requestId
            }.With("chatId", chatId.ToString()).With("messageId", user.Id).With("message", text));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Mobile] Message edit failed: {ex}");
            result = new RemoteCommandResult { Error = Strings.Get("MessageEditFailed"), IsOutcomeUnknown = true };
        }

        if (!IsCurrentHost(host) || _surfaceActivationGeneration != activation
            || version != _messageActionsVersion || ChatId != chatId)
            return;

        IsSavingMessageEdit = false;
        if (!result.Ok)
        {
            if (!IsAmbiguousFailure(result))
                _messageEditRequestId = null;
            MessageActionError = result.Error ?? Strings.Get("MessageEditFailed");
            return;
        }

        CloseMessageActions();
        ChatActivitySubmitted?.Invoke(chatId, text);
        MessageEditAccepted?.Invoke(chatId);
    }

    internal void OpenMessageSelection(string text)
    {
        CloseMessageActions();
        MessageSelectionText = text;
        IsMessageSelectionOpen = true;
    }

    [RelayCommand]
    private void CloseMessageSelection() => IsMessageSelectionOpen = false;

    partial void OnIsMessageActionsOpenChanged(bool value)
    {
        if (value)
            return;
        _messageActionsVersion++;
        IsEditingMessage = false;
        IsSavingMessageEdit = false;
        EditedMessageText = "";
        MessageActionError = null;
        SelectedMessage = null;
        _messageEditRequestId = null;
        _submittedMessageEditText = null;
    }

    partial void OnIsMessageSelectionOpenChanged(bool value)
    {
        if (!value)
            MessageSelectionText = "";
    }

    private void ResetMessageActions()
    {
        CloseMessageActions();
        CloseMessageSelection();
    }

    public event Action<Guid>? MessageEditAccepted;
}
