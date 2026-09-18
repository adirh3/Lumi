using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Lumi.Views.Controls;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class SettingsMobileNavigationTests
{
    [Fact]
    public async Task MobileSidebarItemMatchesTheMobileSettingsPage()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    IsOnboarded = true,
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };
            var viewModel = new MainViewModel(
                new DataStore(data),
                TestCopilot.Shared,
                new UpdateService(),
                startBackgroundJobs: false);
            var window = new MainWindow
            {
                DataContext = viewModel,
                Width = 1100,
                Height = 820
            };

            window.Show();
            try
            {
                viewModel.SelectedNavIndex = 7;
                await PumpAsync();

                var settingsSidebar = window.FindControl<Panel>("SidebarSettings")
                    ?? throw new InvalidOperationException("Settings sidebar was not found.");
                var sidebarList = settingsSidebar.GetVisualDescendants()
                    .OfType<ListBox>()
                    .Single();
                var labels = sidebarList.GetVisualDescendants()
                    .OfType<ListBoxItem>()
                    .Select(item => item.GetVisualDescendants()
                        .OfType<TextBlock>()
                        .First()
                        .Text)
                    .ToArray();

                Assert.Equal(viewModel.SettingsVM.Pages, labels);

                viewModel.SettingsVM.SelectedPageIndex = 2;
                await PumpAsync();

                var settingsView = window.GetVisualDescendants()
                    .OfType<SettingsView>()
                    .Single();
                Assert.True(settingsView.FindControl<Control>("PageMobile")?.IsVisible);
                Assert.False(settingsView.FindControl<Control>("PageGeneral")?.IsVisible);
                Assert.False(settingsView.FindControl<Control>("PageAppearance")?.IsVisible);
                Assert.NotNull(settingsView.FindControl<Button>("MobileWebSetupButton"));
                Assert.NotNull(settingsView.FindControl<Button>("MobileAndroidSetupButton"));
                Assert.NotNull(settingsView.FindControl<Button>("MobileUseTailscaleButton"));
                Assert.NotNull(settingsView.FindControl<Button>("MobileUseDevTunnelButton"));
                Assert.NotNull(settingsView.FindControl<Button>("MobileUseLocalNetworkButton"));
                Assert.Null(settingsView.FindControl<ToggleSwitch>("RemoteInsecureLanToggle"));
                Assert.NotNull(settingsView.FindControl<QrCodeControl>("MobileSetupQrCode"));
            }
            finally
            {
                window.Close();
                viewModel.Dispose();
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(1100, 3, false)]
    [InlineData(800, 1, false)]
    [InlineData(1100, 3, true)]
    public async Task TransportChoicesStayPeersAndExplainAppCompatibility(
        int width, int expectedColumns, bool rightToLeft)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    IsOnboarded = true,
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false,
                    WindowWidth = width,
                    WindowHeight = 1000
                }
            };
            using var viewModel = new MainViewModel(
                new DataStore(data), TestCopilot.Shared, new UpdateService(),
                initializeCopilotOnStartup: false, startBackgroundJobs: false);
            var window = new MainWindow
            {
                DataContext = viewModel,
                Width = width,
                Height = 1000,
                FlowDirection = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
            };
            window.Show();
            try
            {
                viewModel.SelectedNavIndex = 7;
                viewModel.SettingsVM.SelectedPageIndex = 2;
                viewModel.SettingsVM.RemoteAccessEnabled = true;
                viewModel.SettingsVM.IsMobileTailscaleAvailable = true;
                await PumpAsync();
                window.UpdateLayout();
                await PumpAsync();

                var view = window.GetVisualDescendants().OfType<SettingsView>().Single();
                var choices = view.FindControl<Grid>("MobileTransportChoices")!;
                var buttons = choices.Children.OfType<Button>().ToArray();
                Assert.Equal(3, buttons.Length);
                Assert.True(expectedColumns == choices.ColumnDefinitions.Count,
                    $"Expected {expectedColumns} columns; window={window.Bounds}, " +
                    $"page={view.FindControl<Control>("PageMobile")!.Bounds}, choices={choices.Bounds}, " +
                    $"actualColumns={choices.ColumnDefinitions.Count}.");
                Assert.Equal(
                    ["MobileUseTailscaleButton", "MobileUseDevTunnelButton", "MobileUseLocalNetworkButton"],
                    buttons.Select(button => Assert.IsType<string>(button.Name)).ToArray());
                Assert.All(buttons, button =>
                {
                    Assert.InRange(button.Bounds.Width, 140, 680);
                    Assert.True(button.Bounds.Height >= 48);
                    Assert.InRange(Math.Abs(buttons[0].Bounds.Width - button.Bounds.Width), 0, 1);
                });
                if (expectedColumns == 3)
                {
                    Assert.All(buttons, button =>
                    {
                        Assert.Equal(buttons[0].Bounds.Y, button.Bounds.Y);
                        Assert.Equal(buttons[0].Bounds.Height, button.Bounds.Height);
                        Assert.Equal(0, Grid.GetRow(button));
                    });
                }
                else
                {
                    Assert.All(buttons, button => Assert.Equal(0, Grid.GetColumn(button)));
                    Assert.True(buttons[0].Bounds.Bottom < buttons[1].Bounds.Top);
                    Assert.True(buttons[1].Bounds.Bottom < buttons[2].Bounds.Top);
                }

                var microsoft = buttons[1];
                var point = microsoft.TranslatePoint(
                    new Point(microsoft.Bounds.Width / 2, microsoft.Bounds.Height / 2), window)!.Value;
                var hit = window.InputHitTest(point);
                Assert.True(ReferenceEquals(hit, microsoft)
                    || hit is Visual visual && visual.GetVisualAncestors().Contains(microsoft));
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                await PumpAsync();
                Assert.True(data.Settings.RemoteUseDevTunnel);
                Assert.False(data.Settings.RemoteAllowInsecureLan);
                Assert.Contains("selected", microsoft.Classes);

                viewModel.SettingsVM.IsMobileSetupChoiceEnabled = true;
                await PumpAsync();
                Assert.True(view.FindControl<Button>("MobileWebSetupButton")!.IsEffectivelyEnabled);
                Assert.False(view.FindControl<Button>("MobileAndroidSetupButton")!.IsEffectivelyEnabled);
                Assert.False(view.FindControl<Button>("MobileDevTunnelRetryButton")!.IsVisible);
                Assert.Equal(Loc.Get("Remote_SetupWebOnlyDescription"),
                    viewModel.SettingsVM.MobileExperienceDescription);
            }
            finally
            {
                window.Close();
                Loc.Load("en");
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallConfirmationIsReachableAndDismissalCancels(bool escape)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    IsOnboarded = true,
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };
            using var viewModel = new MainViewModel(
                new DataStore(data), TestCopilot.Shared, new UpdateService(),
                initializeCopilotOnStartup: false, startBackgroundJobs: false);
            var window = new MainWindow { DataContext = viewModel };
            window.Show();
            try
            {
                await PumpAsync();
                viewModel.SettingsVM.IsDevTunnelInstallDialogOpen = true;
                await PumpAsync();
                window.UpdateLayout();
                await PumpAsync();

                var dialog = window.FindControl<StrataDialog>("DevTunnelInstallDialog")!;
                var cancel = window.FindControl<Button>("DevTunnelInstallCancelButton")!;
                var install = window.FindControl<Button>("DevTunnelInstallContinueButton")!;
                Assert.True(dialog.IsDialogOpen);
                Assert.Equal(Loc.Get("Remote_DevTunnelInstallTitle"), dialog.Title);
                Assert.True(cancel.IsFocused);
                Assert.True(cancel.Bounds.Height >= 48);
                Assert.True(install.Bounds.Height >= 48);
                if (escape)
                {
                    window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
                }
                else
                {
                    var point = cancel.TranslatePoint(
                        new Point(cancel.Bounds.Width / 2, cancel.Bounds.Height / 2), window)!.Value;
                    var hit = window.InputHitTest(point);
                    Assert.True(ReferenceEquals(hit, cancel)
                        || hit is Visual visual && visual.GetVisualAncestors().Contains(cancel));
                    window.MouseDown(point, MouseButton.Left);
                    window.MouseUp(point, MouseButton.Left);
                }
                await PumpAsync();
                Assert.False(viewModel.SettingsVM.IsDevTunnelInstallDialogOpen);
                Assert.False(dialog.IsDialogOpen);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static async Task PumpAsync()
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(20);
        Dispatcher.UIThread.RunJobs();
    }
}
