using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

using ChatMessage = Lumi.Models.ChatMessage;

namespace Lumi.Tests;

/// <summary>
/// "Reply to": a user message can quote an earlier assistant message, or just the part of it the user
/// selected. These tests pin how the quote is captured, previewed, persisted, carried through forks and
/// transcript replays, framed for the model, and handed from the composer to exactly one outgoing message.
/// </summary>
public sealed class MessageReplyTests
{
    // ── Capturing and previewing the quote ──

    [Fact]
    public void Create_ReturnsNull_WhenThereIsNothingToQuote()
    {
        Assert.Null(MessageReplyFormatter.Create(Guid.NewGuid(), " \n\t ", isSelection: true, author: "Lumi"));
        Assert.Null(MessageReplyFormatter.Create(Guid.NewGuid(), null, isSelection: false, author: "Lumi"));
    }

    [Fact]
    public void Create_CleansRenderingArtifactsFromSelectedText_AndKeepsTheRestVerbatim()
    {
        var messageId = Guid.NewGuid();

        var reply = MessageReplyFormatter.Create(
            messageId,
            "Use \u2005Span<T>\u2005 here\r\n\r\n\r\nthen\uFFFC stop  ",
            isSelection: true,
            author: " Coding Lumi ");

        Assert.NotNull(reply);
        Assert.Equal(messageId, reply!.MessageId);
        // Blank lines survive (only artifacts go), so the quote can be found again in the rendered text.
        Assert.Equal("Use Span<T> here\n\n\nthen stop", reply.Quote);
        Assert.True(reply.IsSelection);
        Assert.Equal("Coding Lumi", reply.Author);
    }

    [Fact]
    public void Create_KeepsOnlyTheOpeningOfAWholeMessage_ButAWholeSelection()
    {
        var longText = string.Join(' ', Enumerable.Repeat("word", 700));

        var wholeMessage = MessageReplyFormatter.Create(Guid.NewGuid(), longText, isSelection: false, author: null)!;
        var selection = MessageReplyFormatter.Create(Guid.NewGuid(), longText, isSelection: true, author: null)!;

        Assert.True(wholeMessage.Quote.Length <= MessageReplyFormatter.MaxMessageQuoteLength);
        Assert.EndsWith("…", wholeMessage.Quote);
        Assert.DoesNotContain("wor…", wholeMessage.Quote);
        Assert.Equal(longText, selection.Quote);
        Assert.Null(wholeMessage.Author);
    }

    [Fact]
    public void BuildPreview_StripsMarkdownFromWholeMessageQuotes()
    {
        var reply = new MessageReply
        {
            Quote = "## Title\n\n1. **Bold** item with `code`\n- [link](https://example.com) and ![img](y.png)\n```csharp\nvar x = 1;\n```",
            IsSelection = false
        };

        Assert.Equal("Title Bold item with code link and img var x = 1;", MessageReplyFormatter.BuildPreview(reply));
    }

    [Fact]
    public void BuildPreview_KeepsSelectedTextVerbatimOnOneLine()
    {
        var reply = new MessageReply { Quote = "2 * 3 = **six**\n  maybe", IsSelection = true };

        Assert.Equal("2 * 3 = **six** maybe", MessageReplyFormatter.BuildPreview(reply));
        Assert.Equal("", MessageReplyFormatter.BuildPreview(null));
    }

    // ── What the model receives ──

    [Fact]
    public void ComposePrompt_FramesASelectionAsABlockQuoteAboveTheQuestion()
    {
        var reply = new MessageReply { Quote = "first line\n\nsecond line", IsSelection = true };

        var prompt = MessageReplyFormatter.ComposePrompt("What does this mean?", reply);

        Assert.Equal(
            "[Replying to this part of your earlier response]\n"
            + "> first line\n"
            + ">\n"
            + "> second line\n"
            + "\n"
            + "What does this mean?",
            prompt);
    }

    [Fact]
    public void ComposePrompt_NamesAWholeMessageReply_AndLeavesPlainMessagesUntouched()
    {
        var reply = new MessageReply { Quote = "Earlier answer", IsSelection = false };

        Assert.StartsWith("[Replying to your earlier response]\n> Earlier answer\n\n", MessageReplyFormatter.ComposePrompt("Thanks", reply));
        Assert.Equal("Just a message", MessageReplyFormatter.ComposePrompt("Just a message", null));
        Assert.Equal("Blank quote", MessageReplyFormatter.ComposePrompt("Blank quote", new MessageReply { Quote = " " }));
    }

    [Fact]
    public void DescribeForTranscript_ProducesAnInlineMarker()
    {
        var reply = new MessageReply { Quote = "the  second\nstep", IsSelection = true };

        Assert.Equal("(replying to: \"the second step\") ", MessageReplyFormatter.DescribeForTranscript(reply));
        Assert.Equal("", MessageReplyFormatter.DescribeForTranscript(null));
    }

    [Fact]
    public void SessionRecoveryReplay_KeepsTheReplyContextOfEarlierMessages()
    {
        var retained = new List<ChatMessage>
        {
            new() { Role = "assistant", Content = "Step one, then step two." },
            new()
            {
                Role = "user",
                Content = "Why step two?",
                ReplyTo = new MessageReply { Quote = "step two", IsSelection = true }
            }
        };
        var method = typeof(ChatViewModel).GetMethod(
            "BuildSessionRecoveryReplayPrompt",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var replay = (string)method.Invoke(null, [retained, "next"])!;

        Assert.Contains("User: (replying to: \"step two\") Why step two?", replay);
    }

    // ── Persistence, copies and history ──

    [Fact]
    public void ReplyTo_RoundTripsThroughPersistence_AndClonesIndependently()
    {
        var sourceId = Guid.NewGuid();
        var original = new ChatMessage
        {
            Role = "user",
            Content = "Tell me more",
            ReplyTo = new MessageReply { MessageId = sourceId, Quote = "the quote", IsSelection = true, Author = "Lumi" }
        };

        var json = JsonSerializer.Serialize(new List<ChatMessage> { original }, AppDataJsonContext.Default.ListChatMessage);
        var restored = Assert.Single(JsonSerializer.Deserialize(json, AppDataJsonContext.Default.ListChatMessage)!);
        var clone = restored.Clone();
        clone.ReplyTo!.Quote = "changed";

        Assert.Equal(sourceId, restored.ReplyTo!.MessageId);
        Assert.Equal("the quote", restored.ReplyTo.Quote);
        Assert.True(restored.ReplyTo.IsSelection);
        Assert.Equal("Lumi", restored.ReplyTo.Author);
        Assert.NotSame(restored.ReplyTo, clone.ReplyTo);
        Assert.Equal("the quote", restored.ReplyTo.Quote);
    }

    [Fact]
    public void Fork_RepointsRepliesAtTheForkedCopyOfTheirSource()
    {
        var answer = new ChatMessage { Role = "assistant", Content = "Three tips" };
        var reply = new ChatMessage
        {
            Role = "user",
            Content = "Expand the second tip",
            ReplyTo = new MessageReply { MessageId = answer.Id, Quote = "second tip", IsSelection = true }
        };
        var messages = new List<ChatMessage> { new() { Role = "user", Content = "tips?" }, answer, reply };

        var fork = ChatForkFactory.CreateFork(new Chat { Title = "Tips" }, messages).Chat;

        var forkedAnswer = fork.Messages[1];
        var forkedReply = fork.Messages[2];
        Assert.NotEqual(answer.Id, forkedAnswer.Id);
        Assert.Equal(forkedAnswer.Id, forkedReply.ReplyTo!.MessageId);
        Assert.Equal(answer.Id, reply.ReplyTo!.MessageId);
    }

    [Fact]
    public void ForkAndEdit_OnAReply_HandsBackItsQuote_PointingAtTheCopiedAnswer()
    {
        var answer = new ChatMessage { Role = "assistant", Content = "Three tips" };
        var reply = new ChatMessage
        {
            Role = "user",
            Content = "Expand the second tip",
            ReplyTo = new MessageReply { MessageId = answer.Id, Quote = "second tip", IsSelection = true }
        };
        var messages = new List<ChatMessage> { new() { Role = "user", Content = "tips?" }, answer, reply };

        var plan = ChatForkFactory.CreateFork(new Chat { Title = "Tips" }, messages, reply.Id);

        Assert.Equal("Expand the second tip", plan.ComposerPrefill);
        Assert.Equal("second tip", plan.ComposerReply!.Quote);
        Assert.Equal(plan.Chat.Messages[1].Id, plan.ComposerReply.MessageId);
        Assert.Null(ChatForkFactory.CreateFork(new Chat { Title = "Tips" }, messages, messages[0].Id).ComposerReply);
    }

    [Fact]
    public void AssistantMessageItem_OffersReplyOnlyWhenItHasText()
    {
        var withText = new AssistantMessageItem(
            new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "An answer" }),
            showTimestamps: false);
        var filesOnly = new AssistantMessageItem(
            new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "" }),
            showTimestamps: false);

        Assert.True(withText.CanReply);
        Assert.False(filesOnly.CanReply);
    }

    [Fact]
    public async Task ReadChat_ShowsWhatAUserMessageRepliedTo()
    {
        var now = DateTimeOffset.Now;
        var chat = new Chat
        {
            Title = "Reply history",
            UpdatedAt = now,
            Messages =
            [
                new ChatMessage { Role = "assistant", Content = "Use a dictionary here.", Timestamp = now },
                new ChatMessage
                {
                    Role = "user",
                    Content = "Why not a list?",
                    Timestamp = now,
                    ReplyTo = new MessageReply { Quote = "Use a dictionary", IsSelection = true }
                }
            ]
        };
        var store = new DataStore(new AppData { Chats = [chat] });
        var service = new ChatHistoryService(store, null, () => now);

        var transcript = await service.ReadChatAsync(chat.Id.ToString());

        Assert.Contains("User: (replying to: \"Use a dictionary\") Why not a list?", transcript);
    }

    [Fact]
    public void UserMessageItem_ExposesTheQuoteAndJumpsBackToItsSource()
    {
        var reply = new MessageReply { MessageId = Guid.NewGuid(), Quote = "Keep methods small", IsSelection = true };
        MessageReply? opened = null;
        var item = new UserMessageItem(
            new ChatMessageViewModel(new ChatMessage { Role = "user", Content = "Why?", ReplyTo = reply }),
            showTimestamps: false,
            openReplySourceAction: source => opened = source);
        var plain = new UserMessageItem(
            new ChatMessageViewModel(new ChatMessage { Role = "user", Content = "Hi" }),
            showTimestamps: false);

        item.OpenReplySourceCommand.Execute(null);

        Assert.True(item.HasReply);
        Assert.Same(item, item.DisplayReply);
        Assert.Equal("Keep methods small", item.ReplyPreview);
        Assert.Equal("Lumi", item.ReplyAuthor);
        Assert.Same(reply, opened);
        Assert.False(plain.HasReply);
        Assert.Null(plain.DisplayReply);
    }

    // ── The composer's pending reply ──

    [Fact]
    public void BeginReply_ShowsAPendingQuote_MarksItsSource_AndCancelClearsIt()
    {
        using var host = ReplyHost.Create();
        var answer = host.AddAssistantMessage("Keep methods small and focused.");

        host.ViewModel.BeginReply(answer.Id, "small and focused", isSelection: true, author: "Lumi");

        Assert.True(host.ViewModel.HasPendingReply);
        Assert.Equal("small and focused", host.ViewModel.PendingReplyPreview);
        Assert.Equal("Replying to Lumi", host.ViewModel.PendingReplyTitle);
        Assert.Equal("Ask about this…", host.ViewModel.ComposerPlaceholder);
        Assert.True(host.AssistantItem(answer.Id).IsReplySource);

        host.ViewModel.CancelReplyCommand.Execute(null);

        Assert.False(host.ViewModel.HasPendingReply);
        Assert.False(host.AssistantItem(answer.Id).IsReplySource);
        Assert.NotEqual("Ask about this…", host.ViewModel.ComposerPlaceholder);
    }

    [Fact]
    public void PendingReply_StaysWithItsChat_WhenSwitchingChats()
    {
        using var host = ReplyHost.Create();
        var other = new Chat { Title = "other" };
        host.DataStore.Data.Chats.Add(other);

        host.ViewModel.BeginReply(Guid.NewGuid(), "quoted", isSelection: true, author: null);
        var reply = host.ViewModel.PendingReply;

        host.ViewModel.CurrentChat = other;
        Assert.Null(host.ViewModel.PendingReply);

        host.ViewModel.CurrentChat = host.Chat;
        Assert.Same(reply, host.ViewModel.PendingReply);
    }

    [Fact]
    public void QueuedComposerSend_TakesThePendingReply_ButARemoteSendDoesNot()
    {
        using var host = ReplyHost.Create();
        host.ViewModel.BeginReply(Guid.NewGuid(), "quoted", isSelection: true, author: "Lumi");
        var reply = host.ViewModel.PendingReply;

        host.Invoke("QueueBusySendPrompt", host.Chat.Id, "from the phone", null, "Lumi Mobile");
        Assert.Null(host.Chat.Messages.Single().ReplyTo);
        Assert.Same(reply, host.ViewModel.PendingReply);

        host.Invoke("QueueBusySendPrompt", host.Chat.Id, "from the composer", null, null);
        Assert.Same(reply, host.Chat.Messages.Last().ReplyTo);
        Assert.Null(host.ViewModel.PendingReply);
    }

    [Fact]
    public void EditingAReply_ShowsItsQuote_AndCancelRestoresTheDraftReply()
    {
        using var host = ReplyHost.Create();
        var edited = new ChatMessage
        {
            Role = "user",
            Content = "Why?",
            ReplyTo = new MessageReply { MessageId = Guid.NewGuid(), Quote = "edited quote", IsSelection = true }
        };
        host.Chat.Messages.Add(edited);
        host.ViewModel.BeginReply(Guid.NewGuid(), "draft quote", isSelection: true, author: null);
        var draftReply = host.ViewModel.PendingReply;

        host.Invoke("BeginComposerEdit", edited);

        Assert.True(host.ViewModel.IsEditingMessage);
        Assert.Equal("edited quote", host.ViewModel.PendingReply!.Quote);
        Assert.NotSame(edited.ReplyTo, host.ViewModel.PendingReply);

        host.Invoke("CancelComposerEditInternal", true, false);

        Assert.False(host.ViewModel.IsEditingMessage);
        Assert.Same(draftReply, host.ViewModel.PendingReply);
    }

    [Fact]
    public void WhileEditing_OnlyAnAnswerBeforeTheEditedMessageCanBeQuoted()
    {
        using var host = ReplyHost.Create();
        var earlierAnswer = host.AddAssistantMessage("Earlier answer");
        var edited = new ChatMessage { Role = "user", Content = "Edit me" };
        host.Chat.Messages.Add(edited);
        var laterAnswer = host.AddAssistantMessage("Later answer");
        host.Invoke("BeginComposerEdit", edited);

        // The later answer is dropped when the edit is resent, so quoting it is refused.
        host.ViewModel.BeginReply(laterAnswer.Id, "Later", isSelection: true, author: "Lumi");
        Assert.Null(host.ViewModel.PendingReply);

        host.ViewModel.BeginReply(earlierAnswer.Id, "Earlier", isSelection: true, author: "Lumi");
        Assert.Equal(earlierAnswer.Id, host.ViewModel.PendingReply!.MessageId);
    }

    private sealed class ReplyHost : IDisposable
    {
        private ReplyHost(ChatViewModel viewModel, DataStore dataStore, Chat chat)
        {
            ViewModel = viewModel;
            DataStore = dataStore;
            Chat = chat;
        }

        public ChatViewModel ViewModel { get; }
        public DataStore DataStore { get; }
        public Chat Chat { get; }

        public static ReplyHost Create()
        {
            var dataStore = new DataStore(new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false,
                    NotificationsEnabled = false
                }
            });
            var chat = new Chat { Title = "reply" };
            dataStore.Data.Chats.Add(chat);

            var viewModel = new ChatViewModel(dataStore, TestCopilot.Shared)
            {
                CurrentChat = chat
            };
            return new ReplyHost(viewModel, dataStore, chat);
        }

        public ChatMessage AddAssistantMessage(string content)
        {
            var message = new ChatMessage { Role = "assistant", Content = content, Author = "Lumi" };
            Chat.Messages.Add(message);
            ViewModel.Messages.Add(new ChatMessageViewModel(message));
            return message;
        }

        public AssistantMessageItem AssistantItem(Guid messageId)
            => ViewModel.TranscriptTurns
                .SelectMany(static turn => turn.Items)
                .OfType<AssistantMessageItem>()
                .Single(item => item.MessageId == messageId);

        public object? Invoke(string name, params object?[] args)
        {
            var method = typeof(ChatViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Method {name} was not found.");
            return method.Invoke(ViewModel, args);
        }

        public void Dispose() => ViewModel.Dispose();
    }
}

/// <summary>
/// The select-to-reply flow end to end through the real view: selecting words in an answer floats
/// Strata's Reply pill, and the pill hands exactly those words to the composer as a reply that the
/// composer shows, the answer stays marked as the reply source, and Escape dismisses.
/// </summary>
[Collection("Headless UI")]
public sealed class ChatViewReplyWiringTests
{
    [Fact]
    public async Task SelectingAnswerText_ThenPill_QuotesThatSelectionInTheComposer()
    {
        using var session = HeadlessTestSession.Start();

        const string excerpt = "small and focused";
        var chat = new Chat { Title = "Replies" };
        var answer = new ChatMessage
        {
            Role = "assistant",
            Author = "Lumi",
            Content = "Keep each method small and focused on one job."
        };
        chat.Messages.AddRange([new ChatMessage { Role = "user", Content = "How do I keep code clean?" }, answer]);
        var data = new AppData
        {
            Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false }
        };
        data.Chats.Add(chat);

        var pillShown = false;
        MessageReply? pending = null;
        var cardVisible = false;
        var markedAsSource = false;
        var clearedByEscape = false;

        await session.Dispatch(async () =>
        {
            Lumi.Localization.Loc.Load("en");
            var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            var view = new ChatView { DataContext = vm };
            var window = new Window { Width = 1000, Height = 800, Content = view };
            window.Show();
            try
            {
                await PumpAsync();
                await vm.LoadChatAsync(chat);

                StrataChatMessage? message = null;
                for (var i = 0; i < 40 && (message = FindAnswer(view)) is null; i++)
                    await PumpAsync();
                Assert.NotNull(message);

                var text = message!.GetVisualDescendants()
                    .OfType<SelectableTextBlock>()
                    .First(block => block.Text?.Contains(excerpt, StringComparison.Ordinal) == true);
                var start = text.Text!.IndexOf(excerpt, StringComparison.Ordinal);
                DragSelect(window, text, start, start + excerpt.Length);
                await PumpAsync();

                var popup = SelectionReplyPopup(message);
                pillShown = popup?.IsOpen == true;
                Press(Assert.IsType<Button>(popup!.Child));
                await PumpAsync();

                pending = vm.PendingReply;
                cardVisible = view.FindControl<Border>("ComposerReplyCard")!.IsVisible;
                markedAsSource = message.IsReplySource;

                var input = view.FindControl<StrataChatComposer>("Composer")!
                    .GetVisualDescendants()
                    .OfType<TextBox>()
                    .Single(box => box.Name == "PART_Input");
                input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                clearedByEscape = vm.PendingReply is null;
            }
            finally
            {
                window.Close();
                vm.Dispose();
            }
        }, CancellationToken.None);

        Assert.True(pillShown, "Selecting text in an answer should float the Reply pill.");
        Assert.NotNull(pending);
        Assert.Equal(excerpt, pending!.Quote);
        Assert.True(pending.IsSelection);
        Assert.Equal(answer.Id, pending.MessageId);
        Assert.Equal("Lumi", pending.Author);
        Assert.True(cardVisible, "The composer should show the pending reply.");
        Assert.True(markedAsSource, "The replied-to answer should stay marked while the reply is composed.");
        Assert.True(clearedByEscape, "Escape in the composer should dismiss the pending reply.");
    }

    private static StrataChatMessage? FindAnswer(Visual root)
        => root.GetVisualDescendants()
            .OfType<StrataChatMessage>()
            .FirstOrDefault(message => message.CanReply && message.DataContext is AssistantMessageItem);

    private const string MarkdownAnswer =
        "Three habits that pay off:\n\n"
        + "1. **Name things clearly** so code reads like prose.\n"
        + "2. Keep each method small and focused on one job.\n\n"
        + "```csharp\nvar total = items.Sum(item => item.Price);\n```";

    [Fact]
    public async Task JumpToReplySource_ReselectsTheQuoteAcrossMarkdownLineBreaks()
    {
        using var session = HeadlessTestSession.Start();
        var (chat, answer, data) = CreateMarkdownChat(fillerTurns: 0);
        string? reselected = null;
        var flashed = false;
        var clearedAfterFlash = false;

        await session.Dispatch(async () =>
        {
            await WithChatViewAsync(data, chat, async (vm, view, _) =>
            {
                var message = await WaitForAnswerAsync(view, answer.Id);
                vm.BeginReply(answer.Id, "small and focused", isSelection: true, author: "Lumi");
                vm.JumpToPendingReplySourceCommand.Execute(null);
                for (var i = 0; i < 10; i++)
                    await PumpAsync();

                string? SelectedInAnswer() => message.GetVisualDescendants()
                    .OfType<SelectableTextBlock>()
                    .Select(block => block.SelectedText)
                    .FirstOrDefault(text => !string.IsNullOrEmpty(text));
                reselected = SelectedInAnswer();
                flashed = message.Classes.Contains("reply-flash");

                // The highlight leaves with the glow, so it cannot linger as a selection for the
                // message's copy and reply actions.
                await Task.Delay(TimeSpan.FromSeconds(2.4));
                await PumpAsync();
                clearedAfterFlash = SelectedInAnswer() is null && !message.Classes.Contains("reply-flash");
            });
        }, CancellationToken.None);

        Assert.Equal("small and focused", reselected);
        Assert.True(flashed, "The replied-to answer should briefly glow after the jump.");
        Assert.True(clearedAfterFlash, "The jump highlight should clear when the glow ends.");
    }

    [Fact]
    public async Task SelectionPill_InACodeBlock_ClosesOnTranscriptScroll_AndIgnoresAnOlderHighlight()
    {
        using var session = HeadlessTestSession.Start();
        var (chat, answer, data) = CreateMarkdownChat(fillerTurns: 12);
        const string code = "items.Sum";
        var closedOnScroll = false;
        MessageReply? pending = null;

        await session.Dispatch(async () =>
        {
            await WithChatViewAsync(data, chat, async (vm, view, window) =>
            {
                var message = await WaitForAnswerAsync(view, answer.Id);
                var prose = message.GetVisualDescendants().OfType<SelectableTextBlock>()
                    .First(block => block.FindAncestorOfType<StrataCodeBlock>() is null
                        && (block.Inlines?.Text ?? block.Text)?.Contains("Keep each method", StringComparison.Ordinal) == true);
                var codeText = message.GetVisualDescendants().OfType<StrataCodeBlock>().Single()
                    .GetVisualDescendants().OfType<SelectableTextBlock>().Single();

                // A highlight the host left earlier in the message (e.g. from a jump) never takes focus.
                SelectText(prose, "Keep");
                var transcript = codeText.GetVisualAncestors().OfType<ScrollViewer>().Last();
                await WaitForScrollToSettleAsync(transcript);
                SelectText(codeText, code);
                await ReleaseAsync(window, codeText);
                var popup = SelectionReplyPopup(message);
                Assert.True(popup?.IsOpen, "Selecting code should float the Reply pill.");

                // The code sits in its own scroller; scrolling the transcript around it must still close the pill.
                var offset = transcript.Offset;
                transcript.Offset = offset.WithY(offset.Y >= 40 ? offset.Y - 40 : offset.Y + 40);
                await PumpAsync();
                closedOnScroll = popup!.IsOpen == false;

                SelectText(codeText, code);
                await ReleaseAsync(window, codeText);
                Press(Assert.IsType<Button>(popup.Child));
                await PumpAsync();
                pending = vm.PendingReply;
            });
        }, CancellationToken.None);

        Assert.True(closedOnScroll, "Scrolling the transcript should close the pill over code.");
        Assert.Equal(code, pending?.Quote);
    }

    [Fact]
    public async Task CopyAsMarkdown_OnAReply_CopiesJustTheMessageWithoutItsQuote()
    {
        using var session = HeadlessTestSession.Start();
        var (chat, answer, data) = CreateMarkdownChat(fillerTurns: 0);
        chat.Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = "Why **small**?",
            ReplyTo = new MessageReply { MessageId = answer.Id, Quote = "small and focused", IsSelection = true, Author = "Lumi" }
        });
        string? copied = null;

        await session.Dispatch(async () =>
        {
            await WithChatViewAsync(data, chat, async (_, view, _) =>
            {
                StrataChatMessage? bubble = null;
                for (var i = 0; i < 60 && bubble is null; i++)
                {
                    bubble = view.GetVisualDescendants().OfType<StrataChatMessage>()
                        .FirstOrDefault(message => message.DataContext is UserMessageItem { HasReply: true });
                    await PumpAsync();
                }

                // What Strata copies from the rendered bubble, quote block included.
                bubble!.RaiseEvent(new StrataCopyRequestedEventArgs(
                    StrataChatMessage.CopyRequestedEvent,
                    "Lumi\nsmall and focused\nWhy small?",
                    isSelection: false,
                    StrataCopyFormat.Markdown));
                await Task.Delay(50);
                await PumpAsync();
                copied = await TopLevel.GetTopLevel(bubble)!.Clipboard!.TryGetTextAsync();
            });
        }, CancellationToken.None);

        Assert.Equal("Why **small**?", copied);
    }

    private static (Chat Chat, ChatMessage Answer, AppData Data) CreateMarkdownChat(int fillerTurns)
    {
        var chat = new Chat { Title = "Markdown replies" };
        for (var i = 0; i < fillerTurns; i++)
        {
            chat.Messages.Add(new ChatMessage { Role = "user", Content = $"Question {i}" });
            chat.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Author = "Lumi",
                Content = $"Answer {i}.\n\nA second paragraph so the transcript needs to scroll."
            });
        }

        var answer = new ChatMessage { Role = "assistant", Author = "Lumi", Content = MarkdownAnswer };
        chat.Messages.AddRange([new ChatMessage { Role = "user", Content = "How do I keep code clean?" }, answer]);
        var data = new AppData
        {
            Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false }
        };
        data.Chats.Add(chat);
        return (chat, answer, data);
    }

    private static async Task WithChatViewAsync(
        AppData data,
        Chat chat,
        Func<ChatViewModel, ChatView, Window, Task> body)
    {
        Lumi.Localization.Loc.Load("en");
        var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
        var view = new ChatView { DataContext = vm };
        var window = new Window { Width = 1000, Height = 800, Content = view };
        window.Show();
        try
        {
            await PumpAsync();
            await vm.LoadChatAsync(chat);
            await body(vm, view, window);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    private static async Task<StrataChatMessage> WaitForAnswerAsync(Visual root, Guid answerId)
    {
        for (var i = 0; i < 60; i++)
        {
            var message = root.GetVisualDescendants()
                .OfType<StrataChatMessage>()
                .FirstOrDefault(candidate => candidate.DataContext is AssistantMessageItem item && item.MessageId == answerId);
            if (message is not null && message.GetVisualDescendants().OfType<SelectableTextBlock>().Any())
                return message;

            await PumpAsync();
        }

        throw new InvalidOperationException("The answer was never realized.");
    }

    /// <summary>
    /// A freshly opened transcript keeps adjusting (turns realize, deferred height work lands on
    /// timers), so wait until its extent and offset have been still for a stretch of real time.
    /// </summary>
    private static async Task WaitForScrollToSettleAsync(ScrollViewer scroller)
    {
        var stableTicks = 0;
        for (var i = 0; i < 120 && stableTicks < 16; i++)
        {
            var (offset, extent) = (scroller.Offset, scroller.Extent);
            await Task.Delay(25);
            await PumpAsync();
            stableTicks = scroller.Offset == offset && scroller.Extent == extent ? stableTicks + 1 : 0;
        }
    }

    /// <summary>Selects <paramref name="excerpt"/> in the text block's own selection index space.</summary>
    private static void SelectText(SelectableTextBlock text, string excerpt)
    {
        var length = (text.Inlines?.Text ?? text.Text ?? "").Length;
        for (var start = 0; start + excerpt.Length <= length; start++)
        {
            text.SelectionStart = start;
            text.SelectionEnd = start + excerpt.Length;
            if (text.SelectedText == excerpt)
                return;
        }

        throw new InvalidOperationException($"'{excerpt}' is not in the text.");
    }

    /// <summary>Finishes a mouse selection on <paramref name="text"/>, which is what floats the pill.</summary>
    private static async Task ReleaseAsync(Window window, SelectableTextBlock text)
    {
        var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        text.RaiseEvent(new PointerReleasedEventArgs(
            text, pointer, window, text.TranslatePoint(new Point(4, 4), window) ?? default, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None,
            MouseButton.Left));
        for (var i = 0; i < 5; i++)
            await PumpAsync();
    }

    private static Popup? SelectionReplyPopup(StrataChatMessage message)
        => typeof(StrataChatMessage)
            .GetField("_selectionReplyPopup", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(message) as Popup;

    /// <summary>
    /// Presses the pill with the left button, the way a user does. The pill acts on press so the
    /// selection it quotes is still intact when the click lands.
    /// </summary>
    private static void Press(Button pill)
    {
        var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        pill.RaiseEvent(new PointerPressedEventArgs(
            pill, pointer, pill, default, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
    }

    /// <summary>Drags the mouse across <paramref name="text"/> from one character index to another.</summary>
    private static void DragSelect(Window window, SelectableTextBlock text, int from, int to)
    {
        Point PointAt(int index)
        {
            var caret = text.TextLayout.HitTestTextPosition(index);
            return text.TranslatePoint(new Point(caret.X + 0.5, caret.Center.Y), window)!.Value;
        }

        var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        text.RaiseEvent(new PointerPressedEventArgs(
            text, pointer, window, PointAt(from), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
        text.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent, text, pointer, window, PointAt(to), 1,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None));
        text.RaiseEvent(new PointerReleasedEventArgs(
            text, pointer, window, PointAt(to), 2,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None,
            MouseButton.Left));
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
