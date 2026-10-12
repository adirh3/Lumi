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
            Assert.NotEqual(navigation, canvas);
            if (dark)
            {
                Assert.InRange(canvas.R, (byte)0, (byte)8);
                Assert.InRange(canvas.G, (byte)0, (byte)8);
                Assert.InRange(canvas.B, (byte)0, (byte)10);
                Assert.True(canvas.R < navigation.R && canvas.G < navigation.G && canvas.B < navigation.B);
            }
            else
            {
                Assert.InRange(canvas.R, (byte)253, byte.MaxValue);
                Assert.InRange(canvas.G, (byte)253, byte.MaxValue);
                Assert.InRange(canvas.B, (byte)253, byte.MaxValue);
                Assert.True(canvas.R > navigation.R && canvas.G > navigation.G && canvas.B > navigation.B);
            }
            Assert.Equal(default, Required<Border>(chat, "TopBarInset").BorderThickness);
            AssertTransparent(Required<Border>(chat, "TopBarInset").Background);
            var controlsButton = Required<Button>(chat, "HeaderDetailsButton");
            AssertGlass(controlsButton);
            foreach (var name in new[] { "MenuButton", "NewChatButton" })
            {
                var action = Required<Button>(chat, name);
                AssertGlass(action);
                Assert.False(action.ClipToBounds);
                Assert.True(action.Bounds.Height >= 48);
                Assert.True(action.Bounds.Width >= 48);
                var surface = Required<Border>(action, "PART_Surface");
                Assert.Equal(44, surface.Bounds.Width, precision: 1);
                Assert.Equal(44, surface.Bounds.Height, precision: 1);
                Assert.True(surface.BoxShadow.Count > 0);
            }
            Assert.Single(controlsButton.GetVisualDescendants().OfType<TextBlock>());
            Assert.Equal(model.Chat.ModelDisplayName, Required<TextBlock>(chat, "HeaderModelText").Text);
            Assert.InRange(Required<TextBlock>(chat, "HeaderModelText").FontSize, 14, 16);
            Assert.Equal("HeaderControlsIcon", Assert.Single(controlsButton.GetVisualDescendants().OfType<PathIcon>()).Name);
            Assert.DoesNotContain(controlsButton.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == model.HeaderTitle);
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
            Assert.DoesNotContain(drawer.GetVisualDescendants().OfType<Button>(),
                button => button.Name == "DrawerNewChatButton");
            AssertGlass(Required<Button>(drawer, "DrawerSearchButton"));
            AssertGlass(Required<Button>(drawer, "DrawerAccountButton"));
            Assert.Equal("Lumi", Required<TextBlock>(drawer, "DrawerBrandTitle").Text);
            var brand = Required<Panel>(drawer, "DrawerBrandSurface");
            Assert.False(brand.IsHitTestVisible);
            Assert.Null(brand.Background);
            Assert.IsType<TextBlock>(Assert.Single(brand.GetVisualChildren()));
            Assert.DoesNotContain(brand.GetVisualDescendants(), control => control is Border or StrataBackdropBlur);
            Assert.DoesNotContain(drawer.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Conversations");
            Assert.Equal(!model.CanDockDrawer, Required<Button>(drawer, "DrawerCloseButton").IsEffectivelyVisible);
            Assert.DoesNotContain(drawer.GetVisualDescendants().OfType<Control>(), control => control.Name == "DrawerToggleButton");
            foreach (var row in drawer.GetVisualDescendants().OfType<Button>()
                         .Where(button => button.Classes.Contains("drawer-chat")))
                Assert.Equal(default, row.BorderThickness);
            foreach (var row in drawer.GetVisualDescendants().OfType<Button>()
                         .Where(button => button.DataContext is ChatListItemViewModel))
            {
                Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible);
                Assert.DoesNotContain(row.GetVisualDescendants().OfType<PathIcon>(),
                    icon => icon.IsEffectivelyVisible);
            }

            model.ShowPageCommand.Execute("Settings");
            await SettleAsync(window);
            var settings = Required<MobileSettingsView>(view, "SettingsPage");
            var groups = settings.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Name == "PART_Card"
                                 && border.TemplatedParent is StrataSettingGroup).ToArray();
            Assert.NotEmpty(groups);
            foreach (var group in groups)
            {
                AssertTransparent(group.Background);
                Assert.Equal(default, group.BorderThickness);
                Assert.Equal(default, group.BoxShadow);
            }
            AssertTransparent(Required<Border>(settings, "SettingsConnectionCard").Background);
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
            Assert.Equal(dark ? 0.56 : 0.20, presence.Intensity, 3);
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
            Assert.Equal(dark ? 0.56 : 0.20, presence.Intensity, 3);
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
            Assert.Equal(dark ? 0.56 : 0.20, presence.Intensity, 3);
            AssertAtComposer();
            AssertNoCompanion();

            window.RequestedThemeVariant = dark ? ThemeVariant.Light : ThemeVariant.Dark;
            await SettleAsync(window);
            Assert.Equal(dark ? 0.20 : 0.56, presence.Intensity, 3);
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
            Assert.Equal(1, edge.BoxShadow.Count);
            Assert.InRange(edge.BoxShadow[0].Blur, 1, 8);
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
        Assert.Equal(new CornerRadius(22), button.CornerRadius);
        Assert.Equal(new Thickness(1), button.BorderThickness);
        Assert.IsAssignableFrom<ISolidColorBrush>(button.BorderBrush);
        var surface = Required<Border>(button, "PART_Surface");
        Assert.Equal(1, surface.BoxShadow.Count);
        var frost = Assert.Single(button.GetVisualDescendants().OfType<StrataBackdropBlur>());
        Assert.Equal(surface.Bounds, frost.Bounds);
        Assert.Equal(surface.CornerRadius, frost.CornerRadius);
    }

    private static void AssertTransparent(IBrush? brush) =>
        Assert.True(brush is null || brush.Opacity == 0 || brush is ISolidColorBrush { Color.A: 0 },
            $"Expected an unframed canvas, got {brush}.");

    private static void AssertNoVisibleRails(Control root) =>
        Assert.DoesNotContain(root.GetVisualDescendants().OfType<ScrollBar>(), bar => bar.IsEffectivelyVisible);
}
