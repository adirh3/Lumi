using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Converters;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// The bar names five places: Chats, Library, Jobs, Customize and Settings. Projects are managed from
/// the chat list's project switcher and Memories is a Settings page, so each lights the place it
/// belongs to.
/// </summary>
[Collection("Headless UI")]
public sealed class NavigationBarTests
{
    private static readonly string[] Places = ["NavChat", "NavLibrary", "NavJobs", "NavCustomize", "NavSettings"];

    private static MainViewModel CreateMainViewModel(AppData data, string? skillsDirectory = null)
    {
        Loc.Load("en");
        data.Settings.IsOnboarded = true;
        data.Settings.AutoSaveChats = false;
        data.Settings.EnableMemoryAutoSave = false;
        return new MainViewModel(
            skillsDirectory is null ? new DataStore(data) : new DataStore(data, skillsDirectory),
            TestCopilot.Shared,
            new UpdateService(),
            startBackgroundJobs: false,
            initializeCopilotOnStartup: false);
    }

    [Fact]
    public void Settings_ReopensMemories_WhenItWasTheLastSettingsPage()
    {
        using var vm = CreateMainViewModel(new AppData());

        // Choosing a Settings page while elsewhere does not navigate.
        vm.SettingsVM.SelectedPageIndex = SettingsViewModel.MemoriesPageIndex;
        Assert.Equal(0, vm.SelectedNavIndex);

        vm.OpenSettingsCommand.Execute(null);
        Assert.Equal(MainViewModel.MemoriesNavIndex, vm.SelectedNavIndex);

        // Another page from the Settings list returns to Settings; Memories in the list opens its page.
        vm.SettingsVM.SelectedPageIndex = 3;
        Assert.Equal(7, vm.SelectedNavIndex);
        vm.SettingsVM.SelectedPageIndex = SettingsViewModel.MemoriesPageIndex;
        Assert.Equal(MainViewModel.MemoriesNavIndex, vm.SelectedNavIndex);

        // Every route to Settings lands on Memories while it was the page used last.
        vm.SelectedNavIndex = 0;
        vm.SetNavCommand.Execute("7");
        Assert.Equal(MainViewModel.MemoriesNavIndex, vm.SelectedNavIndex);
        vm.SelectedNavIndex = 0;
        vm.SelectedNavIndex = 7;
        Assert.Equal(MainViewModel.MemoriesNavIndex, vm.SelectedNavIndex);
        Assert.Equal(MainViewModel.MemoriesNavIndex, MainWindow.PageForNavButton(vm, MainWindow.NavSettingsButton));

        // Opening Memories directly (search, the debug bridge) selects it in the Settings list too.
        vm.SettingsVM.SelectedPageIndex = 0;
        Assert.Equal(7, vm.SelectedNavIndex);
        Assert.Equal(7, MainWindow.PageForNavButton(vm, MainWindow.NavSettingsButton));
        vm.SelectedNavIndex = 0;
        vm.SelectedNavIndex = MainViewModel.MemoriesNavIndex;
        Assert.Equal(SettingsViewModel.MemoriesPageIndex, vm.SettingsVM.SelectedPageIndex);
        Assert.Equal(MainViewModel.MemoriesNavIndex, vm.SelectedNavIndex);
    }

    [Fact]
    public void SettingsList_ShowsMemoriesUnderProfile_AndMapsEveryRowToItsPage()
    {
        var converter = SettingsSidebarPositionConverter.Instance;
        int Row(int page) => (int)converter.Convert(page, typeof(int), null, CultureInfo.InvariantCulture)!;
        int Page(int row) => (int)converter.ConvertBack(row, typeof(int), null, CultureInfo.InvariantCulture)!;

        Assert.Equal(0, Row(0));
        Assert.Equal(1, Row(SettingsViewModel.MemoriesPageIndex));
        Assert.Equal(2, Row(1));
        Assert.Equal(SettingsViewModel.MemoriesPageIndex, Row(SettingsViewModel.AboutPageIndex));

        var rows = Enumerable.Range(0, SettingsViewModel.MemoriesPageIndex + 1).Select(Row).ToList();
        Assert.Equal(rows.Count, rows.Distinct().Count());
        foreach (var page in Enumerable.Range(0, SettingsViewModel.MemoriesPageIndex + 1))
            Assert.Equal(page, Page(Row(page)));

        Assert.Equal(-1, Page(-1));
        Assert.Equal(-1, Row(-1));
    }

    [Fact]
    public void ProjectSwitcher_CreatesEditsAndManagesProjects()
    {
        var project = new Project { Name = "Lumi" };
        using var vm = CreateMainViewModel(new AppData { Projects = [project] });

        // Nothing to edit until the chat list is narrowed to a project.
        Assert.False(vm.HasSelectedProjectFilter);
        vm.EditSelectedProjectCommand.Execute(null);
        Assert.Equal(0, vm.SelectedNavIndex);

        vm.SelectProjectFilterCommand.Execute(project);
        Assert.True(vm.HasSelectedProjectFilter);
        vm.EditSelectedProjectCommand.Execute(null);
        Assert.Equal(2, vm.SelectedNavIndex);
        Assert.Same(project, vm.ProjectsVM.SelectedProject);
        Assert.Equal(MainWindow.NavChatButton, MainWindow.NavButtonForPage(vm.SelectedNavIndex));

        vm.ManageProjectsCommand.Execute(null);
        Assert.Equal(2, vm.SelectedNavIndex);
        Assert.Null(vm.ProjectsVM.SelectedProject);
        Assert.False(vm.ProjectsVM.IsEditing);

        vm.SelectedNavIndex = 0;
        vm.NewProjectFromSwitcherCommand.Execute(null);
        Assert.Equal(2, vm.SelectedNavIndex);
        Assert.Null(vm.ProjectsVM.SelectedProject);
        Assert.True(vm.ProjectsVM.IsEditing);

        vm.ClearProjectFilterCommand.Execute(null);
        Assert.False(vm.HasSelectedProjectFilter);
    }

    [Fact]
    public void Jobs_ShowsHowManyAreRunning()
    {
        var idle = new BackgroundJob { Name = "Digest" };
        var running = new BackgroundJob { Name = "Watcher", IsRunning = true };

        using (var quiet = CreateMainViewModel(new AppData { BackgroundJobs = [idle] }))
        {
            Assert.False(quiet.HasRunningJobs);
            Assert.Equal(Loc.NavTip_Jobs, quiet.JobsNavTip);
        }

        using var busy = CreateMainViewModel(new AppData { BackgroundJobs = [idle, running] });
        Assert.True(busy.HasRunningJobs);
        Assert.Equal(1, busy.RunningJobCount);
        Assert.Equal(string.Format(Loc.Culture, Loc.NavTip_JobsRunning, 1), busy.JobsNavTip);
    }

    [Fact]
    public async Task BarTips_SitAboveTheBar_AndPauseAfterASwitchUntilThePointerMovesOn()
    {
        using var session = HeadlessTestSession.Start();
        var skillsDirectory = Path.Combine(Path.GetTempPath(), $"lumi-nav-tips-{Guid.NewGuid():N}");

        try
        {
            await session.Dispatch(async () =>
            {
                using var vm = CreateMainViewModel(new AppData(), skillsDirectory);
                var window = new MainWindow { DataContext = vm, Width = 1280, Height = 860 };
                window.Show();

                try
                {
                    async Task Settle()
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                        window.UpdateLayout();
                    }

                    T Named<T>(string name) where T : Control
                        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

                    await Settle();

                    // Tips sit above the bar and beside the rail, clear of the pointer, instead of at it.
                    foreach (var place in Places)
                    {
                        var button = Named<Button>(place);
                        Assert.Equal(PlacementMode.Top, ToolTip.GetPlacement(button));
                        Assert.True(ToolTip.GetVerticalOffset(button) < 0, $"{place}'s tip should clear the bar");
                    }
                    foreach (var rail in new[] { "RailChat", "RailLibrary", "RailJobs", "RailCustomize", "RailSettings" })
                    {
                        var button = window.FindControl<Button>(rail)!;
                        Assert.Equal(PlacementMode.Right, ToolTip.GetPlacement(button));
                        Assert.Equal(0, ToolTip.GetVerticalOffset(button));
                    }

                    // Labelled rows don't repeat their names in tips.
                    foreach (var row in new[] { "CustomizeDiscoverTab", "CustomizeLumisTab", "CustomizeSkillsTab", "CustomizeMcpTab" })
                        Assert.Null(ToolTip.GetTip(window.FindControl<Button>(row)!));

                    // The selection ring's border is part of every place, so an unnamed place keeps the
                    // width the label fitting assumes.
                    var library = Named<Button>("NavLibrary");
                    Assert.Equal(31, library.Bounds.Width, 1);
                    var pill = Named<Border>("NavPill");

                    // A place's name opens above the bar, over its own place.
                    var tip = new ToolTip { Content = ToolTip.GetTip(library) };
                    ToolTip.SetTip(library, tip);
                    ToolTip.SetIsOpen(library, true);
                    await Settle();
                    Assert.True(tip.IsAttachedToVisualTree());
                    var tipTopLeft = tip.PointToScreen(new Point(0, 0));
                    var tipBottomRight = tip.PointToScreen(new Point(tip.Bounds.Width, tip.Bounds.Height));
                    var placeCentre = library.PointToScreen(new Point(library.Bounds.Width / 2, 0)).X;
                    Assert.True(tipBottomRight.Y <= pill.PointToScreen(new Point(0, 0)).Y,
                        "The tip sits above the bar, never under the pointer on it");
                    Assert.InRange(placeCentre, tipTopLeft.X, tipBottomRight.X);
                    ToolTip.SetIsOpen(library, false);
                    await Settle();

                    var point = library.TranslatePoint(new Point(library.Bounds.Width / 2, library.Bounds.Height / 2), window)!.Value;
                    window.MouseMove(point);
                    window.MouseDown(point, MouseButton.Left);
                    await Settle();
                    Assert.Equal(MainViewModel.LibraryNavIndex, vm.SelectedNavIndex);
                    // The press captures the pointer; as the places resize under it Avalonia reports a
                    // pointer exit although nothing moved. The tips (and hover) must survive that.
                    Assert.False(ToolTip.GetServiceEnabled(pill), "Tips pause while the places resize under the pointer");
                    Assert.False(ToolTip.GetServiceEnabled(library));

                    window.MouseUp(point, MouseButton.Left);
                    window.MouseMove(point + new Vector(4, 0));
                    await Settle();
                    Assert.False(ToolTip.GetServiceEnabled(library), "A jitter isn't moving on");

                    window.MouseMove(point + new Vector(30, 0));
                    await Settle();
                    Assert.True(ToolTip.GetServiceEnabled(library), "Moving on brings the tips back");

                    // Leaving the bar after a switch brings them back too.
                    window.MouseMove(point);
                    window.MouseDown(point, MouseButton.Left);
                    window.MouseUp(point, MouseButton.Left);
                    await Settle();
                    Assert.False(ToolTip.GetServiceEnabled(library));
                    window.MouseMove(point - new Vector(0, 300));
                    await Settle();
                    Assert.True(ToolTip.GetServiceEnabled(library), "Leaving the bar brings the tips back");

                    // Pressing a place and dragging off the bar leaves it on release. The press captures
                    // the pointer, and the exit reported while the labels resize is ignored, so no other
                    // exit arrives to undo the hover.
                    var jobs = Named<Button>("NavJobs");
                    var jobsPoint = jobs.TranslatePoint(new Point(jobs.Bounds.Width / 2, jobs.Bounds.Height / 2), window)!.Value;
                    window.MouseMove(jobsPoint);
                    for (var wait = 0; wait < 50 && !jobs.Classes.Contains("hovered"); wait++)
                    {
                        await Task.Delay(20);
                        await Settle();
                    }

                    Assert.Contains("hovered", jobs.Classes);
                    window.MouseDown(jobsPoint, MouseButton.Left);
                    window.MouseMove(jobsPoint - new Vector(0, 300));
                    window.MouseUp(jobsPoint - new Vector(0, 300), MouseButton.Left);
                    await Task.Delay(200);
                    await Settle();
                    Assert.Equal(1, vm.SelectedNavIndex);
                    Assert.DoesNotContain(Places, name => Named<Button>(name).Classes.Contains("hovered"));
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(skillsDirectory, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task EveryPlace_ComesAliveOnHover_UnlessAnimationsAreOff()
    {
        using var session = HeadlessTestSession.Start();
        var skillsDirectory = Path.Combine(Path.GetTempPath(), $"lumi-nav-motion-{Guid.NewGuid():N}");

        try
        {
            await session.Dispatch(async () =>
            {
                using var vm = CreateMainViewModel(new AppData(), skillsDirectory);
                var window = new MainWindow { DataContext = vm, Width = 1280, Height = 860 };
                window.Show();

                try
                {
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    window.UpdateLayout();

                    // Every place moves the way its thing does, in the bar and in the rail.
                    (string Bar, string Rail, string Icon, string Still, string Hovered)[] motions =
                    [
                        ("NavChat", "RailChat", "nav-chat", "rotate(0deg) scale(1)", "rotate(-8deg) scale(1.12)"),
                        ("NavLibrary", "RailLibrary", "nav-books", "rotate(0deg) scale(1)", "rotate(-8deg) scale(1.12)"),
                        ("NavJobs", "RailJobs", "nav-clock", "rotate(0deg)", "rotate(360deg)"),
                        ("NavCustomize", "RailCustomize", "nav-knob-left", "translateX(0px)", "translateX(-3.3px)"),
                        ("NavCustomize", "RailCustomize", "nav-knob-right", "translateX(0px)", "translateX(3.3px)"),
                        ("NavSettings", "RailSettings", "nav-gear", "rotate(0deg)", "rotate(60deg)"),
                    ];

                    Button Place(string name) => window.FindControl<Button>(name)!;
                    static void Hover(Button place, bool on) => ((IPseudoClasses)place.Classes).Set(":pointerover", on);

                    // The pose a motion ends in (the style's value), not the transition's current frame.
                    static void AssertPose(Button place, string iconClass, string pose)
                    {
                        var icon = place.GetLogicalDescendants().OfType<PathIcon>().Single(i => i.Classes.Contains(iconClass));
                        var transform = Assert.IsType<TransformOperations>(icon.GetBaseValue(Visual.RenderTransformProperty).GetValueOrDefault());
                        Assert.Equal(TransformOperations.Parse(pose).Operations, transform.Operations);
                    }

                    foreach (var (bar, rail, icon, rest, hovered) in motions)
                    {
                        AssertPose(Place(bar), icon, rest);
                        Hover(Place(bar), true);
                        AssertPose(Place(bar), icon, hovered);
                        Hover(Place(bar), false);

                        // The pill's hover can pick a place before the pointer is over its button.
                        Place(bar).Classes.Add("hovered");
                        AssertPose(Place(bar), icon, hovered);
                        Place(bar).Classes.Remove("hovered");
                        AssertPose(Place(bar), icon, rest);

                        AssertPose(Place(rail), icon, rest);
                        Hover(Place(rail), true);
                        AssertPose(Place(rail), icon, hovered);
                        Hover(Place(rail), false);
                    }

                    // The sliders are stacked layers sharing one frame, so they register as a single glyph.
                    foreach (var name in new[] { "NavCustomize", "RailCustomize" })
                    {
                        var layers = Place(name).GetLogicalDescendants().OfType<PathIcon>().ToArray();
                        Assert.Equal(3, layers.Length);
                        Assert.All(layers, layer => Assert.Equal(new Rect(3, 4, 18, 16), layer.Data!.Bounds));
                    }

                    // With animations off in Settings, the places keep still.
                    vm.SettingsVM.ShowAnimations = false;
                    foreach (var (bar, rail, icon, rest, _) in motions)
                    {
                        Hover(Place(bar), true);
                        Place(bar).Classes.Add("hovered");
                        Hover(Place(rail), true);
                        AssertPose(Place(bar), icon, rest);
                        AssertPose(Place(rail), icon, rest);
                        Hover(Place(bar), false);
                        Place(bar).Classes.Remove("hovered");
                        Hover(Place(rail), false);
                    }
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(skillsDirectory, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task Bar_LightsOnePlace_AndTheProjectSwitcherReachesProjects()
    {
        using var session = HeadlessTestSession.Start();
        var skillsDirectory = Path.Combine(Path.GetTempPath(), $"lumi-nav-ui-{Guid.NewGuid():N}");

        try
        {
            await session.Dispatch(async () =>
            {
                var project = new Project { Name = "Lumi", Instructions = "Keep it simple." };
                var data = new AppData
                {
                    Projects = [project],
                    Memories = [new Memory { Key = "Name", Content = "Adir", Category = "Personal" }],
                    Chats = [new Chat { Title = "Planning", ProjectId = project.Id }]
                };
                using var vm = CreateMainViewModel(data, skillsDirectory);
                var window = new MainWindow { DataContext = vm, Width = 1280, Height = 860 };
                window.Show();

                try
                {
                    async Task Settle()
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                        window.UpdateLayout();
                    }

                    T Named<T>(string name) where T : Control
                        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

                    async Task Click(string name)
                    {
                        var button = Named<Button>(name);
                        Assert.True(button.IsEffectivelyVisible, $"{name} should be visible");
                        new ButtonAutomationPeer(button).Invoke();
                        await Settle();
                    }

                    // One place is lit, and only the lit place may carry its name.
                    void AssertLit(string place)
                    {
                        foreach (var name in Places)
                        {
                            var classes = Named<Button>(name).Classes;
                            Assert.Equal(name == place, classes.Contains("active"));
                            if (name != place)
                                Assert.DoesNotContain("labeled", classes);
                        }
                    }

                    await Settle();
                    Assert.Equal(Places, Named<StackPanel>("NavRow").Children.OfType<Button>().Select(button => button.Name));
                    Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(),
                        button => button.Name is "NavProjects" or "NavMemories" or "LibraryEntryButton" or "LibraryBackButton");
                    AssertLit("NavChat");

                    await Click("NavLibrary");
                    Assert.Equal(MainViewModel.LibraryNavIndex, vm.SelectedNavIndex);
                    AssertLit("NavLibrary");

                    await Click("NavJobs");
                    Assert.Equal(1, vm.SelectedNavIndex);
                    AssertLit("NavJobs");

                    await Click("NavCustomize");
                    Assert.True(MainViewModel.IsCustomizeIndex(vm.SelectedNavIndex));
                    Assert.True(Named<Border>("CustomizeSwitcher").IsVisible);
                    AssertLit("NavCustomize");

                    // Settings remembers Memories: leaving and coming back reopens it.
                    await Click("NavSettings");
                    Assert.Equal(7, vm.SelectedNavIndex);
                    AssertLit("NavSettings");
                    Named<ListBox>("SettingsPagesList").SelectedItem = Named<ListBoxItem>("SettingsMemoriesItem");
                    await Settle();
                    Assert.Equal(MainViewModel.MemoriesNavIndex, vm.SelectedNavIndex);
                    AssertLit("NavSettings");
                    await Click("NavChat");
                    AssertLit("NavChat");
                    await Click("NavSettings");
                    Assert.Equal(MainViewModel.MemoriesNavIndex, vm.SelectedNavIndex);
                    Assert.True(Named<Panel>("MemoriesOverview").IsVisible);
                    await Click("NavChat");

                    // The switcher is always in the chat list; its drawer manages projects and closes
                    // behind the page it opens.
                    Assert.False(Named<Button>("ProjectEditButton").IsVisible);
                    await Click("ProjectSwitchButton");
                    Assert.Contains("open", Named<Button>("ProjectSwitchButton").Classes);
                    new ButtonAutomationPeer(Named<Button>("ProjectSwitcherManageButton")).Invoke();
                    await Settle();
                    Assert.Equal(2, vm.SelectedNavIndex);
                    Assert.DoesNotContain("open", Named<Button>("ProjectSwitchButton").Classes);
                    Assert.True(Named<Panel>("ProjectsOverview").IsVisible);
                    AssertLit("NavChat");

                    // Narrowed to a project, the gear beside the switcher edits that project.
                    await Click("NavChat");
                    vm.SelectProjectFilterCommand.Execute(project);
                    await Settle();
                    await Click("ProjectEditButton");
                    Assert.Equal(2, vm.SelectedNavIndex);
                    Assert.Same(project, vm.ProjectsVM.SelectedProject);
                    Assert.True(Named<Panel>("ProjectDetail").IsVisible);
                    AssertLit("NavChat");
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(skillsDirectory, recursive: true); }
            catch { }
        }
    }
}
