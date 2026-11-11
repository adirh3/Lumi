using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Discover;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class DiscoverGridUiTests
{
    [Theory]
    [InlineData(null, true, "Update from source")]
    [InlineData("0.3.1", true, "Update to 0.3.1")]
    [InlineData("0.3.0", false, "Update to 0.3.0")]
    public async Task PluginDetail_DistinguishesUnknownFromKnownUpdates(string? candidate, bool canUpdate, string label)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            using var fixture = new PluginCatalogFixture();
            using var rpc = new PluginCatalogRpc();
            var vm = PluginDetail(fixture, rpc, candidate);
            Assert.Equal(canUpdate, vm.CanUpdate);
            Assert.Equal(label, vm.UpdateButtonText);
            Assert.Equal(candidate is null, vm.IsUpdateUnknown);
            Assert.Equal(candidate == "0.3.1", vm.OpenCard!.HasUpdate);

            vm.OpenCard.Installation = vm.OpenCard.Installation! with { IsManaged = true };
            Assert.False(vm.CanUpdate);
            vm.OpenCard.Installation = null;
            Assert.False(vm.CanUpdate);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PluginDetail_RefreshKeepsTheOpenOperationAndClearsMissingVersionHints()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var fixture = new PluginCatalogFixture();
            using var rpc = new PluginCatalogRpc();
            var vm = PluginDetail(fixture, rpc, "0.3.1");
            var open = vm.OpenCard!;
            var installed = open.Installation!;
            open.IsBusy = true;

            vm.ApplyPlugins([], [installed]);
            Assert.Same(open, vm.OpenCard);
            Assert.True(vm.IsDetailBusy);
            Assert.True(vm.IsUpdateUnknown);
            Assert.False(vm.CanUpdate);

            vm.ApplyPlugins([open.Listing! with { Version = "0.3.2" }], [installed]);
            Assert.Same(open, vm.OpenCard);
            Assert.True(vm.IsDetailBusy);
            Assert.False(vm.CanUpdate);
            open.IsBusy = false;
            Assert.True(vm.CanUpdate);
            Assert.Equal("0.3.2", open.Listing!.Version);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PluginDetail_FailedUpdatePreservesVersion_AndMissingVersionNeverClaimsCurrent()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            using var fixture = new PluginCatalogFixture();
            using var rpc = new PluginCatalogRpc { FailUpdate = true };
            var vm = PluginDetail(fixture, rpc, "0.3.1");
            await vm.UpdateCommand.ExecuteAsync(null);
            Assert.Contains("os error 5", vm.ActionError);
            Assert.Equal("0.3.0", vm.OpenCard!.Installation!.Version);
            Assert.False(vm.HasResult);
            Assert.False(vm.IsDetailBusy);

            rpc.FailUpdate = false;
            rpc.UpdatedVersion = null;
            await vm.UpdateCommand.ExecuteAsync(null);
            Assert.Null(vm.OpenCard.Installation.Version);
            Assert.False(vm.OpenCard.HasVersion);
            Assert.Equal(Loc.Discover_UpdatedVersionUnknown, vm.ResultMessage);
            Assert.True(vm.IsUpdateUnknown);
            Assert.False(vm.HasActionError);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PluginRefreshFailure_IsVisibleInEveryGalleryFilter()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            using var fixture = new PluginCatalogFixture();
            using var rpc = new PluginCatalogRpc { RefreshSucceeds = false };
            fixture.WriteCatalog("0.3.1");
            var vm = PluginGallery(fixture, rpc);
            await vm.EnsureLoadedAsync();
            Assert.True(Assert.Single(vm.PluginPreview).HasUpdate);
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.True(vm.HasPluginError);
            Assert.True(Assert.Single(vm.PluginPreview).IsUpdateUnknown);

            var view = new DiscoverView { DataContext = vm };
            var window = new Window { Content = view, Width = 1100, Height = 900 };
            window.Show();
            try
            {
                Action[] filters =
                [
                    () => vm.ShowAllCommand.Execute(null),
                    () => vm.ShowPluginsCommand.Execute(null),
                    () => vm.ShowInstalledCommand.Execute(null),
                    () => vm.ShowServersCommand.Execute(null),
                    () => Assert.Single(vm.SourceRows, row => row.Kind == DiscoverKind.Plugin).SelectCommand.Execute(null),
                ];
                foreach (var filter in filters)
                {
                    filter();
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    var error = Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(),
                        text => text.Text == vm.PluginError);
                    Assert.True(error.IsEffectivelyVisible, $"Refresh error hidden in {vm.Filter} ({vm.SourceKey}).");
                    Assert.True(error.Bounds.Width > 0);
                    Assert.True(error.Bounds.Height > 0);
                }
            }
            finally
            {
                view.DataContext = null;
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PluginRefreshFailure_RetryRepeatsRefreshUntilRecovery_ThenOnlyReloadsLoadFailures()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            using var fixture = new PluginCatalogFixture();
            using var rpc = new PluginCatalogRpc { RefreshSucceeds = false };
            fixture.WriteCatalog("0.3.1");
            var vm = PluginGallery(fixture, rpc);
            await vm.EnsureLoadedAsync();
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(1, rpc.Calls.Count(call => call == "refresh"));
            var view = new DiscoverView { DataContext = vm };
            var window = new Window { Content = view, Width = 1100, Height = 900 };
            window.Show();
            try
            {
                async Task RetryPluginError()
                {
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    var button = Assert.Single(view.GetVisualDescendants().OfType<Button>(),
                        button => button.IsEffectivelyVisible && button.Content?.ToString() == Loc.Discover_Retry);
                    await Assert.IsAssignableFrom<IAsyncRelayCommand>(button.Command).ExecuteAsync(null);
                }

                await RetryPluginError();
                Assert.Equal(2, rpc.Calls.Count(call => call == "refresh"));
                Assert.True(vm.HasPluginError);
                Assert.Null(Assert.Single(vm.PluginPreview).Listing!.Version);

                rpc.RefreshSucceeds = true;
                await RetryPluginError();
                Assert.Equal(3, rpc.Calls.Count(call => call == "refresh"));
                Assert.False(vm.HasPluginError);
                var recovered = Assert.Single(vm.PluginPreview);
                Assert.Equal("0.3.1", recovered.Listing!.Version);
                Assert.True(recovered.HasUpdate);

                rpc.FailList = true;
                await vm.RetryCommand.ExecuteAsync(null);
                Assert.True(vm.HasPluginError);
                rpc.FailList = false;
                await RetryPluginError();
                Assert.False(vm.HasPluginError);
                Assert.Equal(3, rpc.Calls.Count(call => call == "refresh"));
            }
            finally
            {
                view.DataContext = null;
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PluginRefreshFailure_ServerRetryDoesNotClearItOrRefreshMarketplaces()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            using var fixture = new PluginCatalogFixture();
            using var rpc = new PluginCatalogRpc { RefreshSucceeds = false };
            fixture.WriteCatalog("0.3.1");
            var registryAvailable = false;
            var vm = PluginGallery(fixture, rpc, _ => registryAvailable ? Page(0, 0, null) : null);
            await vm.EnsureLoadedAsync();
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.True(vm.HasServerError);
            Assert.True(vm.HasPluginError);
            var refreshError = vm.PluginError;

            registryAvailable = true;
            await vm.RetryCommand.ExecuteAsync(null);
            Assert.False(vm.HasServerError);
            Assert.Equal(refreshError, vm.PluginError);
            Assert.Equal(1, rpc.Calls.Count(call => call == "refresh"));
            Assert.Null(Assert.Single(vm.PluginPreview).Listing!.Version);
        }, CancellationToken.None);
    }

    private static DiscoverViewModel PluginGallery(
        PluginCatalogFixture fixture, PluginCatalogRpc rpc, Func<string, string?>? registry = null)
        => new(
            new DataStore(
                new AppData { Settings = new UserSettings { McpRegistryUrls = ["https://registry.test"] } },
                Path.Combine(fixture.Root, "skills")),
            fixture.Service(rpc),
            new McpRegistryClient(new HttpClient(new StubRegistry(registry ?? (_ => Page(0, 0, null))))),
            static () => null);

    private static DiscoverViewModel PluginDetail(PluginCatalogFixture fixture, PluginCatalogRpc rpc, string? candidate)
    {
        var vm = PluginGallery(fixture, rpc);
        var card = DiscoverCard.ForPlugin(Plugin("private-plugin", "Fixture") with { Marketplace = "private-plugin", Version = candidate }, _ => { });
        card.Installation = new("private-plugin", "private-plugin", "0.3.0", true, null, false);
        vm.OpenCard = card;
        return vm;
    }

    [Fact]
    public async Task Grid_ShowsAPageAtATime_BuildsItAfterTheClick_AndFetchesMoreAtTheEnd()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var requests = new List<string>();
            var vm = new DiscoverViewModel(
                new DataStore(
                    new AppData { Settings = new UserSettings { McpRegistryUrls = ["https://registry.test"] } },
                    Path.Combine(Path.GetTempPath(), $"lumi-discover-grid-{Guid.NewGuid():N}")),
                new PluginCatalogService(static () => null, static () => { }),
                new McpRegistryClient(new HttpClient(new StubRegistry(url =>
                {
                    requests.Add(url);
                    return url.Contains("cursor=next", StringComparison.Ordinal) ? Page(60, 10, cursor: null) : Page(0, 60, cursor: "next");
                }))),
                static () => null);

            await vm.EnsureLoadedAsync();
            vm.ShowServersCommand.Execute(null);

            // The click builds no cards itself: placeholders show until the first ones land.
            Assert.True(vm.IsPreparingGrid);
            Assert.True(vm.IsGridLoading);
            Assert.Empty(vm.Cards);

            await WaitFor(() => vm.Cards.Count == 24);
            Assert.False(vm.IsPreparingGrid);
            Assert.True(vm.HasMoreCards);
            Assert.False(vm.CanLoadMoreServers);

            vm.ShowMoreCards();
            await WaitFor(() => vm.Cards.Count == 48);
            vm.ShowMoreCards();
            await WaitFor(() => vm.Cards.Count == 60);
            Assert.False(vm.HasMoreCards);
            Assert.True(vm.CanLoadMoreServers);
            Assert.Single(requests);

            // Past everything loaded, the registry's next page is fetched and shown as it arrives.
            vm.ShowMoreCards();
            await WaitFor(() => vm.Cards.Count == 70);
            Assert.Equal(2, requests.Count);
            Assert.Contains("cursor=next", requests[1]);

            // Another filter starts again at its first page.
            vm.ShowAllCommand.Execute(null);
            Assert.Empty(vm.Cards);
            vm.ShowServersCommand.Execute(null);
            await WaitFor(() => vm.Cards.Count == 24);
            Assert.True(vm.HasMoreCards);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Grid_FetchesOnlyFromTheSourceInView_AndScrollingNeverRetriesAFailedPage()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var requests = new List<string>();
            var vm = new DiscoverViewModel(
                new DataStore(
                    new AppData { Settings = new UserSettings { McpRegistryUrls = ["https://a.test", "https://b.test"] } },
                    Path.Combine(Path.GetTempPath(), $"lumi-discover-grid-{Guid.NewGuid():N}")),
                new PluginCatalogService(static () => null, static () => { }),
                new McpRegistryClient(new HttpClient(new StubRegistry(url =>
                {
                    requests.Add(url);
                    if (url.StartsWith("https://a.test", StringComparison.Ordinal))
                        return Page(0, 6, cursor: null, owner: "alpha");
                    return url.Contains("cursor=next", StringComparison.Ordinal) ? null : Page(0, 6, cursor: "next", owner: "beta");
                }))),
                static () => null);
            await vm.EnsureLoadedAsync();
            Assert.Equal(2, requests.Count);

            // Registry A has no more pages; B's cursor cannot add anything to A's list.
            vm.SourceRows.Single(row => row.Key == "reg:https://a.test").SelectCommand.Execute(null);
            await WaitFor(() => vm.Cards.Count == 6);
            Assert.False(vm.CanLoadMoreServers);
            vm.ShowMoreCards();
            Assert.Equal(2, requests.Count);

            // B's next page fails: the error shows, and reaching the end again does not retry it.
            vm.SourceRows.Single(row => row.Key == "reg:https://b.test").SelectCommand.Execute(null);
            await WaitFor(() => vm.Cards.Count == 6);
            Assert.True(vm.CanLoadMoreServers);
            vm.ShowMoreCards();
            await WaitFor(() => vm.HasServerError && !vm.IsLoadingMoreServers);
            Assert.Equal(3, requests.Count);
            vm.ShowMoreCards();
            await Task.Delay(100);
            Assert.Equal(3, requests.Count);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Search_RanksPluginsInTheBackground_AndKeepsTheListUntilTheRankingLands()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var vm = new DiscoverViewModel(
                new DataStore(
                    new AppData { Settings = new UserSettings { McpRegistryUrls = ["https://registry.test"] } },
                    Path.Combine(Path.GetTempPath(), $"lumi-discover-grid-{Guid.NewGuid():N}")),
                new PluginCatalogService(static () => null, static () => { }),
                new McpRegistryClient(new HttpClient(new StubRegistry(_ => Page(0, 0, cursor: null)))),
                static () => null);
            await vm.EnsureLoadedAsync();
            var listings = Enumerable.Range(0, 200)
                .Select(i => Plugin($"helper-{i:000}", "Keeps things tidy"))
                .Append(Plugin("weather-forecast", "Forecasts for any city"))
                .ToList();
            vm.ApplyPlugins(listings, []);
            vm.ShowAllCommand.Execute(null);
            var shown = vm.PluginPreview.ToList();
            Assert.Equal(DiscoverViewModel.PreviewCount, shown.Count);

            // Typing never ranks on the UI thread: the list keeps what it shows until the ranking lands.
            vm.SearchQuery = "weather";
            Assert.Equal(shown, vm.PluginPreview);
            await WaitFor(() => vm.PluginPreview.Count == 1);
            Assert.Equal("weather-forecast", vm.PluginPreview[0].Listing!.Name);

            // Coming back to a query already ranked shows its plugins at once.
            vm.SearchQuery = "weath";
            vm.SearchQuery = "weather";
            Assert.Equal("weather-forecast", Assert.Single(vm.PluginPreview).Listing!.Name);

            // Plugins that load later are ranked for the query too.
            vm.ApplyPlugins(listings.Append(Plugin("weather-radar", "Rain maps")), []);
            vm.ShowAllCommand.Execute(null);
            await WaitFor(() => vm.PluginPreview.Count == 2);

            // Clearing the search shows everything again at once.
            vm.SearchQuery = "";
            Assert.Equal(shown, vm.PluginPreview);
        }, CancellationToken.None);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("The grid did not reach the expected state.");
            await Task.Delay(5);
        }
    }

    private static PluginListing Plugin(string name, string description)
        => new(name, "market", description, "1.0.0", null, null, null, null, null, [], null);

    private static string Page(int first, int count, string? cursor, string owner = "acme")
    {
        var servers = Enumerable.Range(first, count).Select(i =>
            $$"""{ "server": { "name": "{{owner}}/server-{{i:000}}", "title": "Server {{i}}", "version": "1.0.0", "remotes": [ { "type": "streamable-http", "url": "https://{{owner}}.example/{{i}}" } ] } }""");
        var next = cursor is null ? "" : $$""", "nextCursor": "{{cursor}}" """;
        return $$"""{ "servers": [ {{string.Join(",", servers)}} ], "metadata": { "count": {{count}}{{next}} } }""";
    }

    /// <summary>Answers with the given body, or fails with 500 when there is none.</summary>
    private sealed class StubRegistry(Func<string, string?> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request.RequestUri!.ToString()) is { } body
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }
}
