using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Lumi.Services.Capabilities;

/// <summary>
/// Reads the markdown body of a capability the Copilot runtime already located.
/// </summary>
/// <remarks>
/// Capability <em>discovery</em> belongs entirely to the SDK — nothing here searches for
/// capabilities. This only opens the single path the runtime reported for a descriptor, so the
/// preview can render a skill the model has not invoked yet (the discovery RPCs return metadata,
/// not bodies). Builtin, plugin and remote capabilities have no reachable file and simply return
/// false.
/// </remarks>
public static class CapabilityContent
{
    public static bool TryReadBody(CapabilityDescriptor capability, [NotNullWhen(true)] out string? body)
    {
        ArgumentNullException.ThrowIfNull(capability);
        body = null;

        var path = capability.SourcePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        try
        {
            var text = File.ReadAllText(path);
            body = StripFrontMatter(text);
            return !string.IsNullOrWhiteSpace(body);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Removes a leading YAML front-matter block so only the readable body renders.</summary>
    private static string StripFrontMatter(string content)
    {
        var normalized = content.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            return content.Trim();

        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        return end < 0 ? content.Trim() : normalized[(end + 5)..].Trim();
    }
}
