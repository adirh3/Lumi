using System.Text.Json;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed partial class LazyMcpRuntimeTests
{
    [Theory]
    [InlineData(-1L, 42L, false)]
    [InlineData(7L, 7L, false)]
    [InlineData(7L, 8L, true)]
    public void FrontendRediscovery_TracksAdvertisedEpoch(long advertised, long current, bool expected)
    {
        var client = new McpDiscoverySession(true) { AdvertisedRevision = advertised };
        Assert.Equal(expected, client.NeedsRediscovery(current));
    }

    [Fact]
    public void MarkingOneFrontendForRediscovery_DoesNotRewriteOrBlockOtherFrontends()
    {
        var tools = JsonSerializer.SerializeToElement(new { tools = Array.Empty<object>() });
        var old = new McpDiscoverySession(true) { AdvertisedRevision = 7, ToolsResult = tools };
        var other = new McpDiscoverySession(true) { AdvertisedRevision = 7 };
        old.MarkNeedsRediscovery();
        Assert.True(old.NeedsRediscovery(7));
        Assert.False(other.NeedsRediscovery(7));
        AssertJsonEqual(tools, old.ToolsResult!.Value);
    }

    [SkippableTheory]
    [InlineData("list-changed")]
    [InlineData("server-request")]
    public async Task LiveInvalidation_PreventsReplayInTheNextRuntime(string behavior)
    {
        using var fake = new FakeMcp();
        await fake.PrimeAsync();
        fake.SetBehavior(behavior);
        await using (var live = new McpProxyRuntime(fake.CacheDirectory))
        {
            var remote = live.Register(fake.Definition());
            AssertSuccess(await fake.InitializeAsync(remote.Url));
            Assert.Single(fake.Starts);
            AssertSuccess(await fake.RequestAsync(remote.Url, "tools/call",
                new { name = "echo", arguments = new { value = "invalidate" } }));
        }

        fake.SetBehavior("static");
        await using var next = new McpProxyRuntime(fake.CacheDirectory);
        AssertSuccess(await fake.InitializeAsync(next.Register(fake.Definition()).Url));
        Assert.Equal(3, fake.Starts.Length);
    }

    [SkippableTheory]
    [InlineData("version-drift")]
    [InlineData("list-reordered")]
    [InlineData("unrelated-added")]
    [InlineData("unrelated-removed")]
    public async Task UnchangedSelectedTool_IgnoresVersionOrderAndUnrelatedTools(string behavior)
    {
        using var fake = new FakeMcp();
        await fake.PrimeAsync();
        fake.SetBehavior(behavior);
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        using var client = runtime.AcquireSessionRegistration(fake.Definition());
        AssertSuccess(await fake.InitializeAsync(client.ServerConfig.Url));
        AssertSuccess(await fake.RequestAsync(client.ServerConfig.Url, "tools/list"));
        AssertSuccess(await fake.RequestAsync(client.ServerConfig.Url, "tools/call",
            new { name = "echo", arguments = new { value = "unchanged" } }));
        Assert.Single(fake.Messages("tools/call"));
        Assert.Equal(2, fake.Starts.Length);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshDiscovery_DoesNotSpawnOrStopBackend_AndKeepsActiveSnapshots(bool runningBackend)
    {
        using var fake = new FakeMcp();
        await fake.PrimeAsync();
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        using var old = runtime.AcquireSessionRegistration(fake.Definition());
        AssertSuccess(await fake.InitializeAsync(old.ServerConfig.Url));
        var oldTools = await fake.RequestAsync(old.ServerConfig.Url, "tools/list");
        if (runningBackend)
            AssertSuccess(await fake.RequestAsync(old.ServerConfig.Url, "tools/call",
                new { name = "other", arguments = new { value = "start" } }));
        var starts = fake.Starts.Length;
        fake.SetBehavior("description-drift");
        await runtime.RefreshDiscoveryAsync();
        Assert.Equal(starts, fake.Starts.Length);
        Assert.Empty(Directory.GetFiles(fake.CacheDirectory, "*.json"));
        AssertJsonEqual(oldTools.GetProperty("result"),
            (await fake.RequestAsync(old.ServerConfig.Url, "tools/list")).GetProperty("result"));
        Assert.Equal(starts, fake.Starts.Length);

        using var fresh = runtime.AcquireSessionRegistration(fake.Definition());
        AssertSuccess(await fake.InitializeAsync(fresh.ServerConfig.Url));
        var freshTools = await fake.RequestAsync(fresh.ServerConfig.Url, "tools/list");
        Assert.False(JsonElement.DeepEquals(oldTools.GetProperty("result"), freshTools.GetProperty("result")));
        Assert.Equal(2, fake.Starts.Length);
        AssertError(await fake.RequestAsync(old.ServerConfig.Url, "tools/call",
            new { name = "echo", arguments = new { value = "blocked" } }));
        AssertSuccess(await fake.RequestAsync(old.ServerConfig.Url, "tools/call",
            new { name = "other", arguments = new { value = "still-usable" } }));
        AssertSuccess(await fake.RequestAsync(fresh.ServerConfig.Url, "tools/call",
            new { name = "echo", arguments = new { value = "fresh" } }));
        Assert.Single(Directory.GetFiles(fake.CacheDirectory, "*.json"));
        Assert.Equal(2, fake.Starts.Length);
    }

    [SkippableFact]
    public async Task RefreshEmptyCache_IsValid_AndPersistentRegistrationGetsNewFrontendOnly()
    {
        using var fake = new FakeMcp();
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        await runtime.RefreshDiscoveryAsync();
        Assert.Empty(fake.Starts);
        var old = runtime.Register(fake.Definition());
        Assert.Equal(old.Url, runtime.Register(fake.Definition()).Url);
        AssertSuccess(await fake.InitializeAsync(old.Url));
        var oldTools = await fake.RequestAsync(old.Url, "tools/list");
        await runtime.RefreshDiscoveryAsync();
        var fresh = runtime.Register(fake.Definition());
        Assert.NotEqual(old.Url, fresh.Url);
        Assert.Equal(new Uri(old.Url).AbsolutePath, new Uri(fresh.Url).AbsolutePath);
        AssertJsonEqual(oldTools.GetProperty("result"),
            (await fake.RequestAsync(old.Url, "tools/list")).GetProperty("result"));
        Assert.Single(fake.Starts);
    }

    [SkippableFact]
    public async Task RefreshDuringDiscovery_RejectsStalePublicationAndAllowsSameProcessRecovery()
    {
        using var fake = new FakeMcp();
        await fake.PrimeAsync();
        fake.SetBehavior("slow-list");
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        var remote = runtime.Register(fake.Definition());
        AssertSuccess(await fake.InitializeAsync(remote.Url));
        var call = fake.RequestAsync(remote.Url, "tools/call",
            new { name = "echo", arguments = new { value = "never" } });
        await fake.WaitForMessageCountAsync("tools/list", 2);
        await runtime.RefreshDiscoveryAsync();
        File.WriteAllText(Path.Combine(fake.Root, "continue-list"), "");
        AssertError(await call);
        Assert.Empty(fake.Messages("tools/call"));
        Assert.Empty(Directory.GetFiles(fake.CacheDirectory, "*.json"));
        fake.SetBehavior("static");
        AssertSuccess(await fake.RequestAsync(remote.Url, "tools/call",
            new { name = "echo", arguments = new { value = "recovered" } }));
        Assert.Single(fake.Messages("tools/call"));
        Assert.Single(Directory.GetFiles(fake.CacheDirectory, "*.json"));
        Assert.Equal(2, fake.Starts.Length);
    }

    [SkippableFact]
    public async Task UnrelatedInvalidation_DoesNotWakeDormantClientsOrTheirNewFrontends()
    {
        using var dormant = new FakeMcp();
        using var active = new FakeMcp();
        var cacheDirectory = dormant.CacheDirectory;
        await using (var cold = new McpProxyRuntime(cacheDirectory))
        {
            using var first = cold.AcquireSessionRegistration(dormant.Definition());
            using var second = cold.AcquireSessionRegistration(active.Definition());
            AssertSuccess(await dormant.InitializeAsync(first.ServerConfig.Url));
            AssertSuccess(await dormant.RequestAsync(first.ServerConfig.Url, "tools/list"));
            AssertSuccess(await active.InitializeAsync(second.ServerConfig.Url));
            AssertSuccess(await active.RequestAsync(second.ServerConfig.Url, "tools/list"));
            await first.ReleaseAsync();
            await second.ReleaseAsync();
        }

        await using var runtime = new McpProxyRuntime(cacheDirectory);
        var sleeping = runtime.Register(dormant.Definition());
        AssertSuccess(await dormant.InitializeAsync(sleeping.Url));
        var advertised = await dormant.RequestAsync(sleeping.Url, "tools/list");
        using var waking = runtime.AcquireSessionRegistration(active.Definition());
        AssertSuccess(await active.InitializeAsync(waking.ServerConfig.Url));
        active.SetBehavior("list-changed");
        AssertSuccess(await active.RequestAsync(waking.ServerConfig.Url, "tools/call",
            new { name = "echo", arguments = new { value = "invalidate-only-this-source" } }));
        Assert.Equal(2, active.Starts.Length);
        Assert.Single(Directory.GetFiles(cacheDirectory, "*.json"));

        AssertSuccess(await dormant.RequestAsync(sleeping.Url, "ping"));
        AssertJsonEqual(advertised.GetProperty("result"),
            (await dormant.RequestAsync(sleeping.Url, "tools/list")).GetProperty("result"));
        var nextPersistent = runtime.Register(dormant.Definition());
        AssertSuccess(await dormant.InitializeAsync(nextPersistent.Url));
        AssertSuccess(await dormant.RequestAsync(nextPersistent.Url, "tools/list"));
        using var nextSession = runtime.AcquireSessionRegistration(dormant.Definition());
        AssertSuccess(await dormant.InitializeAsync(nextSession.ServerConfig.Url));
        AssertSuccess(await dormant.RequestAsync(nextSession.ServerConfig.Url, "tools/list"));
        Assert.Single(dormant.Starts);
        Assert.Single(dormant.Messages("initialize"));
        Assert.Single(dormant.Messages("tools/list"));

        await runtime.RefreshDiscoveryAsync();
        Assert.Single(dormant.Starts);
        using var afterClear = runtime.AcquireSessionRegistration(dormant.Definition());
        AssertSuccess(await dormant.InitializeAsync(afterClear.ServerConfig.Url));
        AssertSuccess(await dormant.RequestAsync(afterClear.ServerConfig.Url, "tools/list"));
        Assert.Equal(2, dormant.Starts.Length);
    }

    [SkippableTheory]
    [InlineData("resources")]
    [InlineData("list-changed")]
    public async Task UnrelatedInvalidation_DoesNotRejectInFlightDiscovery(string behavior)
    {
        using var discovering = new FakeMcp();
        using var other = new FakeMcp(behavior);
        await discovering.PrimeAsync();
        await using var runtime = new McpProxyRuntime(discovering.CacheDirectory);
        using var first = runtime.AcquireSessionRegistration(discovering.Definition());
        using var second = runtime.AcquireSessionRegistration(other.Definition(lazy: false));
        AssertSuccess(await discovering.InitializeAsync(first.ServerConfig.Url));
        AssertSuccess(await other.InitializeAsync(second.ServerConfig.Url));
        discovering.SetBehavior("slow-list");

        var call = discovering.RequestAsync(first.ServerConfig.Url, "tools/call",
            new { name = "echo", arguments = new { value = "only-once" } });
        await discovering.WaitForMessageCountAsync("tools/list", 2);
        try
        {
            var response = behavior == "resources"
                ? await other.RequestAsync(second.ServerConfig.Url, "tools/list")
                : await other.RequestAsync(second.ServerConfig.Url, "tools/call",
                    new { name = "echo", arguments = new { value = "invalidate-other-source" } });
            AssertSuccess(response);
        }
        finally
        {
            File.WriteAllText(Path.Combine(discovering.Root, "continue-list"), "");
        }

        AssertSuccess(await call);
        Assert.Single(discovering.Messages("tools/call"));
        Assert.Single(Directory.GetFiles(discovering.CacheDirectory, "*.json"));
        Assert.Equal(2, discovering.Starts.Length);
    }

    [SkippableFact]
    public async Task ObservedListChanged_RefreshesSourceBeforeToolDispatch_WithoutRewritingOldClient()
    {
        using var fake = new FakeMcp("dynamic");
        await fake.PrimeAsync();
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        using var old = runtime.AcquireSessionRegistration(fake.Definition());
        var initialize = await fake.InitializeAsync(old.ServerConfig.Url);
        Assert.False(initialize.GetProperty("result").GetProperty("capabilities").GetProperty("tools")
            .GetProperty("listChanged").GetBoolean());
        var advertised = await fake.RequestAsync(old.ServerConfig.Url, "tools/list");
        fake.SetBehavior("list-changed");
        AssertSuccess(await fake.RequestAsync(old.ServerConfig.Url, "tools/call",
            new { name = "echo", arguments = new { value = "notify" } }));
        var listCount = fake.Messages("tools/list").Length;
        fake.SetBehavior("description-drift");
        AssertError(await fake.RequestAsync(old.ServerConfig.Url, "tools/call",
            new { name = "echo", arguments = new { value = "not-forwarded" } }));
        Assert.Equal(listCount + 1, fake.Messages("tools/list").Length);
        Assert.Single(fake.Messages("tools/call"));
        AssertSuccess(await fake.RequestAsync(old.ServerConfig.Url, "tools/call",
            new { name = "other", arguments = new { value = "unchanged" } }));
        Assert.Equal(listCount + 1, fake.Messages("tools/list").Length);
        AssertJsonEqual(advertised.GetProperty("result"),
            (await fake.RequestAsync(old.ServerConfig.Url, "tools/list")).GetProperty("result"));
        using var fresh = runtime.AcquireSessionRegistration(fake.Definition());
        AssertSuccess(await fake.InitializeAsync(fresh.ServerConfig.Url));
        AssertSuccess(await fake.RequestAsync(fresh.ServerConfig.Url, "tools/list"));
        AssertSuccess(await fake.RequestAsync(fresh.ServerConfig.Url, "tools/call",
            new { name = "echo", arguments = new { value = "fresh" } }));
        Assert.Equal(2, fake.Starts.Length);
    }

    [SkippableTheory]
    [InlineData("discovery-notification")]
    [InlineData("discovery-callback")]
    public async Task SignalsDuringDiscovery_CannotPublishOrDispatchStaleCatalog(string behavior)
    {
        using var fake = new FakeMcp();
        await fake.PrimeAsync();
        fake.SetBehavior(behavior);
        await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
        var remote = runtime.Register(fake.Definition());
        AssertSuccess(await fake.InitializeAsync(remote.Url));
        AssertError(await fake.RequestAsync(remote.Url, "tools/call",
            new { name = "echo", arguments = new { value = "never" } }));
        Assert.Empty(fake.Messages("tools/call"));
        Assert.Empty(Directory.GetFiles(fake.CacheDirectory, "*.json"));
        fake.SetBehavior("static");
        var next = await fake.RequestAsync(remote.Url, "tools/call",
            new { name = "echo", arguments = new { value = "retry" } });
        if (behavior == "discovery-notification")
        {
            AssertSuccess(next);
            Assert.Single(Directory.GetFiles(fake.CacheDirectory, "*.json"));
        }
        else
        {
            AssertError(next);
            Assert.Empty(fake.Messages("tools/call"));
        }
        Assert.Equal(2, fake.Starts.Length);
    }
}
