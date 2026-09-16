using System.Net;
using System.Text.Json;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed partial class LazyMcpRuntimeTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedStartup_NeverReturnsSuccessfulToolExecution(bool warmCache)
    {
        using var fake = new FakeMcp();
        if (warmCache)
            await fake.PrimeAsync();
        fake.SetBehavior("initialize-error");

        await using (var failed = new McpProxyRuntime(fake.CacheDirectory))
        {
            var remote = failed.Register(fake.Definition());
            var initialize = await fake.InitializeAsync(remote.Url);
            if (warmCache)
            {
                AssertSuccess(initialize);
                Assert.Single(fake.Starts);
            }
            else
            {
                AssertError(initialize);
            }
            AssertError(await fake.RequestAsync(remote.Url, "tools/call", new { name = "echo", arguments = new { value = "never" } }));
            Assert.Empty(fake.Messages("tools/call"));
        }

        // The failed live validation must evict even a previously valid snapshot.
        fake.SetBehavior("static");
        var startsBeforeRecovery = fake.Starts.Length;
        await using var recovered = new McpProxyRuntime(fake.CacheDirectory);
        AssertSuccess(await fake.InitializeAsync(recovered.Register(fake.Definition()).Url));
        Assert.Equal(startsBeforeRecovery + 1, fake.Starts.Length);
    }

    [SkippableFact]
    public async Task OrdinaryToolErrors_AreForwardedOnEveryCall_WithoutInvalidatingDiscovery()
    {
        using var fake = new FakeMcp();
        await fake.PrimeAsync();
        fake.SetBehavior("call-error");
        await using (var runtime = new McpProxyRuntime(fake.CacheDirectory))
        {
            var remote = runtime.Register(fake.Definition());
            AssertSuccess(await fake.InitializeAsync(remote.Url));
            Assert.Single(fake.Starts);
            for (var i = 0; i < 2; i++)
            {
                var error = await fake.RequestAsync(remote.Url, "tools/call",
                    new { name = "echo", arguments = new { value = $"failure-{i}" } }, $"call-error-{i}");
                AssertError(error);
                Assert.Equal($"call-error-{i}", error.GetProperty("id").GetString());
                Assert.Equal(-32603, error.GetProperty("error").GetProperty("code").GetInt32());
                Assert.Equal("Synthetic call failure", error.GetProperty("error").GetProperty("message").GetString());
            }
            Assert.Equal(2, fake.Starts.Length);
            Assert.Equal(["failure-0", "failure-1"], fake.Messages("tools/call")
                .Select(message => message.GetProperty("params").GetProperty("arguments").GetProperty("value").GetString()));
        }

        fake.SetBehavior("static");
        await using var next = new McpProxyRuntime(fake.CacheDirectory);
        var cached = next.Register(fake.Definition());
        AssertSuccess(await fake.InitializeAsync(cached.Url));
        AssertSuccess(await fake.RequestAsync(cached.Url, "tools/list"));
        Assert.Equal(2, fake.Starts.Length);
        Assert.Equal(2, fake.Messages("tools/call").Length);
    }

    [SkippableFact]
    public async Task DefaultRuntime_DoesNotSharePersistentDiscoveryWithAnotherRuntime()
    {
        using var fake = new FakeMcp();
        for (var lifetime = 0; lifetime < 2; lifetime++)
        {
            await using var runtime = new McpProxyRuntime();
            var remote = runtime.Register(fake.Definition());
            AssertSuccess(await fake.InitializeAsync(remote.Url));
            AssertSuccess(await fake.RequestAsync(remote.Url, "tools/list"));
            Assert.Equal(lifetime + 1, fake.Starts.Length);
        }
        Assert.False(Directory.Exists(fake.CacheDirectory));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeProfile_IsBoundBeforeCacheReplay_AndLateMismatchCannotMiskey(bool lazyFirst)
    {
        using var fake = new FakeMcp();
        await fake.PrimeAsync();
        await using (var runtime = new McpProxyRuntime(fake.CacheDirectory))
        {
            using var first = runtime.AcquireSessionRegistration(fake.Definition(lazyFirst));
            using var second = runtime.AcquireSessionRegistration(fake.Definition(!lazyFirst));
            AssertSuccess(await fake.InitializeAsync(first.ServerConfig.Url));
            var starts = fake.Starts.Length;
            var changed = FakeMcp.ClientInitialize();
            changed["clientInfo"]!["version"] = "different";
            var mismatch = await fake.InitializeAsync(second.ServerConfig.Url, changed);
            AssertError(mismatch);
            Assert.Contains("different client profile", mismatch.GetProperty("error").GetProperty("message").GetString());
            Assert.Equal(starts, fake.Starts.Length);
            AssertSuccess(await fake.RequestAsync(first.ServerConfig.Url, "tools/list"));
            AssertSuccess(await fake.RequestAsync(first.ServerConfig.Url, "tools/call",
                new { name = "echo", arguments = new { value = "original-profile" } }));
            Assert.All(fake.Messages("initialize"), message =>
                AssertJsonEqual(JsonSerializer.SerializeToElement(FakeMcp.ClientInitialize()), message.GetProperty("params")));
            Assert.Single(Directory.GetFiles(fake.CacheDirectory, "*.json"));
        }
        await using var next = new McpProxyRuntime(fake.CacheDirectory);
        var remote = next.Register(fake.Definition());
        AssertSuccess(await fake.InitializeAsync(remote.Url));
        AssertSuccess(await fake.RequestAsync(remote.Url, "tools/list"));
        Assert.Equal(2, fake.Starts.Length);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LazyAndEagerSessionLeases_ShareBackendWithoutSharingFrontendSnapshot(bool lazyFirst)
    {
        using var fake = new FakeMcp();
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        using var first = runtime.AcquireSessionRegistration(fake.Definition(lazyFirst));
        using var second = runtime.AcquireSessionRegistration(fake.Definition(!lazyFirst));
        Assert.Equal(new Uri(first.ServerConfig.Url).AbsolutePath, new Uri(second.ServerConfig.Url).AbsolutePath);
        AssertSuccess(await fake.InitializeAsync(first.ServerConfig.Url));
        AssertSuccess(await fake.RequestAsync(first.ServerConfig.Url, "tools/list"));
        AssertSuccess(await fake.InitializeAsync(second.ServerConfig.Url));
        AssertSuccess(await fake.RequestAsync(second.ServerConfig.Url, "tools/list"));
        Assert.Single(fake.Starts);
        Assert.Single(fake.Messages("initialize"));
        var eager = lazyFirst ? second : first;
        var lazy = lazyFirst ? first : second;
        fake.SetBehavior("description-drift");
        var fresh = await fake.RequestAsync(eager.ServerConfig.Url, "tools/list");
        var old = await fake.RequestAsync(lazy.ServerConfig.Url, "tools/list");
        Assert.False(JsonElement.DeepEquals(fresh.GetProperty("result"), old.GetProperty("result")));
        AssertError(await fake.RequestAsync(lazy.ServerConfig.Url, "tools/call",
            new { name = "echo", arguments = new { value = "blocked" } }));
        AssertSuccess(await fake.RequestAsync(eager.ServerConfig.Url, "tools/call",
            new { name = "echo", arguments = new { value = "live" } }));
        Assert.Single(fake.Messages("tools/call"));
        await first.ReleaseAsync();
        AssertSuccess(await fake.RequestAsync(second.ServerConfig.Url, "ping"));
        Assert.Single(fake.Starts);
    }

    [SkippableTheory]
    [InlineData("initialize-error")]
    [InlineData("list-error")]
    public async Task TransientDiscoveryFailure_RecoversAndWritesUsableCacheInSameRuntime(string behavior)
    {
        using var fake = new FakeMcp();
        await fake.PrimeAsync();
        await using (var runtime = new McpProxyRuntime(fake.CacheDirectory))
        {
            var remote = runtime.Register(fake.Definition());
            AssertSuccess(await fake.InitializeAsync(remote.Url));
            fake.SetBehavior(behavior);
            AssertError(await fake.RequestAsync(remote.Url, "tools/call",
                new { name = "echo", arguments = new { value = "failure" } }));
            Assert.Empty(fake.Messages("tools/call"));
            Assert.Empty(Directory.GetFiles(fake.CacheDirectory, "*.json"));
            fake.SetBehavior("static");
            AssertSuccess(await fake.RequestAsync(remote.Url, "tools/call",
                new { name = "echo", arguments = new { value = "recovered" } }));
            Assert.Single(fake.Messages("tools/call"));
            Assert.Single(Directory.GetFiles(fake.CacheDirectory, "*.json"));
        }
        var starts = fake.Starts.Length;
        await using var next = new McpProxyRuntime(fake.CacheDirectory);
        var cached = next.Register(fake.Definition());
        AssertSuccess(await fake.InitializeAsync(cached.Url));
        AssertSuccess(await fake.RequestAsync(cached.Url, "tools/list"));
        Assert.Equal(starts, fake.Starts.Length);
    }

    [SkippableTheory]
    [InlineData("discovery-session-lost")]
    [InlineData("call-session-lost")]
    public async Task SessionRecovery_RetriesOnlyReadOnlyDiscovery_NeverReplaysToolCalls(string behavior)
    {
        using var fake = new FakeMcp();
        await fake.PrimeAsync();
        fake.SetBehavior(behavior);
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        var remote = runtime.Register(fake.Definition());
        AssertSuccess(await fake.InitializeAsync(remote.Url));
        var first = await fake.RequestAsync(remote.Url, "tools/call",
            new { name = "echo", arguments = new { value = "first" } });
        if (behavior == "discovery-session-lost")
            AssertSuccess(first);
        else
            AssertError(first);
        Assert.Single(fake.Messages("tools/call"));
        Assert.Equal(3, fake.Starts.Length);
        AssertSuccess(await fake.RequestAsync(remote.Url, "tools/call",
            new { name = "echo", arguments = new { value = "second" } }));
        Assert.Equal(2, fake.Messages("tools/call").Length);
        Assert.Equal(3, fake.Starts.Length);
        Assert.Single(Directory.GetFiles(fake.CacheDirectory, "*.json"));
    }

    [SkippableFact]
    public async Task ToolCallPreflight_RemainsLazyUntilFirstBusinessCall_AndUsesUncachedProbe()
    {
        using var fake = new FakeMcp();
        await fake.PrimeAsync();
        var listsBefore = fake.Messages("tools/list").Length;
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        var remote = runtime.Register(fake.Definition(
            toolCallPreflightPolicy: McpToolCallPreflightPolicy.ToolsListSessionHealth));

        AssertSuccess(await fake.InitializeAsync(remote.Url));
        AssertSuccess(await fake.RequestAsync(remote.Url, "tools/list"));
        AssertSuccess(await fake.RequestAsync(remote.Url, "ping"));
        Assert.Single(fake.Starts);
        Assert.Equal(listsBefore, fake.Messages("tools/list").Length);

        AssertSuccess(await fake.RequestAsync(remote.Url, "tools/call",
            new { name = "echo", arguments = new { value = "first" } }));
        Assert.Equal(2, fake.Starts.Length);
        Assert.Equal(listsBefore + 2, fake.Messages("tools/list").Length);
        Assert.Single(fake.Messages("tools/call"));
    }

    [SkippableFact]
    public async Task ToolCallPreflight_RepairsWarmExpiredSessionBeforeSingleDispatch()
    {
        using var fake = new FakeMcp();
        await fake.PrimeAsync();
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        var remote = runtime.Register(fake.Definition(
            toolCallPreflightPolicy: McpToolCallPreflightPolicy.ToolsListSessionHealth));
        AssertSuccess(await fake.InitializeAsync(remote.Url));
        AssertSuccess(await fake.RequestAsync(remote.Url, "tools/call",
            new { name = "echo", arguments = new { value = "warm" } }));
        var listsBeforeExpiry = fake.Messages("tools/list").Length;

        fake.SetBehavior("discovery-session-lost");
        AssertSuccess(await fake.RequestAsync(remote.Url, "tools/call",
            new { name = "echo", arguments = new { value = "after-expiry" } }));

        Assert.Equal(3, fake.Starts.Length);
        Assert.Equal(listsBeforeExpiry + 2, fake.Messages("tools/list").Length);
        Assert.Equal(["warm", "after-expiry"], fake.Messages("tools/call")
            .Select(message => message.GetProperty("params").GetProperty("arguments")
                .GetProperty("value").GetString()));
    }

    [SkippableFact]
    public async Task ToolCallPreflight_ConcurrentExpiredCallsCoordinateOneRepair()
    {
        using var fake = new FakeMcp();
        await fake.PrimeAsync();
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        var remote = runtime.Register(fake.Definition(
            toolCallPreflightPolicy: McpToolCallPreflightPolicy.ToolsListSessionHealth));
        AssertSuccess(await fake.InitializeAsync(remote.Url));
        AssertSuccess(await fake.RequestAsync(remote.Url, "tools/call",
            new { name = "echo", arguments = new { value = "warm" } }));

        fake.SetBehavior("discovery-session-lost");
        var calls = Enumerable.Range(0, 2).Select(index =>
            fake.RequestAsync(remote.Url, "tools/call",
                new { name = "echo", arguments = new { value = $"concurrent-{index}" } })).ToArray();
        var responses = await Task.WhenAll(calls);

        Assert.All(responses, AssertSuccess);
        Assert.Equal(3, fake.Starts.Length);
        Assert.Equal(3, fake.Messages("tools/call").Length);
        Assert.Equal(2, fake.Messages("tools/call")
            .Count(message => message.GetProperty("params").GetProperty("arguments")
                .GetProperty("value").GetString()!.StartsWith("concurrent-", StringComparison.Ordinal)));
    }

    [SkippableFact]
    public async Task ToolCallPreflight_ChangedReplacementCatalogBlocksBusinessDispatch()
    {
        using var fake = new FakeMcp();
        await fake.PrimeAsync();
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        var remote = runtime.Register(fake.Definition(
            toolCallPreflightPolicy: McpToolCallPreflightPolicy.ToolsListSessionHealth));
        AssertSuccess(await fake.InitializeAsync(remote.Url));
        AssertSuccess(await fake.RequestAsync(remote.Url, "tools/call",
            new { name = "echo", arguments = new { value = "warm" } }));

        fake.SetBehavior("recovery-selected-missing");
        var response = await fake.RequestAsync(remote.Url, "tools/call",
            new { name = "echo", arguments = new { value = "blocked" } });

        AssertError(response);
        Assert.Contains("changed its advertised discovery",
            response.GetProperty("error").GetProperty("message").GetString());
        Assert.Single(fake.Messages("tools/call"));
        Assert.Equal(3, fake.Starts.Length);
    }

    [SkippableFact]
    public async Task ToolCallPreflight_CancellationBeforeDispatchNeverCallsTool()
    {
        using var fake = new FakeMcp("slow-list");
        await using var connection = new McpStdioServerConnection(
            fake.Definition(
                lazy: false,
                toolCallPreflightPolicy: McpToolCallPreflightPolicy.ToolsListSessionHealth),
            new McpDiscoveryCache(fake.CacheDirectory));
        var client = new McpDiscoverySession(useLazyInitialization: false);
        Assert.NotNull(await connection.HandleClientMessageAsync(
            client,
            """{"jsonrpc":"2.0","id":"initialize","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
            CancellationToken.None));
        var callsBefore = fake.Messages("tools/call").Length;
        var listsBefore = fake.Messages("tools/list").Length;

        using var cancellation = new CancellationTokenSource();
        var pending = connection.HandleClientMessageAsync(
            client,
            """{"jsonrpc":"2.0","id":"cancelled","method":"tools/call","params":{"name":"echo","arguments":{"value":"cancelled"}}}""",
            cancellation.Token);
        await fake.WaitForMessageCountAsync("tools/list", listsBefore + 1);
        cancellation.Cancel();
        var response = await pending;
        Assert.NotNull(response);
        using var document = JsonDocument.Parse(response);
        AssertError(document.RootElement);
        Assert.Equal(callsBefore, fake.Messages("tools/call").Length);
        File.WriteAllText(Path.Combine(fake.Root, "continue-list"), "");
    }

    [SkippableFact]
    public async Task ToolCallPreflight_PostDispatchSessionLossIsNeverReplayed()
    {
        using var fake = new FakeMcp();
        await fake.PrimeAsync();
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        var remote = runtime.Register(fake.Definition(
            toolCallPreflightPolicy: McpToolCallPreflightPolicy.ToolsListSessionHealth));
        AssertSuccess(await fake.InitializeAsync(remote.Url));
        fake.SetBehavior("call-session-lost");

        var response = await fake.RequestAsync(remote.Url, "tools/call",
            new { name = "echo", arguments = new { value = "uncertain" } });

        AssertError(response);
        Assert.Contains("outcome is unknown", response.GetProperty("error").GetProperty("message").GetString());
        Assert.Single(fake.Messages("tools/call"));
        Assert.Equal(3, fake.Starts.Length);
        AssertSuccess(await fake.RequestAsync(remote.Url, "tools/call",
            new { name = "echo", arguments = new { value = "next" } }));
        Assert.Equal(2, fake.Messages("tools/call").Length);
    }

    [SkippableFact]
    public async Task ToolCallPreflight_ValidatesToolAdvertisedOnLaterPaginationPage()
    {
        using var fake = new FakeMcp("paginated-later-tool");
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        var remote = runtime.Register(fake.Definition(
            toolCallPreflightPolicy: McpToolCallPreflightPolicy.ToolsListSessionHealth));
        AssertSuccess(await fake.InitializeAsync(remote.Url));
        var firstPage = await fake.RequestAsync(remote.Url, "tools/list", new { });
        AssertSuccess(firstPage);
        Assert.Equal("page-2", firstPage.GetProperty("result").GetProperty("nextCursor").GetString());
        AssertSuccess(await fake.RequestAsync(
            remote.Url,
            "tools/list",
            new { cursor = "page-2" }));

        AssertSuccess(await fake.RequestAsync(remote.Url, "tools/call",
            new { name = "echo", arguments = new { value = "later-page" } }));

        Assert.Single(fake.Messages("tools/call"));
        Assert.Equal(4, fake.Messages("tools/list").Length);
    }

    [SkippableFact]
    public async Task ToolCallPreflight_BlocksToolFromUnfetchedPaginationPage()
    {
        using var fake = new FakeMcp("paginated-later-tool");
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        var remote = runtime.Register(fake.Definition(
            toolCallPreflightPolicy: McpToolCallPreflightPolicy.ToolsListSessionHealth));
        AssertSuccess(await fake.InitializeAsync(remote.Url));
        var firstPage = await fake.RequestAsync(remote.Url, "tools/list", new { });
        AssertSuccess(firstPage);
        Assert.Equal("page-2", firstPage.GetProperty("result").GetProperty("nextCursor").GetString());

        var response = await fake.RequestAsync(remote.Url, "tools/call",
            new { name = "echo", arguments = new { value = "not-advertised" } });

        AssertError(response);
        Assert.Contains("changed its advertised discovery",
            response.GetProperty("error").GetProperty("message").GetString());
        Assert.Empty(fake.Messages("tools/call"));
    }

    [SkippableFact]
    public async Task TimeoutChangesReuseDiscoveryButRetainDistinctRegistrationTimeouts()
    {
        using var fake = new FakeMcp();
        await fake.PrimeAsync();
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        using var original = runtime.AcquireSessionRegistration(fake.Definition());
        var definition = fake.Definition();
        definition.Config.Timeout = 20_000;
        var changed = runtime.Register(definition);
        Assert.NotEqual(new Uri(original.ServerConfig.Url).AbsolutePath, new Uri(changed.Url).AbsolutePath);
        Assert.Equal(20_000, changed.Timeout);
        AssertSuccess(await fake.InitializeAsync(changed.Url));
        AssertSuccess(await fake.RequestAsync(changed.Url, "tools/list"));
        Assert.Single(fake.Starts);
    }

    [SkippableFact]
    public async Task ReleasingLazyLease_RemovesOnlyItsToken_AndDrainsInFlightRequest()
    {
        using var fake = new FakeMcp();
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        using var lease = runtime.AcquireSessionRegistration(fake.Definition());
        using var retained = runtime.AcquireSessionRegistration(fake.Definition());
        Assert.NotEqual(lease.ServerConfig.Url, retained.ServerConfig.Url);
        AssertSuccess(await fake.InitializeAsync(lease.ServerConfig.Url));
        AssertSuccess(await fake.RequestAsync(lease.ServerConfig.Url, "tools/list"));
        Assert.Equal(HttpStatusCode.NotFound,
            await fake.PostStatusAsync(lease.ServerConfig.Url + "unknown", """{"jsonrpc":"2.0","id":1,"method":"ping"}"""));
        fake.SetBehavior("slow-call");
        var call = fake.RequestAsync(lease.ServerConfig.Url, "tools/call",
            new { name = "echo", arguments = new { value = "finish-once" } });
        await fake.WaitForMessageCountAsync("tools/call", 1);
        await lease.ReleaseAsync();
        Assert.Equal(HttpStatusCode.NotFound,
            await fake.PostStatusAsync(lease.ServerConfig.Url, """{"jsonrpc":"2.0","id":2,"method":"ping"}"""));
        AssertSuccess(await fake.InitializeAsync(retained.ServerConfig.Url));
        var releasingLast = retained.ReleaseAsync();
        Assert.False(releasingLast.IsCompleted);
        File.WriteAllText(Path.Combine(fake.Root, "continue-call"), "");
        AssertSuccess(await call);
        await releasingLast;
        Assert.Single(fake.Starts);
        Assert.Single(fake.Messages("tools/call"));
    }

    [SkippableFact]
    public async Task UnsupportedServerRequestFailure_IsForwarded_NotReplayedAsSuccess()
    {
        using var fake = new FakeMcp();
        await fake.PrimeAsync();
        fake.SetBehavior("server-request-failure");
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        var remote = runtime.Register(fake.Definition());
        AssertSuccess(await fake.InitializeAsync(remote.Url));
        var response = await fake.RequestAsync(remote.Url, "tools/call",
            new { name = "echo", arguments = new { value = "callback-failure" } }, "failure");
        AssertError(response);
        Assert.Equal("failure", response.GetProperty("id").GetString());
        Assert.Equal("Required roots request failed", response.GetProperty("error").GetProperty("message").GetString());
        Assert.Single(fake.Messages("tools/call"));
        Assert.Empty(Directory.GetFiles(fake.CacheDirectory, "*.json"));
    }

    [SkippableFact]
    public async Task CallbackTaint_EndsWithItsProcessGeneration()
    {
        using var fake = new FakeMcp();
        await fake.PrimeAsync();
        await using (var runtime = new McpProxyRuntime(fake.CacheDirectory))
        {
            var remote = runtime.Register(fake.Definition());
            AssertSuccess(await fake.InitializeAsync(remote.Url));
            fake.SetBehavior("discovery-callback");
            AssertError(await fake.RequestAsync(remote.Url, "tools/call",
                new { name = "echo", arguments = new { value = "never" } }));
            fake.SetBehavior("exit-on-ping");
            AssertError(await fake.RequestAsync(remote.Url, "ping"));
            // The callback-tainted lazy frontend is fail-closed; the eager frontend retains
            // the existing live behavior and can observe a backend exit.
            var eager = runtime.Register(fake.Definition(lazy: false));
            AssertError(await fake.RequestAsync(eager.Url, "ping"));
            await fake.WaitForLatestProcessExitAsync();
            fake.SetBehavior("static");
            AssertSuccess(await fake.RequestAsync(remote.Url, "tools/call",
                new { name = "echo", arguments = new { value = "new-generation" } }));
            Assert.Single(Directory.GetFiles(fake.CacheDirectory, "*.json"));
        }
        var starts = fake.Starts.Length;
        await using var next = new McpProxyRuntime(fake.CacheDirectory);
        AssertSuccess(await fake.InitializeAsync(next.Register(fake.Definition()).Url));
        Assert.Equal(starts, fake.Starts.Length);
    }
}
