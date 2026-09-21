using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;
using Xunit;
using Xunit.Abstractions;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class ChatInitialBottomTests(ITestOutputHelper output)
{
    [Fact]
    public async Task EmptyTurnRealizesWhenItsFirstContentArrives()
    {
        using var session = HeadlessMobileSession.Start();
        await session.Dispatch(async () =>
        {
            await using var model = new MobileShellViewModel(
                store: session.NewStore(), post: action => action());
            var view = new ChatDetailView { DataContext = model };
            var window = new Window { Width = 412, Height = 892, Content = view };
            window.Show();
            try
            {
                var chatId = Guid.NewGuid();
                model.Chat.Reset(chatId, "Starting", isLoading: true);
                var transcript = new RemoteTranscript
                {
                    ChatId = chatId, Revision = 1, IsLatestWindow = true,
                    Status = new RemoteChatStatus { ChatId = chatId, IsBusy = true },
                    Turns = [new() { Id = "starting-turn" }]
                };
                model.Chat.ApplyTranscript(transcript);
                model.Chat.IsLoading = false;
                await PumpAsync();

                transcript.Revision = 2;
                transcript.TotalRawMessageCount = 1;
                transcript.WindowEndMessageIndex = 1;
                transcript.Turns[0].Items.Add(new()
                {
                    Id = "first-answer", Kind = RemoteProtocol.ItemKinds.Assistant,
                    Text = "The first answer arrived."
                });
                model.Chat.ApplyTranscript(transcript);
                await PumpAsync();

                Assert.Contains(view.GetVisualDescendants().OfType<StrataMarkdown>(),
                    markdown => markdown.Markdown == "The first answer arrived.");
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(412)]
    [InlineData(1000)]
    public async Task TranscriptRealizesOnlyNearbyTurnsAndKeepsOffscreenStreamingContent(double width)
    {
        using var session = HeadlessMobileSession.Start();
        await session.Dispatch(async () =>
        {
            await using var model = new MobileShellViewModel(
                store: session.NewStore(), post: action => action());
            var view = new ChatDetailView { DataContext = model };
            var window = new Window { Width = width, Height = 892, Content = view };
            window.Show();
            try
            {
                var initializedAnswers = new HashSet<string>(StringComparer.Ordinal);
                using var subscription = StrataMarkdown.MarkdownProperty.Changed.AddClassHandler<StrataMarkdown>(
                    (markdown, _) =>
                    {
                        if (markdown.Markdown?.StartsWith("### Step ", StringComparison.Ordinal) == true)
                            initializedAnswers.Add(markdown.Markdown);
                    });
                var chatId = Guid.NewGuid();
                await OpenChatAsync(model, chatId);
                var shell = view.FindControl<StrataChatShell>("ChatShell")!;
                var transcript = view.FindControl<ItemsControl>("Transcript")!;
                Assert.Equal(48, model.Chat.Turns.Sum(turn => turn.Items.Count));
                Assert.InRange(RealizedTurns(transcript), 1, 8);
                Assert.NotNull(TurnView(transcript, 23).Child);
                Assert.Null(TurnView(transcript, 0).Child);
                AssertAtBottom(shell, "virtualized initial landing");
                Assert.InRange(initializedAnswers.Count, 1, 8);

                shell.PreserveViewport();
                transcript.ScrollIntoView(0);
                await PumpAsync();
                var first = Assert.IsAssignableFrom<Control>(transcript.ContainerFromIndex(0));
                var readerPosition = first.TranslatePoint(default, shell)!.Value.Y;
                Assert.Null(TurnView(transcript, 23).Child);
                Assert.InRange(RealizedTurns(transcript), 1, 8);
                Assert.False(shell.IsFollowingTail);

                const string tail = "## Updated while offscreen\n\nThis answer remains available after scrolling back.";
                Assert.True(model.Chat.ApplyDelta(new RemoteStreamDelta
                {
                    ChatId = chatId, ItemId = "answer-23", Text = tail, Offset = -1
                }));
                await PumpAsync();
                Assert.False(shell.IsFollowingTail);
                Assert.Null(TurnView(transcript, 23).Child);
                Assert.InRange(Math.Abs(first.TranslatePoint(default, shell)!.Value.Y - readerPosition), 0, 1);

                shell.JumpToLatest();
                await PumpAsync();
                AssertAtBottom(shell, "return to updated tail");
                var last = Assert.IsAssignableFrom<Control>(transcript.ContainerFromIndex(23));
                Assert.Contains(last.GetVisualDescendants().OfType<StrataMarkdown>(),
                    markdown => markdown.Markdown == tail);
                Assert.InRange(RealizedTurns(transcript), 1, 8);
                Assert.Equal(48, model.Chat.Turns.Sum(turn => turn.Items.Count));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("composer-growth")]
    [InlineData("composer-collapse")]
    [InlineData("markdown-growth")]
    public async Task SwitchedChat_SettlesAtBottomAfterLateLayout(string change)
    {
        using var session = HeadlessMobileSession.Start();
        await session.Dispatch(async () =>
        {
            await using var model = new MobileShellViewModel(
                store: session.NewStore(), post: action => action());
            var view = new ChatDetailView { DataContext = model };
            var window = new Window { Width = 412, Height = 892, Content = view };
            window.Show();
            try
            {
                await PumpAsync();
                var shell = view.FindControl<StrataChatShell>("ChatShell")!;
                var composer = view.FindControl<StrataChatComposer>("Composer")!;
                await OpenChatAsync(model, Guid.NewGuid());
                shell.PreserveViewport();
                shell.ScrollToVerticalOffset(240);
                await PumpAsync();

                await OpenChatAsync(model, Guid.NewGuid());
                Assert.Equal(48, model.Chat.Turns.Sum(turn => turn.Items.Count));
                Assert.True(view.FindControl<Border>("ChatTranscriptSideInset")!.IsHitTestVisible);
                AssertAtBottom(shell, "initial landing");

                // These changes happen AFTER the entrance and both initial scroll callbacks.
                // A native editor / composer animation changes the same viewport metrics.
                var viewportBefore = shell.ViewportHeight;
                var extentBefore = shell.ExtentHeight;
                var generation = shell.ScrollGeneration;
                if (change == "composer-growth")
                    composer.Height = composer.Bounds.Height + 32;
                else if (change == "composer-collapse")
                    composer.Height = composer.Bounds.Height - 24;
                else
                    GrowLastMarkdown(view);
                await PumpAsync();
                output.WriteLine(
                    $"{change}: viewport {viewportBefore:F1} -> {shell.ViewportHeight:F1}, "
                    + $"extent {extentBefore:F1} -> {shell.ExtentHeight:F1}, "
                    + $"bottom gap {shell.CurrentDistanceFromBottom:F1}, follow {shell.IsFollowingTail}");
                AssertAtBottom(shell, change);

                // Collapsing a composer clamps the offset upward; it must not be mistaken for
                // a reader leaving the tail, or the next markdown measurement remains above it.
                GrowLastMarkdown(view);
                await PumpAsync();
                output.WriteLine($"After markdown: extent {shell.ExtentHeight:F1}, "
                    + $"bottom gap {shell.CurrentDistanceFromBottom:F1}, follow {shell.IsFollowingTail}");
                Assert.True(shell.ExtentHeight > extentBefore, "The delayed markdown must actually grow.");
                AssertAtBottom(shell, "subsequent markdown layout");
                Assert.Equal(generation, shell.ScrollGeneration);
                Assert.False(shell.HasNewContent);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("touch")]
    [InlineData("wheel")]
    [InlineData("keyboard")]
    public async Task ReadingHistory_CancelsQueuedSettlingAndSurvivesLateLayout(string input)
    {
        using var session = HeadlessMobileSession.Start();
        await session.Dispatch(async () =>
        {
            await using var model = new MobileShellViewModel(
                store: session.NewStore(), post: action => action());
            var view = new ChatDetailView { DataContext = model };
            var window = new Window { Width = 412, Height = 892, Content = view };
            window.Show();
            try
            {
                var chatId = Guid.NewGuid();
                await OpenChatAsync(model, chatId);
                var shell = view.FindControl<StrataChatShell>("ChatShell")!;
                var scroll = shell.TranscriptScrollViewer!;
                AssertAtBottom(shell, "before reading");
                var bottomOffset = shell.VerticalOffset;
                var generation = shell.ScrollGeneration;
                shell.NotifyTranscriptLayoutChanged();
                if (input == "touch")
                {
                    LastMarkdown(view).RaiseEvent(new ScrollGestureEventArgs(
                        ScrollGestureEventArgs.GetNextFreeId(), new Vector(0, -120))
                    {
                        RoutedEvent = InputElement.ScrollGestureEvent
                    });
                }
                else if (input == "wheel")
                {
                    var point = scroll.TranslatePoint(
                        new Point(scroll.Bounds.Width / 2, scroll.Bounds.Height / 2), window)!.Value;
                    window.MouseWheel(point, new Vector(0, 3), RawInputModifiers.None);
                }
                else
                {
                    scroll.RaiseEvent(new KeyEventArgs
                    {
                        RoutedEvent = InputElement.KeyDownEvent, Key = Key.PageUp
                    });
                }
                await PumpAsync();
                Assert.True(shell.ScrollGeneration > generation);
                Assert.False(shell.IsFollowingTail);
                Assert.True(shell.VerticalOffset < bottomOffset - 10);
                var readerAnchor = CaptureReaderAnchor(view);

                var composer = view.FindControl<StrataChatComposer>("Composer")!;
                composer.Height = composer.Bounds.Height + 32;
                GrowLastMarkdown(view);
                await PumpAsync();
                Assert.False(shell.IsFollowingTail);
                AssertReaderAnchor(view, readerAnchor);
                Assert.False(shell.HasNewContent); // Layout alone is not unread content.

                var refreshed = Transcript(chatId, revision: 2);
                refreshed.Turns.Add(new RemoteTranscriptTurn
                {
                    Id = "new-turn",
                    Items = [new RemoteTranscriptItem { Id = "new-answer", Text = "New remote answer" }]
                });
                model.Chat.ApplyTranscript(refreshed);
                await PumpAsync();
                Assert.False(shell.IsFollowingTail);
                Assert.True(shell.HasNewContent);
                AssertReaderAnchor(view, readerAnchor);

                model.Chat.ApplyTranscript(Transcript(chatId, revision: 3, latest: false));
                await PumpAsync();
                var pageAnchor = CaptureReaderAnchor(view);
                Assert.False(model.Chat.IsLatestWindow);
                composer.Height += 24;
                GrowLastMarkdown(view);
                await PumpAsync();
                Assert.False(shell.IsFollowingTail);
                AssertReaderAnchor(view, pageAnchor);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NavigationWidthReflow_PreservesExistingScrollIntent(bool readingHistory)
    {
        using var session = HeadlessMobileSession.Start();
        await session.Dispatch(async () =>
        {
            await using var model = new MobileShellViewModel(
                store: session.NewStore(), post: action => action());
            var view = new ChatDetailView { DataContext = model };
            // Match the drawer's one-time chat-width commitment without depending on its
            // template landmarks, gestures, or transform-only reveal animation.
            var navigationSpace = new Border { Width = 0 };
            var host = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            host.Children.Add(navigationSpace);
            Grid.SetColumn(view, 1);
            host.Children.Add(view);
            var window = new Window { Width = 1024, Height = 892, Content = host };
            window.Show();
            try
            {
                await OpenChatAsync(model, Guid.NewGuid());
                var transcript = Transcript(model.Chat.ChatId, revision: 2);
                foreach (var turn in transcript.Turns)
                {
                    turn.Items[^1].Text += "\n\n"
                        + string.Join(" ", Enumerable.Repeat(
                            "This longer paragraph reflows when the navigation pane reserves chat width.", 8));
                }
                model.Chat.ApplyTranscript(transcript);
                await PumpAsync();
                var shell = view.FindControl<StrataChatShell>("ChatShell")!;
                var scroll = shell.TranscriptScrollViewer!;
                AssertAtBottom(shell, "before navigation");
                if (readingHistory)
                {
                    scroll.RaiseEvent(new KeyEventArgs
                    {
                        RoutedEvent = InputElement.KeyDownEvent, Key = Key.PageUp
                    });
                    await PumpAsync();
                    Assert.False(shell.IsFollowingTail);
                    shell.ScrollToVerticalOffset(400);
                    await PumpAsync();
                }

                foreach (var reservedWidth in new[] { 328d, 0d })
                {
                    var widthBefore = scroll.Viewport.Width;
                    var extentBefore = shell.ExtentHeight;
                    var generation = shell.ScrollGeneration;
                    navigationSpace.Width = reservedWidth;
                    await PumpAsync();
                    Assert.NotEqual(widthBefore, scroll.Viewport.Width);
                    Assert.NotEqual(extentBefore, shell.ExtentHeight);
                    output.WriteLine(
                        $"Navigation reservation {reservedWidth:F0}: viewport width "
                        + $"{widthBefore:F1} -> {scroll.Viewport.Width:F1}, extent "
                        + $"{extentBefore:F1} -> {shell.ExtentHeight:F1}, "
                        + $"bottom gap {shell.CurrentDistanceFromBottom:F1}, "
                        + $"reading {readingHistory}, follow {shell.IsFollowingTail}");
                    GrowLastMarkdown(view);
                    await PumpAsync();
                    if (readingHistory)
                    {
                        Assert.False(shell.IsFollowingTail);
                        Assert.True(shell.CurrentDistanceFromBottom > 500,
                            "Navigation width reflow must not snap a history reader to the tail.");
                    }
                    else
                    {
                        AssertAtBottom(shell, "navigation width reflow and late markdown");
                        Assert.Equal(generation, shell.ScrollGeneration);
                    }
                    Assert.False(shell.HasNewContent);
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RapidSwitch_InvalidatesOldLandingAndLetsNewReaderCancelSettling()
    {
        using var session = HeadlessMobileSession.Start();
        await session.Dispatch(async () =>
        {
            await using var model = new MobileShellViewModel(
                store: session.NewStore(), post: action => action());
            var view = new ChatDetailView { DataContext = model };
            var window = new Window { Width = 412, Height = 892, Content = view };
            window.Show();
            try
            {
                await OpenChatAsync(model, Guid.NewGuid());
                var shell = view.FindControl<StrataChatShell>("ChatShell")!;
                shell.NotifyTranscriptLayoutChanged();
                var oldGeneration = shell.ScrollGeneration;

                var skippedChat = Guid.NewGuid();
                model.Chat.Reset(skippedChat, "Skipped chat", isLoading: true);
                model.Chat.ApplyTranscript(Transcript(skippedChat));
                model.Chat.IsLoading = false;
                var selectedChat = Guid.NewGuid();
                model.Chat.Reset(selectedChat, "Selected chat", isLoading: true);
                // The old reveal callback must not expose the new, still-loading surface.
                await PumpAsync();
                Assert.False(view.FindControl<Border>("ChatTranscriptSideInset")!.IsHitTestVisible);
                Assert.False(shell.TryScrollToVerticalOffset(100, oldGeneration));

                model.Chat.ApplyTranscript(Transcript(selectedChat));
                model.Chat.IsLoading = false;
                await PumpAsync();
                AssertAtBottom(shell, "selected chat");
                Assert.True(view.FindControl<Border>("ChatTranscriptSideInset")!.IsHitTestVisible);

                shell.NotifyTranscriptLayoutChanged();
                shell.PreserveViewport();
                shell.ScrollToVerticalOffset(300);
                GrowLastMarkdown(view);
                await PumpAsync();
                Assert.False(shell.IsFollowingTail);
                Assert.InRange(shell.VerticalOffset, 299, 301);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static async Task OpenChatAsync(MobileShellViewModel model, Guid chatId)
    {
        model.Chat.Reset(chatId, "Bottom settling", isLoading: true);
        model.Chat.ApplyTranscript(Transcript(chatId));
        model.Chat.IsLoading = false;
        await PumpAsync();
    }

    private static RemoteTranscript Transcript(Guid chatId, long revision = 1, bool latest = true) => new()
    {
        ChatId = chatId,
        Title = "Bottom settling",
        Revision = revision,
        WindowStartMessageIndex = latest ? 96 : 48,
        WindowEndMessageIndex = latest ? 144 : 96,
        TotalRawMessageCount = 144,
        HasEarlierMessages = true,
        HasLaterMessages = !latest,
        IsLatestWindow = latest,
        Status = new RemoteChatStatus { ChatId = chatId },
        Turns = Enumerable.Range(0, 24).Select(index => new RemoteTranscriptTurn
        {
            Id = $"turn-{index}",
            Items =
            [
                new RemoteTranscriptItem
                {
                    Id = $"user-{index}",
                    Kind = RemoteProtocol.ItemKinds.User,
                    Text = $"Explain step {index}."
                },
                new RemoteTranscriptItem
                {
                    Id = $"answer-{index}",
                    Kind = RemoteProtocol.ItemKinds.Assistant,
                    Text = $"### Step {index}\n\nA **multi-line answer** that wraps on a phone "
                        + "and uses the actual markdown template rather than fixed-height rows.\n\n"
                        + "- First detail\n- Second detail with `inline code`."
                }
            ]
        }).ToList()
    };

    private static void GrowLastMarkdown(ChatDetailView view)
    {
        var markdown = LastMarkdown(view);
        // Change the measured child without another ViewModel notification, as with late content
        // measurement. The shell must observe the resulting extent, not rely on caller timing.
        markdown.Markdown += "\n\nAnother paragraph measured after the initial layout."
            + "\n\n- A late detail\n- One more late detail";
    }

    private static StrataMarkdown LastMarkdown(ChatDetailView view)
    {
        var scroll = view.FindControl<StrataChatShell>("ChatShell")!.TranscriptScrollViewer!;
        return scroll.GetVisualDescendants().OfType<StrataMarkdown>()
            .Where(control => control.IsEffectivelyVisible)
            .OrderBy(control => control.TranslatePoint(default, scroll)!.Value.Y)
            .Last();
    }

    private static int RealizedTurns(ItemsControl transcript) =>
        transcript.GetVisualDescendants().OfType<TranscriptTurnView>().Count(turn => turn.Child is not null);

    private static TranscriptTurnView TurnView(ItemsControl transcript, int index) =>
        transcript.ContainerFromIndex(index)!.GetVisualDescendants().OfType<TranscriptTurnView>().Single();

    private (string TurnId, double Y) CaptureReaderAnchor(ChatDetailView view)
    {
        var scroll = view.FindControl<StrataChatShell>("ChatShell")!.TranscriptScrollViewer!;
        var transcript = view.FindControl<ItemsControl>("Transcript")!;
        var anchor = transcript.GetRealizedContainers()
            .Select(control => (Control: control, Y: control.TranslatePoint(default, scroll)!.Value.Y))
            .Where(item => item.Y < scroll.Viewport.Height && item.Y + item.Control.Bounds.Height > 0)
            .OrderBy(item => item.Y)
            .First();
        var id = ((TranscriptTurnViewModel)anchor.Control.DataContext!).Id;
        output.WriteLine($"Reader {id}: y={anchor.Y:F1}, offset={scroll.Offset.Y:F1}, " +
            $"extent={scroll.Extent.Height:F1}, anchor={(scroll.CurrentAnchor?.DataContext as TranscriptTurnViewModel)?.Id}");
        return (id, anchor.Y);
    }

    private void AssertReaderAnchor(ChatDetailView view, (string TurnId, double Y) expected)
    {
        var scroll = view.FindControl<StrataChatShell>("ChatShell")!.TranscriptScrollViewer!;
        var anchor = view.FindControl<ItemsControl>("Transcript")!.GetRealizedContainers()
            .Single(control => control.DataContext is TranscriptTurnViewModel turn && turn.Id == expected.TurnId);
        // Estimated heights outside a virtualized viewport may change its numeric offset.
        // The reader's actual message must stay at the same screen position.
        var y = anchor.TranslatePoint(default, scroll)!.Value.Y;
        output.WriteLine($"Reader {expected.TurnId}: y={y:F1} (was {expected.Y:F1}), offset={scroll.Offset.Y:F1}, " +
            $"extent={scroll.Extent.Height:F1}, anchor={(scroll.CurrentAnchor?.DataContext as TranscriptTurnViewModel)?.Id}");
        Assert.InRange(Math.Abs(y - expected.Y), 0, 1);
    }

    private static void AssertAtBottom(StrataChatShell shell, string stage)
    {
        Assert.True(shell.ExtentHeight > shell.ViewportHeight);
        Assert.True(shell.IsFollowingTail, $"{stage}: layout incorrectly cancelled follow");
        Assert.True(shell.CurrentDistanceFromBottom <= 1,
            $"{stage}: bottom gap {shell.CurrentDistanceFromBottom:F1} DIP "
            + $"(extent {shell.ExtentHeight:F1}, viewport {shell.ViewportHeight:F1}, offset {shell.VerticalOffset:F1})");
    }

    private static async Task PumpAsync()
    {
        // Allow StrataMarkdown's append throttle and the real mobile entrance to advance.
        await Task.Delay(100);
        for (var i = 0; i < 3; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }
}
