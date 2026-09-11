using System.Text.Json;
using System.Text.Json.Nodes;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed partial class LazyMcpRuntimeTests
{
    [SkippableTheory]
    [InlineData("static", false, true)]
    [InlineData("tools-empty", true, false)]
    [InlineData("dynamic", false, true)]
    public async Task WarmDiscovery_ReplaysAcrossRuntimeLifetimes_AndConcurrentCallsStartOnce(
        string behavior, bool omitListParams, bool copilotClient)
    {
        using var fake = new FakeMcp(behavior);
        var client = copilotClient ? FakeMcp.CopilotClientInitialize() : FakeMcp.ClientInitialize();
        object? listParameters = omitListParams ? null
            : copilotClient ? new { _meta = new { progressToken = 0 } } : new { };
        JsonElement coldInitialize;
        JsonElement coldTools;
        await using (var cold = new McpProxyRuntime(fake.CacheDirectory))
        {
            var remote = cold.Register(fake.Definition());
            Assert.Empty(fake.Starts);
            coldInitialize = await fake.InitializeAsync(remote.Url, client);
            coldTools = await fake.RequestAsync(remote.Url, "tools/list", listParameters);
            AssertSuccess(coldInitialize);
            AssertSuccess(coldTools);
            Assert.Single(fake.Starts);
        }

        var coldMessageCount = fake.Messages().Length;
        await using var warm = new McpProxyRuntime(fake.CacheDirectory);
        var cached = warm.Register(fake.Definition());
        var initialize = await fake.InitializeAsync(cached.Url, client);
        var tools = await fake.RequestAsync(cached.Url, "tools/list", listParameters);
        await fake.NotifyInitializedAsync(cached.Url);
        AssertSuccess(await fake.RequestAsync(cached.Url, "ping"));
        AssertJsonEqual(coldInitialize.GetProperty("result"), initialize.GetProperty("result"));
        AssertJsonEqual(coldTools.GetProperty("result"), tools.GetProperty("result"));
        Assert.Single(fake.Starts);
        Assert.Equal(coldMessageCount, fake.Messages().Length);

        var arguments = Enumerable.Range(0, 6)
            .Select(i => new { value = $"marker-{i}", nested = new { index = i, enabled = true } })
            .ToArray();
        var responses = await Task.WhenAll(arguments.Select((args, i) =>
            fake.RequestAsync(cached.Url, "tools/call", new { name = "echo", arguments = args }, $"call-{i}")));

        Assert.Equal(2, fake.Starts.Length);
        var activation = fake.Messages().Skip(coldMessageCount).ToArray();
        Assert.Equal(
            ["initialize", "notifications/initialized", "tools/list"],
            activation.Take(3).Select(m => m.GetProperty("method").GetString()));
        AssertJsonEqual(JsonSerializer.SerializeToElement(client), activation[0].GetProperty("params"));
        Assert.Single(activation, m => m.GetProperty("method").GetString() == "initialize");
        Assert.Single(activation, m => m.GetProperty("method").GetString() == "tools/list");
        var forwarded = activation.Where(m => m.GetProperty("method").GetString() == "tools/call").ToArray();
        Assert.Equal(arguments.Length, forwarded.Length);
        for (var i = 0; i < arguments.Length; i++)
        {
            Assert.Equal($"call-{i}", responses[i].GetProperty("id").GetString());
            AssertSuccess(responses[i]);
            var echoed = JsonSerializer.Deserialize<JsonElement>(
                responses[i].GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
            AssertJsonEqual(JsonSerializer.SerializeToElement(arguments[i]), echoed);
            var sent = Assert.Single(forwarded, m =>
                m.GetProperty("params").GetProperty("arguments").GetProperty("value").GetString() == arguments[i].value);
            AssertJsonEqual(JsonSerializer.SerializeToElement(arguments[i]), sent.GetProperty("params").GetProperty("arguments"));
        }
    }

    [SkippableFact]
    public async Task DisabledLazyInitialization_DoesNotReplayAnExistingCache()
    {
        using var fake = new FakeMcp();
        await fake.PrimeAsync();

        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        var remote = runtime.Register(fake.Definition(lazy: false));
        AssertSuccess(await fake.InitializeAsync(remote.Url));
        Assert.Equal(2, fake.Starts.Length);
        AssertSuccess(await fake.RequestAsync(remote.Url, "tools/list"));
        Assert.Equal(2, fake.Messages("tools/list").Length);
    }

    [SkippableTheory]
    [InlineData("paginated")]
    [InlineData("resources")]
    [InlineData("client-roots")]
    [InlineData("client-unknown-extension")]
    [InlineData("list-error")]
    public async Task IneligibleDiscovery_DoesNotPopulatePersistentCache(string behavior)
    {
        using var fake = new FakeMcp(behavior);
        var client = FakeMcp.ClientInitialize();
        if (behavior == "client-roots")
            client["capabilities"] = JsonNode.Parse("""{"roots":{"listChanged":true}}""");
        else if (behavior == "client-unknown-extension")
            client["capabilities"] = JsonNode.Parse("""{"extensions":{"test/unknown":{}}}""");
        for (var lifetime = 0; lifetime < 2; lifetime++)
        {
            await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
            var remote = runtime.Register(fake.Definition());
            AssertSuccess(await fake.InitializeAsync(remote.Url, client));
            Assert.Equal(lifetime + 1, fake.Starts.Length);
            var tools = await fake.RequestAsync(remote.Url, "tools/list");
            if (behavior == "list-error")
                AssertError(tools);
            else
                AssertSuccess(tools);
        }
        Assert.Equal(2, fake.Messages("tools/list").Length);
    }

    [SkippableFact]
    public async Task CursorList_NeitherPopulatesCacheNorReplaysTheDefaultList()
    {
        using var fake = new FakeMcp();
        await using (var cold = new McpProxyRuntime(fake.CacheDirectory))
        {
            var remote = cold.Register(fake.Definition());
            AssertSuccess(await fake.InitializeAsync(remote.Url));
            AssertSuccess(await fake.RequestAsync(remote.Url, "tools/list", new { cursor = "page-2" }));
        }

        await using (var next = new McpProxyRuntime(fake.CacheDirectory))
        {
            var remote = next.Register(fake.Definition());
            AssertSuccess(await fake.InitializeAsync(remote.Url));
            Assert.Equal(2, fake.Starts.Length);
            AssertSuccess(await fake.RequestAsync(remote.Url, "tools/list", new { }));
        }

        await using var warm = new McpProxyRuntime(fake.CacheDirectory);
        var cached = warm.Register(fake.Definition());
        AssertSuccess(await fake.InitializeAsync(cached.Url));
        Assert.Equal(2, fake.Starts.Length);
        AssertSuccess(await fake.RequestAsync(cached.Url, "tools/list", new { cursor = "page-3" }));
        Assert.Equal(3, fake.Starts.Length);
        Assert.Equal("page-3", fake.Messages("tools/list").Last().GetProperty("params").GetProperty("cursor").GetString());
    }

    [SkippableTheory]
    [InlineData("command")]
    [InlineData("args")]
    [InlineData("cwd")]
    [InlineData("env")]
    [InlineData("tools")]
    [InlineData("clientInfo")]
    [InlineData("protocolVersion")]
    public async Task ChangedConfigurationOrClientIdentity_PreventsReplay(string changed)
    {
        using var fake = new FakeMcp();
        await fake.PrimeAsync();
        var definition = fake.Definition();
        var client = FakeMcp.ClientInitialize();
        switch (changed)
        {
            case "command":
                definition.Config.Command = Path.Combine(Path.GetDirectoryName(definition.Config.Command)!, ".", "powershell.exe");
                break;
            case "args":
                definition.Config.Args = ["-NonInteractive", .. definition.Config.Args!];
                break;
            case "cwd":
                definition.Config.WorkingDirectory = Directory.CreateDirectory(Path.Combine(fake.Root, "other")).FullName;
                break;
            case "env":
                definition.Config.Env!["MCP_UNUSED_IDENTITY"] = "changed";
                break;
            case "tools":
                definition.Config.Tools = ["echo"];
                break;
            case "clientInfo":
                client["clientInfo"]!["version"] = "2";
                break;
            case "protocolVersion":
                client["protocolVersion"] = "2025-03-26";
                break;
        }

        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        var remote = runtime.Register(definition);
        AssertSuccess(await fake.InitializeAsync(remote.Url, client));
        Assert.Equal(2, fake.Starts.Length);
        AssertSuccess(await fake.RequestAsync(remote.Url, "tools/list"));
        Assert.Equal(2, fake.Messages("tools/list").Length);
    }

    [SkippableTheory]
    [InlineData("description-drift")]
    [InlineData("schema-drift")]
    [InlineData("annotations-drift")]
    [InlineData("output-schema-drift")]
    [InlineData("meta-drift")]
    [InlineData("selected-missing")]
    [InlineData("instructions-drift")]
    public async Task MetadataDrift_IsolatesOldClient_AndFreshClientUsesSameBackend(string behavior)
    {
        using var fake = new FakeMcp();
        await fake.PrimeAsync();
        fake.SetBehavior(behavior);
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        using var stale = runtime.AcquireSessionRegistration(fake.Definition());
        AssertSuccess(await fake.InitializeAsync(stale.ServerConfig.Url));
        var advertised = await fake.RequestAsync(stale.ServerConfig.Url, "tools/list");
        Assert.Single(fake.Starts);
        var call = new { name = "echo", arguments = new { value = "must-not-dispatch" } };
        AssertError(await fake.RequestAsync(stale.ServerConfig.Url, "tools/call", call));
        AssertError(await fake.RequestAsync(stale.ServerConfig.Url, "tools/call", call));
        Assert.Empty(fake.Messages("tools/call"));

        using var fresh = runtime.AcquireSessionRegistration(fake.Definition());
        Assert.NotEqual(stale.ServerConfig.Url, fresh.ServerConfig.Url);
        Assert.Equal(new Uri(stale.ServerConfig.Url).AbsolutePath, new Uri(fresh.ServerConfig.Url).AbsolutePath);
        var initialize = await fake.InitializeAsync(fresh.ServerConfig.Url);
        var discovered = await fake.RequestAsync(fresh.ServerConfig.Url, "tools/list");
        AssertSuccess(initialize);
        AssertSuccess(discovered);
        if (behavior == "instructions-drift")
            Assert.Equal("Changed instructions", initialize.GetProperty("result").GetProperty("instructions").GetString());
        else
            Assert.False(JsonNode.DeepEquals(
                JsonNode.Parse(advertised.GetProperty("result").GetRawText()),
                JsonNode.Parse(discovered.GetProperty("result").GetRawText())));
        var freshCall = behavior == "selected-missing"
            ? new { name = "other", arguments = new { value = "new-client" } } : call;
        AssertSuccess(await fake.RequestAsync(fresh.ServerConfig.Url, "tools/call", freshCall));
        Assert.Single(fake.Messages("tools/call"));
        AssertError(await fake.RequestAsync(stale.ServerConfig.Url, "tools/call", call));
        Assert.Single(fake.Messages("tools/call"));
        AssertJsonEqual(advertised.GetProperty("result"),
            (await fake.RequestAsync(stale.ServerConfig.Url, "tools/list")).GetProperty("result"));
        if (behavior != "instructions-drift")
        {
            AssertSuccess(await fake.RequestAsync(stale.ServerConfig.Url, "tools/call",
                new { name = "other", arguments = new { value = "unchanged" } }));
            Assert.Equal(2, fake.Messages("tools/call").Length);
        }
        Assert.Equal(2, fake.Starts.Length);
        await stale.ReleaseAsync();
        AssertSuccess(await fake.RequestAsync(fresh.ServerConfig.Url, "tools/list"));
        await fresh.ReleaseAsync();
    }

    [SkippableFact]
    public async Task DiscoveryCache_WeeksOldSnapshotReplaysWithoutStartupOrSlidingTimestamp()
    {
        using var fake = new FakeMcp();
        await fake.PrimeAsync();
        var cacheFile = Assert.Single(Directory.GetFiles(fake.CacheDirectory, "*.json"));
        var snapshot = JsonNode.Parse(await File.ReadAllTextAsync(cacheFile))!;
        var createdAt = DateTimeOffset.UtcNow.AddDays(-42);
        snapshot["createdAtUtc"] = createdAt;
        await File.WriteAllTextAsync(cacheFile, snapshot.ToJsonString());

        await using (var warm = new McpProxyRuntime(fake.CacheDirectory))
        {
            var remote = warm.Register(fake.Definition());
            AssertSuccess(await fake.InitializeAsync(remote.Url));
            AssertSuccess(await fake.RequestAsync(remote.Url, "tools/list"));
            AssertSuccess(await fake.RequestAsync(remote.Url, "ping"));
            Assert.Single(fake.Starts);
        }
        var afterReplay = JsonNode.Parse(await File.ReadAllTextAsync(cacheFile))!;
        Assert.Equal(createdAt, afterReplay["createdAtUtc"]!.GetValue<DateTimeOffset>());

        await using var another = new McpProxyRuntime(fake.CacheDirectory);
        AssertSuccess(await fake.InitializeAsync(another.Register(fake.Definition()).Url));
        Assert.Single(fake.Starts);
    }

    [SkippableFact]
    public async Task ColdEagerDiscovery_LearnsCacheForLaterLazyClients()
    {
        using var fake = new FakeMcp("dynamic");
        await fake.PrimeAsync(lazy: false);
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        var remote = runtime.Register(fake.Definition());
        var initialize = await fake.InitializeAsync(remote.Url);
        AssertSuccess(initialize);
        Assert.False(initialize.GetProperty("result").GetProperty("capabilities").GetProperty("tools")
            .GetProperty("listChanged").GetBoolean());
        AssertSuccess(await fake.RequestAsync(remote.Url, "tools/list"));
        Assert.Single(fake.Starts);
        var cache = JsonNode.Parse(await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(fake.CacheDirectory, "*.json"))))!;
        Assert.True(cache["initializeResult"]!["capabilities"]!["tools"]!["listChanged"]!.GetValue<bool>());
    }

    [SkippableFact]
    public async Task DirectCallWithoutAdvertisedTools_CapturesFirstRealCatalog()
    {
        using var fake = new FakeMcp();
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        var remote = runtime.Register(fake.Definition());
        AssertSuccess(await fake.InitializeAsync(remote.Url));
        Assert.Empty(fake.Messages("tools/list"));
        fake.SetBehavior("schema-drift");
        AssertSuccess(await fake.RequestAsync(remote.Url, "tools/call",
            new { name = "echo", arguments = new { value = 42 } }));
        Assert.Single(fake.Messages("tools/list"));
        Assert.Single(fake.Messages("tools/call"));
        var list = await fake.RequestAsync(remote.Url, "tools/list");
        Assert.Equal("integer", list.GetProperty("result").GetProperty("tools")[0]
            .GetProperty("inputSchema").GetProperty("properties").GetProperty("value").GetProperty("type").GetString());
    }
}
