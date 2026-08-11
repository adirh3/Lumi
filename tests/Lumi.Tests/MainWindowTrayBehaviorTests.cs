using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
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
    }
}
