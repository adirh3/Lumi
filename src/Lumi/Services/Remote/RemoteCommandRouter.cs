using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using StrataTheme.Controls;
using Lumi.Models;
using Lumi.Localization;
using Lumi.Remote.Protocol;
using Lumi.ViewModels;

namespace Lumi.Services.Remote;

/// <summary>
/// Executes remote commands against the live Lumi view models. Every handler runs on the UI thread
/// and drives the exact same commands and services the desktop UI uses, so a phone never takes a
/// second code path that could drift from the desktop behaviour.
/// </summary>
internal sealed class RemoteCommandRouter
{
    private readonly record struct ProjectSelection(bool IsSpecified, Guid? ProjectId);
    private readonly record struct AgentSelection(bool IsSpecified, LumiAgent? Agent, string? ExternalName);

    private readonly DataStore _dataStore;
    private readonly MainViewModel _main;
    private readonly Func<
        ChatViewModel,
        Chat,
        string,
        CancellationToken,
        string?,
        string?,
        Task<string?>>? _startExternalMessageAsync;

    /// <summary>
    /// Raised after a phone edit changes projects/skills/lumis/memories/MCPs/jobs, so the event hub
    /// can push the new library to every connected device. The desktop's own
    /// <c>FeatureManagementStateChanged</c> never fires for these, because the edit bypasses the
    /// agent tooling entirely.
    /// </summary>
    public RemoteCommandRouter(DataStore dataStore, MainViewModel main)
        : this(dataStore, main, null)
    {
    }

    internal RemoteCommandRouter(
        DataStore dataStore,
        MainViewModel main,
        Func<ChatViewModel, Chat, string, CancellationToken, string?, string?, Task<string?>>?
            startExternalMessageAsync)
    {
        _dataStore = dataStore;
        _main = main;
        _startExternalMessageAsync = startExternalMessageAsync;
    }

    public async Task<RemoteCommandResult> ExecuteAsync(RemoteCommand command, CancellationToken cancellationToken)
    {
        var action = command.Action.Trim().Replace('-', '_').ToLowerInvariant();
        return action switch
        {
            RemoteProtocol.Actions.CreateChat => await CreateChatAsync(command),
            RemoteProtocol.Actions.OpenChat => await MarkChatReadAsync(command),
            RemoteProtocol.Actions.DeleteChat => await DeleteChatAsync(command),
            RemoteProtocol.Actions.RenameChat => await RenameChatAsync(command),
            RemoteProtocol.Actions.PinChat => await PinChatAsync(command),
            RemoteProtocol.Actions.SendMessage => await SendMessageAsync(command, cancellationToken),
            RemoteProtocol.Actions.StopGeneration => await StopGenerationAsync(command),
            RemoteProtocol.Actions.AnswerQuestion => await AnswerQuestionAsync(command),
            RemoteProtocol.Actions.UpdateInteraction => await UpdateInteractionAsync(command, cancellationToken),
            RemoteProtocol.Actions.ConfigureChat => await ConfigureChatAsync(command),
            RemoteProtocol.Actions.ConfigureFeature => await ConfigureFeatureAsync(command),
            RemoteProtocol.Actions.PinModel => await PinModelAsync(command),
            _ => Fail(Loc.Get("Remote_UnknownAction", command.Action))
        };
    }

    private static RemoteCommandResult Fail(string error, Guid? chatId = null) =>
        new() { Ok = false, Error = error, ChatId = chatId };

    private static RemoteCommandResult Success(string message, Guid? chatId = null) =>
        new() { Ok = true, Message = message, ChatId = chatId };

    private Chat? ResolveChat(RemoteCommand command)
    {
        if (command.Arguments.TryGetValue("chatId", out var rawChatId))
        {
            return Guid.TryParse(rawChatId, out var id)
                ? _dataStore.Data.Chats.FirstOrDefault(chat => chat.Id == id)
                : null;
        }

        return _main.ChatVM.CurrentChat;
    }

    private Chat? ResolveExplicitChat(RemoteCommand command) =>
        command.Arguments.ContainsKey("chatId") ? ResolveChat(command) : null;

    /// <summary>
    /// Finds the surface that owns a chat without disturbing another window. Only chats with no
    /// registered owner are acquired into the main surface.
    /// </summary>
    private async Task<ChatViewModel?> ResolveChatOwnerAsync(Chat chat)
    {
        if (TryResolveRegisteredOwner(chat.Id) is { } existingOwner)
            return existingOwner;

        if (!await _main.OpenChatByIdAsync(chat.Id).ConfigureAwait(true))
            return null;

        return TryResolveRegisteredOwner(chat.Id)
               ?? (_main.ChatVM.CurrentChat?.Id == chat.Id ? _main.ChatVM : null);
    }

    private ChatViewModel? TryResolveRegisteredOwner(Guid chatId)
    {
        var displayingLiveOwner = _main.ChatSurfaceRegistry
            .SnapshotSurfaces()
            .FirstOrDefault(surface =>
                surface.CurrentChat?.Id == chatId && surface.OwnsLiveChat(chatId));
        if (displayingLiveOwner is not null)
            return displayingLiveOwner;

        if (_main.ChatSurfaceRegistry.TryGetOwner(chatId, out var owner))
            return owner;

        if (_main.ChatSurfaceRegistry.TryGetLiveOwner(chatId, out var liveOwner))
            return liveOwner;

        return null;
    }

    private async Task<RemoteCommandResult> CreateChatAsync(RemoteCommand command)
    {
        var now = DateTimeOffset.Now;
        var settings = _dataStore.Data.Settings;
        if (!TryResolveProjectSelection(command, out var projectSelection, out var projectError))
            return Fail(projectError);
        if (!TryResolveAgentSelection(command, out var agentSelection, out var agentError))
            return Fail(agentError);

        var model = command.Get("model") ?? settings.PreferredModel;

        var chat = new Chat
        {
            Title = command.Get("title") is { Length: > 0 } title ? title : Loc.Get("Sidebar_NewChat"),
            CreatedAt = now,
            UpdatedAt = now,
            ProjectId = projectSelection.ProjectId,
            AgentId = agentSelection.Agent?.Id,
            LastModelUsed = model,
            // Reconcile against the chosen model. Copying the setting verbatim handed the SDK an
            // effort the model may not accept — with "auto" (the common default) every first
            // message from the phone failed with "Reasoning effort is not supported".
            LastReasoningEffortUsed = _main.ChatVM.NormalizeReasoningEffortFor(model, settings.ReasoningEffort),
            LastContextWindowTierUsed =
                _main.ChatVM.NormalizeContextWindowTierFor(model, settings.ContextWindowTier)
        };

        _dataStore.Data.Chats.Add(chat);
        try
        {
            _dataStore.MarkChatChanged(chat);
            await _dataStore.SaveChatAsync(chat).ConfigureAwait(true);
            await _dataStore.SaveAsync().ConfigureAwait(true);
            _main.RefreshChatList();

            if (command.GetBool("open") ?? true)
                await _main.OpenChatByIdAsync(chat.Id).ConfigureAwait(true);

            return Success(Loc.Remote_ChatCreated, chat.Id);
        }

        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, chat.Id);
        }
    }

    private async Task<RemoteCommandResult> MarkChatReadAsync(RemoteCommand command)
    {
        if (ResolveExplicitChat(command) is not { } chat)
            return Fail(Loc.Remote_ChatNotFound);

        if (!chat.HasUnreadMessages)
            return Success(Loc.Remote_ChatAvailable, chat.Id);

        if (command.GetInt("readThroughMessageCount") is { } readThroughMessageCount &&
            RemoteProjector.GetEffectiveMessageCount(chat) > readThroughMessageCount)
        {
            return Success(Loc.Remote_NewerActivityUnread, chat.Id);
        }

        chat.HasUnreadMessages = false;
        _dataStore.MarkChatChanged(chat);
        await _dataStore.SaveAsync().ConfigureAwait(true);
        _main.RefreshChatList();
        return Success(Loc.Remote_ChatMarkedRead, chat.Id);
    }

    private async Task<RemoteCommandResult> DeleteChatAsync(RemoteCommand command)
    {
        if (!command.Arguments.ContainsKey("chatId"))
            return Fail(Loc.Get("Remote_ArgumentRequired", "chatId"));
        if (ResolveExplicitChat(command) is not { } chat)
            return Fail(Loc.Remote_ChatNotFound);
        if (_main.IsChatFirstTurnReserved(chat.Id))
            return Fail(Loc.Remote_FirstTurnDeleteBlocked, chat.Id);

        return await _main.DeleteChatKeepingWorktreeAsync(chat).ConfigureAwait(true)
            ? Success(Loc.Remote_ChatDeleted, chat.Id)
            : Fail(Loc.Remote_ChatDeleteFailed, chat.Id);
    }

    private async Task<RemoteCommandResult> RenameChatAsync(RemoteCommand command)
    {
        if (!command.Arguments.ContainsKey("chatId"))
            return Fail(Loc.Get("Remote_ArgumentRequired", "chatId"));
        if (ResolveExplicitChat(command) is not { } chat)
            return Fail(Loc.Remote_ChatNotFound);
        if (command.Get("title") is not { Length: > 0 } title)
            return Fail(Loc.Get("Remote_ArgumentRequired", "title"));

        chat.Title = title.Trim();
        chat.UpdatedAt = DateTimeOffset.Now;
        _dataStore.MarkChatChanged(chat);
        await _dataStore.SaveAsync().ConfigureAwait(true);
        _main.RefreshChatList();
        return Success(Loc.Remote_ChatRenamed, chat.Id);
    }

    private async Task<RemoteCommandResult> PinChatAsync(RemoteCommand command)
    {
        if (!command.Arguments.ContainsKey("chatId"))
            return Fail(Loc.Get("Remote_ArgumentRequired", "chatId"));
        if (ResolveExplicitChat(command) is not { } chat)
            return Fail(Loc.Remote_ChatNotFound);

        chat.IsPinned = command.GetBool("pinned") ?? !chat.IsPinned;
        _dataStore.MarkChatChanged(chat);
        await _dataStore.SaveAsync().ConfigureAwait(true);
        _main.RefreshChatList();
        return Success(chat.IsPinned ? Loc.Remote_ChatPinned : Loc.Remote_ChatUnpinned, chat.Id);
    }

    /// <summary>
    /// Pins or unpins a model in the picker. Pins are the app-wide favorites every picker on the PC
    /// lists first, so this is the desktop's own pin; its pickers read the pins again as they open.
    /// The phone states the pin it wants rather than toggling, so a repeated request is harmless.
    /// </summary>
    private async Task<RemoteCommandResult> PinModelAsync(RemoteCommand command)
    {
        if (command.Get("model") is not { Length: > 0 } model)
            return Fail(Loc.Get("Remote_ArgumentRequired", "model"));
        if (command.GetBool("pinned") is not { } pinned)
            return Fail(Loc.Get("Remote_ArgumentRequired", "pinned"));

        var favorites = _dataStore.Data.Settings.FavoriteModelIds ??= [];
        if (pinned)
        {
            if (!_main.ChatVM.AvailableModels.Contains(model, StringComparer.Ordinal))
                return Fail(Loc.Remote_ModelUnavailable);
            if (!favorites.Contains(model, StringComparer.Ordinal))
                favorites.Add(model);
        }
        else
        {
            favorites.RemoveAll(id => string.Equals(id, model, StringComparison.Ordinal));
        }

        await _dataStore.SaveAsync().ConfigureAwait(true);
        return Success(pinned ? Loc.Remote_ModelPinned : Loc.Remote_ModelUnpinned);
    }

    private async Task<RemoteCommandResult> SendMessageAsync(RemoteCommand command, CancellationToken cancellationToken, InteractionReceipt? interactionReceipt = null)
    {
        if (command.Get("message") is not { Length: > 0 } message)
            return Fail(Loc.Get("Remote_ArgumentRequired", "message"));

        if (command is
            {
                AuthenticatedDeviceId: { Length: > 0 } deviceId,
                RequestId: { Length: > 0 } requestId
            })
        {
            var acceptedChat = _dataStore.Data.Chats.FirstOrDefault(candidate =>
                string.Equals(candidate.LastRemoteDeviceId, deviceId, StringComparison.Ordinal)
                && string.Equals(candidate.LastRemoteRequestId, requestId, StringComparison.Ordinal));
            if (acceptedChat is not null)
                return Success(Loc.Remote_MessageAlreadyAccepted, acceptedChat.Id);
        }

        // A phone that defers chat creation has no id to send. Falling through to ResolveChat there
        // would resolve to "whichever chat the desktop currently has open" and post into an unrelated
        // conversation, so the intent is explicit rather than inferred from a missing field.
        var startsNewChat = command.GetBool("newChat") == true;
        var chat = startsNewChat ? null : ResolveChat(command);
        if (!startsNewChat && command.Arguments.ContainsKey("chatId") && chat is null)
            return Fail(Loc.Remote_ChatNotFound);

        var createdChat = false;
        if (chat is null)
        {
            var created = await CreateChatAsync(new RemoteCommand(RemoteProtocol.Actions.CreateChat)
                .With("title", command.Get("title"))
                .With("projectId", command.Get("projectId"))
                // Stable IDs are authoritative. Names remain a compatibility fallback for external
                // clients, but duplicate project labels must never decide where work runs.
                .With("projectName", command.Get("projectName") ?? command.Get("project"))
                .With("agentId", command.Get("agentId"))
                .With("model", command.Get("model"))
                .With("open", "true")).ConfigureAwait(true);
            if (!created.Ok || created.ChatId is not { } createdId)
                return created;
            chat = _dataStore.Data.Chats.First(candidate => candidate.Id == createdId);
            createdChat = true;
        }

        try
        {
            if (!TryResolveProjectSelection(command, out var projectSelection, out var projectError))
                return Fail(projectError, chat.Id);
            if (!TryResolveAgentSelection(command, out var agentSelection, out var agentError))
                return Fail(agentError, chat.Id);

            var owner = await ResolveChatOwnerAsync(chat).ConfigureAwait(true);
            if (owner is null)
                return Fail(Loc.Remote_ChatOpenFailed, chat.Id);
            if (owner.CurrentChat?.Id != chat.Id)
                return Fail(Loc.Remote_ChatSurfaceActivationFailed, chat.Id);

            if (chat.IsPaused)
                return Fail(Loc.Get("Chat_PausedSendBlocked"), chat.Id);

            if (owner.IsExternalSendReserved(chat.Id))
                return Fail(Loc.Remote_ChatAlreadyStartingTurn, chat.Id);

            if (command.GetBool("stopAndSend") == true && owner.IsChatBusy(chat.Id))
            {
                var stopAndSendAccepted = await owner
                    .StopAndSendExternalMessageAsync(
                        chat,
                        message,
                        "Lumi Mobile",
                        command.AuthenticatedDeviceId,
                        command.RequestId,
                        cancellationToken)
                    .ConfigureAwait(true);
                return stopAndSendAccepted
                    ? Success(Loc.Remote_TurnStoppedMessageQueued, chat.Id)
                    : Fail(Loc.Remote_StopAndSendFailed, chat.Id);
            }

            // Readiness chooses send versus steer, never whether to honor an explicit session Stop.
            if (owner.IsAssistantBusy(chat.Id))
            {
                if (command.GetBool("steer") != true)
                    return Fail(Loc.Remote_ChatAlreadyRunning, chat.Id);

                // Real steering injects into the live Copilot turn. Route through the surface that
                // owns that runtime; a detached chat must never steer the main window's surface.
                var accepted = await owner
                    .SteerExternalMessageAsync(chat, message, "Lumi Mobile")
                    .ConfigureAwait(true);
                return accepted
                    ? Success(Loc.Remote_MessageSteered, chat.Id)
                    : Fail(Loc.Remote_MessageSteerFailed, chat.Id);
            }

            var initiallyEmpty = chat.MessageCount == 0 && chat.Messages.Count == 0;
            using var firstTurnReservation = initiallyEmpty
                ? owner.TryReserveExternalSend(chat.Id)
                : null;
            if (initiallyEmpty && firstTurnReservation is null)
                return Fail(Loc.Remote_ChatAlreadyStartingTurn, chat.Id);
            if (!initiallyEmpty && owner.IsExternalSendReserved(chat.Id))
                return Fail(Loc.Remote_ChatAlreadyStartingTurn, chat.Id);

            var previousProjectId = chat.ProjectId;
            var previousWorktreePath = chat.WorktreePath;
            if (firstTurnReservation?.IsCancellationRequested == true)
                return Fail(Loc.Remote_PendingTurnStartCanceled, chat.Id);

            var projectChangeError = await PrepareProjectChangeAsync(
                    owner,
                    chat,
                    projectSelection)
                .ConfigureAwait(true);
            if (projectChangeError is not null)
                return Fail(projectChangeError, chat.Id);
            if (firstTurnReservation?.IsCancellationRequested == true)
            {
                if (previousWorktreePath is { Length: > 0 }
                    && chat.ProjectId == previousProjectId
                    && string.IsNullOrWhiteSpace(chat.WorktreePath))
                {
                    var restoreError = await owner
                        .RestoreWorktreeForExternalChatAsync(chat, previousWorktreePath)
                        .ConfigureAwait(true);
                    if (restoreError is not null)
                        return Fail(restoreError, chat.Id);
                }

                return Fail(Loc.Remote_PendingTurnStartCanceled, chat.Id);
            }

            var explicitWorktree = command.GetBool("worktree");
            var chatHasStarted = chat.MessageCount > 0 || chat.Messages.Count > 0;
            if (explicitWorktree == false &&
                chatHasStarted &&
                !string.IsNullOrWhiteSpace(chat.WorktreePath))
            {
                return Fail(
                    Loc.Remote_WorktreeDetachBeforeFirstMessage,
                    chat.Id);
            }

            // A deferred phone chat is created by this send. Apply every staged run-setting before
            // StartExternalMessageAsync snapshots model/provider/effort/context/agent/skill/MCP state.
            ApplyChatConfiguration(owner, command, projectSelection, agentSelection);

            var project = chat.ProjectId is { } projectId
                ? _dataStore.Data.Projects.FirstOrDefault(candidate => candidate.Id == projectId)
                : null;
            var useWorktree = explicitWorktree
                              ?? (createdChat
                                  && project?.DefaultNewChatsUseWorktree == true
                                  && GitService.IsGitRepo(project.WorkingDirectory ?? ""));
            var projectChanged = previousProjectId != chat.ProjectId;
            var reservedProjectId = firstTurnReservation is null ? null : chat.ProjectId;
            var reservedProjectDirectory = firstTurnReservation is null
                ? null
                : project?.WorkingDirectory;
            var hadWorktreeReference = previousWorktreePath is { Length: > 0 };
            var hadReusableWorktree = hadWorktreeReference
                                      && Directory.Exists(previousWorktreePath);
            var emptyChat = chat.MessageCount == 0 && chat.Messages.Count == 0;
            ChatViewModel.ExternalWorktreeCreationResult worktreeCreation = default;

            if (hadWorktreeReference && emptyChat && (explicitWorktree == false || projectChanged))
            {
                var clearError = await owner
                    .ClearWorktreeForExternalChatAsync(chat)
                    .ConfigureAwait(true);
                if (clearError is not null)
                    return Fail(clearError, chat.Id);
                hadReusableWorktree = false;
            }

            if (useWorktree == true)
            {
                var canCreateWorktree = createdChat
                                        || emptyChat;
                if (!hadReusableWorktree && !canCreateWorktree)
                    return Fail(Loc.Remote_WorktreeCreateBeforeFirstMessage, chat.Id);

                if (!hadReusableWorktree)
                {
                    var worktreeResult = await owner
                        .CreateWorktreeForExternalChatAsync(chat)
                        .ConfigureAwait(true);
                    if (worktreeResult.Error is not null)
                        return Fail(worktreeResult.Error, chat.Id);
                    if (worktreeResult.CreatedByThisCall)
                        worktreeCreation = worktreeResult;
                }
            }

            if (firstTurnReservation?.IsCancellationRequested == true)
            {
                var cleanupError = await CleanupCanceledWorktreeAsync(
                        owner,
                        chat,
                        worktreeCreation)
                    .ConfigureAwait(true);
                if (cleanupError is not null)
                    return Fail(cleanupError, chat.Id);

                return Fail(Loc.Remote_PendingTurnStartCanceled, chat.Id);
            }

            // Acknowledge only after the message has been persisted and the runtime marked active.
            // A newly-created chat already carries these settings on its owner, so do not apply a
            // second per-send override after the configuration snapshot.
            var model = createdChat ? null : command.Get("model");
            var effort = createdChat ? null : command.Get("reasoningEffort") ?? command.Get("quality");
            var startResult = _startExternalMessageAsync is null
                ? await owner.StartExternalMessageAsync(
                        chat,
                        message,
                        "Lumi Mobile",
                        cancellationToken,
                        model,
                        effort,
                        firstTurnReservation?.Token,
                        reservedProjectId,
                        reservedProjectDirectory,
                        command.AuthenticatedDeviceId,
                        command.RequestId,
                        interactionReceipt)
                    .ConfigureAwait(true)
                : await StartInjectedMessageAsync().ConfigureAwait(true);

            // Once the user message is persisted and the runtime is active, success is irrevocable.
            // A later project edit or Stop is a new action; reporting this accepted request as failed
            // would invite a duplicate retry and could remove the worktree from under the live turn.
            if (startResult.Accepted)
                return Success(Loc.Remote_MessageSent, chat.Id);

            if (firstTurnReservation is not null &&
                !owner.IsExternalProjectContextCurrent(
                    chat,
                    reservedProjectId,
                    reservedProjectDirectory))
            {
                var cleanupError = await CleanupCanceledWorktreeAsync(
                        owner,
                        chat,
                        worktreeCreation)
                    .ConfigureAwait(true);
                if (cleanupError is not null)
                    return Fail(cleanupError, chat.Id);

                return Fail(Loc.Remote_ProjectChangedWhileStartingTurn, chat.Id);
            }
            if (firstTurnReservation?.IsCancellationRequested == true)
            {
                var cleanupError = await CleanupCanceledWorktreeAsync(
                        owner,
                        chat,
                        worktreeCreation)
                    .ConfigureAwait(true);
                if (cleanupError is not null)
                    return Fail(cleanupError, chat.Id);

                return Fail(Loc.Remote_PendingTurnStartCanceled, chat.Id);
            }
            if (!string.IsNullOrWhiteSpace(startResult.Error))
                return Fail(startResult.Error, chat.Id);

            return Fail(Loc.Remote_MessageStartFailed, chat.Id);

            async Task<ChatViewModel.ExternalMessageStartResult> StartInjectedMessageAsync()
            {
                var error = await _startExternalMessageAsync!(
                        owner,
                        chat,
                        message,
                        cancellationToken,
                        model,
                        effort)
                    .ConfigureAwait(true);
                return string.IsNullOrWhiteSpace(error)
                    ? ChatViewModel.ExternalMessageStartResult.Success
                    : ChatViewModel.ExternalMessageStartResult.Rejected(error);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Once creation succeeds the phone must be able to adopt the chat even when subsequent
            // configuration/preflight fails; otherwise retrying creates a trail of blank chats.
            return Fail(ex.Message, chat.Id);
        }
    }

    private static async Task<string?> CleanupCanceledWorktreeAsync(
        ChatViewModel owner,
        Chat chat,
        ChatViewModel.ExternalWorktreeCreationResult worktreeCreation)
    {
        if (!worktreeCreation.CreatedByThisCall ||
            worktreeCreation.WorktreePath is not { Length: > 0 } createdWorktreePath ||
            worktreeCreation.ProjectDirectory is not { Length: > 0 } projectDirectory)
        {
            return null;
        }

        return await owner
            .RemoveCreatedWorktreeForExternalChatAsync(
                chat,
                createdWorktreePath,
                projectDirectory)
            .ConfigureAwait(true);
    }

    private async Task<RemoteCommandResult> StopGenerationAsync(RemoteCommand command)
    {
        if (ResolveChat(command) is not { } chat)
            return Fail(Loc.Remote_ChatNotFound);

        var owner = await ResolveChatOwnerAsync(chat).ConfigureAwait(true);
        if (owner is null || owner.CurrentChat?.Id != chat.Id)
            return Fail(Loc.Remote_ChatLiveSurfaceActivationFailed, chat.Id);

        if (owner.CancelExternalSendReservation(chat.Id))
            return Success(Loc.Remote_PendingTurnStartCanceledSuccess, chat.Id);

        if (!owner.IsChatBusy(chat.Id))
            return Success(Loc.Remote_ChatAlreadyStopped, chat.Id);

        var error = await owner.TryStopGenerationAsync().ConfigureAwait(true);
        return error is null
            ? Success(Loc.Remote_GenerationStopped, chat.Id)
            : Fail(error, chat.Id);
    }

    private async Task<RemoteCommandResult> UpdateInteractionAsync(RemoteCommand command, CancellationToken cancellationToken)
    {
        if (ResolveExplicitChat(command) is not { } chat)
            return Fail(Loc.Remote_ExistingChatRequired);
        if (!Guid.TryParse(command.Get("messageId"), out var messageId)
            || command.Get("blockId") is not { Length: > 0 } blockId
            || command.Get("state") is not { Length: > 0 } stateJson)
            return Fail(Loc.Remote_InteractionArgumentsRequired, chat.Id);

        try
        {
            await _dataStore.LoadChatMessagesAsync(chat, cancellationToken).ConfigureAwait(true);
            var owner = TryResolveRegisteredOwner(chat.Id);
            var message = owner?.FindInteractionMessage(chat, messageId)
                ?? chat.Messages.FirstOrDefault(candidate => candidate.Id == messageId);
            if (message is null
                && await _dataStore.FindChatMessageAsync(chat, messageId, cancellationToken).ConfigureAwait(true) is { } stored)
            {
                // An answer in an older exchange, only in storage: edit it in place so saving stores it.
                _dataStore.AttachHistoryMessages(chat, [stored]);
                message = stored;
            }

            if (message is null)
                return Fail(Loc.Remote_AnswerNoLongerInChat, chat.Id);
            if (message.ToolName == "ask_input")
            {
                if (owner is null)
                    return Fail(Loc.Remote_InputRequestEnded, chat.Id);
                var inputResult = await owner.RequestInputInteractionAsync(
                    chat, message, blockId, stateJson, command.Get("action")).ConfigureAwait(true);
                return inputResult.Accepted ? Success(Loc.Remote_InputsReceived, chat.Id) : Fail(inputResult.Error!, chat.Id);
            }
            var updated = InteractiveResponseState.Update(message, blockId, stateJson);
            _dataStore.MarkChatChanged(chat);
            owner?.RefreshInteractionState(messageId);
            await SaveInteractionChatAsync(chat, cancellationToken).ConfigureAwait(true);
            var action = command.Get("action");
            if (string.IsNullOrEmpty(action))
                return Success(Loc.Remote_InteractionSaved, chat.Id);
            var text = InteractiveSubmission.Build(updated.Definition, updated.State, action);
            var receipt = InteractiveResponseState.CreateReceipt(message, updated.Definition, updated.State, action, Loc.Culture.Name);
            var result = await SendMessageAsync(
                command.With("message", text).With("newChat", "false").With("steer", "false").With("stopAndSend", "false"),
                cancellationToken, receipt).ConfigureAwait(true);
            if (result.Ok)
            {
                InteractiveResponseState.Acknowledge(message, blockId, action, updated.State);
                _dataStore.MarkChatChanged(chat);
                TryResolveRegisteredOwner(chat.Id)?.RefreshInteractionState(messageId);
                await SaveInteractionChatAsync(chat, cancellationToken).ConfigureAwait(true);
            }
            return result;
        }
        catch (Exception error) when (error is FormatException or JsonException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("[RemoteInteractiveResponse] {0}", error);
            return Fail(error.Message, chat.Id);
        }
    }

    /// <summary>Interaction edits follow the desktop's Auto-Save Chats setting.</summary>
    private Task SaveInteractionChatAsync(Chat chat, CancellationToken cancellationToken) =>
        _dataStore.Data.Settings.AutoSaveChats ? _dataStore.SaveChatAsync(chat, cancellationToken) : Task.CompletedTask;

    private async Task<RemoteCommandResult> AnswerQuestionAsync(RemoteCommand command)
    {
        if (command.Get("questionId") is not { Length: > 0 } questionId)
            return Fail(Loc.Get("Remote_ArgumentRequired", "questionId"));

        if (ResolveChat(command) is not { } chat)
            return Fail(Loc.Remote_ChatNotFound);

        var owner = await ResolveChatOwnerAsync(chat).ConfigureAwait(true);
        if (owner is null)
            return Fail(Loc.Remote_ChatLiveSurfaceNotFound, chat.Id);

        owner.SubmitQuestionAnswer(questionId, command.Get("answer") ?? "");
        return Success(Loc.Remote_AnswerSubmitted, chat.Id);
    }

    /// <summary>
    /// Applies composer configuration to the open chat. The desktop keeps model / effort / context
    /// tier / agent / project / skills / MCPs on <see cref="ChatViewModel"/> for whichever chat is
    /// currently open, so the chat must be opened first; every other path would diverge from what
    /// the desktop composer itself does.
    ///
    /// Only arguments actually present are applied. That matters because the phone sends one picker
    /// at a time — echoing the whole configuration back would re-apply values the user did not touch
    /// and fight the desktop for control of the chat.
    /// </summary>
    private async Task<RemoteCommandResult> ConfigureChatAsync(RemoteCommand command)
    {
        if (ResolveChat(command) is not { } chat)
            return Fail(Loc.Remote_ChatNotFound);
        if (!TryResolveProjectSelection(command, out var projectSelection, out var projectError))
            return Fail(projectError, chat.Id);
        if (!TryResolveAgentSelection(command, out var agentSelection, out var agentError))
            return Fail(agentError, chat.Id);

        var chatVm = await ResolveChatOwnerAsync(chat).ConfigureAwait(true);
        if (chatVm is null || chatVm.CurrentChat?.Id != chat.Id)
            return Fail(Loc.Remote_ChatSurfaceActivationFailed, chat.Id);

        if (projectSelection.IsSpecified && chatVm.IsExternalSendReserved(chat.Id))
            return Fail(Loc.Remote_ChatAlreadyStartingTurn, chat.Id);

        var projectChangeError = await PrepareProjectChangeAsync(
                chatVm,
                chat,
                projectSelection)
            .ConfigureAwait(true);
        if (projectChangeError is not null)
            return Fail(projectChangeError, chat.Id);

        var applied = ApplyChatConfiguration(chatVm, command, projectSelection, agentSelection);
        if (applied.Count == 0)
            return Fail(Loc.Remote_NothingToConfigure, chat.Id);

        await _dataStore.SaveAsync().ConfigureAwait(true);
        return Success(Loc.Get("Remote_ConfigurationUpdated", string.Join(", ", applied.Distinct())), chat.Id);
    }

    private List<string> ApplyChatConfiguration(
        ChatViewModel chatVm,
        RemoteCommand command,
        ProjectSelection projectSelection,
        AgentSelection agentSelection)
    {
        var applied = new List<string>();
        var setupChat = command.GetBool("useChatSetup") == true
                        && chatVm.CurrentChat is { Messages.Count: 0 } emptyChat
            ? emptyChat
            : null;
        if (setupChat is not null)
        {
            chatVm.ApplyModelSelection(
                command.Get("model") ?? chatVm.SelectedModel,
                command.Get("quality") ?? command.Get("reasoningEffort") ?? chatVm.SelectedQuality,
                command.Get("contextWindowTier") ?? chatVm.SelectedContextWindowTier);
        }

        if (command.Get("model") is { Length: > 0 } model)
        {
            if (setupChat is not null)
                setupChat.LastModelUsed = model;
            else
                chatVm.SelectedModel = model;
            applied.Add(Loc.Chat_ContextWindow_Model);
        }

        // Quality and tier are set after the model on purpose: changing the model rebuilds both
        // catalogs, which would otherwise clobber a value applied in the same request.
        if ((command.Get("quality") ?? command.Get("reasoningEffort")) is { Length: > 0 } quality)
        {
            if (setupChat is not null)
                setupChat.LastReasoningEffortUsed =
                    chatVm.NormalizeReasoningEffortFor(chatVm.SelectedModel, quality);
            else
                chatVm.SelectedQuality = quality;
            applied.Add(Loc.Remote_ConfigurationQuality);
        }

        if (command.Get("contextWindowTier") is { Length: > 0 } tier)
        {
            if (setupChat is not null)
                setupChat.LastContextWindowTierUsed =
                    chatVm.NormalizeContextWindowTierFor(chatVm.SelectedModel, tier);
            else
                chatVm.SelectedContextWindowTier = tier;
            applied.Add(Loc.Chat_ContextWindow_Title);
        }

        if (agentSelection.IsSpecified)
        {
            if (agentSelection.Agent is { } selectedAgent)
                chatVm.SetActiveAgent(selectedAgent);
            else if (agentSelection.ExternalName is { Length: > 0 } externalName)
                chatVm.SelectAgentByName(externalName);
            else
                chatVm.SetActiveAgent(null);

            applied.Add(Loc.Capability_KindLumi);
        }

        if (projectSelection.IsSpecified)
        {
            if (chatVm.CurrentChat is { } chat && chat.ProjectId != projectSelection.ProjectId)
            {
                chat.ProjectId = projectSelection.ProjectId;
                _dataStore.MarkChatChanged(chat);
                chatVm.OnCurrentChatProjectChangedExternally();
            }
            applied.Add(Loc.Library_Field_Project);
        }

        foreach (var skill in command.GetList("addSkills") ?? [])
        {
            chatVm.AddSkillByName(skill);
            applied.Add(Loc.Capability_KindSkill);
        }

        foreach (var skill in command.GetList("removeSkills") ?? [])
        {
            chatVm.RemoveSkillByName(skill);
            applied.Add(Loc.Capability_KindSkill);
        }

        foreach (var mcp in command.GetList("addMcps") ?? [])
        {
            chatVm.AddMcpServer(mcp);
            applied.Add(Loc.Capability_KindMcp);
        }

        foreach (var mcp in command.GetList("removeMcps") ?? [])
        {
            chatVm.RemoveMcpByName(mcp);
            applied.Add(Loc.Capability_KindMcp);
        }

        return applied;
    }

    private async Task<string?> PrepareProjectChangeAsync(
        ChatViewModel chatVm,
        Chat chat,
        ProjectSelection projectSelection)
    {
        if (!projectSelection.IsSpecified ||
            projectSelection.ProjectId == chat.ProjectId ||
            string.IsNullOrWhiteSpace(chat.WorktreePath))
        {
            return null;
        }

        if (chat.MessageCount > 0 || chat.Messages.Count > 0)
        {
            return Loc.Remote_WorktreeProjectChangeBlocked;
        }

        return await chatVm
            .ClearWorktreeForExternalChatAsync(chat)
            .ConfigureAwait(true);
    }

    private bool TryResolveProjectSelection(
        RemoteCommand command,
        out ProjectSelection selection,
        out string error)
    {
        if (command.Arguments.TryGetValue("projectId", out var rawProjectId)
            && rawProjectId is not null)
        {
            if (string.IsNullOrWhiteSpace(rawProjectId))
            {
                selection = new ProjectSelection(true, null);
                error = "";
                return true;
            }

            if (!Guid.TryParse(rawProjectId, out var projectId)
                || !_dataStore.Data.Projects.Any(project => project.Id == projectId))
            {
                selection = default;
                error = Loc.Remote_ProjectNotFound;
                return false;
            }

            selection = new ProjectSelection(true, projectId);
            error = "";
            return true;
        }

        if (GetAliasedArgument(command, "project", "projectName") is not { } projectName)
        {
            selection = default;
            error = "";
            return true;
        }

        if (string.IsNullOrWhiteSpace(projectName))
        {
            selection = new ProjectSelection(true, null);
            error = "";
            return true;
        }

        var project = _dataStore.Data.Projects.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, projectName, StringComparison.Ordinal));
        if (project is null)
        {
            selection = default;
            error = Loc.Remote_ProjectNotFound;
            return false;
        }

        selection = new ProjectSelection(true, project.Id);
        error = "";
        return true;
    }

    private bool TryResolveAgentSelection(
        RemoteCommand command,
        out AgentSelection selection,
        out string error)
    {
        if (command.Arguments.TryGetValue("agentId", out var rawAgentId))
        {
            if (string.IsNullOrWhiteSpace(rawAgentId))
            {
                selection = new AgentSelection(true, null, null);
                error = "";
                return true;
            }

            if (!Guid.TryParse(rawAgentId, out var agentId)
                || _dataStore.Data.Agents.FirstOrDefault(agent => agent.Id == agentId) is not { } agent)
            {
                selection = default;
                error = Loc.Remote_AgentNotFound;
                return false;
            }

            selection = new AgentSelection(true, agent, null);
            error = "";
            return true;
        }

        if (GetAliasedArgument(command, "agent", "agentName") is not { } agentName)
        {
            selection = default;
            error = "";
            return true;
        }

        selection = string.IsNullOrWhiteSpace(agentName)
            ? new AgentSelection(true, null, null)
            : new AgentSelection(true, null, agentName);
        error = "";
        return true;
    }

    private static string? GetAliasedArgument(RemoteCommand command, string primary, string alias)
    {
        if (command.Arguments.TryGetValue(primary, out var value))
            return value;
        return command.Arguments.TryGetValue(alias, out value) ? value : null;
    }

    private async Task<RemoteCommandResult> ConfigureFeatureAsync(RemoteCommand command)
    {
        var resource = (command.Get("resource") ?? "").Trim().Replace('-', '_').ToLowerInvariant();
        var action = command.Get("featureAction") ?? "list";
        var manager = new LumiFeatureManager(_dataStore, localizeOutput: true);
        IReadOnlySet<Guid>? affectedProjectChatIds = null;
        if (NormalizeFeatureResource(resource) == RemoteProtocol.Resources.Projects
            && action is "update" or "delete"
            && ResolveFeatureProject(command.Get("identifier")) is { } affectedProject)
        {
            affectedProjectChatIds = _dataStore.Data.Chats
                .Where(chat => chat.ProjectId == affectedProject.Id)
                .Select(chat => chat.Id)
                .ToHashSet();
        }

        FeatureChangeResult result;
        try
        {
            result = resource switch
            {
                RemoteProtocol.Resources.Projects or "project" => manager.ManageProjects(
                    action,
                    command.Get("identifier"),
                    command.Get("name"),
                    command.Get("instructions"),
                    command.Get("workingDirectory"),
                    command.GetBool("clearWorkingDirectory")),
                RemoteProtocol.Resources.Skills or "skill" => manager.ManageSkills(
                    action,
                    command.Get("identifier"),
                    command.Get("name"),
                    command.Get("description"),
                    command.Get("content"),
                    command.Get("iconGlyph")),
                RemoteProtocol.Resources.Lumis or "agents" or "agent" or "lumi" => manager.ManageLumis(
                    action,
                    command.Get("identifier"),
                    command.Get("name"),
                    command.Get("description"),
                    command.Get("systemPrompt"),
                    command.Get("iconGlyph"),
                    command.GetList("skillIdentifiers"),
                    command.GetList("toolNames"),
                    command.GetList("mcpServerIdentifiers")),
                RemoteProtocol.Resources.Mcps or "mcp" or "mcp_servers" => manager.ManageMcps(
                    action,
                    command.Get("identifier"),
                    command.Get("name"),
                    command.Get("description"),
                    command.Get("serverType"),
                    command.Get("command"),
                    command.GetList("args"),
                    command.Get("url"),
                    command.GetList("envEntries"),
                    command.GetList("headerEntries"),
                    command.GetList("toolNames"),
                    command.GetInt("timeout"),
                    command.GetBool("clearTimeout"),
                    command.GetBool("isEnabled")),
                RemoteProtocol.Resources.Memories or "memory" => manager.ManageMemories(
                    action,
                    command.Get("identifier"),
                    command.Get("key"),
                    command.Get("content"),
                    command.Get("category")),
                RemoteProtocol.Resources.Jobs or "background_jobs" => manager.ManageJobs(
                    action,
                    command.Get("identifier"),
                    command.Get("name"),
                    command.Get("description"),
                    command.Get("prompt"),
                    command.Get("chatIdentifier"),
                    command.Get("triggerType"),
                    command.Get("scheduleType"),
                    command.GetInt("intervalMinutes"),
                    command.Get("dailyTime"),
                    command.Get("daysOfWeek"),
                    command.GetInt("monthlyDay"),
                    command.Get("cronExpression"),
                    command.Get("runAt"),
                    command.Get("scriptContent"),
                    command.Get("scriptLanguage"),
                    command.GetBool("isTemporary"),
                    command.GetBool("isEnabled"),
                    command.GetBool("runNow"),
                    null,
                    _main.ChatVM.CurrentChat?.Id,
                    command.Get("sourceChatIdentifier"),
                    command.GetList("chatEventTypes")),
                _ => throw new InvalidOperationException(Loc.Get("Remote_UnknownFeatureResource", resource))
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Fail(ex.Message);
        }

        var isReadOnly = action is "list" or "show" or "search";
        if (!result.DataChanged && !isReadOnly)
            return Fail(result.Message);

        if (result.DataChanged)
        {
            await _main
                .ApplyFeatureChangeAsync(
                    result,
                    NormalizeFeatureResource(resource),
                    affectedProjectChatIds)
                .ConfigureAwait(true);
        }

        return Success(result.Message);
    }

    private static string NormalizeFeatureResource(string resource) => resource switch
    {
        RemoteProtocol.Resources.Projects or "project" => RemoteProtocol.Resources.Projects,
        RemoteProtocol.Resources.Skills or "skill" => RemoteProtocol.Resources.Skills,
        RemoteProtocol.Resources.Lumis or "agents" or "agent" or "lumi" => RemoteProtocol.Resources.Lumis,
        RemoteProtocol.Resources.Mcps or "mcp" or "mcp_servers" => RemoteProtocol.Resources.Mcps,
        RemoteProtocol.Resources.Memories or "memory" => RemoteProtocol.Resources.Memories,
        RemoteProtocol.Resources.Jobs or "background_jobs" => RemoteProtocol.Resources.Jobs,
        _ => resource
    };

    private Project? ResolveFeatureProject(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return null;
        return Guid.TryParse(identifier, out var id)
            ? _dataStore.Data.Projects.FirstOrDefault(project => project.Id == id)
            : _dataStore.Data.Projects.FirstOrDefault(project =>
                string.Equals(project.Name, identifier, StringComparison.OrdinalIgnoreCase));
    }

}
