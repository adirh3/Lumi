using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;

using ChatMessage = Lumi.Models.ChatMessage;

namespace Lumi.ViewModels;

/// <summary>
/// "Reply to": quoting an earlier assistant message — or just the part of it the user selected — in
/// the next message. The pending reply is composer state like the draft text: it is kept per chat,
/// hydrated when a reply is edited, and consumed by whichever send path creates the next message
/// from the composer.
/// </summary>
public partial class ChatViewModel
{
    // Pending replies of chats the user switched away from, restored when they come back.
    private readonly Dictionary<Guid, MessageReply> _chatReplyDrafts = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingReply))]
    [NotifyPropertyChangedFor(nameof(PendingReplyTitle))]
    [NotifyPropertyChangedFor(nameof(PendingReplyPreview))]
    [NotifyPropertyChangedFor(nameof(ComposerPlaceholder))]
    private MessageReply? _pendingReply;

    public bool HasPendingReply => PendingReply is not null;

    public string PendingReplyTitle => string.Format(
        Loc.Chat_ReplyingTo,
        PendingReply?.Author ?? Loc.Author_Lumi);

    public string PendingReplyPreview => MessageReplyFormatter.BuildPreview(PendingReply);

    /// <summary>Raised to scroll the transcript to a reply's source message and highlight the quote.</summary>
    public event Action<MessageReply>? ReplySourceJumpRequested;

    /// <summary>
    /// Starts replying to an assistant message: the whole message, or <paramref name="text"/> when it
    /// is a selected excerpt. Keeps whatever the user already typed and moves focus to the composer.
    /// </summary>
    public void BeginReply(Guid messageId, string? text, bool isSelection, string? author)
    {
        if (CurrentChat is not { } chat)
            return;

        // Resending an edit drops everything after the edited message, so while editing only an
        // earlier answer can be quoted. Otherwise the edit in progress comes first, as it does when
        // the user tries to edit a second message.
        if (IsEditingMessage
            && _editingUserMessage is { } edited
            && !IsMessageBefore(chat, messageId, edited))
        {
            FocusComposerRequested?.Invoke();
            return;
        }

        var reply = MessageReplyFormatter.Create(messageId, text, isSelection, author);
        if (reply is null)
            return;

        PendingReply = reply;
        FocusComposerAtEndRequested?.Invoke();
    }

    private static bool IsMessageBefore(Chat chat, Guid messageId, ChatMessage laterMessage)
    {
        var index = chat.Messages.FindIndex(message => message.Id == messageId);
        return index >= 0 && index < chat.Messages.IndexOf(laterMessage);
    }

    [RelayCommand]
    private void CancelReply()
    {
        if (PendingReply is null)
            return;

        PendingReply = null;
        FocusComposerRequested?.Invoke();
    }

    [RelayCommand]
    private void JumpToPendingReplySource()
    {
        if (PendingReply is { } reply)
            ReplySourceJumpRequested?.Invoke(reply);
    }

    /// <summary>Invoked from a sent reply's quote to reveal what it was replying to.</summary>
    internal void RequestReplySourceJump(MessageReply reply) => ReplySourceJumpRequested?.Invoke(reply);

    /// <summary>
    /// Hands the composer's pending reply to a message being created from the composer of
    /// <paramref name="chatId"/>. The reply belongs to exactly one message, so it is cleared here.
    /// </summary>
    private MessageReply? TakePendingReply(Guid chatId)
    {
        if (CurrentChat?.Id != chatId || PendingReply is not { } reply)
            return null;

        PendingReply = null;
        return reply;
    }

    partial void OnPendingReplyChanged(MessageReply? value) => UpdateReplySourceHighlight();

    /// <summary>Keeps the replied-to message visibly marked while its reply is being composed.</summary>
    private void UpdateReplySourceHighlight()
    {
        var sourceId = PendingReply?.MessageId;
        foreach (var assistant in TranscriptTurns
                     .SelectMany(static turn => turn.Items)
                     .OfType<AssistantMessageItem>())
        {
            assistant.IsReplySource = sourceId == assistant.MessageId;
        }
    }

    partial void OnCurrentChatChanged(Chat? oldValue, Chat? newValue)
    {
        if (oldValue?.Id == newValue?.Id)
            return;

        if (oldValue is not null)
        {
            if (PendingReply is { } leavingReply)
                _chatReplyDrafts[oldValue.Id] = leavingReply;
            else
                _chatReplyDrafts.Remove(oldValue.Id);
        }

        PendingReply = newValue is not null && _chatReplyDrafts.Remove(newValue.Id, out var restored)
            ? restored
            : null;
    }

    /// <summary>The prompt the model receives for <paramref name="message"/>: its text, framed as a reply when it is one.</summary>
    private static string ComposeModelPrompt(string prompt, ChatMessage? message)
        => MessageReplyFormatter.ComposePrompt(prompt, message?.ReplyTo);
}
