using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class SettingsPageLifetimeTests
{
    [Theory]
    [InlineData(0, "PageProfile")]
    [InlineData(1, "PageGeneral")]
    [InlineData(2, "PageMobile")]
    [InlineData(3, "PageAppearance")]
    [InlineData(4, "PageChat")]
    [InlineData(5, "PageAI")]
    [InlineData(6, "PagePrivacy")]
    [InlineData(7, "PageAbout")]
    [InlineData(9, "PagePermissions")]
    [InlineData(10, "PageAiFeatures")]
    public async Task OnlyTheSelectedPageIsConstructed(int index, string name)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            using var vm = CreateViewModel();
            vm.SelectedPageIndex = index;
            using var view = new SettingsView { DataContext = vm };
            var window = new Window { Content = view, Width = 1000, Height = 800 };
            window.Show();
            try
            {
                Pump(window);
                Assert.Equal([name], Pages(view).Select(page => page.Name));
                Assert.True(view.FindSettingsControl<Control>(name)!.IsVisible);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SwitchingPagesReleasesTheOldPanelAndPreservesValues()
    {
        using var session = HeadlessTestSession.Start();
        WeakReference? released = null;
        SettingsView? retainedView = null;
        SettingsViewModel? retainedModel = null;
        Window? window = null;
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var vm = CreateViewModel();
            retainedModel = vm;
            vm.SelectedPageIndex = 3;
            var view = new SettingsView { DataContext = vm };
            retainedView = view;
            window = new Window { Content = view, Width = 1000, Height = 800 };
            window.Show();
            Pump(window);
            vm.EditingByokModel = new ByokModel { DisplayName = "Unsaved model", ModelId = "draft-model" };
            released = SwitchToGeneral(view, vm);
            Pump(window);
            Assert.Equal(["PageGeneral"], Pages(view).Select(page => page.Name));
            Assert.Equal("Unsaved model", vm.EditingByokModel.DisplayName);
            vm.SelectedPageIndex = 3;
            Pump(window);
            Assert.Equal(["PageAppearance"], Pages(view).Select(page => page.Name));
        }, CancellationToken.None);
        try
        {
            await CollectAsync(session);
            Assert.False(released!.IsAlive);
            GC.KeepAlive(retainedView);
            GC.KeepAlive(retainedModel);
        }
        finally
        {
            await session.Dispatch(() =>
            {
                window!.Close();
                retainedView!.Dispose();
                retainedModel!.Dispose();
            }, CancellationToken.None);
        }
    }

    [Fact]
    public async Task GlobalSearchMaterializesPagesOnlyUntilSearchEnds()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            using var vm = CreateViewModel();
            vm.SelectedPageIndex = 3;
            using var view = new SettingsView { DataContext = vm };
            var window = new Window { Content = view, Width = 1000, Height = 800 };
            window.Show();
            try
            {
                Pump(window);
                vm.SearchQuery = "notifications";
                Pump(window);
                Assert.Equal(10, Pages(view).Count());
                Assert.True(view.FindSettingsControl<Control>("PageGeneral")!.IsEffectivelyVisible);
                vm.SearchQuery = "";
                Pump(window);
                Assert.Equal(["PageAppearance"], Pages(view).Select(page => page.Name));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LeavingSettingsReleasesItsViewWithoutDiscardingDrafts()
    {
        using var session = HeadlessTestSession.Start();
        WeakReference? released = null;
        MainViewModel? retainedMain = null;
        MainWindow? window = null;
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var data = new AppData
            {
                Settings = new UserSettings { IsOnboarded = true, AutoSaveChats = false, EnableMemoryAutoSave = false },
            };
            var main = new MainViewModel(new DataStore(data), TestCopilot.Shared, new UpdateService(),
                startBackgroundJobs: false, initializeCopilotOnStartup: false);
            retainedMain = main;
            window = new MainWindow { DataContext = main, Width = 1100, Height = 820 };
            window.Show();
            Assert.Null(window.FindControl<ContentControl>("PageSettingsHost")!.Content);
            main.SettingsVM.SelectedPageIndex = 3;
            main.SelectedNavIndex = 7;
            Pump(window);
            main.SettingsVM.EditingByokModel = new ByokModel { DisplayName = "Draft account model" };
            main.SettingsVM.CookieImportStatus = "Import in progress";
            main.SettingsVM.IsCookieImportInProgress = true;
            released = LeaveSettings(window, main);
            Pump(window);
            Assert.Null(window.FindControl<ContentControl>("PageSettingsHost")!.Content);
            main.SettingsVM.SearchQuery = "theme";
            main.OpenSettingsCommand.Execute(null);
            Pump(window);
            Assert.Equal(7, main.SelectedNavIndex);
            Assert.Equal("Draft account model", main.SettingsVM.EditingByokModel.DisplayName);
            Assert.True(main.SettingsVM.IsCookieImportInProgress);
            Assert.Equal("Import in progress", main.SettingsVM.CookieImportStatus);
            main.SettingsVM.IsCookieImportInProgress = false;
            main.SettingsVM.SearchQuery = "";
            main.SelectedNavIndex = 0;
            Pump(window);
        }, CancellationToken.None);
        try
        {
            await CollectAsync(session);
            Assert.False(released!.IsAlive);
            GC.KeepAlive(retainedMain);
        }

        finally
        {
            await session.Dispatch(() =>
            {
                window!.Close();
                retainedMain!.Dispose();
            }, CancellationToken.None);
        }
    }

    private static SettingsViewModel CreateViewModel()
        => new(new DataStore(new AppData()), TestCopilot.Shared, new BrowserService(),
            new UpdateService(), new FakeSecureKeyStore());

    [Fact]
    public async Task ReleasingGeneralStopsHotkeyRecording()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var vm = CreateViewModel();
            vm.SelectedPageIndex = 1;
            using var view = new SettingsView { DataContext = vm };
            var window = new Window { Content = view, Width = 1000, Height = 800 };
            window.Show();
            try
            {
                Pump(window);
                view.FindSettingsControl<Button>("HotkeyRecorderButton")!
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.True(view.IsRecordingHotkey);
                vm.SelectedPageIndex = 3;
                Assert.False(view.IsRecordingHotkey);
                Assert.Null(view.FindSettingsControl<Button>("HotkeyRecorderButton"));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AnEarlierCookieImportSuccessDoesNotCloseTheNextProgressDialog()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var vm = CreateViewModel();
            vm.SelectedPageIndex = 6;
            using var view = new SettingsView { DataContext = vm };
            var window = new Window { Content = view, Width = 1000, Height = 800 };
            window.Show();
            try
            {
                Pump(window);
                var dialog = view.FindControl<StrataTheme.Controls.StrataDialog>("CookieImportDialog")!;
                vm.CookieImportSucceeded = true;
                dialog.IsDialogOpen = true;
                vm.CookieImportStatus = "Retrying import";
                Assert.True(dialog.IsDialogOpen);
                vm.CookieImportSucceeded = false;
                vm.IsCookieImportInProgress = true;
                Assert.True(dialog.IsDialogOpen);
                view.Dispose();
                Assert.True(vm.IsCookieImportInProgress);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    private static IEnumerable<Control> Pages(SettingsView view)
        => view.GetLogicalDescendants().OfType<Control>()
            .Where(control => control.Classes.Contains("st-page"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleasingAppearanceNeverChangesAnUneditedScale(bool showWindow)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var vm = CreateViewModel();
            vm.UiScalePercent = 125;
            vm.SelectedPageIndex = 3;
            using var view = new SettingsView { DataContext = vm };
            var window = new Window { Content = view, Width = 1000, Height = 800 };
            try
            {
                if (showWindow)
                {
                    window.Show();
                    Pump(window);
                }
                vm.SelectedPageIndex = 1;
                view.Dispose();
                Assert.Equal(125, vm.UiScalePercent);
            }
            finally
            {
                vm.UiScalePercent = 100;
                if (showWindow)
                    window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReleasingBoundFieldsPreservesTheEditedValue()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            using var vm = CreateViewModel();
            using var view = new SettingsView { DataContext = vm };
            var window = new Window { Content = view, Width = 1000, Height = 800 };
            window.Show();
            try
            {
                Pump(window);
                view.FindSettingsControl<TextBox>("ProfileNameBox")!.Text = "Edited profile name";
                Pump(window);
                Assert.Equal("Edited profile name", vm.UserName);
                vm.SelectedPageIndex = 3;
                Pump(window);
                view.Dispose();
                Assert.Equal("Edited profile name", vm.UserName);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LoadedViewInitializesAnAlreadySearchingReplacementModel()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            using var first = CreateViewModel();
            using var replacement = CreateViewModel();
            replacement.SearchQuery = "ocean";
            using var view = new SettingsView { DataContext = first };
            var window = new Window { Content = view, Width = 1000, Height = 800 };
            window.Show();
            try
            {
                Pump(window);
                view.DataContext = replacement;
                Pump(window);
                Assert.NotEmpty(replacement.SearchResultSummary);
                Assert.True(view.FindSettingsControl<Control>("PageAppearance")!.IsEffectivelyVisible);
                Assert.Equal(10, Pages(view).Count());
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SwitchToGeneral(SettingsView view, SettingsViewModel vm)
    {
        var reference = new WeakReference(view.FindSettingsControl<Control>("PageAppearance")!);
        vm.SelectedPageIndex = 1;
        return reference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LeaveSettings(MainWindow window, MainViewModel vm)
    {
        var reference = new WeakReference(window.GetLogicalDescendants().OfType<SettingsView>().Single());
        vm.SelectedNavIndex = 0;
        return reference;
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static async Task CollectAsync(HeadlessTestSession session)
    {
        for (var index = 0; index < 8; index++)
        {
            await session.Dispatch(() =>
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }, CancellationToken.None);
            await Task.Delay(40);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
