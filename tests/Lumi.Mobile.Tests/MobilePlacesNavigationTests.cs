using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.Behaviors;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

/// <summary>
/// The desktop's places on a phone and a tablet: the chat list is home, every place is one tap
/// away in the thumb zone, and a wide window keeps each place's list beside its content.
/// </summary>
public sealed class MobilePlacesNavigationTests
{
    [Fact]
    public async Task EachPlaceOpensWhereTheDesktopKeepsIt()
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(393, 852);

        shell.SelectPlace(MobilePlace.Jobs);
        Assert.Equal(MobilePage.Library, shell.Page);
        Assert.Equal(LibraryScope.Jobs, shell.Library.Scope);
        Assert.Equal(LibrarySection.Jobs, shell.Library.Section);
        Assert.Equal(MobilePlace.Jobs, shell.Place);
        Assert.True(shell.IsJobsPlace);
        Assert.False(shell.Library.ShowSectionChips);
        Assert.Equal("Jobs", shell.Library.ScopeTitle);

        shell.SelectPlace(MobilePlace.Customize);
        Assert.Equal(LibraryScope.Customize, shell.Library.Scope);
        Assert.Equal(MobilePlace.Customize, shell.Place);
        Assert.Equal(
            new[] { LibrarySection.Lumis, LibrarySection.Skills, LibrarySection.McpServers, LibrarySection.Projects },
            shell.Library.ScopeSections.Select(option => option.Section));
        Assert.Equal(new[] { "Agents", "Skills", "Connectors", "Projects" },
            shell.Library.ScopeSections.Select(option => option.Name));
        Assert.True(shell.Library.ShowSectionChips);
        Assert.Equal(LibrarySection.Lumis, shell.Library.Section);

        shell.SelectPlace(MobilePlace.Settings);
        Assert.Equal(MobilePage.Settings, shell.Page);
        Assert.Equal(SettingsSection.None, shell.SettingsSection);
        Assert.True(shell.IsSettingsPlace);

        shell.SelectPlace(MobilePlace.Chats);
        Assert.Equal(MobilePage.Chat, shell.Page);
        Assert.True(shell.IsDrawerOpen, "the chat list is home on a phone");
        Assert.True(shell.IsChatsPlace);
    }

    [Fact]
    public async Task CustomizeRemembersItsSectionAcrossPlaces()
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(393, 852);

        shell.SelectPlace(MobilePlace.Customize);
        // The section picker selects by index, as its list does.
        shell.Library.ScopeSectionIndex = 1;
        Assert.Equal(LibrarySection.Skills, shell.Library.Section);

        shell.SelectPlace(MobilePlace.Jobs);
        Assert.Equal(LibrarySection.Jobs, shell.Library.Section);

        shell.SelectPlace(MobilePlace.Customize);
        Assert.Equal(LibrarySection.Skills, shell.Library.Section);
        Assert.True(shell.Library.ScopeSections.Single(option => option.Section == LibrarySection.Skills).IsSelected);
    }

    [Fact]
    public async Task LeavingAPlaceWithUnsavedEditsAsksFirst()
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(393, 852);
        shell.SelectPlace(MobilePlace.Customize);
        shell.Library.BeginCreateCommand.Execute(null);
        shell.Library.EditName = "Half-written agent";
        Assert.True(shell.Library.HasUnsavedChanges);

        shell.SelectPlace(MobilePlace.Jobs);

        Assert.True(shell.Library.IsDiscardConfirmationOpen);
        Assert.Equal(LibraryScope.Customize, shell.Library.Scope);
        Assert.Equal("Half-written agent", shell.Library.EditName);

        shell.Library.DiscardChangesCommand.Execute(null);
        Assert.Equal(LibraryScope.Jobs, shell.Library.Scope);
        Assert.Equal(MobilePlace.Jobs, shell.Place);
        Assert.False(shell.Library.IsEditing);
    }

    /// <summary>
    /// Two panes: the chat list docks beside the chat; the other places bring their own list, so the
    /// chat list steps away for them and returns with Chats.
    /// </summary>
    [Fact]
    public async Task OnTwoPanesOnlyTheChatListDocks()
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(984, 1092);
        Assert.True(shell.IsTwoPane);
        Assert.True(shell.CanDockDrawer);
        Assert.True(shell.IsDrawerDocked);
        Assert.Equal(354, shell.NavigationLeadingWidth);

        shell.SelectPlace(MobilePlace.Customize);
        Assert.False(shell.CanDockDrawer);
        Assert.False(shell.IsNavigationOpen);
        Assert.Equal(0, shell.NavigationLeadingWidth);
        Assert.True(shell.IsTwoPane, "Customize lays out its own list beside its content");

        shell.SelectPlace(MobilePlace.Settings);
        Assert.Equal(SettingsSection.Appearance, shell.VisibleSettingsSection);
        Assert.True(shell.ShowSettingsDetail);

        shell.SelectPlace(MobilePlace.Chats);
        Assert.True(shell.IsDrawerDocked);
        Assert.Equal(354, shell.NavigationLeadingWidth);
        Assert.False(shell.IsDrawerOpen, "a docked list is not an overlay");
    }

    [Fact]
    public async Task OnePaneShowsSettingsListThenOnePage()
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(393, 852);
        shell.SelectPlace(MobilePlace.Settings);
        Assert.Equal(SettingsSection.None, shell.VisibleSettingsSection);
        Assert.False(shell.ShowSettingsDetail);

        shell.OpenSettingsSectionCommand.Execute("Appearance");
        Assert.True(shell.IsAppearanceSectionVisible);
        Assert.True(shell.CanGoBack);
        shell.GoBackCommand.Execute(null);
        Assert.Equal(SettingsSection.None, shell.SettingsSection);
        Assert.Equal(MobilePage.Settings, shell.Page);
    }

    [Fact]
    public async Task UnfoldingKeepsTheOpenChatAndFoldingWithNothingOpenLandsOnTheList()
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(412, 960);
        shell.Chat.Reset(Guid.NewGuid(), "Reading on the cover");
        shell.IsDrawerOpen = false;

        shell.UpdateLayout(984, 1092);
        Assert.True(shell.IsDrawerDocked);
        Assert.False(shell.IsDrawerOpen);

        shell.UpdateLayout(412, 960);
        Assert.False(shell.IsDrawerOpen, "the chat being read stays in view on the cover");

        shell.Chat.Reset(Guid.Empty, "New chat");
        shell.UpdateLayout(984, 1092);
        shell.UpdateLayout(412, 960);
        Assert.True(shell.IsDrawerOpen, "with nothing open the cover lands on the chat list");
    }

    [Fact]
    public async Task OnTheUnfoldedFoldThePlanOpensBesideTheChatAndTheListStepsAside()
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(984, 1092);
        shell.Chat.Reset(Guid.NewGuid(), "Plan chat");
        Assert.True(shell.Chat.UseSidePanels);
        Assert.True(shell.IsDrawerDocked);

        shell.Chat.IsPlanOpen = true;

        Assert.True(shell.Chat.HasSidePanelOpen);
        Assert.False(shell.Chat.HasOpenSheet, "a side panel leaves the chat and its composer live");
        Assert.False(shell.IsDrawerDocked, "below three panes the list steps aside for the panel");
        Assert.Equal(new Thickness(0, 0, MobileShellViewModel.SidePanelWidth + 12, 0), shell.ChatSidePanelInset);
        Assert.True(shell.CanGoBack);
        Assert.False(shell.CanDragDrawer);

        shell.GoBackCommand.Execute(null);
        Assert.False(shell.Chat.IsPlanOpen);
        Assert.True(shell.IsDrawerDocked, "the list returns when the panel closes");
        Assert.Equal(default, shell.ChatSidePanelInset);
    }

    [Fact]
    public async Task OnALargeTabletThePanelJoinsTheListAndTheChat()
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(1280, 800);
        shell.Chat.Reset(Guid.NewGuid(), "Three panes");

        shell.Chat.IsSourcesSheetOpen = true;

        Assert.True(shell.IsThreePane);
        Assert.True(shell.Chat.HasSidePanelOpen);
        Assert.True(shell.IsDrawerDocked);
    }

    [Theory]
    [InlineData(393, 852)]
    [InlineData(750, 832)]
    public async Task NarrowerWindowsKeepThePlanAsASheet(double width, double height)
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(width, height);
        shell.Chat.Reset(Guid.NewGuid(), "Sheet chat");

        shell.Chat.IsPlanOpen = true;

        Assert.False(shell.Chat.UseSidePanels);
        Assert.False(shell.Chat.HasSidePanelOpen);
        Assert.True(shell.Chat.HasOpenSheet);
        Assert.Equal(default, shell.ChatSidePanelInset);
    }

    [Fact]
    public async Task AskingForTheListBackTradesThePanelForIt()
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(984, 1092);
        shell.Chat.Reset(Guid.NewGuid(), "Plan chat");
        shell.Chat.IsActivitySheetOpen = true;
        Assert.False(shell.IsDrawerDocked);

        shell.ToggleDrawerCommand.Execute(null);

        Assert.True(shell.IsDrawerDocked);
        Assert.False(shell.Chat.IsActivitySheetOpen);
        Assert.False(shell.IsSidebarCollapsed);
    }

    [Fact]
    public async Task FoldingWithThePanelOpenTurnsItIntoASheet()
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(984, 1092);
        shell.Chat.Reset(Guid.NewGuid(), "Plan chat");
        shell.Chat.IsPlanOpen = true;
        Assert.False(shell.IsDrawerDocked);

        shell.UpdateLayout(412, 960);

        Assert.True(shell.Chat.IsPlanOpen);
        Assert.True(shell.Chat.HasOpenSheet);
        Assert.Equal(default, shell.ChatSidePanelInset);

        shell.Chat.IsPlanOpen = false;
        shell.UpdateLayout(984, 1092);
        Assert.True(shell.IsDrawerDocked, "the list the panel borrowed is back on the unfolded screen");
    }

    [Fact]
    public async Task BesideTheChatOnePanelReplacesAnother()
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(984, 1092);
        shell.Chat.Reset(Guid.NewGuid(), "Panel chat");
        shell.Chat.PlanContent = "## Plan\n\n- [ ] Step";

        shell.Chat.IsActivitySheetOpen = true;
        shell.Chat.IsSourcesSheetOpen = true;
        Assert.False(shell.Chat.IsActivitySheetOpen, "sources must not open hidden under activity");

        shell.Chat.IsPlanOpen = true;
        Assert.False(shell.Chat.IsSourcesSheetOpen);
        Assert.True(shell.Chat.HasSidePanelOpen);
    }

    [Fact]
    public async Task TheChatsDetailsOpenOverAPanelAndBackClosesThemFirst()
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(984, 1092);
        shell.Chat.Reset(Guid.NewGuid(), "Panel chat");
        shell.Chat.PlanContent = "## Plan\n\n- [ ] Step";
        shell.Chat.IsPlanOpen = true;

        shell.Chat.OpenRunSettingsSheetCommand.Execute(null);
        shell.Chat.OpenPlanFromRunSettingsCommand.Execute(null);
        Assert.True(shell.Chat.IsPlanOpen, "Plan in the chat's details shows the plan, never hides it");
        Assert.False(shell.Chat.IsRunSettingsSheetOpen);

        shell.Chat.OpenRunSettingsSheetCommand.Execute(null);
        shell.Chat.IsModelSheetOpen = true;
        shell.GoBackCommand.Execute(null);
        Assert.False(shell.Chat.IsModelSheetOpen);
        Assert.True(shell.Chat.IsPlanOpen);
        shell.GoBackCommand.Execute(null);
        Assert.False(shell.Chat.IsRunSettingsSheetOpen);
        Assert.True(shell.Chat.IsPlanOpen);
        shell.GoBackCommand.Execute(null);
        Assert.False(shell.Chat.IsPlanOpen);
    }

    /// <summary>The browser app's page shrinks above its keyboard; typing must not fold the layout.</summary>
    [Fact]
    public async Task TheBrowserKeyboardLeavesThePanesWhereTheyAre()
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(750, 676);
        shell.Chat.Reset(Guid.Empty, "New chat");
        Assert.True(shell.IsDrawerDocked);

        shell.UpdateLayout(750, 376);
        Assert.True(shell.IsTwoPane);
        Assert.True(shell.IsDrawerDocked);
        Assert.False(shell.IsDrawerOpen, "the list must not slide over the first message being typed");

        shell.UpdateLayout(950, 700);
        shell.Chat.Reset(Guid.NewGuid(), "Reading");
        Assert.True(shell.Chat.UseSidePanels);
        shell.UpdateLayout(950, 380);
        Assert.True(shell.Chat.UseSidePanels);
        Assert.True(shell.IsDrawerDocked);
    }

    /// <summary>
    /// A panel beside the chat leaves the composer usable, in the browser app too, and the chat's
    /// details open over the panel rather than under it.
    /// </summary>
    [Fact]
    public async Task APanelLeavesTheComposerLiveAndTheDetailsOpenOverIt()
    {
        RuntimeHelpers.RunClassConstructor(typeof(NativeTextInputOverlay).TypeHandle);
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            await using var shell = new MobileShellViewModel(store: session.NewStore(), post: action =>
            {
                if (Dispatcher.UIThread.CheckAccess())
                    action();
                else
                    Dispatcher.UIThread.Post(action);
            });
            shell.IsPaired = shell.IsConnected = shell.IsHostReady = true;
            shell.Chat.Reset(Guid.NewGuid(), "Panel chat");
            // No checked task: its strikethrough is Avalonia's shared static decoration, which belongs
            // to whichever headless test session created it first and throws when another draws it.
            shell.Chat.PlanContent = "## Plan\n\n- [ ] One\n- [ ] Two";
            var view = new MobileShellView { DataContext = shell };
            var window = new Window { Width = 984, Height = 1092, Content = view };
            try
            {
                window.Show();
                Pump(window);
                shell.Chat.IsPlanOpen = true;
                TextBox? Composer() => view.GetVisualDescendants().OfType<TextBox>()
                    .FirstOrDefault(textBox => textBox.Name == "PART_Input");
                Assert.True(await WaitUntil(window, () => Composer() is { } input
                                                          && NativeTextInputOverlay.GetVisiblePlacement(input, window) is not null),
                    "the composer stays usable beside the plan");
                var composer = Composer()!;

                shell.Chat.OpenRunSettingsSheetCommand.Execute(null);
                bool DetailsCoverThePanel()
                {
                    var sheets = view.GetVisualDescendants().OfType<StrataBottomSheet>().ToArray();
                    if (sheets.FirstOrDefault(sheet => sheet.Name == "RunSettingsSheet") is not { } details
                        || sheets.FirstOrDefault(sheet => sheet.Name == "PlanSheet")?.GetVisualDescendants().OfType<Border>()
                            .FirstOrDefault(border => border.Name == "PART_SheetMotion") is not { } panel)
                        return false;
                    var inPanel = panel.TranslatePoint(new Point(panel.Bounds.Width - 40, panel.Bounds.Height / 2), window)!.Value;
                    return window.InputHitTest(inPanel) is Visual hit && hit.GetSelfAndVisualAncestors().Contains(details);
                }
                Assert.True(await WaitUntil(window, DetailsCoverThePanel), "the chat's details open over the plan panel");
                Assert.True(await WaitUntil(window, () => NativeTextInputOverlay.GetVisiblePlacement(composer, window) is null),
                    "the composer's native field steps aside while the details are open");
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    /// <summary>
    /// The open place always wears its name, on every place, and the pill sits centred in its own
    /// column without overflowing it: on the narrowest phone, the Fold's cover, and the unfolded Fold
    /// at both its default and its large display size, where the list column is narrowest.
    /// </summary>
    [Theory]
    [InlineData(320, 720)]
    [InlineData(412, 960)]
    [InlineData(750, 832)]
    [InlineData(984, 1092)]
    public async Task ThePlacesAlwaysNameTheOpenPlaceAndSitCentredInTheirColumn(double width, double height)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            await using var shell = new MobileShellViewModel(store: session.NewStore(), post: action =>
            {
                if (Dispatcher.UIThread.CheckAccess())
                    action();
                else
                    Dispatcher.UIThread.Post(action);
            });
            shell.IsPaired = shell.IsConnected = shell.IsHostReady = true;
            var view = new MobileShellView { DataContext = shell };
            var window = new Window { Width = width, Height = height, Content = view };
            try
            {
                window.Show();
                shell.ShowLaunchHome();
                foreach (var (place, barName, columnName) in new[]
                         {
                             (MobilePlace.Chats, "DrawerPlaces", "DrawerContent"),
                             (MobilePlace.Jobs, "LibraryPlaces", "LibraryListPane"),
                             (MobilePlace.Customize, "LibraryPlaces", "LibraryListPane"),
                             (MobilePlace.Settings, "SettingsPlaces", "SettingsListPane")
                         })
                {
                    shell.SelectPlace(place);
                    PlacesBarView? Bar() => view.GetVisualDescendants().OfType<PlacesBarView>()
                        .FirstOrDefault(candidate => candidate.Name == barName);
                    TextBlock? ActiveLabel() => Bar()?.GetVisualDescendants().OfType<Button>()
                        .FirstOrDefault(button => button.Classes.Contains("active"))
                        ?.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Classes.Contains("place-label"));
                    Assert.True(await WaitUntil(window, () => ActiveLabel() is { Opacity: 1 } shown && shown.Bounds.Width > 20),
                        $"{place} at {width} dp: the open place's name is {ActiveLabel()?.Bounds.Width:0.#} dp wide.");
                    await WaitUntil(window, () => !shell.IsDrawerMoving);
                    var bar = Bar()!;

                    var column = view.GetVisualDescendants().OfType<Control>().First(candidate => candidate.Name == columnName);
                    var pill = bar.GetVisualDescendants().OfType<Panel>().Single(panel => panel.Name == "PlacesPill");
                    var left = pill.TranslatePoint(default, column)!.Value.X;
                    var right = column.Bounds.Width - (left + pill.Bounds.Width);
                    Assert.True(left >= 8 && right >= 8, $"{place} at {width} dp: the pill leaves {left:0.#} and {right:0.#} dp beside it.");
                    Assert.True(Math.Abs(left - right) <= 1.5, $"{place} at {width} dp: the pill is off centre ({left:0.#} vs {right:0.#}).");
                    foreach (var button in bar.GetVisualDescendants().OfType<Button>())
                        Assert.True(button.Bounds.Height >= 48 && button.Bounds.Width >= 48, $"{button.Name} is under 48 dp.");
                }
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    /// <summary>
    /// Every place's list runs under its floating places, as the chat list always did: the scroller
    /// fills its pane to the bottom edge, its content rests clear of the pill, and the pill's band
    /// frosts only while rows actually pass beneath it.
    /// </summary>
    [Theory]
    [InlineData(360, 374)]
    [InlineData(750, 832)]
    public async Task EveryPlaceScrollsUnderItsFloatingPlacesAsChatsDo(double width, double height)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            await using var shell = new MobileShellViewModel(store: session.NewStore(), post: action =>
            {
                if (Dispatcher.UIThread.CheckAccess())
                    action();
                else
                    Dispatcher.UIThread.Post(action);
            });
            shell.IsPaired = shell.IsConnected = shell.IsHostReady = true;
            shell.UserName = "Adir";
            shell.ChatList.Apply([new RemoteChatGroup
            {
                Label = "Today",
                Chats = [.. Enumerable.Range(1, 14).Select(index => new RemoteChat { Id = Guid.NewGuid(), Title = $"Chat {index}", Preview = "A reply" })]
            }]);
            shell.Library.Apply(new RemoteLibrary
            {
                Jobs = [.. Enumerable.Range(1, 8).Select(index => new RemoteJob { Id = Guid.NewGuid(), Name = $"Job {index}", IsEnabled = index % 3 != 0, ScheduleSummary = "Every day" })],
                Lumis = [.. Enumerable.Range(1, 6).Select(index => new RemoteLumi { Id = Guid.NewGuid(), Name = $"Agent {index}", Description = "Helps" })]
            });
            var view = new MobileShellView { DataContext = shell };
            var window = new Window { Width = width, Height = height, Content = view };
            try
            {
                window.Show();
                shell.ShowLaunchHome();
                T? Find<T>(string name) where T : Control => view.GetVisualDescendants().OfType<T>()
                    .FirstOrDefault(control => control.Name == name && control.IsEffectivelyVisible);
                double Bottom(Visual visual) => visual.TranslatePoint(new Point(0, visual.Bounds.Height), window)!.Value.Y;
                foreach (var (place, scrollName, footerName, paneName) in new[]
                         {
                             (MobilePlace.Chats, "DrawerScroll", "DrawerFooterChrome", "DrawerLayout"),
                             (MobilePlace.Jobs, "LibraryListScroll", "LibraryFooterChrome", "LibraryListPane"),
                             (MobilePlace.Customize, "LibraryListScroll", "LibraryFooterChrome", "LibraryListPane"),
                             (MobilePlace.Settings, "SettingsScroll", "SettingsFooterChrome", "SettingsListPane")
                         })
                {
                    shell.SelectPlace(place);
                    Assert.True(await WaitUntil(window, () => Find<ScrollViewer>(scrollName) is not null
                                                              && Find<Panel>(footerName) is { Bounds.Height: > 0 }
                                                              && !shell.IsDrawerMoving),
                        $"{place} at {width}x{height}: its list and places did not appear.");
                    var scroll = Find<ScrollViewer>(scrollName)!;
                    var footer = Find<Panel>(footerName)!;
                    var pane = Find<Control>(paneName)!;

                    // The list reaches the pane's bottom edge, under the floating pill...
                    Assert.Equal(Bottom(pane), Bottom(scroll), 1);
                    Assert.Equal(Bottom(pane), Bottom(footer), 1);
                    Assert.True(footer.Bounds.Height >= 64, $"{place}: the places band is {footer.Bounds.Height:0.#} dp.");
                    // ...and its content rests clear of the pill.
                    Assert.Equal(footer.Bounds.Height, Assert.IsAssignableFrom<Layoutable>(scroll.Content).Margin.Bottom, 1);
                    // On a short screen every list overflows, so rows pass under the pill until the end.
                    if (height < 500)
                        Assert.True(scroll.Extent.Height > scroll.Viewport.Height + 1, $"{place}: nothing to scroll.");
                }
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    [Fact]
    public async Task TheTitleSheetActsOnTheOpenChat()
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(393, 852);
        var id = Guid.NewGuid();
        shell.ChatList.Apply([new RemoteChatGroup
        {
            Label = "Today",
            Chats = [new RemoteChat { Id = id, Title = "Weekend plans", IsPinned = true }]
        }]);
        shell.Chat.Reset(id, "Weekend plans");
        Assert.True(shell.CanManageOpenChat);
        shell.Chat.IsRunSettingsSheetOpen = true;
        Assert.Equal("Unpin chat", shell.OpenChatPinLabel);

        shell.RenameOpenChatCommand.Execute(null);
        Assert.False(shell.Chat.IsRunSettingsSheetOpen);
        Assert.True(shell.IsChatActionsOpen);
        Assert.True(shell.IsRenamingChat);
        Assert.Equal(id, shell.ActionChat!.Id);
        Assert.Equal("Weekend plans", shell.ChatNameDraft);

        shell.GoBackCommand.Execute(null);
        shell.GoBackCommand.Execute(null);
        Assert.False(shell.IsChatActionsOpen);

        shell.DeleteOpenChatCommand.Execute(null);
        Assert.True(shell.IsChatActionsOpen);
        Assert.True(shell.IsConfirmingChatDelete);
        Assert.Equal(id, shell.ActionChat!.Id);
    }

    [Fact]
    public async Task ANewChatHasNoChatActionsYet()
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(393, 852);
        shell.Chat.Reset(Guid.Empty, "New chat");

        Assert.False(shell.CanManageOpenChat);
        shell.RenameOpenChatCommand.Execute(null);
        Assert.False(shell.IsChatActionsOpen);
    }

    [Fact]
    public async Task AChatTheListHasNotLoadedOffersNoPin()
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(393, 852);
        shell.Chat.Reset(Guid.NewGuid(), "Opened from the PC");

        // Whether it is pinned is unknown, so the sheet renames and deletes it but does not guess a pin.
        Assert.True(shell.CanManageOpenChat);
        Assert.False(shell.CanPinOpenChat);
        await shell.PinOpenChatCommand.ExecuteAsync(null);
        Assert.Null(shell.Chat.ErrorText);
    }

    [Fact]
    public async Task TheChatHeaderMarksTheChatWithItsProjectsHue()
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(393, 852);
        var project = Guid.NewGuid();
        shell.Chat.Reset(Guid.NewGuid(), "Release notes");
        Assert.False(shell.HasChatHeaderMark);

        shell.Chat.ProjectValue = project.ToString();
        shell.Chat.ProjectName = "lumi";

        Assert.True(shell.HasChatHeaderMark);
        Assert.Equal("L", shell.ChatHeaderMark.Glyph);
        Assert.Equal(ChatIdentityMark.HueFor(project), shell.ChatHeaderMark.Hue);
    }

    [Fact]
    public void ProjectHuesMatchTheDesktopsPalette()
    {
        var palette = new[] { "#818CF8", "#34D399", "#F472B6", "#38BDF8", "#A78BFA", "#2DD4BF" }
            .Select(Avalonia.Media.Color.Parse).ToArray();
        foreach (var id in Enumerable.Range(0, 24).Select(_ => Guid.NewGuid()))
            Assert.Equal(palette[(int)((uint)id.GetHashCode() % (uint)palette.Length)], ChatIdentityMark.HueFor(id));
    }

    /// <summary>A tablet with a keyboard answers the desktop's shortcuts, and a secondary click asks for row actions.</summary>
    [Fact]
    public async Task TabletKeyboardShortcutsAndSecondaryClicksReachThePlaces()
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            await using var shell = new MobileShellViewModel(store: session.NewStore(), post: action =>
            {
                if (Dispatcher.UIThread.CheckAccess())
                    action();
                else
                    Dispatcher.UIThread.Post(action);
            });
            shell.IsPaired = shell.IsConnected = shell.IsHostReady = true;
            var id = Guid.NewGuid();
            shell.Chat.Reset(id, "Keyboard chat");
            shell.ChatList.Apply([new RemoteChatGroup
            {
                Label = "Today",
                Chats = [new RemoteChat { Id = id, Title = "Keyboard chat" }]
            }]);
            var view = new MobileShellView { DataContext = shell };
            var window = new Window { Width = 1100, Height = 820, Content = view };
            try
            {
                window.Show();
                Pump(window);

                void Shortcut(Key key)
                {
                    view.RaiseEvent(new KeyEventArgs
                    {
                        RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = KeyModifiers.Control
                    });
                    Pump(window);
                }

                Shortcut(Key.D2);
                Assert.Equal(MobilePlace.Jobs, shell.Place);
                Shortcut(Key.D3);
                Assert.Equal(MobilePlace.Customize, shell.Place);
                Shortcut(Key.OemComma);
                Assert.Equal(MobilePlace.Settings, shell.Place);
                Shortcut(Key.D1);
                Assert.Equal(MobilePlace.Chats, shell.Place);
                Assert.True(shell.IsDrawerDocked);
                Shortcut(Key.B);
                Assert.True(shell.IsSidebarCollapsed);
                Shortcut(Key.B);
                Assert.False(shell.IsSidebarCollapsed);
                Shortcut(Key.K);
                Assert.True(shell.IsSearchPage);
                shell.GoBackCommand.Execute(null);
                Pump(window);

                await Task.Delay(260);
                Pump(window);
                var row = view.GetVisualDescendants().OfType<MobileDrawerView>().Single()
                    .GetVisualDescendants().OfType<Button>()
                    .Single(button => button.DataContext is ChatListItemViewModel);
                row.RaiseEvent(new ContextRequestedEventArgs());
                Pump(window);
                Assert.True(shell.IsChatActionsOpen);
                Assert.Equal(id, shell.ActionChat!.Id);
                shell.IsChatActionsOpen = false;

                Shortcut(Key.N);
                Assert.Equal(Guid.Empty, shell.Chat.ChatId);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    /// <summary>The list is home, so it says when the PC can't be reached; docked, the chat's strip says it.</summary>
    [Theory]
    [InlineData(393, true)]
    [InlineData(1100, false)]
    public async Task HomeSaysWhenThePcCannotBeReached(double width, bool listShowsStatus)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            await using var shell = new MobileShellViewModel(store: session.NewStore(), post: action =>
            {
                if (Dispatcher.UIThread.CheckAccess())
                    action();
                else
                    Dispatcher.UIThread.Post(action);
            });
            shell.IsPaired = true;
            shell.IsConnectionOffline = true;
            var view = new MobileShellView { DataContext = shell };
            var window = new Window { Width = width, Height = 820, Content = view };
            try
            {
                window.Show();
                shell.ShowLaunchHome();
                Pump(window);
                await Task.Delay(260);
                Pump(window);
                var banner = view.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Name == "DrawerConnectionBanner");
                Assert.Equal(listShowsStatus, banner.IsEffectivelyVisible);
                if (listShowsStatus)
                {
                    var retry = view.GetVisualDescendants().OfType<Button>()
                        .Single(button => button.Name == "DrawerRetryConnectionButton");
                    Assert.True(retry.IsEffectivelyVisible);
                    Assert.True(retry.Bounds.Height >= 48);
                }

                shell.IsConnectionOffline = false;
                shell.IsConnected = shell.IsHostReady = true;
                Pump(window);
                Assert.False(banner.IsEffectivelyVisible);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    /// <summary>
    /// Moving to another place is the desktop's movement, not a slide of the whole list: the list lands
    /// at once, the bar stays where it is, and its highlight glides from the place left behind to the new
    /// one while the names trade places. The bar that comes into view picks the movement up from the
    /// place the user left, whether it was hidden while another place was open or is built only now, the
    /// first time its page shows, and lands exactly on the new place's target.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MovingToAnotherPlaceGlidesTheHighlightWhileTheListLandsInPlace(bool libraryShownBefore)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            await using var shell = new MobileShellViewModel(store: session.NewStore(), post: action =>
            {
                if (Dispatcher.UIThread.CheckAccess())
                    action();
                else
                    Dispatcher.UIThread.Post(action);
            });
            shell.IsPaired = shell.IsConnected = shell.IsHostReady = true;
            var view = new MobileShellView { DataContext = shell };
            var window = new Window { Width = 412, Height = 960, Content = view };
            try
            {
                window.Show();
                shell.ShowLaunchHome();
                await Frames(window, 200);
                if (libraryShownBefore)
                {
                    // The Library's bar was last on screen on Customize, so a bar that started from what it
                    // last showed would set off from the wrong place.
                    shell.SelectPlace(MobilePlace.Customize);
                    await Frames(window, 500);
                    shell.SelectPlace(MobilePlace.Chats);
                    await Frames(window, 500);
                }
                else
                {
                    // A page's bar is built when the page first shows, so it joins the glide partway.
                    Assert.DoesNotContain(view.GetVisualDescendants().OfType<PlacesBarView>(), bar => bar.Name == "LibraryPlaces");
                }
                var drawer = view.GetVisualDescendants().OfType<StrataNavigationDrawer>().Single(control => control.Name == "NavDrawer");
                Assert.Equal(1, drawer.Progress);

                PlacesBarView Bar(string name) => view.GetVisualDescendants().OfType<PlacesBarView>().Single(bar => bar.Name == name);
                static T Part<T>(Control root, string name) where T : Control =>
                    root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
                static (double X, double Width) Selection(PlacesBarView bar)
                {
                    var selection = Part<Border>(bar, "PlacesSelection");
                    return (Assert.IsType<TranslateTransform>(selection.RenderTransform).X, selection.Width);
                }
                static TextBlock Name(PlacesBarView bar, string place) => Part<Button>(bar, place)
                    .GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("place-label"));

                var chatsBar = Bar("DrawerPlaces");
                var chats = Part<Button>(chatsBar, "PlacesChatsButton");
                var (chatsX, chatsWidth) = Selection(chatsBar);
                Assert.Equal(chats.Bounds.X, chatsX, 1);
                Assert.Equal(chats.Bounds.Width, chatsWidth, 1);
                var barBottom = chatsBar.TranslatePoint(new Point(0, chatsBar.Bounds.Height), view)!.Value.Y;

                // The list lands at once rather than sliding away across the screen: not one frame shows it
                // partway.
                var drawerProgress = new System.Collections.Generic.List<double>();
                drawer.PropertyChanged += (_, change) =>
                {
                    if (change.Property == StrataNavigationDrawer.ProgressProperty)
                        drawerProgress.Add(drawer.Progress);
                };
                var switched = System.Diagnostics.Stopwatch.StartNew();
                shell.SelectPlace(MobilePlace.Jobs);
                Assert.Equal(0, drawer.Progress);
                Dispatcher.UIThread.RunJobs();

                window.UpdateLayout();
                var jobsBar = Bar("LibraryPlaces");
                // A stall right after the glide's first frame, as a page building the first time it shows makes
                // on a phone, holds the highlight near where it set off: it glides on once frames resume
                // rather than jumping ahead.
                Thread.Sleep(300);
                Tick(window);
                var afterStall = Selection(jobsBar).X;
                var samples = new System.Collections.Generic.List<(double Elapsed, double X, double ListX)>();
                // A slow frame holds the glide rather than skipping it, so it is followed to its end.
                while (switched.Elapsed < PlacesBarView.GlideDuration + TimeSpan.FromMilliseconds(160)
                       || (switched.Elapsed < TimeSpan.FromSeconds(4) && (jobsBar.IsGliding || chatsBar.IsGliding)))
                {
                    Tick(window);
                    samples.Add((switched.Elapsed.TotalMilliseconds, Selection(jobsBar).X, Selection(chatsBar).X));
                    await Task.Delay(16);
                }
                Tick(window);

                Assert.True(jobsBar.IsEffectivelyVisible);
                Assert.Equal(barBottom, jobsBar.TranslatePoint(new Point(0, jobsBar.Bounds.Height), view)!.Value.Y, 1);
                var jobs = Part<Button>(jobsBar, "PlacesJobsButton");
                var (endX, endWidth) = Selection(jobsBar);
                Assert.Equal(jobs.Bounds.X, endX, 1);
                Assert.Equal(jobs.Bounds.Width, endWidth, 1);
                Assert.True(endX - chatsX > 40, $"Jobs sits {endX - chatsX:0.#} dp after Chats.");
                Assert.True(afterStall - chatsX < (endX - chatsX) * 0.25,
                    $"after a stall the highlight jumped {afterStall - chatsX:0.#} dp of the {endX - chatsX:0.#} dp to Jobs.");
                foreach (var (elapsed, x, listX) in samples)
                {
                    // One movement for the window: the chat list's bar, hidden now, and the bar in view draw
                    // the same glide, though the one in view may have been built partway through it.
                    Assert.Equal(listX, x, 1.5);
                    Assert.InRange(x, chatsX - 1, endX + 1);
                    // Early on the highlight is still on its way: it glides from Chats rather than appearing.
                    if (elapsed < PlacesBarView.GlideDuration.TotalMilliseconds * 0.3)
                        Assert.True(x < endX - 3, $"at {elapsed:0} ms the highlight was already on Jobs.");
                }
                // It never turns back, as it would setting off from a place the user had not just left.
                Assert.All(samples.Zip(samples.Skip(1)), step => Assert.True(step.Second.X >= step.First.X - 0.5,
                    $"the highlight turned back from {step.First.X:0.#} to {step.Second.X:0.#} dp."));
                Assert.Equal(0, drawer.Progress);
                Assert.DoesNotContain(drawerProgress, progress => progress is > 0 and < 1);
                // The names traded places.
                Assert.Equal(1, Name(jobsBar, "PlacesJobsButton").Opacity, 2);
                Assert.True(Name(jobsBar, "PlacesJobsButton").Bounds.Width > 20);
                Assert.Equal(0, Name(jobsBar, "PlacesChatsButton").Opacity, 2);
                Assert.Equal(0, Name(jobsBar, "PlacesChatsButton").MaxWidth, 1);
                Assert.False(jobsBar.IsGliding);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    /// <summary>
    /// The places pill is the desktop's nav pill in the desktop's materials and ink: Surface1 at 40% under
    /// a hairline of text at 6% that runs unbroken all round and the nav pill's small shadow, over one frost
    /// of its own and nothing else (no sheen); the places you are not in are quiet glyphs, the open one takes
    /// the accent and its name the text's ink.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThePillIsTheDesktopsNavigationPill(bool darkTheme)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            await using var shell = new MobileShellViewModel(store: session.NewStore(), post: action => action());
            var drawer = new MobileDrawerView { DataContext = shell };
            var window = new Window
            {
                Width = 412, Height = 892, Content = drawer,
                RequestedThemeVariant = darkTheme ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light,
            };
            try
            {
                window.Show();
                await Frames(window, 400);

                object Resource(string key)
                {
                    Assert.True(drawer.TryFindResource(key, drawer.ActualThemeVariant, out var value), key);
                    return value!;
                }
                Color ColorOf(string key) => Resource(key) switch
                {
                    Color color => color,
                    ISolidColorBrush brush => brush.Color,
                    var other => throw new Xunit.Sdk.XunitException($"{key} is a {other.GetType().Name}."),
                };
                static Color At(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
                static T Part<T>(Control root, string name) where T : Control =>
                    root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
                static Color Solid(IBrush? brush)
                {
                    var solid = Assert.IsAssignableFrom<ISolidColorBrush>(brush);
                    Assert.Equal(1, solid.Opacity);
                    return solid.Color;
                }

                var surface = Part<Border>(drawer, "PlacesSurface");
                Assert.Equal(At(ColorOf("Color.Surface1"), 0x66), Solid(surface.Background));
                Assert.Equal(At(ColorOf("Color.TextPrimary"), 0x0F), Solid(surface.BorderBrush));
                Assert.Equal(new Thickness(1), surface.BorderThickness);
                Assert.Equal(BoxShadows.Parse("0 1 6 0 #16000000"), surface.BoxShadow);
                var pill = Part<Panel>(drawer, "PlacesPill");
                Assert.Equal("PlacesFrost", Assert.Single(pill.GetVisualDescendants().OfType<StrataBackdropBlur>()).Name);
                Assert.DoesNotContain(pill.GetVisualDescendants().OfType<Control>(), control => control.Name == "PlacesSheen");
                // The highlight sits 4 dp inside the hairline, concentric with the pill's ends.
                var selection = Part<Border>(drawer, "PlacesSelection");
                Assert.Equal(ColorOf("Brush.AccentGlowSoft"), Solid(selection.Background));
                Assert.Equal(ColorOf("Brush.AccentGlowStrong"), Solid(selection.BorderBrush));
                Assert.Equal(surface.CornerRadius.TopLeft - surface.BorderThickness.Left - surface.Padding.Left,
                    selection.CornerRadius.TopLeft, 1);
                Assert.Equal(surface.Bounds.Height - 2 * (surface.BorderThickness.Top + surface.Padding.Top),
                    Part<Button>(drawer, "PlacesChatsButton").Bounds.Height, 1);

                static PathIcon Glyph(Button place) => place.GetVisualDescendants().OfType<PathIcon>().First();
                var chats = Part<Button>(drawer, "PlacesChatsButton");
                Assert.Contains("active", chats.Classes);
                Assert.Equal(ColorOf("Brush.AccentDefault"), Solid(Glyph(chats).Foreground));
                Assert.Equal(1, Glyph(chats).Opacity, 2);
                Assert.Equal(ColorOf("Brush.TextPrimary"),
                    Solid(chats.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("place-label")).Foreground));
                foreach (var name in new[] { "PlacesJobsButton", "PlacesSettingsButton" })
                {
                    var glyph = Glyph(Part<Button>(drawer, name));
                    Assert.Equal(ColorOf("Brush.TextSecondary"), Solid(glyph.Foreground));
                    Assert.Equal(0.6, glyph.Opacity, 2);
                }
                // The three-layer Customize glyph dims as one (BackdropBlurRenderTests checks its pixels).
                var tune = Part<Button>(drawer, "PlacesCustomizeButton").GetVisualDescendants().OfType<Panel>()
                    .Single(panel => panel.Classes.Contains("place-tune"));
                Assert.Equal(1, tune.Opacity, 2);
                Assert.Equal(0x99, Assert.IsAssignableFrom<ISolidColorBrush>(tune.OpacityMask).Color.A);
                Assert.All(tune.Children.OfType<PathIcon>(), layer => Assert.Equal(1, layer.Opacity, 2));
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    private static async Task Frames(Window window, int milliseconds)
    {
        for (var waited = 0; waited <= milliseconds; waited += 16)
        {
            Tick(window);
            await Task.Delay(16);
        }
    }

    private static void Tick(Window window)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// <summary>Pumps until a sheet or slide has settled into the expected state, or two seconds pass.</summary>
    private static async Task<bool> WaitUntil(Window window, Func<bool> condition)
    {
        for (var waited = 0; waited <= 2000; waited += 40)
        {
            Pump(window);
            if (condition())
                return true;
            await Task.Delay(40);
        }

        return false;
    }

    private static void Pump(Window window)
    {
        window.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class ShellScope : IAsyncDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "lumi-mobile-tests", Guid.NewGuid().ToString("n"));

        public ShellScope() =>
            Shell = new MobileShellViewModel(store: new MobileSettingsStore(_directory), post: action => action());

        public MobileShellViewModel Shell { get; }

        public MobileShellViewModel Paired(double width, double height)
        {
            Shell.IsPaired = true;
            Shell.UpdateLayout(width, height);
            return Shell;
        }

        public async ValueTask DisposeAsync()
        {
            await Shell.DisposeAsync();
            try
            {
                if (Directory.Exists(_directory))
                    Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
