using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class IdleSessionTimeoutSettingsTests
{
    private static SettingsViewModel CreateVm(AppData data) => new(
        new DataStore(data), TestCopilot.Shared, new BrowserService(), new UpdateService());

    [Fact]
    public void DefaultAndEditedTimeout_SaveAndRevert()
    {
        var data = new AppData();
        using var vm = CreateVm(data);
        Assert.Equal(60m, vm.IdleSessionTimeoutMinutes);
        Assert.False(vm.IsIdleSessionTimeoutModified);

        vm.IdleSessionTimeoutMinutes = 180;
        Assert.Equal(180, data.Settings.IdleSessionTimeoutMinutes);
        Assert.True(vm.IsIdleSessionTimeoutModified);

        vm.RevertIdleSessionTimeoutCommand.Execute(null);
        Assert.Equal(60m, vm.IdleSessionTimeoutMinutes);
        Assert.Equal(60, data.Settings.IdleSessionTimeoutMinutes);
        Assert.False(vm.IsIdleSessionTimeoutModified);
    }

    [Fact]
    public void SavedTimeout_LoadsAndResetsWithAllSettings()
    {
        var data = new AppData { Settings = new UserSettings { IdleSessionTimeoutMinutes = 180 } };
        using var vm = CreateVm(data);
        Assert.Equal(180m, vm.IdleSessionTimeoutMinutes);
        Assert.True(vm.IsIdleSessionTimeoutModified);

        vm.ResetSettingsCommand.Execute(null);

        Assert.Equal(60m, vm.IdleSessionTimeoutMinutes);
        Assert.Equal(60, data.Settings.IdleSessionTimeoutMinutes);
        Assert.False(vm.IsIdleSessionTimeoutModified);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(525600)]
    public void Boundaries_AreAccepted(int minutes)
    {
        var data = new AppData();
        using var vm = CreateVm(data);
        vm.IdleSessionTimeoutMinutes = minutes;
        Assert.Equal(minutes, data.Settings.IdleSessionTimeoutMinutes);
    }

    [Fact]
    public void InvalidEdits_DoNotChangeStoredOrDisplayedValue()
    {
        var data = new AppData();
        using var vm = CreateVm(data);
        vm.IdleSessionTimeoutMinutes = 180;

        foreach (var value in new decimal?[] { null, 0, -1, 525601, 60.5m })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => vm.IdleSessionTimeoutMinutes = value);
            Assert.Equal(180m, vm.IdleSessionTimeoutMinutes);
            Assert.Equal(180, data.Settings.IdleSessionTimeoutMinutes);
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("he")]
    public async Task ChatSettings_AreLocalizedSearchableAndInteractive(string language)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load(language);
            var data = new AppData();
            using var vm = CreateVm(data);
            vm.SelectedPageIndex = 4;
            using var view = new SettingsView { DataContext = vm };
            var window = new Window { Content = view, Width = 1100, Height = 1000 };
            window.Show();
            try
            {
                Pump(window);
                var setting = view.FindSettingsControl<StrataSetting>("IdleSessionTimeoutSetting")!;
                var input = view.FindSettingsControl<NumericUpDown>("IdleSessionTimeoutInput")!;
                Assert.Equal(Loc.Setting_IdleSessionTimeout, setting.Header);
                Assert.Equal(Loc.SettingDesc_IdleSessionTimeout, setting.Description);
                Assert.Equal(Loc.Setting_IdleSessionTimeout, AutomationProperties.GetName(input));
                Assert.Equal(60m, input.Value);

                input.SetCurrentValue(NumericUpDown.ValueProperty, 25m);
                Pump(window);
                Assert.Equal(25, data.Settings.IdleSessionTimeoutMinutes);
                Assert.True(setting.IsModified);

                setting.RaiseEvent(new RoutedEventArgs(StrataSetting.RevertedEvent, setting));
                Pump(window);
                Assert.Equal(60m, input.Value);
                Assert.Equal(60, data.Settings.IdleSessionTimeoutMinutes);
                Assert.False(setting.IsModified);

                vm.SearchQuery = language == "he" ? "סשן" : "idle session";
                Pump(window);
                Assert.True(setting.IsEffectivelyVisible);
            }
            finally
            {
                window.Close();
                Loc.Load("en");
            }
        }, CancellationToken.None);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
