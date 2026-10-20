using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class OpenCanvasDesignTests
{
    [Fact]
    public Task DrawableFoldCreaseLetsTheConversationFillTheReleasedPane() =>
        RunAsync(1100, false, async (model, view, window) =>
        {
            view.HingePosition = 550;
            view.Posture = Lumi.Mobile.Layout.FoldPosture.BookVerticalHinge;
            Pump(window);
            Assert.False(model.HasHingeGap);
            model.IsNavigationOpen = false;
            await SettleAsync(window);
            var chat = Required<ChatDetailView>(view, "ChatSurface");
            Assert.Equal(0, chat.TranslatePoint(default, view)!.Value.X, precision: 1);
            Assert.Equal(view.Bounds.Width, chat.Bounds.Width, precision: 1);
        });

    [Fact]
    public async Task StandardScrollbarPropertyLeavesNonMobileShellsUnchanged()
    {
        using var session = HeadlessMobileSession.Start();
        await session.Dispatch(() =>
        {
            var shell = new StrataChatShell { Transcript = new Border { Height = 1800 } };
            var window = new Window { Width = 360, Height = 780, Content = shell };
            try
            {
                window.Show();
                shell.ApplyTemplate();
                Pump(window);
                var scroll = shell.TranscriptScrollViewer!;
                Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
                Assert.Contains(scroll.GetVisualDescendants().OfType<ScrollBar>(), bar => bar.IsEffectivelyVisible);
                ScrollViewer.SetVerticalScrollBarVisibility(shell, ScrollBarVisibility.Hidden);
                Pump(window);
                Assert.Equal(ScrollBarVisibility.Hidden, scroll.VerticalScrollBarVisibility);
                AssertNoVisibleRails(scroll);
                Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(360, false)]
    [InlineData(360, true)]
    [InlineData(1100, false)]
    [InlineData(1100, true)]
    public Task CanvasUsesTypographyAndFloatingActionsInsteadOfFramedSections(double width, bool dark) =>
        RunAsync(width, dark, async (model, view, window) =>
        {
            var chat = Required<ChatDetailView>(view, "ChatSurface");
            var drawer = Required<MobileDrawerView>(view, "DrawerContent");
            var canvas = ColorOf(chat.Background);
            var navigation = ColorOf(drawer.Background);
            Assert.Equal(255, canvas.A);
            Assert.Equal(canvas, ColorOf(Required<Border>(view, "ConversationFrame").Background));
            // The panes share the desktop's graphite canvas; a hairline divides them, not a second
            // colour, and the canvas itself carries no hue.
            Assert.Equal(navigation, canvas);
            Assert.True(Math.Abs(canvas.R - canvas.G) <= 3 && Math.Abs(canvas.G - canvas.B) <= 4,
                $"The canvas is neutral graphite, not tinted: {canvas}.");
            if (dark)
                Assert.InRange(canvas.R, (byte)0, (byte)16);
            else
                Assert.InRange(canvas.R, (byte)240, byte.MaxValue);
            Assert.Equal(default, Required<Border>(chat, "TopBarInset").BorderThickness);
            AssertTransparent(Required<Border>(chat, "TopBarInset").Background);
            var controlsButton = Required<Button>(chat, "HeaderDetailsButton");
            AssertGlass(controlsButton);
            // New chat is the top right of the chat's bar in every layout, balancing the back or sidebar
            // button; a docked list leaves it to the chat's bar.
            var newChat = Required<Button>(chat, "NewChatButton");
            Assert.True(newChat.IsEffectivelyVisible);
            Assert.Equal(!model.CanDockDrawer, Required<Button>(drawer, "DrawerNewChatButton").IsVisible);
            var menuButton = Required<Button>(chat, "MenuButton");
            var headerActions = new[] { menuButton, newChat };
            foreach (var action in headerActions)
            {
                AssertGlass(action);
                Assert.False(action.ClipToBounds);
                Assert.True(action.Bounds.Height >= 48);
                Assert.True(action.Bounds.Width >= 48);
                var surface = Required<Border>(action, "PART_Surface");
                Assert.Equal(44, surface.Bounds.Width, precision: 1);
                Assert.Equal(44, surface.Bounds.Height, precision: 1);
                Assert.True(surface.BoxShadow.Count > 0);
            }
            Assert.True(newChat.Bounds.Height >= 48 && newChat.Bounds.Width >= 48);
            // The title pill names the chat on one line, its project in the room the title leaves, as
            // the desktop's does; the model answering lives beside the composer's send.
            Assert.Equal(model.ChatDetailsTitle, Required<TextBlock>(chat, "HeaderTitleText").Text);
            Assert.DoesNotContain(chat.GetVisualDescendants().OfType<Control>(), control => control.Name == "HeaderModelText");
            Assert.Equal(model.Chat.ProjectName ?? "", Required<TextBlock>(chat, "HeaderProjectText").Text ?? "");
            // The title pill fills the bar between its buttons, so the bar is symmetrical.
            double Left(Control control) => control.TranslatePoint(default, chat)!.Value.X;
            double Right(Control control) => control.TranslatePoint(new Point(control.Bounds.Width, 0), chat)!.Value.X;
            Assert.Equal(Left(controlsButton) - Right(menuButton), Left(newChat) - Right(controlsButton), 1);
            Assert.Equal(Left(menuButton), chat.Bounds.Width - Right(newChat), 1);
            Assert.Equal("HeaderControlsIcon", Assert.Single(controlsButton.GetVisualDescendants().OfType<PathIcon>()).Name);
            Assert.True(Required<Button>(chat, "MenuButton").IsEffectivelyVisible);
            var composer = Required<StrataChatComposer>(chat, "Composer");
            Assert.False(composer.ClipToBounds);
            var composerSurface = Required<Border>(composer, "PART_Root");
            AssertTranslucent(composerSurface.Background);
            Assert.True(Required<StrataBackdropBlur>(composer, "PART_Backdrop").IsVisible);
            Assert.True(composerSurface.BoxShadow.Count > 0);
            var bubble = chat.GetVisualDescendants().OfType<Border>()
                .First(border => border.Name == "PART_Bubble");
            Assert.Equal(default, bubble.BorderThickness);

            var menu = Required<Button>(chat, "MenuButton");
            var menuTint = TintOf(menu.Background);
            model.IsNavigationOpen = true;
            await SettleAsync(window);
            Assert.Equal(menuTint, TintOf(menu.Background));
            Assert.DoesNotContain("active", menu.Classes);
            Assert.Equal(!model.CanDockDrawer, Required<Button>(drawer, "DrawerNewChatButton").IsEffectivelyVisible);
            Assert.DoesNotContain(drawer.GetVisualDescendants().OfType<Button>(), button => button.Name == "PlacesNewChatButton");
            // The list's head: Lumi's name as the place's title with the title bar's glass buttons beside it,
            // and the project lens below in the same glass, built as the chat's title pill is.
            Assert.Equal("Lumi", Required<TextBlock>(drawer, "DrawerTitle").Text);
            var search = Required<Button>(drawer, "DrawerSearchButton");
            AssertGlass(search);
            Assert.Equal(44, Required<Border>(search, "PART_Surface").Bounds.Width, precision: 1);
            var switcher = Required<Button>(drawer, "DrawerProjectButton");
            AssertGlass(switcher);
            Assert.Equal(Required<Border>(controlsButton, "PART_Surface").Bounds.Height,
                Required<Border>(switcher, "PART_Surface").Bounds.Height, precision: 1);
            // The places are the desktop's navigation pill at touch size, glass of its own.
            AssertTranslucent(Required<Border>(drawer, "PlacesSurface").Background);
            Assert.True(Required<StrataBackdropBlur>(drawer, "PlacesFrost").IsEffectivelyVisible);
            Assert.Contains("active", Required<Button>(drawer, "PlacesChatsButton").Classes);
            // No brand bar or close button: the list is a place of its own.
            Assert.DoesNotContain(drawer.GetVisualDescendants().OfType<Control>(),
                control => control.Name is "DrawerBrandTitle" or "DrawerBrandSurface" or "DrawerCloseButton");
            Assert.DoesNotContain(drawer.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Conversations");
            Assert.DoesNotContain(drawer.GetVisualDescendants().OfType<Control>(), control => control.Name == "DrawerToggleButton");
            foreach (var row in drawer.GetVisualDescendants().OfType<Button>()
                         .Where(button => button.Classes.Contains("drawer-chat")))
                Assert.Equal(default, row.BorderThickness);
            foreach (var row in drawer.GetVisualDescendants().OfType<Button>()
                         .Where(button => button.DataContext is ChatListItemViewModel chatRow
                                          && !chatRow.IsRunning && !chatRow.HasBackgroundActivity))
            {
                // One line: the title alone, as on the desktop; only a pinned chat adds its quiet pin.
                var item = (ChatListItemViewModel)row.DataContext!;
                Assert.True(Required<TextBlock>(row, "ChatRowTitle").IsEffectivelyVisible);
                Assert.DoesNotContain(row.GetVisualDescendants().OfType<Control>(),
                    control => control.Name is "ChatRowDetail" or "ChatRowPreview" or "ChatRowProject" or "ChatRowTime");
                Assert.DoesNotContain(row.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.IsEffectivelyVisible && text.Name != "ChatRowTitle");
                Assert.DoesNotContain(row.GetVisualDescendants().OfType<PathIcon>(),
                    icon => icon.IsEffectivelyVisible && !(icon.Name == "ChatRowPin" && item.IsPinned));
            }

            model.ShowPageCommand.Execute("Settings");
            await SettleAsync(window);
            var settings = Required<MobileSettingsView>(view, "SettingsPage");
            // Settings groups are the desktop's graphite cards, not text floating on the canvas.
            AssertCard(Required<Border>(settings, "SettingsConnectionCard"));
            // The PC's state is a small dot beside it: green only while the PC answers.
            var dot = Required<Border>(settings, "SettingsConnectionDot");
            Assert.Equal(new Size(7, 7), dot.Bounds.Size);
            model.IsConnected = true;
            model.IsHostReady = true;
            Pump(window);
            Assert.Contains("live", dot.Classes);
            var live = ColorOf(dot.Background);
            Assert.True(live.G > live.R + 60 && live.G > live.B + 40, $"A live PC shows a green dot, not {live}.");
            model.IsHostReady = false;
            Pump(window);
            Assert.DoesNotContain("live", dot.Classes);
            Assert.NotEqual(live, ColorOf(dot.Background));
            model.OpenSettingsSectionCommand.Execute("Connection");
            await SettleAsync(window);
            var groups = settings.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Name == "PART_Card"
                                 && border.TemplatedParent is StrataSettingGroup).ToArray();
            Assert.NotEmpty(groups);
            foreach (var group in groups)
                AssertCard(group);
            var refresh = Required<Button>(settings, "RefreshButton");
            var label = refresh.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "Refresh");
            Assert.Equal(ColorOf(refresh.Foreground), ColorOf(label.Foreground));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SinglePresenceMovesFromWelcomeToWorkAndBackToTheComposer(bool dark) =>
        RunAsync(412, dark, async (model, view, window) =>
        {
            var presence = Required<StrataPresence>(view, "Presence");
            var composer = Required<StrataChatComposer>(view, "Composer");
            Assert.Equal(PresenceState.Idle, presence.State);
            Assert.False(presence.IsHitTestVisible);
            Assert.Equal(dark ? 0.48 : 0.18, presence.Intensity, 3);
            Assert.False(presence.Halo);
            AssertNoCompanion();
            AssertAtComposer();
            var restingFocus = presence.FocusPoint;

            var originalHeight = composer.Height;
            composer.Height = 180;
            await SettleAsync(window);
            AssertAtComposer();
            composer.Height = originalHeight;
            await SettleAsync(window);
            AssertAtComposer();

            model.Chat.ApplyStatus(new RemoteChatStatus { ChatId = model.Chat.ChatId, IsBusy = true });
            Pump(window);
            Assert.Contains(presence.State, new[] { PresenceState.Thinking, PresenceState.Streaming });
            Assert.Equal(dark ? 0.48 : 0.18, presence.Intensity, 3);
            Assert.True(presence.FocusPoint.Y < restingFocus.Y - 0.10);
            AssertNoCompanion();
            model.Chat.ApplyStatus(new RemoteChatStatus
            {
                ChatId = model.Chat.ChatId, IsBusy = true, IsStreaming = true
            });
            Pump(window);
            Assert.Equal(PresenceState.Streaming, presence.State);
            AssertNoCompanion();
            model.Chat.ApplyTranscript(new RemoteTranscript
            {
                ChatId = model.Chat.ChatId, Revision = 2, IsLatestWindow = true,
                Turns =
                [
                    new()
                    {
                        Id = "completed",
                        Items = [new() { Id = "done", Kind = RemoteProtocol.ItemKinds.Assistant, Text = "Done" }]
                    }
                ],
                Status = new RemoteChatStatus { ChatId = model.Chat.ChatId, IsBusy = false }
            });
            Pump(window);
            Assert.Equal(PresenceState.Idle, presence.State);
            Assert.Equal(dark ? 0.48 : 0.18, presence.Intensity, 3);
            AssertAtComposer();
            AssertNoCompanion();

            window.RequestedThemeVariant = dark ? ThemeVariant.Light : ThemeVariant.Dark;
            await SettleAsync(window);
            Assert.Equal(dark ? 0.18 : 0.48, presence.Intensity, 3);
            AssertNoCompanion();

            model.Chat.Reset(Guid.Empty, "New conversation");
            Pump(window);
            Assert.Same(presence, Required<StrataPresence>(view, "Presence"));
            Assert.True(presence.Halo);
            Assert.InRange(presence.FocusPoint.Y, 0.30, 0.74);
            AssertNoCompanion();

            model.IsConnected = false;
            Pump(window);
            Assert.Equal(PresenceState.Dormant, presence.State);
            AssertNoCompanion();

            void AssertNoCompanion() =>
                Assert.Equal(0, Assert.IsType<Border>(Assert.IsType<Border>(presence.Children[^1]).Child).Opacity);

            void AssertAtComposer()
            {
                var anchor = composer.TranslatePoint(
                    new Point(composer.Bounds.Width / 2, composer.Bounds.Height * 0.28), presence)!.Value;
                Assert.InRange(Math.Abs(presence.FocusPoint.X - anchor.X / presence.Bounds.Width), 0, 0.014);
                Assert.InRange(Math.Abs(presence.FocusPoint.Y - anchor.Y / presence.Bounds.Height), 0, 0.014);
            }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ConversationSeparationOnlyFramesVisibleDockedNavigation(bool dark) =>
        RunAsync(1100, dark, async (model, view, window) =>
        {
            var frame = Required<Border>(view, "ConversationFrame");
            var edge = Required<Border>(view, "ConversationSeparation");
            var drawer = Required<StrataNavigationDrawer>(view, "NavDrawer");
            Assert.True(edge.IsEffectivelyVisible);
            Assert.False(edge.IsHitTestVisible);
            Assert.Equal(1, edge.Bounds.Width);
            Assert.Equal(frame.Bounds.Height, edge.Bounds.Height);
            Assert.Equal(frame.TranslatePoint(default, view), edge.TranslatePoint(default, view));
            Assert.Equal(drawer.PanelWidth, edge.TranslatePoint(default, view)!.Value.X, precision: 1);
            // A hairline, as the hinge draws on the Fold: no shadow smearing into either pane.
            for (var shadow = 0; shadow < edge.BoxShadow.Count; shadow++)
                Assert.Equal(0, edge.BoxShadow[shadow].Color.A);
            Assert.True(ColorOf(edge.Background).A > 0);
            Assert.Equal(0, frame.BorderThickness.Left);

            model.IsNavigationOpen = false;
            await SettleAsync(window);
            Assert.False(edge.IsEffectivelyVisible);
            Assert.Equal(0, frame.TranslatePoint(default, view)!.Value.X, precision: 1);
            Assert.Equal(view.Bounds.Width, frame.Bounds.Width, precision: 1);

            model.IsNavigationOpen = true;
            await SettleAsync(window);
            Assert.True(edge.IsEffectivelyVisible);
            Assert.Equal(frame.TranslatePoint(default, view), edge.TranslatePoint(default, view));

            view.HingePosition = 550;
            view.HingeSize = 24;
            view.Posture = Lumi.Mobile.Layout.FoldPosture.BookVerticalHinge;
            await SettleAsync(window);
            Assert.True(model.HasHingeGap);
            Assert.False(edge.IsEffectivelyVisible);

            view.Posture = Lumi.Mobile.Layout.FoldPosture.Flat;
            view.HingeSize = 0;
            window.Width = 360;
            await SettleAsync(window);
            model.IsNavigationOpen = true;
            await SettleAsync(window);
            Assert.False(model.CanDockDrawer);
            Assert.False(edge.IsEffectivelyVisible);
        });

    [Theory]
    [InlineData(360)]
    [InlineData(1100)]
    public Task HiddenRailsDoNotDisableTranscriptOrDrawerScrolling(double width) =>
        RunAsync(width, false, async (model, view, window) =>
        {
            var chat = Required<ChatDetailView>(view, "ChatSurface");
            var shell = Required<StrataChatShell>(chat, "ChatShell");
            var transcript = shell.TranscriptScrollViewer!;
            Assert.True(transcript.Extent.Height > transcript.Viewport.Height);
            AssertNoVisibleRails(transcript);
            shell.PreserveViewport();
            shell.ScrollToVerticalOffset(180);
            Pump(window);
            Assert.InRange(transcript.Offset.Y, 179, 181);

            model.IsNavigationOpen = true;
            await SettleAsync(window);
            var drawer = Required<MobileDrawerView>(view, "DrawerContent");
            var list = drawer.GetVisualDescendants().OfType<ScrollViewer>().Single();
            Assert.True(list.Extent.Height > list.Viewport.Height);
            AssertNoVisibleRails(list);
            list.Offset = new Vector(0, 200);
            Pump(window);
            Assert.InRange(list.Offset.Y, 199, 201);
        });

    private static async Task RunAsync(
        double width, bool dark, Func<MobileShellViewModel, MobileShellView, Window, Task> test)
    {
        using var session = HeadlessMobileSession.Start();
        await session.Dispatch(async () =>
        {
            await using var model = new MobileShellViewModel(store: session.NewStore(), post: action => action());
            model.IsPaired = model.IsConnected = model.IsHostReady = true;
            model.HostName = "Preview PC";
            var id = Guid.NewGuid();
            model.Chat.Reset(id, "An open space for a useful conversation");
            model.Chat.ApplyTranscript(new RemoteTranscript
            {
                ChatId = id, Revision = 1,
                Turns = Enumerable.Range(0, 24).Select(index => new RemoteTranscriptTurn
                {
                    Id = $"turn-{index}",
                    Items =
                    [
                        new() { Id = $"user-{index}", Kind = RemoteProtocol.ItemKinds.User, Text = "A useful question" },
                        new() { Id = $"answer-{index}", Kind = RemoteProtocol.ItemKinds.Assistant,
                            Text = "A readable answer on the open canvas.\n\nMore detail without an enclosing card." }
                    ]
                }).ToList()
            });
            model.ChatList.Apply([new RemoteChatGroup
            {
                Label = "Today",
                Chats = Enumerable.Range(0, 100).Select(index => new RemoteChat
                {
                    Id = index == 0 ? id : Guid.NewGuid(), Title = $"Conversation {index}", MessageCount = 4
                }).ToList()
            }]);
            var view = new MobileShellView { DataContext = model };
            var window = new Window
            {
                Width = width, Height = 840, Content = view,
                RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
            };
            try
            {
                window.Show();
                await SettleAsync(window);
                await test(model, view, window);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    private static T Required<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void Pump(Window window)
    {
        window.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task SettleAsync(Window window)
    {
        Pump(window);
        await Task.Delay(280);
        Pump(window);
    }

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    // Glass surfaces may carry a vertical sheen, so every stop of their tint counts.
    private static Color[] TintOf(IBrush? brush) => brush switch
    {
        ISolidColorBrush solid => [solid.Color],
        IGradientBrush gradient => [.. gradient.GradientStops.Select(stop => stop.Color)],
        _ => throw new Xunit.Sdk.XunitException($"Expected a glass tint, got {brush}.")
    };

    private static void AssertTranslucent(IBrush? brush)
    {
        var tint = TintOf(brush);
        Assert.NotEmpty(tint);
        Assert.All(tint, color => Assert.InRange(color.A, (byte)1, (byte)254));
    }

    private static void AssertGlass(Button button)
    {
        AssertTranslucent(button.Background);
        // Glass is graphite, as on the desktop: no navy or slate cast in its tint.
        Assert.All(TintOf(button.Background), tint =>
            Assert.True(Math.Abs(tint.R - tint.G) <= 6 && Math.Abs(tint.G - tint.B) <= 6,
                $"Glass is graphite, not tinted: {tint}."));
        Assert.Equal(new CornerRadius(22), button.CornerRadius);
        Assert.Equal(new Thickness(1), button.BorderThickness);
        // The rim is lit from above yet runs unbroken all round.
        AssertTranslucent(button.BorderBrush);
        var surface = Required<Border>(button, "PART_Surface");
        // Every floating surface shares the floating glass's quiet elevation.
        Assert.True(button.TryFindResource("Shadow.FloatingGlass", button.ActualThemeVariant, out var elevation));
        Assert.Equal(Assert.IsType<BoxShadows>(elevation), surface.BoxShadow);
        Assert.True(Enumerable.Range(0, surface.BoxShadow.Count).Any(index => !surface.BoxShadow[index].IsInset),
            "a floating surface casts a shadow");
        var frost = Assert.Single(button.GetVisualDescendants().OfType<StrataBackdropBlur>());
        Assert.Equal(surface.Bounds, frost.Bounds);
        Assert.Equal(surface.CornerRadius, frost.CornerRadius);
    }

    /// <summary>A grouped card: an opaque neutral surface with a hairline rim and the desktop's radius.</summary>
    private static void AssertCard(Border card)
    {
        var surface = Assert.Single(TintOf(card.Background));
        Assert.Equal(255, surface.A);
        Assert.True(Math.Abs(surface.R - surface.G) <= 6 && Math.Abs(surface.G - surface.B) <= 6,
            $"Cards are graphite, not tinted: {surface}.");
        Assert.Equal(new Thickness(1), card.BorderThickness);
        Assert.Equal(14, card.CornerRadius.TopLeft);
    }

    private static void AssertTransparent(IBrush? brush) =>
        Assert.True(brush is null || brush.Opacity == 0 || brush is ISolidColorBrush { Color.A: 0 },
            $"Expected an unframed canvas, got {brush}.");

    private static void AssertNoVisibleRails(Control root) =>
        Assert.DoesNotContain(root.GetVisualDescendants().OfType<ScrollBar>(), bar => bar.IsEffectivelyVisible);
}
