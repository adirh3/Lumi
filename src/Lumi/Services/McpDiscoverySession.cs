using System.Text.Json;

namespace Lumi.Services;

/// <summary>A frontend's advertised contract, independent of the shared source process.</summary>
internal sealed class McpDiscoverySession(bool useLazyInitialization)
{
    public bool UseLazyInitialization { get; } = useLazyInitialization;

    // Metadata is accessed under the source's lifecycle lock; registration reads only the epoch.
    public JsonElement? InitializeResult { get; set; }
    public JsonElement? ToolsResult { get; set; }
    public bool LiveOnly { get; set; }
    public long AdvertisedRevision = -1;
    private int _needsRediscovery;

    public bool NeedsRediscovery(long revision)
    {
        var advertised = Volatile.Read(ref AdvertisedRevision);
        return Volatile.Read(ref _needsRediscovery) != 0 || advertised >= 0 && advertised != revision;
    }

    public void MarkNeedsRediscovery() => Interlocked.Exchange(ref _needsRediscovery, 1);
}
