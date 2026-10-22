using System.Diagnostics;
using System.Text.Json;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    private static void BeginInteractionReceiptTurn(ChatRuntimeState runtime, ChatMessage? message)
    {
        runtime.InteractionReceiptMessageId = message?.InteractionReceipt is null ? null : message.Id;
        if (message?.InteractionReceipt is { } receipt)
        {
            receipt.Error = null;
            receipt.Status = "Working";
        }
    }

    private static void SetInteractionReceiptStatus(ChatRuntimeState runtime, string status, string? error = null)
    {
        var receipt = runtime.Chat?.Messages.FirstOrDefault(message => message.Id == runtime.InteractionReceiptMessageId)?.InteractionReceipt;
        if (receipt is null || receipt.Status is "ReplyReady" or "Failed" or "Stopped")
            return;
        receipt.Error = error;
        receipt.Status = status;
    }

    /// <summary>The turn ended without its reply, for example after a lost connection or an ended session.</summary>
    private static void SettleInterruptedInteractionReceipt(ChatRuntimeState runtime) =>
        SetInteractionReceiptStatus(runtime, "Stopped", InteractionHistory.InterruptedReceiptError);

    /// <summary>
    /// The answer that hosts an interaction, including one still streaming into the transcript or one
    /// of an older exchange the transcript has built.
    /// </summary>
    internal ChatMessage? FindInteractionMessage(Chat chat, Guid messageId) =>
        EnumerateShownMessages(chat).FirstOrDefault(message => message.Id == messageId)
        ?? (_inProgressMessages.TryGetValue(chat.Id, out var streaming) && streaming.Id == messageId ? streaming : null);

    public bool CanSubmitInteractions => CurrentChat is not null && !IsBusy && !IsPaused;
    public string InteractionLanguage => Loc.Culture.Name;

    public async Task<InteractiveActionResult> RequestInteractionAsync(
        Guid messageId, string blockId, string stateJson, string? action)
    {
        if (CurrentChat is not { } chat || FindInteractionMessage(chat, messageId) is not { } message)
            return new(false, "That answer is no longer open.");
        if (message.ToolName == "ask_input")
            return await RequestInputInteractionAsync(chat, message, blockId, stateJson, action);
        try
        {
            var updated = InteractiveResponseState.Update(message, blockId, stateJson);
            _dataStore.MarkChatChanged(chat);
            RefreshInteractionState(messageId);
            await SaveChatAsync(chat, saveIndex: false);
            if (action is null)
                return new(true);
            if (CurrentChat?.Id != chat.Id || !CanSubmitInteractions)
                return new(false, "Wait for Lumi's reply to finish before continuing.");

            var request = InteractiveSubmission.Build(updated.Definition, updated.State, action);
            var receipt = InteractiveResponseState.CreateReceipt(message, updated.Definition, updated.State, action, InteractionLanguage);
            var result = await StartExternalMessageAsync(
                chat,
                request,
                _dataStore.Data.Settings.UserName ?? Loc.Author_You,
                interactionReceipt: receipt);
            if (!result.Accepted)
                return new(false, result.Error ?? "Lumi could not accept this request. Your choices are saved.");
            InteractiveResponseState.Acknowledge(message, blockId, action, updated.State);
            _dataStore.MarkChatChanged(chat);
            RefreshInteractionState(messageId);
            await SaveChatAsync(chat, saveIndex: false);
            return new(true);
        }
        catch (Exception error) when (error is FormatException or JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Trace.TraceWarning("[InteractiveResponse] {0}", error);
            StatusText = error.Message;
            return new(false, error.Message);
        }
        catch (OperationCanceledException)
        {
            return new(false, "The request was canceled. Your choices are kept.");
        }
    }

    internal void RefreshInteractionState(Guid messageId)
    {
        foreach (var turn in TranscriptTurns)
            foreach (var item in turn.EnumerateItems().OfType<AssistantMessageItem>())
                if (item.MessageId == messageId)
                    item.RefreshInteractionState();
        foreach (var input in TranscriptTurns.SelectMany(turn => turn.Items).OfType<InputRequestItem>())
            if (input.Message.Id == messageId)
                input.Refresh();
    }
}
