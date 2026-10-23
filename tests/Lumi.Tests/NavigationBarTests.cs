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
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Converters;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Animation;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// The bar names five places: Chats, Library, Jobs, Customize and Settings. Projects are set up in
/// Customize and Memories is a Settings page, so each lights the place it belongs to.
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
        // Permissions follows Chat, AI Features follows AI & Models; About stays the last row.
        Assert.Equal(Row(4) + 1, Row(SettingsViewModel.PermissionsPageIndex));
        Assert.Equal(Row(SettingsViewModel.AiModelsPageIndex) + 1, Row(SettingsViewModel.AiFeaturesPageIndex));
        var lastPage = SettingsViewModel.AiFeaturesPageIndex;
        Assert.Equal(lastPage, Row(SettingsViewModel.AboutPageIndex));

        var rows = Enumerable.Range(0, lastPage + 1).Select(Row).ToList();
        Assert.Equal(rows.Count, rows.Distinct().Count());
        foreach (var page in Enumerable.Range(0, lastPage + 1))
            Assert.Equal(page, Page(Row(page)));

        Assert.Equal(-1, Page(-1));
        Assert.Equal(-1, Row(-1));
    }

    [Fact]
    public void ProjectSwitcher_CreatesEditsAndManagesProjects()
    {
        var project = new Project { Name = "Lumi" };
        var other = new Project { Name = "Strata" };
        using var vm = CreateMainViewModel(new AppData { Projects = [project, other] });

        // A project's gear in the switcher opens it in Customize → Projects, without switching the
        // chat list to it.
        vm.SelectProjectFilterCommand.Execute(project);
        vm.EditProjectCommand.Execute(other);
        Assert.Equal(2, vm.SelectedNavIndex);
        Assert.Same(other, vm.ProjectsVM.SelectedProject);
        Assert.Equal(project.Id, vm.SelectedProjectFilter);
        Assert.Equal(MainWindow.NavCustomizeButton, MainWindow.NavButtonForPage(vm.SelectedNavIndex));
        Assert.True(vm.IsProjectsSection);

        // Choosing the open section's row again returns to every project.
        vm.OpenCustomizeSectionCommand.Execute("2");
        Assert.Null(vm.ProjectsVM.SelectedProject);
        Assert.False(vm.ProjectsVM.IsEditing);

        vm.SelectedNavIndex = 0;
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
        Assert.Null(vm.SelectedProjectFilter);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bar_FrostsOnlyThePill_LeavingItsSurroundingsClear(bool isDark)
    {
        using var session = HeadlessTestSession.Start(typeof(SkiaHeadlessTestApp));

        await session.Dispatch(() =>
        {
            var window = new MainWindow
            {
                RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light,
            };
            try
            {
                var nav = window.FindControl<Panel>("SidebarNav")!;
                var pill = window.FindControl<Border>("NavPill")!;
                Assert.IsAssignableFrom<Panel>(nav.Parent).Children.Remove(nav);
                nav.Width = 280;
                nav.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
                // Do not warm shared icon geometry on this separate raster-rendering UI thread.
                pill.Child = null;
                pill.Width = 200;
                pill.Height = 40;
                pill.Opacity = 0;

                var surface = new Panel();
                for (var x = 0; x < 560; x += 8)
                {
                    var shade = (byte)(x % 16 == 0 ? 64 : 192);
                    surface.Children.Add(new Border
                    {
                        Width = 8,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                        Margin = new Thickness(x, 0, 0, 0),
                        Background = new SolidColorBrush(Color.FromArgb(128, shade, shade, shade)),
                    });
                }
                surface.Children.Add(nav);
                window.Content = surface;
                surface.Measure(new Size(560, 96));
                surface.Arrange(new Rect(0, 0, 560, 96));

                using var rendered = new RenderTargetBitmap(new PixelSize(560, 96));
                rendered.Render(surface);
                using var stream = new MemoryStream();
                rendered.Save(stream, PngBitmapEncoderOptions.Default);
                stream.Position = 0;
                using var pixels = SkiaSharp.SKBitmap.Decode(stream);
                Assert.NotNull(pixels);

                var origin = pill.TranslatePoint(default, surface)!.Value;
                var centreX = (int)(origin.X + pill.Bounds.Width / 2);
                var centreY = (int)(origin.Y + pill.Bounds.Height / 2);
                var clearPoints = new[]
                {
                    new PixelPoint(2, centreY),
                    new PixelPoint(277, centreY),
                    new PixelPoint(283, centreY),
                    new PixelPoint(centreX, (int)origin.Y - 8),
                    new PixelPoint(centreX, 94),
                    new PixelPoint((int)origin.X + 1, (int)origin.Y + 1),
                };
                foreach (var point in clearPoints)
                {
                    var expected = pixels.GetPixel(point.X, 10);
                    Assert.InRange(expected.Alpha, 1, 254);
                    Assert.Equal(expected, pixels.GetPixel(point.X, point.Y));
                }
                Assert.NotEqual(pixels.GetPixel(centreX, 10), pixels.GetPixel(centreX, centreY));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Bar_FrostsChatsScrollingUnderneath_WhileTheLastChatStaysReachable()
    {
        using var session = HeadlessTestSession.Start();
        var skillsDirectory = Path.Combine(Path.GetTempPath(), $"lumi-nav-glass-{Guid.NewGuid():N}");

        try
        {
            await session.Dispatch(async () =>
            {
                var emptyProject = new Project { Name = "Empty project" };
                using var vm = CreateMainViewModel(new AppData
                {
                    Projects = [emptyProject],
                    Chats = Enumerable.Range(0, 30).Select(index => new Chat { Title = $"Chat {index:D2}" }).ToList()
                }, skillsDirectory);
                vm.SettingsVM.ShowAnimations = false;
                var window = new MainWindow { DataContext = vm, Width = 1280, Height = 740 };
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

                    double Top(Control control) => control.TranslatePoint(default, window)!.Value.Y;
                    double Bottom(Control control) => Top(control) + control.Bounds.Height;

                    await Settle();
                    var sidebar = Named<Border>("SidebarBorder");
                    var nav = Named<Panel>("SidebarNav");
                    var material = Named<Panel>("NavPillMaterial");
                    var pill = Named<Border>("NavPill");
                    var scroll = Named<ScrollViewer>("ChatListScroller");
                    var spacer = Named<Border>("SidebarNavSpacer");
                    var frost = Assert.Single(nav.GetVisualDescendants().OfType<StrataBackdropBlur>());

                    Assert.Equal(sidebar.Bounds.Width, nav.Bounds.Width, 1);
                    Assert.Equal(Bottom(sidebar), Bottom(nav), 1);
                    Assert.Equal(Bottom(sidebar), Bottom(scroll), 1);
                    Assert.True(Bottom(scroll) > Top(nav));
                    Assert.False(spacer.IsVisible);
                    Assert.Equal(new CornerRadius(20), pill.CornerRadius);
                    Assert.Equal(new Thickness(1), pill.BorderThickness);
                    Assert.NotEqual(default, pill.BoxShadow);
                    var pillFill = Assert.IsAssignableFrom<ISolidColorBrush>(pill.Background);
                    Assert.Equal(0.4, pillFill.Opacity);
                    Assert.True(pillFill.Color.A > 0);
                    Assert.Null(nav.Background);
                    Assert.Same(material, frost.Parent);
                    Assert.False(frost.IsHitTestVisible);
                    Assert.Equal(pill.Bounds.Size, frost.Bounds.Size);
                    Assert.Equal(pill.CornerRadius, frost.CornerRadius);
                    Assert.Equal(Top(pill), Top(frost), 1);
                    Assert.Equal(StrataBackdropFade.None, frost.Fade);
                    Assert.True(frost.SamplesBeyondEdges);
                    Assert.Equal(10, frost.BlurRadius);

                    var lastChat = vm.ChatGroups.Last().Chats.Last();
                    var chatList = Named<ItemsControl>("ChatGroupsHost")
                        .GetVisualDescendants().OfType<ListBox>().Single(list => list.Items.Contains(lastChat));
                    ListBoxItem LastRow() => chatList.GetVisualDescendants().OfType<ListBoxItem>()
                        .Single(row => ReferenceEquals(row.DataContext, lastChat));

                    scroll.ScrollToEnd();
                    await Settle();
                    Assert.True(scroll.Offset.Y > 0);
                    Assert.True(Bottom(LastRow()) <= Top(nav), "The last chat scrolls fully clear of the navigation");

                    scroll.Offset = new Vector(0, Math.Max(0, scroll.ScrollBarMaximum.Y - nav.Bounds.Height));
                    await Settle();
                    Assert.True(Bottom(LastRow()) > Top(nav), "Chats really occupy the space behind the navigation");

                    var jobs = Named<Button>("NavJobs");
                    var point = jobs.TranslatePoint(new Point(jobs.Bounds.Width / 2, jobs.Bounds.Height / 2), window)!.Value;
                    window.MouseDown(point, MouseButton.Left);
                    window.MouseUp(point, MouseButton.Left);
                    await Settle();
                    Assert.Equal(1, vm.SelectedNavIndex);
                    Assert.True(spacer.IsVisible);
                    Assert.Equal(nav.Bounds.Height, spacer.Bounds.Height, 1);

                    new ButtonAutomationPeer(Named<Button>("NavSettings")).Invoke();
                    await Settle();
                    Assert.True(Bottom(Named<ListBox>("SettingsPagesList")) <= Top(nav) + 1,
                        "Other sidebar pages keep their content above the navigation");

                    new ButtonAutomationPeer(Named<Button>("NavChat")).Invoke();
                    await Settle();
                    scroll.Offset = default;
                    await Settle();
                    chatList.SelectedItem = lastChat;
                    await Settle();
                    await Settle();
                    Assert.True(Bottom(LastRow()) <= Top(nav),
                        "Selecting an offscreen chat brings its row above the floating navigation");

                    window.Height = 560;
                    sidebar.Width = 240;
                    await Settle();
                    scroll.ScrollToEnd();
                    await Settle();
                    Assert.Equal(sidebar.Bounds.Width, nav.Bounds.Width, 1);
                    Assert.Equal(Bottom(sidebar), Bottom(scroll), 1);
                    Assert.True(Bottom(LastRow()) <= Top(nav));

                    vm.SelectProjectFilterCommand.Execute(emptyProject);
                    await Settle();
                    Assert.Empty(vm.ChatGroups);
                    Assert.True(nav.IsEffectivelyVisible);
                    Assert.Equal(Bottom(sidebar), Bottom(nav), 1);
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(skillsDirectory))
                Directory.Delete(skillsDirectory, recursive: true);
        }
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
                    foreach (var row in new[] { "CustomizeDiscoverTab", "CustomizeLumisTab", "CustomizeSkillsTab", "CustomizeMcpTab", "CustomizeProjectsTab" })
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

                    // Hover must animate the shared glass, not leave the frost behind the scaled pill.
                    var pill = window.FindControl<Border>("NavPill")!;
                    var material = window.FindControl<Panel>("NavPillMaterial")!;
                    var materialVisual = ElementComposition.GetElementVisual(material);
                    var pillVisual = ElementComposition.GetElementVisual(pill);
                    Assert.NotNull(materialVisual);
                    Assert.NotNull(pillVisual);
                    var point = pill.TranslatePoint(new Point(pill.Bounds.Width / 2, pill.Bounds.Height / 2), window)!.Value;
                    window.MouseMove(point);
                    Assert.NotEqual(default, materialVisual.CenterPoint);
                    Assert.Equal(default, pillVisual.CenterPoint);
                    window.MouseMove(point - new Vector(0, 300));

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

                    void AssertSwitcherCountsAligned()
                    {
                        var counts = Named<StackPanel>("ProjectFilterResults").GetVisualDescendants()
                            .OfType<TextBlock>()
                            .Where(text => text.Classes.Contains("project-switcher-row-count"))
                            .ToArray();
                        Assert.Equal(2, counts.Length);
                        var right = counts[0].TranslatePoint(new Point(counts[0].Bounds.Width, 0), window)!.Value.X;
                        Assert.All(counts, count =>
                        {
                            Assert.True(count.Bounds.Width > 0);
                            Assert.Equal(right,
                                count.TranslatePoint(new Point(count.Bounds.Width, 0), window)!.Value.X, 1);
                            var content = Assert.IsType<DockPanel>(count.GetVisualParent());
                            Assert.Equal(content.Bounds.Width, count.Bounds.Right, 1);
                            var edit = content.Children.SingleOrDefault(child =>
                                child.Classes.Contains("project-switcher-row-edit"));
                            if (edit is not null)
                                Assert.True(edit.Bounds.Right <= count.Bounds.Left);
                        });
                    }

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

                    // The switcher is always in the chat list. It only switches: its drawer's actions open
                    // Customize → Projects, and the drawer closes behind the page it opens. At rest there
                    // is nothing beneath it to frost.
                    var switcher = Named<Panel>("ProjectSwitcherRoot");
                    var surface = switcher.Children.OfType<Border>().Single();
                    var frost = switcher.Children.OfType<StrataBackdropBlur>().Single();
                    Assert.Equal(0, frost.BlurRadius);
                    Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button => button.Name == "ProjectEditButton");
                    await Click("ProjectSwitchButton");
                    Assert.Contains("open", switcher.Classes);
                    new ButtonAutomationPeer(Named<Button>("ProjectSwitcherManageButton")).Invoke();
                    await Settle();
                    Assert.Equal(2, vm.SelectedNavIndex);
                    Assert.DoesNotContain("open", switcher.Classes);
                    Assert.True(Named<Panel>("ProjectsOverview").IsVisible);
                    Assert.True(Named<Border>("CustomizeSwitcher").IsVisible);
                    Assert.Contains("selected", Named<Button>("CustomizeProjectsTab").Classes);
                    AssertLit("NavCustomize");

                    // Narrowed to a project, the switcher still spans the list and shows the project's
                    // initial; the open project's gear waits in the drawer instead of beside it.
                    await Click("NavChat");
                    vm.SelectProjectFilterCommand.Execute(project);
                    await Settle();
                    Assert.Equal(project.Name, Named<TextBlock>("ProjectSwitchTitleText").Text);
                    Assert.True(Named<TextBlock>("ProjectSwitchInitial").IsVisible);
                    Assert.Equal(LaunchpadProjectBadge.InitialOf(project.Name), Named<TextBlock>("ProjectSwitchInitial").Text);
                    Assert.False(Named<TextBlock>("ProjectSwitchMetaText").IsVisible);
                    Assert.Equal(surface.Bounds.Width - surface.BorderThickness.Left - surface.BorderThickness.Right,
                        Named<Button>("ProjectSwitchButton").Bounds.Width, 1);
                    await Click("ProjectSwitchButton");
                    AssertSwitcherCountsAligned();
                    new ButtonAutomationPeer(Named<Button>("ProjectEditButton")).Invoke();
                    await Settle();
                    Assert.Equal(2, vm.SelectedNavIndex);
                    Assert.Same(project, vm.ProjectsVM.SelectedProject);
                    Assert.True(Named<Panel>("ProjectDetail").IsVisible);
                    Assert.DoesNotContain("open", switcher.Classes);
                    AssertLit("NavCustomize");

                    // Showing every chat, the switcher says how many projects it holds.
                    await Click("NavChat");
                    vm.ClearProjectFilterCommand.Execute(null);
                    await Settle();
                    Assert.True(Named<PathIcon>("ProjectSwitchAllIcon").IsVisible);
                    Assert.True(Named<TextBlock>("ProjectSwitchMetaText").IsVisible);
                    Assert.Equal(Loc.ProjectSwitcher_CountOne, Named<TextBlock>("ProjectSwitchMetaText").Text);

                    // Open, the switcher grows out of itself over the chat list, which stays where it was:
                    // its parts flow out of the header one after another, and it turns to frosted glass.
                    vm.ProjectsVM.CloseDetailCommand.Execute(null);
                    var chatList = Named<ScrollViewer>("ChatListScroller");
                    var chatListTop = chatList.TranslatePoint(default, window)!.Value.Y;
                    await Click("ProjectSwitchButton");
                    var parts = Named<StackPanel>("ProjectSwitchPanel").Children.OfType<Border>()
                        .Concat(Named<StackPanel>("ProjectFilterResults").Children).ToList();
                    Assert.All(parts, part => Assert.True(SlideFadeEntrance.IsCompositedRunning(part)));
                    var revealHost = Named<Border>("ProjectSwitchRevealHost");
                    for (var frame = 0; frame < 30 && !double.IsNaN(revealHost.Height); frame++)
                    {
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        await Task.Delay(20);
                        await Settle();
                    }
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Assert.True(double.IsNaN(revealHost.Height), "The drawer finishes opening");
                    Assert.Equal(chatListTop, chatList.TranslatePoint(default, window)!.Value.Y, 1);
                    Assert.True(switcher.TranslatePoint(new Point(0, switcher.Bounds.Height), window)!.Value.Y > chatListTop,
                        "The open switcher lies over the chat list instead of pushing it down");
                    Assert.True(frost.BlurRadius > 0, "Open, the switcher frosts the chats beneath it");
                    Assert.InRange(Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background).Opacity, 0.5, 0.9);
                    AssertSwitcherCountsAligned();

                    // Every project in it has a gear that sets it up without switching to it. Like the
                    // gear's appearance, it waits for the row's first hover.
                    Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(),
                        button => button.Classes.Contains("project-switcher-row-edit"));
                    var row = window.GetVisualDescendants().OfType<Button>().Single(button =>
                        button.Classes.Contains("project-switcher-row")
                        && button.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == project.Name));
                    window.MouseMove(row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value);
                    await Settle();
                    AssertSwitcherCountsAligned();
                    var gear = row.GetVisualDescendants().OfType<Button>()
                        .Single(button => button.Classes.Contains("project-switcher-row-edit"));
                    new ButtonAutomationPeer(gear).Invoke();
                    await Settle();
                    Assert.Equal(2, vm.SelectedNavIndex);
                    Assert.Same(project, vm.ProjectsVM.SelectedProject);
                    Assert.Null(vm.SelectedProjectFilter);
                    Assert.DoesNotContain("open", switcher.Classes);
                    AssertLit("NavCustomize");

                    // With animations off, the list simply opens.
                    vm.SettingsVM.ShowAnimations = false;
                    await Click("NavChat");
                    await Click("ProjectSwitchButton");
                    Assert.Contains("open", switcher.Classes);
                    Assert.DoesNotContain(Named<StackPanel>("ProjectFilterResults").Children, SlideFadeEntrance.IsCompositedRunning);
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
