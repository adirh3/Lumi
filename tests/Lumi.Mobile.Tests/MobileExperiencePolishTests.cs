using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Media.Transformation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.Behaviors;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;
using StrataTheme.Animation;
using Xunit;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class MobileExperiencePolishTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public Task NavigationIsAQuietListWithTwoLineRowsAndPlacesInTheThumbZone() =>
        RunUiAsync(320, async (shell, view, window) =>
        {
            shell.ChatList.Apply([new RemoteChatGroup
            {
                Label = "Today",
                Chats = [new RemoteChat
                {
                    Id = shell.Chat.ChatId, Title = "A useful conversation",
                    Preview = "A readable preview of the latest thought", UpdatedAt = DateTimeOffset.Now
                }]
            }]);
            shell.ChatList.SelectedChatId = shell.Chat.ChatId;
            shell.IsDrawerOpen = true;
            await Task.Delay(320);
            Pump(window);
            var drawer = Required<MobileDrawerView>(view, "DrawerContent");

            // No brand bar: search and the project lens head the list, as on the desktop's sidebar.
            Assert.DoesNotContain(drawer.GetVisualDescendants().OfType<TextBlock>(), text => text.Name == "DrawerBrandTitle");
            Assert.DoesNotContain(drawer.GetVisualDescendants().OfType<Image>(), image => image.Name == "DrawerBrandImage");
            Assert.DoesNotContain(drawer.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Your workspace");
            Assert.Equal("All chats", Required<TextBlock>(drawer, "DrawerProjectLabel").Text);
            Assert.DoesNotContain(drawer.GetVisualDescendants().OfType<ItemsControl>(),
                control => control.Name == "DrawerProjects");

            // On a phone the list is a screen of its own, so New chat sits beside search at its top right,
            // as it does in the chat's bar. The places pill holds only the places, centred.
            Assert.Same(shell.ChatList.NewChatCommand, Required<Button>(view, "NewChatButton").Command);
            Assert.Same(shell.ChatList.NewChatCommand, Required<Button>(drawer, "DrawerNewChatButton").Command);
            Assert.True(Required<Button>(drawer, "DrawerNewChatButton").IsEffectivelyVisible);
            Assert.DoesNotContain(drawer.GetVisualDescendants().OfType<Button>(), button => button.Name == "PlacesNewChatButton");
            foreach (var name in new[]
                     {
                         "DrawerSearchButton", "DrawerNewChatButton", "DrawerProjectButton", "PlacesChatsButton",
                         "PlacesJobsButton", "PlacesCustomizeButton", "PlacesSettingsButton"
                     })
            {
                var action = Required<Button>(drawer, name);
                Assert.True(action.Bounds.Width >= 48 && action.Bounds.Height >= 48, $"{name} is {action.Bounds}");
                Assert.True(action.TranslatePoint(new Point(action.Bounds.Width, 0), drawer)!.Value.X <= drawer.Bounds.Width);
            }
            Assert.Contains("active", Required<Button>(drawer, "PlacesChatsButton").Classes);
            var places = Required<PlacesBarView>(drawer, "DrawerPlaces");
            Assert.True(places.TranslatePoint(default, drawer)!.Value.Y > drawer.Bounds.Height - 120,
                "the places belong in the thumb zone");

            // One line, as the desktop's rows: the title alone at a thumb's height. The date groups say
            // when a chat last moved, so the row carries no time.
            var row = drawer.GetVisualDescendants().OfType<Button>()
                .Single(button => button.DataContext is ChatListItemViewModel);
            Assert.Contains("selected", row.Classes);
            Assert.InRange(row.Bounds.Height, 48, 52);
            var title = row.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Classes.Contains("conversation-title"));
            Assert.Equal("A useful conversation", title.Text);
            Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, title.TextTrimming);
            Assert.Equal(new[] { title }, row.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible));
            Assert.InRange(title.TranslatePoint(new Point(0, title.Bounds.Height / 2), row)!.Value.Y
                           - row.Bounds.Height / 2, -1, 1);
            // The open chat wears the desktop's neutral wash and its short accent pipe, not an accent fill.
            var pipe = Required<Border>(row, "ChatRowMarker");
            Assert.True(pipe.IsEffectivelyVisible);
            Assert.Equal(16, pipe.Bounds.Height, precision: 1);
            Assert.Equal(3, pipe.Bounds.Width, precision: 1);
            var wash = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(row.Background).Color;
            Assert.True(Math.Abs(wash.R - wash.G) <= 2 && Math.Abs(wash.G - wash.B) <= 2,
                $"the selected row is a neutral wash, not tinted: {wash}");

            // Picking the chat shows it; on a phone the list steps aside.
            Tap(window, row);
            Assert.False(shell.IsDrawerOpen);
        }, setup: shell => shell.Projects.Add(
            new ProjectPickViewModel { Id = Guid.NewGuid(), Name = "Lumi" }));

    [Fact]
    public Task ProjectPickerKeepsLargeCatalogsVirtualizedAndAvoidsTheBookHinge() =>
        RunUiAsync(884, async (shell, view, window) =>
        {
            view.Posture = Lumi.Mobile.Layout.FoldPosture.BookVerticalHinge;
            view.HingeSize = 24;
            view.HingePosition = 430;
            Pump(window);
            await Task.Delay(260);
            Tap(window, Required<Button>(view, "DrawerProjectButton"));
            await Task.Delay(280);
            Pump(window);
            var sheet = Required<StrataBottomSheet>(view, "ProjectPickerSheet");
            var surface = Required<Border>(sheet, "PART_Sheet");
            Assert.True(surface.TranslatePoint(new Point(surface.Bounds.Width, 0), view)!.Value.X <= 430);
            var list = Required<ItemsControl>(sheet, "ProjectPickerList");
            var realized = list.GetVisualDescendants().OfType<Button>().Count();
            Assert.InRange(realized, 1, 40);
            Assert.True(shell.IsModalSheetPresented);
            shell.GoBackCommand.Execute(null);
            await Task.Delay(StrataBottomSheet.CloseDuration + TimeSpan.FromMilliseconds(60));
            Pump(window);
            Assert.False(shell.IsModalSheetPresented);
        }, setup: shell =>
        {
            foreach (var index in Enumerable.Range(0, 400))
                shell.Projects.Add(new ProjectPickViewModel
                {
                    Id = Guid.NewGuid(), Name = $"Project {index:000}"
                });
        });

    [Theory]
    [InlineData(360)]
    [InlineData(1100)]
    public Task FooterProjectPickerSelectsExplicitScopesAndBackPreservesTheDrawer(double width) =>
        RunUiAsync(width, async (shell, view, window) =>
        {
            shell.IsNavigationOpen = true;
            await Task.Delay(260);
            Pump(window);
            var pickerButton = Required<Button>(view, "DrawerProjectButton");
            var label = Required<TextBlock>(view, "DrawerProjectLabel");
            var currentChatId = shell.Chat.ChatId;
            Assert.Equal("All chats", label.Text);
            Assert.Null(shell.ChatList.ProjectFilterId);
            Tap(window, pickerButton);
            await Task.Delay(260);
            Pump(window);
            Assert.True(shell.IsProjectPickerOpen);
            Assert.True(shell.CanGoBack);
            Assert.False(shell.CanDragDrawer);
            var list = Required<ItemsControl>(view, "ProjectPickerList");
            Button ProjectRow() => list.GetVisualDescendants().OfType<Button>()
                .Single(button => button.DataContext == shell.Projects[0]);
            Tap(window, ProjectRow());
            Assert.False(shell.IsProjectPickerOpen);
            Assert.True(shell.IsNavigationOpen);
            Assert.Equal(shell.Projects[0].Id, shell.ChatList.ProjectFilterId);
            Assert.Equal(currentChatId, shell.Chat.ChatId);
            Assert.Equal("Lumi", label.Text);

            await Task.Delay(240);
            Tap(window, pickerButton);
            await Task.Delay(260);
            Pump(window);
            Tap(window, ProjectRow());
            Assert.Equal(shell.Projects[0].Id, shell.ActiveProjectId);
            Assert.False(shell.IsProjectPickerOpen);

            await Task.Delay(240);
            Tap(window, pickerButton);
            await Task.Delay(260);
            Pump(window);
            shell.GoBackCommand.Execute(null);
            Assert.False(shell.IsProjectPickerOpen);
            Assert.True(shell.IsNavigationOpen);
            Assert.Equal("Lumi", label.Text);

            await Task.Delay(240);
            // New chat sits at the top right: beside search in the list on a phone, and in the chat's bar
            // beside a docked list. Either starts the chat in the project the list shows.
            Tap(window, Required<Button>(view, shell.CanDockDrawer ? "NewChatButton" : "DrawerNewChatButton"));
            Assert.Equal(shell.Projects[0].Id.ToString(), shell.Chat.ProjectValue);
            Assert.Equal("Lumi", shell.Chat.ProjectName);

            shell.IsNavigationOpen = true;
            await Task.Delay(260);
            Pump(window);
            Tap(window, pickerButton);
            await Task.Delay(260);
            Pump(window);
            Tap(window, Required<Button>(view, "AllProjectsButton"));
            Assert.Null(shell.ActiveProjectId);
            Assert.Null(shell.ChatList.ProjectFilterId);
            Assert.Null(shell.Chat.ProjectValue);
            Assert.Equal("All chats", label.Text);
            Assert.False(shell.IsProjectPickerOpen);
        }, setup: shell =>
        {
            shell.Projects.Add(new ProjectPickViewModel { Id = Guid.NewGuid(), Name = "Lumi" });
            shell.Projects.Add(new ProjectPickViewModel { Id = Guid.NewGuid(), Name = "Everyday life" });
        });

    [Fact]
    public Task EmptyProjectPickerCanDismissWithoutChangingItsDefaultScope() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            shell.IsNavigationOpen = true;
            await Task.Delay(260);
            Pump(window);
            Tap(window, Required<Button>(view, "DrawerProjectButton"));
            await Task.Delay(260);
            Pump(window);
            Assert.Empty(Required<ItemsControl>(view, "ProjectPickerList")
                .GetVisualDescendants().OfType<Button>());
            Tap(window, Required<Button>(view, "AllProjectsButton"));
            Assert.False(shell.IsProjectPickerOpen);
            Assert.Equal("All chats", shell.ProjectSwitcherTitle);
            shell.OpenProjectPickerCommand.Execute(null);
            shell.IsPaired = false;
            Assert.False(shell.IsProjectPickerOpen);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TabletDrawerMotionDoesNotRemeasureTheTranscriptAtEveryStep(bool readingHistory) =>
        RunUiAsync(900, async (shell, view, window) =>
        {
            var drawer = Required<StrataNavigationDrawer>(view, "NavDrawer");
            var body = Required<Border>(view, "ChatTranscriptSideInset");
            var payload = Assert.IsType<StackPanel>(body.Child);
            var probe = new WidthMeasureProbe();
            payload.Children.Add(probe);
            await Task.Delay(260);
            Pump(window);
            var chatShell = Required<StrataChatShell>(view, "ChatShell");
            if (readingHistory)
            {
                chatShell.PreserveViewport();
                chatShell.ScrollToVerticalOffset(120);
            }
            else
                chatShell.JumpToLatest();
            Pump(window);

            foreach (var direction in new[] { -1, 1 })
            {
                var widthBefore = body.Bounds.Width;
                var measuresBefore = probe.Measures;
                var offsetBefore = chatShell.VerticalOffset;
                for (var step = 0; step < 8; step++)
                {
                    drawer.RaiseEvent(new EdgeDragEventArgs(
                        EdgeDragGestureRecognizer.EdgeDragEvent, direction * 28, 600 + direction * (step + 1) * 28));
                    Pump(window);
                    Assert.Equal(widthBefore, body.Bounds.Width, precision: 1);
                    Assert.Equal(measuresBefore, probe.Measures);
                    if (step == 3)
                    {
                        drawer.RaiseEvent(new EdgeDragEventArgs(
                            EdgeDragGestureRecognizer.EdgeDragEvent, -direction * 16, 600));
                        Pump(window);
                        Assert.Equal(widthBefore, body.Bounds.Width, precision: 1);
                        Assert.Equal(measuresBefore, probe.Measures);
                    }
                }
                drawer.RaiseEvent(new EdgeDragEndedEventArgs(EdgeDragGestureRecognizer.EdgeDragEndedEvent, 0));
                await Task.Delay(300);
                Pump(window);
                Assert.True(double.IsNaN(body.Width));
                Assert.True(Math.Abs(body.Bounds.Width - widthBefore) > 20);
                Assert.InRange(probe.Measures - measuresBefore, 1, 2);
                Assert.Equal(!readingHistory, chatShell.IsFollowingTail);
                if (readingHistory)
                    Assert.Equal(offsetBefore, chatShell.VerticalOffset, precision: 1);
                else
                    Assert.InRange(chatShell.CurrentDistanceFromBottom, 0, 1);
                output.WriteLine($"{(direction < 0 ? "Close" : "Open")}: payload measures during drag 0; "
                                 + $"settle measures {probe.Measures - measuresBefore}.");
            }

            drawer.RaiseEvent(new EdgeDragEventArgs(EdgeDragGestureRecognizer.EdgeDragEvent, -28, 500));
            Pump(window);
            Assert.False(double.IsNaN(body.Width));
            window.Width = 412;
            Pump(window);
            Assert.False(shell.CanDockDrawer);
            Assert.True(double.IsNaN(body.Width));
        }, setup: shell => shell.Chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = shell.Chat.ChatId, Revision = 1, TotalRawMessageCount = 8, WindowEndMessageIndex = 8,
            Turns = Enumerable.Range(0, 8).Select(index => new RemoteTranscriptTurn
            {
                Id = $"width-turn-{index}",
                Items = [new RemoteTranscriptItem
                {
                    Id = $"width-answer-{index}", Kind = RemoteProtocol.ItemKinds.Assistant,
                    Text = string.Join(" ", Enumerable.Repeat(
                        "The conversation should stay readable while navigation moves beside it.", 30))
                }]
            }).ToList()
        }));

    [Fact]
    public Task DockedSidebarButtonMovesBothCanvasEdgesContinuouslyAndReversesWithoutJumping() =>
        RunUiAsync(1100, async (shell, view, window) =>
        {
            var drawer = Required<StrataNavigationDrawer>(view, "NavDrawer");
            var panel = Required<Avalonia.Controls.Presenters.ContentPresenter>(drawer, "PART_Panel");
            var chat = Required<ChatDetailView>(view, "ChatSurface");
            var menu = Required<Button>(view, "MenuButton");
            await Task.Delay(220);
            Pump(window);
            Assert.True(shell.IsDrawerDocked);
            Assert.False(drawer.IsModal);
            Assert.True(drawer.IsOpen);
            Assert.True(menu.IsEffectivelyVisible);
            Assert.Equal("Hide chats", AutomationProperties.GetName(menu));
            Assert.Single(view.GetVisualDescendants().OfType<MobileDrawerView>());
            // The desktop's sidebar toggle: a frame whose list column is filled while the chats show.
            var glyph = Required<Panel>(menu, "MenuSidebarIcon");
            Assert.True(glyph.IsEffectivelyVisible);
            Assert.False(Required<PathIcon>(menu, "MenuBackIcon").IsEffectivelyVisible);
            Assert.DoesNotContain("collapsed", glyph.Classes);
            var column = glyph.GetVisualChildren().OfType<Border>().Single(border => border.Classes.Contains("sidebar-glyph-column"));
            Assert.Equal(1, column.Opacity, 2);
            var chatWidth = chat.Bounds.Width;
            var chatResizes = 0;
            chat.SizeChanged += (_, _) => chatResizes++;
            var transform = panel.RenderTransform;
            var motionLayoutChanges = 0;
            drawer.PropertyChanged += (_, change) =>
            {
                if (change.Property == StrataNavigationDrawer.PanelWidthProperty
                    || change.Property == StrataNavigationDrawer.IsModalProperty
                    || change.Property == Visual.FlowDirectionProperty)
                    motionLayoutChanges++;
            };
            await AssertDockedCanvasGeometry(view, drawer, "open");

            Tap(window, menu);
            Assert.True(shell.IsSidebarCollapsed);
            Assert.Contains("collapsed", glyph.Classes);
            Assert.True(drawer.Progress > 0, "The button must not snap to the closed endpoint.");
            await AssertDockedCanvasGeometry(view, drawer, "button release");
            await Task.Delay(55);
            Pump(window);
            Assert.InRange(drawer.Progress, 0.001, 0.999);
            Assert.InRange(chat.Bounds.Width, chatWidth + 1, drawer.Bounds.Width - 1);
            await AssertDockedCanvasGeometry(view, drawer, "button intermediate");
            await Task.Delay(200);
            Pump(window);
            Assert.Equal(0, drawer.Progress);
            Assert.False(panel.IsEffectivelyEnabled);
            Assert.True(menu.IsEffectivelyVisible);
            // With the chats hidden the glyph's list column has emptied out.
            Assert.InRange(column.Opacity, 0, 0.1);

            shell.ToggleDrawerCommand.Execute(null);
            Pump(window);
            await AssertDockedCanvasGeometry(view, drawer, "opening start");
            await Task.Delay(220);
            shell.ToggleDrawerCommand.Execute(null);
            await Task.Delay(45);
            Pump(window);
            window.Width = 1080;
            Pump(window);
            Assert.InRange(drawer.Progress, 0.001, 0.999);
            await AssertDockedCanvasGeometry(view, drawer, "resize while settling");
            window.Width = 1100;
            Pump(window);
            var beforeReversal = drawer.Progress;
            var beforeReversalLeft = chat.TranslatePoint(default, drawer)!.Value.X;
            shell.ToggleDrawerCommand.Execute(null);
            Assert.Equal(beforeReversal, drawer.Progress);
            Assert.Equal(beforeReversalLeft, chat.TranslatePoint(default, drawer)!.Value.X);
            Pump(window);
            await AssertDockedCanvasGeometry(view, drawer, "reversal");
            await Task.Delay(220);
            Pump(window);
            Assert.True(panel.IsEffectivelyEnabled);
            Assert.Equal(1, drawer.Progress, precision: 2);
            Assert.Same(transform, panel.RenderTransform);
            Assert.Equal(chatWidth, chat.Bounds.Width);
            Assert.True(chatResizes > 2, "The real conversation must resize during motion, not just on commit.");
            // DrawerWidth is re-notified by sidebar toggles, but equivalent binding values must not
            // reach ResetMotionForLayout and cancel a running settle.
            Assert.Equal(0, motionLayoutChanges);
            await AssertDockedCanvasGeometry(view, drawer, "reopened");
        });

    [Fact]
    public Task TabletChatAreaGesturesOpenAndCloseTheDockedPaneWithoutAModalOverlay() =>
        RunUiAsync(1100, async (shell, view, window) =>
        {
            var drawer = Required<StrataNavigationDrawer>(view, "NavDrawer");
            var editor = Assert.IsType<NativeComposerEditorHost>(
                Required<StrataChatComposer>(view, "Composer").EditorContent);
            await Task.Delay(220);
            Assert.True(drawer.IsDragEnabled);
            Assert.True(editor.IsVisible);
            drawer.RaiseEvent(new EdgeDragEventArgs(
                EdgeDragGestureRecognizer.EdgeDragEvent, -64, 500));
            Pump(window);
            Assert.InRange(drawer.Progress, 0, 1);
            Assert.True(shell.IsDrawerDocked);
            await AssertDockedCanvasGeometry(view, drawer, "close drag", verifyHits: true);
            drawer.RaiseEvent(new EdgeDragEndedEventArgs(
                EdgeDragGestureRecognizer.EdgeDragEndedEvent, 0));
            Assert.False(editor.IsVisible);
            await Task.Delay(45);
            Pump(window);
            await AssertDockedCanvasGeometry(view, drawer, "close settle");
            await Task.Delay(220);
            Pump(window);
            Assert.True(shell.IsSidebarCollapsed);
            Assert.False(shell.IsDrawerOverlay);
            Assert.Equal(0, shell.NavigationLeadingWidth);
            Assert.True(editor.IsVisible);

            drawer.RaiseEvent(new EdgeDragEventArgs(
                EdgeDragGestureRecognizer.EdgeDragEvent, 64, 560));
            Pump(window);
            Assert.False(editor.IsVisible);
            await AssertDockedCanvasGeometry(view, drawer, "open drag", verifyHits: true);
            drawer.RaiseEvent(new EdgeDragEndedEventArgs(
                EdgeDragGestureRecognizer.EdgeDragEndedEvent, 0));
            await Task.Delay(220);
            Pump(window);
            Assert.True(shell.IsDrawerDocked);
            Assert.False(shell.IsDrawerOverlay);
            // The docked list is the desktop sidebar's width: about a third of the window, 300 to 360 dp.
            Assert.Equal(360, shell.NavigationLeadingWidth);
            Assert.True(editor.IsVisible);
            Assert.False(drawer.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Name == "PART_Scrim" && ReferenceEquals(border.TemplatedParent, drawer)).IsVisible);

            Tap(window, Required<Button>(view, "MenuButton"));
            Assert.True(shell.IsSidebarCollapsed);
            await Task.Delay(220);
            Pump(window);
            Assert.Equal(0, drawer.Progress);
            Assert.True(Required<Button>(view, "MenuButton").IsEffectivelyVisible);
        }, new FocusEditorFactory());

    private async Task AssertDockedCanvasGeometry(
        MobileShellView view, StrataNavigationDrawer drawer, string stage, bool verifyHits = false)
    {
        // A captured drag can hold an exact frame for hit testing. A live settle keeps advancing
        // while a composition commit is awaited, so check its current layout geometry synchronously.
        if (verifyHits)
        {
            await Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(drawer)!
                .Compositor.RequestCommitAsync();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        var panel = Required<Avalonia.Controls.Presenters.ContentPresenter>(drawer, "PART_Panel");
        var canvas = Required<Border>(view, "ConversationFrame");
        var chat = Required<ChatDetailView>(view, "ChatSurface");
        var panelRight = panel.TranslatePoint(new Point(panel.Bounds.Width, 0), drawer)!.Value.X;
        var left = canvas.TranslatePoint(default, drawer)!.Value.X;
        var right = canvas.TranslatePoint(new Point(canvas.Bounds.Width, 0), drawer)!.Value.X;
        output.WriteLine($"{stage}: progress={drawer.Progress:F4}, sidebarRight={panelRight:F2}, "
            + $"canvas=[{left:F2},{right:F2}], chatWidth={chat.Bounds.Width:F2}");
        Assert.InRange(Math.Abs(left - panelRight), 0, 1);
        Assert.Equal(drawer.Bounds.Width, right, precision: 1);
        Assert.Equal(canvas.Bounds.Width, chat.Bounds.Width, precision: 1);
        if (!verifyHits)
            return;
        foreach (var x in new[] { left + 2, right - 2 })
        {
            var hit = Assert.IsAssignableFrom<Visual>(drawer.InputHitTest(new Point(x, 360)));
            Assert.True(ReferenceEquals(hit, canvas) || hit.GetVisualAncestors().Contains(canvas),
                $"{stage}: exposed canvas at x={x:F1} is instead covered by {hit.GetType().Name} "
                + string.Join("/", hit.GetVisualAncestors().OfType<Control>().Select(control => control.Name ?? control.GetType().Name)));
        }
    }

    [Fact]
    public Task TabletTouchMotionKeepsBoundedTranscriptAndNativeDraftThroughResizeAndReversal() =>
        RunUiAsync(1100, async (shell, view, window) =>
        {
            var drawer = Required<StrataNavigationDrawer>(view, "NavDrawer");
            var chat = Required<ChatDetailView>(view, "ChatSurface");
            var chatShell = Required<StrataChatShell>(view, "ChatShell");
            var transcript = Required<ItemsControl>(view, "Transcript");
            var composer = Required<StrataChatComposer>(view, "Composer");
            var editor = Assert.IsType<NativeComposerEditorHost>(composer.EditorContent);
            shell.Chat.ApplyTranscript(new RemoteTranscript
            {
                ChatId = shell.Chat.ChatId, Revision = 1, IsLatestWindow = true,
                TotalRawMessageCount = 20000, WindowStartMessageIndex = 19952,
                WindowEndMessageIndex = 20000, HasEarlierMessages = true,
                Status = new RemoteChatStatus { ChatId = shell.Chat.ChatId },
                Turns = Enumerable.Range(0, 24).Select(index => new RemoteTranscriptTurn
                {
                    Id = $"motion-turn-{index}",
                    Items =
                    [
                        new RemoteTranscriptItem
                        {
                            Id = $"motion-user-{index}", Kind = RemoteProtocol.ItemKinds.User,
                            Text = $"Explain step {index} and its trade-offs."
                        },
                        new RemoteTranscriptItem
                        {
                            Id = $"motion-answer-{index}", Kind = RemoteProtocol.ItemKinds.Assistant,
                            Text = $"### Step {index}\n\n"
                                + string.Concat(Enumerable.Repeat(
                                    "A **bounded transcript** keeps real markdown wrapping, `inline code`, "
                                    + "and a readable conversation mounted while navigation moves. ", 4))
                                + "\n\n- First detail\n- Second detail\n\n```csharp\nreturn result;\n```"
                        }
                    ]
                }).ToList()
            });
            shell.Chat.PromptText = "Keep the native draft through every frame.";
            await Task.Delay(280);
            Pump(window);
            var turns = shell.Chat.Turns.ToArray();
            var messages = transcript.GetVisualDescendants().OfType<StrataChatMessage>().ToArray();
            var markdown = transcript.GetVisualDescendants().OfType<StrataMarkdown>().ToArray();
            Assert.NotEmpty(messages);
            Assert.NotEmpty(markdown);
            Assert.True(chatShell.ExtentHeight > chatShell.ViewportHeight);
            Assert.True(chatShell.IsFollowingTail);

            var touch = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Touch, true);
            var recognizer = drawer.GestureRecognizers.OfType<EdgeDragGestureRecognizer>().Single();
            var moveTouch = typeof(EdgeDragGestureRecognizer).GetMethod("PointerMoved",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var releaseTouch = typeof(EdgeDragGestureRecognizer).GetMethod("PointerReleased",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            chat.RaiseEvent(new PointerPressedEventArgs(chat, touch, drawer, new Point(650, 360), 1000,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None, 1));
            var frameTimes = new List<double>();
            var frameAllocations = new List<long>();
            var allocatedBytes = 0L;
            async Task Move(double x, ulong timestamp)
            {
                var args = new PointerEventArgs(InputElement.PointerMovedEvent, chat, touch, drawer,
                    new Point(x, 360), timestamp,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None);
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                // Headless has no raw touch injector. Deliver the captured touch directly to the
                // production recognizer, as EdgeDragGestureRecognizerTests do.
                moveTouch.Invoke(recognizer, [args]);
                window.UpdateLayout();
                frameTimes.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                var frameBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                frameAllocations.Add(frameBytes);
                allocatedBytes += frameBytes;
                Dispatcher.UIThread.RunJobs();
                await AssertDockedCanvasGeometry(view, drawer, $"touch x={x}", verifyHits: true);
                output.WriteLine($"  input/layout: {frameTimes[^1]:F2}ms, {frameBytes} bytes");
                Assert.Same(editor, composer.EditorContent);
                var settled = drawer.Progress is <= 0.001 or >= 0.999;
                Assert.Equal(settled, editor.IsEffectivelyVisible);
                Assert.Equal(!settled, shell.IsNavigationCoveringContent);
                Assert.Equal(shell.Chat.PromptText, editor.Text);
                if (settled)
                {
                    var editorLeft = editor.TranslatePoint(default, drawer)!.Value.X;
                    var editorRight = editor.TranslatePoint(new Point(editor.Bounds.Width, 0), drawer)!.Value.X;
                    Assert.True(editorLeft >= chat.TranslatePoint(default, drawer)!.Value.X);
                    Assert.True(editorRight <= drawer.Bounds.Width);
                }
            }

            try
            {
                // Progress follows the finger across the docked list's own width.
                var paneWidth = drawer.PanelWidth;
                await Move(586, 1400);
                Assert.Equal(1 - 64 / paneWidth, drawer.Progress, precision: 3);
                window.Width = 1000;
                Pump(window);
                Assert.Equal(1 - 64 / paneWidth, drawer.Progress, precision: 3);
                await AssertDockedCanvasGeometry(view, drawer, "resize while captured", verifyHits: true);
                await Move(618, 1800);
                Assert.Equal(1 - 32 / paneWidth, drawer.Progress, precision: 3);
                for (var i = 1; i <= 8; i++)
                    await Move(618 - i * 32, (ulong)(1800 + i * 200));
                Assert.Equal(1 - 288 / paneWidth, drawer.Progress, precision: 3);
                releaseTouch.Invoke(recognizer, [new PointerReleasedEventArgs(chat, touch, drawer, new Point(362, 360), 3800,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                    KeyModifiers.None, MouseButton.Left)]);
            }
            finally
            {
                touch.Capture(null);
            }
            await Task.Delay(220);
            Pump(window);
            Assert.Equal(0, drawer.Progress);
            await AssertDockedCanvasGeometry(view, drawer, "touch closed");
            Assert.True(chatShell.IsFollowingTail);
            Assert.InRange(chatShell.CurrentDistanceFromBottom, 0, 1);

            // Real upward input must keep owning scroll intent during the next width changes.
            var scrollPoint = chat.TranslatePoint(new Point(500, 360), window)!.Value;
            window.MouseWheel(scrollPoint, new Vector(0, 3), RawInputModifiers.None);
            Pump(window);
            Assert.False(chatShell.IsFollowingTail);
            var openingProgress = new List<double>();
            void ObserveOpening(object? sender, AvaloniaPropertyChangedEventArgs change)
            {
                if (change.Property == StrataNavigationDrawer.ProgressProperty)
                    openingProgress.Add(drawer.Progress);
            }
            drawer.PropertyChanged += ObserveOpening;
            Tap(window, Required<Button>(view, "MenuButton"));
            await Task.Delay(280);
            Pump(window);
            drawer.PropertyChanged -= ObserveOpening;
            Assert.Contains(openingProgress, progress => progress > 0.001 && progress < 0.999);
            await AssertDockedCanvasGeometry(view, drawer, "heavy button reopened");
            Assert.False(chatShell.IsFollowingTail);
            Assert.Equal(turns, shell.Chat.Turns);
            // Reflow may reveal another turn; already-visible content must still be reused.
            var visibleMessages = transcript.GetVisualDescendants().OfType<StrataChatMessage>().ToArray();
            var visibleMarkdown = transcript.GetVisualDescendants().OfType<StrataMarkdown>().ToArray();
            Assert.All(messages, message => Assert.Contains(message, visibleMessages));
            Assert.All(markdown, item => Assert.Contains(item, visibleMarkdown));
            Assert.InRange(visibleMarkdown.Length, 1, 8);
            Assert.Equal("Keep the native draft through every frame.", editor.Text);
            output.WriteLine($"Bounded history: total=20000, turns={turns.Length}, messageControls={messages.Length}, "
                + $"markdownControls={markdown.Length}; {frameTimes.Count} touch+layout frames "
                + $"median={frameTimes.Order().ElementAt(frameTimes.Count / 2):F2}ms, max={frameTimes.Max():F2}ms, "
                + $"allocated median={frameAllocations.Order().ElementAt(frameAllocations.Count / 2)}, "
                + $"mean={allocatedBytes / frameTimes.Count} bytes/frame (headless, not device GPU timing).");
            output.WriteLine($"Heavy opening progress: {string.Join(", ", openingProgress.Select(value => value.ToString("F3")))}");

            // A real breakpoint changes mode; it must not leave the tablet's inset behind.
            window.Width = 412;
            Pump(window);
            Assert.True(drawer.IsModal);
            Assert.Equal(0, Required<Border>(view, "NavigationSpacer").Bounds.Width);
            Assert.Equal(412, chat.Bounds.Width);
            Assert.Equal("Keep the native draft through every frame.", editor.Text);
        }, new FocusEditorFactory());

    [Theory]
    [InlineData(320)]
    [InlineData(412)]
    public Task PhoneDrawerFillsTheScreenAndKeepsNativeInputBehindTheEntireTransition(double width) =>
        RunUiAsync(width, async (shell, view, window) =>
        {
            view.ApplyPlatformInsets(new Thickness(12, 28, 8, 20));
            Pump(window);
            var drawer = Required<StrataNavigationDrawer>(view, "NavDrawer");
            var composer = Required<StrataChatComposer>(view, "Composer");
            var editor = Assert.IsType<NativeComposerEditorHost>(composer.EditorContent);
            Assert.Equal(width, drawer.PanelWidth, precision: 1);
            Assert.True(editor.IsVisible);

            drawer.RaiseEvent(new EdgeDragEventArgs(
                EdgeDragGestureRecognizer.EdgeDragEvent, 24, 80));
            Assert.False(shell.IsDrawerOpen);
            Assert.False(shell.IsDrawerOverlay);
            Assert.True(shell.IsNavigationCoveringContent);
            Assert.False(editor.IsVisible);
            drawer.RaiseEvent(new EdgeDragEndedEventArgs(
                EdgeDragGestureRecognizer.EdgeDragEndedEvent, 0));
            await Task.Delay(220);
            Assert.True(editor.IsVisible);

            shell.IsDrawerOpen = true;
            await Task.Delay(220);
            Pump(window);
            var panel = Required<Avalonia.Controls.Presenters.ContentPresenter>(drawer, "PART_Panel");
            Assert.Equal(width, panel.Bounds.Width, precision: 1);
            Assert.False(editor.IsVisible);
            shell.IsDrawerOpen = false;
            Assert.False(editor.IsVisible);
            await Task.Delay(220);
            Pump(window);
            Assert.True(editor.IsVisible);
            Assert.False(shell.IsDrawerOverlay);
        }, new FocusEditorFactory());

    [Fact]
    public Task ChatSettingsSheetsAreOutsideTheChatViewport() =>
        RunUiAsync(1112, (shell, view, window) =>
        {
            PumpFrame(window);
            var project = Required<StrataBottomSheet>(view, "ProjectPickerSheet");
            var model = Required<StrataBottomSheet>(view, "ModelSheet");
            var details = Required<StrataBottomSheet>(view, "RunSettingsSheet");
            var overlay = Assert.IsType<Panel>(model.Parent);
            Assert.Same(project.Parent, overlay.Parent);
            Assert.Same(overlay, details.Parent);
            foreach (var sheet in new[] { model, details })
            {
                Assert.Same(shell, sheet.DataContext);
                Assert.DoesNotContain(sheet.GetVisualAncestors(),
                    ancestor => ancestor is ChatDetailView or StrataNavigationDrawer);
            }
            var conversation = Required<Border>(view, "ConversationFrame");
            var expected = conversation.TranslatePoint(default, view)!.Value;
            var actual = overlay.TranslatePoint(default, view)!.Value;
            Assert.Equal(expected.X, actual.X, precision: 1);
            Assert.Equal(expected.Y, actual.Y, precision: 1);
            Assert.Equal(conversation.Bounds.Size, overlay.Bounds.Size);
            return Task.CompletedTask;
        });

    [Fact]
    public Task ChoiceSheetsShareSelectionIndicatorsAndPreserveTheNewChatDraft() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            shell.Chat.Reset(Guid.Empty, "New chat");
            shell.Chat.ApplyCatalogs(new RemoteSettings
            {
                PreferredModel = "gpt-6.1-sol",
                AvailableModels = ["gpt-6.1-sol", "gpt-6-sol"],
                ModelContextWindowTiers =
                [
                    "gpt-6.1-sol=Default,Long context", "gpt-6-sol=Default,Long context"
                ],
                ModelDefaults =
                [
                    new() { Model = "gpt-6.1-sol", ContextWindowTier = "Default" },
                    new() { Model = "gpt-6-sol", ContextWindowTier = "Default" }
                ]
            });
            shell.Chat.PromptText = "Keep this unsent thought.";
            shell.OpenProjectPickerCommand.Execute(null);
            PumpFrame(window);
            await Task.Delay(260);
            PumpFrame(window);
            Assert.Contains("selected", Required<Button>(view, "AllProjectsButton").Classes);
            var project = Required<ItemsControl>(view, "ProjectPickerList").GetVisualDescendants()
                .OfType<Button>().Single(button => button.DataContext == shell.Projects[0]);
            Tap(window, project);
            Assert.Equal(shell.Projects[0].Id, shell.ActiveProjectId);
            Assert.False(shell.IsProjectPickerOpen);

            shell.Chat.OpenModelSheetCommand.Execute(null);
            PumpFrame(window);
            await Task.Delay(260);
            PumpFrame(window);
            var modelSheet = Required<StrataBottomSheet>(view, "ModelSheet");
            AssertSelection(modelSheet, "gpt-6.1-sol");
            // As in the desktop's picker, the model's context tiers sit beneath it as segments.
            var segments = Required<StackPanel>(modelSheet, "ModelSheetContext").GetVisualDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("segment")).ToArray();
            Assert.Equal(["Default", "Long context"], segments.Select(segment => ((PickerOption)segment.DataContext!).Name));
            Assert.Equal("Default", ((PickerOption)Assert.Single(segments, segment => segment.Classes.Contains("selected")).DataContext!).Name);
            Assert.All(segments, segment => Assert.True(segment.Bounds.Height >= 48));
            var model = modelSheet.GetVisualDescendants().OfType<Button>()
                .Single(button => button.DataContext is ModelPickerOption { Name: "gpt-6-sol" }
                                  && button.Classes.Contains("model-option"));
            Tap(window, model);
            Assert.Equal("gpt-6-sol", shell.Chat.Model);
            Assert.False(shell.Chat.IsModelSheetOpen);

            shell.Chat.OpenContextFromRunSettingsCommand.Execute(null);
            PumpFrame(window);
            await Task.Delay(260);
            PumpFrame(window);
            var contextSheet = Required<StrataBottomSheet>(view, "ContextSheet");
            AssertSelection(contextSheet, "Default");
            var context = contextSheet.GetVisualDescendants().OfType<Button>()
                .Single(button => button.DataContext is PickerOption { Name: "Long context" });
            Tap(window, context);
            Assert.Equal("Long context", shell.Chat.ContextWindowTier);
            Assert.False(shell.Chat.IsContextSheetOpen);
            Assert.Equal("Keep this unsent thought.", shell.Chat.PromptText);
            Assert.Equal(Guid.Empty, shell.Chat.ChatId);
            Assert.Equal(shell.Projects[0].Id, shell.ActiveProjectId);

            static string? OptionName(object? option) => option switch
            {
                PickerOption picked => picked.Name,
                ModelPickerOption model => model.Name,
                _ => null
            };

            static void AssertSelection(StrataBottomSheet sheet, string expected)
            {
                var rows = sheet.GetVisualDescendants().OfType<Button>()
                    .Where(button => OptionName(button.DataContext) is not null && button.Classes.Contains("sheet-item")).ToArray();
                Assert.All(rows, row => Assert.True(row.Bounds.Height >= 48));
                var selected = Assert.Single(rows, row => row.Classes.Contains("selected"));
                Assert.Equal(expected, OptionName(selected.DataContext));
                Assert.All(rows, row =>
                {
                    var check = row.GetVisualDescendants().OfType<PathIcon>()
                        .Single(icon => icon.Classes.Contains("sheet-check"));
                    Assert.Equal(ReferenceEquals(row, selected), check.IsEffectivelyVisible);
                });
            }
        }, setup: shell =>
            shell.Projects.Add(new ProjectPickViewModel { Id = Guid.NewGuid(), Name = "Lumi" }));

    [Fact]
    public Task ChatDetailsIsTitledByItsChatAndClosesFromItsHeader() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            var sheet = Required<StrataBottomSheet>(view, "RunSettingsSheet");
            Assert.Equal("A long conversation title that deserves room to breathe", sheet.Title);
            shell.Chat.Reset(Guid.NewGuid(), string.Join(' ', Enumerable.Repeat("A remarkably detailed conversation title", 4)));
            shell.Chat.OpenRunSettingsSheetCommand.Execute(null);
            PumpFrame(window);
            await Task.Delay(260);
            PumpFrame(window);
            var title = Required<TextBlock>(sheet, "PART_Title");
            var close = Required<Button>(sheet, "PART_CloseButton");
            Assert.True(title.TranslatePoint(new Point(title.Bounds.Width, 0), window)!.Value.X
                        <= close.TranslatePoint(default, window)!.Value.X);
            // A long title takes two lines at most, at the font's own spacing, then an ellipsis.
            Assert.Equal(2, title.TextLayout.TextLines.Count);
            Assert.DoesNotContain(sheet.GetVisualDescendants().OfType<Button>(),
                button => button.IsEffectivelyVisible && button.Content as string == "Done");
            Tap(window, close);
            Assert.False(shell.Chat.IsRunSettingsSheetOpen);
            shell.Chat.Reset(Guid.Empty, "New chat");
            Assert.Equal("New chat", sheet.Title);
        });

    [Fact]
    public Task InteractiveChoicePickerUsesTheSameChromeAndFinishesItsExit() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            var root = Required<Panel>(view, "Root");
            var form = new StrataInteractiveBlock
            {
                TouchMode = true, PickerHost = root,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(20),
                Definition = InteractiveDefinition.Parse("form",
                    """{"id":"preview","title":"Preferences","fields":[{"id":"pace","label":"Response style","type":"select","value":"Balanced","help":"Choose your preferred style.","options":["Concise","Balanced","Detailed","Step-by-step","Technical","Conversational"]}]}""")
            };
            root.Children.Add(form);
            PumpFrame(window);
            Tap(window, Required<Button>(form, "Field_pace"));
            await Task.Delay(260);
            Pump(window);
            var picker = root.Children.OfType<StrataBottomSheet>()
                .Single(sheet => sheet.Name == "InteractionPicker");
            var surface = Required<Border>(picker, "PART_Sheet");
            Assert.Equal(new CornerRadius(28), surface.CornerRadius);
            Assert.Equal("Choose your preferred style.", picker.Description);
            Assert.True(Required<Button>(picker, "PART_CloseButton").Bounds.Width >= 48);
            var frost = Required<StrataBackdropBlur>(picker, "PART_Backdrop");
            Assert.True(frost.IsEffectivelyVisible);
            Assert.Equal(28, frost.BlurRadius, precision: 1);
            Assert.Equal(surface.Bounds, frost.Bounds);
            Assert.True(shell.IsModalSheetPresented);
            var options = picker.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Name?.StartsWith("PickerOption_", StringComparison.Ordinal) == true)
                .ToArray();
            Assert.All(options, option => Assert.True(option.Bounds.Height >= 48));
            var current = Assert.Single(options, option => option.Classes.Contains("selected"));
            Assert.Equal("Balanced", AutomationProperties.GetName(current));
            Assert.True(current.GetVisualDescendants().OfType<PathIcon>()
                .Single(icon => icon.Classes.Contains("sheet-check")).IsEffectivelyVisible);
            Tap(window, options.Single(option => AutomationProperties.GetName(option) == "Concise"));
            Assert.Equal("Concise", InteractiveState.Parse(form.StateJson!).Values["pace"]);
            Assert.False(picker.IsOpen);
            Assert.True(picker.IsPresented);
            await Task.Delay(StrataBottomSheet.CloseDuration + TimeSpan.FromMilliseconds(60));
            Assert.DoesNotContain(picker, root.Children);
            Assert.False(shell.IsModalSheetPresented);
        });

    [Theory]
    [InlineData(320)]
    [InlineData(412)]
    public Task DatePickerHasReadableTouchRowsAndKeepsAllSevenColumnsReachable(double width) =>
        RunUiAsync(width, async (shell, view, window) =>
        {
            var root = Required<Panel>(view, "Root");
            var form = new StrataInteractiveBlock
            {
                TouchMode = true, PickerHost = root,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(20),
                Definition = InteractiveDefinition.Parse("form",
                    """{"id":"trip","title":"Trip","fields":[{"id":"date","label":"Start date","type":"date","value":"2026-10-13"}]}""")
            };
            root.Children.Add(form);
            PumpFrame(window);
            Tap(window, Required<Button>(form, "Field_date"));
            await Task.Delay(260);
            PumpFrame(window);
            var picker = root.Children.OfType<StrataBottomSheet>()
                .Single(sheet => sheet.Name == "InteractionPicker");
            var surface = Required<Border>(picker, "PART_Sheet");
            var days = picker.GetVisualDescendants()
                .OfType<Avalonia.Controls.Primitives.CalendarDayButton>().ToArray();
            Assert.Equal(42, days.Length);
            Assert.All(days, day =>
            {
                Assert.True(day.Bounds.Height >= 48);
                Assert.True(day.Bounds.Width >= 40);
                var left = day.TranslatePoint(default, surface)!.Value.X;
                Assert.True(left >= 0 && left + day.Bounds.Width <= surface.Bounds.Width);
            });
            foreach (var name in new[] { "PART_PreviousButton", "PART_NextButton" })
                Assert.True(Required<Button>(picker, name).Bounds.Width >= 48);
            Tap(window, days.Single(day => (string?)day.Content == "14"));
            Assert.Equal("2026-10-14", InteractiveState.Parse(form.StateJson!).Values["date"]);
            await Task.Delay(StrataBottomSheet.CloseDuration + TimeSpan.FromMilliseconds(60));
            Assert.DoesNotContain(picker, root.Children);
            Assert.False(shell.IsModalSheetPresented);
        });

    [Theory]
    [InlineData(320)]
    [InlineData(1112)]
    public Task EveryPopupFloatsInsideSafeBoundsWithAFullScreenScrim(double width) =>
        RunUiAsync(width, async (shell, view, window) =>
        {
            view.ApplyPlatformInsets(new Thickness(12, 28, 8, 24));
            Pump(window);
            foreach (var page in new[] { MobilePage.Library, MobilePage.Settings, MobilePage.Chat })
            {
                shell.Page = page;
                Pump(window);
            }
            var sheets = view.GetVisualDescendants().OfType<StrataBottomSheet>().ToArray();
            Assert.Equal(15, sheets.Length);
            foreach (var sheet in sheets)
            {
                shell.Page = sheet.GetVisualAncestors().OfType<LibraryView>().Any() ? MobilePage.Library
                    : sheet.GetVisualAncestors().OfType<MobileSettingsView>().Any() ? MobilePage.Settings
                    : MobilePage.Chat;
                sheet.SetCurrentValue(StrataBottomSheet.IsOpenProperty, true);
                PumpFrame(window);
                await Task.Delay(260);
                PumpFrame(window);
                // Beside a two-pane chat the plan, sources and activity are a side panel with margins of their own.
                Assert.Equal(sheet.Name == "ProjectPickerSheet" ? shell.ProjectPickerSheetMargin
                    : sheet.Classes.Contains("side-panel") ? shell.SidePanelSheetMargin
                    : shell.SafeAreaSheetMargin, sheet.SheetMargin);
                var surface = Required<Border>(sheet, "PART_Sheet");
                var scrim = Required<Border>(sheet, "PART_Scrim");
                var bounds = new Rect(surface.Bounds.Size).TransformToAABB(surface.TransformToVisual(sheet)!.Value);
                Assert.True(bounds.Left >= sheet.SheetMargin.Left - 0.1, sheet.Name);
                Assert.True(bounds.Right <= sheet.Bounds.Width - sheet.SheetMargin.Right + 0.1, sheet.Name);
                Assert.True(bounds.Top >= sheet.SheetMargin.Top - 0.1, sheet.Name);
                Assert.Equal(sheet.Bounds.Height - sheet.SheetMargin.Bottom, bounds.Bottom, precision: 1);
                Assert.InRange(surface.Bounds.Width, 1, 560);
                Assert.Equal(new CornerRadius(28), surface.CornerRadius);
                Assert.Equal(new Thickness(1), surface.BorderThickness);
                Assert.True(Required<Border>(sheet, "PART_Handle").Bounds.Height >= 48);
                Assert.Equal(sheet.Bounds.Size, scrim.Bounds.Size);
                var close = Required<Button>(sheet, "PART_CloseButton");
                Assert.True(close.IsEffectivelyVisible);
                Assert.True(close.Bounds.Width >= 48 && close.Bounds.Height >= 48);
                Assert.Equal("Close", AutomationProperties.GetName(close));
                // Glass belongs to the popup only: the frost matches the sheet, not the screen.
                var backdrop = Required<StrataBackdropBlur>(sheet, "PART_Backdrop");
                Assert.True(backdrop.IsEffectivelyVisible);
                Assert.Equal(28, backdrop.BlurRadius, precision: 1);
                Assert.Equal(surface.Bounds, backdrop.Bounds);
                Assert.Equal(surface.CornerRadius, backdrop.CornerRadius);
                Assert.Contains(Required<Border>(sheet, "PART_SheetMotion"), backdrop.GetVisualAncestors());
                // And it is glass, as the desktop's menus are: the page shows through the sheet, frosted,
                // behind a light dim rather than a dark one.
                var tint = Assert.IsAssignableFrom<Avalonia.Media.IGradientBrush>(surface.Background);
                Assert.All(tint.GradientStops, stop => Assert.InRange(stop.Color.A, (byte)0x90, (byte)0xC0));
                Assert.InRange(Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(scrim.Background).Color.A,
                    (byte)0, (byte)0x40);

                // Tap drains the UI and can finish a short exit; observe its start in Closed.
                var retainedAtClose = false;
                EventHandler<Avalonia.Interactivity.RoutedEventArgs> onClosed =
                    (_, _) => retainedAtClose = sheet.IsPresented;
                sheet.Closed += onClosed;
                Tap(window, close);
                sheet.Closed -= onClosed;
                Assert.False(sheet.IsOpen);
                Assert.True(retainedAtClose);
                PumpFrame(window);
                await Task.Delay(StrataBottomSheet.CloseDuration + TimeSpan.FromMilliseconds(60));
                PumpFrame(window);
                Assert.False(sheet.IsPresented);
                Assert.False(backdrop.IsEffectivelyVisible);
            }

            shell.Page = MobilePage.Chat;
            shell.OpenChatActionsCommand.Execute(new ChatListItemViewModel(new RemoteChat
            {
                Id = shell.Chat.ChatId, Title = "Rename this chat"
            }));
            shell.RenameActionChatCommand.Execute(null);
            view.ApplyInputPaneGeometry(InputPaneState.Open, new Rect(0, 500, width, 300));
            await Task.Delay(260);
            Pump(window);
            var actions = Required<StrataBottomSheet>(view, "ChatActionsSheet");
            var actionSurface = Required<Border>(actions, "PART_Sheet");
            var actionBottom = actionSurface.TranslatePoint(new Point(0, actionSurface.Bounds.Height), view)!.Value.Y;
            Assert.InRange(actionBottom, 460, 489);
            var save = Required<Button>(actions, "SaveChatNameButton");
            var saveBottom = save.TranslatePoint(new Point(0, save.Bounds.Height), view)!.Value.Y;
            Assert.True(saveBottom <= actionBottom);
        });

    [Fact]
    public Task LoadedChatWaitsForRealizationBeforeSpendingItsEntrance() =>
        RunUiAsync(360, (shell, view, window) =>
        {
            shell.Chat.Reset(Guid.NewGuid(), "A fresh conversation");
            shell.Chat.ApplyTranscript(new RemoteTranscript
            {
                ChatId = shell.Chat.ChatId, Revision = 1,
                Turns = [new RemoteTranscriptTurn
                {
                    Id = "ready-turn",
                    Items = [new RemoteTranscriptItem
                    {
                        Id = "ready-answer", Kind = RemoteProtocol.ItemKinds.Assistant, Text = "Ready to read."
                    }]
                }]
            });
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
            var body = Required<Border>(view, "ChatTranscriptSideInset");
            Assert.False(body.IsHitTestVisible);
            Assert.Equal(0, body.Opacity);
            Assert.False(SlideFadeEntrance.IsCompositedRunning(body));
            PumpFrame(window);
            Assert.True(body.IsHitTestVisible);
            Assert.Equal(1, body.Opacity);
            Assert.True(SlideFadeEntrance.IsCompositedRunning(body));
            Assert.False(Required<Border>(view, "ChatLoadingOverlay").IsVisible);
            return Task.CompletedTask;
        });

    /// <summary>
    /// A chat that takes a moment to load shows its outline breathing over the canvas, where Lumi's
    /// presence keeps glowing: the overlay paints no backdrop of its own, which on a dark or light canvas
    /// read as a black or white box over the glow. The chat it replaces has already faded out.
    /// </summary>
    [Fact]
    public Task ASlowLoadShowsItsOutlineOverThePresenceRatherThanABox() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            // As the shell opens a chat it has to fetch from the PC.
            shell.Chat.Reset(Guid.NewGuid(), "A slow conversation", isLoading: true);
            Assert.True(shell.Chat.IsInitialLoading);
            await Task.Delay(180);
            Dispatcher.UIThread.RunJobs();
            var overlay = Required<Border>(view, "ChatLoadingOverlay");
            Assert.True(overlay.IsVisible);
            Assert.True(overlay.Background is null
                        || overlay.Background is Avalonia.Media.ISolidColorBrush { Color.A: 0 },
                $"the loading overlay paints {overlay.Background} over the canvas.");
            Assert.Equal(0, Required<Border>(view, "ChatTranscriptSideInset").Opacity);
        });

    [Fact]
    public Task TabletProjectPickerKeepsTheDraftVisibleWithoutRaisingNativeTextOverThePopup() =>
        RunUiAsync(1100, async (shell, view, window) =>
        {
            const string draft = "Keep this draft visible.\nIt should not disappear behind project selection.";
            var composer = Required<StrataChatComposer>(view, "Composer");
            var editor = Assert.IsType<NativeComposerEditorHost>(composer.EditorContent);
            shell.Chat.PromptText = draft;
            editor.SetContentHeightFromNative(96);
            await Task.Delay(300);
            Pump(window);
            var height = composer.Bounds.Height;
            Tap(window, Required<Button>(view, "DrawerProjectButton"));
            await Task.Delay(280);
            Pump(window);

            var fallback = Required<TextBox>(composer, "PART_Input");
            Assert.False(editor.IsVisible);
            Assert.True(fallback.IsEffectivelyVisible);
            Assert.Equal(draft, fallback.Text);
            Assert.True(fallback.IsReadOnly);
            Assert.False(fallback.IsHitTestVisible);
            Assert.False(NativeTextInputOverlay.GetIsEnabled(fallback));
            Assert.Same(editor, composer.EditorContent);
            Assert.Equal(height, composer.Bounds.Height, precision: 1);

            shell.GoBackCommand.Execute(null);
            await Task.Delay(StrataBottomSheet.CloseDuration + TimeSpan.FromMilliseconds(60));
            Pump(window);
            Assert.True(editor.IsVisible);
            Assert.False(fallback.IsVisible);
            Assert.Equal(draft, editor.Text);
            Assert.Same(editor, composer.EditorContent);

            var projectButton = Required<Button>(view, "DrawerProjectButton");
            Assert.True(projectButton.Focus());
            editor.FocusAtEnd();
            Assert.True(editor.IsInputFocused);
            Assert.False(projectButton.IsFocused);
            Assert.False(shell.IsProjectPickerOpen);
        }, nativeFactory: new FocusEditorFactory());

    [Fact]
    public Task NativeComposerWaitsForClosingPopupsAndIgnoresHiddenPageSheets() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            var editor = Assert.IsType<NativeComposerEditorHost>(
                Required<StrataChatComposer>(view, "Composer").EditorContent);
            shell.Chat.IsModelSheetOpen = true;
            await Task.Delay(260);
            Assert.True(shell.IsModalSheetPresented);
            Assert.False(editor.IsVisible);
            shell.Chat.IsModelSheetOpen = false;
            Assert.True(shell.IsModalSheetPresented);
            Assert.False(editor.IsVisible);
            await Task.Delay(StrataBottomSheet.CloseDuration + TimeSpan.FromMilliseconds(60));
            Assert.False(shell.IsModalSheetPresented);
            Assert.True(editor.IsVisible);

            shell.OpenChatActionsCommand.Execute(new ChatListItemViewModel(new RemoteChat
            {
                Id = shell.Chat.ChatId, Title = "Chat actions"
            }));
            await Task.Delay(260);
            shell.CloseChatActionsCommand.Execute(null);
            Assert.False(editor.IsVisible);
            await Task.Delay(StrataBottomSheet.CloseDuration + TimeSpan.FromMilliseconds(60));
            Assert.True(editor.IsVisible);

            shell.Page = MobilePage.Library;
            shell.Library.IsRowActionsOpen = true;
            Pump(window);
            await Task.Delay(260);
            Assert.True(shell.IsModalSheetPresented);
            shell.Page = MobilePage.Chat;
            Pump(window);
            Assert.True(Required<StrataBottomSheet>(view, "LibraryActionsSheet").IsPresented);
            Assert.False(shell.IsModalSheetPresented);
            Assert.True(editor.IsVisible);
        }, new FocusEditorFactory());

    [Fact]
    public Task NativeComposerReturnsWhenAPresentedSheetIsRemoved() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            var editor = Assert.IsType<NativeComposerEditorHost>(
                Required<StrataChatComposer>(view, "Composer").EditorContent);
            var root = Required<Panel>(view, "ChatRoot");
            var sheet = new StrataBottomSheet { Title = "Departure", Content = new Border { Height = 200 } };
            root.Children.Add(sheet);
            sheet.IsOpen = true;
            Pump(window);
            await Task.Delay(260);
            Assert.True(shell.IsModalSheetPresented);
            Assert.False(editor.IsVisible);

            // An interactive answer's picker leaves the tree when it closes.
            root.Children.Remove(sheet);
            Pump(window);
            Assert.False(shell.IsModalSheetPresented);
            Assert.True(editor.IsVisible);
        }, new FocusEditorFactory());

    [Fact]
    public Task FoldSidebarExitRemainsVisibleAboveTheReservedHingePane() =>
        RunUiAsync(884, async (shell, view, window) =>
        {
            view.Posture = Lumi.Mobile.Layout.FoldPosture.BookVerticalHinge;
            view.HingeSize = 24;
            view.HingePosition = 430;
            Pump(window);
            var drawer = Required<StrataNavigationDrawer>(view, "NavDrawer");
            var lead = Required<Border>(view, "NavigationSpacer");
            var chat = Required<ChatDetailView>(view, "ChatSurface");
            var hinge = Required<Border>(view, "HingeGap");
            void AssertHingeGeometry()
            {
                Assert.Equal(430, lead.Bounds.Width);
                Assert.Equal(430, hinge.TranslatePoint(default, drawer)!.Value.X);
                Assert.Equal(24, hinge.Bounds.Width);
                Assert.Equal(454, chat.TranslatePoint(default, drawer)!.Value.X);
                Assert.Equal(884, chat.TranslatePoint(new Point(chat.Bounds.Width, 0), drawer)!.Value.X);
            }
            await Task.Delay(220);
            var width = chat.Bounds.Width;
            drawer.RaiseEvent(new EdgeDragEventArgs(EdgeDragGestureRecognizer.EdgeDragEvent, -64, 600));
            Pump(window);
            AssertHingeGeometry();
            Assert.InRange(drawer.Progress, 0.001, 0.999);
            drawer.RaiseEvent(new EdgeDragEventArgs(EdgeDragGestureRecognizer.EdgeDragEvent, 64, 664));
            drawer.RaiseEvent(new EdgeDragEndedEventArgs(EdgeDragGestureRecognizer.EdgeDragEndedEvent, 0));
            shell.ToggleDrawerCommand.Execute(null);
            Pump(window);
            Assert.True(lead.IsVisible);
            Assert.Equal(430, lead.Bounds.Width);
            await Task.Delay(55);
            Assert.InRange(drawer.Progress, 0.001, 0.999);
            Assert.Equal(width, chat.Bounds.Width);
            AssertHingeGeometry();
            await Task.Delay(200);
            Pump(window);
            Assert.Equal(0, drawer.Progress);
            Assert.Equal(width, chat.Bounds.Width);
            AssertHingeGeometry();
        });

    [Fact]
    public Task ChatTitlePillCarriesTheChatsIdentityAndProjectOnOneLine() =>
        RunUiAsync(360, (shell, view, window) =>
        {
            var controls = Required<Button>(view, "HeaderDetailsButton");
            var title = Required<TextBlock>(view, "HeaderTitleText");
            var project = Required<TextBlock>(view, "HeaderProjectText");
            var mark = Required<Border>(view, "HeaderMark");
            Assert.Equal("HeaderControlsIcon", Assert.Single(controls.GetVisualDescendants().OfType<PathIcon>()).Name);
            Assert.Equal(shell.Chat.Title, title.Text);
            Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, title.TextTrimming);
            Assert.False(project.IsVisible, "a chat outside a project names none");
            Assert.False(mark.IsVisible, "a chat with neither project nor agent has no mark");

            shell.Chat.ApplyStatus(new RemoteChatStatus
            {
                ChatId = shell.Chat.ChatId, Model = "gpt-4.1",
                ProjectId = Guid.NewGuid(), ProjectName = "Lumi"
            });
            Pump(window);
            // As on the desktop, the project follows the title on its line, quieter, and gives way first:
            // the two share a baseline. Its initial marks the chat on its hue.
            Assert.True(project.IsVisible);
            Assert.Equal("Lumi", project.Text);
            Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, project.TextTrimming);
            static double Baseline(TextBlock text, Visual root) =>
                text.TranslatePoint(new Point(0, text.TextLayout.TextLines[0].Baseline), root)!.Value.Y;
            Assert.InRange(Math.Abs(Baseline(title, view) - Baseline(project, view)), 0, 0.75);
            Assert.True(mark.IsVisible);
            Assert.Equal("L", mark.GetVisualDescendants().OfType<TextBlock>().Single().Text);
            Assert.Equal($"Chat details: {shell.Chat.Title}", AutomationProperties.GetName(controls));
            return Task.CompletedTask;
        });

    [Theory]
    [InlineData(320, false)]
    [InlineData(884, true)]
    [InlineData(1100, false)]
    public Task NavigationAndChatControlsShareOneVerticalAlignment(double width, bool hinge) =>
        RunUiAsync(width, async (shell, view, window) =>
        {
            if (hinge)
            {
                view.Posture = Lumi.Mobile.Layout.FoldPosture.BookVerticalHinge;
                view.HingePosition = 430;
                view.HingeSize = 24;
            }
            view.ApplyPlatformInsets(new Thickness(0, 28, 0, 24));
            Pump(window);
            shell.IsNavigationOpen = true;
            await Task.Delay(280);
            Pump(window);

            var controls = new List<Control>
            {
                Required<Button>(view, "DrawerSearchButton"),
                Required<Button>(view, "MenuButton"),
                Required<Button>(view, "HeaderDetailsButton"),
                // New chat is the top right of the chat's bar in every layout.
                Required<Button>(view, "NewChatButton")
            };
            var centers = controls.Select(control =>
                control.TranslatePoint(new Point(0, control.Bounds.Height / 2), view)!.Value.Y).ToArray();
            Assert.InRange(centers.Max() - centers.Min(), 0, 1);
            Assert.Equal(new Thickness(16, 8, 16, 8), Required<Grid>(view, "DrawerBrandHeader").Margin);
            Assert.Equal(Required<Grid>(view, "TopBar").Margin, Required<Grid>(view, "DrawerBrandHeader").Margin);
            Assert.Equal(48, Required<Button>(view, "HeaderDetailsButton").Bounds.Height);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CompactComposerCentersItsContentAndKeepsSmallerActionsTouchReachable(bool dark) =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            window.RequestedThemeVariant = dark
                ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
            shell.Chat.IsBusy = true;
            await Task.Delay(300);
            Pump(window);
            var composer = Required<StrataChatComposer>(view, "Composer");
            var input = Required<TextBox>(composer, "PART_Input");
            var attach = Required<Button>(composer, "ComposerAttachButton");
            var send = Required<Button>(composer, "PART_SendButton");
            Point Center(Control control) =>
                control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), composer)!.Value;

            Assert.True(composer.IsCompact);
            Assert.InRange(composer.Bounds.Height, 50, 56);
            Assert.Equal(composer.Bounds.Height / 2, Center(input).Y, precision: 1);
            Assert.Equal(Center(input).Y, Center(attach).Y, precision: 1);
            Assert.Equal(Center(attach).Y, Center(send).Y, precision: 1);
            Assert.Equal(Center(attach).X, composer.Bounds.Width - Center(send).X, precision: 1);
            Assert.Equal(input.Padding.Top, input.Padding.Bottom);
            Assert.Equal(Avalonia.Layout.VerticalAlignment.Center, input.VerticalContentAlignment);

            var sendSurface = Required<Border>(send, "PART_Surface");
            Assert.Equal(38, sendSurface.Bounds.Width, precision: 1);
            Assert.Equal(48, send.Bounds.Width, precision: 1);
            var foreground = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(send.Foreground).Color;
            foreach (var name in new[] { "PART_SendIcon", "PART_StopIcon" })
                Assert.Equal(foreground, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(
                    Required<Avalonia.Controls.Shapes.Path>(send, name).Fill).Color);
            // The desktop's orb: its line glyphs, and while Lumi works a dark disc with a stop square
            // ringed by a breath that hugs the disc rather than the whole target.
            Assert.True(Required<Panel>(send, "PART_OrbGlyphs").IsEffectivelyVisible);
            Assert.False(Required<Viewbox>(send, "PART_SendGlyphs").IsEffectivelyVisible);
            Assert.True(Required<Border>(send, "PART_OrbStopIcon").IsEffectivelyVisible);
            var halo = Required<Border>(composer, "PART_SendHalo");
            Assert.True(halo.IsEffectivelyVisible);
            Assert.InRange(halo.Bounds.Width, sendSurface.Bounds.Width, sendSurface.Bounds.Width + 6);
            // The + answers the orb across the composer: a disc of the same size, its whole target tappable.
            Assert.Equal(sendSurface.Bounds.Width, Required<Border>(attach, "PART_Surface").Bounds.Width, precision: 1);
            Assert.Equal(48, attach.Bounds.Width, precision: 1);

            var edge = send.TranslatePoint(new Point(1, send.Bounds.Height / 2), window)!.Value;
            var hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(edge));
            Assert.True(ReferenceEquals(hit, send) || hit.GetVisualAncestors().Contains(send));

            shell.Chat.IsBusy = false;
            Tap(window, Required<Button>(view, "NewChatButton"), new Point(1, 24));
            Assert.False(shell.Chat.HasChat);
        });

    [Fact]
    public Task ComposerStartsAsOneRowAndAnimatesFocusWithoutReplacingItsEditor() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            var composer = Required<StrataChatComposer>(view, "Composer");
            var input = Required<TextBox>(composer, "PART_Input");
            var toolbar = Required<Grid>(composer, "PART_Toolbar");
            var layout = Required<Panel>(composer, "PART_ComposerLayout");
            var actions = Required<WrapPanel>(composer, "PART_SecondaryActions");
            var send = Required<Button>(composer, "PART_SendButton");
            var attach = Required<Button>(composer, "ComposerAttachButton");
            Pump(window);
            await Task.Delay(280);
            Pump(window);
            var compactHeight = composer.Bounds.Height;
            var transitions = layout.Transitions;
            Assert.True(composer.IsCompact);
            Assert.InRange(compactHeight, 48, 76);
            Assert.Equal(0, actions.Opacity);
            Assert.False(actions.IsEffectivelyEnabled);
            Assert.True(send.IsEffectivelyVisible);
            Assert.True(attach.IsEffectivelyVisible);
            Assert.True(send.Bounds.Height >= 48);
            var inputRight = input.TranslatePoint(new Point(input.Bounds.Width, 0), composer)!.Value.X;
            Assert.True(inputRight <= send.TranslatePoint(default, composer)!.Value.X);
            Assert.True(input.TranslatePoint(default, composer)!.Value.X
                        >= attach.TranslatePoint(new Point(attach.Bounds.Width, 0), composer)!.Value.X);

            // Every frame the composer lays out while it grows, so the test sees the animation itself
            // rather than one moment of it, which a delay that overruns on a busy runner misses.
            var heights = new List<double>();
            void RecordHeight(object? sender, EventArgs e) => heights.Add(composer.Bounds.Height);
            composer.LayoutUpdated += RecordHeight;
            Assert.True(input.Focus());
            Pump(window);
            Assert.False(composer.IsCompact);
            // The secondary tools are fully revealed only once the expansion has finished.
            await Eventually(() => actions.Opacity == 1);
            Pump(window);
            composer.LayoutUpdated -= RecordHeight;
            var expandedHeight = composer.Bounds.Height;
            Assert.Contains(heights, height => height > compactHeight + 1 && height < expandedHeight - 1);
            Assert.True(expandedHeight >= compactHeight + 40);
            Assert.True(actions.IsEffectivelyEnabled);
            var editorBottom = input.TranslatePoint(new Point(0, input.Bounds.Height), composer)!.Value.Y;
            Assert.InRange(toolbar.TranslatePoint(default, composer)!.Value.Y - editorBottom, 0, 6);
            Assert.Same(input, Required<TextBox>(composer, "PART_Input"));
            Assert.Same(transitions, layout.Transitions);

            var settings = Required<Button>(view, "ComposerModelButton");
            settings.Focus();
            Pump(window);
            Assert.False(composer.IsCompact);
            Required<Button>(view, "MenuButton").Focus();
            Pump(window);
            await Eventually(() => Math.Abs(composer.Bounds.Height - compactHeight) < 0.05);
            Assert.True(composer.IsCompact);
            Assert.Empty(shell.Chat.PromptText);
        });

    [Fact]
    public Task TappingAwayReleasesFocusAndKeepsAMultilineDraftExpanded() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            var composer = Required<StrataChatComposer>(view, "Composer");
            var input = Required<TextBox>(composer, "PART_Input");
            const string draft = "A first thought\nA second thought\nRoom to finish";
            shell.Chat.PromptText = draft;
            input.Focus();
            await Task.Delay(340);
            Pump(window);
            var expandedHeight = composer.Bounds.Height;

            Tap(window, Required<Button>(view, "HeaderDetailsButton"));
            Assert.False(input.IsFocused);
            Assert.False(composer.IsCompact);
            Assert.Equal(draft, input.Text);
            shell.Chat.ClosePickerSheetCommand.Execute(null);
            await Task.Delay(340);
            Pump(window);
            Assert.False(input.IsFocused);
            Assert.False(composer.IsCompact);
            Assert.Equal(expandedHeight, composer.Bounds.Height, precision: 1);
        });

    [Fact]
    public Task EmptyComposerReturnsToIdleAfterATapAwayAndSendReleasesFocus() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            var composer = Required<StrataChatComposer>(view, "Composer");
            var input = Required<TextBox>(composer, "PART_Input");
            input.Focus();
            await Task.Delay(340);
            Tap(window, Required<Button>(view, "HeaderDetailsButton"));
            Assert.False(input.IsFocused);
            Assert.True(composer.IsCompact);
            shell.Chat.ClosePickerSheetCommand.Execute(null);
            await Task.Delay(340);
            Pump(window);
            Assert.False(input.IsFocused);

            shell.Chat.PromptText = "Send this thought";
            input.Focus();
            await Task.Delay(340);
            Pump(window);
            Tap(window, Required<Button>(composer, "PART_SendButton"));
            Assert.False(input.IsFocused);
        });

    [Fact]
    public Task FirstSendFromBlankChatBlursNativeEditorBeforeCreatedChatArrives() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            var chatId = Guid.NewGuid();
            var received = new TaskCompletionSource<RemoteCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
            var response = new TaskCompletionSource<RemoteCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var desktop = new FakeLumiDesktop
            {
                CommandResultFactory = command =>
                {
                    if (command?.Action != RemoteProtocol.Actions.SendMessage)
                        return new RemoteCommandResult { Ok = true };
                    received.TrySetResult(command);
                    return response.Task.GetAwaiter().GetResult();
                }
            };
            desktop.Start();
            await PairAsync(shell, desktop);
            shell.ChatList.NewChatCommand.Execute(null);
            var composer = Required<StrataChatComposer>(view, "Composer");
            var editor = Assert.IsType<NativeComposerEditorHost>(composer.EditorContent);
            shell.Chat.PromptText = "Start a new conversation";
            editor.FocusAtEnd();
            await Task.Delay(340);
            Pump(window);
            Assert.Equal(Guid.Empty, shell.Chat.ChatId);
            Assert.True(editor.IsInputFocused);

            try
            {
                Tap(window, Required<Button>(composer, "PART_SendButton"));
                Assert.False(editor.IsInputFocused);
                Assert.True(composer.IsCompact);
                var command = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(command.GetBool("newChat"));
                Assert.Null(command.Get("chatId"));
                Assert.Equal(Guid.Empty, shell.Chat.ChatId);
                var send = Assert.IsAssignableFrom<Task>(shell.Chat.SendCommand.ExecutionTask);
                Assert.False(send.IsCompleted);
                Assert.True(shell.Chat.ShowThinking);
                Assert.True(Required<StrataTypingIndicator>(view, "ChatTyping").IsEffectivelyVisible);
                var echo = Required<ItemsControl>(view, "Transcript").GetVisualDescendants()
                    .OfType<ItemsControl>().Single(control => control.DataContext == shell.Chat.Turns[^1]);
                Assert.True(SlideFadeEntrance.IsCompositedRunning(echo.ContainerFromIndex(0)!));

                response.SetResult(new RemoteCommandResult { Ok = true, ChatId = chatId });
                await send.WaitAsync(TimeSpan.FromSeconds(5));
                Pump(window);
                Assert.Equal(chatId, shell.Chat.ChatId);
                Assert.False(editor.IsInputFocused);
                Assert.True(composer.IsCompact);
            }
            finally
            {
                response.TrySetResult(new RemoteCommandResult { Ok = true, ChatId = chatId });
                if (shell.Chat.SendCommand.ExecutionTask is { } send)
                    await send.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }, new FocusEditorFactory());

    [Fact]
    public Task ReversingComposerFocusRetainsItsTransitionAndDoesNotJumpToTheOldTarget() =>
        RunUiAsync(360, async (_, view, window) =>
        {
            var composer = Required<StrataChatComposer>(view, "Composer");
            var input = Required<TextBox>(composer, "PART_Input");
            var layout = Required<Panel>(composer, "PART_ComposerLayout");
            await Task.Delay(340);
            var transitions = layout.Transitions;
            var compactHeight = composer.Bounds.Height;
            input.Focus();
            Pump(window);
            await Task.Delay(65);
            Pump(window);
            Assert.InRange(composer.Bounds.Height, compactHeight + 1, compactHeight + 47);
            Required<Button>(view, "MenuButton").Focus();
            Pump(window);
            Assert.Same(transitions, layout.Transitions);
            Assert.True(composer.Bounds.Height > compactHeight);
            await Task.Delay(35);
            input.Focus();
            Pump(window);
            await Task.Delay(340);
            Pump(window);
            Assert.False(composer.IsCompact);
            Assert.True(composer.Bounds.Height >= compactHeight + 40);
            Assert.Same(transitions, layout.Transitions);
        });

    [Fact]
    public Task ComposerKeepsStopAndSelectedContextTouchReachableWithoutFocus() =>
        RunUiAsync(320, async (shell, view, window) =>
        {
            var composer = Required<StrataChatComposer>(view, "Composer");
            var projectId = Guid.NewGuid();
            shell.Chat.ApplyLibraryCatalogs(new RemoteLibrary
            {
                Projects = [new RemoteProject { Id = projectId, Name = "Lumi" }]
            });
            shell.Chat.SelectFileCommand.Execute(@"C:\fixture\notes.txt");
            shell.Chat.ApplyStatus(new RemoteChatStatus
            {
                ChatId = shell.Chat.ChatId, IsBusy = true, ProjectId = projectId, ProjectName = "Lumi"
            });
            composer.StopCommand = null;
            var stops = 0;
            composer.StopRequested += (_, _) => stops++;
            Pump(window);
            await Task.Delay(280);
            Pump(window);
            Assert.False(composer.IsCompact);
            Assert.Equal("Lumi", shell.Chat.ProjectName);
            // The chat's project shows in the composer, as on the desktop, beside its staged file.
            Assert.Equal("Lumi", composer.ProjectName);
            Assert.True(Required<Button>(composer, "ComposerProjectChip").IsEffectivelyVisible);
            Assert.False(Required<ScrollViewer>(composer, "PART_ContextScroll").IsVisible);
            Assert.Equal("Lumi", Required<TextBlock>(view, "HeaderProjectText").Text);
            Assert.True(Required<ItemsControl>(composer, "PendingAttachments").IsEffectivelyVisible);
            Assert.True(Required<StrataTypingIndicator>(view, "ChatTyping").IsEffectivelyVisible);
            Tap(window, Required<Button>(composer, "PART_SendButton"));
            Assert.Equal(1, stops);
            Assert.False(composer.IsCompact);
            Assert.Single(shell.Chat.Attachments);
        });

    [Fact]
    public Task NativeFocusKeyboardDismissalAndResumePreserveTheEditorAndDraft() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            var composer = Required<StrataChatComposer>(view, "Composer");
            var editor = Assert.IsType<NativeComposerEditorHost>(composer.EditorContent);
            Assert.True(composer.IsCompact);
            Assert.Equal(48, editor.Height);
            var draft = "First line\nSecond line\nThird line\nFourth line";
            shell.Chat.PromptText = draft;
            Pump(window);
            Assert.False(composer.IsCompact);

            editor.FocusAt(7);
            view.ApplyInputPaneGeometry(InputPaneState.Open, new Rect(0, 500, 360, 300));
            editor.SetContentHeightFromNative(120);
            await Task.Delay(280);
            Pump(window);
            Assert.False(composer.IsCompact);
            Assert.Equal(120, editor.Height, precision: 1);
            Assert.Equal(7, editor.CaretIndex);

            view.ApplyInputPaneGeometry(InputPaneState.Closed, default);
            await Task.Delay(340);
            Pump(window);
            Assert.False(editor.IsInputFocused);
            Assert.False(composer.IsCompact);
            Assert.Equal(120, editor.Height, precision: 1);
            Assert.Equal(draft, editor.Text);
            editor.FocusAt(7);
            view.ApplyInputPaneGeometry(InputPaneState.Open, new Rect(0, 500, 360, 300));
            await Task.Delay(280);
            Pump(window);
            Assert.False(composer.IsCompact);
            Assert.Equal(120, editor.Height, precision: 1);

            view.NotifyApplicationDeactivated();
            Assert.False(editor.IsInputFocused);
            view.ApplyInputPaneGeometry(InputPaneState.Open, new Rect(0, 500, 360, 300));
            Pump(window);
            Assert.False(composer.IsCompact);
            Assert.Same(editor, composer.EditorContent);
            Assert.Equal(draft, shell.Chat.PromptText);
            Assert.Equal(7, editor.CaretIndex);
            editor.SetInputFocusFromNative(false);
            Pump(window);
            Assert.False(composer.IsCompact);
            shell.Chat.IsRunSettingsSheetOpen = true;
            await Task.Delay(280);
            Pump(window);
            Assert.False(editor.IsVisible);
            Assert.True(composer.Bounds.Height >= 120);
            shell.Chat.IsRunSettingsSheetOpen = false;
            Pump(window);
            Assert.False(editor.IsVisible);
            await Task.Delay(StrataBottomSheet.CloseDuration + TimeSpan.FromMilliseconds(60));
            Pump(window);
            Assert.True(editor.IsVisible);
            Assert.Same(editor, composer.EditorContent);
            Assert.Equal(draft, editor.Text);
            shell.Chat.PromptText = "";
            editor.SetContentHeightFromNative(40);
            await Task.Delay(340);
            Pump(window);
            Assert.True(composer.IsCompact);
            Assert.Equal(48, editor.Height);
        }, new FocusEditorFactory());

    private static void ApplyComposerContext(MobileShellViewModel shell, string projectName = "Lumi")
    {
        var projectId = Guid.NewGuid();
        shell.Chat.ApplyLibraryCatalogs(new RemoteLibrary
        {
            Projects = [new RemoteProject { Id = projectId, Name = projectName }],
            Lumis = [new RemoteLumi { Id = Guid.NewGuid(), Name = "Coding Lumi", IconGlyph = "\u26A1" }],
            Skills =
            [
                new RemoteSkill { Id = Guid.NewGuid(), Name = "A detailed research skill with a long descriptive name", IconGlyph = "\U0001F50E" },
                new RemoteSkill { Id = Guid.NewGuid(), Name = "Debug Expert", IconGlyph = "\U0001F41E" }
            ],
            McpServers = [new RemoteMcpServer { Id = Guid.NewGuid(), Name = "GitHub", IsEnabled = true }]
        });
        shell.Chat.ApplyStatus(new RemoteChatStatus
        {
            ChatId = shell.Chat.ChatId,
            ProjectId = projectId,
            ProjectName = projectName,
            AgentName = "Coding Lumi",
            AgentGlyph = "\u26A1",
            SkillNames = ["A detailed research skill with a long descriptive name"],
            McpNames = ["GitHub"]
        });
    }

    [Fact]
    public Task TheComposersChipsShowWhatTheChatCarriesAndOpenTheirPickers() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            var composer = Required<StrataChatComposer>(view, "Composer");
            var strip = Required<ScrollViewer>(composer, "ComposerContextScroll");
            await Task.Delay(280);
            Pump(window);
            // A chat that carries nothing keeps the one-row composer.
            Assert.False(strip.IsEffectivelyVisible);
            var compactHeight = composer.Bounds.Height;

            ApplyComposerContext(shell, "A very long project name that should not make the composer wider");
            await Task.Delay(280);
            Pump(window);
            Assert.True(strip.IsEffectivelyVisible);
            // The desktop composer's own chip row has no place for connectors and only a hover-size
            // remove button, so on a phone the host's chips stand in for it.
            Assert.False(Required<ScrollViewer>(composer, "PART_ContextScroll").IsVisible);
            var chips = composer.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("context-chip")).ToList();
            // The project, agent and skills; the chat's connectors stay behind +, as the desktop keeps
            // them behind its Connectors button.
            Assert.Equal(["project", "agent", "skill"],
                chips.Select(chip => chip.Classes.First(name => name is "project" or "agent" or "skill" or "connector")));
            Assert.NotEmpty(shell.Chat.McpChips);

            foreach (var theme in new[] { Avalonia.Styling.ThemeVariant.Light, Avalonia.Styling.ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme;
                await Task.Delay(200);
                Pump(window);
                var surfaces = chips.Select(chip => chip.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Classes.Contains("context-chip-surface"))).ToList();
                foreach (var chip in chips)
                    Assert.Equal(48, chip.Bounds.Height, precision: 1);
                foreach (var surface in surfaces)
                {
                    // One quiet, translucent pill in the desktop's proportions; only its small tile wears a hue.
                    Assert.Equal(32, surface.Bounds.Height, precision: 1);
                    Assert.True(surface.Bounds.Width <= 210, $"a chip is {surface.Bounds.Width:0.#} dp wide");
                    Assert.Equal(surfaces[0].CornerRadius, surface.CornerRadius);
                    var fill = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(surface.Background).Color;
                    Assert.Equal(Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(surfaces[0].Background).Color, fill);
                    Assert.InRange(fill.A, 1, 60);
                }
                var hues = chips.Where(chip => !chip.Classes.Contains("agent"))
                    .Select(chip => Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(
                        chip.GetVisualDescendants().OfType<StrataLineIcon>().Single().Foreground).Color)
                    .ToList();
                Assert.Equal(hues.Count, hues.Distinct().Count());
            }
            Assert.All(chips.SelectMany(chip => chip.GetVisualDescendants().OfType<TextBlock>()
                    .Where(text => text.Classes.Contains("context-chip-name"))),
                name => Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, name.TextTrimming));
            // The chips add one row; a long one scrolls sideways instead of taking another.
            Assert.InRange(composer.Bounds.Height - compactHeight, 46, 54);

            // A chip opens its own picker, with what the chat has already checked.
            Tap(window, chips[0]);
            Assert.True(shell.Chat.IsContextPickerOpen);
            Assert.Equal(ContextPickerKind.Project, shell.Chat.ContextPickerKind);
            shell.Chat.IsContextPickerOpen = false;
            await Task.Delay(320);
            Pump(window);
            chips[2].BringIntoView();
            await Task.Delay(100);
            Pump(window);
            Tap(window, chips[2]);
            Assert.Equal(ContextPickerKind.Skills, shell.Chat.ContextPickerKind);
            Assert.Contains(shell.Chat.ContextOptions, option => option.IsSelected);
            Assert.Equal("Skill: A detailed research skill with a long descriptive name", AutomationProperties.GetName(chips[2]));
        });

    [Fact]
    public Task PlusOpensTheAddSheetWhoseRowsReachEveryKindOfContext() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            ApplyComposerContext(shell);
            var picks = 0;
            shell.Chat.AttachmentPickRequested += () => picks++;
            await Task.Delay(280);
            Pump(window);

            Tap(window, Required<Button>(view, "ComposerAttachButton"));
            await Task.Delay(320);
            Pump(window);
            Assert.True(shell.Chat.IsAddSheetOpen);
            string Value(string row) => Required<Button>(view, row).GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Classes.Contains("sheet-value")).Text!;
            foreach (var name in new[] { "AddFilesButton", "AddAgentButton", "AddProjectButton", "AddSkillsButton", "AddConnectorsButton" })
            {
                var row = Required<Button>(view, name);
                Assert.True(row.IsEffectivelyVisible && row.IsEffectivelyEnabled, name);
                Assert.True(row.Bounds.Height >= 48, $"{name} is {row.Bounds.Height:0.#} dp");
            }
            Assert.Equal("Coding Lumi", Value("AddAgentButton"));
            Assert.Equal("Lumi", Value("AddProjectButton"));
            Assert.Equal("A detailed research skill with a long descriptive name", Value("AddSkillsButton"));
            Assert.Equal("GitHub", Value("AddConnectorsButton"));

            Tap(window, Required<Button>(view, "AddSkillsButton"));
            await Task.Delay(320);
            Pump(window);
            Assert.False(shell.Chat.IsAddSheetOpen);
            Assert.True(shell.Chat.IsContextPickerOpen);
            var rows = Required<ItemsControl>(view, "ContextPickerList").GetVisualDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("context-option")).ToList();
            Assert.Equal(2, rows.Count);
            // Skills are a multi-select: a check, without the single choice's selection band.
            Assert.Contains("checked", rows[0].Classes);
            Assert.DoesNotContain("selected", rows[0].Classes);
            Assert.DoesNotContain("checked", rows[1].Classes);
            Tap(window, rows[1]);
            Assert.Contains("checked", rows[1].Classes);
            Assert.Equal("2 skills", shell.Chat.SkillsSummary);
            Assert.True(shell.Chat.IsContextPickerOpen);

            shell.Chat.IsContextPickerOpen = false;
            shell.Chat.OpenAddSheetCommand.Execute(null);
            await Task.Delay(320);
            Pump(window);
            Tap(window, Required<Button>(view, "AddFilesButton"));
            Assert.False(shell.Chat.IsAddSheetOpen);
            Assert.Equal(1, picks);
        });

    [Fact]
    public Task WorkingIndicatorRemainsVisibleAfterPreambleAndWhileReadingEarlierContent() =>
        RunUiAsync(360, (shell, view, window) =>
        {
            var chat = shell.Chat;
            chat.ApplyStatus(new RemoteChatStatus { ChatId = chat.ChatId, IsBusy = true });
            Assert.True(chat.ShowThinking);
            chat.ApplyTranscript(new RemoteTranscript
            {
                ChatId = chat.ChatId, Revision = 1,
                Status = new RemoteChatStatus { ChatId = chat.ChatId, IsBusy = true, StatusText = "Running tools" },
                Turns = [new RemoteTranscriptTurn
                {
                    Id = "running",
                    Items = [new RemoteTranscriptItem { Id = "preamble", Text = "I will check that for you." }]
                }]
            });
            Pump(window);
            Assert.False(chat.ShowThinking);
            Assert.True(chat.IsWorking);
            var indicator = Required<StrataTypingIndicator>(view, "ChatTyping");
            Assert.True(indicator.IsActive);
            Assert.True(indicator.IsEffectivelyVisible);
            Assert.Equal("Running tools", indicator.Label);
            Assert.Empty(Required<ItemsControl>(view, "Transcript").GetVisualDescendants().OfType<StrataTypingIndicator>());

            chat.IsLatestWindow = false;
            var scroller = Required<ScrollViewer>(view, "PART_TranscriptScroll");
            scroller.Offset = new Vector(0, 0);
            Pump(window);
            var bottom = indicator.TranslatePoint(new Point(0, indicator.Bounds.Height), window)!.Value.Y;
            Assert.InRange(bottom, 0, window.ClientSize.Height);
            chat.StatusText = new string('x', 200);
            Pump(window);
            var label = indicator.GetVisualDescendants().OfType<TextBlock>().Single();
            Assert.True(label.TranslatePoint(new Point(label.Bounds.Width, 0), window)!.Value.X <= window.ClientSize.Width);

            chat.ApplyStatus(new RemoteChatStatus { ChatId = chat.ChatId, IsBusy = true, IsStreaming = true });
            Pump(window);
            Assert.Equal("Writing…", indicator.Label);
            chat.ApplyStatus(new RemoteChatStatus { ChatId = chat.ChatId });
            Pump(window);
            Assert.False(indicator.IsEffectivelyVisible);
            return Task.CompletedTask;
        });

    [Fact]
    public Task NativeComposerHeightGrowsShrinksAndCapsWithoutChangingItsDraft() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            var editor = new NativeComposerEditorHost { Text = "First\nSecond\nThird\nFourth" };
            var composer = Required<StrataChatComposer>(view, "Composer");
            composer.EditorContent = editor;
            Pump(window);
            var initial = composer.Bounds.Height;
            Assert.Equal(48, editor.Height);
            editor.SetContentHeightFromNative(122.5);
            await Task.Delay(280);
            Pump(window);
            Assert.Equal(123, editor.Height);
            Assert.True(composer.Bounds.Height > initial + 50);
            Assert.Equal("First\nSecond\nThird\nFourth", editor.Text);

            editor.SetContentHeightFromNative(1000);
            await Task.Delay(280);
            Pump(window);
            Assert.Equal(176, editor.Height);
            editor.SetContentHeightFromNative(40);
            await Task.Delay(280);
            Pump(window);
            Assert.Equal(48, editor.Height);
            Assert.Equal(initial, composer.Bounds.Height);
        });

    [Fact]
    public Task NativeEditorFitsLargeTextWithoutLosingTheDraftWhenFocusEnds() =>
        RunUiAsync(360, async (_, view, window) =>
        {
            var composer = Required<StrataChatComposer>(view, "Composer");
            var editor = new NativeComposerEditorHost { Text = "Keep\nEvery\nLine" };
            composer.EditorContent = editor;
            editor.SetContentHeightFromNative(120);
            await Task.Delay(280);
            Assert.Equal(120, editor.Height);

            editor.SetInputFocusFromNative(true);
            editor.SetContentHeightFromNative(68.2);
            await Task.Delay(280);
            Pump(window);
            Assert.Equal(69, editor.Height);
            Assert.Equal("Keep\nEvery\nLine", editor.Text);

            editor.Blur();
            Assert.False(editor.IsInputFocused);
            Assert.Equal(69, editor.Height);
            editor.SetContentHeightFromNative(47);
            await Task.Delay(280);
            Assert.Equal(48, editor.Height);
        });

    [Fact]
    public Task SharedComposerExpandsForMultilineTextAndRemainsBounded() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            var composer = Required<StrataChatComposer>(view, "Composer");
            var input = Required<TextBox>(composer, "PART_Input");
            shell.Chat.PromptText = "One line";
            input.Focus();
            await Task.Delay(280);
            Pump(window);
            var initial = input.Bounds.Height;
            shell.Chat.PromptText = "One\nTwo\nThree\nFour\nFive";
            Pump(window);
            Assert.True(input.Bounds.Height > initial + 32);
            shell.Chat.PromptText = string.Join('\n', Enumerable.Repeat("Long draft", 40));
            Pump(window);
            Assert.InRange(input.Bounds.Height, initial + 32, 176);
            shell.Chat.PromptText = "";
            Pump(window);
            Assert.Equal(initial, input.Bounds.Height);
            var draft = $"Keep every word of a long line {new string('x', 100)}\nEvery\nLine";
            shell.Chat.PromptText = draft;
            Required<Button>(view, "MenuButton").Focus();
            await Task.Delay(280);
            Pump(window);
            Assert.False(composer.IsCompact);
            Assert.True(input.Bounds.Height > 48);
            Assert.Equal(draft, shell.Chat.PromptText);
        });

    [Fact]
    public async Task HeadlessDispatchWaitsForTheWholeAsyncInteraction()
    {
        using var session = HeadlessMobileSession.Start();
        var completed = false;
        await session.Dispatch(async () =>
        {
            await Task.Delay(25);
            completed = true;
        }, CancellationToken.None);
        Assert.True(completed, "The test must not finish before its post-await interaction assertions.");
    }

    [Fact]
    public async Task OnboardingBackReturnsToPcSelectionWithoutLeavingTheApp()
    {
        await using var shell = new MobileShellViewModel(store: new MemoryStore(), post: action => action());
        shell.Connect.Step = ConnectStep.EnterCode;
        Assert.True(shell.CanGoBack);
        shell.GoBackCommand.Execute(null);
        Assert.Equal(ConnectStep.FindPc, shell.Connect.Step);
        Assert.False(shell.CanGoBack);
        Assert.False(shell.IsPaired);
    }

    [Fact]
    public async Task FailedChatRefreshKeepsRowsAndOffersAnHonestRetryState()
    {
        var sink = new PageSink();
        using var list = new ChatListViewModel(sink);
        list.Apply([new RemoteChatGroup
        {
            Label = "Today",
            Chats = [new RemoteChat { Id = Guid.NewGuid(), Title = "Keep my place" }]
        }]);

        await list.RefreshFromServerAsync();

        Assert.NotNull(list.LoadErrorText);
        Assert.Single(list.Groups[0].Chats);
        Assert.False(list.ShowEmptyState);
        Assert.False(list.IsRefreshing);

        sink.Page = new RemoteChatPage();
        await list.RetryLoadCommand.ExecuteAsync(null);

        Assert.Null(list.LoadErrorText);
        Assert.True(list.ShowEmptyState);
        Assert.Equal("Your next idea starts here", list.EmptyTitle);
    }

    [Fact]
    public async Task SearchAcknowledgesTypingBeforeDebounceAndSeparatesNoResultsFromAnEmptyLibrary()
    {
        var sink = new PageSink
        {
            DeferredPage = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        using var list = new ChatListViewModel(sink);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        list.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatListViewModel.IsRefreshing) && !list.IsRefreshing)
                completed.TrySetResult();
        };

        list.SearchText = "weekend";
        Assert.True(list.IsRefreshing);
        Assert.Equal("Searching your chats...", list.ResultsLabel);
        Assert.False(list.ShowEmptyState);

        sink.DeferredPage.SetResult(new RemoteChatPage { Query = "weekend" });
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(list.ShowEmptyState);
        Assert.Equal("No chats found", list.EmptyTitle);
        Assert.Equal("0 results", list.ResultsLabel);
        sink.DeferredPage = null;
        sink.Page = new RemoteChatPage();
        list.ClearSearchCommand.Execute(null);
        Assert.False(list.HasSearchQuery);
    }

    [Fact]
    public void SwitchingChatsClearsTransientDetailsButKeepsTheSavedDraft()
    {
        var chat = new MobileChatViewModel(new PageSink(), action => action());
        var first = Guid.NewGuid();
        chat.Reset(first, "First chat");
        chat.PromptText = "My unfinished thought";
        chat.ContextCurrentTokens = 90000;
        chat.ContextTokenLimit = 100000;
        chat.StatusText = "Working on the previous chat";
        chat.IsRunSettingsSheetOpen = true;
        chat.IsPlanOpen = true;

        chat.Reset(Guid.Empty, "New chat");

        Assert.False(chat.HasContextUsage);
        Assert.False(chat.HasOpenSheet);
        Assert.Null(chat.StatusText);

        chat.Reset(first, "First chat");
        Assert.Equal("My unfinished thought", chat.PromptText);
    }

    [Fact]
    public async Task ChatDeletionRequiresConfirmationAndKeepsTheSheetOpenOnFailure()
    {
        await using var desktop = new FakeLumiDesktop
        {
            CommandResultFactory = _ => new RemoteCommandResult { Error = "The PC couldn't delete this chat." }
        };
        desktop.Start();
        await using var shell = new MobileShellViewModel(store: new MemoryStore(), post: action => action());
        await PairAsync(shell, desktop);
        var chat = new ChatListItemViewModel(new RemoteChat { Id = Guid.NewGuid(), Title = "Important chat" });

        shell.OpenChatActionsCommand.Execute(chat);
        shell.DeleteActionChatCommand.Execute(null);
        Assert.Empty(desktop.ReceivedCommands);
        Assert.True(shell.IsConfirmingChatDelete);

        shell.GoBackCommand.Execute(null);
        Assert.False(shell.IsConfirmingChatDelete);
        Assert.True(shell.IsChatActionsOpen);
        Assert.Empty(desktop.ReceivedCommands);

        shell.DeleteActionChatCommand.Execute(null);
        await shell.ConfirmDeleteActionChatCommand.ExecuteAsync(null);

        var command = Assert.Single(desktop.ReceivedCommands);
        Assert.Equal(RemoteProtocol.Actions.DeleteChat, command.Action);
        Assert.Equal(chat.Id, command.GetGuid("chatId"));
        Assert.True(shell.IsChatActionsOpen);
        Assert.True(shell.IsConfirmingChatDelete);
        Assert.Equal("The PC couldn't delete this chat.", shell.ChatActionError);
        Assert.False(shell.IsChatActionBusy);
    }

    [Fact]
    public async Task RenameUsesTheSelectedChatAndUpdatesItsVisibleTitleOnlyAfterSuccess()
    {
        await using var desktop = new FakeLumiDesktop();
        desktop.Start();
        await using var shell = new MobileShellViewModel(store: new MemoryStore(), post: action => action());
        await PairAsync(shell, desktop);
        var chat = new ChatListItemViewModel(new RemoteChat { Id = Guid.NewGuid(), Title = "Old name" });
        shell.Chat.Reset(chat.Id, chat.Title);
        shell.OpenChatActionsCommand.Execute(chat);
        shell.RenameActionChatCommand.Execute(null);

        Assert.Equal("Old name", shell.ChatNameDraft);
        shell.ChatNameDraft = "   ";
        Assert.False(shell.SaveChatNameCommand.CanExecute(null));
        shell.ChatNameDraft = "  Weekend plans  ";
        Assert.Equal("Old name", chat.Title);

        await shell.SaveChatNameCommand.ExecuteAsync(null);

        var command = Assert.Single(desktop.ReceivedCommands);
        Assert.Equal(RemoteProtocol.Actions.RenameChat, command.Action);
        Assert.Equal(chat.Id, command.GetGuid("chatId"));
        Assert.Equal("Weekend plans", command.Get("title"));
        Assert.Equal("Weekend plans", shell.Chat.Title);
        Assert.Equal("Weekend plans", chat.Title);
        Assert.False(shell.IsChatActionsOpen);
    }

    [Fact]
    public async Task DeletingTheOpenChatFromItsDetailsReturnsToTheList()
    {
        var id = Guid.NewGuid();
        await using var desktop = new FakeLumiDesktop();
        desktop.Snapshot.Chats.Groups = [new RemoteChatGroup
        {
            Label = "Today",
            Chats = [new RemoteChat { Id = id, Title = "Old plan" }]
        }];
        desktop.CommandResultFactory = command =>
        {
            if (command?.Action == RemoteProtocol.Actions.DeleteChat)
                desktop.ChatCatalog = [];
            return new RemoteCommandResult { Ok = true };
        };
        desktop.Start();
        await using var shell = new MobileShellViewModel(store: new MemoryStore(), post: action => action());
        await PairAsync(shell, desktop);
        shell.UpdateLayout(393, 852);
        await Eventually(() => shell.ChatList.FindLoadedChat(id) is not null);
        shell.Chat.Reset(id, "Old plan");
        shell.IsDrawerOpen = false;

        shell.DeleteOpenChatCommand.Execute(null);
        await shell.ConfirmDeleteActionChatCommand.ExecuteAsync(null);

        var delete = Assert.Single(desktop.ReceivedCommands, command => command.Action == RemoteProtocol.Actions.DeleteChat);
        Assert.Equal(id, delete.GetGuid("chatId"));
        // The hidden list hears of no deletion, so the chat leaves at once and the list comes back without it.
        Assert.Equal(Guid.Empty, shell.Chat.ChatId);
        Assert.False(shell.IsChatActionsOpen);
        Assert.True(shell.IsDrawerOpen);
        await Eventually(() => shell.ChatList.FindLoadedChat(id) is null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackDoesNotReopenADeletedChatFromSearchHistory(bool throughSettings)
    {
        var original = new RemoteChat { Id = Guid.NewGuid(), Title = "Original conversation" };
        var found = new RemoteChat { Id = Guid.NewGuid(), Title = "Found conversation" };
        await using var desktop = new FakeLumiDesktop
        {
            Transcript = new RemoteTranscript
            {
                ChatId = found.Id,
                Title = found.Title,
                Status = new RemoteChatStatus { ChatId = found.Id }
            }
        };
        desktop.Snapshot.Chats.Groups = [new RemoteChatGroup { Label = "Today", Chats = [original, found] }];
        desktop.CommandResultFactory = command =>
        {
            if (command?.Action == RemoteProtocol.Actions.DeleteChat)
            {
                desktop.Snapshot.Chats.Groups = [new RemoteChatGroup { Label = "Today", Chats = [found] }];
                desktop.ChatCatalog = desktop.Snapshot.Chats.Groups;
            }
            return new RemoteCommandResult { Ok = true };
        };
        desktop.Start();
        await using var shell = new MobileShellViewModel(store: new MemoryStore(), post: action => action());
        await PairAsync(shell, desktop);
        shell.UpdateLayout(393, 852);
        shell.Chat.Reset(original.Id, original.Title);
        shell.IsDrawerOpen = false;
        if (throughSettings)
            shell.SelectPlace(MobilePlace.Settings);
        shell.OpenSearchCommand.Execute(null);
        shell.SearchChatList.OpenChatCommand.Execute(new ChatListItemViewModel(found));
        await shell.RefreshTranscriptAsync();
        shell.GoBackCommand.Execute(null);
        Assert.Equal(MobilePage.Search, shell.Page);

        shell.OpenChatActionsCommand.Execute(new ChatListItemViewModel(original));
        shell.DeleteActionChatCommand.Execute(null);
        await shell.ConfirmDeleteActionChatCommand.ExecuteAsync(null);
        Assert.Equal(found.Id, shell.Chat.ChatId);
        Assert.Equal(MobilePage.Search, shell.Page);

        shell.GoBackCommand.Execute(null);
        if (throughSettings)
        {
            Assert.Equal(MobilePage.Settings, shell.Page);
            Assert.Equal(found.Id, shell.Chat.ChatId);
            shell.GoBackCommand.Execute(null);
        }
        Assert.True(shell.IsDrawerOpen, "Back skips the deleted conversation and returns to the chat list.");
        Assert.Equal(found.Id, shell.Chat.ChatId);
    }

    [Fact]
    public async Task PinningTheOpenChatFlipsItsLabelOnceThePcAgrees()
    {
        var id = Guid.NewGuid();
        var served = new RemoteChat { Id = id, Title = "Weekend plans" };
        await using var desktop = new FakeLumiDesktop();
        desktop.Snapshot.Chats.Groups = [new RemoteChatGroup { Label = "Today", Chats = [served] }];
        desktop.CommandResultFactory = command =>
        {
            if (command?.Action == RemoteProtocol.Actions.PinChat)
                served.IsPinned = command.Get("pinned") == bool.TrueString;
            return new RemoteCommandResult { Ok = true };
        };
        desktop.Start();
        await using var shell = new MobileShellViewModel(store: new MemoryStore(), post: action => action());
        await PairAsync(shell, desktop);
        await Eventually(() => shell.ChatList.FindLoadedChat(id) is not null);
        shell.Chat.Reset(id, "Weekend plans");
        Assert.True(shell.CanPinOpenChat);
        Assert.Equal("Pin chat", shell.OpenChatPinLabel);

        await shell.PinOpenChatCommand.ExecuteAsync(null);

        var pin = Assert.Single(desktop.ReceivedCommands, command => command.Action == RemoteProtocol.Actions.PinChat);
        Assert.Equal(id, pin.GetGuid("chatId"));
        Assert.Equal(bool.TrueString, pin.Get("pinned"));
        Assert.True(shell.ChatList.FindLoadedChat(id)!.IsPinned);
        Assert.Equal("Unpin chat", shell.OpenChatPinLabel);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 10)
        {
            Assert.True(waited < 5000, "The expected state was not reached.");
            await Task.Delay(10);
        }
    }

    [Theory]
    [InlineData(320)]
    [InlineData(412)]
    public Task CompactChatControlsIsExplicitAndOpensTheCompleteDetailsSurface(double width) =>
        RunUiAsync(width, async (shell, view, window) =>
        {
            shell.Chat.ContextCurrentTokens = 90000;
            shell.Chat.ContextTokenLimit = 100000;
            shell.Chat.PlanContent = "# Plan\n\n- One useful next step";
            Pump(window);

            var topBar = Required<Grid>(view, "TopBar");
            Assert.Equal(3, topBar.GetVisualDescendants().OfType<Button>().Count());
            var details = Required<Button>(view, "HeaderDetailsButton");
            // The title pill takes the bar between back and New chat.
            Assert.InRange(details.Bounds.Width, 48, width - 140);
            Assert.True(details.Bounds.Height >= 48);
            Assert.Equal($"Chat details: {shell.Chat.Title}", AutomationProperties.GetName(details));
            Tap(window, details);
            await Task.Delay(320);
            Pump(window);

            Assert.True(shell.Chat.IsRunSettingsSheetOpen);
            Assert.False(shell.CanDragDrawer);
            Assert.True(Required<Button>(view, "PlanButton").IsEffectivelyVisible);
            Assert.True(Required<Button>(view, "GitChangesButton").IsEffectivelyVisible);
            Assert.True(Required<TextBlock>(view, "ContextText").IsEffectivelyVisible);

            shell.GoBackCommand.Execute(null);
            Assert.False(shell.Chat.HasOpenSheet);
        });

    [Fact]
    public Task SettingsActionsDoNotSqueezeTheirDescriptionsAndDisconnectIsReversible() =>
        RunUiAsync(320, async (shell, view, window) =>
        {
            shell.Page = MobilePage.Settings;
            Pump(window);
            var settings = Required<MobileSettingsView>(view, "SettingsPage");
            // A phone lists Settings' pages first; the connection is one of them.
            Assert.False(shell.ShowSettingsDetail);
            Assert.False(Required<Grid>(settings, "SettingsDetailPane").IsVisible);
            await Task.Delay(320);
            Pump(window);
            Tap(window, Required<Button>(settings, "SettingsConnectionRow"));
            await Task.Delay(320);
            Pump(window);
            Assert.Equal(SettingsSection.Connection, shell.SettingsSection);
            var description = settings.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Text == "Bring your chats and library up to date.");
            Assert.True(description.Bounds.Width >= 200, $"Description width: {description.Bounds.Width}");
            var disconnect = Required<Button>(settings, "ForgetButton");
            disconnect.BringIntoView();
            await Task.Delay(320);
            Pump(window);
            Tap(window, disconnect);
            await Task.Delay(320);
            Pump(window);

            Assert.True(shell.IsDisconnectConfirmationOpen);
            Assert.True(shell.IsPaired);
            Assert.True(shell.CanGoBack);
            Assert.False(shell.CanDragDrawer);
            shell.GoBackCommand.Execute(null);
            Assert.False(shell.IsDisconnectConfirmationOpen);
            Assert.True(shell.IsPaired);
        });

    [Theory]
    [InlineData(false, 320)]
    [InlineData(true, 320)]
    [InlineData(true, 900)]
    public async Task BrowserSignInRecoveryIsAvailableOnlyInTheWebApp(bool webApp, double width)
    {
        var previousHost = MobilePlatformServices.HostEnvironment;
        var reloads = 0;
        MobilePlatformServices.HostEnvironment =
            new ReloadableWebHost(webApp ? () => reloads++ : null);
        try
        {
            await RunUiAsync(width, async (shell, view, window) =>
            {
                shell.IsConnected = false;
                shell.IsHostReady = false;
                shell.IsGatewaySignInRequired = true;
                shell.ConnectionMessage = "Sign in again. Pairing is kept; copy your draft before reloading.";
                shell.Chat.PromptText = "An unsent draft";
                await Task.Delay(320);
                Pump(window);
                Assert.Equal("Sign-in required", shell.ConnectionStateLabel);
                Assert.False(Required<Control>(view, "ConnectionRetryPulse").IsEffectivelyVisible);
                var recovery = Required<Button>(view, "GatewaySignInButton");
                Assert.Equal(webApp, recovery.IsEffectivelyVisible);
                Assert.False(shell.CanAutomaticallyRecoverGatewaySignIn);
                if (webApp)
                {
                    Assert.True(recovery.Bounds.Width >= 48 && recovery.Bounds.Height >= 48);
                    Assert.True(recovery.TranslatePoint(
                        new Point(recovery.Bounds.Width, 0), view)!.Value.X <= view.Bounds.Width);
                    Tap(window, recovery);
                    Assert.Equal(1, reloads);
                    Assert.True(shell.IsPaired);
                    Assert.Equal("An unsent draft", shell.Chat.PromptText);
                }

                shell.IsGatewaySignInRequired = false;
                shell.IsConnected = true;
                shell.IsHostReady = true;
                Pump(window);
                Assert.False(Required<Control>(view, "ConnectionBanner").IsEffectivelyVisible);
                Assert.False(recovery.IsEffectivelyVisible);

                // Browser sign-in lives on Settings' Connection page.
                shell.Page = MobilePage.Settings;
                shell.OpenSettingsSectionCommand.Execute("Connection");
                Pump(window);
                Assert.Equal(webApp, shell.CanReloadWebApp);
                Assert.Equal(webApp, shell.ReloadWebAppCommand.CanExecute(null));
                Assert.Equal(webApp, Required<Control>(view, "ReloadWebAppSetting").IsEffectivelyVisible);
                if (webApp)
                {
                    var button = Required<Button>(view, "ReloadWebAppButton");
                    button.BringIntoView();
                    await Task.Delay(320);
                    Pump(window);
                    Tap(window, button);
                    Assert.Equal(2, reloads);
                    Assert.True(shell.IsPaired);
                }
            });
        }
        finally
        {
            MobilePlatformServices.HostEnvironment = previousHost;
        }
    }

    [Fact]
    public async Task AutomaticBrowserSignInRecoveryProtectsCurrentAndInactiveChatWork()
    {
        var previousHost = MobilePlatformServices.HostEnvironment;
        MobilePlatformServices.HostEnvironment = new ReloadableWebHost(() => { });
        try
        {
            await RunUiAsync(320, (shell, _, _) =>
            {
                Assert.True(shell.CanAutomaticallyRecoverGatewaySignIn);
                var originalChat = shell.Chat.ChatId;
                shell.Chat.PromptText = "Keep this draft";
                Assert.False(shell.CanAutomaticallyRecoverGatewaySignIn);

                shell.Chat.Reset(Guid.NewGuid(), "Another chat");
                Assert.Empty(shell.Chat.PromptText);
                Assert.False(shell.CanAutomaticallyRecoverGatewaySignIn);
                shell.Chat.Reset(originalChat, "Original chat");
                Assert.Equal("Keep this draft", shell.Chat.PromptText);
                shell.Chat.PromptText = "";
                Assert.True(shell.CanAutomaticallyRecoverGatewaySignIn);

                shell.Chat.IsUploading = true;
                Assert.False(shell.CanAutomaticallyRecoverGatewaySignIn);
                shell.Chat.IsUploading = false;
                shell.Page = MobilePage.Settings;
                Assert.False(shell.CanAutomaticallyRecoverGatewaySignIn);
                return Task.CompletedTask;
            });
        }
        finally
        {
            MobilePlatformServices.HostEnvironment = previousHost;
        }
    }

    [Theory]
    [InlineData(false, 320)]
    [InlineData(true, 320)]
    [InlineData(true, 900)]
    public async Task BrowserUpdateBannerKeepsDraftsAndOffersSafeApplyAndRetryOnEveryPage(bool webApp, double width)
    {
        var previousHost = MobilePlatformServices.HostEnvironment;
        var applies = 0;
        var checks = 0;
        MobilePlatformServices.HostEnvironment = new ReloadableWebHost(
            reload: null,
            apply: webApp ? () => applies++ : null,
            check: webApp ? () => checks++ : null);
        try
        {
            await RunUiAsync(width, async (shell, view, window) =>
            {
                shell.WebAppUpdateState = WebAppUpdateState.Ready;
                shell.Chat.PromptText = "Keep this draft while updating";
                foreach (var (page, name) in new[]
                         {
                             (MobilePage.Chat, "ChatUpdateBanner"),
                             (MobilePage.Library, "LibraryUpdateBanner"),
                             (MobilePage.Settings, "SettingsUpdateBanner"),
                             (MobilePage.Search, "SearchUpdateBanner")
                         })
                {
                    shell.Page = page;
                    await Task.Delay(260);
                    Pump(window);
                    var banner = Required<WebAppUpdateBannerView>(view, name);
                    Assert.Equal(webApp, banner.IsEffectivelyVisible);
                    if (!webApp)
                        continue;
                    var apply = Required<Button>(banner, "ApplyWebAppUpdateButton");
                    Assert.True(apply.IsEffectivelyEnabled);
                    Assert.Equal("Keep this draft while updating", shell.Chat.PromptText);

                    Assert.Contains(banner.GetVisualDescendants().OfType<TextBlock>(),
                        text => text.Text == "Update ready");
                    Assert.True(apply.Bounds.Width >= 48 && apply.Bounds.Height >= 48);
                    Assert.True(banner.TranslatePoint(
                        new Point(banner.Bounds.Width, 0), view)!.Value.X <= view.Bounds.Width);

                    Tap(window, apply);
                    Pump(window);
                    Assert.Equal(0, applies);
                    Assert.Equal("Keep this draft while updating", shell.Chat.PromptText);
                    var cancel = Required<Button>(banner, "CancelWebAppUpdateButton");
                    Assert.True(cancel.IsEffectivelyVisible);
                    Tap(window, cancel);
                    Pump(window);
                    Assert.True(apply.IsEffectivelyVisible);
                    Assert.Equal("Keep this draft while updating", shell.Chat.PromptText);

                    shell.WebAppUpdateState = WebAppUpdateState.Failed;
                    await Task.Delay(80);
                    Pump(window);
                    Tap(window, Required<Button>(banner, "CheckWebAppUpdateButton"));
                    shell.WebAppUpdateState = WebAppUpdateState.Ready;
                }
                Assert.Equal(webApp ? 4 : 0, checks);
                shell.Page = MobilePage.Chat;
                shell.Chat.PromptText = "";
                await Task.Delay(260);
                Pump(window);
                Assert.Equal(webApp, shell.CanApplyWebAppUpdate);
                Assert.Equal(webApp, shell.ApplyWebAppUpdateCommand.CanExecute(null));
                if (webApp)
                    Tap(window, Required<Button>(
                        Required<WebAppUpdateBannerView>(view, "ChatUpdateBanner"), "ApplyWebAppUpdateButton"));
                Assert.Equal(webApp ? 1 : 0, applies);
                Assert.True(shell.IsPaired);
                shell.WebAppUpdateState = WebAppUpdateState.Current;
                Pump(window);
                Assert.False(Required<WebAppUpdateBannerView>(view, "ChatUpdateBanner").IsEffectivelyVisible);
            });
        }
        finally
        {
            MobilePlatformServices.HostEnvironment = previousHost;
        }
    }

    [Fact]
    public async Task BrowserManualUpdateRequiresConfirmationAndKeepsWorkIfActivationFails()
    {
        var previousHost = MobilePlatformServices.HostEnvironment;
        var applies = 0;
        MobilePlatformServices.HostEnvironment = new ReloadableWebHost(null, () => applies++, () => { });
        try
        {
            await RunUiAsync(320, async (shell, view, window) =>
            {
                var originalChat = shell.Chat.ChatId;
                shell.Chat.PromptText = "Keep my other chat's draft";
                shell.Chat.Reset(Guid.NewGuid(), "Another chat");
                shell.WebAppUpdateState = WebAppUpdateState.Ready;
                await Task.Delay(260);
                Pump(window);
                var banner = Required<WebAppUpdateBannerView>(view, "ChatUpdateBanner");
                Tap(window, Required<Button>(banner, "ApplyWebAppUpdateButton"));
                Assert.True(shell.IsWebAppUpdateConfirmationOpen);
                Assert.False(shell.CanApplyWebAppUpdate);
                Assert.Equal(0, applies);

                var confirm = Required<Button>(banner, "ConfirmWebAppUpdateButton");
                shell.BeginPendingRemoteAction();
                Assert.False(confirm.IsEffectivelyEnabled);
                shell.EndPendingRemoteAction();
                Assert.True(confirm.IsEffectivelyEnabled);
                shell.Chat.IsUploading = true;
                Assert.False(confirm.IsEffectivelyEnabled);
                shell.Chat.IsUploading = false;
                shell.Chat.IsPickingAttachment = true;
                Assert.False(confirm.IsEffectivelyEnabled);
                shell.Chat.IsPickingAttachment = false;
                Tap(window, confirm);
                Assert.Equal(1, applies);
                Assert.True(shell.CanApplyWebAppUpdate);
                Assert.False(Required<Panel>(view, "Root").IsEffectivelyEnabled);

                shell.BeginPendingRemoteAction();
                Assert.False(shell.CanApplyWebAppUpdate);
                shell.EndPendingRemoteAction();
                Assert.True(shell.CanApplyWebAppUpdate);

                shell.WebAppUpdateState = WebAppUpdateState.Applying;
                shell.WebAppUpdateState = WebAppUpdateState.Failed;
                Assert.True(Required<Panel>(view, "Root").IsEffectivelyEnabled);
                Assert.False(shell.IsWebAppUpdateRequested);
                Assert.False(shell.IsWebAppUpdateConfirmationOpen);
                Assert.False(shell.CanApplyWebAppUpdate);
                shell.Chat.Reset(originalChat, "Original chat");
                Assert.Equal("Keep my other chat's draft", shell.Chat.PromptText);

                shell.WebAppUpdateState = WebAppUpdateState.Ready;
                PumpFrame(window);
                var retryApply = Required<Button>(banner, "ApplyWebAppUpdateButton");
                Assert.True(shell.ApplyWebAppUpdateCommand.CanExecute(null));
                Assert.True(retryApply.IsEffectivelyEnabled);
                Assert.True(retryApply.IsEffectivelyVisible);
                Tap(window, retryApply);
                shell.GoBackCommand.Execute(null);
                PumpFrame(window);
                Assert.False(shell.IsWebAppUpdateConfirmationOpen);
                Assert.False(shell.CanApplyWebAppUpdate);
                Assert.Equal("Keep my other chat's draft", shell.Chat.PromptText);
                Assert.Equal(1, applies);

                Tap(window, Required<Button>(banner, "ApplyWebAppUpdateButton"));
                shell.Page = MobilePage.Settings;
                Assert.False(shell.IsWebAppUpdateConfirmationOpen);
            });
        }
        finally
        {
            MobilePlatformServices.HostEnvironment = previousHost;
        }
    }

    [Fact]
    public async Task BrowserUpdateSafetyTracksInactiveDraftsAttachmentsPickersSheetsEditorsAndLifecycle()
    {
        var previousHost = MobilePlatformServices.HostEnvironment;
        MobilePlatformServices.HostEnvironment = new ReloadableWebHost(null, () => { }, () => { });
        try
        {
            await RunUiAsync(320, async (shell, _, _) =>
            {
                var safetySignals = 0;
                shell.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(shell.CanApplyWebAppUpdate))
                        safetySignals++;
                };
                Assert.True(shell.CanApplyWebAppUpdate);
                var original = shell.Chat.ChatId;
                shell.Chat.PromptText = "Keep my other chat's draft";
                Assert.False(shell.CanApplyWebAppUpdate);
                shell.WebAppUpdateState = WebAppUpdateState.Ready;
                Assert.True(shell.ApplyWebAppUpdateCommand.CanExecute(null));
                shell.Chat.Reset(Guid.NewGuid(), "Another chat");
                Assert.False(shell.CanApplyWebAppUpdate);
                shell.Chat.Reset(original, "Original");
                shell.Chat.PromptText = "";
                Assert.True(shell.CanApplyWebAppUpdate);

                shell.Chat.Attachments.Add(new PendingAttachment("notes.txt", @"C:\uploads\notes.txt"));
                Assert.False(shell.CanApplyWebAppUpdate);
                shell.Chat.Attachments.Clear();
                Assert.True(shell.CanApplyWebAppUpdate);
                shell.Chat.IsPickingAttachment = true;
                Assert.False(shell.CanApplyWebAppUpdate);
                Assert.False(shell.ApplyWebAppUpdateCommand.CanExecute(null));
                shell.Chat.IsPickingAttachment = false;
                shell.Chat.IsUploading = true;
                Assert.False(shell.CanApplyWebAppUpdate);
                Assert.False(shell.ApplyWebAppUpdateCommand.CanExecute(null));
                shell.Chat.IsUploading = false;
                shell.Chat.IsModelSheetOpen = true;
                Assert.False(shell.CanApplyWebAppUpdate);
                shell.Chat.IsModelSheetOpen = false;
                shell.IsProjectPickerOpen = true;
                Assert.False(shell.CanApplyWebAppUpdate);
                shell.IsProjectPickerOpen = false;
                shell.IsChatActionBusy = true;
                Assert.False(shell.CanApplyWebAppUpdate);
                Assert.False(shell.ApplyWebAppUpdateCommand.CanExecute(null));
                shell.IsChatActionBusy = false;

                shell.Library.BeginCreateCommand.Execute(null);
                Assert.True(shell.Library.IsEditing);
                Assert.False(shell.CanApplyWebAppUpdate);
                shell.Library.EditName = "Unsaved project";
                Assert.True(shell.ApplyWebAppUpdateCommand.CanExecute(null));
                shell.Library.CancelEditCommand.Execute(null);
                Assert.False(shell.CanApplyWebAppUpdate);
                shell.Library.DiscardChangesCommand.Execute(null);
                await Task.Delay(300);
                Assert.True(shell.CanApplyWebAppUpdate);

                shell.IsKeyboardOpen = true;
                Assert.False(shell.CanApplyWebAppUpdate);
                shell.IsKeyboardOpen = false;
                shell.Chat.IsBusy = true;
                shell.Chat.IsSessionActive = true;
                Assert.True(shell.CanApplyWebAppUpdate, "Desktop work continues independently of the phone runtime.");
                await shell.NotifyApplicationDeactivatedAsync();
                Assert.False(shell.CanApplyWebAppUpdate);
                Assert.False(shell.ApplyWebAppUpdateCommand.CanExecute(null));
                await shell.NotifyApplicationActivatedAsync();
                Assert.True(shell.CanApplyWebAppUpdate);
                Assert.True(safetySignals >= 12);
            });
        }
        finally
        {
            MobilePlatformServices.HostEnvironment = previousHost;
        }
    }

    [Fact]
    public async Task BrowserUpdatesKeepUnsubmittedQuestionInputAfterTheKeyboardCloses()
    {
        var previousHost = MobilePlatformServices.HostEnvironment;
        MobilePlatformServices.HostEnvironment = new ReloadableWebHost(null, () => { }, () => { });
        try
        {
            await RunUiAsync(360, async (shell, view, window) =>
            {
                shell.Chat.ApplyTranscript(new RemoteTranscript
                {
                    ChatId = shell.Chat.ChatId, Revision = 1,
                    TotalRawMessageCount = 1, WindowEndMessageIndex = 1,
                    Turns = [new RemoteTranscriptTurn
                    {
                        Id = "question-turn",
                        Items = [new RemoteTranscriptItem
                        {
                            Id = "question-row", Kind = RemoteProtocol.ItemKinds.Question,
                            Question = new RemoteQuestion
                            {
                                QuestionId = "question-id", Text = "Which direction should we take?",
                                Options = ["First", "Second"], AllowFreeText = true, AllowMultiSelect = true
                            }
                        }]
                    }]
                });
                Pump(window);
                var card = view.GetVisualDescendants().OfType<StrataQuestionCard>().Single();
                var input = Required<TextBox>(card, "PART_FreeTextBox");
                Assert.True(shell.CanApplyWebAppUpdate);
                bool? signaledSafety = null;
                shell.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(shell.CanApplyWebAppUpdate))
                        signaledSafety = shell.CanApplyWebAppUpdate;
                };
                input.Text = "Keep this unsent answer";
                shell.IsKeyboardOpen = false;
                Pump(window);
                Assert.False(shell.CanApplyWebAppUpdate);
                Assert.False(signaledSafety);
                signaledSafety = null;
                input.Text = "";
                Pump(window);
                Assert.True(shell.CanApplyWebAppUpdate);
                Assert.True(signaledSafety);
                await Task.Delay(80);
                Pump(window);
                var option = card.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Classes.Contains("question-option") && (string?)button.Tag == "First");
                signaledSafety = null;
                Tap(window, option);
                Assert.False(shell.CanApplyWebAppUpdate);
                Assert.False(signaledSafety);
                signaledSafety = null;
                Tap(window, option);
                Assert.True(shell.CanApplyWebAppUpdate);
                Assert.True(signaledSafety);
            });
        }
        finally
        {
            MobilePlatformServices.HostEnvironment = previousHost;
        }
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("option")]
    [InlineData("multiple")]
    [InlineData("free-text")]
    public Task QuestionAnswerReceiptSurvivesRecreatingThePhoneView(string answerKind) =>
        RunUiAsync(390, async (shell, view, window) =>
        {
            var expected = answerKind switch
            {
                "multiple" => "First, Second",
                "free-text" => "My own answer",
                _ => "Second"
            };
            var question = new RemoteQuestion
            {
                QuestionId = "receipt-question", Text = "Which option?",
                Options = ["First", "Second"],
                AllowFreeText = answerKind == "free-text",
                AllowMultiSelect = answerKind == "multiple"
            };
            var transcript = new RemoteTranscript
            {
                ChatId = shell.Chat.ChatId, Revision = 1,
                TotalRawMessageCount = 1, WindowEndMessageIndex = 1,
                Turns = [new RemoteTranscriptTurn
                {
                    Id = "receipt-turn",
                    Items = [new RemoteTranscriptItem
                    {
                        Id = "receipt-row", Kind = RemoteProtocol.ItemKinds.Question,
                        Question = question
                    }]
                }]
            };
            shell.Chat.ApplyTranscript(transcript);
            Pump(window);
            var card = view.GetVisualDescendants().OfType<StrataQuestionCard>().Single();
            var item = Assert.IsType<QuestionItemViewModel>(card.DataContext);
            if (answerKind != "desktop")
            {
                await Task.Delay(80);
                Pump(window);
                Button Option(string label) => card.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Classes.Contains("question-option") && (string?)button.Tag == label);
                if (answerKind == "free-text")
                {
                    var input = Required<TextBox>(card, "PART_FreeTextBox");
                    input.Focus();
                    window.KeyTextInput(expected);
                    Pump(window);
                    Assert.Equal(expected, input.Text);
                    Tap(window, Required<Button>(card, "PART_FreeTextSubmit"));
                }
                else
                {
                    if (answerKind == "multiple")
                        Tap(window, Option("First"));
                    Tap(window, Option("Second"));
                    if (answerKind == "multiple")
                        Tap(window, Required<Button>(card, "PART_MultiSubmit"));
                }
            }
            else
            {
                question.IsAnswered = true;
                question.Answer = expected;
                transcript.Revision = 2;
                shell.Chat.ApplyTranscript(transcript);
                Pump(window);
            }

            Assert.True(item.IsAnswered);
            Assert.Equal(expected, item.Answer);
            Assert.True(Required<DockPanel>(card, "PART_AnsweredRow").IsEffectivelyVisible);
            Assert.Equal(expected, Required<TextBlock>(card, "PART_AnswerText").Text);

            window.Content = null;
            Pump(window);
            var recreated = new MobileShellView { DataContext = shell };
            window.Content = recreated;
            Pump(window);
            var restored = recreated.GetVisualDescendants().OfType<StrataQuestionCard>().Single();
            Assert.True(restored.IsAnswered);
            Assert.Equal(expected, Required<TextBlock>(restored, "PART_AnswerText").Text);
            Assert.True(Required<DockPanel>(restored, "PART_AnsweredRow").IsEffectivelyVisible);
            Assert.False(Required<StackPanel>(restored, "PART_Body").IsEffectivelyVisible);
        });

    [Fact]
    public async Task BrowserUpdatesWaitForInFlightRemoteActions()
    {
        await using var desktop = new FakeLumiDesktop();
        desktop.Start();
        var previousHost = MobilePlatformServices.HostEnvironment;
        MobilePlatformServices.HostEnvironment =
            new ReloadableWebHost(null, () => { }, () => { }, desktop.BaseUrl);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        desktop.CommandResultFactory = _ =>
        {
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The action test did not release its response.");
            return new RemoteCommandResult { Ok = true };
        };
        try
        {
            await RunUiAsync(360, async (shell, _, window) =>
            {
                await PairAsync(shell, desktop);
                Assert.True(shell.CanApplyWebAppUpdate);
                var action = shell.SendCommandAsync(new RemoteCommand(RemoteProtocol.Actions.AnswerQuestion)
                    .With("questionId", "fixture-question").With("answer", "First"));
                await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(shell.CanApplyWebAppUpdate);
                release.Set();
                Assert.True((await action).Ok);
                Pump(window);
                Assert.True(shell.CanApplyWebAppUpdate);
            });
        }
        finally
        {
            release.Set();
            MobilePlatformServices.HostEnvironment = previousHost;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserNavigationWaitsForTheProducedFileBodyAndOpener(bool openerFails)
    {
        var chatId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        await using var desktop = new FakeLumiDesktop
        {
            ProducedFileBytes = "A complete produced file."u8.ToArray(),
            FileBodyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ReleaseFileBody = new(TaskCreationOptions.RunContinuationsAsynchronously),
            Snapshot = new RemoteSnapshot { ActiveChatId = chatId },
            Transcript = new RemoteTranscript
            {
                ChatId = chatId, Revision = 1, TotalRawMessageCount = 1, WindowEndMessageIndex = 1,
                Turns = [new RemoteTranscriptTurn
                {
                    Id = "file-turn",
                    Items = [new RemoteTranscriptItem
                    {
                        Id = "file-row", Kind = RemoteProtocol.ItemKinds.File,
                        Attachments = [new RemoteAttachment
                        {
                            MessageId = messageId, FileName = "review-file.txt", Extension = "txt"
                        }]
                    }]
                }]
            }
        };
        desktop.Start();
        var previousHost = MobilePlatformServices.HostEnvironment;
        var previousOpener = MobilePlatformServices.ProducedFileOpener;
        var opener = new DeferredProducedFileOpener();
        MobilePlatformServices.HostEnvironment = new ReloadableWebHost(
            () => { }, () => { }, () => { }, desktop.BaseUrl);
        MobilePlatformServices.ProducedFileOpener = opener;
        try
        {
            await RunUiAsync(360, async (shell, view, window) =>
            {
                await PairAsync(shell, desktop);
                await desktop.SubscriberConnected.WaitAsync(TimeSpan.FromSeconds(2));
                shell.Chat.Reset(chatId, "File fixture");
                shell.Chat.ApplyTranscript(desktop.Transcript);
                // A newly paired phone opens on its chat list; reading the chat steps the list aside, and
                // the transcript realizes once the list has finished sliding away.
                shell.IsDrawerOpen = false;
                Pump(window);
                for (var waited = 0; waited < 2000 && shell.IsDrawerMoving; waited += 20)
                {
                    await Task.Delay(20);
                    Pump(window);
                }
                Pump(window);
                var file = view.GetVisualDescendants().OfType<StrataFileAttachment>()
                    .Single(card => card.Classes.Contains("transcript-file"));
                var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var opening = false;
                shell.PropertyChanged += (_, e) =>
                {
                    if (opening && e.PropertyName == nameof(shell.CanApplyWebAppUpdate)
                        && shell.CanApplyWebAppUpdate)
                        finished.TrySetResult();
                };
                try
                {
                    Assert.True(shell.CanApplyWebAppUpdate);
                    file.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(
                        StrataFileAttachment.OpenRequestedEvent));
                    await desktop.FileBodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    opening = true;
                    shell.WebAppUpdateState = WebAppUpdateState.Ready;
                    Assert.False(shell.CanApplyWebAppUpdate);
                    Assert.False(shell.CanAutomaticallyRecoverGatewaySignIn);
                    Assert.True(shell.ReloadWebAppCommand.CanExecute(null));
                    Assert.False(opener.Started.Task.IsCompleted);

                    desktop.ReleaseFileBody.TrySetResult();
                    await opener.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    Assert.Equal(desktop.ProducedFileBytes, await File.ReadAllBytesAsync(opener.Path!));
                    Assert.False(shell.CanApplyWebAppUpdate);
                    Assert.False(shell.CanAutomaticallyRecoverGatewaySignIn);

                    if (openerFails)
                        opener.Result.TrySetException(new IOException("The fixture opener failed."));
                    else
                        opener.Result.TrySetResult(true);
                    await finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    Assert.True(shell.CanApplyWebAppUpdate);
                    Assert.True(shell.CanAutomaticallyRecoverGatewaySignIn);
                    Assert.False(File.Exists(opener.Path));
                    Assert.Equal(openerFails ? "That file could not be opened." : null, shell.Chat.ErrorText);
                }
                finally
                {
                    desktop.ReleaseFileBody.TrySetResult();
                    opener.Result.TrySetResult(true);
                }
            });
        }
        finally
        {
            desktop.ReleaseFileBody.TrySetResult();
            opener.Result.TrySetResult(true);
            MobilePlatformServices.HostEnvironment = previousHost;
            MobilePlatformServices.ProducedFileOpener = previousOpener;
        }
    }

    [Fact]
    public Task PairingErrorsStayBesideTheCodeAndAboveTheKeyboard() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            shell.IsPaired = false;
            shell.Connect.Step = ConnectStep.EnterCode;
            shell.Connect.TargetHostName = "Preview PC";
            view.ApplyPlatformInsets(new Thickness(0, 24, 0, 20), keyboardInset: 300);
            shell.Connect.StatusText = "Pairing...";
            shell.Connect.ErrorText = "That code isn't correct. Please try again.";
            Pump(window);
            await Task.Delay(320);
            Pump(window);

            var error = Required<Border>(view, "PairingError");
            var bottom = error.TranslatePoint(new Point(0, error.Bounds.Height), window)!.Value.Y;
            var viewport = error.GetVisualAncestors().OfType<ScrollViewer>().First();
            var viewportBottom = viewport.TranslatePoint(new Point(0, viewport.Bounds.Height), window)!.Value.Y;
            Assert.True(error.IsEffectivelyVisible);
            Assert.True(viewportBottom <= window.ClientSize.Height - shell.SafeAreaBottom.Bottom + 1);
            Assert.True(bottom <= viewportBottom + 1,
                $"Pairing error bottom {bottom}, viewport bottom {viewportBottom}; "
                + $"client {window.ClientSize}, view {view.Bounds}, inset {shell.SafeAreaBottom}.");
            var retry = Required<Button>(view, "PairButton");
            Assert.True(retry.TranslatePoint(new Point(0, retry.Bounds.Height), window)!.Value.Y <= viewportBottom + 1);
            Assert.Null(shell.Connect.StatusText);
            Assert.False(shell.Connect.IsBusy);
        });

    [Fact]
    public Task FreshInputPaneGeometryAfterActivationRestoresKeyboardLayout() =>
        RunUiAsync(360, (shell, view, window) =>
        {
            view.ApplyPlatformInsets(new Thickness(0, 24, 0, 20));
            view.ApplyInputPaneGeometry(InputPaneState.Open, new Rect(0, 500, 360, 280));
            Assert.True(shell.IsKeyboardOpen);
            view.NotifyApplicationDeactivated();
            Assert.False(shell.IsKeyboardOpen);
            Assert.Equal(20, shell.SafeAreaBottom.Bottom);

            view.ApplyInputPaneGeometry(InputPaneState.Open, new Rect(0, 500, 360, 280));
            Assert.False(shell.IsKeyboardOpen);
            view.NotifyApplicationActivated();
            Assert.False(shell.IsKeyboardOpen);
            view.ApplyInputPaneGeometry(InputPaneState.Open, new Rect(0, 500, 360, 280));
            Assert.True(shell.IsKeyboardOpen);
            Assert.False(shell.IsWelcomeVisible);
            Assert.Equal(window.ClientSize.Height - 500, shell.SafeAreaBottom.Bottom);

            view.ApplyInputPaneGeometry(InputPaneState.Closed, default);
            Assert.False(shell.IsKeyboardOpen);
            Assert.Equal(20, shell.SafeAreaBottom.Bottom);
            return Task.CompletedTask;
        });

    [Fact]
    public Task ReapplyingAnUnchangedTranscriptDoesNotInventUnreadContent() =>
        RunUiAsync(360, (shell, view, window) =>
        {
            var transcript = new RemoteTranscript
            {
                ChatId = shell.Chat.ChatId,
                Revision = 1,
                Status = new RemoteChatStatus { ChatId = shell.Chat.ChatId },
                Turns = [new RemoteTranscriptTurn
                {
                    Id = "read-turn",
                    Items = [new RemoteTranscriptItem
                    {
                        Id = "read-answer",
                        Kind = RemoteProtocol.ItemKinds.Assistant,
                        Text = string.Join("\n\n", Enumerable.Repeat("A paragraph already read.", 40))
                    }]
                }]
            };
            shell.Chat.ApplyTranscript(transcript);
            Pump(window);
            var chatShell = Required<StrataChatShell>(view, "ChatShell");
            var scroll = Required<ScrollViewer>(view, "PART_TranscriptScroll");
            chatShell.JumpToLatest();
            Pump(window);
            scroll.Offset = default;
            Pump(window);
            Assert.False(chatShell.IsFollowingTail);
            Assert.False(chatShell.HasNewContent);

            shell.Chat.ApplyTranscript(transcript);
            Pump(window);
            Assert.False(chatShell.HasNewContent);
            Assert.Equal(0, scroll.Offset.Y);

            transcript.Revision = 0;
            shell.Chat.ApplyTranscript(transcript);
            Pump(window);
            Assert.False(chatShell.HasNewContent);

            transcript.Revision = 2;
            transcript.Turns[0].Items.Add(new RemoteTranscriptItem
            {
                Id = "new-answer", Kind = RemoteProtocol.ItemKinds.Assistant, Text = "New information."
            });
            shell.Chat.ApplyTranscript(transcript);
            Pump(window);
            Assert.True(chatShell.HasNewContent);
            Assert.Equal(0, scroll.Offset.Y);
            return Task.CompletedTask;
        });

    [Fact]
    public Task ChatSwitchDoesNotBuildMarkdownBehindTheClosingDrawer() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            shell.IsDrawerOpen = true;
            await Task.Delay(240);
            PumpFrame(window);
            var drawer = Required<StrataNavigationDrawer>(view, "NavDrawer");
            Assert.Equal(1, drawer.Progress);
            var builtUnderDrawer = 0;
            using var subscription = StrataMarkdown.MarkdownProperty.Changed.AddClassHandler<StrataMarkdown>(
                (markdown, _) =>
                {
                    if (markdown.Markdown?.StartsWith("### Heavy chat") == true
                        && drawer.Progress > 0.001)
                        builtUnderDrawer++;
                });
            shell.Chat.Reset(Guid.NewGuid(), "Next chat", isLoading: true);
            shell.Chat.ApplyTranscript(new RemoteTranscript
            {
                ChatId = shell.Chat.ChatId, Revision = 1,
                Turns = [new RemoteTranscriptTurn
                {
                    Id = "heavy-turn",
                    Items = [new RemoteTranscriptItem
                    {
                        Id = "heavy-answer", Kind = RemoteProtocol.ItemKinds.Assistant,
                        Text = "### Heavy chat\n\n" + string.Join("\n\n",
                            Enumerable.Repeat("A **paragraph** with `inline code`.", 20))
                    }]
                }]
            });
            shell.Chat.IsLoading = false;
            PumpFrame(window);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<StrataMarkdown>(),
                markdown => markdown.Markdown?.StartsWith("### Heavy chat") == true);
            var surface = Required<Border>(view, "ChatTranscriptSideInset");
            Assert.False(SlideFadeEntrance.IsCompositedRunning(surface));
            // Picking the chat in the list steps the list aside.
            shell.IsDrawerOpen = false;
            await Task.Delay(240);
            PumpFrame(window);
            Assert.Equal(0, drawer.Progress);
            Assert.Equal(0, builtUnderDrawer);
            Assert.Contains(view.GetVisualDescendants().OfType<StrataMarkdown>(),
                markdown => markdown.Markdown?.StartsWith("### Heavy chat") == true);
            Assert.True(surface.IsHitTestVisible);
            Assert.True(SlideFadeEntrance.IsCompositedRunning(surface));
        });

    [Fact]
    public Task ALoadedTranscriptStartsExactlyOneEntranceAndLiveRefreshDoesNotReplayIt() =>
        RunUiAsync(360, (shell, view, window) =>
        {
            var chatId = Guid.NewGuid();
            shell.Chat.Reset(chatId, "Next chat", isLoading: true);
            var surface = Required<Border>(view, "ChatTranscriptSideInset");
            var starts = 0;
            surface.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.OpacityProperty && surface.Opacity == 1)
                    starts++;
            };
            var transcript = new RemoteTranscript
            {
                ChatId = chatId, Title = "Next chat", Revision = 1,
                Status = new RemoteChatStatus { ChatId = chatId },
                Turns = [new RemoteTranscriptTurn
                {
                    Id = "turn",
                    Items = [new RemoteTranscriptItem { Id = "answer", Text = "Ready to read." }]
                }]
            };
            shell.Chat.ApplyTranscript(transcript);
            shell.Chat.IsLoading = false;
            PumpFrame(window);
            Assert.Equal(1, starts);
            Assert.True(surface.IsHitTestVisible);

            shell.Chat.ApplyTranscript(transcript);
            Pump(window);
            Assert.Equal(1, starts);
            return Task.CompletedTask;
        });

    [Fact]
    public Task WorkingEntranceDoesNotReplayWhenBusyAndStreamingNotificationsOverlap() =>
        RunUiAsync(360, (shell, view, window) =>
        {
            var indicator = Required<StrataTypingIndicator>(view, "ChatTyping");
            var starts = 0;
            indicator.PropertyChanged += (_, e) =>
            {
                // The running-state style also restores opacity; only the entrance writes it locally.
                if (e.Property == Visual.OpacityProperty && e.Priority == Avalonia.Data.BindingPriority.LocalValue
                    && indicator.Opacity == 1)
                    starts++;
            };
            shell.Chat.IsBusy = true;
            PumpFrame(window);
            Assert.Equal(1, starts);
            Assert.True(indicator.IsEffectivelyVisible);
            shell.Chat.IsStreaming = true;
            shell.Chat.IsBusy = false;
            Pump(window);
            Assert.Equal(1, starts);
            Assert.True(indicator.IsEffectivelyVisible);
            shell.Chat.IsStreaming = false;
            Pump(window);
            Assert.False(indicator.IsEffectivelyVisible);
            shell.Chat.IsBusy = true;
            PumpFrame(window);
            Assert.Equal(2, starts);
            return Task.CompletedTask;
        });

    [Fact]
    public Task FreshReplyAnimatesWithoutReplacingExistingTurnControls() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            var transcript = new RemoteTranscript
            {
                ChatId = shell.Chat.ChatId, Revision = 1,
                Status = new RemoteChatStatus { ChatId = shell.Chat.ChatId },
                Turns = [new RemoteTranscriptTurn
                {
                    Id = "motion-turn",
                    Items =
                    [
                        new RemoteTranscriptItem
                        {
                            Id = "user", Kind = RemoteProtocol.ItemKinds.User, Text = "A thought"
                        },
                        new RemoteTranscriptItem
                        {
                            Id = "answer", Kind = RemoteProtocol.ItemKinds.Assistant, Text = "Already visible."
                        }
                    ]
                }]
            };
            shell.Chat.ApplyTranscript(transcript);
            Pump(window);
            var turn = view.GetVisualDescendants().OfType<TranscriptTurnView>().Single();
            var items = Assert.IsType<ItemsControl>(turn.Child);
            var user = items.ContainerFromIndex(0);
            var answer = items.ContainerFromIndex(1);
            Assert.NotNull(user);
            Assert.NotNull(answer);
            Assert.Null(answer.Transitions);

            transcript.Revision = 2;
            transcript.Turns[0].Items.Add(new RemoteTranscriptItem
            {
                Id = "arrival", Kind = RemoteProtocol.ItemKinds.Assistant, Text = "A fresh reply."
            });
            shell.Chat.ApplyTranscript(transcript);
            Pump(window);
            Assert.Same(user, items.ContainerFromIndex(0));
            Assert.Same(answer, items.ContainerFromIndex(1));
            var arrival = items.ContainerFromIndex(2)!;
            Assert.True(SlideFadeEntrance.IsCompositedRunning(arrival));
            Assert.Null(arrival.Transitions);

            await Task.Delay(65);
            Pump(window);
            Assert.Equal(1, arrival.Opacity);
            Assert.True(SlideFadeEntrance.IsCompositedRunning(arrival));
            transcript.Revision = 3;
            transcript.Turns[0].Items[^1].Text += " More streamed text.";
            shell.Chat.ApplyTranscript(transcript);
            Pump(window);
            Assert.Same(arrival, items.ContainerFromIndex(2));
            Assert.True(SlideFadeEntrance.IsCompositedRunning(arrival));
            await Task.Delay(300);
            Pump(window);
            Assert.Equal(1, arrival.Opacity);
            Assert.Equal(0, arrival.RenderTransform!.Value.M32, precision: 3);
            Assert.False(SlideFadeEntrance.IsCompositedRunning(arrival));
        });

    [Fact]
    public Task PageReturnAnimatesWithoutResettingTheReadersViewport() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            shell.Chat.ApplyTranscript(new RemoteTranscript
            {
                ChatId = shell.Chat.ChatId, Revision = 1,
                Status = new RemoteChatStatus { ChatId = shell.Chat.ChatId },
                Turns = [new RemoteTranscriptTurn
                {
                    Id = "reading-turn",
                    Items = [new RemoteTranscriptItem
                    {
                        Id = "reading-answer", Kind = RemoteProtocol.ItemKinds.Assistant,
                        Text = string.Join("\n\n", Enumerable.Repeat("A paragraph to keep reading.", 40))
                    }]
                }]
            });
            Pump(window);
            var scroll = Required<ScrollViewer>(view, "PART_TranscriptScroll");
            scroll.Offset = default;
            Pump(window);
            var offset = scroll.Offset;
            var answer = view.GetVisualDescendants().OfType<StrataMarkdown>()
                .Single(markdown => markdown.Markdown?.StartsWith("A paragraph") == true);
            shell.ShowPageCommand.Execute("Settings");
            Pump(window);
            Assert.True(SlideFadeEntrance.IsCompositedRunning(Required<StackPanel>(view, "SettingsContent")));
            await Task.Delay(65);
            Pump(window);
            Assert.True(SlideFadeEntrance.IsCompositedRunning(Required<StackPanel>(view, "SettingsContent")));

            // Returning to the conversation keeps its controls and reading position.
            shell.GoBackCommand.Execute(null);
            Pump(window);
            Assert.True(shell.IsChatPage);
            Assert.False(shell.IsDrawerOpen);
            await Task.Delay(240);
            Pump(window);
            Assert.Same(answer, view.GetVisualDescendants().OfType<StrataMarkdown>()
                .Single(markdown => markdown.Markdown?.StartsWith("A paragraph") == true));
            Assert.Equal(offset, scroll.Offset);
            var surface = Required<Border>(view, "ChatTranscriptSideInset");
            Assert.True(SlideFadeEntrance.IsCompositedRunning(surface));
            await Task.Delay(300);
            Pump(window);
            Assert.Equal(offset, scroll.Offset);
            Assert.Equal(1, surface.Opacity);
        });

    [Fact]
    public Task LibraryCategoryAndEditorHaveInterruptibleMotion() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            shell.ShowPageCommand.Execute("Library");
            await Task.Delay(340);
            Pump(window);
            var library = Required<LibraryView>(view, "LibraryPage");
            var list = Required<StackPanel>(view, "LibraryListContent");
            shell.Library.SectionIndex = 1;
            Pump(window);
            Assert.True(SlideFadeEntrance.IsCompositedRunning(list));
            await Task.Delay(340);
            Tap(window, Required<Button>(library, "LibraryNewButton"));
            var editor = Required<StackPanel>(view, "LibraryEditor");
            Assert.True(shell.Library.IsEditing);
            Assert.True(SlideFadeEntrance.IsCompositedRunning(editor));
            Tap(window, Required<Button>(library, "EditCancelButton"));
            Assert.False(shell.Library.IsEditing);
            Assert.True(SlideFadeEntrance.IsCompositedRunning(list));
            Assert.Equal(1, editor.Opacity);
            await Task.Delay(340);
            Pump(window);
            Assert.Equal(1, list.Opacity);
        });

    [Fact]
    public Task TouchPressEasesTheSurfaceWithoutShrinkingItsHitTarget() =>
        RunUiAsync(360, async (_, view, window) =>
        {
            var button = Required<Button>(view, "NewChatButton");
            var surface = Required<Border>(button, "PART_Surface");
            var bounds = button.Bounds;
            Assert.True(bounds.Width >= 48 && bounds.Height >= 48);
            Assert.Equal(1, button.RenderTransform?.Value.M11 ?? 1);
            var center = button.TranslatePoint(new Point(bounds.Width / 2, bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            Pump(window);
            await Task.Delay(65);
            Pump(window);
            Assert.InRange(surface.RenderTransform!.Value.M11, 0.961, 0.999);
            Assert.Equal(bounds, button.Bounds);
            Assert.Equal(1, button.RenderTransform?.Value.M11 ?? 1);
            window.MouseUp(center, MouseButton.Left);
            await Task.Delay(240);
            Pump(window);
            Assert.Equal(1, surface.RenderTransform!.Value.M11, precision: 3);
            Assert.Equal(bounds, button.Bounds);
        });

    [Fact]
    public Task DetachedOrDisposedEntranceCannotLeaveContentHidden() =>
        RunUiAsync(360, async (_, view, window) =>
        {
            var root = Required<Panel>(view, "Root");
            var target = new Border { Width = 100, Height = 40 };
            using var entrance = new MobileEntrance(target);
            entrance.Play();
            Pump(window);
            var starts = 0;
            target.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.OpacityProperty && target.Opacity == 1)
                    starts++;
            };
            root.Children.Add(target);
            PumpFrame(window);
            Assert.Equal(1, starts);
            var bounds = target.Bounds;
            await Task.Delay(65);
            Pump(window);
            Assert.Equal(1, target.Opacity);
            Assert.True(SlideFadeEntrance.IsCompositedRunning(target));
            Assert.Equal(bounds, target.Bounds);
            root.Children.Remove(target);
            Assert.False(SlideFadeEntrance.IsCompositedRunning(target));
            Assert.Equal(1, target.Opacity);
            Assert.Equal(TransformOperations.Identity.Value, target.RenderTransform!.Value);
            entrance.Dispose();
            entrance.Play();
            entrance.Prepare();
            entrance.Reveal();
            root.Children.Add(target);
            Pump(window);
            Assert.Equal(1, target.Opacity);
            Assert.Equal(1, starts);
        });

    [Fact]
    public Task LibraryLongPressDoesNotEnterTheEditorWhenThePointerIsReleased() =>
        RunUiAsync(1100, async (shell, view, window) =>
        {
            shell.Library.Apply(new RemoteLibrary
            {
                Projects = [new RemoteProject { Id = Guid.NewGuid(), Name = "Project actions" }]
            });
            shell.ShowPageCommand.Execute("Library");
            await Task.Delay(280);
            Pump(window);

            var row = Required<Button>(view, "LibraryEntryButton");
            Assert.True(InputElement.GetIsHoldingEnabled(row));
            var center = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            var held = (HoldingRoutedEventArgs)Activator.CreateInstance(
                typeof(HoldingRoutedEventArgs),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                binder: null, [HoldingState.Started, new Point(20, 20), PointerType.Touch, null], culture: null)!;
            row.RaiseEvent(held);
            Assert.True(shell.Library.IsRowActionsOpen);
            window.MouseUp(center, MouseButton.Left);
            await Task.Delay(280);
            Pump(window);

            Assert.True(shell.Library.IsRowActionsOpen);
            Assert.False(shell.Library.IsEditing);
        });

    [Fact]
    public Task DrawerLongPressOpensActionsWithoutOpeningTheChatAndRenameFocusesTheName() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            var chat = new RemoteChat
            {
                Id = shell.Chat.ChatId,
                Title = "A long conversation title that must not draw over its actions"
            };
            shell.ChatList.Apply([new RemoteChatGroup { Label = "Today", Chats = [chat] }]);
            shell.IsDrawerOpen = true;
            await Task.Delay(320);
            Pump(window);

            var drawer = Required<MobileDrawerView>(view, "DrawerContent");
            Assert.DoesNotContain(drawer.GetVisualDescendants().OfType<Button>(),
                button => AutomationProperties.GetName(button)?.StartsWith("Actions for ") == true);
            var row = drawer.GetVisualDescendants().OfType<Button>()
                .Single(button => button.DataContext is ChatListItemViewModel);
            Assert.True(row.Bounds.Width >= drawer.Bounds.Width - 26);
            Assert.True(InputElement.GetIsHoldingEnabled(row));
            var center = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            var held = (HoldingRoutedEventArgs)Activator.CreateInstance(
                typeof(HoldingRoutedEventArgs),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                binder: null, [HoldingState.Started, new Point(20, 20), PointerType.Touch, null], culture: null)!;
            row.RaiseEvent(held);
            window.MouseUp(center, MouseButton.Left);
            await Task.Delay(320);
            Pump(window);
            Assert.True(shell.IsChatActionsOpen);

            Tap(window, Required<Button>(view, "SheetRenameButton"));
            Pump(window);
            var name = Required<TextBox>(view, "RenameChatBox");
            Assert.True(shell.IsRenamingChat);
            Assert.False(shell.IsDrawerOpen);
            Assert.True(name.IsFocused);
            Assert.Equal(chat.Title, name.SelectedText);
            Assert.NotNull(NativeTextInputOverlay.GetVisiblePlacement(name, window));
        });

    [Fact]
    public Task AndroidBackStepsOutOfRenameAndDeleteBeforeClosingChatActions() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            shell.OpenChatActionsCommand.Execute(new ChatListItemViewModel(new RemoteChat
            {
                Id = shell.Chat.ChatId,
                Title = "Designing a calmer mobile experience"
            }));
            PumpFrame(window);
            await Task.Delay(StrataBottomSheet.OpenDuration + TimeSpan.FromMilliseconds(60));
            PumpFrame(window);

            Tap(window, Required<Button>(view, "SheetRenameButton"));
            Pump(window);
            Assert.True(Required<TextBox>(view, "RenameChatBox").IsFocused);
            PressAndroidBack(window, shell);
            Assert.True(shell.IsChatActionsOpen);
            Assert.False(shell.IsRenamingChat);

            Tap(window, Required<Button>(view, "SheetDeleteButton"));
            Assert.True(shell.IsConfirmingChatDelete);
            PressAndroidBack(window, shell);
            Assert.True(shell.IsChatActionsOpen);
            Assert.False(shell.IsConfirmingChatDelete);

            PressAndroidBack(window, shell);
            Assert.False(shell.IsChatActionsOpen);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AndroidBackClosesTheViewerBeforeTheChatAndNativeEditorStaysBehindIt(bool native) =>
        RunUiAsync(360, (shell, view, window) =>
        {
            shell.IsDrawerOpen = false;
            var diagram = new StrataMermaid
            {
                Source = "flowchart LR\nA[Phone] --> B[Desktop]",
                Height = 250,
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = true
            };
            Required<Panel>(view, "Root").Children.Add(diagram);
            PumpFrame(window);
            var composer = Required<TextBox>(view, "PART_Input");
            var nativeEditor = view.GetVisualDescendants().OfType<NativeComposerEditorHost>().FirstOrDefault();
            if (native)
                Assert.True(nativeEditor?.IsVisible);
            else
                Assert.NotNull(NativeTextInputOverlay.GetVisiblePlacement(composer, window));
            var expand = Required<Button>(diagram, "PART_Expand");
            Assert.True(expand.Bounds.Width >= 48 && expand.Bounds.Height >= 48);
            Tap(window, expand);
            PumpFrame(window);
            Assert.True(StrataFullScreenOverlay.IsPresented(view));
            Assert.Null(NativeTextInputOverlay.GetVisiblePlacement(composer, window));
            if (native)
                Assert.False(nativeEditor!.IsVisible);

            // Android raises Back on the top level, not an Escape key on the focused viewer.
            diagram.Focus();
            var back = new RoutedEventArgs(TopLevel.BackRequestedEvent);
            window.RaiseEvent(back);
            PumpFrame(window);
            Assert.True(back.Handled);
            Assert.False(StrataFullScreenOverlay.IsPresented(view));
            Assert.Equal(MobilePage.Chat, shell.Page);
            Assert.False(shell.IsDrawerOpen);
            if (native)
                Assert.True(nativeEditor!.IsVisible);
            else
                Assert.NotNull(NativeTextInputOverlay.GetVisiblePlacement(composer, window));

            PressAndroidBack(window, shell);
            Assert.True(shell.IsDrawerOpen);
            back = new RoutedEventArgs(TopLevel.BackRequestedEvent);
            window.RaiseEvent(back);
            Assert.False(back.Handled, "Android can leave the app only after returning home.");
            return Task.CompletedTask;
        }, nativeFactory: native ? new FocusEditorFactory() : null);

    private static async Task PairAsync(MobileShellViewModel shell, FakeLumiDesktop desktop)
    {
        shell.Connect.ManualAddress = desktop.BaseUrl;
        await shell.Connect.ConnectManuallyCommand.ExecuteAsync(null);
        shell.Connect.PairingCode = "123456";
        await shell.Connect.SubmitCodeCommand.ExecuteAsync(null);
        Assert.True(shell.IsPaired);
    }

    private static async Task RunUiAsync(
        double width,
        Func<MobileShellViewModel, MobileShellView, Window, Task> test,
        INativeComposerEditorFactory? nativeFactory = null,
        Action<MobileShellViewModel>? setup = null)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            var previousFactory = MobilePlatformServices.NativeComposerEditorFactory;
            if (nativeFactory is not null)
                MobilePlatformServices.NativeComposerEditorFactory = nativeFactory;
            await using var shell = new MobileShellViewModel(store: session.NewStore(), post: action =>
            {
                if (Dispatcher.UIThread.CheckAccess())
                    action();
                else
                    Dispatcher.UIThread.Post(action);
            });
            shell.IsPaired = true;
            shell.IsConnected = true;
            shell.IsHostReady = true;
            shell.HostName = "Preview PC";
            shell.Chat.Reset(Guid.NewGuid(), "A long conversation title that deserves room to breathe");
            setup?.Invoke(shell);
            var view = new MobileShellView { DataContext = shell };
            var window = new Window { Width = width, Height = 800, Content = view };
            try
            {
                window.Show();
                Pump(window);
                await test(shell, view, window);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
                MobilePlatformServices.NativeComposerEditorFactory = previousFactory;
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    private static T Required<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void Pump(Window window)
    {
        window.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();
    }

    private static void PumpFrame(Window window)
    {
        Pump(window);
        using var frame = window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Tap(Window window, Button button, Point? position = null)
    {
        var center = button.TranslatePoint(
            position ?? new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);
        Assert.NotNull(center);
        var hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(center.Value));
        Assert.True(ReferenceEquals(hit, button) || hit.GetVisualAncestors().Contains(button),
            $"{button.Name ?? "Action"} at {center} is covered by {hit.GetType().Name} "
            + $"{(hit as Control)?.Name}; button bounds: {button.Bounds}; "
            + $"hit ancestry: {string.Join(" > ", hit.GetVisualAncestors().OfType<Control>().Select(control => control.Name ?? control.GetType().Name))}.");
        window.MouseDown(center.Value, MouseButton.Left);
        window.MouseUp(center.Value, MouseButton.Left);
        Pump(window);
    }

    // The Android navigation manager raises this event directly on the top level.
    private static void PressAndroidBack(Window window, MobileShellViewModel shell)
    {
        window.RaiseEvent(new RoutedEventArgs(TopLevel.BackRequestedEvent));
        PumpFrame(window);
    }

    private sealed class WidthMeasureProbe : Control
    {
        public int Measures { get; private set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            Measures++;
            return new Size(1, 0);
        }
    }

    private sealed class MemoryStore : IMobileSettingsStore
    {
        private MobileConnectionSettings _settings = new();
        public MobileConnectionSettings Load() => _settings;
        public void Save(MobileConnectionSettings settings) => _settings = settings;
    }

    private sealed class ReloadableWebHost(
        Action? reload, Action? apply = null, Action? check = null, string? baseUrl = "https://lumi.test") : IMobileHostEnvironment
    {
        public bool HasFixedEndpoint => true;
        public string? FixedBaseUrl => baseUrl;
        public string FixedEndpointName => "Test PC";
        public Action? ReloadWebApp => reload;
        public Action? ApplyWebAppUpdate => apply;
        public Action? CheckWebAppUpdate => check;
    }

    private sealed class DeferredProducedFileOpener : IProducedFileOpener
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Path { get; private set; }

        public Task<bool> TryOpenAsync(
            string downloadedPath, string displayName, CancellationToken cancellationToken)
        {
            Path = downloadedPath;
            Started.TrySetResult();
            return Result.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FocusEditorFactory : INativeComposerEditorFactory
    {
        private int _caret;
        public bool IsAvailable => true;
        public IPlatformHandle Create(NativeComposerEditorHost host, IPlatformHandle parent) =>
            new PlatformHandle(IntPtr.Zero, "test");
        public void Destroy(NativeComposerEditorHost host, IPlatformHandle control) { }
        public void ApplyText(NativeComposerEditorHost host, string text) { }
        public void ApplyPlaceholder(NativeComposerEditorHost host, string placeholder) { }
        public int GetCaretIndex(NativeComposerEditorHost host) => _caret;
        public void Blur(NativeComposerEditorHost host) => host.SetInputFocusFromNative(false);
        public void FocusAt(NativeComposerEditorHost host, int caretIndex)
        {
            _caret = caretIndex;
            host.SetInputFocusFromNative(true);
        }
        public void FocusAtEnd(NativeComposerEditorHost host) => FocusAt(host, host.Text.Length);
    }

    private sealed class PageSink : IRemoteCommandSink, IRemoteChatPageSink
    {
        public RemoteChatPage? Page { get; set; }
        public TaskCompletionSource<RemoteChatPage?>? DeferredPage { get; set; }

        public Task<RemoteChatPage?> GetChatPageAsync(
            int offset, int limit, string? query, Guid? projectId, CancellationToken cancellationToken) =>
            DeferredPage?.Task.WaitAsync(cancellationToken) ?? Task.FromResult(Page);

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command) =>
            Task.FromResult(new RemoteCommandResult { Ok = true });

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse { Ok = true });
    }
}
