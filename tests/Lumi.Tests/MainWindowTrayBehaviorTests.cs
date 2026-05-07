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

    private static (MainWindow Window, MainViewModel ViewModel) CreateWindow(bool minimizeToTray)
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
        var viewModel = new MainViewModel(new DataStore(data), new CopilotService(), new UpdateService());
        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 1100,
            Height = 820,
            ShowInTaskbar = true
        };

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
}
