using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;

namespace Lumi.Services;

internal static class NativeBrowserLogic
{
    internal const string EncodingProbe = "lumi-native-probe";

    internal static bool IsEmbeddedBrowserAvailable =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOSVersionAtLeast(14);

    internal static string WrapScript(string script) =>
        "(function(){try{var r=(" + script + ");return JSON.stringify(r===undefined?null:r);}" +
        "catch(e){return \"null\";}})()";

    internal static bool TryReadString(string? json, out string? value)
    {
        value = null;
        if (string.IsNullOrEmpty(json))
            return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            switch (document.RootElement.ValueKind)
            {
                case JsonValueKind.String:
                    value = document.RootElement.GetString();
                    return true;
                case JsonValueKind.Null:
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool DetectDoubleEncoding(string? raw)
    {
        var expected = "\"" + EncodingProbe + "\"";
        if (raw == expected)
            return false;
        if (TryReadString(raw, out var decoded) && decoded == expected)
            return true;
        throw new InvalidOperationException("The native browser did not return a valid JavaScript initialization result.");
    }

    internal static string NormalizeResult(string? raw, bool doubleEncoded)
    {
        if (string.IsNullOrEmpty(raw))
            return "null";
        return doubleEncoded && TryReadString(raw, out var decoded) ? decoded ?? "null" : raw;
    }

    internal static string ReadDisplayResult(string json) =>
        TryReadString(json, out var value) ? value ?? "null" : json;

    internal static Guid GetAppleDataStoreId(string folder)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)).AsSpan(0, 16));
    }

    internal static Rect? GetOverlayBounds(Canvas layer, Rect physicalBounds, double rasterizationScale)
    {
        var topLevel = TopLevel.GetTopLevel(layer);
        if (topLevel is null)
            return null;
        var origin = layer.TranslatePoint(default, topLevel);
        var horizontal = layer.TranslatePoint(new Point(1, 0), topLevel);
        var vertical = layer.TranslatePoint(new Point(0, 1), topLevel);
        if (origin is null || horizontal is null || vertical is null)
            return null;
        var scaleX = horizontal.Value.X - origin.Value.X;
        var scaleY = vertical.Value.Y - origin.Value.Y;
        if (scaleX <= 0 || scaleY <= 0)
            return null;
        return GetOverlayBounds(physicalBounds, topLevel.RenderScaling, origin.Value, scaleX, scaleY);
    }

    internal static Rect GetOverlayBounds(
        Rect physicalBounds, double renderScaling, Point origin, double scaleX = 1, double scaleY = 1) =>
        new((physicalBounds.X / renderScaling - origin.X) / scaleX,
            (physicalBounds.Y / renderScaling - origin.Y) / scaleY,
            physicalBounds.Width / renderScaling / scaleX,
            physicalBounds.Height / renderScaling / scaleY);

    internal static Uri ParseUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            return parsed;
        if (Uri.TryCreate("https://" + url, UriKind.Absolute, out parsed))
            return parsed;
        throw new ArgumentException("The browser needs a valid absolute URL.", nameof(url));
    }
}
