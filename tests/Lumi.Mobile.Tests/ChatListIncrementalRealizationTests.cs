using System.Runtime.ExceptionServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class ChatListIncrementalRealizationTests
{
    [Fact]
    public void Apply_LargeRealStyleHistory_RealizesOnlyTheInitialPageInServerOrder()
    {
        var source = LargeChatHistory.Create();
        var list = new ChatListViewModel(new NoOpSink());

        list.Apply(source);

        Assert.Equal(LargeChatHistory.ChatCount, list.TotalChats);
        Assert.Equal(LargeChatHistory.ChatCount, list.MatchingChatCount);
        Assert.Equal(ChatListViewModel.InitialVisibleChatLimit, list.VisibleChatCount);
        Assert.True(list.HasMoreChats);

        var expected = LargeChatHistory.Flatten(source)
            .Take(ChatListViewModel.InitialVisibleChatLimit)
            .Select(chat => chat.Id);
        var visible = VisibleChats(list);

        Assert.Equal(expected, visible.Select(chat => chat.Id));
        Assert.Equal(visible.Count, visible.Select(chat => chat.Id).Distinct().Count());
        Assert.Equal(["Pinned", "Today"], list.Groups.Select(group => group.Label));
    }

    [Fact]
    public void Search_FindsAnExactOffPageChatWithoutRealizingInterveningRows()
    {
        var list = new ChatListViewModel(new NoOpSink());
        list.Apply(LargeChatHistory.Create());
        list.SelectedChatId = LargeChatHistory.IdFor(1700);

        list.SearchText = "Chat #1700";

        var result = Assert.Single(VisibleChats(list));
        Assert.Equal(LargeChatHistory.IdFor(1700), result.Id);
        Assert.Equal("Chat #1700", result.Title);
        Assert.True(result.IsSelected);
        Assert.Equal(1, list.MatchingChatCount);
        Assert.Equal(1, list.VisibleChatCount);
        Assert.False(list.HasMoreChats);
    }

    [Fact]
    public void LoadMore_AddsFixedPagesWithoutDuplicatesOrSkippedChats()
    {
        var source = LargeChatHistory.Create();
        var expected = LargeChatHistory.Flatten(source).Select(chat => chat.Id).ToList();
        var list = new ChatListViewModel(new NoOpSink());
        list.Apply(source);

        var page = 1;
        while (true)
        {
            var expectedCount = Math.Min(
                page * ChatListViewModel.ChatPageSize,
                LargeChatHistory.ChatCount);
            var visible = VisibleChats(list);

            Assert.Equal(expectedCount, list.VisibleChatCount);
            Assert.Equal(expected.Take(expectedCount), visible.Select(chat => chat.Id));
            Assert.Equal(visible.Count, visible.Select(chat => chat.Id).Distinct().Count());

            if (!list.HasMoreChats)
                break;

            list.LoadMoreChatsCommand.Execute(null);
            page++;
        }

        Assert.Equal(LargeChatHistory.ChatCount, list.VisibleChatCount);
        Assert.False(list.HasMoreChats);

        list.LoadMoreChatsCommand.Execute(null);
        Assert.Equal(LargeChatHistory.ChatCount, list.VisibleChatCount);
    }

    [Fact]
    public void ProjectFilterAndSnapshotUpdates_ResetPagingAndPreserveRowStateInPlace()
    {
        var source = LargeChatHistory.Create();
        var list = new ChatListViewModel(new NoOpSink());
        list.Apply(source);
        list.LoadMoreChatsCommand.Execute(null);
        list.LoadMoreChatsCommand.Execute(null);
        Assert.Equal(ChatListViewModel.InitialVisibleChatLimit + (2 * ChatListViewModel.ChatPageSize),
            list.VisibleChatCount);

        list.ProjectFilterId = LargeChatHistory.ApolloProjectId;

        var expectedApollo = LargeChatHistory.Flatten(source)
            .Where(chat => chat.ProjectName == "Apollo")
            .ToList();
        Assert.Equal(expectedApollo.Count, list.MatchingChatCount);
        Assert.Equal(ChatListViewModel.InitialVisibleChatLimit, list.VisibleChatCount);
        Assert.Equal(
            expectedApollo.Take(ChatListViewModel.InitialVisibleChatLimit).Select(chat => chat.Id),
            VisibleChats(list).Select(chat => chat.Id));

        var targetSource = expectedApollo[20];
        list.SelectedChatId = targetSource.Id;
        var target = VisibleChats(list).Single(chat => chat.Id == targetSource.Id);
        Assert.True(target.IsSelected);
        Assert.Single(VisibleChats(list), chat => chat.IsSelected);

        var updatedSource = LargeChatHistory.Create();
        var updatedTarget = LargeChatHistory.Flatten(updatedSource)
            .Single(chat => chat.Id == targetSource.Id);
        updatedTarget.Title = "Updated Apollo chat";
        updatedTarget.Preview = "Fresh preview";
        updatedTarget.IsRunning = true;
        updatedTarget.HasUnreadMessages = true;
        updatedTarget.AgentName = "Coding Lumi";
        updatedTarget.AgentGlyph = "\u26A1";

        list.Apply(updatedSource);

        var reconciled = VisibleChats(list).Single(chat => chat.Id == targetSource.Id);
        Assert.Same(target, reconciled);
        Assert.Equal("Updated Apollo chat", reconciled.Title);
        Assert.Equal("Fresh preview", reconciled.Preview);
        Assert.True(reconciled.IsRunning);
        Assert.True(reconciled.HasUnreadMessages);
        Assert.True(reconciled.HasProject);
        Assert.True(reconciled.HasAgent);
        Assert.True(reconciled.IsSelected);

        list.ProjectFilterId = Guid.NewGuid();
        Assert.True(list.IsEmpty);
        Assert.Equal(0, list.MatchingChatCount);
        Assert.Equal(0, list.VisibleChatCount);

        list.ProjectFilterId = null;
        Assert.Equal(LargeChatHistory.ChatCount, list.MatchingChatCount);
        Assert.Equal(ChatListViewModel.InitialVisibleChatLimit, list.VisibleChatCount);
    }

    [Fact]
    public async Task DelayedLoadMoreCannotMergeIntoANewSearchContext()
    {
        var sink = new DelayedPageSink();
        using var list = new ChatListViewModel(sink);
        list.Apply(new RemoteChatPage
        {
            TotalCount = 2,
            HasMore = true,
            Groups =
            [
                new RemoteChatGroup
                {
                    Label = "Today",
                    Chats = [new RemoteChat { Id = Guid.NewGuid(), Title = "Initial" }]
                }
            ]
        });

        var loadMore = list.LoadMoreChatsCommand.ExecuteAsync(null);
        await sink.LoadMoreStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        list.SearchText = "fresh";
        sink.CompleteLoadMore(new RemoteChatPage
        {
            Offset = 1,
            TotalCount = 2,
            Query = null,
            Groups =
            [
                new RemoteChatGroup
                {
                    Label = "Today",
                    Chats = [new RemoteChat { Id = Guid.NewGuid(), Title = "Stale page" }]
                }
            ]
        });
        await loadMore;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline
               && !VisibleChats(list).Any(chat => chat.Title == "Fresh result"))
        {
            await Task.Delay(10);
        }

        Assert.Equal(["Fresh result"], VisibleChats(list).Select(chat => chat.Title));
    }

    private static List<ChatListItemViewModel> VisibleChats(ChatListViewModel list) =>
        [.. list.Groups.SelectMany(group => group.Chats)];

    private sealed class NoOpSink : IRemoteCommandSink
    {
        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command) =>
            Task.FromResult(new RemoteCommandResult { Ok = true });

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse { FileName = fileName, Path = fileName });
    }

    private sealed class DelayedPageSink : IRemoteCommandSink, IRemoteChatPageSink
    {
        private readonly TaskCompletionSource<RemoteChatPage?> _loadMore =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public TaskCompletionSource LoadMoreStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command) =>
            Task.FromResult(new RemoteCommandResult { Ok = true });

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse { Ok = true });

        public Task<RemoteChatPage?> GetChatPageAsync(
            int offset,
            int limit,
            string? query,
            Guid? projectId,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                LoadMoreStarted.TrySetResult();
                return _loadMore.Task;
            }

            return Task.FromResult<RemoteChatPage?>(new RemoteChatPage
            {
                Offset = 0,
                TotalCount = 1,
                Query = query,
                ProjectId = projectId,
                Groups =
                [
                    new RemoteChatGroup
                    {
                        Label = "Search",
                        Chats = [new RemoteChat { Id = Guid.NewGuid(), Title = "Fresh result" }]
                    }
                ]
            });
        }

        public void CompleteLoadMore(RemoteChatPage page) => _loadMore.TrySetResult(page);
    }
}

[Collection("Headless mobile UI")]
public sealed class ChatListIncrementalRealizationRenderTests
{
    [Fact]
    public Task Drawer_RealizesOnlyTheBoundedVisibleChatRows() =>
        Render(
            shell => new MobileDrawerView { DataContext = shell },
            "DrawerLoadMoreButton");

    [Fact]
    public Task Search_RealizesOnlyTheBoundedVisibleChatRows() =>
        Render(
            shell => new MobileSearchView { DataContext = shell },
            "SearchLoadMoreButton");

    private static async Task Render(
        Func<MobileShellViewModel, Control> buildContent,
        string loadMoreButtonName)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;

        await session.Dispatch(async () =>
        {
            MobileShellViewModel? shell = null;
            Window? window = null;
            try
            {
                shell = new MobileShellViewModel(store: session.NewStore(), post: action => action());
                shell.ChatList.Apply(LargeChatHistory.Create());

                window = new Window
                {
                    Width = 412,
                    Height = 892,
                    Content = buildContent(shell)
                };
                window.Show();
                window.InvalidateMeasure();
                Dispatcher.UIThread.RunJobs();

                var realizedRows = window.GetVisualDescendants()
                    .OfType<Button>()
                    .Where(button => button.DataContext is ChatListItemViewModel)
                    .ToList();
                Assert.Equal(ChatListViewModel.InitialVisibleChatLimit, shell.ChatList.VisibleChatCount);
                Assert.Equal(shell.ChatList.VisibleChatCount, realizedRows.Count);
                Assert.True(realizedRows.Count < LargeChatHistory.ChatCount);

                var loadMore = window.GetVisualDescendants()
                    .OfType<Button>()
                    .Single(button => button.Name == loadMoreButtonName);
                Assert.True(loadMore.IsEffectivelyVisible);
                Assert.Same(shell.ChatList.LoadMoreChatsCommand, loadMore.Command);
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
}

internal static class LargeChatHistory
{
    public const int ChatCount = 1751;
    public static readonly Guid ApolloProjectId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid LumiProjectId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    public static readonly Guid ArchiveProjectId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static readonly (string Label, int Count)[] Groups =
    [
        ("Pinned", 24),
        ("Today", 176),
        ("Yesterday", 250),
        ("Previous 7 days", 401),
        ("Older", 900)
    ];

    public static List<RemoteChatGroup> Create()
    {
        var index = 1;
        var groups = new List<RemoteChatGroup>(Groups.Length);

        foreach (var (label, count) in Groups)
        {
            var chats = new List<RemoteChat>(count);
            for (var i = 0; i < count; i++, index++)
            {
                chats.Add(new RemoteChat
                {
                    Id = IdFor(index),
                    Title = $"Chat #{index}",
                    Preview = $"Preview for chat {index}",
                    ProjectId = (index % 4) switch
                    {
                        0 => ApolloProjectId,
                        1 => LumiProjectId,
                        2 => ArchiveProjectId,
                        _ => null
                    },
                    ProjectName = (index % 4) switch
                    {
                        0 => "Apollo",
                        1 => "Lumi",
                        2 => "Archive",
                        _ => null
                    },
                    AgentName = index % 10 == 0 ? "Coding Lumi" : null,
                    AgentGlyph = index % 10 == 0 ? "\u26A1" : null,
                    MessageCount = index % 31,
                    UpdatedAt = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero)
                        .AddMinutes(-index),
                    IsPinned = label == "Pinned",
                    IsRunning = index % 97 == 0,
                    HasUnreadMessages = index % 11 == 0,
                    LastModelUsed = index % 2 == 0 ? "claude-opus-5" : "gpt-5.6-sol"
                });
            }

            groups.Add(new RemoteChatGroup { Label = label, Chats = chats });
        }

        return groups;
    }

    public static List<RemoteChat> Flatten(IEnumerable<RemoteChatGroup> groups) =>
        [.. groups.SelectMany(group => group.Chats)];

    public static Guid IdFor(int index) =>
        Guid.Parse($"00000000-0000-0000-0000-{index:000000000000}");
}
