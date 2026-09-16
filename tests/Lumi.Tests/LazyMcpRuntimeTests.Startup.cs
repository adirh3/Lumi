using System.Text.Json;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed partial class LazyMcpRuntimeTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServerDiscover_DoesNotStartOrBindBackend_BeforeInitializeFallback(bool lazy)
    {
        using var fake = new FakeMcp();
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        using var first = runtime.AcquireSessionRegistration(fake.Definition(lazy));
        using var second = runtime.AcquireSessionRegistration(fake.Definition(lazy));

        var discovery = await fake.RequestAsync(first.ServerConfig.Url,
            "server/discover", FakeMcp.CopilotServerDiscover(), "discover");
        AssertError(discovery);
        Assert.Equal("discover", discovery.GetProperty("id").GetString());
        Assert.Equal(-32601, discovery.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Empty(fake.Starts);

        foreach (var registration in new[] { first, second })
        {
            var url = registration.ServerConfig.Url;
            AssertSuccess(await fake.InitializeAsync(url, FakeMcp.CopilotCliInitialize()));
            AssertSuccess(await fake.RequestAsync(url, "tools/list"));
            AssertSuccess(await fake.RequestAsync(url, "tools/call",
                new { name = "echo", arguments = new { value = "fallback-works" } }));
            var subsequentDiscovery = await fake.RequestAsync(url,
                "server/discover", FakeMcp.CopilotServerDiscover());
            Assert.Equal(-32601, subsequentDiscovery.GetProperty("error").GetProperty("code").GetInt32());
        }

        Assert.Empty(fake.Messages("server/discover"));
        Assert.Single(fake.Starts);
        var initialize = Assert.Single(fake.Messages("initialize"));
        AssertJsonEqual(JsonSerializer.SerializeToElement(FakeMcp.CopilotCliInitialize()),
            initialize.GetProperty("params"));
        Assert.Equal(2, fake.Messages("tools/call").Length);
    }

    [SkippableFact]
    public async Task ServerDiscover_PreservesWarmDiscovery_WithoutStartingBackend()
    {
        using var fake = new FakeMcp();
        await using (var cold = new McpProxyRuntime(fake.CacheDirectory))
        {
            var remote = cold.Register(fake.Definition());
            AssertSuccess(await fake.InitializeAsync(remote.Url, FakeMcp.CopilotCliInitialize()));
            AssertSuccess(await fake.RequestAsync(remote.Url, "tools/list"));
        }

        await using var warm = new McpProxyRuntime(fake.CacheDirectory);
        var cached = warm.Register(fake.Definition());
        var discovery = await fake.RequestAsync(cached.Url, "server/discover", FakeMcp.CopilotServerDiscover());
        Assert.Equal(-32601, discovery.GetProperty("error").GetProperty("code").GetInt32());
        AssertSuccess(await fake.InitializeAsync(cached.Url, FakeMcp.CopilotCliInitialize()));
        AssertSuccess(await fake.RequestAsync(cached.Url, "tools/list"));
        Assert.Single(fake.Starts);
        Assert.Empty(fake.Messages("server/discover"));

        AssertSuccess(await fake.RequestAsync(cached.Url, "tools/call",
            new { name = "echo", arguments = new { value = "activate" } }));
        Assert.Equal(2, fake.Starts.Length);
        Assert.Single(fake.Messages("tools/call"));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupDiagnostics_DoNotPreventInitializeOrToolExecution(bool lazy)
    {
        using var fake = new FakeMcp("startup-logs");
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        var remote = runtime.Register(fake.Definition(lazy));
        AssertSuccess(await fake.InitializeAsync(remote.Url));
        AssertSuccess(await fake.RequestAsync(remote.Url, "tools/list"));
        AssertSuccess(await fake.RequestAsync(remote.Url, "tools/call",
            new { name = "echo", arguments = new { value = "startup-works" } }));
        Assert.Single(fake.Starts);
        Assert.Single(fake.Messages("tools/call"));
    }

    [SkippableFact]
    public async Task MalformedJsonRpcOutput_RemainsAnExplicitFailure()
    {
        using var fake = new FakeMcp("malformed-stdout");
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        var remote = runtime.Register(fake.Definition());
        var initialize = await fake.InitializeAsync(remote.Url);
        AssertError(initialize);
        var message = initialize.GetProperty("error").GetProperty("message").GetString();
        Assert.Contains("non-JSON output", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("""{"jsonrpc":"2.0","id":""", message, StringComparison.Ordinal);
        Assert.Empty(fake.Messages("tools/call"));
    }

    [SkippableFact]
    public async Task StartupDiagnostics_WithoutHandshake_ReturnsTimeoutWithRedactedOutput()
    {
        using var fake = new FakeMcp("startup-logs-timeout");
        var definition = fake.Definition();
        definition.Config.Timeout = 4_000;
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        var remote = runtime.Register(definition);
        var initialize = await fake.InitializeAsync(remote.Url);
        AssertError(initialize);
        var message = initialize.GetProperty("error").GetProperty("message").GetString();
        Assert.Contains("timed out", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Credential provider ready", message, StringComparison.Ordinal);
        Assert.Contains("token=[redacted]", message, StringComparison.Ordinal);
        Assert.DoesNotContain("startup-secret", message, StringComparison.Ordinal);
        Assert.Empty(fake.Messages("tools/call"));
    }
}
