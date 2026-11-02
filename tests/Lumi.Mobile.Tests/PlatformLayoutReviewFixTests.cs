using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.Layout;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class PlatformLayoutReviewFixTests
{
    [Fact]
    public async Task NativeKeyboardReportsDoNotTakeOwnershipOfSafeAreaInsets()
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;

        await session.Dispatch(async () =>
        {
            MobileShellViewModel? shell = null;
            Window? window = null;
            try
            {
                shell = new MobileShellViewModel(store: session.NewStore(), post: action => action())
                {
                    HostName = "Test PC",
                    IsPaired = true
                };
                var view = new MobileShellView { DataContext = shell };
                window = new Window { Width = 390, Height = 844, Content = view };
                window.Show();
                Pump(window);

                view.ApplyNativeInsets(new Thickness(0, 48, 0, 24));
                view.ApplyPlatformKeyboardInset(300);
                Assert.True(shell.IsKeyboardOpen);
                Assert.Equal(300, shell.SafeAreaBottom.Bottom);
                Assert.Equal(300, shell.ComposerSafeAreaBottom.Bottom);

                var updated = new Thickness(44, 18, 20, 21);
                view.ApplyNativeInsets(updated);
                Pump(window);
                Assert.Equal(new Thickness(44, 0, 20, 0), shell.SafeAreaSides);
                Assert.Equal(18, shell.SafeAreaTop.Top);
                Assert.Equal(300, shell.SafeAreaBottom.Bottom);

                view.ApplyPlatformKeyboardInset(0);
                Pump(window);
                Assert.False(shell.IsKeyboardOpen);
                Assert.Equal(updated, shell.SafeArea);
                Assert.Equal(updated.Bottom, shell.ComposerSafeAreaBottom.Bottom);

                view.NotifyApplicationDeactivated();
                view.ApplyPlatformKeyboardInset(300);
                Assert.False(shell.IsKeyboardOpen);
                Assert.Equal(updated, shell.SafeArea);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window?.Close();
                if (shell is not null)
                    await shell.DisposeAsync();
            }
        }, CancellationToken.None);

        failure?.Throw();
    }

    [Theory]
    [InlineData(0, 24, 0, 0, false)]
    [InlineData(0, 24, 0, 8, false)]
    [InlineData(0, 59, 0, 34, false)]
    [InlineData(59, 0, 59, 21, false)]
    [InlineData(0, 59, 0, 34, true)]
    public async Task BrowserHostInsetsRemainAuthoritativeAfterNativeSafeAreaRefresh(
        double left, double top, double right, double bottom, bool hosted)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;

        await session.Dispatch(async () =>
        {
            MobileShellViewModel? shell = null;
            Window? window = null;
            try
            {
                shell = new MobileShellViewModel(store: session.NewStore(), post: action => action())
                {
                    HostName = "Test PC",
                    IsPaired = true
                };
                var view = new MobileShellView { DataContext = shell };
                window = new Window { Width = 390, Height = 844, Content = view };
                window.Show();
                Pump(window);

                var correct = new Thickness(left, top, right, bottom);
                var swapped = new Thickness(left, top, bottom, right);
                view.ApplyNativeInsets(swapped);
                Assert.Equal(swapped, shell.SafeArea);

                if (hosted)
                {
                    view.Height = 844;
                    window.Height = 1000;
                    Pump(window);
                }

                view.ApplyPlatformInsets(correct, viewportHeight: 844);
                view.ApplyNativeInsets(swapped);
                Pump(window);

                Assert.Equal(correct, shell.SafeArea);
                Assert.Equal(new Thickness(left, 0, right, 0), shell.SafeAreaSides);
                Assert.Equal(bottom, shell.SafeAreaBottom.Bottom);
                Assert.False(shell.IsKeyboardOpen);
                Assert.Equal(844, shell.UsableContentHeight);
                var chat = Required<ChatDetailView>(view, "ChatSurface");
                var composer = Required<StrataChatComposer>(chat, "Composer");
                var composerInset = Required<Border>(chat, "ComposerInset");
                var island = Required<ContentControl>(chat, "ChatIsland");
                Assert.Equal(view.Bounds.Size, chat.Bounds.Size);
                // Alone on a phone the chat is the whole pane: its padding carries the composer's clearance
                // and its content adds none of its own.
                Assert.Equal(default, island.Margin);
                Assert.Equal(shell.ChatIslandPadding, island.Padding);
                Assert.Equal(shell.ComposerSafeAreaBottom.Bottom, island.Padding.Bottom);
                Assert.Equal(default, composerInset.Padding);
                Assert.Equal(12, ComposerGap(), 1);
                foreach (var name in new[] { "ComposerAttachButton", "PART_SendButton" })
                {
                    var button = composer.GetVisualDescendants().OfType<Button>()
                        .Single(button => button.Name == name);
                    Assert.True(button.IsEffectivelyVisible);
                    Assert.True(button.Bounds.Width >= 48 && button.Bounds.Height >= 48);
                    var buttonBottom = button.TranslatePoint(
                        new Point(0, button.Bounds.Height), chat)!.Value.Y;
                    Assert.True(buttonBottom <= chat.Bounds.Height - 12);
                    var surface = button.GetVisualDescendants().OfType<Border>()
                        .Single(border => border.Name == "PART_Surface");
                    var surfaceBottom = surface.TranslatePoint(
                        new Point(0, surface.Bounds.Height), chat)!.Value.Y;
                    Assert.True(surfaceBottom <= chat.Bounds.Height - 16);
                }

                view.ApplyPlatformInsets(correct, keyboardInset: 300, viewportHeight: 844);
                view.ApplyNativeInsets(swapped);
                Pump(window);
                Assert.True(shell.IsKeyboardOpen);
                Assert.Equal(300, shell.SafeAreaBottom.Bottom);
                Assert.Equal(right, shell.SafeAreaSides.Right);
                Assert.Equal(312, ComposerGap(), 1);

                view.ApplyPlatformInsets(correct, viewportHeight: 844);
                view.ApplyNativeInsets(swapped);
                Pump(window);
                Assert.False(shell.IsKeyboardOpen);
                Assert.Equal(correct, shell.SafeArea);
                Assert.Equal(12, ComposerGap(), 1);

                double ComposerGap() => chat.Bounds.Height - composer.TranslatePoint(
                    new Point(0, composer.Bounds.Height), chat)!.Value.Y;
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window?.Close();
                if (shell is not null)
                    await shell.DisposeAsync();
            }
        }, CancellationToken.None);

        failure?.Throw();
    }

    [Theory]
    [InlineData(810, 844, true)]
    [InlineData(810, 844, false)]
    [InlineData(844, 776, true)]
    [InlineData(844, 776, false)]
    public async Task BrowserViewportInsets_UseMeasuredHostHeightRegardlessOfResizeOrder(
        double previousHeight,
        double viewportHeight,
        bool publishBeforeResize)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;

        await session.Dispatch(async () =>
        {
            MobileShellViewModel? shell = null;
            Window? window = null;

            try
            {
                shell = new MobileShellViewModel(store: session.NewStore(), post: action => action())
                {
                    HostName = "Test PC",
                    IsPaired = true
                };
                var shellView = new MobileShellView { DataContext = shell };
                window = new Window
                {
                    Width = 390,
                    Height = previousHeight,
                    Content = shellView
                };
                window.Show();
                Pump(window);

                var safeArea = new Thickness(0, 47, 0, 34);
                const double keyboardInset = 300;
                shellView.ApplyPlatformInsets(safeArea);

                if (!publishBeforeResize)
                {
                    window.Height = viewportHeight;
                    Pump(window);
                }

                // The DOM measurement and visualViewport event can precede Avalonia's
                // ResizeObserver. Both values belong to the new host, not the old layout.
                shellView.ApplyPlatformInsets(safeArea, keyboardInset, viewportHeight);

                if (publishBeforeResize)
                    window.Height = viewportHeight;
                Pump(window);

                var chat = Required<ChatDetailView>(shellView, "ChatSurface");
                var composerInset = Required<Border>(chat, "ComposerInset");
                var composerContent = Required<Border>(chat, "ComposerSideInset");
                var island = Required<ContentControl>(chat, "ChatIsland");
                AssertEdgeToEdge(chat, window);
                Assert.Equal(default, shellView.Padding);
                Assert.Equal(viewportHeight, shell.UsableContentHeight, 1);
                Assert.True(shell.IsKeyboardOpen);
                Assert.Equal(keyboardInset, shell.SafeAreaBottom.Bottom, 1);
                // Alone on a phone the chat's padding keeps the composer above the keyboard; the composer
                // needs no inset of its own.
                Assert.Equal(default, island.Margin);
                Assert.Equal(keyboardInset, island.Padding.Bottom, 1);
                Assert.Equal(default, composerInset.Padding);

                var composerBottom = composerContent.TranslatePoint(
                    new Point(0, composerContent.Bounds.Height), chat)!.Value.Y;
                // The composer keeps its own 12 dp above the keyboard, as it does above the screen's foot.
                Assert.Equal(chat.Bounds.Height - keyboardInset - 12, composerBottom, 1);

                // Closing the keyboard returns the composer to the screen's foot without shortening the
                // canvas, including after Safari/standalone height changes: the home indicator passes over
                // the composer's own gap.
                shellView.ApplyPlatformInsets(safeArea, viewportHeight: viewportHeight);
                Pump(window);
                Assert.False(shell.IsKeyboardOpen);
                Assert.Equal(0, island.Padding.Bottom, 1);
                AssertEdgeToEdge(chat, window);

                shellView.ApplyPlatformInsets(safeArea, keyboardInset, viewportHeight);
                shellView.NotifyApplicationDeactivated();
                Pump(window);
                Assert.False(shell.IsKeyboardOpen);
                Assert.Equal(0, island.Padding.Bottom, 1);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window?.Close();
                if (shell is not null)
                    await shell.DisposeAsync();
            }
        }, CancellationToken.None);

        failure?.Throw();
    }

    [Fact]
    public async Task OverlayPages_ConsumeSafeAreaWithoutLetterboxingTheirSurfaces()
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;

        await session.Dispatch(async () =>
        {
            MobileShellViewModel? shell = null;
            Window? window = null;

            try
            {
                shell = new MobileShellViewModel(store: session.NewStore(), post: action => action())
                {
                    HostName = "Test PC",
                    IsPaired = true
                };

                var shellView = new MobileShellView { DataContext = shell };
                window = new Window
                {
                    Width = 412,
                    Height = 892,
                    Content = shellView
                };
                window.Show();
                Pump(window);

                const double topInset = 48;
                const double bottomInset = 24;
                var safeArea = new Thickness(0, topInset, 0, bottomInset);

                shellView.ApplyPlatformInsets(safeArea);
                Pump(window);

                Assert.Equal(default, shellView.Padding);
                Assert.Equal(new Thickness(0, topInset, 0, 0), shell.SafeAreaTop);
                Assert.Equal(new Thickness(0, 0, 0, bottomInset), shell.SafeAreaBottom);

                shell.Page = MobilePage.Search;
                Pump(window);

                var search = Required<MobileSearchView>(shellView, "SearchPage");
                AssertEdgeToEdge(search, window);
                // A phone's lists start right under the status bar: no island rests beside them.
                Assert.Equal(shell.SafeAreaTop, shell.CanvasHeaderInset);
                Assert.Equal(shell.CanvasHeaderInset, Required<Border>(search, "SearchTopInset").Padding);
                Assert.Equal(shell.SafeAreaBottom, Required<Border>(search, "SearchBottomInset").Padding);

                shell.Page = MobilePage.Settings;
                Pump(window);

                var settings = Required<MobileSettingsView>(shellView, "SettingsPage");
                AssertEdgeToEdge(settings, window);
                Assert.Equal(shell.CanvasHeaderInset, Required<Border>(settings, "SettingsTopInset").Padding);
                Assert.Equal(shell.SafeAreaBottom, Required<Border>(settings, "SettingsBottomInset").Padding);

                shell.Page = MobilePage.Library;
                Pump(window);

                var library = Required<LibraryView>(shellView, "LibraryPage");
                AssertEdgeToEdge(library, window);
                Assert.Equal(shell.CanvasHeaderInset, Required<Border>(library, "LibraryTopInset").Padding);
                Assert.Equal(shell.SafeAreaBottom, Required<Border>(library, "LibraryListBottomInset").Padding);

                shell.Library.BeginCreateCommand.Execute(null);
                Pump(window);

                // Alone on a phone the open item is the whole pane, kept clear of the bars by its padding;
                // its editor adds no inset.
                var detail = Required<ContentControl>(library, "LibraryDetailIsland");
                Assert.Equal(default, detail.Margin);
                Assert.Equal(shell.PageIslandPadding, detail.Padding);
                Assert.Equal(topInset, detail.Padding.Top);
                Assert.Equal(default, Required<Border>(library, "LibraryEditorBottomInset").Padding);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window?.Close();
                if (shell is not null)
                    await shell.DisposeAsync();
            }
        }, CancellationToken.None);

        failure?.Throw();
    }

    [Fact]
    public async Task LandscapeCutout_InsetsForegroundsWithoutShrinkingSurfacesOrHingePanes()
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;

        await session.Dispatch(async () =>
        {
            MobileShellViewModel? shell = null;
            Window? window = null;

            try
            {
                shell = new MobileShellViewModel(store: session.NewStore(), post: action => action());
                var shellView = new MobileShellView { DataContext = shell };
                window = new Window
                {
                    Width = 948,
                    Height = 600,
                    Content = shellView
                };
                window.Show();
                Pump(window);

                var safeArea = new Thickness(44, 18, 20, 12);
                var sideInsets = new Thickness(44, 0, 20, 0);
                shellView.ApplyPlatformInsets(safeArea);
                Pump(window);

                Assert.Equal(sideInsets, shell.SafeAreaSides);
                Assert.Equal(default, shellView.Padding);

                var connect = Required<ConnectView>(shellView, "ConnectPage");
                AssertEdgeToEdge(connect, window);
                Assert.Equal(shell.SafeAreaTop, Required<Border>(connect, "ConnectTopInset").Padding);
                Assert.Equal(shell.SafeAreaBottom, Required<Border>(connect, "ConnectBottomInset").Padding);
                AssertRenderedSideInset(Required<Border>(connect, "ConnectSideInset"), sideInsets);

                shell.HostName = "Test PC";
                shell.IsPaired = true;
                shell.IsSidebarCollapsed = false;
                shellView.Posture = FoldPosture.BookVerticalHinge;
                shellView.HingeSize = 24;
                shellView.HingePosition = 430;
                Pump(window);

                Assert.True(
                    shell.IsDrawerDocked,
                    $"Layout={shell.Layout.Width}/{shell.Layout.WidthClass}, " +
                    $"collapsed={shell.IsSidebarCollapsed}, shell={shellView.Bounds.Width}, " +
                    $"client={window.ClientSize.Width}");
                Assert.Equal(430, shell.DrawerWidth, 1);
                Assert.Equal(24, shell.HingeGapWidth, 1);

                var dockedDrawer = Required<Border>(shellView, "DrawerPane");
                var hingeGap = Required<Border>(shellView, "HingeGap");
                var chat = Required<ChatDetailView>(shellView, "ChatSurface");
                Assert.Equal(430, dockedDrawer.Bounds.Width, 1);
                Assert.Equal(24, hingeGap.Bounds.Width, 1);
                Assert.Equal(window.ClientSize.Width - 430 - 24, chat.Bounds.Width, 1);

                var drawer = Required<MobileDrawerView>(shellView, "DrawerContent");
                AssertRenderedSideInset(Required<Border>(drawer, "DrawerHeaderSideInset"), sideInsets);
                AssertRenderedSideInset(Required<Border>(drawer, "DrawerContentSideInset"), sideInsets);
                AssertRenderedSideInset(Required<Border>(drawer, "DrawerAccountSideInset"), sideInsets);

                // The chat's island keeps clear of the trailing cutout and the bars; the list beside it
                // takes the leading one. Nothing inside the island insets itself again.
                var gap = MobileShellViewModel.IslandGap;
                var chatIsland = Required<ContentControl>(chat, "ChatIsland");
                Assert.Equal(shell.ChatIslandMargin, chatIsland.Margin);
                Assert.Equal(gap, chatIsland.Margin.Left);
                Assert.Equal(safeArea.Top + gap, chatIsland.Margin.Top);
                Assert.Equal(safeArea.Right + gap, chatIsland.Margin.Right);
                Assert.Equal(shell.ComposerSafeAreaBottom.Bottom + gap, chatIsland.Margin.Bottom);
                foreach (var name in new[] { "ChatHeaderSideInset", "ChatTranscriptSideInset", "ComposerSideInset", "WelcomeSideInset" })
                    AssertRenderedSideInset(Required<Border>(chat, name), default);
                Assert.Equal(default, Required<Border>(chat, "TopBarInset").Padding);
                Assert.Equal(default, Required<Border>(chat, "ComposerInset").Padding);

                var runSettings = Required<StrataBottomSheet>(shellView, "RunSettingsSheet");
                shell.Chat.IsRunSettingsSheetOpen = true;
                Pump(window);

                Assert.Equal(shell.SafeAreaSheetMargin, runSettings.SheetMargin);
                Assert.Equal(
                    default,
                    runSettings.GetVisualDescendants()
                        .OfType<TextBlock>()
                        .Single(control => control.Name == "PART_Title")
                        .Margin);
                Assert.Equal(
                    new Thickness(24, 0, 12, 12),
                    runSettings.GetVisualDescendants()
                        .OfType<Grid>()
                        .Single(control => control.Name == "PART_HeaderContent")
                        .Margin);
                var runSettingsContent = Required<Border>(shellView, "RunSettingsSideInset");
                Assert.Equal(
                    new Thickness(0, 0, 0, 10),
                    runSettingsContent.GetVisualAncestors()
                        .OfType<Control>()
                        .First(control => control.Name == "PART_ContentPresenter")
                        .Margin);
                AssertRenderedSideInset(runSettingsContent, default);

                shell.Chat.IsRunSettingsSheetOpen = false;
                Pump(window);

                shell.Page = MobilePage.Search;
                Pump(window);

                var search = Required<MobileSearchView>(shellView, "SearchPage");
                AssertSameRenderedBounds(search, chat);
                AssertRenderedSideInset(Required<Border>(search, "SearchHeaderSideInset"), sideInsets);
                AssertRenderedSideInset(Required<Border>(search, "SearchResultsSideInset"), sideInsets);

                shell.Page = MobilePage.Settings;
                Pump(window);

                var settings = Required<MobileSettingsView>(shellView, "SettingsPage");
                AssertSameRenderedBounds(settings, chat);
                AssertRenderedSideInset(Required<Border>(settings, "SettingsHeaderSideInset"), sideInsets);
                AssertRenderedSideInset(Required<Border>(settings, "SettingsContentSideInset"), sideInsets);

                shell.Page = MobilePage.Library;
                Pump(window);

                var library = Required<LibraryView>(shellView, "LibraryPage");
                AssertSameRenderedBounds(library, chat);
                AssertRenderedSideInset(Required<Border>(library, "LibraryHeaderSideInset"), sideInsets);
                AssertRenderedSideInset(Required<Border>(library, "LibraryListSideInset"), sideInsets);
                // Customize's sections and search scroll with the list, inside its side inset.
                Assert.True(Required<Border>(library, "LibrarySectionsSideInset").TranslatePoint(default, library)!.Value.X
                            >= sideInsets.Left);
                Assert.True(Required<Border>(library, "LibrarySearchSideInset").TranslatePoint(default, library)!.Value.X
                            >= sideInsets.Left);

                var libraryActions = Required<StrataBottomSheet>(library, "LibraryActionsSheet");
                shell.Library.IsRowActionsOpen = true;
                Pump(window);

                AssertSameRenderedBounds(libraryActions, library);
                Assert.Equal(shell.SafeAreaSheetMargin, libraryActions.SheetMargin);
                var libraryActionsContent = Required<Border>(library, "LibraryActionsSideInset");
                Assert.Equal(
                    new Thickness(0, 0, 0, 10),
                    libraryActionsContent.GetVisualAncestors()
                        .OfType<Control>()
                        .First(control => control.Name == "PART_ContentPresenter")
                        .Margin);
                AssertRenderedSideInset(libraryActionsContent, default);

                shell.Library.IsRowActionsOpen = false;
                Pump(window);

                shell.Library.BeginCreateCommand.Execute(null);
                Pump(window);

                // Beside the hinge-held navigation the open item's island takes only the trailing cutout.
                var detailIsland = Required<ContentControl>(library, "LibraryDetailIsland");
                Assert.Equal(shell.PageIslandMargin, detailIsland.Margin);
                Assert.Equal(gap, detailIsland.Margin.Left);
                Assert.Equal(safeArea.Top + gap, detailIsland.Margin.Top);
                Assert.Equal(safeArea.Right + gap, detailIsland.Margin.Right);
                AssertRenderedSideInset(Required<Border>(library, "LibraryEditorSideInset"), default);

                // Foreground padding must not alter the physical split even after every overlay has
                // been measured with asymmetric cutout values.
                Assert.Equal(430, dockedDrawer.Bounds.Width, 1);
                Assert.Equal(24, hingeGap.Bounds.Width, 1);
                Assert.Equal(window.ClientSize.Width - 430 - 24, chat.Bounds.Width, 1);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window?.Close();
                if (shell is not null)
                    await shell.DisposeAsync();
            }
        }, CancellationToken.None);

        failure?.Throw();
    }

    [Fact]
    public void ClearedFoldingFeature_DropsStaleHingeGeometry()
    {
        var layout = MobileLayoutState.From(
            884,
            908,
            FoldPosture.Flat,
            hingeSize: 24,
            hingePosition: 430);

        Assert.Equal(0, layout.HingeSize);
        Assert.Equal(0, layout.HingePosition);
    }

    private static T Required<T>(Control root, string name)
        where T : Control =>
        root.FindControl<T>(name)
        ?? throw new InvalidOperationException($"{typeof(T).Name} #{name} was not found.");

    private static void AssertEdgeToEdge(UserControl overlay, Window window)
    {
        var origin = overlay.TranslatePoint(default, window)
            ?? throw new InvalidOperationException("Overlay is not attached to the test window.");

        Assert.Equal(default, overlay.Padding);
        Assert.Equal(0, origin.X, 1);
        Assert.Equal(0, origin.Y, 1);
        Assert.Equal(window.ClientSize.Width, overlay.Bounds.Width, 1);
        Assert.Equal(window.ClientSize.Height, overlay.Bounds.Height, 1);
    }

    private static void AssertSameRenderedBounds(Control actual, Control expected)
    {
        var origin = actual.TranslatePoint(default, expected)
            ?? throw new InvalidOperationException("Controls are not attached to the same visual tree.");

        Assert.Equal(0, origin.X, 1);
        Assert.Equal(0, origin.Y, 1);
        Assert.Equal(expected.Bounds.Width, actual.Bounds.Width, 1);
        Assert.Equal(expected.Bounds.Height, actual.Bounds.Height, 1);
    }

    private static void AssertRenderedSideInset(Border inset, Thickness expected)
    {
        Assert.Equal(expected, inset.Padding);
        Assert.True(
            inset.Bounds.Width > expected.Left + expected.Right,
            $"{inset.Name} was too narrow to render both side insets.");

        var child = inset.Child
            ?? throw new InvalidOperationException($"{inset.Name} does not have foreground content.");
        var origin = child.TranslatePoint(default, inset)
            ?? throw new InvalidOperationException($"{inset.Name}'s content is not attached.");

        Assert.True(
            origin.X >= expected.Left - 1,
            $"{inset.Name}'s content started at {origin.X}, before the {expected.Left}dp left inset.");
        Assert.True(
            origin.X + child.Bounds.Width <= inset.Bounds.Width - expected.Right + 1,
            $"{inset.Name}'s content ended at {origin.X + child.Bounds.Width}, after the right safe edge.");
    }

    private static void Pump(Window window)
    {
        window.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();
    }
}
