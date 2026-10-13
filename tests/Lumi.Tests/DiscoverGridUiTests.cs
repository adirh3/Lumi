using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Discover;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class DiscoverGridUiTests
{
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
