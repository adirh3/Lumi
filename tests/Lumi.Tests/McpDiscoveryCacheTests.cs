using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class McpDiscoveryCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lumi-discovery-cache-" + Guid.NewGuid().ToString("N"));
    private static JsonElement Client => Parse("""{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}""");
    private static JsonElement Initialize => Parse("""{"protocolVersion":"2025-06-18","capabilities":{"tools":{}},"serverInfo":{"name":"test","version":"1"},"instructions":"Use carefully"}""");
    private static JsonElement Tools => Parse("""{"tools":[{"name":"lookup","description":"Exact description","inputSchema":{"type":"object"},"outputSchema":{"type":"object"},"annotations":{"readOnlyHint":true}}]}""");

    [Fact]
    public void RoundTrip_PreservesAllDiscoveryMetadataWithoutChangingDiscoveryTimestamp()
    {
        var cache = new McpDiscoveryCache(_root);
        cache.Write("entry", new(Initialize, Tools));
        var before = File.ReadAllText(Path.Combine(_root, "entry.json"));
        var restored = new McpDiscoveryCache(_root).Read("entry", Client);

        Assert.NotNull(restored);
        Assert.True(JsonElement.DeepEquals(Initialize, restored.InitializeResult));
        Assert.True(JsonElement.DeepEquals(Tools, restored.ToolsResult));
        Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "entry.json")));
    }

    [Theory]
    [InlineData(-24 * 365)]
    [InlineData(-25)]
    [InlineData(1)]
    public void DiscoveryAgeAndClockChanges_DoNotInvalidateSnapshots(int hours)
    {
        var cache = new McpDiscoveryCache(_root);
        cache.Write("entry", new(Initialize, Tools));
        var path = Path.Combine(_root, "entry.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        document["createdAtUtc"] = DateTimeOffset.UtcNow.AddHours(hours);
        File.WriteAllText(path, document.ToJsonString());
        var before = File.ReadAllText(path);
        var snapshot = cache.Read("entry", Client);
        Assert.NotNull(snapshot);
        Assert.True(JsonElement.DeepEquals(Tools, snapshot.ToolsResult));
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"version":"invalid"}""")]
    public void CorruptEntries_AreCacheMisses(string content)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "entry.json"), content);
        Assert.Null(new McpDiscoveryCache(_root).Read("entry", Client));
    }

    [Fact]
    public void OversizedEntry_IsNotReplayed()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "entry.json"), new string(' ', 4 * 1024 * 1024 + 1));
        Assert.Null(new McpDiscoveryCache(_root).Read("entry", Client));
    }

    [Fact]
    public void Invalidation_PreventsLateDiscoveryFromResurrectingEntry()
    {
        var cache = new McpDiscoveryCache(_root);
        cache.Write("entry", new(Initialize, Tools));
        var revision = cache.GetRevision("entry");
        cache.Invalidate("entry");
        cache.Write("entry", new(Initialize, Tools), revision);
        Assert.Null(cache.Read("entry", Client));
        Assert.False(File.Exists(Path.Combine(_root, "entry.json")));

        cache.Write("entry", new(Initialize, Tools), cache.GetRevision("entry"));
        Assert.NotNull(cache.Read("entry", Client));
    }

    [Fact]
    public void UnwritableDirectory_DoesNotFailLiveDiscovery()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(path, "occupied");
        var cache = new McpDiscoveryCache(path);
        cache.Write("entry", new(Initialize, Tools));
        Assert.Null(cache.Read("entry", Client));
        cache.Invalidate("entry");
    }

    [Theory]
    [InlineData("""{"tools":{},"resources":{}}""")]
    [InlineData("""{"tools":{},"prompts":{}}""")]
    [InlineData("""{"tools":{},"tasks":{}}""")]
    [InlineData("""{"tools":{},"experimental":{}}""")]
    [InlineData("""{"tools":{"futureCapability":false}}""")]
    public void UnsupportedCapabilities_AreNotCacheable(string capabilities)
    {
        var initialize = JsonNode.Parse(Initialize.GetRawText())!;
        initialize["capabilities"] = JsonNode.Parse(capabilities);
        Assert.False(McpDiscoveryCache.IsCacheable(Client, Parse(initialize.ToJsonString()), Tools));
    }

    [Fact]
    public void NotificationCapability_IsEligibleButProxyDoesNotPromiseNotifications()
    {
        var initialize = JsonNode.Parse(Initialize.GetRawText())!;
        initialize["capabilities"]!["tools"]!["listChanged"] = true;
        var source = Parse(initialize.ToJsonString());
        Assert.True(McpDiscoveryCache.IsCacheable(Client, source, Tools));
        var advertised = McpDiscoveryCache.CreateProxyInitializeResult(source);
        Assert.False(advertised.GetProperty("capabilities").GetProperty("tools").GetProperty("listChanged").GetBoolean());
        Assert.True(source.GetProperty("capabilities").GetProperty("tools").GetProperty("listChanged").GetBoolean());
        Assert.True(McpDiscoveryCache.HasCompatibleInitialization(source, advertised));
        Assert.Equal("Use carefully", advertised.GetProperty("instructions").GetString());
    }

    [Fact]
    public void ExplicitRefresh_RemovesSnapshotsAndRejectsInFlightWritesButAllowsNewDiscovery()
    {
        var cache = new McpDiscoveryCache(_root);
        cache.Write("one", new(Initialize, Tools));
        cache.Write("two", new(Initialize, Tools));
        var revision = cache.GetRevision("one");
        cache.Clear();
        cache.Write("one", new(Initialize, Tools), revision);
        cache.Write("new-in-flight-key", new(Initialize, Tools), revision);
        Assert.Empty(Directory.GetFiles(_root));
        Assert.Null(cache.Read("one", Client));

        cache.Write("one", new(Initialize, Tools), cache.GetRevision("one"));
        Assert.NotNull(cache.Read("one", Client));
    }

    [Fact]
    public void KeyInvalidation_IsIsolatedWhileExplicitRefreshInvalidatesEveryKey()
    {
        var cache = new McpDiscoveryCache(_root);
        var first = cache.GetRevision("first");
        var second = cache.GetRevision("second");
        var refresh = cache.RefreshRevision;
        cache.Invalidate("second");

        Assert.Equal(first, cache.GetRevision("first"));
        Assert.NotEqual(second, cache.GetRevision("second"));
        Assert.Equal(refresh, cache.RefreshRevision);
        cache.Write("first", new(Initialize, Tools), first);
        cache.Write("second", new(Initialize, Tools), second);
        Assert.NotNull(cache.Read("first", Client));
        Assert.Null(cache.Read("second", Client));

        second = cache.GetRevision("second");
        cache.Clear();
        Assert.NotEqual(first, cache.GetRevision("first"));
        Assert.NotEqual(second, cache.GetRevision("second"));
        Assert.NotEqual(refresh, cache.RefreshRevision);
        cache.Write("first", new(Initialize, Tools), first);
        cache.Write("second", new(Initialize, Tools), second);
        Assert.Null(cache.Read("first", Client));
        Assert.Null(cache.Read("second", Client));

        cache.Write("second", new(Initialize, Tools), cache.GetRevision("second"));
        Assert.NotNull(cache.Read("second", Client));
    }

    [Fact]
    public void InitializationCompatibility_IgnoresServerVersionButNotIdentityOrInstructions()
    {
        var changed = JsonNode.Parse(Initialize.GetRawText())!;
        changed["serverInfo"]!["version"] = "2";
        Assert.True(McpDiscoveryCache.HasCompatibleInitialization(Initialize, Parse(changed.ToJsonString())));
        changed["serverInfo"]!["name"] = "different-server";
        Assert.False(McpDiscoveryCache.HasCompatibleInitialization(Initialize, Parse(changed.ToJsonString())));
        changed["serverInfo"]!["name"] = "test";
        changed["instructions"] = "Different behavior";
        Assert.False(McpDiscoveryCache.HasCompatibleInitialization(Initialize, Parse(changed.ToJsonString())));
        changed["instructions"] = "Use carefully";
        changed["protocolVersion"] = "2025-11-25";
        Assert.False(McpDiscoveryCache.HasCompatibleInitialization(Initialize, Parse(changed.ToJsonString())));
    }

    [Fact]
    public void ToolCompatibility_IgnoresOrderingAndUnrelatedToolsButNotTheSelectedContract()
    {
        var original = JsonNode.Parse(Tools.GetRawText())!;
        var originalTool = original["tools"]![0]!;
        var reordered = new JsonObject
        {
            ["tools"] = new JsonArray(
                JsonNode.Parse("""{"name":"new_tool","inputSchema":{"type":"object"}}"""),
                originalTool.DeepClone())
        };
        Assert.True(McpDiscoveryCache.IsToolCompatible(Tools, Parse(reordered.ToJsonString()), "lookup"));
        Assert.False(McpDiscoveryCache.IsToolCompatible(Tools, Parse(reordered.ToJsonString()), "LOOKUP"));
        Assert.False(McpDiscoveryCache.IsToolCompatible(Tools, Parse(reordered.ToJsonString()), "new_tool"));
        reordered["tools"]![1]!["description"] = "Changed behavior";
        Assert.False(McpDiscoveryCache.IsToolCompatible(Tools, Parse(reordered.ToJsonString()), "lookup"));
    }

    [Theory]
    [InlineData("inputSchema", """{"type":"object","required":["id"]}""")]
    [InlineData("outputSchema", """{"type":"string"}""")]
    [InlineData("annotations", """{"readOnlyHint":false}""")]
    [InlineData("_meta", """{"scope":"different"}""")]
    public void ToolCompatibility_PreservesFullSelectedToolMetadata(string field, string value)
    {
        var changed = JsonNode.Parse(Tools.GetRawText())!;
        changed["tools"]![0]![field] = JsonNode.Parse(value);
        Assert.False(McpDiscoveryCache.IsToolCompatible(Tools, Parse(changed.ToJsonString()), "lookup"));
    }

    [Fact]
    public void EmptyCompleteCatalog_IsCacheableButPaginationAndContextAreNot()
    {
        Assert.True(McpDiscoveryCache.IsCacheable(Client, Initialize, Parse("""{"tools":[]}""")));
        Assert.False(McpDiscoveryCache.IsCacheable(Client, Initialize, Parse("""{"tools":[],"nextCursor":"session-token"}""")));
        Assert.False(McpDiscoveryCache.IsPlainListRequest(Parse("""{"params":{"cursor":"session-token"}}""")));
        Assert.False(McpDiscoveryCache.IsPlainListRequest(Parse("""{"params":{"_meta":{"context":"different"}}}""")));
    }

    [Theory]
    [InlineData("""{"params":{"_meta":{"progressToken":0}}}""")]
    [InlineData("""{"params":{"_meta":{"progressToken":"different-request"}}}""")]
    public void ProgressCorrelation_DoesNotSelectADifferentCatalog(string request)
        => Assert.True(McpDiscoveryCache.IsPlainListRequest(Parse(request)));

    [Fact]
    public void ActualCopilotClientProfile_IsEligibleWithoutPretendingCallbacksAreSupported()
    {
        var client = Parse("""
            {"protocolVersion":"2025-11-25","capabilities":{"extensions":{"io.modelcontextprotocol/tasks":{},"io.modelcontextprotocol/ui":{"mimeTypes":["text/html;profile=mcp-app"]}},"sampling":{}},"clientInfo":{"name":"github-copilot-developer","version":"1.0.78"}}
            """);
        Assert.True(McpDiscoveryCache.IsCacheableClient(client));
        var initialize = JsonNode.Parse(Initialize.GetRawText())!;
        initialize["protocolVersion"] = "2025-11-25";
        Assert.True(McpDiscoveryCache.IsCacheable(client, Parse(initialize.ToJsonString()), Tools));
    }

    [Theory]
    [InlineData("""{"roots":{"listChanged":true}}""")]
    [InlineData("""{"elicitation":{}}""")]
    [InlineData("""{"extensions":{"unknown/context":{}}}""")]
    [InlineData("""{"sampling":{"tools":{}}}""")]
    public void ContextBearingOrUnknownClients_RemainLive(string capabilities)
    {
        var client = JsonNode.Parse(Client.GetRawText())!;
        client["capabilities"] = JsonNode.Parse(capabilities);
        Assert.False(McpDiscoveryCache.IsCacheableClient(Parse(client.ToJsonString())));
    }

    [Fact]
    public void Key_TracksClientEnvironmentAndDirectFilesWithoutPersistingSecrets()
    {
        Directory.CreateDirectory(_root);
        var script = Path.Combine(_root, "server.ps1");
        File.WriteAllText(script, "one");
        var start = new ProcessStartInfo("test-mcp") { WorkingDirectory = _root };
        start.ArgumentList.Add(script);
        start.Environment["MCP_TEST_SECRET"] = "secret-one";
        var original = McpDiscoveryCache.CreateKey("config", Client, start);
        var reordered = Parse("""{"clientInfo":{"version":"1","name":"test"},"capabilities":{},"protocolVersion":"2025-06-18"}""");
        Assert.Equal(original, McpDiscoveryCache.CreateKey("config", reordered, start));
        start.Environment["MCP_TEST_SECRET"] = "secret-two";
        var changedEnvironment = McpDiscoveryCache.CreateKey("config", Client, start);
        Assert.NotEqual(original, changedEnvironment);
        File.WriteAllText(script, "different script");
        Assert.NotEqual(changedEnvironment, McpDiscoveryCache.CreateKey("config", Client, start));
        Assert.Equal(64, original.Length);
        Assert.DoesNotContain("secret", original);
    }

    [SkippableFact]
    public void WindowsRelativeExecutable_UsesParentDirectoryNotBackendDirectory()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var executable = Environment.ProcessPath!;
        var relative = new ProcessStartInfo(Path.GetRelativePath(Environment.CurrentDirectory, executable))
            { WorkingDirectory = _root };
        var absolute = new ProcessStartInfo(executable) { WorkingDirectory = _root };
        Assert.Equal(
            McpDiscoveryCache.CreateKey("config", Client, absolute),
            McpDiscoveryCache.CreateKey("config", Client, relative));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
