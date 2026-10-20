using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class MainWindowTrayBehaviorTests
{
    [Fact]
    public async Task ClosingPrimaryWindowWithoutTray_ClosesDetachedChatWindows()
    {
        using var session = HeadlessTestSession.Start();
        var detachedChatClosed = false;
        var detachedChatClosedByPrimaryWindow = false;

        await session.Dispatch(async () =>
        {
            var testWindow = new TestMainWindow();
            var (window, viewModel) = CreateWindow(minimizeToTray: false, testWindow);
            var detachedChatWindow = new ChatWindow();
            detachedChatWindow.Closed += (_, _) => detachedChatClosed = true;
            testWindow.DesktopWindows = [window, detachedChatWindow];
            window.Show();
            detachedChatWindow.Show();

            try
            {
                await PumpAsync();
                window.Close();
                await PumpAsync();
                detachedChatClosedByPrimaryWindow = detachedChatClosed;
            }
            finally
            {
                if (detachedChatWindow.IsVisible)
                    detachedChatWindow.Close();
                if (window.IsVisible)
                    window.Close();
                viewModel.Dispose();
            }
        }, CancellationToken.None);

        Assert.True(detachedChatClosedByPrimaryWindow);
    }

    [Fact]
    public async Task ClosingPrimaryWindowToTray_KeepsDetachedChatWindowsOpen()
    {
        using var session = HeadlessTestSession.Start();
        var detachedChatStayedOpen = false;

        await session.Dispatch(async () =>
        {
            var (window, viewModel) = CreateWindow(minimizeToTray: true);
            var detachedChatWindow = new ChatWindow();
            window.Show();
            detachedChatWindow.Show();

            try
            {
                await PumpAsync();
                window.Close();
                await PumpAsync();
                detachedChatStayedOpen = detachedChatWindow.IsVisible;
            }
            finally
            {
                detachedChatWindow.Close();
                CloseWindow(window, viewModel);
            }
        }, CancellationToken.None);

        Assert.True(detachedChatStayedOpen);
    }

    [Theory]
    [InlineData(WindowCloseReason.ApplicationShutdown)]
    [InlineData(WindowCloseReason.OSShutdown)]
    public async Task ShutdownIsNotCancelledByMinimizeToTray(WindowCloseReason reason)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var testWindow = new TestMainWindow();
            var (window, viewModel) = CreateWindow(minimizeToTray: true, testWindow);
            window.Show();
            try
            {
                await PumpAsync();
                var closing = testWindow.RaiseClosing(reason);

                Assert.False(closing.Cancel);
                Assert.True(viewModel.SettingsVM.MinimizeToTray);
            }
            finally
            {
                CloseWindow(window, viewModel);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TraySetting_DoesNotHideWindowWhenMinimized()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var (window, viewModel) = CreateWindow(minimizeToTray: true);
            window.Show();
            try
            {
                await PumpAsync();

                window.WindowState = WindowState.Minimized;
                await PumpAsync();

                Assert.True(window.IsVisible);
                Assert.True(window.ShowInTaskbar);
            }
            finally
            {
                CloseWindow(window, viewModel);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReopenAfterClosingToTray_RestoresTheSameWindowAndDraftRepeatedly()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var (window, viewModel) = CreateWindow(minimizeToTray: true);
            var app = CreateAppWithWindow(window);
            viewModel.ChatVM.PromptText = "Keep this unsent draft";
            window.Show();
            try
            {
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    window.Close();
                    await PumpAsync();
                    Assert.False(window.IsVisible);
                    Assert.False(window.ShowInTaskbar);

                    Activate(app, ActivationKind.Reopen);
                    await PumpAsync();

                    Assert.True(window.IsVisible);
                    Assert.True(window.ShowInTaskbar);
                    Assert.Equal(WindowState.Normal, window.WindowState);
                    Assert.Same(viewModel, window.DataContext);
                    Assert.Same(window, Assert.Single(GetAppWindows(app)));
                    Assert.Equal("Keep this unsent draft", viewModel.ChatVM.PromptText);
                    Assert.True(viewModel.SettingsVM.MinimizeToTray);
                }
            }
            finally
            {
                CloseWindow(window, viewModel);
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(ActivationKind.Background)]
    [InlineData(ActivationKind.File)]
    [InlineData(ActivationKind.OpenUri)]
    public async Task OtherActivationKinds_DoNotRestoreTheHiddenWindow(ActivationKind kind)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var (window, viewModel) = CreateWindow(minimizeToTray: true);
            var app = CreateAppWithWindow(window);
            window.Show();
            try
            {
                window.Close();
                Activate(app, kind);
                await PumpAsync();
                Assert.False(window.IsVisible);
            }
            finally
            {
                CloseWindow(window, viewModel);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReopenAfterMinimizing_RestoresNormalWindowState()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var (window, viewModel) = CreateWindow(minimizeToTray: true);
            var app = CreateAppWithWindow(window);
            window.Show();
            try
            {
                window.WindowState = WindowState.Minimized;
                Activate(app, ActivationKind.Reopen);
                await PumpAsync();
                Assert.True(window.IsVisible);
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.Same(window, Assert.Single(GetAppWindows(app)));
            }
            finally
            {
                CloseWindow(window, viewModel);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task QueuedReopen_DoesNotRestoreAWindowDuringShutdown()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var (window, viewModel) = CreateWindow(minimizeToTray: true);
            var app = CreateAppWithWindow(window);
            window.Show();
            try
            {
                window.Close();
                Activate(app, ActivationKind.Reopen);
                typeof(App).GetField("_isShuttingDown", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(app, true);
                await PumpAsync();
                Assert.False(window.IsVisible);
            }
            finally
            {
                CloseWindow(window, viewModel);
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(WindowState.Maximized, false)]
    [InlineData(WindowState.FullScreen, false)]
    [InlineData(WindowState.Maximized, true)]
    [InlineData(WindowState.FullScreen, true)]
    public async Task ReopenKeepsTheExistingWindowState(WindowState state, bool hideFirst)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var (window, viewModel) = CreateWindow(minimizeToTray: true);
            var app = CreateAppWithWindow(window);
            window.Show();
            try
            {
                window.WindowState = state;
                await PumpAsync();
                if (hideFirst)
                {
                    window.Close();
                    await PumpAsync();
                    Assert.False(window.IsVisible);
                }
                Activate(app, ActivationKind.Reopen);
                await PumpAsync();

                Assert.True(window.IsVisible);
                Assert.Equal(state, window.WindowState);
            }
            finally
            {
                CloseWindow(window, viewModel);
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(WindowState.Maximized, WindowState.Maximized)]
    [InlineData(WindowState.FullScreen, WindowState.FullScreen)]
    [InlineData(WindowState.Minimized, WindowState.Normal)]
    public async Task FocusingDetachedChatRestoresOnlyMinimizedWindows(
        WindowState state, WindowState expectedState)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var window = new ChatWindow();
            window.Show();
            try
            {
                window.WindowState = state;
                await PumpAsync();
                typeof(App).GetMethod("FocusChatWindow", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [window]);
                await PumpAsync();
                Assert.Equal(expectedState, window.WindowState);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static App CreateAppWithWindow(MainWindow window)
    {
        var app = new App();
        GetAppWindows(app).Add(window);
        typeof(App).GetField("_mainWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(app, window);
        return app;
    }

    private static List<MainWindow> GetAppWindows(App app) =>
        (List<MainWindow>)typeof(App).GetField("_windows", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(app)!;

    private static void Activate(App app, ActivationKind kind) =>
        typeof(App).GetMethod("OnApplicationActivated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(app, [null, new ActivatedEventArgs(kind)]);

    private static (MainWindow Window, MainViewModel ViewModel) CreateWindow(
        bool minimizeToTray,
        MainWindow? window = null)
    {
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false,
                MinimizeToTray = minimizeToTray
            }
        };
        var viewModel = new MainViewModel(new DataStore(data), TestCopilot.Shared, new UpdateService());
        window ??= new MainWindow();
        window.DataContext = viewModel;
        window.Width = 1100;
        window.Height = 820;
        window.ShowInTaskbar = true;

        return (window, viewModel);
    }

    private static void CloseWindow(MainWindow window, MainViewModel viewModel)
    {
        viewModel.SettingsVM.MinimizeToTray = false;

        if (!window.IsVisible)
            window.Show();

        window.Close();
        viewModel.Dispose();
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private sealed class TestMainWindow : MainWindow
    {
        public IReadOnlyList<Window> DesktopWindows { get; set; } = [];

        protected override IReadOnlyList<Window> GetDesktopWindows() => DesktopWindows;

        public WindowClosingEventArgs RaiseClosing(WindowCloseReason reason)
        {
            var args = (WindowClosingEventArgs)Activator.CreateInstance(
                typeof(WindowClosingEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: [reason, false],
                culture: null)!;
            OnClosing(args);
            return args;
        }
    }
}
