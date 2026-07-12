using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;

using ChatMessage = Lumi.Models.ChatMessage;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    // UI-thread only. Entries remain pending until the agent consumes the steer or the turn terminates.
    private readonly Dictionary<Guid, List<ChatMessageViewModel>> _pendingSteerConfirmations = new();

    private async Task SteerActiveTurnAsync(Chat activeChat, string prompt, bool consumeComposerPrompt)
    {
        var chatId = activeChat.Id;

        if (!_sessionCache.TryGetValue(chatId, out var session))
            session = _activeSession;

        _runtimeStates.TryGetValue(chatId, out var runtime);
        if (session is null || runtime is null || !CanSteerImmediately(runtime))
        {
            QueueBusySendPrompt(chatId, prompt);
            if (consumeComposerPrompt)
            {
                PromptText = "";
                _chatDrafts.Remove(chatId);
            }

            return;
        }

        var attachments = TakePendingAttachments();
        var userMsg = new ChatMessage
        {
            Role = "user",
            Content = prompt,
            Author = _dataStore.Data.Settings.UserName ?? Loc.Author_You,
            Attachments = attachments?.OfType<AttachmentFile>().Select(a => a.Path).ToList() ?? [],
            ActiveSkills = BuildSkillReferences(ActiveSkillIds)
        };

        if (WorktreePath is { Length: > 0 } worktreePath && attachments is { Count: > 0 })
        {
            var projectDirectory = GetProjectWorkingDirectory();
            var effectiveWorktreeDirectory =
                GitService.ResolveWorktreeWorkingDirectory(worktreePath, projectDirectory);
            RebaseAttachmentPaths(
                attachments,
                userMsg,
                projectDirectory,
                effectiveWorktreeDirectory);
        }

        activeChat.Messages.Add(userMsg);
        var messageViewModel = new ChatMessageViewModel(userMsg)
        {
            SteerState = MessageSteerState.Steering
        };
        Messages.Add(messageViewModel);

        // Register before SendAsync so a consumption event cannot race ahead of the pending entry.
        RegisterPendingSteer(chatId, messageViewModel);
        QueueSaveChat(activeChat, saveIndex: true, touchIndex: true);
        ChatUpdated?.Invoke();
        UserMessageSent?.Invoke();

        if (consumeComposerPrompt)
        {
            PromptText = "";
            _chatDrafts.Remove(chatId);
        }
        ClearSuggestions();

        var sendOptions = new MessageOptions
        {
            Prompt = prompt + BuildSendPromptAdditions(),
            Mode = GitHub.Copilot.Rpc.SendMode.Immediate.Value
        };
        if (attachments is { Count: > 0 })
            sendOptions.Attachments = attachments;

        var token = _ctsSources.TryGetValue(chatId, out var cts)
            ? cts.Token
            : CancellationToken.None;

        try
        {
            // SendAsync confirms queue acceptance; the event stream confirms actual consumption.
            await session.SendAsync(sendOptions, token);
        }
        catch (Exception ex)
        {
            UnregisterPendingSteer(chatId, messageViewModel);
            if (messageViewModel.SteerState == MessageSteerState.Steering)
                messageViewModel.SteerState = MessageSteerState.Failed;

            Debug.WriteLine($"[Steer] Immediate send failed for chat {chatId}: {ex.Message}");
        }
    }

    private void RegisterPendingSteer(Guid chatId, ChatMessageViewModel message)
    {
        if (!_pendingSteerConfirmations.TryGetValue(chatId, out var pendingMessages))
        {
            pendingMessages = [];
            _pendingSteerConfirmations[chatId] = pendingMessages;
        }

        pendingMessages.Add(message);
    }

    private void UnregisterPendingSteer(Guid chatId, ChatMessageViewModel message)
    {
        if (!_pendingSteerConfirmations.TryGetValue(chatId, out var pendingMessages))
            return;

        pendingMessages.Remove(message);
        if (pendingMessages.Count == 0)
            _pendingSteerConfirmations.Remove(chatId);
    }

    private void ConfirmOldestPendingSteer(Guid chatId)
    {
        if (!_pendingSteerConfirmations.TryGetValue(chatId, out var pendingMessages)
            || pendingMessages.Count == 0)
        {
            return;
        }

        var message = pendingMessages[0];
        pendingMessages.RemoveAt(0);
        if (pendingMessages.Count == 0)
            _pendingSteerConfirmations.Remove(chatId);

        if (message.SteerState == MessageSteerState.Steering)
            message.SteerState = MessageSteerState.Steered;
    }

    private void ResolvePendingSteersAsDelivered(Guid chatId)
        => ResolvePendingSteers(chatId, MessageSteerState.Steered);

    private void ResolvePendingSteersAsFailed(Guid chatId)
        => ResolvePendingSteers(chatId, MessageSteerState.Failed);

    private void ResolvePendingSteers(Guid chatId, MessageSteerState resolvedState)
    {
        if (!_pendingSteerConfirmations.Remove(chatId, out var pendingMessages))
            return;

        foreach (var message in pendingMessages)
        {
            if (message.SteerState == MessageSteerState.Steering)
                message.SteerState = resolvedState;
        }
    }

    private static bool CanSteerImmediately(ChatRuntimeState runtime)
        => runtime.TurnInProgress
           || runtime.ActiveToolCount > 0
           || runtime.ActiveSubagentExecutionDepth > 0;

    private async Task SendSteeredNowAsync(ChatMessageViewModel message)
    {
        if (message.SteerState != MessageSteerState.Steering)
            return;

        if (CurrentChat is not { } chat
            || !chat.Messages.Contains(message.Message)
            || !IsChatRuntimeActive(chat.Id))
        {
            return;
        }

        await StopGenerationInternal(resolvePendingSteersAsFailed: false);
    }
}
