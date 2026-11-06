using System;
using System.Text;
using System.Text.Json;
using Lumi.Localization;

namespace Lumi.Services;

/// <summary>How an <c>image_generation</c> step reads in the transcript, whichever route ran it.</summary>
public static partial class ToolDisplayHelper
{
    /// <summary>"Editing image" when the call starts from images, else "Creating image".</summary>
    internal static string GetImageGenerationName(string? argsJson)
        => HasInputImages(argsJson) ? Loc.Tool_EditingImage : Loc.Tool_CreatingImage;

    internal static bool HasInputImages(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return false;
        try
        {
            using var document = JsonDocument.Parse(argsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("input_images", out var images)
                && images.ValueKind == JsonValueKind.Array
                && images.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The prompt's first line, short enough for the step's glance detail.</summary>
    internal static string? SummarizeImagePrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return null;

        var line = prompt.Trim().Split('\n', 2)[0].Trim();
        return line.Length > 90 ? line[..89].TrimEnd() + "\u2026" : line;
    }

    /// <summary>The step's opened fields: its whole prompt, then the size, quality and source images it asked for.</summary>
    private static string? FormatImageGenerationArgs(JsonElement root)
    {
        var text = new StringBuilder();
        if (GetString(root, "prompt") is { Length: > 0 } prompt)
            text.AppendLine($"**{Loc.FieldLabel_Prompt}:** {prompt.Trim()}");
        if (GetString(root, "size") is { Length: > 0 } size && size != "auto")
            text.AppendLine($"**{Loc.FieldLabel_ImageSize}:** {size.Replace("x", " × ", StringComparison.Ordinal)}");
        if (GetString(root, "quality") is { Length: > 0 } quality && quality != "auto")
            text.AppendLine($"**{Loc.FieldLabel_ImageQuality}:** {quality}");
        if (root.TryGetProperty("input_images", out var images) && images.ValueKind == JsonValueKind.Array)
        {
            foreach (var image in images.EnumerateArray())
            {
                var source = image.ValueKind == JsonValueKind.Object
                    ? GetString(image, "path") is { } path ? GetDisplayFileName(path) : GetString(image, "image_id")
                    : null;
                if (!string.IsNullOrWhiteSpace(source))
                    text.AppendLine($"**{Loc.FieldLabel_SourceImage}:** `{source}`");
            }
        }

        return text.Length == 0 ? null : text.ToString().TrimEnd();
    }
}
