using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Covers <see cref="ModelCatalogCache"/>'s refresh semantics. These paths were untestable while the
/// logic lived inside <see cref="CopilotService"/> and needed a live CLI connection — the cache takes
/// fetch delegates instead, so failures, races and freshness can be driven deterministically.
/// </summary>
public class ModelCatalogCacheTests
{
    private static ModelInfo Model(string id) => new() { Id = id, Name = id };

    private static ModelInfo RichModel(string? changedField = null) => new()
    {
        Id = "gpt-5.4",
        Name = "GPT 5.4",
        Capabilities = new ModelCapabilities
        {
            Limits = new ModelLimits { MaxContextWindowTokens = 128_000 },
            Supports = new ModelSupports
            {
                Vision = changedField == "vision",
                ReasoningEffort = changedField == "reasoning"
            }
        },
        Billing = new ModelBilling { Multiplier = changedField == "billing" ? 0.5 : 1 },
        Policy = new ModelPolicy { State = changedField == "policy" ? "unconfigured" : "enabled" }
    };

    private static ModelContextWindowCatalog Catalog(params string[] longContextModelIds)
        => new(
            new HashSet<string>(longContextModelIds, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ModelContextWindowLimits>(StringComparer.OrdinalIgnoreCase));

    private static ModelContextWindowCatalog CatalogWithLimits(string modelId, long @default, long? longContext)
        => new(
            new HashSet<string>(longContext is null ? [] : new[] { modelId }, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ModelContextWindowLimits>(StringComparer.OrdinalIgnoreCase)
            {
                [modelId] = new(@default, longContext)
            });

    [Fact]
    public async Task Refresh_DoesNotPublish_WhenCatalogIsUnchanged()
    {
        var published = 0;
        var cache = new ModelCatalogCache(
            _ => Task.FromResult(new List<ModelInfo> { Model("gpt-5.4") }),
            _ => Task.FromResult<ModelContextWindowCatalog?>(Catalog()));
        cache.Changed += _ => published++;

        Assert.True(await cache.RefreshAsync());
        Assert.False(await cache.RefreshAsync(force: true));
        Assert.Equal(1, published);
    }

    [Fact]
    public async Task Refresh_Publishes_WhenNewModelAppears()
    {
        var models = new List<ModelInfo> { Model("gpt-5.4") };
        ModelCatalogSnapshot? latest = null;
        var cache = new ModelCatalogCache(
            _ => Task.FromResult(models),
            _ => Task.FromResult<ModelContextWindowCatalog?>(Catalog()));
        cache.Changed += snapshot => latest = snapshot;

        await cache.RefreshAsync();
        models = [Model("gpt-5.4"), Model("claude-opus-6")];

        Assert.True(await cache.RefreshAsync(force: true));
        Assert.NotNull(latest);
        Assert.Contains(latest!.Models, m => m.Id == "claude-opus-6");
    }

    [Theory]
    [InlineData("vision")]
    [InlineData("reasoning")]
    [InlineData("billing")]
    [InlineData("policy")]
    public async Task Refresh_Publishes_WhenRichPickerMetadataChanges(string changedField)
    {
        var models = new List<ModelInfo> { RichModel() };
        var published = 0;
        var cache = new ModelCatalogCache(
            _ => Task.FromResult(models),
            _ => Task.FromResult<ModelContextWindowCatalog?>(Catalog()));
        cache.Changed += _ => published++;

        Assert.True(await cache.RefreshAsync());
        models = [RichModel(changedField)];

        Assert.True(await cache.RefreshAsync(force: true));
        Assert.Equal(2, published);
    }

    /// <summary>
    /// The bug a human reviewer caught: the raw context-window RPC is a preview API that is expected
    /// to fail, and committing its empty result would strip long context from every model — and that
    /// loss is then persisted per chat.
    /// </summary>
    [Fact]
    public async Task Refresh_KeepsCachedContextWindows_WhenContextFetchFails()
    {
        var contextWindows = (ModelContextWindowCatalog?)CatalogWithLimits("gpt-5.4", 128_000, 1_000_000);
        var cache = new ModelCatalogCache(
            _ => Task.FromResult(new List<ModelInfo> { Model("gpt-5.4") }),
            _ => Task.FromResult(contextWindows));

        await cache.RefreshAsync();
        contextWindows = null; // the raw RPC now fails

        await cache.RefreshAsync(force: true);

        var cached = await cache.GetContextWindowsAsync();
        Assert.Contains("gpt-5.4", cached.LongContextModelIds);
        Assert.Equal(1_000_000, cached.Limits["gpt-5.4"].LongContext);
    }

    [Fact]
    public async Task Refresh_DoesNotPublish_WhenContextFetchFailsAndNothingIsCached()
    {
        var published = 0;
        var cache = new ModelCatalogCache(
            _ => Task.FromResult(new List<ModelInfo> { Model("gpt-5.4") }),
            _ => Task.FromResult<ModelContextWindowCatalog?>(null));
        cache.Changed += _ => published++;

        Assert.False(await cache.RefreshAsync());
        Assert.Equal(0, published);
    }

    [Fact]
    public async Task Refresh_SkipsFetch_WhileCatalogIsFresh()
    {
        var fetches = 0;
        var cache = new ModelCatalogCache(
            _ => { fetches++; return Task.FromResult(new List<ModelInfo> { Model("gpt-5.4") }); },
            _ => Task.FromResult<ModelContextWindowCatalog?>(Catalog()));

        await cache.RefreshAsync();
        await cache.RefreshAsync();
        await cache.RefreshAsync();

        Assert.Equal(1, fetches);

        await cache.RefreshAsync(force: true);
        Assert.Equal(2, fetches);
    }

    /// <summary>
    /// A reconnect during a refresh invalidates the cache; the in-flight read must be discarded
    /// instead of resurrecting the catalog the reconnect just cleared.
    /// </summary>
    [Fact]
    public async Task Refresh_DiscardsResult_WhenInvalidatedMidFetch()
    {
        var released = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var published = 0;

        var cache = new ModelCatalogCache(
            async _ =>
            {
                entered.TrySetResult();
                await released.Task;
                return [Model("gpt-5.4")];
            },
            _ => Task.FromResult<ModelContextWindowCatalog?>(Catalog()));
        cache.Changed += _ => published++;

        var refresh = cache.RefreshAsync();
        await entered.Task;
        cache.Invalidate();
        released.SetResult();

        Assert.False(await refresh);
        Assert.Equal(0, published);
    }

    [Fact]
    public async Task Refresh_CollapsesConcurrentCallers_OntoOneFetch()
    {
        var released = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var fetches = 0;

        var cache = new ModelCatalogCache(
            async _ =>
            {
                Interlocked.Increment(ref fetches);
                entered.TrySetResult();
                await released.Task;
                return [Model("gpt-5.4")];
            },
            _ => Task.FromResult<ModelContextWindowCatalog?>(Catalog()));

        var first = cache.RefreshAsync();
        await entered.Task;
        var second = await cache.RefreshAsync();
        released.SetResult();

        Assert.True(await first);
        Assert.False(second);
        Assert.Equal(1, fetches);
    }

    [Fact]
    public async Task Refresh_KeepsCachedCatalog_WhenModelFetchThrows()
    {
        var fail = false;
        var cache = new ModelCatalogCache(
            _ => fail
                ? Task.FromException<List<ModelInfo>>(new InvalidOperationException("Not connected"))
                : Task.FromResult(new List<ModelInfo> { Model("gpt-5.4") }),
            _ => Task.FromResult<ModelContextWindowCatalog?>(Catalog()));

        await cache.RefreshAsync();
        fail = true;

        Assert.False(await cache.RefreshAsync(force: true));
        Assert.Equal("gpt-5.4", (await cache.GetModelsAsync()).Single().Id);
    }

    [Fact]
    public async Task Invalidate_ForcesTheNextReadToFetchAgain()
    {
        var fetches = 0;
        var cache = new ModelCatalogCache(
            _ => { fetches++; return Task.FromResult(new List<ModelInfo> { Model("gpt-5.4") }); },
            _ => Task.FromResult<ModelContextWindowCatalog?>(Catalog()));

        await cache.GetModelsAsync();
        await cache.GetModelsAsync();
        Assert.Equal(1, fetches);

        cache.Invalidate();
        await cache.GetModelsAsync();
        Assert.Equal(2, fetches);
    }

    [Fact]
    public async Task GetContextWindows_FallsBackToEmpty_WhenFetchFailsOnColdStart()
    {
        var cache = new ModelCatalogCache(
            _ => Task.FromResult(new List<ModelInfo>()),
            _ => Task.FromResult<ModelContextWindowCatalog?>(null));

        var catalog = await cache.GetContextWindowsAsync();

        Assert.Empty(catalog.LongContextModelIds);
        Assert.Empty(catalog.Limits);
    }
}
