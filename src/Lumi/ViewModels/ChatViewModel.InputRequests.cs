using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Microsoft.Extensions.AI;
using StrataTheme.Controls;
using ChatMessage = Lumi.Models.ChatMessage;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    public bool IsAwaitingInput => CurrentChat?.IsAwaitingInput == true;

    private AIFunction BuildAskInputTool(Guid chatId) => AIFunctionFactory.Create(
        async (
            [Description("Input UI type: form for related fields, or results for choosing from a prepared shortlist.")] string kind,
            [Description("Complete JSON definition using Lumi's form/results schema: stable id, title, fields or items, optional description and submitLabel. Supply a JSON string, not a fenced markdown answer.")] string definition,
            CancellationToken cancellationToken) =>
            await AskInputAsync(chatId, kind, definition, cancellationToken),
        "ask_input",
        "Ask for structured input needed before you can proceed. Presents a native form or selectable shortlist and waits for the user. Submission returns to this same task as a tool result; it does not create a user message or new turn. Use ask_question for one simple choice, and reply fences for completed results the user can explore later. Never use this as approval for purchases, bookings, messages, or destructive work.");

    internal async Task<string> AskInputAsync(Guid chatId, string kind, string definitionJson, CancellationToken cancellationToken)
    {
        if (kind is not ("form" or "results"))
            throw new ArgumentException("ask_input supports form or results.", nameof(kind));
        var definition = InteractiveDefinition.Parse(kind, definitionJson);
        var questionId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        TrackPendingQuestion(chatId, questionId, completion);
        try
        {
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            var saveTask = await Dispatcher.UIThread.InvokeAsync<Task>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chat = _dataStore.Data.Chats.FirstOrDefault(chat => chat.Id == chatId)
                    ?? throw new InvalidOperationException("The requesting chat is no longer available.");
                var requestMessage = chat.Messages.LastOrDefault(message =>
                    message.ToolName == "ask_input" && message.ToolStatus == "InProgress" && message.QuestionId is null);
                if (requestMessage is null)
                {
                    requestMessage = new ChatMessage { Role = "tool", ToolName = "ask_input", ToolStatus = "InProgress" };
                    requestMessage.MarkToolStarted(DateTimeOffset.UtcNow);
                    chat.Messages.Add(requestMessage);
                }
                requestMessage.QuestionId = questionId;
                requestMessage.QuestionText = definition.Title;
                requestMessage.InputKind = kind;
                requestMessage.InputDefinition = definitionJson;
                chat.IsAwaitingInput = true;
                if (CurrentChat?.Id == chatId)
                {
                    _transcriptBuilder.AddInputRequest(requestMessage);
                    RefreshInputWaitingState(chat);
                    ScrollToEndRequested?.Invoke();
                }
                NotifyQuestionAsked(chatId, definition.Title);
                _dataStore.MarkChatChanged(chat);
                return SaveChatAsync(chat, saveIndex: false, cancellationToken: cancellationToken);
            }, DispatcherPriority.Normal, cancellationToken);
            await saveTask;
            var answer = await completion.Task;
            return answer;
        }
        finally
        {
            RemovePendingQuestion(questionId);
        }
    }

    internal async Task<InteractiveActionResult> RequestInputInteractionAsync(
        Chat chat, ChatMessage message, string blockId, string stateJson, string? action)
    {
        var labels = InteractiveLabels.ForLanguage(Loc.Culture.Name);
        if (message.QuestionId is not { } questionId || !IsPendingQuestion(questionId)
            || message.InputAnswer is not null || message.InputCanceled)
            return new(false, labels["inputExpired"]);
        if (chat.IsPaused)
            return new(false, Loc.Get("Chat_PausedSendBlocked"));
        try
        {
            var updated = InteractiveResponseState.Update(message, blockId, stateJson);
            if (action is null)
            {
                RefreshInteractionState(message.Id);
                await SaveChatAsync(chat, saveIndex: false);
                return new(true);
            }
            if (action == "cancel")
            {
                const string cancelResult = "User canceled the input request. Do not continue using guessed inputs.";
                if (!TryCompletePendingQuestion(questionId, cancelResult))
                    return new(false, labels["inputExpired"]);
                message.InputCanceled = true;
                message.ToolOutput = cancelResult;
            }
            else
            {
                var expectedAction = updated.Definition is InteractiveForm ? "submit" : "use";
                if (action != expectedAction)
                    throw new FormatException("This request accepts the requested choices, not a follow-up action.");
                _ = InteractiveSubmission.Build(updated.Definition, updated.State, action);
                await SaveChatAsync(chat, saveIndex: false);
                if (!IsPendingQuestion(questionId))
                    return new(false, labels["inputExpired"]);
                var result = $"User submitted {updated.Definition.Kind} \"{updated.Definition.Title}\":\n{updated.State.ToJson()}\nThis is input to the existing task, not permission for high-stakes actions.";
                if (!TryCompletePendingQuestion(questionId, result))
                    return new(false, labels["inputExpired"]);
                message.InputAnswer = updated.State.ToJson();
                message.ToolOutput = result;
                InteractiveResponseState.Acknowledge(message, blockId, action, updated.State);
            }
            RefreshInteractionState(message.Id);
            RefreshInputWaitingState(chat);
            await SaveChatAsync(chat, saveIndex: false);
            return new(true);
        }
        catch (Exception error) when (error is FormatException or JsonException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("[InputRequest] {0}", error);
            return new(false, error.Message);
        }
    }

    private void RefreshInputWaitingState(Chat chat)
    {
        chat.IsAwaitingInput = HasPendingQuestion(chat.Id);
        if (CurrentChat?.Id != chat.Id) return;
        OnPropertyChanged(nameof(IsAwaitingInput));
        if (IsAwaitingInput)
            _transcriptBuilder.HideTypingIndicator();
        else if (IsBusy && !IsPaused)
            _transcriptBuilder.ShowTypingIndicator(StatusText);
    }
}
