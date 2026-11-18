using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Copilot;
using Microsoft.Copilot.Rpc;
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

    [Fact]
    public async Task AddMarketplace_CancelStopsTheRuntimesDownload_AndLeavesItsLock()
    {
        using var fixture = new PluginCatalogFixture();
        using var rpc = new PluginCatalogRpc();
        var download = new FakeMarketplaceDownload(fixture.Root, rpc);
        rpc.OnMarketplaceAdd = () =>
        {
            download.Begin();
            download.Receive(2048);
        };
        var service = download.Service(rpc);
        using var cancel = new CancellationTokenSource();
        var progress = new RecordedProgress<MarketplaceDownloadProgress?>();

        var adding = service.AddMarketplaceAsync(" contoso/plugin-catalog ", cancel.Token, progress);
        await WaitFor(() => progress.Contains(new MarketplaceDownloadProgress(MarketplaceDownloadPhase.Downloading, 2048)));
        cancel.Cancel();

        var stopped = await Assert.ThrowsAsync<MarketplaceAddStoppedException>(() => adding);
        Assert.False(stopped.Stalled);
        Assert.False(stopped.StillRunning);
        Assert.Equal([FakeMarketplaceDownload.GitProcessId], download.Stopped);
        // The runtime locks it natively: removing it could let another add take a lock still held.
        Assert.True(File.Exists(download.Lock));
        download.AssertOtherMarketplaceKept();
    }

    [Fact]
    public async Task AddMarketplace_ReportsAGrowingDownloadThenItsUnpacking_WithoutStoppingIt()
    {
        using var fixture = new PluginCatalogFixture();
        using var rpc = new PluginCatalogRpc();
        var download = new FakeMarketplaceDownload(fixture.Root, rpc);
        rpc.OnMarketplaceAdd = () =>
        {
            download.Begin();
            download.Receive(1024);
        };
        var service = download.Service(rpc, stallTimeout: TimeSpan.FromSeconds(1));
        var progress = new RecordedProgress<MarketplaceDownloadProgress?>();

        var adding = service.AddMarketplaceAsync("contoso/plugin-catalog", CancellationToken.None, progress);
        await WaitFor(() => progress.Contains(new MarketplaceDownloadProgress(MarketplaceDownloadPhase.Downloading, 1024)));

        // Longer than the stall timeout in all, but never without progress.
        for (var bytes = 2048; bytes <= 16 * 1024; bytes += 1024)
        {
            await Task.Delay(100);
            download.Receive(bytes);
        }

        await WaitFor(() => progress.Contains(new MarketplaceDownloadProgress(MarketplaceDownloadPhase.Downloading, 16 * 1024)));
        download.Unpack();
        await WaitFor(() => progress.Contains(new MarketplaceDownloadProgress(MarketplaceDownloadPhase.Unpacking, 16 * 1024)));

        // Writing out the files is local work: it may take longer than the stall timeout.
        await Task.Delay(1500);
        Assert.False(adding.IsCompleted);
        rpc.MarketplaceAdd.SetResult("plugin-catalog");

        Assert.Equal("plugin-catalog", await adding);
        Assert.Empty(download.Stopped);
    }

    [Fact]
    public async Task AddMarketplace_StopsADownloadThatMakesNoProgress()
    {
        using var fixture = new PluginCatalogFixture();
        using var rpc = new PluginCatalogRpc();
        var download = new FakeMarketplaceDownload(fixture.Root, rpc);
        rpc.OnMarketplaceAdd = () =>
        {
            download.Begin();
            download.Receive(4096);
        };
        var service = download.Service(rpc, stallTimeout: TimeSpan.FromMilliseconds(300));

        var stopped = await Assert.ThrowsAsync<MarketplaceAddStoppedException>(
            () => service.AddMarketplaceAsync("contoso/plugin-catalog", CancellationToken.None));

        Assert.True(stopped.Stalled);
        Assert.False(stopped.StillRunning);
        Assert.Equal([FakeMarketplaceDownload.GitProcessId], download.Stopped);
    }

    [Fact]
    public async Task AddMarketplace_WithNoDownloadToStop_SaysTheRuntimeMayStillAddIt()
    {
        using var fixture = new PluginCatalogFixture();
        using var rpc = new PluginCatalogRpc();
        var download = new FakeMarketplaceDownload(fixture.Root, rpc);
        var service = download.Service(rpc);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var stopped = await Assert.ThrowsAsync<MarketplaceAddStoppedException>(
            () => service.AddMarketplaceAsync("contoso/plugin-catalog", cancel.Token));

        Assert.True(stopped.StillRunning);
        Assert.Empty(download.Stopped);
        rpc.MarketplaceAdd.SetResult("plugin-catalog");
    }

    [Fact]
    public async Task AddMarketplace_CancelLeavesTheRuntimesOtherDownloadsAlone()
    {
        using var fixture = new PluginCatalogFixture();
        using var rpc = new PluginCatalogRpc();
        var download = new FakeMarketplaceDownload(fixture.Root, rpc);
        // Adds of the same path on another host, and of this repository at a ref, run meanwhile in the
        // same runtime and cache.
        var otherHost = Path.Combine(download.Marketplaces, ".copilot-clone-other");
        var otherRef = Path.Combine(download.Marketplaces, MarketplaceDownloads.PinnedFolder, ".copilot-clone-nightly");
        rpc.OnMarketplaceAdd = () =>
        {
            foreach (var other in new[] { otherHost, otherRef })
            {
                Directory.CreateDirectory(Path.Combine(other, "repo", ".git", "objects", "pack"));
                File.WriteAllBytes(Path.Combine(other, "repo", ".git", "objects", "pack", "tmp_pack_y"), new byte[512]);
            }

            download.Run(new GitProcess(7003, $"git clone --depth 1 https://gitlab.com/contoso/plugin-catalog.git {Path.Combine(otherHost, "repo")}", StartedByRuntime: true));
            download.Run(new GitProcess(7004, $"git clone --depth 1 --branch nightly https://github.com/contoso/plugin-catalog.git {Path.Combine(otherRef, "repo")}", StartedByRuntime: true));
            download.Begin();
            download.Receive(2048);
        };
        var service = download.Service(rpc);
        using var cancel = new CancellationTokenSource();
        var progress = new RecordedProgress<MarketplaceDownloadProgress?>();

        var adding = service.AddMarketplaceAsync("contoso/plugin-catalog", cancel.Token, progress);
        await WaitFor(() => progress.Contains(new MarketplaceDownloadProgress(MarketplaceDownloadPhase.Downloading, 2048)));
        cancel.Cancel();
        await Assert.ThrowsAsync<MarketplaceAddStoppedException>(() => adding);

        Assert.Equal([FakeMarketplaceDownload.GitProcessId], download.Stopped);
        Assert.True(Directory.Exists(otherHost));
        Assert.True(Directory.Exists(otherRef));
    }

    [Fact]
    public async Task AddMarketplace_ThatCantBeObservedIsNeverStoppedAsStalled()
    {
        using var fixture = new PluginCatalogFixture();
        using var rpc = new PluginCatalogRpc();
        var download = new FakeMarketplaceDownload(fixture.Root, rpc);
        var service = download.Service(rpc, stallTimeout: TimeSpan.FromMilliseconds(200));

        // No clone of this source is visible, as when Lumi can't read the runtime's processes.
        var adding = service.AddMarketplaceAsync("contoso/plugin-catalog", CancellationToken.None);
        await Task.Delay(1000);
        Assert.False(adding.IsCompleted);

        rpc.MarketplaceAdd.SetResult("plugin-catalog");
        Assert.Equal("plugin-catalog", await adding);
        Assert.Empty(download.Stopped);
    }

    [Fact]
    public void CloneInto_FindsTheStagingFolderOnlyInTheRuntimesClone()
    {
        var marketplaces = Path.Combine(TestProfile.Root, "cache with space", "marketplaces");
        var staging = Path.Combine(marketplaces, ".copilot-clone-jxF2ol");
        var target = Path.Combine(staging, "repo");
        const string Git = "\"git\" -c core.askPass= -c credential.interactive=never -c core.longpaths=true -c core.fsmonitor=false clone --depth 1 --progress --config core.longpaths=true";
        const string Url = "https://github.com/contoso/plugin-catalog.git";
        var clone = new MarketplaceClone(staging, target, Url, Branch: null, Pinned: false);

        Assert.Equal(clone, MarketplaceDownloads.CloneInto($"{Git} {Url} \"{target}\"", marketplaces));
        // ps on Linux and macOS never quotes arguments.
        Assert.Equal(clone, MarketplaceDownloads.CloneInto($"{Git} {Url} {target}", marketplaces));
        // An add at a ref clones it into _pinned.
        var pinned = Path.Combine(marketplaces, MarketplaceDownloads.PinnedFolder, ".copilot-clone-fDHIUN", "repo");
        Assert.Equal(
            new MarketplaceClone(Path.GetDirectoryName(pinned)!, pinned, Url, "main", Pinned: true),
            MarketplaceDownloads.CloneInto($"{Git} --branch main {Url} \"{pinned}\"", marketplaces));
        // git's helper for the same clone, a refresh of an added marketplace and a clone elsewhere are not the download.
        Assert.Null(MarketplaceDownloads.CloneInto($"git --shallow-file {target}/.git/shallow.lock index-pack --stdin -v --fix-thin", marketplaces));
        Assert.Null(MarketplaceDownloads.CloneInto($"git -C {Path.Combine(marketplaces, "github-awesome-copilot")} fetch origin", marketplaces));
        Assert.Null(MarketplaceDownloads.CloneInto($"git clone https://example.test/m.git {Path.Combine(TestProfile.Root, "elsewhere", ".copilot-clone-x", "repo")}", marketplaces));
        if (OperatingSystem.IsWindows())
        {
            // COPILOT_CACHE_HOME, and so the runtime's paths, may use forward slashes.
            Assert.Equal(clone, MarketplaceDownloads.CloneInto($"{Git} {Url} \"{target.Replace('\\', '/')}\"", marketplaces));
            Assert.Equal(clone, MarketplaceDownloads.CloneInto($"{Git} {Url} \"{target}\"", marketplaces.Replace('\\', '/')));
        }
    }

    [Theory]
    [InlineData("contoso/plugin-catalog", "https://github.com/contoso/plugin-catalog.git {staging}", true)]
    [InlineData("https://octo_contoso@github.com/contoso/plugin-catalog.git", "https://octo_contoso@github.com/contoso/plugin-catalog.git {staging}", true)]
    [InlineData("git@github.com:contoso/plugin-catalog.git", "git@github.com:contoso/plugin-catalog.git {staging}", true)]
    [InlineData("https://dev.azure.com/contoso/tools/_git/plugins", "https://dev.azure.com/contoso/tools/_git/plugins {staging}", true)]
    [InlineData("http://127.0.0.1:4711/marketplace.git", "http://127.0.0.1:4711/marketplace.git {staging}", true)]
    [InlineData("contoso/plugin", "https://github.com/contoso/plugin-catalog.git {staging}", false)]
    [InlineData("contoso/plugin-catalog", "https://github.com/microsoft/work-iq.git {staging}", false)]
    [InlineData("contoso/plugin-catalog", "https://gitlab.com/contoso/plugin-catalog.git {staging}", false)]
    [InlineData("https://dev.azure.com/contoso/tools/_git/plugins", "https://notdev.azure.com/contoso/tools/_git/plugins {staging}", false)]
    // An add at a ref: a branch or tag is cloned by name, a commit without one, both into _pinned.
    [InlineData("Contoso/Plugin-Catalog#main", "--branch main https://github.com/contoso/plugin-catalog.git {pinned}", true)]
    [InlineData("contoso/plugin-catalog#refs/tags/v1.2", "--branch v1.2 https://github.com/contoso/plugin-catalog.git {pinned}", true)]
    [InlineData("contoso/plugin-catalog#0123456789abcdef0123456789abcdef01234567", "https://github.com/contoso/plugin-catalog.git {pinned}", true)]
    [InlineData("contoso/plugin-catalog#main", "--branch nightly https://github.com/contoso/plugin-catalog.git {pinned}", false)]
    [InlineData("contoso/plugin-catalog", "https://github.com/contoso/plugin-catalog.git {pinned}", false)]
    public void IsCloneOf_MatchesTheSourcesRepositoryAndRef(string source, string arguments, bool expected)
    {
        var marketplaces = Path.Combine(TestProfile.Root, "marketplaces");
        var staging = Path.Combine(marketplaces, ".copilot-clone-ab12cd", "repo");
        var pinned = Path.Combine(marketplaces, MarketplaceDownloads.PinnedFolder, ".copilot-clone-ef34gh", "repo");
        var commandLine = "\"git\" -c core.askPass= clone --depth 1 --progress "
                          + arguments.Replace("{staging}", $"\"{staging}\"").Replace("{pinned}", $"\"{pinned}\"");

        var clone = Assert.IsType<MarketplaceClone>(MarketplaceDownloads.CloneInto(commandLine, marketplaces));
        Assert.Equal(expected, MarketplaceDownloads.IsCloneOf(clone, source));
    }

    [Theory]
    [InlineData("./marketplaces/local")]
    [InlineData("C:\\marketplaces\\local")]
    [InlineData("file:///C:/marketplaces/local")]
    public void CloneAddress_IsNoneForALocalFolder(string source)
        => Assert.Null(MarketplaceDownloads.CloneAddress(source));

    [Fact]
    public void RemoveAbandoned_RemovesOnlyClonesNothingWorksOn_AndNoLocks()
    {
        using var fixture = new PluginCatalogFixture();
        var download = new FakeMarketplaceDownload(fixture.Root, rpc: null);
        var hourAgo = DateTime.UtcNow.AddHours(-1);

        // Adds interrupted when Lumi closed, without and at a ref, and the lock the runtime kept for one.
        var interrupted = Abandoned(Path.Combine(download.Marketplaces, ".copilot-clone-old"));
        // git makes its object files read-only.
        File.SetAttributes(Path.Combine(interrupted, "repo", ".git", "objects", "pack", "pack-abc.pack"), FileAttributes.ReadOnly);
        var pinned = Abandoned(Path.Combine(download.Marketplaces, MarketplaceDownloads.PinnedFolder, ".copilot-clone-old"));
        File.WriteAllText(download.Lock, "");
        File.SetCreationTimeUtc(download.Lock, hourAgo);
        File.SetLastWriteTimeUtc(download.Lock, hourAgo);
        // Long clones: one git still writes, and one the runtime moves into place now that git is done.
        var busy = Abandoned(Path.Combine(download.Marketplaces, ".copilot-clone-busy"));
        download.Run(new GitProcess(9, $"git clone https://example.test/m.git {Path.Combine(busy, "repo")}", StartedByRuntime: false));
        var finished = Abandoned(Path.Combine(download.Marketplaces, ".copilot-clone-done"));
        File.WriteAllText(Path.Combine(finished, "repo", "marketplace.json"), "{}");
        var recent = Directory.CreateDirectory(Path.Combine(download.Marketplaces, ".copilot-clone-new")).FullName;

        download.Downloads.RemoveAbandoned(TimeSpan.FromMinutes(10));

        Assert.False(Directory.Exists(interrupted));
        Assert.False(Directory.Exists(pinned));
        Assert.True(Directory.Exists(busy));
        Assert.True(Directory.Exists(finished));
        Assert.True(Directory.Exists(recent));

        // With no git running at all, the lock still stays: the runtime may hold it before or after git.
        download.Exit();
        download.Downloads.RemoveAbandoned(TimeSpan.FromMinutes(10));
        Assert.False(Directory.Exists(busy));
        Assert.True(File.Exists(download.Lock));
        download.AssertOtherMarketplaceKept();

        string Abandoned(string staging)
        {
            // Begun and last written an hour ago.
            var pack = Path.Combine(Directory.CreateDirectory(Path.Combine(staging, "repo", ".git", "objects", "pack")).FullName, "pack-abc.pack");
            File.WriteAllBytes(pack, new byte[64]);
            File.SetLastWriteTimeUtc(pack, hourAgo);
            foreach (var folder in Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories).Append(staging))
            {
                Directory.SetCreationTimeUtc(folder, hourAgo);
                Directory.SetLastWriteTimeUtc(folder, hourAgo);
            }

            return staging;
        }
    }

    [SkippableFact]
    public async Task Stop_EndsARealGitCloneStartedByTheRuntime()
    {
        // Something that accepts git's connection and never answers, so the clone waits.
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var cache = Path.Combine(TestProfile.Root, "git-" + Guid.NewGuid().ToString("N"));
        var downloads = new MarketplaceDownloads(cache, () => Environment.ProcessId);
        var before = downloads.Snapshot();
        var source = $"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}/marketplace.git";
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-c", "credential.interactive=never", "clone", "--depth", "1", source,
                     Path.Combine(downloads.Directory, ".copilot-clone-test", "repo") })
            start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";

        Process git;
        try
        {
            git = Process.Start(start)!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new SkipException("git is not installed.");
        }

        using (git)
        {
            try
            {
                // The clone is found through the operating system's own process information.
                await WaitFor(() => downloads.Measure(before, source) is not null);
                Assert.Null(downloads.Measure(before, "contoso/other-marketplace"));
                Assert.False(downloads.Stop(before, "contoso/other-marketplace"));

                Assert.True(downloads.Stop(before, source));
                Assert.True(git.WaitForExit(10_000));
                await WaitFor(() => downloads.Measure(before, source) is null);
            }
            finally
            {
                if (!git.HasExited)
                    git.Kill(entireProcessTree: true);
            }
        }
    }

    internal static async Task WaitFor(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("The expected state was not reached.");
            await Task.Delay(5);
        }
    }
}

/// <summary>Records each report as it is made, unlike <see cref="Progress{T}"/>, which posts it later.</summary>
internal sealed class RecordedProgress<T> : IProgress<T>
{
    private readonly List<T> _reports = [];

    public void Report(T value)
    {
        lock (_reports)
            _reports.Add(value);
    }

    public bool Contains(T value)
    {
        lock (_reports)
            return _reports.Contains(value);
    }
}

/// <summary>
/// The runtime's marketplace cache with a large download as the runtime runs it: a lock file, a staging
/// folder git receives the repository into, and the git process cloning there. Stopping that process
/// makes the runtime's add fail, as it does for real.
/// </summary>
internal sealed class FakeMarketplaceDownload
{
    public const int RuntimeProcessId = 7001;
    public const int GitProcessId = 7002;
    private readonly List<GitProcess> _running = [];
    private readonly PluginCatalogRpc? _rpc;

    public FakeMarketplaceDownload(string root, PluginCatalogRpc? rpc)
    {
        _rpc = rpc;
        var cache = Path.Combine(root, "cache");
        Marketplaces = Path.Combine(cache, "marketplaces");
        Staging = Path.Combine(Marketplaces, ".copilot-clone-abc123");
        Packs = Path.Combine(Staging, "repo", ".git", "objects", "pack");
        Lock = Path.Combine(Marketplaces, "contoso-plugin-catalog.lock");
        Directory.CreateDirectory(Path.Combine(Marketplaces, "github-awesome-copilot"));
        File.WriteAllText(Path.Combine(Marketplaces, "github-awesome-copilot.lock"), "");
        Downloads = new MarketplaceDownloads(cache, () => RuntimeProcessId, _ => Running(), Stop);
    }

    public string Marketplaces { get; }
    public string Staging { get; }
    public string Packs { get; }
    public string Lock { get; }
    public MarketplaceDownloads Downloads { get; }
    public List<int> Stopped { get; } = [];

    public PluginCatalogService Service(PluginCatalogRpc rpc, TimeSpan? stallTimeout = null)
        => new(() => rpc.Client, static () => { }, downloads: Downloads)
        {
            ProgressInterval = TimeSpan.FromMilliseconds(10),
            StallTimeout = stallTimeout ?? TimeSpan.FromMinutes(5),
            StopTimeout = TimeSpan.FromSeconds(10),
        };

    /// <summary>What the runtime does as an add begins.</summary>
    public void Begin()
    {
        File.WriteAllText(Lock, "");
        Directory.CreateDirectory(Packs);
        Run(new GitProcess(
            GitProcessId,
            "\"git\" -c core.askPass= -c credential.interactive=never clone --depth 1 --progress " +
            $"https://github.com/contoso/plugin-catalog.git {Path.Combine(Staging, "repo")}",
            StartedByRuntime: true));
    }

    /// <summary>git has received this much of the repository.</summary>
    public void Receive(int bytes) => File.WriteAllBytes(Path.Combine(Packs, "tmp_pack_x1"), new byte[bytes]);

    /// <summary>git has all of it and writes out the files.</summary>
    public void Unpack() => File.Move(Path.Combine(Packs, "tmp_pack_x1"), Path.Combine(Packs, "pack-abc.pack"));

    public void Run(GitProcess process)
    {
        lock (_running)
            _running.Add(process);
    }

    public void Exit()
    {
        lock (_running)
            _running.Clear();
    }

    public void AssertOtherMarketplaceKept()
    {
        Assert.True(Directory.Exists(Path.Combine(Marketplaces, "github-awesome-copilot")));
        Assert.True(File.Exists(Path.Combine(Marketplaces, "github-awesome-copilot.lock")));
    }

    private IReadOnlyList<GitProcess> Running()
    {
        lock (_running)
            return [.. _running];
    }

    private void Stop(int processId)
    {
        Stopped.Add(processId);
        lock (_running)
            _running.RemoveAll(process => process.Id == processId);
        _rpc?.MarketplaceAdd.TrySetException(new InvalidOperationException("Failed to fetch GitHub marketplace: git clone was stopped."));
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
    public bool FailList { get; set; }
    public bool FailUpdate { get; set; }
    public string? UpdatedVersion { get; set; } = "0.3.1";
    public List<string> Calls { get; } = [];

    /// <summary>What the runtime does when asked to add a marketplace, before it answers.</summary>
    public Action? OnMarketplaceAdd { get; set; }

    /// <summary>The runtime's answer to adding a marketplace: its name, or the error it fails with.</summary>
    public TaskCompletionSource<string> MarketplaceAdd { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PluginCatalogRpc()
    {
        var requests = new Pipe();
        var responses = new Pipe();
        _server = new JsonRpc(new HeaderDelimitedMessageHandler(
            responses.Writer.AsStream(), requests.Reader.AsStream(), new JsonMessageFormatter()));
        _server.AddLocalRpcTarget(this);
        _server.StartListening();
        var rpcType = typeof(CopilotClient).Assembly.GetType("Microsoft.Copilot.JsonRpc", throwOnError: true)!;
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

    [JsonRpcMethod("plugins.marketplaces.add", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> AddMarketplace(object request)
    {
        Calls.Add("add");
        OnMarketplaceAdd?.Invoke();
        return new { name = await MarketplaceAdd.Task };
    }

    [JsonRpcMethod("plugins.marketplaces.browse", UseSingleObjectParameterDeserialization = true)]
    public object Browse(object request)
    {
        return new { plugins = new[] { new { name = "private-plugin", description = "Private plugin" } } };
    }

    [JsonRpcMethod("plugins.marketplaces.refresh", UseSingleObjectParameterDeserialization = true)]
    public object Refresh(object request)
    {
        Calls.Add("refresh");
        return new { results = new[] { new { name = "private-plugin", success = RefreshSucceeds, error = RefreshSucceeds ? null : "Access denied" } } };
    }

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
        if (FailList)
            throw new InvalidOperationException("The installed plugin list is unavailable.");
        return new { plugins = new[] { new { name = "private-plugin", marketplace = "private-plugin", version = "0.3.0", enabled = true } } };
    }

    public void Dispose()
    {
        _sdkRpc.Dispose();
        _server.Dispose();
    }
}
