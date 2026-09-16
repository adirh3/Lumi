using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Lumi.Models;
using Lumi.Services;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

#if WINDOWS
[Collection("Headless UI")]
public sealed class BrowserTabOwnershipTests
{
    [Fact]
    public async Task NewChatBrowserHasOneStableDistinctBlankTabWithoutCreatingAController()
    {
        await using var first = new BrowserService();
        await using var second = new BrowserService();
        var tab = Assert.Single(first.Tabs);

        Assert.Equal(tab.Id, Assert.Single(first.Tabs).Id);
        Assert.Equal(tab.Id, first.ActiveTabId);
        Assert.NotEqual(tab.Id, second.ActiveTabId);
        Assert.Equal("about:blank", tab.Url);
        Assert.True(tab.IsActive);
        Assert.False(first.IsInitialized);
        Assert.False(first.HasController);
    }

    [Fact]
    public async Task DisposalRemovesTabsAndRejectsLateCaptures()
    {
        var browser = new BrowserService();
        var tabId = browser.ActiveTabId;
        await browser.DisposeAsync();
        await browser.DisposeAsync();

        Assert.Empty(browser.Tabs);
        Assert.False(browser.HasController);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => browser.CaptureScreenshotAsync(tabId));
    }

    [Fact]
    public async Task ScreenshotOfUnknownTabFailsWithoutInitializingOrSwitching()
    {
        await using var browser = new BrowserService();
        var tabId = browser.ActiveTabId;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => browser.CaptureScreenshotAsync("missing-tab"));

        Assert.Contains("missing-tab", error.Message);
        Assert.Equal(tabId, browser.ActiveTabId);
        Assert.False(browser.IsInitialized);
    }

    [Fact]
    public async Task ScreenshotOfUninitializedTabFailsWithoutCreatingAController()
    {
        await using var browser = new BrowserService();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => browser.CaptureScreenshotAsync());
        Assert.Contains("not initialized", error.Message);
        Assert.False(browser.HasController);
    }

    [Fact]
    public async Task TabStripUsesStableIdsAndClearsOnChatDetach()
    {
        using var session = HeadlessTestSession.Start();
        var renderedCount = 0;
        var clearedCount = -1;
        string? selectionName = null;
        string? closeName = null;
        string? tabId = null;
        await session.Dispatch(async () =>
        {
            await using var browser = new BrowserService();
            tabId = browser.ActiveTabId;
            var view = new BrowserView();
            view.SetBrowserService(browser, new DataStore(new AppData()));
            var strip = view.FindControl<StackPanel>("BrowserTabs")!;
            renderedCount = strip.Children.Count;
            var row = (StackPanel)strip.Children[0];
            selectionName = row.Children[0].Name;
            closeName = row.Children[1].Name;
            view.ClearBrowserService();
            clearedCount = strip.Children.Count;
        }, CancellationToken.None);

        Assert.Equal(1, renderedCount);
        Assert.Equal("BrowserTab_" + tabId, selectionName);
        Assert.Equal("BrowserTabClose_" + tabId, closeName);
        Assert.Equal(0, clearedCount);
    }
}
#endif
