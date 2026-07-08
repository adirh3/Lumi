using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class BrowserToggleAfterChatSwitchTests
{
    // Regression test for "the open browser button doesn't work after switching chats".
    // The per-chat browser session used to be disposed (and its runtime state swept) every time
    // the user switched away, so switching back left the toggle hidden with no session to show.
    [Fact]
    public async Task BrowserToggleAndSessionSurviveSwitchingChats()
    {
        using var session = HeadlessTestSession.Start();

        // IMPORTANT: capture observations inside the dispatched body and assert OUTSIDE it. Avalonia's
        // HeadlessUnitTestSession.Dispatch(Func<Task>) swallows exceptions thrown inside an async body,
        // so an Assert.* inside the lambda would NOT fail the test. See HeadlessTestSession.Dispatch.
        bool toggleShownForA = false;
        bool hiddenForB = false;
        bool sessionSurvivedForA = false;
        bool toggleShownAfterReturn = false;
        bool sessionIntactAfterReturn = false;
        bool autoShownAfterReturn = false;
        bool toggleDrivesSessionForA = false;
        bool sessionIntactAfterToggle = false;

        await session.Dispatch(async () =>
        {
            var dataStore = CreateDataStore();
            var chatA = new Chat { Title = "A" };
            var chatB = new Chat { Title = "B" };
            chatA.Messages.Add(new ChatMessage { Role = "user", Content = "a" });
            chatB.Messages.Add(new ChatMessage { Role = "user", Content = "b" });
            dataStore.Data.Chats.Add(chatA);
            dataStore.Data.Chats.Add(chatB);

            var vm = new ChatViewModel(dataStore, new CopilotService());
            var shows = new List<Guid>();
            vm.BrowserShowRequested += shows.Add;

            await vm.LoadChatAsync(chatA);

            // Simulate the browser tool having created a live per-chat browser session in chat A
            // and opened its panel (IsBrowserOpen is what the controller sets when the panel shows).
            var services = GetField<ConcurrentDictionary<Guid, BrowserService>>(vm, "_chatBrowserServices");
            services[chatA.Id] = new BrowserService();
            vm.HasUsedBrowser = true;
            vm.IsBrowserOpen = true;
            toggleShownForA = vm.ShowBrowserToggle;

            // Switch away to B: the toggle hides for B, but A's browser session must survive.
            await vm.LoadChatAsync(chatB);
            hiddenForB = !vm.HasUsedBrowser;
            sessionSurvivedForA = services.ContainsKey(chatA.Id);

            // Switch back to A: the toggle reappears, the session is intact, and the panel
            // auto-restores via BrowserShowRequested.
            shows.Clear();
            await vm.LoadChatAsync(chatA);
            toggleShownAfterReturn = vm.ShowBrowserToggle;
            sessionIntactAfterReturn = vm.GetBrowserServiceForChat(chatA.Id) is not null;
            autoShownAfterReturn = shows.Contains(chatA.Id);

            // The panel auto-restored open on return; simulate the controller closing it (as a hide
            // would), then clicking the toggle must re-drive the live session for the current chat.
            vm.IsBrowserOpen = false;
            shows.Clear();
            vm.ToggleBrowser();
            toggleDrivesSessionForA = shows.Contains(chatA.Id);
            sessionIntactAfterToggle = vm.GetBrowserServiceForChat(chatA.Id) is not null;

            vm.Dispose();
        }, CancellationToken.None);

        Assert.True(toggleShownForA, "Browser toggle should be shown for chat A after a browser session is created.");
        Assert.True(hiddenForB, "Browser toggle should hide when switching to chat B.");
        Assert.True(sessionSurvivedForA, "Chat A's browser session must survive switching away.");
        Assert.True(toggleShownAfterReturn, "Browser toggle should reappear when returning to chat A.");
        Assert.True(sessionIntactAfterReturn, "Chat A's browser session must still be available after returning.");
        Assert.True(autoShownAfterReturn, "Returning to chat A should auto-restore the browser panel.");
        Assert.True(toggleDrivesSessionForA, "Clicking the toggle should drive chat A's live browser session.");
        Assert.True(sessionIntactAfterToggle, "Chat A's browser session must remain after toggling.");
    }

    // Regression test for "the open browser button doesn't work after switching chats and returning".
    // Chats are backed by pooled per-chat ChatViewModel *surfaces* (ChatSessionStore). Returning to a
    // chat whose surface is still cached REUSES that surface and SKIPS LoadChatAsync — which is the only
    // place that re-raises the browser auto-show. Without a dedicated re-sync, the panel never reopens and
    // the surface's IsBrowserOpen stays stuck 'true', so the toggle command tries to hide an already-hidden
    // panel and the button appears dead. This drives the real MainViewModel surface reuse plus a faithful
    // test-double of the browser panel view (the controller + the MainWindow active-chat hide).
    [Fact]
    public async Task BrowserToggleSurvivesReturnToCachedChatSurface()
    {
        using var session = HeadlessTestSession.Start();

        // IMPORTANT: capture observations inside the dispatched body and assert OUTSIDE it. Avalonia's
        // HeadlessUnitTestSession.Dispatch(Func<Task>) swallows exceptions thrown inside an async body,
        // so an Assert.* inside the lambda would NOT fail the test. See HeadlessTestSession.Dispatch.
        bool visibleAfterOpen = false;
        bool openFlagAfterOpen = false;
        bool hiddenAfterSwitchAway = false;
        bool sameSurfaceOnReturn = false;
        bool showRequestedOnReturn = false;
        bool toggleAvailableOnReturn = false;
        bool visibleAfterReturn = false;
        bool hiddenAfterToggle = false;
        bool visibleAfterSecondToggle = false;

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var chatA = new Chat { Title = "A" };
            var chatB = new Chat { Title = "B" };
            chatA.Messages.Add(new ChatMessage { Role = "user", Content = "a" });
            chatB.Messages.Add(new ChatMessage { Role = "user", Content = "b" });
            var vm = new MainViewModel(CreateDataStore(chatA, chatB), new CopilotService(), new UpdateService());

            // Open chat A and simulate the browser tool creating a live per-chat session and opening the panel.
            await vm.OpenChatByIdAsync(chatA.Id);
            var surfaceA = vm.ChatVM;
            var services = GetField<ConcurrentDictionary<Guid, BrowserService>>(surfaceA, "_chatBrowserServices");
            services[chatA.Id] = new BrowserService();
            surfaceA.HasUsedBrowser = true;

            using var panel = new BrowserPanelProbe(vm);
            surfaceA.RequestShowBrowser();
            visibleAfterOpen = panel.IsVisible;
            openFlagAfterOpen = surfaceA.IsBrowserOpen;

            // Switch away to B: the shared panel hides. A's surface is cached (not disposed).
            await vm.OpenChatByIdAsync(chatB.Id);
            hiddenAfterSwitchAway = !panel.IsVisible;

            // Return to A: the cached surface is reused (LoadChatAsync is skipped), so the fix must
            // re-establish the panel from ShowChatSurface.
            var showsOnReturn = new List<Guid>();
            surfaceA.BrowserShowRequested += showsOnReturn.Add;
            await vm.OpenChatByIdAsync(chatA.Id);
            sameSurfaceOnReturn = ReferenceEquals(surfaceA, vm.ChatVM); // proves the cached-surface reuse path
            showRequestedOnReturn = showsOnReturn.Contains(chatA.Id);   // the restored auto-show request
            toggleAvailableOnReturn = surfaceA.HasUsedBrowser;
            visibleAfterReturn = panel.IsVisible;                       // panel actually reopened (fails before the fix)

            // The toggle button is alive again: it hides, then shows.
            surfaceA.ToggleBrowser();
            hiddenAfterToggle = !panel.IsVisible;
            surfaceA.ToggleBrowser();
            visibleAfterSecondToggle = panel.IsVisible;

            vm.Dispose();
        }, CancellationToken.None);

        Assert.True(visibleAfterOpen, "Browser panel should be visible after opening it in chat A.");
        Assert.True(openFlagAfterOpen, "IsBrowserOpen should be true after opening the browser in chat A.");
        Assert.True(hiddenAfterSwitchAway, "Browser panel should hide when switching away to chat B.");
        Assert.True(sameSurfaceOnReturn, "Returning to chat A should reuse the cached surface (skipping LoadChatAsync).");
        Assert.True(showRequestedOnReturn, "Returning to cached chat A should re-request the browser panel to show.");
        Assert.True(toggleAvailableOnReturn, "The browser toggle should remain available after returning to chat A.");
        Assert.True(visibleAfterReturn, "Browser panel should reopen after returning to cached chat A.");
        Assert.True(hiddenAfterToggle, "Toggle should hide the browser after returning (button still works).");
        Assert.True(visibleAfterSecondToggle, "Toggle should re-show the browser after returning (button still works).");
    }

    // Regression test for "returning to a chat auto-opens the browser even after it was closed".
    // A live per-chat BrowserService outlives a closed panel, so the restore path must gate the
    // auto-show on IsBrowserOpen (was the browser open when the user left) rather than merely "the
    // chat has a browser service". Returning to a chat whose browser was closed must leave it closed,
    // while keeping the toggle button alive so the user can reopen it on demand.
    [Fact]
    public async Task BrowserStaysClosedWhenReturningAfterClosingIt()
    {
        using var session = HeadlessTestSession.Start();

        // IMPORTANT: capture observations inside the dispatched body and assert OUTSIDE it (see the
        // note on HeadlessTestSession.Dispatch swallowing exceptions thrown inside an async body).
        bool visibleAfterOpen = false;
        bool hiddenAfterClose = false;
        bool sameSurfaceOnReturn = false;
        bool showRequestedOnReturn = false;
        bool toggleAvailableOnReturn = false;
        bool visibleAfterReturn = false;
        bool visibleAfterToggleOpen = false;

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var chatA = new Chat { Title = "A" };
            var chatB = new Chat { Title = "B" };
            chatA.Messages.Add(new ChatMessage { Role = "user", Content = "a" });
            chatB.Messages.Add(new ChatMessage { Role = "user", Content = "b" });
            var vm = new MainViewModel(CreateDataStore(chatA, chatB), new CopilotService(), new UpdateService());

            await vm.OpenChatByIdAsync(chatA.Id);
            var surfaceA = vm.ChatVM;
            var services = GetField<ConcurrentDictionary<Guid, BrowserService>>(surfaceA, "_chatBrowserServices");
            services[chatA.Id] = new BrowserService();
            surfaceA.HasUsedBrowser = true;

            using var panel = new BrowserPanelProbe(vm);

            // Open the browser in chat A, then explicitly close it (the browser task is "finished").
            surfaceA.RequestShowBrowser();
            visibleAfterOpen = panel.IsVisible;
            surfaceA.ToggleBrowser();
            hiddenAfterClose = !panel.IsVisible;

            // Switch away to B, then return to A: the cached surface is reused (LoadChatAsync skipped).
            await vm.OpenChatByIdAsync(chatB.Id);
            var showsOnReturn = new List<Guid>();
            surfaceA.BrowserShowRequested += showsOnReturn.Add;
            await vm.OpenChatByIdAsync(chatA.Id);

            sameSurfaceOnReturn = ReferenceEquals(surfaceA, vm.ChatVM); // proves the cached-surface reuse path
            showRequestedOnReturn = showsOnReturn.Contains(chatA.Id);   // must NOT auto-show a closed browser
            toggleAvailableOnReturn = surfaceA.HasUsedBrowser;
            visibleAfterReturn = panel.IsVisible;

            // The toggle button is still alive: the user can reopen the browser on demand.
            surfaceA.ToggleBrowser();
            visibleAfterToggleOpen = panel.IsVisible;

            vm.Dispose();
        }, CancellationToken.None);

        Assert.True(visibleAfterOpen, "Browser panel should be visible after opening it in chat A.");
        Assert.True(hiddenAfterClose, "Browser panel should hide after the user closes it.");
        Assert.True(sameSurfaceOnReturn, "Returning to chat A should reuse the cached surface (skipping LoadChatAsync).");
        Assert.False(showRequestedOnReturn, "Returning to a chat whose browser was closed must NOT auto-show it.");
        Assert.False(visibleAfterReturn, "Browser panel must stay closed when returning after it was closed.");
        Assert.True(toggleAvailableOnReturn, "The browser toggle should remain available so the user can reopen it.");
        Assert.True(visibleAfterToggleOpen, "Clicking the toggle should reopen the browser after returning.");
    }

    // Faithful test-double of the browser panel view: the single shared BrowserIsland panel follows
    // whichever ChatViewModel surface is active (ChatWorkspaceView re-subscribes its controller on swap),
    // MainWindow hides it on every ActiveChatId change, and the controller's HideBrowserPanel early-returns
    // (leaving IsBrowserOpen untouched) when the panel is already hidden.
    private sealed class BrowserPanelProbe : IDisposable
    {
        private readonly MainViewModel _vm;
        private ChatViewModel? _surface;

        public BrowserPanelProbe(MainViewModel vm)
        {
            _vm = vm;
            _vm.PropertyChanged += OnMainPropertyChanged;
            Attach(vm.ChatVM);
        }

        public bool IsVisible { get; private set; }

        private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(MainViewModel.ChatVM))
                Attach(_vm.ChatVM);
            else if (args.PropertyName == nameof(MainViewModel.ActiveChatId))
                Hide(); // MainWindow hides the browser panel whenever the active chat changes
        }

        private void Attach(ChatViewModel? surface)
        {
            if (ReferenceEquals(_surface, surface))
                return;

            if (_surface is not null)
            {
                _surface.BrowserShowRequested -= OnShow;
                _surface.BrowserHideRequested -= OnHide;
            }

            _surface = surface;

            if (_surface is not null)
            {
                _surface.BrowserShowRequested += OnShow;
                _surface.BrowserHideRequested += OnHide;
            }
        }

        private void OnShow(Guid chatId)
        {
            if (_vm.ActiveChatId != chatId) // ChatPreviewPanelController._canShowBrowserPanel gate
                return;

            IsVisible = true;
            if (_surface is not null)
                _surface.IsBrowserOpen = true;
        }

        private void OnHide() => Hide();

        private void Hide()
        {
            if (!IsVisible) // HideBrowserPanelAsync early-return: leaves IsBrowserOpen as-is
                return;

            IsVisible = false;
            if (_surface is not null)
                _surface.IsBrowserOpen = false;
        }

        public void Dispose()
        {
            _vm.PropertyChanged -= OnMainPropertyChanged;
            Attach(null);
        }
    }

    private static DataStore CreateDataStore(params Chat[] chats)
        => new(new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            },
            Chats = [.. chats]
        });

    private static T GetField<T>(object instance, string name) where T : class
        => (T)(instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance)
            ?? throw new InvalidOperationException($"Field {name} was not found."));
}
