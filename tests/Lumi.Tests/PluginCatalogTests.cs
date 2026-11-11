using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Lumi.Services.Discover;
using Lumi.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;
using Xunit;
using GitHubRepoRef = Lumi.Services.Discover.GitHubRepoRef;
using JsonRpc = StreamJsonRpc.JsonRpc;

namespace Lumi.Tests;

public sealed class PluginCatalogTests
{
    internal const string Source = "URL: https://octo_contoso@github.com/contoso/private-plugin.git";

    [Fact]
    public async Task Browse_PreservesVersionHintsWhenOptionalGitHubMetadataIsReadable()
    {
        using var fixture = new PluginCatalogFixture();
        using var rpc = new PluginCatalogRpc();
        var catalog = fixture.Service(rpc);
        var marketplace = Assert.Single(await catalog.ListMarketplacesAsync(CancellationToken.None));
        Assert.Equal("contoso/private-plugin", marketplace.Repository!.FullName);
        fixture.WriteCatalog("0.3.1");

        var listing = Assert.Single(await catalog.BrowseAsync(marketplace, CancellationToken.None));
        Assert.Equal("0.3.1", listing.Version);
        var card = DiscoverCard.ForPlugin(listing, _ => { });
        card.Installation = new("private-plugin", "private-plugin", "0.3.0", true, null, false);
        Assert.True(card.HasUpdate);
        Assert.False(card.IsUpdateUnknown);

        fixture.WriteCatalog("0.3.2");
        await catalog.RefreshAsync(CancellationToken.None);
        Assert.Equal("0.3.2", Assert.Single(await catalog.BrowseAsync(marketplace, CancellationToken.None)).Version);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Browse_PrivateOrMissingMetadataIsUnknown_NotCurrent(HttpStatusCode metadataStatus)
    {
        using var fixture = new PluginCatalogFixture(metadataStatus);
        using var rpc = new PluginCatalogRpc();
        var service = fixture.Service(rpc);
        var listing = Assert.Single(await service.BrowseAsync(fixture.Marketplace, CancellationToken.None));
        Assert.Null(listing.Version);
        var card = DiscoverCard.ForPlugin(listing, _ => { });
        card.Installation = new("private-plugin", "private-plugin", "0.3.0", true, null, false);
        Assert.False(card.HasUpdate);
        Assert.True(card.IsUpdateUnknown);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"plugins":[{"name":"private-plugin"}]}""")]
    [InlineData("""{"plugins":[{"name":"private-plugin","version":"latest"}]}""")]
    public async Task Browse_UnusableVersionMetadataNeverClaimsAnUpdate(string body)
    {
        using var fixture = new PluginCatalogFixture();
        using var rpc = new PluginCatalogRpc();
        fixture.Files[PluginCatalogFixture.ManifestKey] = body;
        var listing = Assert.Single(await fixture.Service(rpc).BrowseAsync(fixture.Marketplace, CancellationToken.None));
        Assert.Null(DisplayNames.CompareVersions(listing.Version, "0.3.0"));
    }

    [Fact]
    public async Task Refresh_FailureIsSurfacedAndTheOldCandidateVersionIsNotOffered()
    {
        using var fixture = new PluginCatalogFixture();
        using var rpc = new PluginCatalogRpc { RefreshSucceeds = false };
        fixture.WriteCatalog("0.3.1");
        var service = fixture.Service(rpc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RefreshAsync(CancellationToken.None));
        Assert.Null(Assert.Single(await service.BrowseAsync(fixture.Marketplace, CancellationToken.None)).Version);
        rpc.RefreshSucceeds = true;
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal("0.3.1", Assert.Single(await service.BrowseAsync(fixture.Marketplace, CancellationToken.None)).Version);
    }

    [Fact]
    public async Task Update_FailureDoesNotInvalidateSessionsOrChangeTheInstallation()
    {
        using var fixture = new PluginCatalogFixture();
        using var rpc = new PluginCatalogRpc { FailUpdate = true };
        var changes = 0;
        var service = fixture.Service(rpc, changed: () => changes++);
        var installation = new PluginInstallation("private-plugin", "private-plugin", "0.3.0", true, null, false);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => service.UpdateAsync(installation, CancellationToken.None));
        Assert.Contains("os error 5", error.Message);
        Assert.Equal(0, changes);
        Assert.Equal("0.3.0", installation.Version);
        Assert.Equal(["update"], rpc.Calls);
    }

    [Fact]
    public async Task Update_UsesTheSdkReportedVersion_AndNotifiesOnlyAfterSuccess()
    {
        using var fixture = new PluginCatalogFixture();
        using var rpc = new PluginCatalogRpc { UpdatedVersion = "0.3.2" };
        var service = fixture.Service(rpc, changed: () => rpc.Calls.Add("changed"));
        var result = await service.UpdateAsync(new("private-plugin", "private-plugin", "0.3.0", true, null, false), CancellationToken.None);
        Assert.Equal("0.3.2", result.NewVersion);
        Assert.Equal(["update", "changed"], rpc.Calls);
    }

    [Fact]
    public async Task Browse_NonGitHubSourceStillListsTheRuntimePluginWithUnknownVersion()
    {
        using var fixture = new PluginCatalogFixture();
        using var rpc = new PluginCatalogRpc();
        var local = fixture.Marketplace with { Source = "Local: fixture", Repository = null };
        var listing = Assert.Single(await fixture.Service(rpc).BrowseAsync(local, CancellationToken.None));
        Assert.Equal("private-plugin", listing.Name);
        Assert.Null(listing.Version);
    }
}

internal sealed class PluginCatalogFixture : IDisposable
{
    private readonly HttpClient _http;
    public string Root { get; } = Path.Combine(TestProfile.Root, "plugin-catalog-" + Guid.NewGuid().ToString("N"));
    public PluginMarketplace Marketplace { get; } = new("private-plugin", PluginCatalogTests.Source, GitHubRepoRef.TryParse(PluginCatalogTests.Source), false, false);
    public const string ManifestKey = "raw/contoso/private-plugin/HEAD/.github/plugin/marketplace.json";
    public Dictionary<string, string> Files { get; } = [];

    public PluginCatalogFixture(HttpStatusCode missingStatus = HttpStatusCode.NotFound)
        => _http = new HttpClient(new DiscoverServicesTests.FakeGitHub(Files, missingStatus));

    public void WriteCatalog(string? version)
        => Files[ManifestKey] = JsonSerializer.Serialize(new
        {
            name = "private-plugin",
            plugins = new[] { new { name = "private-plugin", source = "./", version } },
        });

    public PluginCatalogService Service(PluginCatalogRpc rpc, GitHubRepoReader? reader = null, Action? changed = null)
        => new(() => rpc.Client, changed ?? (() => { }), reader ?? new GitHubRepoReader(_http, static () => "fixture-token"));

    public void Dispose()
    {
        _http.Dispose();
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}

/// <summary>Exercises the real generated SDK calls in memory; no CLI, credentials or user files.</summary>
internal sealed class PluginCatalogRpc : IDisposable
{
    private readonly JsonRpc _server;
    private readonly IDisposable _sdkRpc;
    public CopilotClient Client { get; }
    public bool RefreshSucceeds { get; set; } = true;
    public bool FailUpdate { get; set; }
    public string? UpdatedVersion { get; set; } = "0.3.1";
    public List<string> Calls { get; } = [];

    public PluginCatalogRpc()
    {
        var requests = new Pipe();
        var responses = new Pipe();
        _server = new JsonRpc(new HeaderDelimitedMessageHandler(
            responses.Writer.AsStream(), requests.Reader.AsStream(), new JsonMessageFormatter()));
        _server.AddLocalRpcTarget(this);
        _server.StartListening();
        var rpcType = typeof(CopilotClient).Assembly.GetType("GitHub.Copilot.JsonRpc", throwOnError: true)!;
        _sdkRpc = (IDisposable)Activator.CreateInstance(rpcType,
            requests.Writer.AsStream(), responses.Reader.AsStream(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() },
            NullLogger.Instance)!;
        rpcType.GetMethod("StartListening")!.Invoke(_sdkRpc, null);
        Client = (CopilotClient)RuntimeHelpers.GetUninitializedObject(typeof(CopilotClient));
        GC.SuppressFinalize(Client);
        var serverRpc = Activator.CreateInstance(typeof(ServerRpc), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null, args: [_sdkRpc], culture: null);
        typeof(CopilotClient).GetField("_serverRpc", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Client, serverRpc);
    }

    [JsonRpcMethod("plugins.marketplaces.list")]
    public object Marketplaces() => new { marketplaces = new[] { new { name = "private-plugin", source = PluginCatalogTests.Source } } };

    [JsonRpcMethod("plugins.marketplaces.browse", UseSingleObjectParameterDeserialization = true)]
    public object Browse(object request)
    {
        return new { plugins = new[] { new { name = "private-plugin", description = "Private plugin" } } };
    }

    [JsonRpcMethod("plugins.marketplaces.refresh", UseSingleObjectParameterDeserialization = true)]
    public object Refresh(object request) => new { results = new[] { new { name = "private-plugin", success = RefreshSucceeds, error = RefreshSucceeds ? null : "Access denied" } } };

    [JsonRpcMethod("plugins.update", UseSingleObjectParameterDeserialization = true)]
    public object Update(object request)
    {
        Calls.Add("update");
        if (FailUpdate)
            throw new InvalidOperationException("Failed to install plugin: Access is denied. (os error 5)");
        return new { previousVersion = "0.3.0", newVersion = UpdatedVersion, skillsInstalled = 0 };
    }

    [JsonRpcMethod("plugins.list")]
    public object List()
    {
        Calls.Add("list");
        return new { plugins = new[] { new { name = "private-plugin", marketplace = "private-plugin", version = "0.3.0", enabled = true } } };
    }

    public void Dispose()
    {
        _sdkRpc.Dispose();
        _server.Dispose();
    }
}
