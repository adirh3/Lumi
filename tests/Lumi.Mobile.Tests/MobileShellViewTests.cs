using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Styling;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.Layout;
using Lumi.Mobile.Views;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

/// <summary>
/// Renders the real <see cref="MobileShellView"/> against a real <see cref="MobileShellViewModel"/>.
///
/// These exist because of a bug every view-model test passed straight through: the shell reported
/// <c>Section = Chats</c> while the Library page was what actually got painted. The cause was
/// XAML-only — <c>IsVisible="{Binding IsLibrarySection}"</c> sat on the same control that set
/// <c>DataContext="{Binding Library}"</c>, so the binding resolved against LibraryViewModel, failed,
/// and left the page visible. Only a rendered-tree assertion catches that class of mistake.
/// </summary>
[Collection("Headless mobile UI")]
public sealed class MobileShellViewTests
{
    private const string Pc = "LIGHTO-DESKTOP";

    private static async Task Run(Action<MobileShellViewModel, Window> body)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;

        await session.Dispatch(async () =>
        {
            MobileShellViewModel? shell = null;
            Window? window = null;
            try
            {
                // post: run inline so property fan-out is observable without pumping the dispatcher.
                shell = new MobileShellViewModel(store: session.NewStore(), post: action => action());
                window = new Window
                {
                    Width = 412,
                    Height = 892,
                    Content = new MobileShellView { DataContext = shell },
                };
                window.Show();

                body(shell, window);
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

    /// <summary>Drains the dispatcher so queued measure/arrange passes actually run.</summary>
    private static void Pump(Window window)
    {
        window.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Whether the overlay drawer is open. Asserting this rather than a named Border keeps the test
    /// tied to the behaviour rather than to one implementation of it — these assertions used to
    /// reach for a hand-rolled "SlidingDrawer"/"Scrim" pair that the real drawer control replaced.
    /// </summary>
    private static StrataNavigationDrawer Drawer(Window window) =>
        window.GetVisualDescendants().OfType<StrataNavigationDrawer>().Single();

    private static bool DrawerOpen(Window window) => Drawer(window).IsOpen;

    private static Control Named(Window window, string name) =>
        window.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name);

    private static void Pair(MobileShellViewModel shell)
    {
        shell.HostName = Pc;
        shell.IsPaired = true;
    }

    private static void OpenChat(MobileShellViewModel shell) =>
        shell.Chat.Reset(Guid.NewGuid(), "Hello");

    private static void Layout(Window window, MobileShellViewModel shell, double width, double height,
        FoldPosture posture = FoldPosture.Flat, double hingeSize = 0, double hingePosition = 0)
    {
        // Drive the same path the real heads use: the host pushes posture onto the view, the view
        // derives the layout from its own bounds. Calling shell.UpdateLayout directly would bypass
        // (and be immediately overwritten by) the view's own size-changed push.
        var view = (MobileShellView)window.Content!;
        view.Posture = posture;
        view.HingeSize = hingeSize;
        view.HingePosition = hingePosition;

        window.Width = width;
        window.Height = height;
        Pump(window);
    }

    /// <summary>
    /// The app IS the chat: it launches straight into a conversation surface, never a list. This is
    /// the single most important structural property of the redesign, and the thing every other
    /// navigation assertion below depends on.
    /// </summary>
    [Fact]
    public async Task PairedShell_LandsOnTheChatSurfaceNotAList()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            Layout(window, shell, 412, 892);

            Assert.True(shell.IsChatPage);
            Assert.True(Named(window, "ChatSurface").IsVisible);
            Assert.False(Named(window, "LibraryHost").IsVisible);
            Assert.False(Named(window, "SettingsPage").IsVisible);

            var separator = window.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Name == "PART_HeaderSeparator");
            Assert.Equal(0, separator.Bounds.Height);

            // Nothing is docked or slid open on a phone until the user asks for it.
            Assert.False(Named(window, "DockedDrawer").IsVisible);
            Assert.False(DrawerOpen(window));
        });
    }

    [Fact]
    public async Task PairedButOffline_ShowsAVisibleReconnectBanner()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            shell.IsConnected = false;
            shell.IsHostReady = false;
            Layout(window, shell, 412, 892);

            var banner = Named(window, "ConnectionBanner");
            Assert.True(banner.IsEffectivelyVisible);
            Assert.Contains("Reconnecting", shell.ConnectionBannerText);
            Assert.Contains(Pc, shell.ConnectionBannerText);

            shell.IsConnected = true;
            shell.IsHostReady = true;
            Pump(window);
            Assert.False(banner.IsEffectivelyVisible);
        });
    }

    [Fact]
    public async Task UnpairedShell_PaintsConnectFlowOnly()
    {
        await Run((shell, window) =>
        {
            Layout(window, shell, 412, 892);

            Assert.True(Named(window, "ConnectHost").IsVisible);
            Assert.False(Named(window, "NavDrawer").IsVisible);
        });
    }

    [Fact]
    public async Task PairingSwapsConnectFlowForTheShell()
    {
        await Run((shell, window) =>
        {
            Layout(window, shell, 412, 892);
            Assert.True(Named(window, "ConnectHost").IsVisible);

            Pair(shell);
            Pump(window);

            Assert.False(Named(window, "ConnectHost").IsVisible);
            Assert.True(Named(window, "ShellRoot").IsVisible);
        });
    }

    /// <summary>The hamburger opens a scrimmed overlay drawer, and the scrim dismisses it.</summary>
    [Fact]
    public async Task CompactWidth_HamburgerOpensAnOverlayDrawerThatTheScrimDismisses()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            Layout(window, shell, 412, 892);

            Assert.True(shell.ShowMenuButton);
            Assert.False(DrawerOpen(window));

            shell.ToggleDrawerCommand.Execute(null);
            Pump(window);

            Assert.True(DrawerOpen(window));
            Assert.True(DrawerOpen(window));

            // The chat stays mounted underneath — the drawer covers it, never replaces it.
            Assert.True(Named(window, "ChatSurface").IsVisible);

            shell.CloseDrawerCommand.Execute(null);
            Pump(window);

            Assert.False(DrawerOpen(window));
            Assert.False(DrawerOpen(window));
        });
    }

    /// <summary>
    /// At expanded widths the drawer docks beside the chat — and is still collapsible, because even
    /// on a tablet the sidebar costs the conversation a third of its width. The hamburger stays.
    /// </summary>
    [Fact]
    public async Task ExpandedWidth_DocksTheDrawerAndLetsTheUserCollapseIt()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            Layout(window, shell, 1112, 834);

            Assert.True(shell.IsDrawerDocked);
            Assert.True(shell.ShowMenuButton);
            Assert.True(Named(window, "DockedDrawer").IsVisible);
            Assert.False(DrawerOpen(window));
            Assert.False(DrawerOpen(window));

            var narrow = Named(window, "ChatSurface").Bounds.Width;

            // Collapsing hands the sidebar's width to the conversation, without floating a second
            // copy of the drawer over the top.
            shell.ToggleDrawerCommand.Execute(null);
            Pump(window);

            Assert.True(shell.IsSidebarCollapsed);
            Assert.False(Named(window, "DockedDrawer").IsVisible);
            Assert.False(DrawerOpen(window));
            Assert.True(Named(window, "ChatSurface").Bounds.Width > narrow);

            shell.ToggleDrawerCommand.Execute(null);
            Pump(window);

            Assert.True(Named(window, "DockedDrawer").IsVisible);
        });
    }

    [Fact]
    public async Task RotatingBackToCompact_UndocksTheDrawer()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            Layout(window, shell, 1112, 834);
            Assert.True(Named(window, "DockedDrawer").IsVisible);

            Layout(window, shell, 412, 892);

            Assert.False(shell.IsDrawerDocked);
            Assert.False(Named(window, "DockedDrawer").IsVisible);
            Assert.True(shell.ShowMenuButton);
        });
    }

    /// <summary>
    /// A drawer that stays open after you pick something from it hides the very thing you asked
    /// for, so every drawer action must dismiss it.
    /// </summary>
    [Fact]
    public async Task PickingFromTheDrawer_ClosesItAndShowsThePage()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            Layout(window, shell, 412, 892);

            shell.ToggleDrawerCommand.Execute(null);
            Pump(window);
            Assert.True(DrawerOpen(window));

            shell.ShowPageCommand.Execute("Library");
            Pump(window);

            Assert.False(DrawerOpen(window));
            Assert.True(Named(window, "LibraryHost").IsVisible);
        });
    }

    /// <summary>
    /// Search takes the whole screen, so the keyboard is not competing with a list behind it.
    /// </summary>
    [Fact]
    public async Task SearchOpensFullScreenAndBackReturnsToTheChat()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            Layout(window, shell, 412, 892);

            shell.ToggleDrawerCommand.Execute(null);
            shell.OpenSearchCommand.Execute(null);
            Pump(window);

            Assert.True(shell.IsSearchPage);
            Assert.False(DrawerOpen(window));

            var search = Named(window, "SearchPage");
            Assert.True(search.IsVisible);
            Assert.True(search.Bounds.Width > 400, "search must own the full width");
            Assert.True(
                Named(window, "SearchField").IsFocused,
                "the search field must own focus so Android opens the keyboard without a second tap");
            var focusAccent = Named(window, "SearchField")
                .GetVisualDescendants()
                .OfType<Border>()
                .Single(border => border.Name == "FocusAccentBar");
            Assert.False(
                focusAccent.IsVisible,
                "a capsule search field must not render Strata's rectangular bottom accent line");

            shell.GoBackCommand.Execute(null);
            Pump(window);
            Assert.True(shell.IsChatPage);
        });
    }

    [Fact]
    public async Task LibrarySectionSelection_IsOneRoundedSurface()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            Layout(window, shell, 412, 892);
            shell.Page = MobilePage.Library;
            Pump(window);

            var picker = Assert.IsType<ListBox>(Named(window, "SectionPicker"));
            var selected = picker.GetVisualDescendants()
                .OfType<ListBoxItem>()
                .Single(item => item.IsSelected);
            var outer = selected.GetVisualDescendants()
                .OfType<Border>()
                .Single(border => border.Name == "LayoutRoot");
            var content = selected.GetVisualDescendants()
                .OfType<ContentPresenter>()
                .Single(presenter => presenter.Name == "PART_ContentPresenter");
            var pipe = selected.GetVisualDescendants()
                .OfType<Border>()
                .Single(border => border.Name == "PART_SelectedPipe");

            Assert.Equal(new CornerRadius(16), outer.CornerRadius);
            Assert.Equal(default, content.CornerRadius);
            Assert.True(
                content.Background is null
                || content.Background is ISolidColorBrush { Color.A: 0 },
                "the inner presenter must not paint a second selection rectangle");
            Assert.False(pipe.IsVisible);
        });
    }

    [Fact]
    public async Task OpeningAProjectChat_DoesNotChangeTheProjectFilter()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            Layout(window, shell, 412, 892);
            var chatId = Guid.NewGuid();
            shell.ChatList.Apply(
            [
                new RemoteChatGroup
                {
                    Label = "Today",
                    Chats =
                    [
                        new RemoteChat
                        {
                            Id = chatId,
                            Title = "Apollo chat",
                            ProjectName = "Apollo",
                            LastModelUsed = "claude-opus-5"
                        }
                    ]
                }
            ]);

            shell.ChatList.OpenChatCommand.Execute(
                shell.ChatList.Groups.SelectMany(group => group.Chats).Single());
            shell.Chat.ApplyStatus(new RemoteChatStatus
            {
                ChatId = chatId,
                ProjectName = "Apollo",
                Model = "claude-opus-5"
            });

            Assert.Null(shell.ActiveProject);
            Assert.Null(shell.ChatList.ProjectFilterId);
            Assert.Equal("claude-opus-5", shell.Chat.Model);
        });
    }

    [Fact]
    public async Task SwitchingChats_SeedsAndNeverClearsTheirPersistedModel()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            Layout(window, shell, 412, 892);
            var opus = Guid.NewGuid();
            var gpt = Guid.NewGuid();
            shell.ChatList.Apply(
            [
                new RemoteChatGroup
                {
                    Label = "Today",
                    Chats =
                    [
                        new RemoteChat { Id = opus, Title = "Opus", LastModelUsed = "claude-opus-5" },
                        new RemoteChat { Id = gpt, Title = "GPT", LastModelUsed = "gpt-5.6-sol" }
                    ]
                }
            ]);

            shell.ChatList.OpenChatCommand.Execute(
                shell.ChatList.Groups.SelectMany(group => group.Chats).Single(chat => chat.Id == opus));
            Assert.Equal("claude-opus-5", shell.Chat.Model);

            // An inactive/racing transcript used to carry Model=null and erase the seeded value.
            shell.Chat.ApplyStatus(new RemoteChatStatus { ChatId = opus, Model = null });
            Assert.Equal("claude-opus-5", shell.Chat.Model);

            shell.ChatList.OpenChatCommand.Execute(
                shell.ChatList.Groups.SelectMany(group => group.Chats).Single(chat => chat.Id == gpt));
            Assert.Equal("gpt-5.6-sol", shell.Chat.Model);
        });
    }

    /// <summary>Pages stack over the chat; back peels them off one layer at a time.</summary>
    [Fact]
    public async Task BackDismissesTheDrawerThenThePageThenStops()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            Layout(window, shell, 412, 892);

            shell.ShowPageCommand.Execute("Settings");
            shell.ToggleDrawerCommand.Execute(null);
            Pump(window);

            // Drawer first: it is the topmost thing on screen.
            Assert.True(shell.CanGoBack);
            shell.GoBackCommand.Execute(null);
            Pump(window);
            Assert.False(DrawerOpen(window));
            Assert.True(Named(window, "SettingsPage").IsVisible);

            // Then the page.
            Assert.True(shell.CanGoBack);
            shell.GoBackCommand.Execute(null);
            Pump(window);
            Assert.True(shell.IsChatPage);

            // Nothing left of ours: the system back must fall through and leave the app.
            Assert.False(shell.CanGoBack);
        });
    }

    [Fact]
    public async Task UnfoldedFoldable_DocksTheDrawerAndKeepsTheHingeClear()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            Layout(window, shell, 884, 908, FoldPosture.BookVerticalHinge, 24, 430);

            Assert.True(shell.IsDrawerDocked);
            Assert.True(shell.HasHingeGap);
            Assert.Equal(24, shell.HingeGapWidth, 1);

            // The drawer must end exactly at the crease. Docking it at the default 320 would leave
            // the conversation starting mid-drawer and running underneath the physical fold.
            var drawer = Named(window, "DockedDrawer");
            Assert.True(drawer.IsVisible);
            Assert.Equal(430, drawer.Bounds.Width, 1);
        });
    }

    /// <summary>
    /// The ambient field has to travel vertically with the work, exactly like the desktop: resting
    /// low at the composer, rising into the conversation when a turn starts, and settling back down
    /// when it lands. A field pinned at one height — which is what the first version did, swapping
    /// between two fixed points only on <c>HasChat</c> — reads as a static gradient, not a presence.
    /// </summary>
    [Fact]
    public async Task Presence_RisesWhileLumiWorksAndSettlesBackDownWhenItFinishes()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            Layout(window, shell, 412, 892);

            var presence = Assert.IsType<StrataPresence>(Named(window, "Presence"));

            var resting = presence.FocusPoint.Y;
            Assert.True(resting > 0.6, $"idle field sat at {resting:F2}, expected it low near the composer");

            shell.Chat.ApplyStatus(new RemoteChatStatus { ChatId = shell.Chat.ChatId, IsBusy = true });
            Pump(window);

            var working = presence.FocusPoint.Y;
            Assert.True(working < resting - 0.1,
                $"field moved from {resting:F2} to {working:F2} — it must visibly rise into the conversation");

            shell.Chat.ApplyTranscript(new RemoteTranscript
            {
                ChatId = shell.Chat.ChatId,
                Revision = 1,
                IsLatestWindow = true,
                Turns =
                [
                    new RemoteTranscriptTurn
                    {
                        Id = "completed",
                        Items =
                        [
                            new RemoteTranscriptItem
                            {
                                Id = "assistant",
                                Kind = RemoteProtocol.ItemKinds.Assistant,
                                Text = "Done"
                            }
                        ]
                    }
                ],
                Status = new RemoteChatStatus
                {
                    ChatId = shell.Chat.ChatId,
                    IsBusy = false
                }
            });
            Pump(window);

            Assert.True(presence.FocusPoint.Y > working + 0.1,
                $"field stayed at {presence.FocusPoint.Y:F2} after the turn — it must pour back down to the composer");
        });
    }

    /// <summary>
    /// An empty chat used to be a greeting on a blank screen, which tells a new user nothing about
    /// what Lumi can do and leaves the expensive part on a phone — typing — entirely to them.
    ///
    /// <para>The trigger is an EMPTY CONVERSATION rather than a missing one. "New chat" creates the
    /// chat on the desktop immediately, so keying off <c>HasChat</c> meant the panel vanished the
    /// instant you started a chat and you were left staring at nothing.</para>
    /// </summary>
    [Fact]
    public async Task NewChat_OffersStartersUntilTheConversationHasSomethingInIt()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            Layout(window, shell, 412, 892);

            var starters = Named(window, "WelcomeStarters");
            Assert.True(starters.IsVisible);

            var buttons = starters.GetVisualDescendants().OfType<Button>().ToList();
            Assert.Equal(shell.Chat.Starters.Count, buttons.Count);
            Assert.NotEmpty(buttons);

            // Tapping one has to load the composer, not send blind.
            buttons[0].Command!.Execute(shell.Chat.Starters[0].Text);
            Assert.Equal(shell.Chat.Starters[0].Text, shell.Chat.PromptText);

            // A freshly created chat still counts as empty — this is the case that regressed.
            OpenChat(shell);
            Pump(window);
            Assert.True(Named(window, "NoChatPlaceholder").IsVisible);

            // ...and it goes away as soon as the conversation actually has content.
            shell.Chat.ApplyTranscript(new RemoteTranscript
            {
                ChatId = shell.Chat.ChatId,
                Revision = 1,
                Turns =
                [
                    new RemoteTranscriptTurn
                    {
                        Id = "t1",
                        Items = [new RemoteTranscriptItem { Id = "i1", Kind = RemoteProtocol.ItemKinds.User, Text = "hi" }]
                    }
                ]
            });
            Pump(window);
            Assert.DoesNotContain(
                window.GetVisualDescendants().OfType<Control>(),
                c => c.Name == "NoChatPlaceholder" && c.IsVisible);
        });
    }

    /// <summary>
    /// Live progress belongs at the end of the transcript. A status strip docked to the composer
    /// steals height from the one control the user came for and duplicates the conversation.
    /// </summary>
    [Fact]
    public async Task Composer_CarriesNoProgressOrSuggestionFurniture()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            Layout(window, shell, 412, 892);

            shell.Chat.ApplyStatus(new RemoteChatStatus
            {
                ChatId = shell.Chat.ChatId,
                IsBusy = true,
                StatusText = "Running powershell"
            });
            Pump(window);

            var composer = window.GetVisualDescendants().OfType<StrataChatComposer>().Single();
            Assert.Null(composer.StatusContent);
            Assert.Equal("", composer.SuggestionA);

            // Nothing in the composer may claim vertical space to report progress or offer prompts:
            // on a phone that height comes straight out of the user's view of their own draft.
            Assert.DoesNotContain(composer.GetVisualDescendants().OfType<StrataTypingIndicator>(),
                i => i.IsVisible);
            Assert.DoesNotContain(
                composer.GetVisualDescendants().OfType<Button>(),
                b => b.Name == "PART_ActionA" && b.IsVisible);

            var typing = Assert.IsType<StrataTypingIndicator>(Named(window, "ChatTyping"));
            Assert.True(typing.IsVisible);
            Assert.Equal("Running powershell", typing.Label);
        });
    }

    /// <summary>
    /// The drawer must track the finger, not just toggle when a threshold is crossed.
    ///
    /// <para>This is the difference Adir could feel: a panel that snaps open after 56px of travel
    /// reads as a menu that happens to slide, because it never responds to the hand moving it. It
    /// also could not work at all through pointer events — once the transcript's scroll recognizer
    /// captures the pointer, Avalonia delivers moves straight to that recognizer and they stop
    /// travelling the event route, so the shell's own PointerMoved handler went silent on a real
    /// device while appearing to work against synthetic desktop events.</para>
    /// </summary>
    [Fact]
    public async Task Drawer_TracksTheFingerAndSettlesOnRelease()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            Layout(window, shell, 412, 892);

            var drawer = Drawer(window);
            var width = drawer.PanelWidth;
            Assert.True(width > 0);
            Assert.False(drawer.IsOpen);
            Assert.True(drawer.CanOpenFromAnywhere);
            Assert.False(StrataTextSelection.GetIsTouchSelectionEnabled(
                Assert.IsType<ChatDetailView>(Named(window, "ChatSurface"))));

            // Halfway through the drag the panel must be halfway out — not still closed.
            drawer.RaiseEvent(new EdgeDragEventArgs(
                EdgeDragGestureRecognizer.EdgeDragEvent, width * 0.5, width * 0.5));
            Assert.Equal(0.5, drawer.Progress, 2);
            Assert.False(drawer.IsOpen);

            // Released past halfway with no throw: settles open.
            drawer.RaiseEvent(new EdgeDragEndedEventArgs(
                EdgeDragGestureRecognizer.EdgeDragEndedEvent, 0));
            Assert.True(drawer.IsOpen);
            Assert.True(shell.IsDrawerOpen);
        });
    }

    /// <summary>
    /// A short, fast flick opens the drawer even though it never travelled past halfway — matching
    /// every native drawer. Requiring 50% of travel regardless of speed feels unresponsive.
    /// </summary>
    [Fact]
    public async Task Drawer_FlingBeatsDistance()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            Layout(window, shell, 412, 892);

            var drawer = Drawer(window);

            drawer.RaiseEvent(new EdgeDragEventArgs(
                EdgeDragGestureRecognizer.EdgeDragEvent, drawer.PanelWidth * 0.2, 40));
            drawer.RaiseEvent(new EdgeDragEndedEventArgs(
                EdgeDragGestureRecognizer.EdgeDragEndedEvent, 1600));

            Assert.True(drawer.IsOpen);

            // ...and the mirror: a fast leftward flick from nearly open still closes it.
            drawer.RaiseEvent(new EdgeDragEventArgs(
                EdgeDragGestureRecognizer.EdgeDragEvent, -drawer.PanelWidth * 0.1, 200));
            drawer.RaiseEvent(new EdgeDragEndedEventArgs(
                EdgeDragGestureRecognizer.EdgeDragEndedEvent, -1600));

            Assert.False(drawer.IsOpen);
        });
    }

    /// <summary>
    /// A docked sidebar is pinned open, so the drag must be off: swiping there would fight the
    /// transcript for a gesture that has nothing to do.
    /// </summary>
    [Fact]
    public async Task Drawer_DoesNotDragWhileDocked()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            Layout(window, shell, 412, 892);
            Assert.True(Drawer(window).IsDragEnabled);

            Layout(window, shell, 1100, 900);
            Assert.True(shell.IsDrawerDocked);
            Assert.False(Drawer(window).IsDragEnabled);
        });
    }

    [Fact]
    public async Task Drawer_DoesNotDragWhileAModalSheetOwnsTheSurface()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            Layout(window, shell, 412, 892);

            var drawer = Drawer(window);
            Assert.True(drawer.IsDragEnabled);

            shell.Chat.IsPlanOpen = true;
            Pump(window);
            Assert.False(drawer.IsDragEnabled);

            shell.Chat.IsPlanOpen = false;
            shell.IsChatActionsOpen = true;
            Pump(window);
            Assert.False(drawer.IsDragEnabled);

            shell.IsChatActionsOpen = false;
            Pump(window);
            Assert.True(drawer.IsDragEnabled);

            shell.Page = MobilePage.Library;
            shell.Library.IsEditing = true;
            Pump(window);
            Assert.False(drawer.IsDragEnabled);

            shell.Page = MobilePage.Chat;
            Pump(window);
            Assert.True(drawer.IsDragEnabled);
        });
    }

    /// <summary>
    /// The starters must be genuinely tappable, not merely present and visible.
    ///
    /// <para>They were neither: <c>NoChatPlaceholder</c> was declared before <c>ChatShell</c> in the
    /// same <c>Panel</c>, so the transcript's scroll surface painted on top and swallowed every tap.
    /// The buttons rendered perfectly and did nothing. The previous test missed it completely by
    /// invoking <c>Command.Execute</c> directly, which bypasses hit-testing — so it asserted the
    /// binding was wired while the control was unreachable by a finger. Hit-testing at the button's
    /// own centre is the only assertion that can tell those two apart.</para>
    /// </summary>
    [Fact]
    public async Task WelcomeStarters_AreActuallyHittableAndNotCoveredByTheTranscript()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            Layout(window, shell, 412, 892);

            var starter = Named(window, "WelcomeStarters")
                .GetVisualDescendants().OfType<Button>().First();

            Assert.True(starter.Bounds.Width > 0 && starter.Bounds.Height > 0);

            var centre = starter.TranslatePoint(
                new Point(starter.Bounds.Width / 2, starter.Bounds.Height / 2), window);
            Assert.NotNull(centre);

            var hit = window.InputHitTest(centre!.Value);
            Assert.NotNull(hit);

            // Whatever is on top at that point must belong to the button, not to the transcript.
            var hitVisual = Assert.IsAssignableFrom<Visual>(hit);
            Assert.True(
                ReferenceEquals(hitVisual, starter) || hitVisual.GetVisualAncestors().Contains(starter),
                $"tap at the starter's centre landed on {hitVisual.GetType().Name}, not the starter");
        });
    }

    [Fact]
    public async Task ShortLandscapeWelcomeNeverCoversTheComposerInput()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            Layout(window, shell, 892, 360);

            var input = Named(window, "Composer")
                .GetVisualDescendants()
                .OfType<TextBox>()
                .Single(control => control.Name == "PART_Input");
            var welcome = Named(window, "WelcomeSideInset");
            Assert.False(shell.IsWelcomeVisible);
            Assert.False(welcome.IsEffectivelyVisible);
            Assert.True(input.IsHitTestVisible);
            Assert.True(input.IsEffectivelyVisible);
        });
    }

    [Fact]
    public async Task DraggingAcrossAButton_DoesNotInvokeItOnRelease()
    {
        await Run((shell, window) =>
        {
            var clicks = 0;
            var row = new Button
            {
                Content = "Do not open",
                Width = 320,
                Height = 56,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            row.Click += (_, _) => clicks++;
            window.Content = row;
            Pump(window);

            var centre = row.TranslatePoint(
                new Point(row.Bounds.Width / 2, row.Bounds.Height / 2),
                window)!.Value;

            window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(centre + new Point(0, 18), RawInputModifiers.LeftMouseButton);
            window.MouseUp(centre + new Point(0, 18), MouseButton.Left, RawInputModifiers.None);
            Pump(window);

            Assert.Equal(0, clicks);

            // A real tap still works.
            window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);
            Pump(window);
            Assert.Equal(1, clicks);
        });
    }

    [Fact]
    public async Task CriticalChatActions_AreActuallyHittable()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            shell.Chat.ApplyCatalogs(new RemoteSettings
            {
                AvailableModels = ["claude-opus-5"],
                ModelReasoningEfforts = ["claude-opus-5=low,medium,high"]
            });
            shell.Chat.Model = "claude-opus-5";
            shell.Chat.PromptText = "hello";
            Layout(window, shell, 412, 892);

            foreach (var name in new[]
                     {
                         "MenuButton",
                         "NewChatButton",
                         "ComposerAttachButton",
                         "PART_SendButton",
                         "ComposerRunSettingsButton"
                     })
            {
                var control = Named(window, name);
                Assert.True(control.IsEffectivelyVisible, $"{name} is not visible");
                Assert.True(
                    control.Bounds.Width >= 48 && control.Bounds.Height >= 48,
                    $"{name} is only {control.Bounds.Width:0.#}x{control.Bounds.Height:0.#}");

                var centre = control.TranslatePoint(
                    new Point(control.Bounds.Width / 2, control.Bounds.Height / 2),
                    window);
                Assert.NotNull(centre);

                var hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(centre!.Value));
                Assert.True(
                    ReferenceEquals(hit, control) || hit.GetVisualAncestors().Contains(control),
                    $"{name}'s centre is covered by {hit.GetType().Name}");
            }
        });
    }

    /// <summary>
    /// Body text has to be sized for a phone. Strata's desktop scale is 14px and mobile views
    /// additionally hardcoded 9-13px, which at phone viewing distance reads as a shrunken desktop
    /// app — the single biggest reason it did not feel native.
    /// </summary>
    [Fact]
    public async Task Typography_UsesThePhoneScaleNotTheDesktopOne()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            Layout(window, shell, 412, 892);

            Assert.Equal(16d, window.FindResource("Font.SizeBody"));

            // Nothing anywhere in the shell may render below the meta rung.
            var tooSmall = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text) && t.FontSize < 13)
                .Select(t => $"{t.Name ?? t.Text} @ {t.FontSize}px")
                .ToList();

            Assert.Empty(tooSmall);
        });
    }

    /// <summary>
    /// Going busy must snap the transcript to the tail.
    ///
    /// <para>Tapping send is explicit intent to be at the bottom, but the view only issued the
    /// gentle "layout changed" notify — which honours a reader who has scrolled up, and is posted at
    /// Background priority. So after sending from anywhere but the very bottom, the echoed bubble
    /// and the thinking row both landed off-screen and the app looked like it had ignored the tap
    /// until the answer eventually pushed the view down.</para>
    /// </summary>
    [Fact]
    public async Task GoingBusy_SnapsTheTranscriptToTheTail()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            Layout(window, shell, 412, 892);

            // A conversation long enough that the tail is genuinely off-screen.
            shell.Chat.ApplyTranscript(new RemoteTranscript
            {
                ChatId = shell.Chat.ChatId,
                Revision = 1,
                Turns = [.. Enumerable.Range(0, 40).Select(i => new RemoteTranscriptTurn
                {
                    Id = $"t{i}",
                    Items =
                    [
                        new RemoteTranscriptItem
                        {
                            Id = $"i{i}",
                            Kind = RemoteProtocol.ItemKinds.User,
                            Text = $"message number {i} with enough text to take a line or two on a phone"
                        }
                    ]
                })]
            });
            Pump(window);

            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>()
                .Single(s => s.Name == "PART_TranscriptScroll");

            // Park the reader at the top, as if they had scrolled back through history.
            scroll.Offset = new Vector(0, 0);
            Pump(window);
            Assert.Equal(0, scroll.Offset.Y, 1);

            shell.Chat.IsBusy = true;
            Pump(window);

            Assert.True(
                scroll.Offset.Y > 0,
                "sending left the transcript parked at the top, so the thinking row was off-screen");
        });
    }

    /// <summary>
    /// Surfaces must bleed to the screen edges while only content takes the safe area.
    ///
    /// <para>The shell used to apply the insets as its own <c>Padding</c>, which pushed the drawer,
    /// the top bar and the conversation background all inside the safe area — so the app sat in a
    /// letterbox with a dead black band under the status bar instead of looking full-screen. The
    /// insets now travel on the view model and each surface pads its own content.</para>
    /// </summary>
    [Fact]
    public async Task SafeArea_InsetsContentWithoutLetterboxingTheSurfaces()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            Layout(window, shell, 412, 892);

            var statusBar = 48d;
            var shellView = window.GetVisualDescendants().OfType<MobileShellView>().Single();
            shellView.ApplyPlatformInsets(new Thickness(0, statusBar, 0, 24));
            Pump(window);

            Assert.Equal(new Thickness(0, statusBar, 0, 0), shell.SafeAreaTop);
            Assert.Equal(new Thickness(0, 0, 0, 24), shell.SafeAreaBottom);

            // The shell itself must NOT consume the inset, or everything inside it is letterboxed.
            Assert.Equal(default, shellView.Padding);

            // The chat surface still fills the window top to bottom — its background runs under the
            // status bar — while the top bar's own content is pushed clear of it.
            var chat = Named(window, "ChatSurface");
            Assert.Equal(0, chat.Bounds.Y, 1);
            Assert.Equal(892, chat.Bounds.Height, 1);

            var topBarInset = Assert.IsType<Border>(Named(window, "TopBarInset"));
            Assert.Equal(statusBar, topBarInset.Padding.Top, 1);
        });
    }

    [Fact]
    public async Task TabletopInsetsOnlyApplyKeyboardOverlapWithTheUpperPane()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            Layout(window, shell, 884, 908);
            var shellView = window.GetVisualDescendants().OfType<MobileShellView>().Single();
            shellView.Posture = FoldPosture.TabletopHorizontalHinge;
            shellView.HingePosition = 454;
            shellView.HingeSize = 24;
            Pump(window);

            shellView.ApplyPlatformInsets(new Thickness(0, 24, 0, 24), keyboardInset: 308);
            Pump(window);

            Assert.Equal(454, shell.UsableContentHeight, 1);
            Assert.Equal(0, shell.SafeAreaBottom.Bottom, 1);
            Assert.Equal(454, Named(window, "ShellRoot").Bounds.Height, 1);

            shellView.ApplyPlatformInsets(new Thickness(0, 24, 0, 24), keyboardInset: 508);
            Pump(window);

            Assert.Equal(54, shell.SafeAreaBottom.Bottom, 1);
        });
    }

    [Fact]
    public async Task AppDeactivationClearsAStaleKeyboardInset()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            Layout(window, shell, 412, 892);
            var shellView = window.GetVisualDescendants().OfType<MobileShellView>().Single();

            shellView.ApplyPlatformInsets(new Thickness(0, 48, 0, 24), keyboardInset: 320);
            Pump(window);
            Assert.True(shell.IsKeyboardOpen);
            Assert.Equal(320, shell.SafeArea.Bottom, 1);

            shellView.NotifyApplicationDeactivated();
            Pump(window);

            Assert.False(shell.IsKeyboardOpen);
            Assert.Equal(24, shell.SafeArea.Bottom, 1);
        });
    }

    /// <summary>
    /// The drawer is one scroll surface. New chat, Library, Projects and the history used to be
    /// fixed rows above a scroller that owned only the chat list, so on a short viewport the history
    /// got a few rows and a long project list simply could not be reached.
    /// </summary>
    [Fact]
    public async Task Drawer_ScrollsAsOneSurface()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);

            // Laid out wide so the drawer is docked and therefore actually measured: an invisible
            // drawer never realizes its template children, and this is a structural assertion.
            Layout(window, shell, 1100, 900);
            Assert.True(shell.IsDrawerDocked);
            Pump(window);

            var drawer = window.GetVisualDescendants().OfType<MobileDrawerView>().First();

            var scrollers = drawer.GetVisualDescendants().OfType<ScrollViewer>().ToList();
            Assert.Single(scrollers);

            // New chat, Library and Projects must all live inside it — they used to be pinned.
            var scroller = scrollers[0];
            foreach (var name in new[] { "DrawerNewChatButton", "DrawerLibraryButton", "DrawerProjects" })
            {
                var row = drawer.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name);
                Assert.True(
                    row.GetVisualAncestors().Contains(scroller),
                    $"{name} is outside the drawer's scroll surface");
            }

            // Search and the account row stay pinned, exactly like ChatGPT's drawer.
            var search = drawer.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "DrawerSearchButton");
            Assert.DoesNotContain(scroller, search.GetVisualAncestors());
        });
    }

    /// <summary>
    /// Model selection is a sheet, not the composer's anchored popup. The desktop picker is a 160dp
    /// dropdown that assumes a cursor; on a phone it opened over the keyboard as a cramped list.
    /// </summary>
    [Fact]
    public async Task ModelSelection_IsASheetOfFullWidthRows()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            Layout(window, shell, 412, 892);

            shell.Chat.ApplyCatalogs(new RemoteSettings
            {
                AvailableModels = ["auto", "claude-opus-5", "gpt-5.6"],
                ModelDisplayNames =
                [
                    "auto=Auto",
                    "claude-opus-5=Claude Opus 5",
                    "gpt-5.6=GPT 5.6"
                ]
            });
            shell.Chat.ApplyStatus(new RemoteChatStatus
            {
                ChatId = shell.Chat.ChatId,
                Model = "claude-opus-5"
            });
            Pump(window);

            var sheet = window.GetVisualDescendants().OfType<StrataBottomSheet>()
                .Single(s => s.Name == "ModelSheet");
            Assert.False(sheet.IsOpen);

            // The composer must not also carry an inline picker — two ways to do one thing, and the
            // inline one is unusable on a phone.
            var composer = window.GetVisualDescendants().OfType<StrataChatComposer>().Single();
            Assert.Contains("mobile", composer.Classes);

            // Assert the part is REALIZED and hidden, not merely absent: an unrealised template
            // would make a "does not contain a visible picker" check pass for the wrong reason.
            var inlinePicker = Assert.Single(
                composer.GetVisualDescendants().OfType<Control>(),
                c => c.Name == "PART_ModelPickerWrap");
            Assert.False(inlinePicker.IsVisible);

            shell.Chat.OpenModelSheetCommand.Execute(null);
            Pump(window);
            Assert.True(sheet.IsOpen);

            // Exactly one row is ticked, and it is the active model.
            var selected = Assert.Single(shell.Chat.ModelOptions, o => o.IsSelected);
            Assert.Equal("claude-opus-5", selected.Name);
            Assert.Equal("Claude Opus 5", selected.Label);
            Assert.Equal("Claude Opus 5", shell.Chat.ModelDisplayName);

            var rows = Named(window, "ModelSheetList").GetVisualDescendants().OfType<Button>().ToList();
            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.True(r.Bounds.Height >= 48, $"row was {r.Bounds.Height}px tall"));
            Assert.Contains(
                rows.SelectMany(row => row.GetVisualDescendants().OfType<TextBlock>()),
                text => text.Text == "Claude Opus 5");
            Assert.DoesNotContain(
                rows.SelectMany(row => row.GetVisualDescendants().OfType<TextBlock>()),
                text => text.Text == "claude-opus-5");
            Assert.True(
                rows.Single(row => row.DataContext is PickerOption { IsSelected: true }).IsFocused,
                "focus must move into the modal sheet, not remain on the composer behind it");

            // Picking dismisses; a sheet that lingers after a choice reads as broken.
            shell.Chat.PickModelCommand.Execute("gpt-5.6");
            Assert.Equal("gpt-5.6", shell.Chat.Model);
            Assert.False(sheet.IsOpen);
        });
    }

    /// <summary>
    /// Nothing a finger has to hit may be smaller than a fingertip.
    ///
    /// <para>Sweeps the real rendered tree rather than the stylesheet, so it catches a target that
    /// is small because of its content or its container as well as one that declares a small size.
    /// The floor is Android's documented 48dp minimum; padding around a 40dp button is not part of
    /// its hit target unless the parent itself handles the input.</para>
    /// </summary>
    [Theory]
    [InlineData(MobilePage.Chat)]
    [InlineData(MobilePage.Library)]
    [InlineData(MobilePage.Settings)]
    [InlineData(MobilePage.Search)]
    public async Task EveryTapTarget_IsAtLeastAFingertipSized(MobilePage page)
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            Layout(window, shell, 412, 892);
            shell.Page = page;
            Pump(window);

            const double floor = 48;

            var tooSmall = window.GetVisualDescendants().OfType<Control>()
                .Where(control => control is Button or TextBox or ToggleSwitch or ListBoxItem or Slider)
                .Where(control => control.IsVisible && control.IsEffectivelyVisible)
                .Where(control => control.Bounds.Width > 0 && control.Bounds.Height > 0)
                .Where(control => control.Bounds.Width < floor || control.Bounds.Height < floor)
                // ScrollBar repeat buttons are page-jump internals of a drag affordance, not
                // targets the user aims at.
                .Where(control => control is not RepeatButton)
                .Select(control =>
                    $"{control.Name ?? control.Classes.FirstOrDefault() ?? "?"} " +
                    $"{control.Bounds.Width:0.#}x{control.Bounds.Height:0.#}px")
                .Distinct()
                .ToList();

            Assert.Empty(tooSmall);
        });
    }

    /// <summary>
    /// The light variant has to actually change the palette.
    ///
    /// <para>It did nothing, for two compounding reasons. The mobile colours were merged flat into
    /// <c>Application.Resources</c>, which outranks a theme's ThemeDictionaries — so the dark set
    /// applied in both variants. Splitting them into ThemeDictionaries declared directly on the
    /// outer application dictionary then only half-worked: Strata-owned surfaces turned white while
    /// anything reading a Lumi token stayed black. They have to be nested inside
    /// <c>MergedDictionaries</c>, matching StrataTheme's own structure.</para>
    /// </summary>
    [Fact]
    public async Task LightTheme_ActuallyRepaintsTheSurfaces()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            Layout(window, shell, 412, 892);

            var chat = Named(window, "ChatSurface");

            static Color SurfaceOf(Control control) =>
                Assert.IsType<SolidColorBrush>(control.GetValue(TemplatedControl.BackgroundProperty)).Color;

            window.RequestedThemeVariant = ThemeVariant.Dark;
            Pump(window);
            var dark = SurfaceOf(chat);

            window.RequestedThemeVariant = ThemeVariant.Light;
            Pump(window);
            var light = SurfaceOf(chat);


            Assert.NotEqual(dark, light);

            // Not just different — actually light. A half-applied switch left this black.
            Assert.True(
                light.R > 0xC0 && light.G > 0xC0 && light.B > 0xC0,
                $"light surface was #{light.R:X2}{light.G:X2}{light.B:X2}, which is not a light colour");
            Assert.True(
                dark.R < 0x40 && dark.G < 0x40 && dark.B < 0x40,
                $"dark surface was #{dark.R:X2}{dark.G:X2}{dark.B:X2}");
        });
    }

    /// <summary>
    /// Model, effort and context are one run-settings affordance. Three permanent controls recreated
    /// a desktop toolbar and duplicated the model in the header.
    /// </summary>
    [Fact]
    public async Task Composer_ShowsRunSettingsAndOpensTheSheet()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            Layout(window, shell, 412, 892);

            shell.Chat.ApplyCatalogs(new RemoteSettings
            {
                AvailableModels = ["auto", "claude-opus-5"],
                ModelDisplayNames = ["auto=Auto", "claude-opus-5=Claude Opus 5"],
                ModelReasoningEfforts = ["claude-opus-5=low,medium,high"],
                ModelContextWindowTiers = ["claude-opus-5=Default,Long context"]
            });
            shell.Chat.ApplyStatus(new RemoteChatStatus
            {
                ChatId = shell.Chat.ChatId,
                Model = "claude-opus-5",
                Quality = "high",
                QualityLevels = ["low", "medium", "high"]
            });
            Pump(window);

            Assert.Equal("Claude Opus 5 · High", shell.Chat.RunSettingsSummary);

            var pill = Assert.IsType<Button>(Named(window, "ComposerRunSettingsButton"));
            Assert.True(pill.IsVisible);
            Assert.True(pill.Bounds.Height >= 48, $"pill was {pill.Bounds.Height}px tall");

            var sheet = window.GetVisualDescendants().OfType<StrataBottomSheet>()
                .Single(s => s.Name == "RunSettingsSheet");
            Assert.False(sheet.IsOpen);

            pill.Command!.Execute(pill.CommandParameter);
            Pump(window);
            Assert.True(sheet.IsOpen);

            // A model with no effort/context choice still leaves the stable settings entry point.
            shell.Chat.ApplyStatus(new RemoteChatStatus
            {
                ChatId = shell.Chat.ChatId,
                Model = "auto",
                QualityLevels = []
            });
            Assert.Equal("Auto", shell.Chat.RunSettingsSummary);
            Assert.False(Named(window, "RunSettingsEffortButton").IsEnabled);
            Assert.False(Named(window, "RunSettingsContextButton").IsEnabled);
        });
    }

    /// <summary>The composer has to say what it is for while it is empty.</summary>
    [Fact]
    public async Task Composer_ShowsItsPlaceholderWhileEmpty()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            Layout(window, shell, 412, 892);

            var composer = window.GetVisualDescendants().OfType<StrataChatComposer>().Single();
            var watermark = composer.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Name == "PART_Watermark");

            Assert.True(watermark.IsVisible);
            Assert.False(string.IsNullOrWhiteSpace(watermark.Text));

            shell.Chat.PromptText = "typing";
            Pump(window);
            Assert.False(watermark.IsVisible);
        });
    }

    [Fact]
    public async Task CompactComposer_KeepsAttachSettingsAndSendOnOneRow()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            Layout(window, shell, 360, 780);
            shell.Chat.ApplyCatalogs(new RemoteSettings
            {
                PreferredModel = "claude-opus-5",
                AvailableModels = ["claude-opus-5"],
                ModelDisplayNames = ["claude-opus-5=Claude Opus 5"],
                ModelReasoningEfforts = ["claude-opus-5=low,medium,high"],
                ModelContextWindowTiers = ["claude-opus-5=Default,Long"]
            });
            Pump(window);

            var composer = Named(window, "Composer");
            var attach = Named(window, "ComposerAttachButton");
            var settings = Named(window, "ComposerRunSettingsButton");
            var send = Named(window, "PART_SendButton");

            Assert.True(composer.Bounds.Height <= 120, $"compact composer was {composer.Bounds.Height:0.#}dp tall");
            Assert.True(Math.Abs(attach.Bounds.Y - settings.Bounds.Y) < 1);
            Assert.True(Math.Abs(attach.Bounds.Y - send.Bounds.Y) < 1);
        });
    }

    [Fact]
    public async Task LongStarterText_TrimsInsteadOfClipping()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            Layout(window, shell, 360, 780);
            shell.Chat.ApplyStatus(new RemoteChatStatus
            {
                Suggestions =
                [
                    "Research an intentionally very long topic whose label cannot fit on one compact phone row"
                ]
            });
            Pump(window);

            var starterText = Named(window, "WelcomeStarters")
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .First(text => text.Text?.StartsWith("Research an intentionally", StringComparison.Ordinal) == true);

            Assert.Equal(TextTrimming.CharacterEllipsis, starterText.TextTrimming);
            Assert.Equal(1, starterText.MaxLines);
        });
    }

    /// <summary>
    /// Theme is a three-way, and "System" has to mean Avalonia's <see cref="ThemeVariant.Default"/>
    /// — that is what defers to the OS and re-resolves when the OS flips on its own schedule.
    /// </summary>
    [Theory]
    [InlineData(MobileShellViewModel.ThemePreference.Light)]
    [InlineData(MobileShellViewModel.ThemePreference.Dark)]
    [InlineData(MobileShellViewModel.ThemePreference.System)]
    public async Task ThemePreference_MapsOntoTheRightVariant(MobileShellViewModel.ThemePreference preference)
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            Layout(window, shell, 412, 892);

            shell.SelectThemeCommand.Execute(preference.ToString());
            Assert.Equal(preference, shell.Theme);

            // Exactly one segment reads as selected.
            var selected = new[] { shell.IsSystemTheme, shell.IsLightTheme, shell.IsDarkTheme }.Count(on => on);
            Assert.Equal(1, selected);

            var expected = preference switch
            {
                MobileShellViewModel.ThemePreference.Light => ThemeVariant.Light,
                MobileShellViewModel.ThemePreference.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };

            // The mapping, not the app wiring: the headless harness skips initialization, so the
            // instance hook that normally applies this never runs here.
            Assert.Equal(expected, App.VariantFor(preference));
        });
    }

    /// <summary>
    /// The welcome panel is centred in the remaining space, so when the IME lifts the composer it
    /// landed directly on top of it. Someone who is typing has also stopped needing a greeting.
    ///
    /// <para>Asserted on EFFECTIVE visibility, not each control's own flag: hiding the starters and
    /// the subtitle individually left the orb and the "Good afternoon" heading behind — which is
    /// exactly what the composer then collided with — while a test reading their own IsVisible
    /// would have passed.</para>
    /// </summary>
    [Fact]
    public async Task WelcomePanel_GetsOutOfTheWayWhenTheKeyboardOpens()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            Layout(window, shell, 412, 892);

            var starters = Named(window, "WelcomeStarters");
            var greeting = Named(window, "GreetingText");
            Assert.True(starters.IsEffectivelyVisible);
            Assert.True(greeting.IsEffectivelyVisible);

            var shellView = window.GetVisualDescendants().OfType<MobileShellView>().Single();
            shellView.ApplyPlatformInsets(new Thickness(0, 48, 0, 24), keyboardInset: 320);
            Pump(window);

            Assert.True(shell.IsKeyboardOpen);
            Assert.False(starters.IsEffectivelyVisible);
            Assert.False(greeting.IsEffectivelyVisible, "the greeting must not sit above the lifted composer");

            shellView.ApplyPlatformInsets(new Thickness(0, 48, 0, 24));
            Pump(window);
            Assert.False(shell.IsKeyboardOpen);
            Assert.True(starters.IsEffectivelyVisible);
            Assert.True(greeting.IsEffectivelyVisible);
        });
    }

    /// <summary>
    /// Effort is an ordered scale, so it gets a slider. Its bounds come from what the model actually
    /// supports, which on a not-yet-created chat has to come from the catalog rather than a status.
    /// </summary>
    [Fact]
    public async Task EffortSlider_TracksTheModelsOwnLevels()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            Layout(window, shell, 412, 892);

            shell.Chat.ApplyCatalogs(new RemoteSettings
            {
                AvailableModels = ["auto", "claude-opus-5"],
                ModelReasoningEfforts = ["claude-opus-5=low,medium,high"]
            });

            // "auto" supports none, so the settings row explains the limitation instead of the
            // entire run-settings entry disappearing.
            shell.Chat.Model = "auto";
            Pump(window);
            Assert.False(shell.Chat.HasQualityLevels);
            shell.Chat.OpenRunSettingsSheetCommand.Execute(null);
            Pump(window);
            Assert.False(Named(window, "RunSettingsEffortButton").IsEnabled);
            shell.Chat.IsRunSettingsSheetOpen = false;
            Pump(window);

            shell.Chat.Model = "claude-opus-5";
            Pump(window);

            Assert.True(shell.Chat.HasQualityLevels);
            shell.Chat.OpenRunSettingsSheetCommand.Execute(null);
            Pump(window);
            Assert.True(Named(window, "RunSettingsEffortButton").IsEnabled);
            shell.Chat.IsRunSettingsSheetOpen = false;
            Pump(window);
            Assert.Equal(2, shell.Chat.EffortMax);

            // An OPEN chat must offer them too. The desktop only reports levels for the chat it
            // currently has active, so a chat opened from the list arrived with none and the pill
            // stayed hidden — but which efforts exist is a property of the model, not the chat.
            shell.Chat.Reset(Guid.NewGuid(), "Existing");
            shell.Chat.Model = "claude-opus-5";
            Pump(window);

            Assert.True(shell.Chat.HasQualityLevels, "an existing chat must offer its model's efforts too");

            // Dragging the slider selects by position, and the label follows.
            shell.Chat.EffortIndex = 2;
            Assert.Equal("high", shell.Chat.Quality);
            Assert.Equal("High", shell.Chat.EffortLabel);

            shell.Chat.OpenEffortSheetCommand.Execute(null);
            Pump(window);
            var sheet = window.GetVisualDescendants().OfType<StrataBottomSheet>()
                .Single(s => s.Name == "EffortSheet");
            Assert.True(sheet.IsOpen);
            Assert.True(
                Named(window, "EffortSlider").IsFocused,
                "the slider must own focus while its modal sheet is open");
        });
    }

    [Fact]
    public async Task NewChat_CanChooseContextWindowBeforeFirstSend()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            Layout(window, shell, 412, 892);
            shell.Chat.ApplyCatalogs(new RemoteSettings
            {
                PreferredModel = "claude-opus-5",
                AvailableModels = ["claude-opus-5"],
                ModelDisplayNames = ["claude-opus-5=Claude Opus 5"],
                ModelReasoningEfforts = ["claude-opus-5=low,medium,high"],
                ModelContextWindowTiers = ["claude-opus-5=Default,Long context"]
            });

            Assert.Equal(Guid.Empty, shell.Chat.ChatId);
            Assert.True(shell.Chat.HasQualityLevels);
            Assert.True(shell.Chat.HasContextWindowTiers);

            shell.Chat.OpenRunSettingsSheetCommand.Execute(null);
            Pump(window);
            Assert.True(Named(window, "RunSettingsEffortButton").IsEnabled);
            Assert.True(Named(window, "RunSettingsContextButton").IsEnabled);

            shell.Chat.OpenContextFromRunSettingsCommand.Execute(null);
            Pump(window);
            Assert.True(
                window.GetVisualDescendants().OfType<StrataBottomSheet>()
                    .Single(sheet => sheet.Name == "ContextSheet")
                    .IsOpen);
            Assert.Equal(["Default", "Long context"], shell.Chat.ContextWindowTiers);

            shell.Chat.PickContextTierCommand.Execute("Long context");
            Assert.Equal("Long context", shell.Chat.ContextWindowTier);
        });
    }

    /// <summary>
    /// After tapping send, the progress indicator must be ON SCREEN — not merely visible somewhere
    /// in the scroll content.
    ///
    /// <para>The indicator lives at the end of the transcript, so in a conversation long enough to
    /// scroll it sits below the fold. If the viewport does not follow, the user taps send and sees
    /// absolutely nothing change until the server's reply eventually pushes the view down, which
    /// reads as a long delay even though the view model flipped instantly.</para>
    /// </summary>
    [Fact]
    public async Task Sending_BringsTheProgressIndicatorIntoTheViewport()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            Layout(window, shell, 412, 892);

            // A conversation tall enough that the tail is well below the fold.
            var transcript = new RemoteTranscript { ChatId = shell.Chat.ChatId, Title = "Long", Revision = 1 };
            for (var i = 0; i < 40; i++)
            {
                transcript.Turns.Add(new RemoteTranscriptTurn
                {
                    Items =
                    [
                        new RemoteTranscriptItem { Kind = "user", Text = $"question {i}" },
                        new RemoteTranscriptItem { Kind = "assistant", Text = $"answer {i}" }
                    ]
                });
            }

            shell.Chat.ApplyTranscript(transcript);
            Pump(window);

            // Scroll away from the tail, the way someone re-reading earlier context would.
            var scroller = window.GetVisualDescendants().OfType<ScrollViewer>()
                .First(s => s.GetVisualDescendants().Any(d => d.Equals(Named(window, "Transcript"))));
            scroller.Offset = new Vector(0, 0);
            Pump(window);

            // Drive the same status transition an external/remote turn uses. The view model owns the
            // "waiting for visible activity" state; setting IsBusy directly would bypass it.
            shell.Chat.ApplyStatus(new RemoteChatStatus
            {
                ChatId = shell.Chat.ChatId,
                IsBusy = true
            });
            Pump(window);

            var indicator = Named(window, "ChatTyping");
            Assert.True(indicator.IsVisible, "the indicator should be shown while Lumi is working");

            // Its position in the window is what the user actually experiences.
            var topLeft = indicator.TranslatePoint(new Point(0, 0), window);
            Assert.NotNull(topLeft);

            var y = topLeft!.Value.Y;
            Assert.True(
                y >= 0 && y <= window.Height,
                $"the progress indicator is off-screen at y={y:F0} in a {window.Height:F0}px window — "
                + "the user would see nothing happen after tapping send");

            // Sending then appends the optimistic user turn. That grows the tail after the first
            // busy-state jump, so the view must follow again after the new container is measured.
            var pending = new TranscriptTurnViewModel("__pending_echo__");
            pending.Items.Add(new UserTurnItemViewModel(new RemoteTranscriptItem
            {
                Kind = RemoteProtocol.ItemKinds.User,
                Text = "new optimistic message"
            }));
            shell.Chat.Turns.Add(pending);
            Pump(window);

            topLeft = indicator.TranslatePoint(new Point(0, 0), window);
            Assert.NotNull(topLeft);
            y = topLeft!.Value.Y;
            Assert.True(
                y >= 0 && y <= window.Height,
                $"the optimistic message pushed the progress indicator off-screen to y={y:F0}");
        });
    }

    /// <summary>
    /// The theme picker is three segments wide. In a side-by-side settings row that leaves the
    /// description a few words per line, which is exactly the "shrunken desktop" look. Stacking puts
    /// the control on its own full-width line under the labels.
    /// </summary>
    [Fact]
    public async Task ThemeSetting_StacksItsControlBelowTheLabelsAtFullWidth()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            Layout(window, shell, 412, 892);

            shell.ShowPageCommand.Execute("Settings");
            Pump(window);

            var options = Named(window, "ThemeOptions");
            var labels = window.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Text == "Theme");

            // Below, not beside.
            var optionsTop = options.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            var labelBottom = labels.TranslatePoint(new Point(0, labels.Bounds.Height), window)!.Value.Y;
            Assert.True(
                optionsTop >= labelBottom,
                $"theme options start at y={optionsTop:F0} but the label still runs to y={labelBottom:F0}");

            // And it uses the row, rather than being squeezed into a right-hand column.
            Assert.True(
                options.Bounds.Width > 260,
                $"theme options only got {options.Bounds.Width:F0}px of a {window.Width:F0}px screen");

            // Every segment stays a real touch target.
            foreach (var name in new[] { "ThemeSystemButton", "ThemeLightButton", "ThemeDarkButton" })
            {
                var b = Named(window, name);
                Assert.True(b.Bounds.Height >= 44, $"{name} is only {b.Bounds.Height:F0}px tall");
            }
        });
    }

    [Fact]
    public async Task CompactWidth_TheChatGetsTheWholeSurface()
    {
        await Run((shell, window) =>
        {
            Pair(shell);
            OpenChat(shell);
            Layout(window, shell, 412, 892);

            // No rail, no tab bar, no list column: on a phone the conversation owns every pixel.
            var chat = Named(window, "ChatSurface");
            Assert.True(chat.Bounds.Width > 400, $"chat was {chat.Bounds.Width}px wide, expected full width");
        });
    }
}
