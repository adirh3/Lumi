using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

/// <summary>
/// The chat list never reshuffles while it is on screen: live pushes update rows where they are,
/// and the desktop's recency order returns when the list is loaded again.
/// </summary>
public sealed class ChatListStableOrderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    [Fact]
    public void LiveUpdatesChangeRowsWhereTheyAre()
    {
        var (a, b, c) = (Row("A", -1), Row("B", -2), Row("C", -3));
        var list = new ChatListViewModel(new PageSink());
        list.Apply(Page(Group("Today", a, b, c)));
        var rows = Rows(list);

        // An agent in C finishes a step and B gets a reply: both now have the newest activity.
        list.ApplyLive(Patch(Group("Today", Updated(c, minutes: 0, running: true, preview: "Working"))));
        list.ApplyLive(Patch(Group("Today", Updated(b, minutes: 1, unread: true))));

        Assert.Equal(["A", "B", "C"], Rows(list).Select(row => row.Title));
        Assert.Equal(rows, Rows(list));
        Assert.True(rows[2].IsRunning);
        Assert.Equal("Working", rows[2].Preview);
        Assert.True(rows[1].HasUnreadMessages);
    }

    [Fact]
    public void NewChatsAppearAtTheTopAndPinningOrDeletingStillMovesRows()
    {
        var (a, b) = (Row("A", -1), Row("B", -2));
        var list = new ChatListViewModel(new PageSink());
        list.Apply(Page(Group("Today", a, b)));

        list.ApplyLive(Patch(Group("Today", Row("D", 1), Row("E", 0))));
        Assert.Equal(["D", "E", "A", "B"], Rows(list).Select(row => row.Title));

        list.ApplyLive(Patch(Group("Pinned", Updated(b, minutes: -2, pinned: true))));
        Assert.Equal(["Pinned", "Today"], list.Groups.Select(group => group.Label));
        Assert.Equal(["B", "D", "E", "A"], Rows(list).Select(row => row.Title));

        var page = Patch();
        page.RemovedChatIds = [a.Id];
        list.ApplyLive(page);
        Assert.Equal(["B", "D", "E"], Rows(list).Select(row => row.Title));
    }

    [Fact]
    public async Task LoadingTheListAgainRestoresTheDesktopsOrder()
    {
        var (a, b, c) = (Row("A", -1), Row("B", -2), Row("C", -3));
        var sink = new PageSink();
        var list = new ChatListViewModel(sink);
        list.Apply(Page(Group("Today", a, b, c)));
        var newest = Updated(c, minutes: 0);
        list.ApplyLive(Patch(Group("Today", newest)));
        Assert.Equal(["A", "B", "C"], Rows(list).Select(row => row.Title));

        sink.Next = Page(Group("Today", newest, a, b));
        await list.RefreshFromServerAsync();

        Assert.Equal(["C", "A", "B"], Rows(list).Select(row => row.Title));
    }

    [Fact]
    public async Task AFilteredListUpdatesTheRowsItShowsWithoutLoadingAgain()
    {
        var projectId = Guid.NewGuid();
        var (p1, p2) = (Row("P1", -1, projectId), Row("P2", -2, projectId));
        var sink = new PageSink { Next = Page(Group("Today", p1, p2)) };
        var list = new ChatListViewModel(sink);
        list.ProjectFilterId = projectId;
        await WaitUntilAsync(() => Rows(list).Count == 2);
        var requests = sink.Requests;

        list.ApplyLive(Patch(Group(
            "Today",
            Row("Another project", 1, Guid.NewGuid()),
            Row("P3", 0, projectId),
            Updated(p2, minutes: 0, running: true))));

        Assert.Equal(requests, sink.Requests);
        Assert.Equal(["P1", "P2"], Rows(list).Select(row => row.Title));
        Assert.True(Rows(list)[1].IsRunning);
    }

    [Fact]
    public async Task AFilteredListLoadsAgainWhenOneOfItsChatsLeavesIt()
    {
        var projectId = Guid.NewGuid();
        var (p1, p2) = (Row("P1", -1, projectId), Row("P2", -2, projectId));
        var sink = new PageSink { Next = Page(Group("Today", p1, p2)) };
        var list = new ChatListViewModel(sink);
        list.ProjectFilterId = projectId;
        await WaitUntilAsync(() => Rows(list).Count == 2);

        // P2 is moved to another project on the PC: the list asks for its chats again.
        sink.Next = Page(Group("Today", p1));
        var moved = Updated(p2, minutes: 0);
        moved.ProjectId = Guid.NewGuid();
        list.ApplyLive(Patch(Group("Today", moved)));
        await WaitUntilAsync(() => sink.Requests == 2 && !list.IsRefreshing);
        Assert.Equal(["P1"], Rows(list).Select(row => row.Title));
        Assert.Equal(1, list.MatchingChatCount);

        // P1 is deleted: the list is empty and says so.
        sink.Next = Page();
        var deleted = Patch();
        deleted.RemovedChatIds = [p1.Id];
        list.ApplyLive(deleted);
        await WaitUntilAsync(() => sink.Requests == 3 && !list.IsRefreshing);
        Assert.True(list.IsEmpty);
        Assert.True(list.ShowEmptyState);
    }

    private static List<ChatListItemViewModel> Rows(ChatListViewModel list) =>
        list.Groups.SelectMany(group => group.Chats).ToList();

    private static RemoteChat Row(string title, int minutes, Guid? projectId = null) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        ProjectId = projectId,
        UpdatedAt = Now.AddMinutes(minutes)
    };

    private static RemoteChat Updated(
        RemoteChat row,
        int minutes,
        bool running = false,
        bool unread = false,
        bool pinned = false,
        string? preview = null) => new()
    {
        Id = row.Id,
        Title = row.Title,
        ProjectId = row.ProjectId,
        UpdatedAt = Now.AddMinutes(minutes),
        IsRunning = running,
        IsSessionActive = running,
        HasUnreadMessages = unread,
        IsPinned = pinned,
        Preview = preview ?? row.Preview
    };

    private static RemoteChatGroup Group(string label, params RemoteChat[] rows) =>
        new() { Label = label, Chats = [.. rows] };

    private static RemoteChatPage Page(params RemoteChatGroup[] groups) => new()
    {
        TotalCount = groups.Sum(group => group.Chats.Count),
        Groups = [.. groups]
    };

    private static RemoteChatPage Patch(params RemoteChatGroup[] groups)
    {
        var page = Page(groups);
        page.IsPatch = true;
        return page;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The chat list did not reach the expected state.");
            await Task.Delay(5);
        }
    }

    private sealed class PageSink : IRemoteCommandSink, IRemoteChatPageSink
    {
        private int _requests;

        public RemoteChatPage? Next { get; set; }

        public int Requests => Volatile.Read(ref _requests);

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
            Interlocked.Increment(ref _requests);
            var page = Next ?? new RemoteChatPage();
            page.Query = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
            page.ProjectId = projectId;
            return Task.FromResult<RemoteChatPage?>(page);
        }
    }
}
