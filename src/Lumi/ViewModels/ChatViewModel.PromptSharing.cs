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
        SharePromptRequested?.Invoke(source, async (snapshot, ct) =>
        {
            await _copilotService.ConnectAsync(ct);
            return await _copilotService.GenerateSharedPromptAsync(snapshot, ct: ct);
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
