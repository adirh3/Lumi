using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Covers the capability pipeline: live Lumi data and external providers feed one merged snapshot.
/// </summary>
public sealed class CapabilityCatalogTests
{
    [Fact]
    public void LumiProvider_ProjectsStoreEntriesOntoDescriptors()
    {
        var skillId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var mcpId = Guid.NewGuid();
        var store = new DataStore(new AppData
        {
            Skills = [new Skill { Id = skillId, Name = "Doc Creator", Description = "Makes docs", Content = "# body", IconGlyph = "📄" }],
            Agents = [new LumiAgent { Id = agentId, Name = "Daily Lumi", Description = "Plans the day", SystemPrompt = "Be helpful", IconGlyph = "🌅" }],
            McpServers = [new McpServer { Id = mcpId, Name = "avalonia", IsEnabled = true }],
        });

        var snapshot = new CapabilitySnapshot(
            CapabilityQuery.Empty,
            new LumiCapabilityProvider(store).Load(),
            isComplete: true);

        var skill = Assert.Single(snapshot.Skills);
        Assert.Equal("Doc Creator", skill.Name);
        Assert.Equal(skillId, skill.LumiId);
        Assert.Equal("# body", skill.Content);
        Assert.True(skill.Origin.IsLumi);
        Assert.Equal("Lumi", skill.SourceLabel);

        Assert.Equal(agentId, Assert.Single(snapshot.Agents).LumiId);
        Assert.Equal(mcpId, Assert.Single(snapshot.McpServers).LumiId);
    }

    [Fact]
    public async Task MergesProvidersAndLetsLumiWinOnNameCollision()
    {
        var catalog = Catalog(
            Store(new Skill { Name = "Shared", Description = "Lumi copy", Content = "lumi" }),
            new StubProvider(
                SkillOf("Shared", CapabilityOrigin.Project, "project"),
                SkillOf("Repo Only", CapabilityOrigin.Project, "repo")));

        var snapshot = await LoadSnapshotAsync(catalog);

        Assert.Equal(2, snapshot.Skills.Count);
        var shared = snapshot.FindSkill("Shared");
        Assert.NotNull(shared);
        Assert.True(shared!.Origin.IsLumi);
        Assert.Equal("lumi", shared.Content);
        Assert.Equal(CapabilityOrigin.Project, snapshot.FindSkill("Repo Only")!.Origin);
    }

    [Fact]
    public async Task OrdersLumiCapabilitiesBeforeDiscoveredOnes()
    {
        var catalog = Catalog(
            Store(new Skill { Name = "Zebra" }),
            new StubProvider(
                SkillOf("Alpha", CapabilityOrigin.BuiltIn),
                SkillOf("Beta", CapabilityOrigin.Project)));

        var snapshot = await LoadSnapshotAsync(catalog);

        Assert.Equal(["Zebra", "Beta", "Alpha"], snapshot.Skills.Select(skill => skill.Name));
    }

    [Fact]
    public void GetSnapshot_ReturnsLumiCapabilitiesWithoutWaitingForProviders()
    {
        // The composer must paint without blocking on the Copilot runtime.
        var catalog = Catalog(Store(new Skill { Name = "Instant" }), new NeverCompletingProvider());

        var snapshot = catalog.GetSnapshot(CapabilityQuery.Empty);

        Assert.Equal("Instant", Assert.Single(snapshot.Skills).Name);
        Assert.False(snapshot.IsComplete);
    }

    [Fact]
    public void GetSnapshot_IsCompleteWhenThereAreNoProviders()
    {
        var catalog = Catalog(Store(new Skill { Name = "Only" }));

        Assert.True(catalog.GetSnapshot(CapabilityQuery.Empty).IsComplete);
    }

    [Fact]
    public void Constructor_RejectsNullProviders()
    {
        var store = Store();

        Assert.Throws<ArgumentException>(() => new CapabilityCatalog(
            new LumiCapabilityProvider(store),
            (ICapabilityProvider)null!));
    }

    [Fact]
    public async Task DisposedCatalog_RejectsReadsAndLoads()
    {
        var catalog = Catalog(Store());
        catalog.Dispose();

        Assert.Throws<ObjectDisposedException>(() => catalog.GetSnapshot(CapabilityQuery.Empty));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => catalog.LoadAsync(CapabilityQuery.Empty));
    }

    [Fact]
    public void ChatSessionStore_InjectsOneCatalogIntoEverySurface()
    {
        var dataStore = Store();
        var catalog = Catalog(dataStore);
        using var sessions = new ChatSessionStore(
            dataStore,
            TestCopilot.Shared,
            new ChatSurfaceRegistry(),
            capabilityCatalog: catalog);

        var first = sessions.AcquireDraft(projectId: null);
        var second = sessions.AcquireDraft(projectId: null);

        Assert.Same(catalog, GetCatalog(first));
        Assert.Same(catalog, GetCatalog(second));
    }

    [Fact]
    public async Task LoadAsync_ResolvesBeforeReturning()
    {
        // Session building must never act on a Lumi-only view: it would drop the chat's Copilot
        // agent and, because config discovery starts anything not explicitly disabled, start MCP
        // servers the user deselected.
        var catalog = Catalog(Store(), new StubProvider(SkillOf("Discovered", CapabilityOrigin.Project)));

        Assert.False(catalog.GetSnapshot(CapabilityQuery.Empty).IsComplete);

        var complete = await LoadSnapshotAsync(catalog);

        Assert.True(complete.IsComplete);
        Assert.Equal("Discovered", Assert.Single(complete.Skills).Name);
    }

    [Fact]
    public async Task LoadAsync_JoinsTheInFlightLoadInsteadOfStartingAnother()
    {
        // Regression: a second competing load could time out against the first and return an
        // unresolved snapshot, which the session builder then used as if it were whole.
        var provider = new GatedProvider();
        var catalog = Catalog(Store(), provider);

        var first = catalog.LoadAsync(CapabilityQuery.Empty);
        var joined = catalog.LoadAsync(CapabilityQuery.Empty);

        provider.Release();
        await Task.WhenAll(first, joined).WaitAsync(TimeSpan.FromSeconds(10));
        var snapshot = catalog.GetSnapshot(CapabilityQuery.Empty);

        Assert.True(snapshot.IsComplete);
        Assert.Equal(1, provider.Loads);
    }

    [Fact]
    public async Task LoadAsync_ReturnsTheReadableSnapshotWhenASourceIsDown()
    {
        var catalog = Catalog(Store(new Skill { Name = "Local" }), new SwitchableProvider());

        var snapshot = await LoadSnapshotAsync(catalog);

        Assert.False(snapshot.IsComplete);
        Assert.Equal("Local", Assert.Single(snapshot.Skills).Name);
    }

    [Fact]
    public async Task LoadAsync_RetriesAnIncompleteResult()
    {
        var provider = new SwitchableProvider();
        var catalog = Catalog(Store(), provider);

        Assert.False((await LoadSnapshotAsync(catalog)).IsComplete);

        provider.IsOnline = true;
        var second = await LoadSnapshotAsync(catalog);

        Assert.True(second.IsComplete);
        Assert.Equal("Late Arrival", Assert.Single(second.Skills).Name);
        Assert.Equal(2, provider.Loads);
    }

    [Fact]
    public async Task Reset_PreventsAnOldRuntimeLoadFromOverwritingTheNewGeneration()
    {
        var provider = new GenerationProvider();
        var catalog = Catalog(Store(), provider);

        var oldLoad = catalog.LoadAsync(CapabilityQuery.Empty);
        await provider.FirstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        catalog.Reset();
        var current = await LoadSnapshotAsync(catalog);
        Assert.Equal("New Runtime", Assert.Single(current.Skills).Name);

        provider.ReleaseFirstLoad();
        await oldLoad.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("New Runtime", Assert.Single(catalog.GetSnapshot(CapabilityQuery.Empty).Skills).Name);
    }

    [Fact]
    public void GetSnapshot_DoesNotStartProviderWork()
    {
        var provider = new CountingProvider();
        var catalog = Catalog(Store(new Skill { Name = "Local" }), provider);

        var snapshot = catalog.GetSnapshot(CapabilityQuery.Empty);

        Assert.Equal("Local", Assert.Single(snapshot.Skills).Name);
        Assert.Equal(0, provider.Loads);
    }

    [Fact]
    public async Task NonForcedLoad_JoinsAnActiveForcedRefreshBeforeReturningCache()
    {
        var provider = new VersionedProvider();
        var catalog = Catalog(Store(), provider);
        Assert.Equal("Version 1", Assert.Single((await LoadSnapshotAsync(catalog)).Skills).Name);

        provider.SkillName = "Version 2";
        provider.BlockNextLoad();
        var forced = catalog.LoadAsync(CapabilityQuery.Empty, forceRefresh: true);
        await WaitForAsync(() => provider.Loads == 2);

        var joined = catalog.LoadAsync(CapabilityQuery.Empty);
        provider.ReleaseNextLoad();
        await Task.WhenAll(forced, joined).WaitAsync(TimeSpan.FromSeconds(10));
        var snapshot = catalog.GetSnapshot(CapabilityQuery.Empty);

        Assert.Equal("Version 2", Assert.Single(snapshot.Skills).Name);
        Assert.Equal(2, provider.Loads);
    }

    [Fact]
    public async Task CompleteDiscovery_IsCachedUntilAForcedRefresh()
    {
        var provider = new CountingProvider();
        var catalog = Catalog(Store(), provider);

        await catalog.LoadAsync(CapabilityQuery.Empty);
        catalog.GetSnapshot(CapabilityQuery.Empty);
        await catalog.LoadAsync(CapabilityQuery.Empty);
        Assert.Equal(1, provider.Loads);

        await catalog.LoadAsync(CapabilityQuery.Empty, forceRefresh: true);
        Assert.Equal(2, provider.Loads);
    }

    [Fact]
    public async Task FailedForcedRefresh_PreservesTheLastCompleteSnapshot()
    {
        var provider = new SwitchableProvider { IsOnline = true };
        var catalog = Catalog(Store(), provider);
        var first = await LoadSnapshotAsync(catalog);
        Assert.True(first.IsComplete);

        provider.IsOnline = false;
        var refreshed = await LoadSnapshotAsync(catalog, forceRefresh: true);

        Assert.True(refreshed.IsComplete);
        Assert.Equal("Late Arrival", Assert.Single(refreshed.Skills).Name);
    }

    [Fact]
    public async Task CallerCancellation_DoesNotCancelTheSharedLoad()
    {
        var provider = new GatedProvider();
        var catalog = Catalog(Store(), provider);
        using var cancellation = new CancellationTokenSource();

        var canceledCaller = catalog.LoadAsync(CapabilityQuery.Empty, cancellationToken: cancellation.Token);
        var continuingCaller = catalog.LoadAsync(CapabilityQuery.Empty);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCaller);
        provider.Release();
        await continuingCaller.WaitAsync(TimeSpan.FromSeconds(10));
        var snapshot = catalog.GetSnapshot(CapabilityQuery.Empty);

        Assert.True(snapshot.IsComplete);
        Assert.Equal(1, provider.Loads);
    }

    [Fact]
    public async Task ProviderFailure_ReturnsAnIncompleteReadableSnapshot()
    {
        var catalog = Catalog(Store(new Skill { Name = "Kept" }), new ThrowingProvider());

        var snapshot = await LoadSnapshotAsync(catalog);

        Assert.Equal("Kept", Assert.Single(snapshot.Skills).Name);
        Assert.False(snapshot.IsComplete);
    }

    [Fact]
    public void Snapshot_ResolvesAgentsByNameSlugAndCase()
    {
        // Regression: agent selection matched ordinally against a UI collection, so a caller that
        // held the file-name form ("lumi-e2e-agent") or a different case silently selected nothing.
        var snapshot = new CapabilitySnapshot(
            CapabilityQuery.Empty,
            [
                new CapabilityDescriptor
                {
                    Kind = CapabilityKind.Agent,
                    Name = "Lumi E2E Agent",
                    Origin = CapabilityOrigin.Personal,
                },
            ],
            isComplete: true);

        Assert.NotNull(snapshot.FindAgent("Lumi E2E Agent"));
        Assert.NotNull(snapshot.FindAgent("lumi-e2e-agent"));
        Assert.NotNull(snapshot.FindAgent("LUMI_E2E_AGENT"));
        Assert.Null(snapshot.FindAgent("Some Other Agent"));
        // The canonical name is what callers persist, whichever form they matched on.
        Assert.Equal("Lumi E2E Agent", snapshot.FindAgent("lumi-e2e-agent")!.Name);
    }

    [Fact]
    public void Snapshot_ResolvesSlugifiedSkillNames()
    {
        // The native Copilot skill tool reports a slug ("Publish-New-Version") while the catalog is
        // keyed by the front-matter name ("Publish New Version"). Both must resolve to one skill.
        var snapshot = new CapabilitySnapshot(
            CapabilityQuery.Empty,
            [SkillOf("Publish New Version", CapabilityOrigin.Project, "# Body")],
            isComplete: true);

        Assert.Equal("Publish New Version", snapshot.FindSkill("Publish-New-Version")?.Name);
        Assert.Equal("Publish New Version", snapshot.FindSkill("publish new version")?.Name);
        Assert.Null(snapshot.FindSkill("Totally Different Skill"));
    }

    [Fact]
    public void Snapshot_HidesDisabledAndNonInvocableCapabilitiesFromPickers()
    {
        var snapshot = new CapabilitySnapshot(
            CapabilityQuery.Empty,
            [
                SkillOf("Visible", CapabilityOrigin.Project),
                SkillOf("Disabled", CapabilityOrigin.Project) with { IsEnabled = false },
                SkillOf("Internal", CapabilityOrigin.Project) with { IsUserInvocable = false },
            ],
            isComplete: true);

        Assert.Equal(3, snapshot.Skills.Count);
        Assert.Equal(["Visible"], snapshot.UserInvocable(CapabilityKind.Skill).Select(skill => skill.Name));
    }

    [Theory]
    [InlineData("project", "Project")]
    [InlineData("inherited", "Project")]
    [InlineData("workspace", "Workspace")]
    [InlineData("user", "Personal")]
    [InlineData("personal-copilot", "Personal")]
    [InlineData("personal-agents", "Personal")]
    [InlineData("custom", "Personal")]
    [InlineData("remote", "Remote")]
    [InlineData("builtin", "Built-in")]
    [InlineData("something-new", "Copilot")]
    public void Origin_MapsEverySdkSourceOntoAUserFacingLabel(string sdkSource, string expectedLabel)
        => Assert.Equal(expectedLabel, CapabilityOrigin.FromSdkSource(sdkSource).Label);

    [Fact]
    public void Origin_NamesThePluginThatSuppliedTheCapability()
    {
        var origin = CapabilityOrigin.FromSdkSource("plugin", "acme-tools");

        Assert.Equal(CapabilityOrigin.PluginId, origin.Id);
        Assert.Contains("acme-tools", origin.Label);
    }

    [Fact]
    public void Query_UsesPlatformPathCaseRules()
    {
        var a = new CapabilityQuery([@"C:\repo\", @"C:\REPO", @"C:\repo\sub"]);
        var b = new CapabilityQuery([@"c:\repo", @"c:\repo\sub"]);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(2, a.WorkingDirectories.Count);
            Assert.Equal(a.CacheKey, b.CacheKey);
            Assert.Equal(a, b);
        }
        else
        {
            Assert.Equal(3, a.WorkingDirectories.Count);
            Assert.NotEqual(a.CacheKey, b.CacheKey);
            Assert.NotEqual(a, b);
        }
    }

    [Fact]
    public void Query_KeepsAFilesystemRootIntact()
    {
        // Trimming separators blindly turns "C:\" into "C:", which names the current directory on
        // that drive rather than its root — so discovery would run against the wrong folder.
        var query = new CapabilityQuery([@"C:\"]);

        Assert.Equal([@"C:\"], query.WorkingDirectories);
    }

    private static DataStore Store(params Skill[] skills) => new(new AppData { Skills = [.. skills] });

    private static async Task<CapabilitySnapshot> LoadSnapshotAsync(
        CapabilityCatalog catalog,
        bool forceRefresh = false)
    {
        await catalog.LoadAsync(CapabilityQuery.Empty, forceRefresh);
        return catalog.GetSnapshot(CapabilityQuery.Empty);
    }

    private static CapabilityCatalog GetCatalog(ChatViewModel viewModel)
        => (CapabilityCatalog)(typeof(ChatViewModel)
            .GetField("_capabilityCatalog", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(viewModel)
            ?? throw new InvalidOperationException("Capability catalog was not found."));

    /// <summary>Polls until a background load lands, so the test never races the catalog.</summary>
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(15);
        }

        Assert.True(condition(), "Condition was not met before the timeout.");
    }

    private static CapabilityCatalog Catalog(DataStore store, params ICapabilityProvider[] providers)
        => new(new LumiCapabilityProvider(store), providers);

    private static CapabilityDescriptor SkillOf(string name, CapabilityOrigin origin, string? content = null)
        => new()
        {
            Kind = CapabilityKind.Skill,
            Name = name,
            Origin = origin,
            Content = content,
        };

    private sealed class StubProvider(params CapabilityDescriptor[] capabilities) : ICapabilityProvider
    {
        public string Id => "stub";

        public Task<CapabilityProviderResult> LoadAsync(CapabilityQuery query, CancellationToken cancellationToken)
            => Task.FromResult(new CapabilityProviderResult(capabilities));
    }

    private sealed class CountingProvider : ICapabilityProvider
    {
        public int Loads;

        public string Id => "counting";

        public Task<CapabilityProviderResult> LoadAsync(CapabilityQuery query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Loads);
            return Task.FromResult(CapabilityProviderResult.Empty);
        }
    }

    private sealed class ThrowingProvider : ICapabilityProvider
    {
        public string Id => "throwing";

        public Task<CapabilityProviderResult> LoadAsync(CapabilityQuery query, CancellationToken cancellationToken)
            => throw new InvalidOperationException("runtime unavailable");
    }

    private sealed class NeverCompletingProvider : ICapabilityProvider
    {
        public string Id => "never";

        public Task<CapabilityProviderResult> LoadAsync(CapabilityQuery query, CancellationToken cancellationToken)
            => new TaskCompletionSource<CapabilityProviderResult>().Task;
    }

    /// <summary>Stands in for the Copilot runtime coming online after Lumi has already painted.</summary>
    private sealed class SwitchableProvider : ICapabilityProvider
    {
        public int Loads;
        public bool IsOnline;

        public string Id => "switchable";

        public Task<CapabilityProviderResult> LoadAsync(CapabilityQuery query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Loads);
            return Task.FromResult(IsOnline
                ? new CapabilityProviderResult([SkillOf("Late Arrival", CapabilityOrigin.Personal)])
                : CapabilityProviderResult.Unavailable);
        }
    }

    private sealed class VersionedProvider : ICapabilityProvider
    {
        private TaskCompletionSource? _nextLoadGate;

        public int Loads;
        public string SkillName { get; set; } = "Version 1";
        public string Id => "versioned";

        public void BlockNextLoad()
            => _nextLoadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseNextLoad() => _nextLoadGate?.TrySetResult();

        public async Task<CapabilityProviderResult> LoadAsync(
            CapabilityQuery query,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Loads);
            var skillName = SkillName;
            var gate = _nextLoadGate;
            if (gate is not null)
                await gate.Task.ConfigureAwait(false);
            return new CapabilityProviderResult([SkillOf(skillName, CapabilityOrigin.Project)]);
        }
    }

    private sealed class GenerationProvider : ICapabilityProvider
    {
        private readonly TaskCompletionSource _firstLoadGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _loads;

        public TaskCompletionSource FirstLoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "generation";

        public void ReleaseFirstLoad() => _firstLoadGate.TrySetResult();

        public async Task<CapabilityProviderResult> LoadAsync(
            CapabilityQuery query,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _loads) == 1)
            {
                FirstLoadStarted.TrySetResult();
                await _firstLoadGate.Task.ConfigureAwait(false);
                return new CapabilityProviderResult([SkillOf("Old Runtime", CapabilityOrigin.Project)]);
            }

            return new CapabilityProviderResult([SkillOf("New Runtime", CapabilityOrigin.Project)]);
        }
    }

    /// <summary>Holds a load open so a second caller has to join rather than start its own.</summary>
    private sealed class GatedProvider : ICapabilityProvider
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Loads;

        public string Id => "gated";

        public void Release() => _gate.TrySetResult();

        public async Task<CapabilityProviderResult> LoadAsync(CapabilityQuery query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Loads);
            await _gate.Task.ConfigureAwait(false);
            return new CapabilityProviderResult([SkillOf("Gated", CapabilityOrigin.Project)]);
        }
    }
}
