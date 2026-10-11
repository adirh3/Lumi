using Lumi.Services;
using Lumi.Services.Sharing;
using CommunityToolkit.Mvvm.Input;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    public event Action<PromptShareSource, Func<PromptShareSource, CancellationToken, Task<SharedPrompt>>>? SharePromptRequested;
    public event Action<string>? PromptImportRequested;

    public bool CanShareLatestPrompt => LatestShareableResult() is not null;

    private Lumi.Models.ChatMessage? LatestShareableResult()
        => CurrentChat?.Messages.LastOrDefault(static message => message.Role == "assistant"
            && !message.IsStreaming && message.ParentToolCallId is null && !string.IsNullOrWhiteSpace(message.Content));

    [RelayCommand(CanExecute = nameof(CanShareLatestPrompt))]
    private void ShareLatestPrompt()
    {
        if (LatestShareableResult() is { } result)
            RequestSharePrompt(result.Id);
    }

    public void RequestSharePrompt(Guid messageId)
    {
        if (CurrentChat is not { } chat)
            throw new InvalidOperationException("There is no chat to share a prompt from.");

        var platform = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";
        var source = PromptDraftBuilder.Create(chat, messageId, platform);
        var selection = ResolveSelectedModelForChat(chat);
        SharePromptRequested?.Invoke(source, async (snapshot, ct) =>
        {
            var route = ResolveModelRouteForChat(selection, chat, allowLegacyByWireId: true);
            if (route.IsInvalidByok)
                throw new InvalidOperationException("The selected BYOK model is no longer configured.");
            var capabilities = ByokConfigHelper.BuildModelCapabilitiesOverride(route.ByokModel, null);
            await _copilotService.ConnectAsync(ct);
            if (route.ByokModel is { MaxRequestsPerMinute: > 0 } model)
                await _byokRateLimiter.AcquireSendSlotAsync(model.Id, model.MaxRequestsPerMinute, ct);
            var modelId = route.WireModelId;
            if (string.IsNullOrWhiteSpace(modelId) && !_dataStore.Data.Settings.UseBYOKOnly)
                modelId = PickBestModel((await _copilotService.GetModelsAsync(ct)).Select(static model => model.Id).ToList());
            return await _copilotService.GenerateSharedPromptAsync(snapshot, modelId, route.Provider, capabilities, ct);
        });
    }

    /// <summary>Explicit paste uses the normal preview, even when automatic clipboard offers are disabled.</summary>
    public bool TryImportPastedPrompt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !CapabilityNoticeViewModel.LooksLikeLumiCapability(text))
            return false;
        var result = CapabilityPackReader.Read(text);
        if (result.Pack?.Kind != SharedCapabilityKind.Prompt
            && result.Error is not (PackReadError.DamagedCode or PackReadError.NewerVersion or PackReadError.InvalidPrompt))
            return false;
        PromptImportRequested?.Invoke(text);
        return true;
    }
}
