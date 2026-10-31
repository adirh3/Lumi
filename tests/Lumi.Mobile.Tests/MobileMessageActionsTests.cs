using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.Behaviors;
using Lumi.Mobile.Localization;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class MobileMessageActionsTests
{
    [Fact]
    public async Task EditSendsExplicitIdsAndPreservesTheConversationAndComposerUntilAccepted()
    {
        var sink = new EditSink();
        var chat = CreateChat(sink);
        var user = Assert.IsType<UserTurnItemViewModel>(chat.Turns[0].Items[0]);
        chat.PromptText = "Unsent newer draft";
        var attachment = new PendingAttachment("draft.txt", @"C:\draft.txt");
        chat.Attachments.Add(attachment);
        chat.OpenMessageActionsCommand.Execute(user);
        chat.EditSelectedMessageCommand.Execute(null);
        chat.EditedMessageText = "Updated message";

        var operation = chat.SaveMessageEditCommand.ExecuteAsync(null);
        var command = Assert.Single(sink.Commands);
        Assert.Equal(RemoteProtocol.Actions.EditMessage, command.Action);
        Assert.Equal(chat.ChatId, command.GetGuid("chatId"));
        Assert.Equal(user.Id, command.Get("messageId"));
        Assert.Equal("Updated message", command.Get("message"));
        Assert.False(string.IsNullOrWhiteSpace(command.RequestId));
        Assert.True(chat.IsSavingMessageEdit);
        Assert.Equal("Original user message", user.Text);
        Assert.Equal("Unsent newer draft", chat.PromptText);
        Assert.Same(attachment, Assert.Single(chat.Attachments));
        Guid? acceptedChat = null;
        chat.MessageEditAccepted += id => acceptedChat = id;
        sink.Completion.SetResult(new RemoteCommandResult { Ok = true, ChatId = chat.ChatId });
        await operation;

        Assert.Equal(chat.ChatId, acceptedChat);
        Assert.False(chat.HasOpenSheet);
        Assert.Equal("Unsent newer draft", chat.PromptText);
        Assert.Same(attachment, Assert.Single(chat.Attachments));
    }

    [Fact]
    public async Task FailedEditKeepsItsTextAndAnAmbiguousRetryReusesTheRequestId()
    {
        var sink = new EditSink();
        var chat = CreateChat(sink);
        chat.OpenMessageActionsCommand.Execute(chat.Turns[0].Items[0]);
        chat.EditSelectedMessageCommand.Execute(null);
        chat.EditedMessageText = "Keep this edit";
        var first = chat.SaveMessageEditCommand.ExecuteAsync(null);
        sink.Completion.SetResult(new RemoteCommandResult { Error = "Connection interrupted", IsOutcomeUnknown = true });
        await first;

        Assert.True(chat.IsEditingMessage);
        Assert.Equal("Keep this edit", chat.EditedMessageText);
        Assert.Equal("Connection interrupted", chat.MessageActionError);
        var requestId = sink.Commands[0].RequestId;
        sink.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = chat.SaveMessageEditCommand.ExecuteAsync(null);
        Assert.Equal(requestId, sink.Commands[1].RequestId);
        sink.Completion.SetResult(new RemoteCommandResult { Ok = true });
        await retry;
        Assert.False(chat.IsMessageActionsOpen);
    }

    [Fact]
    public async Task ChangingComputerOrChatCannotApplyAnOldEditResultToTheNewSurface()
    {
        var sink = new EditSink();
        var chat = CreateChat(sink);
        chat.OpenMessageActionsCommand.Execute(chat.Turns[0].Items[0]);
        chat.EditSelectedMessageCommand.Execute(null);
        chat.EditedMessageText = "Old edit";
        var operation = chat.SaveMessageEditCommand.ExecuteAsync(null);
        chat.ResetHostState();
        var newChatId = Guid.NewGuid();
        chat.Reset(newChatId, "Different computer");
        chat.PromptText = "New draft";
        sink.Completion.SetResult(new RemoteCommandResult { Error = "Old error" });
        await operation;

        Assert.Equal(newChatId, chat.ChatId);
        Assert.Equal("New draft", chat.PromptText);
        Assert.Null(chat.MessageActionError);
        Assert.False(chat.HasOpenSheet);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void UnsupportedOrTruncatedMessagesKeepCopyAndSelectionButCannotBeEdited(bool supported, bool truncated)
    {
        var chat = CreateChat(new EditSink { SupportsMessageEditing = supported },
            truncated ? "Partial text" + RemoteProtocol.MobileTruncationMarker : "Original user message");
        var original = Assert.IsType<UserTurnItemViewModel>(chat.Turns[0].Items[0]);
        chat.OpenMessageActionsCommand.Execute(original);
        Assert.True(chat.HasSelectedMessageText);
        // Copy and Select text get the phone's localized notice, never the raw wire marker.
        Assert.Equal(ChatSessionStrings.LocalizeMobileTruncation(original.Text), chat.SelectedMessageText);
        if (truncated)
        {
            var language = ChatSessionStrings.Language;
            ChatSessionStrings.Load("he");
            try
            {
                Assert.DoesNotContain(RemoteProtocol.MobileTruncationMarker, chat.SelectedMessageText);
                Assert.EndsWith(ChatSessionStrings.Get("MobileTruncationNotice"), chat.SelectedMessageText);
            }
            finally
            {
                ChatSessionStrings.Load(language);
            }
        }
        Assert.False(chat.CanEditSelectedMessage);
        Assert.False(string.IsNullOrWhiteSpace(chat.MessageEditUnavailableReason));
        chat.EditSelectedMessageCommand.Execute(null);
        Assert.False(chat.IsEditingMessage);
    }

    [Fact]
    public void NativeLongPressPresentsTheArmedActionOnceInsteadOfImmediatelySelectingText()
    {
        var presenter = new RecordingSelectionPresenter();
        var previous = MobilePlatformServices.TextSelectionPresenter;
        MobilePlatformServices.TextSelectionPresenter = presenter;
        try
        {
            var opened = 0;
            MobilePlatformServices.ArmTextSelectionGesture("user text", () => opened++);
            MobilePlatformServices.PresentTextSelectionGesture();
            MobilePlatformServices.PresentTextSelectionGesture();
            Assert.Equal(1, opened);
            Assert.Null(presenter.Text);
            MobilePlatformServices.ClearTextSelectionGesture();
            MobilePlatformServices.ArmTextSelectionGesture("ordinary detail");
            MobilePlatformServices.PresentTextSelectionGesture();
            Assert.Equal("ordinary detail", presenter.Text);
        }
        finally
        {
            MobilePlatformServices.ClearTextSelectionGesture();
            MobilePlatformServices.TextSelectionPresenter = previous;
        }
    }

    [Fact]
    public Task HoldingTheBubblePaddingOpensReachableUserActionsAndCopyCopiesTheWholeMessage() =>
        RunUiAsync(412, async (shell, view, window) =>
        {
            var bubble = Named<StrataChatMessage>(view, "UserMessageBubble");
            Assert.True(NativeTextSelection.GetIsEnabled(bubble));
            var point = bubble.TranslatePoint(new Point(6, bubble.Bounds.Height - 6), window)!.Value;
            var hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(point));
            Assert.True(ReferenceEquals(hit, bubble) || hit.GetVisualAncestors().Contains(bubble));
            var touch = window.TouchBegin(point);
            await Task.Delay(850);
            Pump(window);
            Assert.True(shell.Chat.IsMessageActionsOpen,
                $"Hold target: {(hit as Control)?.Name ?? hit.GetType().Name}; "
                + $"holding enabled: {InputElement.GetIsHoldingEnabled((StyledElement)hit)}");
            window.TouchEnd(touch, point);
            await SettleAsync(window);
            Assert.False(shell.Chat.IsEditingMessage);
            foreach (var name in new[] { "CopyMessageButton", "EditMessageButton", "SelectMessageTextButton" })
            {
                var button = Named<Button>(view, name);
                Assert.True(button.IsEffectivelyVisible);
                Assert.True(button.Bounds.Height >= 48 && button.Bounds.Width >= 48, $"{name}: {button.Bounds}");
            }
            Tap(window, Named<Button>(view, "CopyMessageButton"));
            await SettleAsync(window);
            Assert.False(shell.Chat.IsMessageActionsOpen);
            Assert.Equal("Original user message", await window.Clipboard!.TryGetTextAsync());
        });

    [Fact]
    public Task AssistantActionsHaveNoEditAndExplicitSelectionCanCopyOnlyPartOfTheAnswer() =>
        RunUiAsync(412, async (shell, view, window) =>
        {
            var markdown = view.GetVisualDescendants().OfType<StrataMarkdown>()
                .Single(control => control.DataContext is AssistantItemViewModel);
            Hold(markdown);
            await SettleAsync(window);
            Assert.True(shell.Chat.IsMessageActionsOpen);
            Assert.False(Named<Button>(view, "EditMessageButton").IsEffectivelyVisible);
            Tap(window, Named<Button>(view, "SelectMessageTextButton"));
            await SettleAsync(window);
            Assert.True(shell.Chat.IsMessageSelectionOpen);
            Assert.False(shell.CanDragDrawer);
            var text = Named<TextBox>(view, "MessageSelectionTextBox");
            Assert.True(text.IsReadOnly);
            Assert.False(text.ClearSelectionOnLostFocus);
            Assert.False(NativeTextInputOverlay.GetIsEnabled(text));
            Assert.True(text.IsFocused);
            text.SelectionStart = 0;
            text.SelectionEnd = "Assistant".Length;
            Tap(window, Named<Button>(view, "CopyMessageSelectionButton"));
            await SettleAsync(window);
            Assert.Equal("Assistant", await window.Clipboard!.TryGetTextAsync());
            Tap(window, Named<Button>(view, "CloseMessageSelectionButton"));
            await SettleAsync(window);
            Assert.False(shell.Chat.HasOpenSheet);
        });

    [Fact]
    public async Task SelectTextUsesTheNativePresenterWhenTheHostProvidesOne()
    {
        var previous = MobilePlatformServices.TextSelectionPresenter;
        var presenter = new RecordingSelectionPresenter();
        MobilePlatformServices.TextSelectionPresenter = presenter;
        try
        {
            await RunUiAsync(412, async (shell, view, window) =>
            {
                Hold(Named<StrataChatMessage>(view, "UserMessageBubble"));
                await SettleAsync(window);
                Tap(window, Named<Button>(view, "SelectMessageTextButton"));
                Assert.Equal("Original user message", presenter.Text);
                Assert.False(shell.Chat.IsMessageSelectionOpen);
                Assert.False(shell.Chat.IsMessageActionsOpen);
            });
        }
        finally
        {
            MobilePlatformServices.TextSelectionPresenter = previous;
        }
    }

    [Theory]
    [InlineData(320, "en", false)]
    [InlineData(884, "he", true)]
    public Task EditIsLocalizedAndCancelableWithoutChangingAnyComposerDraft(double width, string language, bool light) =>
        RunUiAsync(width, async (shell, view, window) =>
        {
            shell.Chat.PromptText = "Newer draft";
            Hold(Named<StrataChatMessage>(view, "UserMessageBubble"));
            await SettleAsync(window);
            Tap(window, Named<Button>(view, "EditMessageButton"));
            await SettleAsync(window);
            var editor = Named<TextBox>(view, "MessageEditTextBox");
            Assert.True(editor.IsFocused);
            Assert.Equal("Original user message", editor.Text);
            Assert.Equal("Newer draft", shell.Chat.PromptText);
            editor.Text = "An unsent correction";
            Pump(window);
            Assert.Equal("An unsent correction", shell.Chat.EditedMessageText);
            var sheet = Named<StrataBottomSheet>(view, "MessageEditSheet");
            Assert.Equal(ChatSessionStrings.Get("EditMessage"), sheet.Title);
            Assert.Equal(ChatSessionStrings.Get("MessageEditWarning"), sheet.Description);
            Assert.False(Named<Panel>(view, "MessageMenuLayer").IsVisible);
            var save = Named<Button>(view, "SaveMessageEditButton");
            Assert.True(save.Bounds.Height >= 48);
            Assert.True(save.TranslatePoint(new Point(save.Bounds.Width, save.Bounds.Height), view)!.Value.Y <= view.Bounds.Height);
            Tap(window, Named<Button>(view, "CancelMessageEditButton"));
            await SettleAsync(window);
            // Cancel ends the message's actions, as a phone's editor does, rather than reopening its menu.
            Assert.False(shell.Chat.IsMessageActionsOpen);
            Assert.False(shell.Chat.HasOpenSheet);
            Assert.False(Named<Panel>(view, "MessageMenuLayer").IsVisible);
            Assert.Equal("Newer draft", shell.Chat.PromptText);
            Assert.Equal("Original user message", Assert.IsType<UserTurnItemViewModel>(shell.Chat.Turns[0].Items[0]).Text);
            Assert.False(shell.Chat.DismissTopmostSheet());
        }, language, light);

    [Theory]
    [InlineData(412, "en")]
    [InlineData(884, "he")]
    public Task TheMenuFloatsBesideTheLitBubbleAlignedToItsOuterEdge(double width, string language) =>
        RunUiAsync(width, async (shell, view, window) =>
        {
            var chatView = view.GetVisualDescendants().OfType<ChatDetailView>().Single();
            var bubble = Named<StrataChatMessage>(view, "UserMessageBubble");
            Hold(bubble);
            await SettleAsync(window);
            Assert.True(shell.Chat.IsMessageMenuOpen);

            var layer = Named<Panel>(view, "MessageMenuLayer");
            Assert.True(layer.IsEffectivelyVisible);
            Assert.Equal(Avalonia.Media.FlowDirection.LeftToRight, layer.FlowDirection);
            Assert.Equal(ChatSessionStrings.UiFlowDirection, Named<Border>(view, "MessageMenu").FlowDirection);
            var part = bubble.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Bubble");
            var lit = ScreenRect(part, layer);
            AssertClose(lit, chatView.MessageHighlightBounds);

            var menu = ScreenRect(Named<Panel>(view, "MessageMenuSurface"), layer);
            AssertClose(chatView.MessageMenuBounds, menu);
            Assert.True(new Rect(layer.Bounds.Size).Contains(menu), $"{menu} leaves {layer.Bounds}");
            Assert.False(menu.Intersects(lit), $"{menu} covers the message {lit}");
            Assert.InRange(Math.Abs(menu.Right - lit.Right), 0, 1);
            Assert.InRange(menu.Top - lit.Bottom, ChatDetailView.MessageMenuGap - 1, ChatDetailView.MessageMenuGap + 1);

            // The page dims around the message, which stays lit.
            var scrim = Named<Avalonia.Controls.Shapes.Path>(view, "MessageMenuScrim");
            Assert.False(scrim.IsHitTestVisible);
            var dim = Assert.IsType<Avalonia.Media.CombinedGeometry>(scrim.Data);
            Assert.Equal(Avalonia.Media.GeometryCombineMode.Exclude, dim.GeometryCombineMode);
            Assert.Equal(new Rect(layer.Bounds.Size), Assert.IsType<Avalonia.Media.RectangleGeometry>(dim.Geometry1).Rect);
            var hole = Assert.IsType<Avalonia.Media.RectangleGeometry>(dim.Geometry2);
            AssertClose(lit, hole.Rect);
            Assert.Equal(part.CornerRadius.TopLeft, hole.RadiusX);

            // Every row is a 48 dp target with its glyph in one column, centred on the label.
            var rows = new[] { "CopyMessageButton", "EditMessageButton", "SelectMessageTextButton" }
                .Select(name => Named<Button>(view, name)).ToArray();
            var icons = rows.Select(row => row.GetVisualDescendants().OfType<StrataLineIcon>().Single()).ToArray();
            foreach (var (row, icon) in rows.Zip(icons))
            {
                Assert.True(row.IsEffectivelyVisible);
                Assert.True(row.Bounds.Height >= 48 && row.Bounds.Width >= 48, $"{row.Name}: {row.Bounds}");
                var glyph = ScreenRect(icon, layer);
                var label = ScreenRect(row.GetVisualDescendants().OfType<TextBlock>().First(), layer);
                Assert.InRange(Math.Abs(glyph.Center.Y - label.Center.Y), 0, 1);
                Assert.Equal(20, glyph.Width);
            }
            var columns = icons.Select(icon => ScreenRect(icon, layer).Center.X).ToArray();
            Assert.All(columns, x => Assert.InRange(Math.Abs(x - columns[0]), 0, 0.5));
        }, language);

    [Fact]
    public Task TouchingTheDimOrPressingBackClosesTheMenu() =>
        RunUiAsync(412, async (shell, view, window) =>
        {
            Hold(Named<StrataChatMessage>(view, "UserMessageBubble"));
            await SettleAsync(window);
            var layer = Named<Panel>(view, "MessageMenuLayer");
            var point = layer.TranslatePoint(new Point(layer.Bounds.Width / 2, 24), window)!.Value;
            var hit = Assert.IsAssignableFrom<Control>(window.InputHitTest(point));
            Assert.Equal("MessageMenuDismissArea", hit.Name);
            var touch = window.TouchBegin(point);
            window.TouchEnd(touch, point);
            await SettleAsync(window);
            Assert.False(shell.Chat.IsMessageActionsOpen);
            Assert.False(layer.IsVisible);

            Hold(Named<StrataChatMessage>(view, "UserMessageBubble"));
            await SettleAsync(window);
            Assert.True(shell.Chat.IsMessageMenuOpen);
            Assert.True(shell.Chat.DismissTopmostSheet());
            Assert.False(shell.Chat.HasOpenSheet);
        });

    [Fact]
    public void TheMenuGoesBelowThenAboveThenBesideThePressAndStaysOnScreen()
    {
        var area = new Rect(12, 12, 388, 776);
        var menu = new Size(220, 160);

        var below = ChatDetailView.PlaceMessageMenu(area, new Rect(200, 100, 190, 60), menu, alignEnd: true, press: null);
        Assert.True(below.IsBelow);
        Assert.Equal(new Rect(170, 168, 220, 160), below.Bounds);

        var above = ChatDetailView.PlaceMessageMenu(area, new Rect(200, 680, 190, 60), menu, alignEnd: true, press: null);
        Assert.False(above.IsBelow);
        Assert.Equal(512, above.Bounds.Y);

        // A message taller than the screen: the card sits by the finger, centred on it over an answer.
        var tall = new Rect(0, -400, 412, 1600);
        var byPress = ChatDetailView.PlaceMessageMenu(area, tall, menu, alignEnd: false, press: new Point(30, 300));
        Assert.True(byPress.IsBelow);
        Assert.Equal(new Rect(12, 308, 220, 160), byPress.Bounds);
        var nearBottom = ChatDetailView.PlaceMessageMenu(area, tall, menu, alignEnd: false, press: new Point(300, 760));
        Assert.False(nearBottom.IsBelow);
        Assert.Equal(new Rect(180, 592, 220, 160), nearBottom.Bounds);

        var noPress = ChatDetailView.PlaceMessageMenu(area, tall, menu, alignEnd: false, press: null);
        Assert.True(area.Contains(noPress.Bounds));
    }

    [Fact]
    public Task MenuGlyphsShareThePencilsBoxOnTheLineIconGrid() =>
        RunUiAsync(412, (shell, view, window) =>
        {
            var pencil = StrataIcons.Pencil.Bounds;
            foreach (var key in new[] { "Mobile.Icon.Copy", "Mobile.Icon.SelectText" })
            {
                Assert.True(view.TryFindResource(key, view.ActualThemeVariant, out var resource), key);
                var bounds = Assert.IsAssignableFrom<Avalonia.Media.Geometry>(resource).Bounds;
                Assert.InRange(bounds.Center.X, 11.5, 12.5);
                Assert.InRange(bounds.Center.Y, 11.5, 12.5);
                Assert.InRange(bounds.Width, pencil.Width - 1, pencil.Width + 1);
                Assert.InRange(bounds.Height, pencil.Height - 1, pencil.Height + 1);
            }
            return Task.CompletedTask;
        });

    [Fact]
    public Task TheMenuFollowsItsMessageWhenTheTranscriptScrollsUnderIt() =>
        RunUiAsync(412, async (shell, view, window) =>
        {
            var long_ = string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"Paragraph {i} of a streaming answer."));
            var transcript = Transcript(shell.Chat.ChatId);
            transcript.Revision = 2;
            transcript.Turns[0].Items[1].Text = long_;
            shell.Chat.ApplyTranscript(transcript);
            await SettleAsync(window);

            var chatView = view.GetVisualDescendants().OfType<ChatDetailView>().Single();
            var bubble = Named<StrataChatMessage>(view, "UserMessageBubble");
            var scroller = bubble.GetVisualAncestors().OfType<ScrollViewer>().First();
            scroller.Offset = new Vector(0, 0);
            await SettleAsync(window);
            Hold(bubble);
            await SettleAsync(window);
            var before = chatView.MessageHighlightBounds;

            // A streaming answer's follow moves the transcript while the menu is open.
            scroller.Offset = new Vector(0, 60);
            await SettleAsync(window);
            Assert.True(shell.Chat.IsMessageMenuOpen);
            var part = bubble.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Bubble");
            var lit = ScreenRect(part, Named<Panel>(view, "MessageMenuLayer"));
            Assert.InRange(before.Y - lit.Y, 59, 61);
            AssertClose(lit, chatView.MessageHighlightBounds);
            Assert.InRange(chatView.MessageMenuBounds.Top - lit.Bottom,
                ChatDetailView.MessageMenuGap - 1, ChatDetailView.MessageMenuGap + 1);
        });

    [Fact]
    public async Task TheJustSentBubbleOpensTheMenuWithEditWaitingForTheDesktop()
    {
        var sink = new EditSink();
        var chat = CreateChat(sink);
        chat.PromptText = "Just sent";
        var send = chat.SendCommand.ExecuteAsync(null);
        var echo = chat.Turns.SelectMany(turn => turn.Items).OfType<UserTurnItemViewModel>()
            .Single(item => item.Text == "Just sent");

        Assert.NotNull(echo.OpenActionsCommand);
        echo.OpenActionsCommand!.Execute(null);
        Assert.True(chat.IsMessageMenuOpen);
        Assert.Equal("Just sent", chat.SelectedMessageText);
        Assert.False(chat.CanEditSelectedMessage);
        Assert.Equal(ChatSessionStrings.Get("MessageEditPending"), chat.MessageEditUnavailableReason);

        sink.Completion.SetResult(new RemoteCommandResult { Ok = true, ChatId = chat.ChatId });
        await send;
    }

    private static Rect ScreenRect(Visual visual, Visual layer) =>
        new Rect(visual.Bounds.Size).TransformToAABB(visual.TransformToVisual(layer)!.Value);

    private static void AssertClose(Rect expected, Rect actual)
    {
        Assert.True(Math.Abs(expected.X - actual.X) < 1 && Math.Abs(expected.Y - actual.Y) < 1
            && Math.Abs(expected.Width - actual.Width) < 1 && Math.Abs(expected.Height - actual.Height) < 1,
            $"Expected {expected}, got {actual}");
    }

    private static MobileChatViewModel CreateChat(EditSink sink, string userText = "Original user message")
    {
        var chat = new MobileChatViewModel(sink, action => action());
        chat.Reset(Guid.NewGuid(), "Message actions");
        chat.ApplyTranscript(Transcript(chat.ChatId, userText));
        return chat;
    }

    private static RemoteTranscript Transcript(Guid id, string userText = "Original user message") => new()
    {
        ChatId = id, Revision = 1,
        Turns =
        [
            new RemoteTranscriptTurn
            {
                Id = "turn", FinalAnswerId = "answer",
                Items =
                [
                    new() { Id = Guid.NewGuid().ToString("N"), Kind = RemoteProtocol.ItemKinds.User, Text = userText },
                    new() { Id = "answer", Kind = RemoteProtocol.ItemKinds.Assistant, Text = "Assistant message text." }
                ]
            }
        ]
    };

    private static async Task RunUiAsync(double width, Func<MobileShellViewModel, MobileShellView, Window, Task> test,
        string language = "en", bool light = false)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            var previousLanguage = ChatSessionStrings.Language;
            ChatSessionStrings.Load(language);
            await using var server = new FakeLumiDesktop();
            server.Capabilities.Add(RemoteProtocol.Capabilities.MessageEditingV1);
            server.Start();
            await using var shell = new MobileShellViewModel(store: session.NewStore(), post: action =>
            {
                if (Dispatcher.UIThread.CheckAccess()) action();
                else Dispatcher.UIThread.Post(action);
            });
            await shell.Client.HelloAsync(server.BaseUrl, CancellationToken.None);
            shell.IsPaired = shell.IsConnected = shell.IsHostReady = true;
            shell.Chat.Reset(Guid.NewGuid(), "Message actions");
            shell.Chat.ApplyTranscript(Transcript(shell.Chat.ChatId));
            shell.IsDrawerOpen = false;
            var view = new MobileShellView { DataContext = shell };
            var window = new Window
            {
                Width = width, Height = 800, Content = view,
                RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark
            };
            try
            {
                window.Show();
                await SettleAsync(window);
                await test(shell, view, window);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
                ChatSessionStrings.Load(previousLanguage);
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void Pump(Window window)
    {
        window.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task SettleAsync(Window window)
    {
        Pump(window);
        using (window.CaptureRenderedFrame()) { }
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(StrataBottomSheet.OpenDuration + TimeSpan.FromMilliseconds(60));
        Pump(window);
        using var frame = window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Hold(Control control)
    {
        var args = (HoldingRoutedEventArgs)Activator.CreateInstance(typeof(HoldingRoutedEventArgs),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            binder: null, [HoldingState.Started, new Point(12, 12), PointerType.Touch, null], culture: null)!;
        control.RaiseEvent(args);
    }

    private static void Tap(Window window, Button button)
    {
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        var hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(point));
        Assert.True(ReferenceEquals(hit, button) || hit.GetVisualAncestors().Contains(button),
            $"{button.Name} at {point} ({button.Bounds}) is covered by "
            + $"{hit.GetType().Name} {(hit as Control)?.Name}; "
            + $"ancestry: {string.Join(" > ", hit.GetVisualAncestors().OfType<Control>().Select(control => control.Name ?? control.GetType().Name))}");
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Pump(window);
    }

    private sealed class RecordingSelectionPresenter : ITextSelectionPresenter
    {
        public string? Text { get; private set; }
        public void Show(string text) => Text = text;
        public void Dismiss() => Text = null;
    }

    private sealed class EditSink : IRemoteCommandSink
    {
        public bool SupportsMessageEditing { get; init; } = true;
        public List<RemoteCommand> Commands { get; } = [];
        public TaskCompletionSource<RemoteCommandResult> Completion { get; set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command)
        {
            Commands.Add(command);
            return Completion.Task;
        }
        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            throw new NotSupportedException();
    }
}
