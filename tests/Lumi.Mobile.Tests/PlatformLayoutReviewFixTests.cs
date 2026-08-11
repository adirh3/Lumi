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
                Assert.Equal(shell.SafeAreaTop, Required<Border>(search, "SearchTopInset").Padding);
                Assert.Equal(shell.SafeAreaBottom, Required<Border>(search, "SearchBottomInset").Padding);

                shell.Page = MobilePage.Settings;
                Pump(window);

                var settings = Required<MobileSettingsView>(shellView, "SettingsPage");
                AssertEdgeToEdge(settings, window);
                Assert.Equal(shell.SafeAreaTop, Required<Border>(settings, "SettingsTopInset").Padding);
                Assert.Equal(shell.SafeAreaBottom, Required<Border>(settings, "SettingsBottomInset").Padding);

                shell.Page = MobilePage.Library;
                Pump(window);

                var library = Required<LibraryView>(shellView, "LibraryPage");
                AssertEdgeToEdge(library, window);
                Assert.Equal(shell.SafeAreaTop, Required<Border>(library, "LibraryTopInset").Padding);
                Assert.Equal(shell.SafeAreaBottom, Required<Border>(library, "LibraryListBottomInset").Padding);

                shell.Library.BeginCreateCommand.Execute(null);
                Pump(window);

                Assert.Equal(shell.SafeAreaBottom, Required<Border>(library, "LibraryEditorBottomInset").Padding);
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

                var dockedDrawer = Required<Border>(shellView, "DockedDrawer");
                var hingeGap = Required<Border>(shellView, "HingeGap");
                var chat = Required<ChatDetailView>(shellView, "ChatSurface");
                Assert.Equal(430, dockedDrawer.Bounds.Width, 1);
                Assert.Equal(24, hingeGap.Bounds.Width, 1);
                Assert.Equal(window.ClientSize.Width - 430 - 24, chat.Bounds.Width, 1);

                var drawer = Required<MobileDrawerView>(shellView, "DockedDrawerContent");
                AssertRenderedSideInset(Required<Border>(drawer, "DrawerHeaderSideInset"), sideInsets);
                AssertRenderedSideInset(Required<Border>(drawer, "DrawerContentSideInset"), sideInsets);
                AssertRenderedSideInset(Required<Border>(drawer, "DrawerAccountSideInset"), sideInsets);

                AssertRenderedSideInset(Required<Border>(chat, "ChatHeaderSideInset"), sideInsets);
                AssertRenderedSideInset(Required<Border>(chat, "ChatTranscriptSideInset"), sideInsets);
                AssertRenderedSideInset(Required<Border>(chat, "ComposerSideInset"), sideInsets);
                AssertRenderedSideInset(Required<Border>(chat, "WelcomeSideInset"), sideInsets);
                Assert.Equal(new Thickness(0, safeArea.Top, 0, 0),
                    Required<Border>(chat, "TopBarInset").Padding);
                Assert.Equal(new Thickness(0, 0, 0, safeArea.Bottom),
                    Required<Border>(chat, "ComposerInset").Padding);

                var runSettings = Required<StrataBottomSheet>(chat, "RunSettingsSheet");
                shell.Chat.IsRunSettingsSheetOpen = true;
                Pump(window);

                Assert.Equal(shell.SafeAreaSheetTitleMargin, runSettings.Padding);
                Assert.Equal(
                    shell.SafeAreaSheetTitleMargin,
                    runSettings.GetVisualDescendants()
                        .OfType<TextBlock>()
                        .Single(control => control.Name == "PART_Title")
                        .Margin);
                var runSettingsContent = Required<Border>(chat, "RunSettingsSideInset");
                Assert.Equal(
                    shell.SafeAreaSheetPresenterMargin,
                    runSettingsContent.GetVisualAncestors()
                        .OfType<Control>()
                        .First(control => control.Name == "PART_ContentPresenter")
                        .Margin);
                AssertRenderedSideInset(runSettingsContent, sideInsets);

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
                AssertRenderedSideInset(Required<Border>(library, "LibrarySectionsSideInset"), sideInsets);
                AssertRenderedSideInset(Required<Border>(library, "LibrarySearchSideInset"), sideInsets);
                AssertRenderedSideInset(Required<Border>(library, "LibraryListSideInset"), sideInsets);

                var libraryActions = Required<StrataBottomSheet>(library, "LibraryActionsSheet");
                shell.Library.IsRowActionsOpen = true;
                Pump(window);

                AssertSameRenderedBounds(libraryActions, library);
                Assert.Equal(shell.SafeAreaSheetTitleMargin, libraryActions.Padding);
                var libraryActionsContent = Required<Border>(library, "LibraryActionsSideInset");
                Assert.Equal(
                    shell.SafeAreaSheetPresenterMargin,
                    libraryActionsContent.GetVisualAncestors()
                        .OfType<Control>()
                        .First(control => control.Name == "PART_ContentPresenter")
                        .Margin);
                AssertRenderedSideInset(libraryActionsContent, sideInsets);

                shell.Library.IsRowActionsOpen = false;
                Pump(window);

                shell.Library.BeginCreateCommand.Execute(null);
                Pump(window);

                AssertRenderedSideInset(Required<Border>(library, "LibraryEditorSideInset"), sideInsets);

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
