using System.Collections.Concurrent;
using System.Text.Json;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services.Byok;
using Microsoft.Extensions.AI;

namespace Lumi.Services.ImageGeneration;

/// <summary>
/// Lumi's <c>image_generation</c> for chats that can't use GitHub Copilot's: the same name, description and
/// arguments as Copilot's native tool, so the model sees one tool either way, backed by the user's own account.
/// Its result has Copilot's shape too (<c>saved_to</c> lines and the image), so the transcript, the Library and
/// the guidance after it don't tell the two apart.
/// </summary>
internal sealed class AccountImageGenerationTool : AIFunction
{
    /// <summary>Copilot's own description of its native tool (runtime 1.0.105), word for word.</summary>
    internal const string NativeDescription =
        "Generates or edits one PNG. Only invoke this tool when the user explicitly asks you to create or edit an image. Include input_images only when the user asks to edit an image or use it as a reference for a new image, otherwise omit them. Identify each input by its image_id or file path; never include unrelated attachments.\n\n"
        + "Omit size and quality unless explicitly requested. Sends the prompt and selected images to a separately authorized image model; may consume credits. Do not automatically retry failures, cancellations, or timeouts.\n\n"
        + "This tool is not for image inspection. Use text, Mermaid, or SVG for diagrams unless a rendered image is requested.";

    /// <summary>Copilot's own input schema for its native tool (runtime 1.0.105).</summary>
    internal const string NativeSchema = """
        {"type":"object","properties":{"prompt":{"type":"string","minLength":1,"maxLength":32000,"description":"Complete image description or edit instructions."},"input_images":{"type":"array","minItems":1,"maxItems":16,"items":{"oneOf":[{"type":"object","properties":{"image_id":{"type":"string"}},"required":["image_id"],"additionalProperties":false},{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}]}},"size":{"type":"string","enum":["auto","1024x1024","1536x1024","1024x1536"]},"quality":{"type":"string","enum":["auto","low","medium","high"],"description":"Higher quality may cost more."}},"required":["prompt"],"additionalProperties":false}
        """;

    private static readonly JsonElement Schema = JsonDocument.Parse(NativeSchema).RootElement.Clone();
    private static readonly ConcurrentDictionary<string, string> DiscoveredModels = new(StringComparer.Ordinal);

    private readonly ByokEndpoint _account;
    private readonly string? _model;
    private readonly string _outputDirectory;
    private readonly OpenAiImageClient _client;
    private readonly ISecureKeyStore? _keyStore;
    private readonly Action<string, string>? _onSaved;

    /// <param name="onSaved">Told the tool call id and path of each image saved, before the call returns.</param>
    public AccountImageGenerationTool(
        ByokEndpoint account,
        string? model,
        string outputDirectory,
        OpenAiImageClient? client = null,
        ISecureKeyStore? keyStore = null,
        Action<string, string>? onSaved = null)
    {
        _onSaved = onSaved;
        _account = account;
        _model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        _outputDirectory = outputDirectory;
        _client = client ?? OpenAiImageClient.Shared;
        _keyStore = keyStore;
    }

    public override string Name => GeneratedImageResults.ToolName;
    public override string Description => NativeDescription;
    public override JsonElement JsonSchema => Schema;

    // Copilot keeps its native tool out of a session that runs this one, but says so explicitly.
    public override IReadOnlyDictionary<string, object?> AdditionalProperties { get; } =
        new Dictionary<string, object?> { ["is_override"] = true };

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        try
        {
            var prompt = ReadString(arguments, "prompt");
            if (string.IsNullOrWhiteSpace(prompt))
                return Failure("A prompt is required.");

            var inputImages = ReadInputImages(arguments);
            var connection = ImageAccountConnection.From(_account, _keyStore);
            var model = _model ?? await ResolveModelAsync(connection, cancellationToken).ConfigureAwait(false);
            var bytes = await _client.CreateAsync(
                connection, model, prompt, ReadString(arguments, "size"), ReadString(arguments, "quality"),
                inputImages, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(_outputDirectory);
            var path = Path.Combine(_outputDirectory, $"image-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.png");
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            // A tool Lumi runs completes without the runtime's result event, so it reports its own images.
            if (arguments.Context?.TryGetValue(typeof(ToolInvocation), out var context) == true
                && context is ToolInvocation { ToolCallId: { Length: > 0 } toolCallId })
                _onSaved?.Invoke(toolCallId, path);

            return new ToolResultAIContent(new ToolResultObject
            {
                ResultType = "success",
                TextResultForLlm = $"Image generated successfully.\nmodel: {model}\nsaved_to: {path}",
                BinaryResultsForLlm =
                [
                    new ToolBinaryResult
                    {
                        Type = ToolBinaryResultType.Image,
                        MimeType = OpenAiImageClient.MimeTypeOf(path),
                        Data = Convert.ToBase64String(bytes),
                    }
                ],
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is ImageAccountException or HttpRequestException or IOException
                                          or InvalidOperationException or UnauthorizedAccessException or TaskCanceledException)
        {
            return Failure(error is TaskCanceledException ? "The image account didn't answer in time." : error.Message);
        }
    }

    private async Task<string> ResolveModelAsync(ImageAccountConnection connection, CancellationToken cancellationToken)
    {
        if (DiscoveredModels.TryGetValue(_account.Id, out var known))
            return known;

        // Listed newest first; on Foundry, already narrowed to image deployments whose names are the account's own.
        var models = await _client.ListImageModelsAsync(connection, cancellationToken).ConfigureAwait(false);
        var model = models.FirstOrDefault()
                    ?? throw new ImageAccountException(
                        $"\"{_account.Name}\" doesn't list an image model. Choose one in Settings → AI & Models → Image generation.");
        DiscoveredModels[_account.Id] = model;
        return model;
    }

    /// <summary>Forgets the model picked for an account, after its settings change.</summary>
    internal static void ForgetDiscoveredModel(string accountId) => DiscoveredModels.TryRemove(accountId, out _);

    private static List<string> ReadInputImages(AIFunctionArguments arguments)
    {
        var paths = new List<string>();
        if (!arguments.TryGetValue("input_images", out var raw) || raw is not JsonElement { ValueKind: JsonValueKind.Array } element)
            return paths;

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            if (item.TryGetProperty("path", out var pathValue) && pathValue.GetString() is { Length: > 0 } path)
            {
                if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
                    throw new ImageAccountException($"The input image \"{path}\" doesn't exist. Give its absolute path.");
                paths.Add(Path.GetFullPath(path));
            }
            else if (item.TryGetProperty("image_id", out _))
            {
                throw new ImageAccountException("This image account takes input images by their file path. Pass {\"path\": \"<absolute path>\"} instead of image_id.");
            }
        }

        return paths;
    }

    private static string? ReadString(AIFunctionArguments arguments, string name)
        => arguments.TryGetValue(name, out var value) ? value switch
        {
            null => null,
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            JsonElement json => json.GetRawText(),
            _ => value.ToString(),
        } : null;

    private static ToolResultAIContent Failure(string message)
        => new(new ToolResultObject
        {
            ResultType = "failure",
            TextResultForLlm = "Error: Image generation failed. " + message,
            Error = message,
        });
}
