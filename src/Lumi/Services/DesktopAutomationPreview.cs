using System;
using System.Text.Json.Serialization;

namespace Lumi.Services;

/// <summary>An in-process screenshot. Its bytes must not be mutated after publication.</summary>
public sealed record DesktopPreviewFrame(
    [property: JsonIgnore] ReadOnlyMemory<byte> PngBytes,
    int PixelWidth,
    int PixelHeight,
    DateTimeOffset CapturedAt);

/// <summary>A chat-scoped desktop action update; omitting a frame keeps the same target's last capture.</summary>
public sealed record DesktopPreviewUpdate(
    Guid ChatId,
    string WindowKey,
    string WindowTitle,
    string? ProcessName,
    string ActionText,
    string StatusText,
    DateTimeOffset UpdatedAt,
    DesktopPreviewFrame? NewFrame = null);
