using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Fork = an independent copy of a chat that carries the source's setup and transcript.
/// These tests pin the two properties that make a fork useful and safe: it reproduces enough
/// of the source to continue the conversation, and it shares no mutable state with it.
/// </summary>
public class ChatForkFactoryTests
{
    private static Chat CreateSourceChat() => new()
    {
        Title = "Ship the release",
        ProjectId = Guid.NewGuid(),
        AgentId = Guid.NewGuid(),
        ActiveSkillIds = [Guid.NewGuid()],
        ActiveExternalSkillNames = ["pdf"],
        ActiveMcpServerNames = ["lumi-mcp"],
        HasExplicitMcpServerSelection = true,
        SdkAgentName = "Coding Lumi",
        WorktreePath = @"E:\Git\Lumi-wt-demo",
        LastModelUsed = "claude-opus-5",
        LastReasoningEffortUsed = "high",
        LastContextWindowTierUsed = ModelContextWindowTiers.LongContext,
        PlanContent = "1. do the thing",
        CopilotSessionId = "session-abc",
        SessionProviderSignature = "provider-sig",
        IsPinned = true,
        FollowUpSuggestions = ["what next?"],
        FollowUpSuggestionAssistantMessageId = Guid.NewGuid()
    };

    private static List<ChatMessage> CreateMessages() =>
    [
        new() { Role = "user", Content = "hello" },
        new() { Role = "assistant", Content = "hi there" },
        new() { Role = "user", Content = "keep going" },
        new() { Role = "assistant", Content = "done" }
    ];

    [Fact]
    public void CreateFork_CopiesSetupSoTheForkIsImmediatelyUsable()
    {
        var source = CreateSourceChat();

        var fork = ChatForkFactory.CreateFork(source, CreateMessages()).Chat;

        Assert.Equal(source.ProjectId, fork.ProjectId);
        Assert.Equal(source.AgentId, fork.AgentId);
        Assert.Equal(source.ActiveSkillIds, fork.ActiveSkillIds);
        Assert.Equal(source.ActiveExternalSkillNames, fork.ActiveExternalSkillNames);
        Assert.Equal(source.ActiveMcpServerNames, fork.ActiveMcpServerNames);
        Assert.True(fork.HasExplicitMcpServerSelection);
        Assert.Equal(source.SdkAgentName, fork.SdkAgentName);
        Assert.Equal(source.WorktreePath, fork.WorktreePath);
        Assert.Equal(source.LastModelUsed, fork.LastModelUsed);
        Assert.Equal(source.LastReasoningEffortUsed, fork.LastReasoningEffortUsed);
        Assert.Equal(source.LastContextWindowTierUsed, fork.LastContextWindowTierUsed);
        Assert.Equal(source.PlanContent, fork.PlanContent);
    }

    [Fact]
    public void CreateFork_ResetsSessionAndPerChatState()
    {
        var source = CreateSourceChat();

        var fork = ChatForkFactory.CreateFork(source, CreateMessages()).Chat;

        Assert.NotEqual(source.Id, fork.Id);

        // A null session id is what makes the fork replay its transcript on first send.
        Assert.Null(fork.CopilotSessionId);
        Assert.Null(fork.SessionProviderSignature);

        Assert.False(fork.IsPinned);
        Assert.Empty(fork.FollowUpSuggestions);
        Assert.Null(fork.FollowUpSuggestionAssistantMessageId);
    }

    [Fact]
    public void CreateFork_RecordsBreadcrumbBackToSource()
    {
        var source = CreateSourceChat();

        var fork = ChatForkFactory.CreateFork(source, CreateMessages()).Chat;

        Assert.Equal(source.Id, fork.ForkedFromChatId);
        Assert.Equal("Ship the release", fork.ForkedFromTitle);
    }

    [Fact]
    public void CreateFork_CopiesWholeTranscriptWithFreshMessageIds()
    {
        var source = CreateSourceChat();
        var messages = CreateMessages();

        var fork = ChatForkFactory.CreateFork(source, messages).Chat;

        Assert.Equal(4, fork.Messages.Count);
        Assert.Equal(4, fork.MessageCount);
        Assert.Equal(
            messages.Select(m => m.Content),
            fork.Messages.Select(m => m.Content));

        var sourceIds = messages.Select(m => m.Id).ToHashSet();
        Assert.All(fork.Messages, m => Assert.DoesNotContain(m.Id, sourceIds));
    }

    [Fact]
    public void CreateFork_ThroughMessageId_CutsTranscriptInclusively()
    {
        var source = CreateSourceChat();
        var messages = CreateMessages();

        var fork = ChatForkFactory.CreateFork(source, messages, messages[1].Id).Chat;

        Assert.Equal(2, fork.Messages.Count);
        Assert.Equal(["hello", "hi there"], fork.Messages.Select(m => m.Content));
    }

    [Fact]
    public void CreateFork_ThroughAssistantMessage_KeepsItSoTheBranchEndsOnAnAnswer()
    {
        var source = CreateSourceChat();
        var messages = CreateMessages();

        var plan = ChatForkFactory.CreateFork(source, messages, messages[1].Id);

        Assert.Equal(["hello", "hi there"], plan.Chat.Messages.Select(m => m.Content));
        Assert.Null(plan.ComposerPrefill);
        Assert.Equal(1, plan.SessionForkCutUserTurns);
    }

    [Fact]
    public void CreateFork_ThroughUserMessage_CutsBeforeItAndReturnsItAsADraft()
    {
        var source = CreateSourceChat();
        var messages = CreateMessages();

        // Forking from "keep going" must not leave the branch dead-ending on an unanswered
        // question — it stops at the previous answer and hands the prompt back to the composer.
        var plan = ChatForkFactory.CreateFork(source, messages, messages[2].Id);

        Assert.Equal(["hello", "hi there"], plan.Chat.Messages.Select(m => m.Content));
        Assert.Equal("keep going", plan.ComposerPrefill);
        Assert.Equal(1, plan.SessionForkCutUserTurns);
    }

    [Fact]
    public void CreateFork_ThroughFirstUserMessage_ProducesAnEmptyBranchWithTheDraft()
    {
        var source = CreateSourceChat();
        var messages = CreateMessages();

        var plan = ChatForkFactory.CreateFork(source, messages, messages[0].Id);

        Assert.Empty(plan.Chat.Messages);
        Assert.Equal("hello", plan.ComposerPrefill);
        Assert.Equal(0, plan.SessionForkCutUserTurns);
    }

    [Fact]
    public void CreateFork_ThroughFinalAssistantMessage_StillRequestsSessionBoundaryValidation()
    {
        var source = CreateSourceChat();
        var messages = CreateMessages();

        var plan = ChatForkFactory.CreateFork(source, messages, messages[^1].Id);

        Assert.Equal(messages.Count, plan.Chat.Messages.Count);
        Assert.Equal(2, plan.SessionForkCutUserTurns);
    }

    [Fact]
    public void CreateFork_SessionCutIsOnlySkippedForWholeChatDuplicate()
    {
        var source = CreateSourceChat();
        var messages = CreateMessages();

        foreach (var anchor in messages)
        {
            var plan = ChatForkFactory.CreateFork(source, messages, anchor.Id);
            Assert.Equal(
                plan.Chat.Messages.Count(m => m.Role == "user"),
                plan.SessionForkCutUserTurns);
        }

        var whole = ChatForkFactory.CreateFork(source, messages);
        Assert.Null(whole.SessionForkCutUserTurns);
        Assert.Null(whole.ComposerPrefill);
    }

    [Fact]
    public void CreateFork_UnknownThroughMessageId_CopiesEverything()
    {
        var source = CreateSourceChat();
        var messages = CreateMessages();

        var fork = ChatForkFactory.CreateFork(source, messages, Guid.NewGuid()).Chat;

        Assert.Equal(messages.Count, fork.Messages.Count);
    }

    [Fact]
    public void CreateFork_DoesNotShareMutableStateWithSource()
    {
        var source = CreateSourceChat();
        var messages = CreateMessages();
        messages[1].Attachments.Add(@"C:\a.png");
        messages[1].ActiveSkills.Add(new SkillReference { Name = "pdf" });
        messages[1].Sources.Add(new SearchSource { Title = "docs", Url = "https://example.com" });

        var fork = ChatForkFactory.CreateFork(source, messages).Chat;

        fork.ActiveMcpServerNames.Add("extra-server");
        fork.Messages[1].Attachments.Add(@"C:\b.png");
        fork.Messages[1].ActiveSkills[0].Name = "changed";
        fork.Messages[1].Sources[0].Title = "changed";

        Assert.Single(source.ActiveMcpServerNames);
        Assert.Single(messages[1].Attachments);
        Assert.Equal("pdf", messages[1].ActiveSkills[0].Name);
        Assert.Equal("docs", messages[1].Sources[0].Title);
    }

    [Fact]
    public void CreateFork_NormalizesUnfinishedToolCallsToTerminalStatus()
    {
        var source = CreateSourceChat();
        List<ChatMessage> messages =
        [
            new() { Role = "tool", ToolName = "shell", ToolStatus = "InProgress" },
            new() { Role = "tool", ToolName = "read", ToolStatus = "Completed" },
            new() { Role = "tool", ToolName = "write", ToolStatus = "Failed" },
            new() { Role = "user", Content = "hi", ToolStatus = null }
        ];

        var fork = ChatForkFactory.CreateFork(source, messages).Chat;

        Assert.Equal("Stopped", fork.Messages[0].ToolStatus);
        Assert.Equal("Completed", fork.Messages[1].ToolStatus);
        Assert.Equal("Failed", fork.Messages[2].ToolStatus);
        Assert.Null(fork.Messages[3].ToolStatus);
    }

    [Fact]
    public void CreateFork_DropsEmptyStreamingMessageAndClearsStreamingFlag()
    {
        var source = CreateSourceChat();
        List<ChatMessage> messages =
        [
            new() { Role = "user", Content = "go" },
            new() { Role = "assistant", Content = "partial answer", IsStreaming = true },
            new() { Role = "assistant", Content = "   ", IsStreaming = true }
        ];

        var fork = ChatForkFactory.CreateFork(source, messages).Chat;

        Assert.Equal(2, fork.Messages.Count);
        Assert.All(fork.Messages, m => Assert.False(m.IsStreaming));
    }

    [Theory]
    [InlineData("Ship it", "Ship it (fork)")]
    [InlineData("Ship it (fork)", "Ship it (fork 2)")]
    [InlineData("Ship it (fork 2)", "Ship it (fork 3)")]
    [InlineData("Ship it (fork 9)", "Ship it (fork 10)")]
    [InlineData("", "Chat (fork)")]
    [InlineData("Notes (draft)", "Notes (draft) (fork)")]
    public void BuildForkTitle_ProducesDistinguishableSiblings(string source, string expected)
        => Assert.Equal(expected, ChatForkFactory.BuildForkTitle(source, "fork"));

    /// <summary>
    /// A whole-chat duplicate and a branch from one message are different actions to the user, so
    /// the title says which one made this chat — and each marker counts up independently.
    /// </summary>
    [Theory]
    [InlineData("Ship it", "Ship it (copy)")]
    [InlineData("Ship it (copy)", "Ship it (copy 2)")]
    [InlineData("Ship it (copy 9)", "Ship it (copy 10)")]
    [InlineData("Ship it (fork)", "Ship it (fork) (copy)")]
    public void BuildForkTitle_CopyMarker_CountsIndependentlyOfForks(string source, string expected)
        => Assert.Equal(expected, ChatForkFactory.BuildForkTitle(source, "copy"));

    /// <summary>Duplicating a whole chat is a "copy"; forking through a message is a "fork".</summary>
    [Fact]
    public void CreateFork_TitleMarkerReflectsWhichActionWasUsed()
    {
        var source = new Chat { Title = "Original" };
        var user = new ChatMessage { Role = "user", Content = "q" };
        var answer = new ChatMessage { Role = "assistant", Content = "a" };
        source.Messages.Add(user);
        source.Messages.Add(answer);

        Assert.Equal("Original (copy)", ChatForkFactory.CreateFork(source, source.Messages).Chat.Title);
        Assert.Equal("Original (fork)", ChatForkFactory.CreateFork(source, source.Messages, answer.Id).Chat.Title);
    }

    /// <summary>
    /// The breadcrumb has to name the action the user actually took, so the chat records whether it
    /// branched from a message or copied the whole chat.
    /// </summary>
    [Fact]
    public void CreateFork_RecordsWhetherItBranchedFromAMessage()
    {
        var source = new Chat { Title = "Original" };
        var user = new ChatMessage { Role = "user", Content = "q" };
        var answer = new ChatMessage { Role = "assistant", Content = "a" };
        source.Messages.Add(user);
        source.Messages.Add(answer);

        Assert.False(ChatForkFactory.CreateFork(source, source.Messages).Chat.ForkedFromMessage);
        Assert.True(ChatForkFactory.CreateFork(source, source.Messages, answer.Id).Chat.ForkedFromMessage);
    }
}

/// <summary>
/// MainViewModel-level fork behaviour: persistence, list refresh, and the worktree-safety rule
/// that forking introduces (several chats can now legitimately share one worktree).
/// </summary>
[Collection("Headless UI")]
public class MainViewModelForkTests
{
    private static DataStore CreateDataStore(params Chat[] chats)
    {
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            }
        };
        foreach (var c in chats)
            data.Chats.Add(c);
        return new DataStore(data);
    }

    [Fact]
    public async Task ForkChatAsync_AddsForkAndLeavesSourceUntouched()
    {
        using var session = HeadlessTestSession.Start();

        var source = new Chat { Title = "Original", CopilotSessionId = "session-1" };
        source.Messages.Add(new ChatMessage { Role = "user", Content = "hello" });
        source.Messages.Add(new ChatMessage { Role = "assistant", Content = "hi" });
        source.MessageCount = 2;

        var ds = CreateDataStore(source);
        Chat? fork = null;

        // Assertions inside a dispatched body are swallowed by the headless harness, so the
        // result is captured here and verified on the test thread.
        await session.Dispatch(async () =>
        {
            var vm = new MainViewModel(ds, TestCopilot.Shared, new UpdateService());
            fork = await vm.ForkChatAsync(source);
        }, CancellationToken.None);

        Assert.NotNull(fork);
        Assert.Contains(fork, ds.Data.Chats);
        Assert.Equal("Original (copy)", fork!.Title);
        Assert.Equal(2, fork.Messages.Count);
        Assert.Null(fork.CopilotSessionId);
        Assert.Equal(source.Id, fork.ForkedFromChatId);

        // The source must be completely unaffected — that is the point of a fork.
        Assert.Equal("Original", source.Title);
        Assert.Equal("session-1", source.CopilotSessionId);
        Assert.Equal(2, source.Messages.Count);
    }

    [Fact]
    public async Task ForkChatAsync_ForkFromMessage_TruncatesTranscript()
    {
        using var session = HeadlessTestSession.Start();

        var source = new Chat { Title = "Original" };
        var first = new ChatMessage { Role = "user", Content = "one" };
        var second = new ChatMessage { Role = "assistant", Content = "two" };
        source.Messages.Add(first);
        source.Messages.Add(second);
        source.Messages.Add(new ChatMessage { Role = "user", Content = "three" });
        source.MessageCount = 3;

        var ds = CreateDataStore(source);
        Chat? fork = null;

        await session.Dispatch(async () =>
        {
            var vm = new MainViewModel(ds, TestCopilot.Shared, new UpdateService());
            fork = await vm.ForkChatAsync(source, second.Id);
        }, CancellationToken.None);

        Assert.NotNull(fork);
        Assert.Equal(["one", "two"], fork!.Messages.Select(m => m.Content));
    }

    /// <summary>
    /// The bug this fixes: forking from a user turn used to copy that turn, leaving the branch
    /// dead-ending on an unanswered question while the server-side fork silently kept the answer
    /// to it. The branch must instead stop at the previous answer and hand the prompt to the
    /// composer as an editable draft.
    /// </summary>
    [Fact]
    public async Task ForkChatAsync_ForkFromUserMessage_EndsOnAnswerAndDraftsThePrompt()
    {
        using var session = HeadlessTestSession.Start();

        var source = new Chat { Title = "Original" };
        source.Messages.Add(new ChatMessage { Role = "user", Content = "one" });
        source.Messages.Add(new ChatMessage { Role = "assistant", Content = "two" });
        var third = new ChatMessage { Role = "user", Content = "three" };
        source.Messages.Add(third);
        source.Messages.Add(new ChatMessage { Role = "assistant", Content = "four" });
        source.MessageCount = 4;

        var ds = CreateDataStore(source);
        Chat? fork = null;
        string? draft = null;

        await session.Dispatch(async () =>
        {
            var vm = new MainViewModel(ds, TestCopilot.Shared, new UpdateService());
            fork = await vm.ForkChatAsync(source, third.Id);
            draft = vm.ChatVM.PromptText;
        }, CancellationToken.None);

        Assert.NotNull(fork);
        Assert.Equal(["one", "two"], fork!.Messages.Select(m => m.Content));
        Assert.Equal("assistant", fork.Messages[^1].Role);
        Assert.Equal("three", draft);

        // The source keeps all four messages — forking never edits the original.
        Assert.Equal(4, source.Messages.Count);
    }

    [Fact]
    public async Task ForkChatAsync_ForkFromAssistantMessage_LeavesComposerEmpty()
    {
        using var session = HeadlessTestSession.Start();

        var source = new Chat { Title = "Original" };
        source.Messages.Add(new ChatMessage { Role = "user", Content = "one" });
        var second = new ChatMessage { Role = "assistant", Content = "two" };
        source.Messages.Add(second);
        source.Messages.Add(new ChatMessage { Role = "user", Content = "three" });
        source.MessageCount = 3;

        var ds = CreateDataStore(source);
        string? draft = null;

        await session.Dispatch(async () =>
        {
            var vm = new MainViewModel(ds, TestCopilot.Shared, new UpdateService());
            await vm.ForkChatAsync(source, second.Id);
            draft = vm.ChatVM.PromptText;
        }, CancellationToken.None);

        // Assistant anchors carry no draft: the branch is ready for a fresh follow-up.
        Assert.True(string.IsNullOrEmpty(draft));
    }

    [Fact]
    public async Task ForkChatAsync_ClearsBusyStateAfterDuplicating()
    {
        using var session = HeadlessTestSession.Start();

        var source = new Chat { Title = "Original" };
        source.Messages.Add(new ChatMessage { Role = "user", Content = "hello" });
        source.MessageCount = 1;

        var ds = CreateDataStore(source);
        var stillBusy = true;
        var stillLoading = true;

        await session.Dispatch(async () =>
        {
            var vm = new MainViewModel(ds, TestCopilot.Shared, new UpdateService());
            await vm.ForkChatAsync(source);
            stillBusy = vm.IsDuplicatingChat;
            stillLoading = vm.ChatVM.IsLoadingChat;
        }, CancellationToken.None);

        // A stuck busy state would leave the pill on screen or the chat surface permanently blank.
        Assert.False(stillBusy);
        Assert.False(stillLoading);
    }

    [Fact]
    public async Task ForkChatAsync_OverlappingRequests_ProduceOnlyOneDuplicate()
    {
        using var session = HeadlessTestSession.Start();

        var source = new Chat { Title = "Original" };
        source.Messages.Add(new ChatMessage { Role = "user", Content = "hello" });
        source.MessageCount = 1;

        var ds = CreateDataStore(source);
        Chat? first = null;
        Chat? second = null;

        await session.Dispatch(async () =>
        {
            var vm = new MainViewModel(ds, TestCopilot.Shared, new UpdateService());

            // Not awaited between the calls: this is exactly what key auto-repeat on Ctrl+Shift+D
            // does, and MainWindow bypasses AsyncRelayCommand's CanExecute guard entirely.
            var a = vm.ForkChatAsync(source);
            var b = vm.ForkChatAsync(source);
            first = await a;
            second = await b;
        }, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(2, ds.Data.Chats.Count);
    }

    [Fact]
    public async Task ForkChatAsync_NullChat_IsNoOp()
    {
        using var session = HeadlessTestSession.Start();

        var ds = CreateDataStore();
        Chat? fork = null;

        await session.Dispatch(async () =>
        {
            var vm = new MainViewModel(ds, TestCopilot.Shared, new UpdateService());
            fork = await vm.ForkChatAsync(null);
        }, CancellationToken.None);

        Assert.Null(fork);
        Assert.Empty(ds.Data.Chats);
    }

    /// <summary>
    /// A fork prefers a real server-side Copilot session fork, but that is only possible for the
    /// chat currently open in the ChatViewModel (its session must be live). Forking any other chat
    /// must fall back to the replay path — a null session id — rather than hijacking or sharing the
    /// open chat's session.
    /// </summary>
    [Fact]
    public async Task ForkChatAsync_SourceIsNotTheOpenChat_FallsBackToReplayShape()
    {
        using var session = HeadlessTestSession.Start();

        var source = new Chat
        {
            Title = "Original",
            CopilotSessionId = "server-session-1",
            SessionProviderSignature = "provider-sig"
        };
        source.Messages.Add(new ChatMessage { Role = "user", Content = "hello" });
        source.MessageCount = 1;

        var ds = CreateDataStore(source);
        Chat? fork = null;

        await session.Dispatch(async () =>
        {
            var vm = new MainViewModel(ds, TestCopilot.Shared, new UpdateService());
            fork = await vm.ForkChatAsync(source);
        }, CancellationToken.None);

        Assert.NotNull(fork);

        // Null session id + retained messages is exactly the shape that triggers transcript replay
        // on the fork's first send, and it proves the source's session was never shared.
        Assert.Null(fork!.CopilotSessionId);
        Assert.Null(fork.SessionProviderSignature);
        Assert.NotEmpty(fork.Messages);
        Assert.Equal("server-session-1", source.CopilotSessionId);
    }

    [Fact]
    public async Task ForkChatAsync_ForkSharesSourceWorktree_WhichIsThenProtectedFromDeletion()
    {
        using var session = HeadlessTestSession.Start();

        var source = new Chat { Title = "Original", WorktreePath = @"E:\Git\Lumi-wt-demo" };
        var ds = CreateDataStore(source);
        Chat? fork = null;
        bool sourceShared = false, forkShared = false, sharedAfterForkRemoved = true;

        await session.Dispatch(async () =>
        {
            var vm = new MainViewModel(ds, TestCopilot.Shared, new UpdateService());
            fork = await vm.ForkChatAsync(source);

            sourceShared = vm.IsWorktreeSharedWithOtherChats(source);
            forkShared = fork is not null && vm.IsWorktreeSharedWithOtherChats(fork);

            if (fork is not null)
                ds.Data.Chats.Remove(fork);
            sharedAfterForkRemoved = vm.IsWorktreeSharedWithOtherChats(source);
        }, CancellationToken.None);

        Assert.NotNull(fork);
        Assert.Equal(source.WorktreePath, fork!.WorktreePath);

        // Deleting either branch must not offer to remove the shared worktree directory.
        Assert.True(sourceShared);
        Assert.True(forkShared);
        Assert.False(sharedAfterForkRemoved);
    }
}

/// <summary>
/// The "Fork from here" path can only be exercised through the real view: Strata raises a routed
/// event from the message's context menu, and ChatView must translate that back into the id of
/// the underlying <see cref="ChatMessage"/>. That translation is what decides where the branch is
/// cut, so it is verified against a live transcript rather than by inspection.
/// </summary>
[Collection("Headless UI")]
public sealed class ChatViewForkWiringTests
{
    [Fact]
    public async Task ForkRequestedFromMessage_ReportsThatMessagesId()
    {
        using var session = HeadlessTestSession.Start();

        var chat = new Chat { Title = "Forkable" };
        var firstUser = new ChatMessage { Role = "user", Content = "first question" };
        var firstAnswer = new ChatMessage { Role = "assistant", Content = "first answer" };
        var secondUser = new ChatMessage { Role = "user", Content = "second question" };
        chat.Messages.AddRange([firstUser, firstAnswer, secondUser]);

        var data = new AppData
        {
            Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false }
        };
        data.Chats.Add(chat);

        var requested = new List<(Guid ChatId, Guid MessageId)>();
        var forkableRoles = new List<string>();

        await session.Dispatch(async () =>
        {
            var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            vm.ForkChatRequested += (c, messageId) => requested.Add((c.Id, messageId));

            var view = new ChatView { DataContext = vm };
            var window = new Window { Width = 1000, Height = 800, Content = view };
            window.Show();
            try
            {
                await PumpAsync();
                await vm.LoadChatAsync(chat);

                for (var i = 0; i < 40 && CollectForkableMessages(view).Count < 3; i++)
                    await PumpAsync();

                var messages = CollectForkableMessages(view);
                forkableRoles.AddRange(messages.Select(m => m.Role.ToString()));

                foreach (var message in messages)
                    message.RaiseEvent(new RoutedEventArgs(StrataChatMessage.ForkRequestedEvent, message));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

        // Both user and assistant messages offer the fork action.
        Assert.Equal(3, forkableRoles.Count);
        Assert.Contains("User", forkableRoles);
        Assert.Contains("Assistant", forkableRoles);

        // Each raised event resolved to the id of the message it came from — in transcript order.
        Assert.Equal(
            [firstUser.Id, firstAnswer.Id, secondUser.Id],
            requested.Select(r => r.MessageId));
        Assert.All(requested, r => Assert.Equal(chat.Id, r.ChatId));
    }

    private static List<StrataChatMessage> CollectForkableMessages(Visual root)
        => root.GetVisualDescendants()
            .OfType<StrataChatMessage>()
            .Where(m => m.CanFork)
            .ToList();

    [Fact]
    public void ForkIconGeometry_IsRenderable()
    {
        // The icon fonts have no fork/branch glyph, so the fork affordances draw a vector path.
        // A malformed path would parse to empty bounds and render as a blank menu icon.
        var bounds = StrataChatMessage.ForkIconGeometry.Bounds;

        Assert.True(bounds.Width > 0 && bounds.Height > 0, $"Fork icon geometry is empty: {bounds}.");
        Assert.True(bounds.Right <= 24 && bounds.Bottom <= 24, $"Fork icon must fit a 24x24 box: {bounds}.");
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
