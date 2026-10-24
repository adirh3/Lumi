using System.Diagnostics;
using System.Text.Json;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.ViewModels;

public sealed partial class MobileChatViewModel
{
    private readonly Dictionary<string, string> _interactionRequestIds = [];
    private readonly SemaphoreSlim _interactionWriteGate = new(1, 1);
    private readonly Dictionary<string, long> _interactionEditVersions = [];
    private long _nextInteractionEditVersion;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSheet))]
    private bool _isInteractivePickerOpen;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowThinking))]
    [NotifyPropertyChangedFor(nameof(WorkingStatusText))]
    private bool _isAwaitingInput;
    public bool SupportsInteractiveResponses => _sink.SupportsInteractiveResponses;
    public bool CanSubmitInteractions => SupportsInteractiveResponses && ChatId != Guid.Empty && !IsBusy && !IsLoading;
    internal void RefreshInteractiveCapabilities()
    {
        OnPropertyChanged(nameof(SupportsInteractiveResponses));
        OnPropertyChanged(nameof(CanSubmitInteractions));
    }

    public async Task<InteractiveActionResult> RequestInteractionAsync(
        string messageId, string blockId, string stateJson, string? action)
    {
        if (!SupportsInteractiveResponses)
            return new(false, Strings.Get("InteractiveUpdateRequired"));
        var chatId = ChatId;
        var hostGeneration = _hostGeneration;
        var item = Turns.SelectMany(turn => turn.Items)
            .FirstOrDefault(item => item.Id == messageId && item is AssistantItemViewModel or InputRequestItemViewModel);
        if (chatId == Guid.Empty || item is null)
            return new(false, Strings.Get("AnswerClosed"));
        if (action is not null && !(item is InputRequestItemViewModel input ? input.CanSubmit : CanSubmitInteractions))
            return new(false, Strings.Get("WaitForReply"));
        try
        {
            var state = InteractiveState.Parse(stateJson);
            var widgetKey = $"{hostGeneration}:{chatId:N}:{messageId}:{blockId}";
            var editVersion = ++_nextInteractionEditVersion;
            if (action is null)
                _interactionEditVersions[widgetKey] = editVersion;
            if (item is AssistantItemViewModel assistant)
                assistant.StageInteractionState(blockId, stateJson);
            else if (item is InputRequestItemViewModel inputItem)
                inputItem.Stage(stateJson);
            await _interactionWriteGate.WaitAsync();
            try
            {
                if (hostGeneration != _hostGeneration || chatId != ChatId)
                    return new(false, Strings.Get("InteractionChatChanged"));
                if (action is null && _interactionEditVersions.GetValueOrDefault(widgetKey) != editVersion)
                    return new(true);
                var key = $"{widgetKey}:{action}:{state.Fingerprint()}";
                if (!_interactionRequestIds.TryGetValue(key, out var requestId))
                    _interactionRequestIds[key] = requestId = Guid.NewGuid().ToString("N");
                var command = new RemoteCommand(RemoteProtocol.Actions.UpdateInteraction)
                    .With("chatId", chatId.ToString())
                    .With("messageId", messageId)
                    .With("blockId", blockId)
                    .With("state", stateJson)
                    .With("action", action);
                command.RequestId = requestId;
                var result = await _sink.SendCommandAsync(command);
                if (!result.IsOutcomeUnknown)
                    _interactionRequestIds.Remove(key);
                if (!result.Ok)
                    return new(false, result.IsOutcomeUnknown
                        ? Strings.Get("InteractionUnconfirmed")
                        : result.Error ?? Strings.Get("InteractionSaveFailed"));
                if (item is AssistantItemViewModel updated)
                    updated.ConfirmInteractionState(blockId, stateJson);
                else if (item is InputRequestItemViewModel confirmed && action is not null)
                {
                    confirmed.Confirm(stateJson);
                    confirmed.InputStatus = action == "cancel" ? "Canceled" : "Submitted";
                }
                return new(true);
            }
            finally
            {
                _interactionWriteGate.Release();
            }
        }
        catch (Exception error) when (error is JsonException or FormatException or HttpRequestException or IOException)
        {
            Trace.TraceWarning("[MobileInteractiveResponse] {0}", error);
            return new(false, error.Message);
        }
        catch (OperationCanceledException)
        {
            return new(false, Strings.Get("InteractionInterrupted"));
        }
    }
}
