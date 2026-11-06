using System.IO;
using System.Text.Json;
using Avalonia.Threading;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Byok;
using Lumi.Services.ImageGeneration;
using Microsoft.Extensions.AI;
using ChatMessage = Lumi.Models.ChatMessage;

namespace Lumi.ViewModels;

/// <summary>
/// Image generation in chats. A chat on GitHub Copilot's route turns on Copilot's native <c>image_generation</c>
/// tool; a chat on an account's route gets Lumi's tool of the same name. Either way, once an image is created
/// the model is told it stays out of the reply until it shows it (<see cref="GeneratedImageResults"/>), so it
/// decides which images the user sees.
/// </summary>
public partial class ChatViewModel
{
    private ImageGenerationPlan ResolveImageGenerationPlan()
        => ImageGenerationRouting.Resolve(_dataStore.Data.Settings, _copilotService.CanUseCopilotImages);

    /// <summary>
    /// The native switch for a session, always explicit: a resumed session keeps its previous state when the
    /// option is left out, so a chat that moved to an account's route must turn Copilot's tool off.
    /// </summary>
#pragma warning disable GHCP001 // Image generation is an experimental Copilot SDK option.
    internal static ImageGenerationConfig BuildImageGenerationConfig(ImageGenerationPlan plan, UserSettings settings)
        => plan.Route == ImageGenerationRoute.Copilot
            ? new ImageGenerationConfig { Enabled = true, Watermark = settings.ImageGenerationWatermark }
            : new ImageGenerationConfig { Enabled = false };
#pragma warning restore GHCP001

    private AIFunction BuildAccountImageTool(Guid chatId, ByokEndpoint account, UserSettings settings)
        => new AccountImageGenerationTool(
            account,
            ImageGenerationRouting.ModelFor(settings, account),
            Path.Combine(DataStore.AppDirectory, "images", chatId.ToString("N")),
            keyStore: SecureKeyStoreFactory.Instance,
            onSaved: (toolCallId, path) => Dispatcher.UIThread.Post(() => AddGeneratedImage(chatId, toolCallId, path)));

    private void AddGeneratedImage(Guid chatId, string toolCallId, string path)
    {
        if (_dataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == chatId) is not { } chat
            || chat.Messages.LastOrDefault(message => message.ToolCallId == toolCallId) is not { } toolMessage)
            return;

        toolMessage.GeneratedImages = [.. toolMessage.GeneratedImages ?? [], path];
        QueueSaveChat(chat, saveIndex: false);
        if (CurrentChat?.Id == chatId)
            Messages.LastOrDefault(message => message.Message.ToolCallId == toolCallId)?.NotifyGeneratedImagesChanged();
    }

    /// <summary>After an image is created, tells the model the image is shown only when its reply shows it.</summary>
    private static Task<PostToolUseHookOutput?> AddImagePresentationGuidance(PostToolUseHookInput input)
    {
        if (!string.Equals(input.ToolName, GeneratedImageResults.ToolName, StringComparison.Ordinal))
            return Task.FromResult<PostToolUseHookOutput?>(null);

        var guidance = GeneratedImageResults.BuildPresentationGuidance(
            GeneratedImageResults.ParseSavedPaths(ReadHookResultText(input.ToolResult)));
        return Task.FromResult(guidance is null ? null : new PostToolUseHookOutput { AdditionalContext = guidance });
    }

    private static string? ReadHookResultText(object? result) => result switch
    {
        null => null,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.Object } json
            => json.TryGetProperty("textResultForLlm", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : json.GetRawText(),
        JsonElement json => json.ValueKind == JsonValueKind.String ? json.GetString() : json.GetRawText(),
        ToolResultObject tool => tool.TextResultForLlm,
        _ => result.ToString(),
    };

    /// <summary>Records the images an <c>image_generation</c> call saved on its message, for the transcript and the Library.</summary>
    internal static bool RecordGeneratedImages(ChatMessage toolMessage, string? resultText)
    {
        if (!string.Equals(toolMessage.ToolName, GeneratedImageResults.ToolName, StringComparison.Ordinal))
            return false;

        var paths = GeneratedImageResults.ParseSavedPaths(resultText);
        if (paths.Count == 0)
            return false;

        toolMessage.GeneratedImages = paths;
        return true;
    }
}
