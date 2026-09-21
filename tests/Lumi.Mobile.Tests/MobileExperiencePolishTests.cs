using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Platform;
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

[Collection("Headless mobile UI")]
public sealed class MobileExperiencePolishTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public Task NavigationHasMinimalBrandingTitleOnlyRowsAndReachableFloatingActions() =>
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
            Assert.Equal("Lumi", Required<TextBlock>(drawer, "DrawerBrandTitle").Text);
            Assert.DoesNotContain(drawer.GetVisualDescendants().OfType<Image>(), image => image.Name == "DrawerBrandImage");
            Assert.DoesNotContain(drawer.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Your workspace");
            Assert.DoesNotContain(drawer.GetVisualDescendants().OfType<Button>(),
                button => button.Name == "DrawerNewChatButton");
            Assert.Same(shell.ChatList.NewChatCommand, Required<Button>(view, "NewChatButton").Command);
            foreach (var name in new[] { "DrawerSearchButton", "DrawerCloseButton" })
            {
                var action = Required<Button>(drawer, name);
                Assert.True(action.Bounds.Width >= 48 && action.Bounds.Height >= 48);
                Assert.True(action.TranslatePoint(new Point(action.Bounds.Width, 0), drawer)!.Value.X <= drawer.Bounds.Width);
            }
            var row = drawer.GetVisualDescendants().OfType<Button>()
                .Single(button => button.DataContext is ChatListItemViewModel);
            Assert.Contains("selected", row.Classes);
            Assert.InRange(row.Bounds.Height, 48, 60);
            Assert.DoesNotContain(row.GetVisualDescendants().OfType<Border>(),
                border => border.Classes.Contains("navigation-icon-tile"));
            var title = row.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Classes.Contains("conversation-title"));
            Assert.True(title.Bounds.Width >= row.Bounds.Width - 26);
            Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible);
            Assert.Equal("A useful conversation", title.Text);
            Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, title.TextTrimming);
            Assert.Equal("All projects", Required<TextBlock>(drawer, "DrawerProjectLabel").Text);
            Assert.DoesNotContain(drawer.GetVisualDescendants().OfType<ItemsControl>(),
                control => control.Name == "DrawerProjects");
            Assert.Single(Required<Border>(drawer, "DrawerAccountSideInset")
                .GetVisualDescendants().OfType<TextBlock>());
            Tap(window, Required<Button>(drawer, "DrawerCloseButton"));
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
            await Task.Delay(240);
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
            Assert.Equal("All projects", label.Text);
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
            if (!shell.CanDockDrawer)
            {
                Tap(window, Required<Button>(view, "DrawerCloseButton"));
                await Task.Delay(240);
            }
            Tap(window, Required<Button>(view, "NewChatButton"));
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
            Assert.Equal("All projects", label.Text);
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
            Assert.Equal("All projects", shell.ProjectScopeLabel);
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
            Assert.Equal("Hide navigation", AutomationProperties.GetName(menu));
            Assert.Single(view.GetVisualDescendants().OfType<MobileDrawerView>());
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
            Assert.Equal(320, shell.NavigationLeadingWidth);
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
                await Move(586, 1400);
                Assert.Equal(0.8, drawer.Progress, precision: 3);
                window.Width = 1000;
                Pump(window);
                Assert.Equal(0.8, drawer.Progress, precision: 3);
                await AssertDockedCanvasGeometry(view, drawer, "resize while captured", verifyHits: true);
                await Move(618, 1800);
                Assert.Equal(0.9, drawer.Progress, precision: 3);
                for (var i = 1; i <= 8; i++)
                    await Move(618 - i * 32, (ulong)(1800 + i * 200));
                Assert.Equal(0.1, drawer.Progress, precision: 3);
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
            Assert.Equal(12, sheets.Length);
            foreach (var sheet in sheets)
            {
                shell.Page = sheet.GetVisualAncestors().OfType<LibraryView>().Any() ? MobilePage.Library
                    : sheet.GetVisualAncestors().OfType<MobileSettingsView>().Any() ? MobilePage.Settings
                    : MobilePage.Chat;
                sheet.SetCurrentValue(StrataBottomSheet.IsOpenProperty, true);
                Pump(window);
                await Task.Delay(260);
                Pump(window);
                Assert.Equal(sheet.Name == "ProjectPickerSheet"
                    ? shell.ProjectPickerSheetMargin : shell.SafeAreaSheetMargin, sheet.SheetMargin);
                var surface = Required<Border>(sheet, "PART_Sheet");
                var scrim = Required<Border>(sheet, "PART_Scrim");
                var bounds = new Rect(surface.Bounds.Size).TransformToAABB(surface.TransformToVisual(sheet)!.Value);
                Assert.True(bounds.Left >= sheet.SheetMargin.Left - 0.1, sheet.Name);
                Assert.True(bounds.Right <= sheet.Bounds.Width - sheet.SheetMargin.Right + 0.1, sheet.Name);
                Assert.True(bounds.Top >= sheet.SheetMargin.Top - 0.1, sheet.Name);
                Assert.Equal(sheet.Bounds.Height - sheet.SheetMargin.Bottom, bounds.Bottom, precision: 1);
                Assert.InRange(surface.Bounds.Width, 1, 640);
                Assert.Equal(new CornerRadius(24), surface.CornerRadius);
                Assert.Equal(default, surface.BorderThickness);
                Assert.True(Required<Border>(sheet, "PART_Handle").Bounds.Height >= 48);
                Assert.Equal(sheet.Bounds.Size, scrim.Bounds.Size);

                sheet.SetCurrentValue(StrataBottomSheet.IsOpenProperty, false);
                await Task.Delay(180);
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
    public Task LoadedChatBecomesInteractiveWithoutWaitingForBackgroundDispatcherWork() =>
        RunUiAsync(360, (shell, view, _) =>
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
            Assert.True(body.IsHitTestVisible);
            Assert.Equal(1, body.GetBaseValue(Visual.OpacityProperty));
            Assert.False(Required<Border>(view, "ChatLoadingOverlay").IsVisible);
            return Task.CompletedTask;
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
            await Task.Delay(240);
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
            await Task.Delay(200);
            Assert.False(shell.IsModalSheetPresented);
            Assert.True(editor.IsVisible);

            shell.OpenChatActionsCommand.Execute(new ChatListItemViewModel(new RemoteChat
            {
                Id = shell.Chat.ChatId, Title = "Chat actions"
            }));
            await Task.Delay(260);
            shell.CloseChatActionsCommand.Execute(null);
            Assert.False(editor.IsVisible);
            await Task.Delay(200);
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
    public Task ChatControlsShowsTheLiveModelWithoutRepeatingTheConversationTitle() =>
        RunUiAsync(360, (shell, view, window) =>
        {
            var controls = Required<Button>(view, "HeaderDetailsButton");
            var model = Required<TextBlock>(view, "HeaderModelText");
            Assert.Single(controls.GetVisualDescendants().OfType<TextBlock>());
            Assert.Equal("HeaderControlsIcon", Assert.Single(controls.GetVisualDescendants().OfType<PathIcon>()).Name);
            Assert.Equal(shell.Chat.ModelDisplayName, model.Text);
            shell.Chat.ApplyStatus(new RemoteChatStatus
            {
                ChatId = shell.Chat.ChatId, Model = "gpt-4.1",
                ProjectId = Guid.NewGuid(), ProjectName = "Lumi"
            });
            Pump(window);
            Assert.Equal(shell.Chat.ModelDisplayName, model.Text);
            Assert.Equal($"Chat controls: {shell.Chat.ModelDisplayName}", AutomationProperties.GetName(controls));
            Assert.DoesNotContain(controls.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == shell.Chat.Title || text.Text == shell.Chat.ProjectName);
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
                Required<TextBlock>(view, "DrawerBrandTitle"),
                Required<Button>(view, "DrawerSearchButton"),
                Required<Button>(view, "MenuButton"),
                Required<Button>(view, "HeaderDetailsButton"),
                Required<Button>(view, "NewChatButton")
            };
            if (!shell.CanDockDrawer)
                controls.Add(Required<Button>(view, "DrawerCloseButton"));
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

            Assert.True(input.Focus());
            Pump(window);
            Assert.False(composer.IsCompact);
            await Task.Delay(65);
            Pump(window);
            var intermediateHeight = composer.Bounds.Height;
            await Task.Delay(300);
            Pump(window);
            var expandedHeight = composer.Bounds.Height;
            Assert.True(intermediateHeight > compactHeight + 1);
            Assert.True(intermediateHeight < expandedHeight - 1);
            Assert.True(expandedHeight >= compactHeight + 40);
            Assert.True(actions.IsEffectivelyEnabled);
            var editorBottom = input.TranslatePoint(new Point(0, input.Bounds.Height), composer)!.Value.Y;
            Assert.InRange(toolbar.TranslatePoint(default, composer)!.Value.Y - editorBottom, 0, 6);
            Assert.Same(input, Required<TextBox>(composer, "PART_Input"));
            Assert.Same(transitions, layout.Transitions);

            var settings = Required<Button>(view, "ComposerRunSettingsButton");
            settings.Focus();
            Pump(window);
            Assert.False(composer.IsCompact);
            Required<Button>(view, "MenuButton").Focus();
            Pump(window);
            await Task.Delay(340);
            Pump(window);
            Assert.True(composer.IsCompact);
            Assert.Equal(compactHeight, composer.Bounds.Height, precision: 1);
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
            Assert.Equal("Lumi", composer.ProjectName);
            Assert.True(Required<Border>(composer, "PART_ProjectChip").IsEffectivelyVisible);
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
            await Task.Delay(200);
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

    [Fact]
    public Task CompactContextChipsKeepTheirSurfaceAndRemovalTargetConsistent() =>
        RunUiAsync(360, async (shell, view, window) =>
        {
            var composer = Required<StrataChatComposer>(view, "Composer");
            var skill = new StrataComposerChip("A detailed research skill with a long descriptive name");
            composer.ProjectName = "Lumi";
            composer.AgentName = "A custom assistant with a long descriptive name";
            composer.AgentGlyph = "\u2726";
            composer.SkillItems = new[] { skill };
            composer.SkillRemovedCommand = null;
            await Task.Delay(280);
            Pump(window);
            var project = Required<Border>(composer, "PART_ProjectChip");
            var agent = Required<Border>(composer, "PART_AgentChip");
            var skillChip = composer.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Classes.Contains("composer-skill-chip"));
            var projectSurface = Required<Border>(composer, "PART_ProjectChipSurface");
            var agentSurface = Required<Border>(composer, "PART_AgentChipSurface");
            var skillSurface = skillChip.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Classes.Contains("composer-chip-surface"));
            var remove = skillChip.GetVisualDescendants().OfType<Button>().Single();

            foreach (var theme in new[] { Avalonia.Styling.ThemeVariant.Light, Avalonia.Styling.ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme;
                await Task.Delay(200);
                Pump(window);
                foreach (var chip in new[] { project, agent, skillChip })
                {
                    Assert.Equal(48, chip.Bounds.Height, precision: 1);
                    Assert.True(chip.Bounds.Width <= 268);
                    Assert.Equal(0, chip.BorderThickness.Left);
                    Assert.Equal(0, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(chip.Background).Color.A);
                }
                foreach (var surface in new[] { projectSurface, agentSurface, skillSurface })
                {
                    Assert.True(surface.IsEffectivelyVisible);
                    Assert.Equal(32, surface.Bounds.Height, precision: 1);
                    Assert.Equal(projectSurface.CornerRadius, surface.CornerRadius);
                    Assert.Equal(
                        Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(projectSurface.Background).Color,
                        Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(surface.Background).Color);
                }
            }
            Assert.True(remove.Bounds.Width >= 48 && remove.Bounds.Height >= 48);
            var contextRow = Required<WrapPanel>(composer, "PART_ChipsRow");
            var contextScroll = Required<ScrollViewer>(composer, "PART_ContextScroll");
            Assert.Equal(48, contextRow.Bounds.Height, precision: 1);
            Assert.True(contextScroll.Extent.Width > contextScroll.Viewport.Width);
            remove.BringIntoView();
            await Task.Delay(100);
            Pump(window);
            Assert.Equal($"Remove {skill.Name}", AutomationProperties.GetName(remove));
            object? removed = null;
            composer.SkillRemoved += (_, args) => removed = args.Item;
            Tap(window, remove);
            Assert.Same(skill, removed);
        });

    [Fact]
    public Task ProjectContextUsesARestrainedSurfaceWithoutSacrificingTheRemovalTarget() =>
        RunUiAsync(320, (shell, view, window) =>
        {
            var composer = Required<StrataChatComposer>(view, "Composer");
            composer.ProjectName = "A very long project name that should not make the composer wider";
            Pump(window);
            var chip = Required<Border>(composer, "PART_ProjectChip");
            var surface = Required<Border>(composer, "PART_ProjectChipSurface");
            var label = Required<TextBlock>(composer, "PART_ProjectName");
            var remove = Required<Button>(composer, "PART_ProjectRemoveButton");
            Assert.True(chip.IsEffectivelyVisible);
            Assert.Equal(32, surface.Bounds.Height);
            Assert.True(chip.Bounds.Width <= 268);
            Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, label.TextTrimming);
            Assert.True(remove.Bounds.Width >= 48 && remove.Bounds.Height >= 48);
            Assert.True(Required<Border>(composer, "PART_Root").BoxShadow.Count > 0);
            Assert.NotNull(Required<PathIcon>(composer, "PART_ProjectIcon"));
            return Task.CompletedTask;
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
            Assert.InRange(details.Bounds.Width, 48, Math.Min(260, width - 140));
            Assert.True(details.Bounds.Height >= 48);
            Assert.Equal($"Chat controls: {shell.Chat.ModelDisplayName}", AutomationProperties.GetName(details));
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
    public Task CurrentInputPaneGeometryRestoresKeyboardLayoutAfterDeactivation() =>
        RunUiAsync(360, (shell, view, window) =>
        {
            view.ApplyPlatformInsets(new Thickness(0, 24, 0, 20));
            view.ApplyInputPaneGeometry(InputPaneState.Open, new Rect(0, 500, 360, 280));
            Assert.True(shell.IsKeyboardOpen);
            view.NotifyApplicationDeactivated();
            Assert.False(shell.IsKeyboardOpen);
            Assert.Equal(20, shell.SafeAreaBottom.Bottom);

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
    public Task ALoadedTranscriptStartsExactlyOneEntranceAndLiveRefreshDoesNotReplayIt() =>
        RunUiAsync(360, (shell, view, window) =>
        {
            var chatId = Guid.NewGuid();
            shell.Chat.Reset(chatId, "Next chat", isLoading: true);
            var surface = Required<Border>(view, "ChatTranscriptSideInset");
            var starts = 0;
            surface.PropertyChanged += (_, e) =>
            {
                if (e.Property == Animatable.TransitionsProperty && surface.Transitions is not null)
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
            Pump(window);
            Assert.Equal(1, starts);
            Assert.True(surface.IsHitTestVisible);

            shell.Chat.ApplyTranscript(transcript);
            Pump(window);
            Assert.Equal(1, starts);
            return Task.CompletedTask;
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

    private static void Tap(Window window, Button button, Point? position = null)
    {
        var center = button.TranslatePoint(
            position ?? new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);
        Assert.NotNull(center);
        var hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(center.Value));
        Assert.True(ReferenceEquals(hit, button) || hit.GetVisualAncestors().Contains(button),
            $"{button.Name ?? "Action"} at {center} is covered by {hit.GetType().Name} "
            + $"{(hit as Control)?.Name}; button bounds: {button.Bounds}.");
        window.MouseDown(center.Value, MouseButton.Left);
        window.MouseUp(center.Value, MouseButton.Left);
        Pump(window);
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
