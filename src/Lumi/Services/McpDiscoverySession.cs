using System.Text.Json;

namespace Lumi.Services;

/// <summary>A frontend's advertised contract, independent of the shared source process.</summary>
internal sealed class McpDiscoverySession(bool useLazyInitialization)
{
    private readonly Dictionary<string, JsonElement> _advertisedTools = new(StringComparer.Ordinal);

    public bool UseLazyInitialization { get; } = useLazyInitialization;

    // Metadata is accessed under the source's lifecycle lock; registration reads only the epoch.
    public JsonElement? InitializeResult { get; set; }
    public JsonElement? ToolsResult { get; set; }
    public bool HasAdvertisedCatalog { get; private set; }
    public bool LiveOnly { get; set; }
    public long AdvertisedRevision = -1;
    private int _needsRediscovery;

    public bool NeedsRediscovery(long revision)
    {
        var advertised = Volatile.Read(ref AdvertisedRevision);
        return Volatile.Read(ref _needsRediscovery) != 0 || advertised >= 0 && advertised != revision;
    }

    public void MarkNeedsRediscovery() => Interlocked.Exchange(ref _needsRediscovery, 1);

    public void RecordAdvertisedTools(JsonElement toolsResult)
    {
        HasAdvertisedCatalog = true;
        if (!toolsResult.TryGetProperty("tools", out var tools)
            || tools.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind == JsonValueKind.Object
                && tool.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(name.GetString()))
            {
                _advertisedTools[name.GetString()!] = tool.Clone();
            }
        }
    }

    public bool HasAdvertisedTool(string name)
        => _advertisedTools.ContainsKey(name);

    public bool IsAdvertisedToolCompatible(JsonElement actualToolsResult, string name)
        => _advertisedTools.TryGetValue(name, out var expected)
            && McpDiscoveryCache.TryFindTool(actualToolsResult, name, out var actual)
            && JsonElement.DeepEquals(expected, actual);
}
