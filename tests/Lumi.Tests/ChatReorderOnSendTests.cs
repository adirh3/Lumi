using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Regression: sending a message to an existing chat must move it
/// to the top of the sidebar (most-recent first).
/// Bug: ChatUpdated only fired for *new* chats, so existing chats
/// kept their old position after a message was sent.
/// </summary>
[Collection("Headless UI")]
public class ChatReorderOnSendTests
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
    public void RefreshChatList_OrdersByChatUpdatedAtDescending()
    {
        var older = new Chat { Title = "Older", UpdatedAt = DateTimeOffset.Now.AddHours(-2) };
        var newer = new Chat { Title = "Newer", UpdatedAt = DateTimeOffset.Now.AddMinutes(-5) };
        var ds = CreateDataStore(older, newer);

        var vm = new MainViewModel(ds, new CopilotService(), new UpdateService());

        // Initial order: newer first
        var firstChat = GetFirstChat(vm);
        Assert.Equal("Newer", firstChat?.Title);

        // Simulate a message sent to the older chat (updates timestamp)
        older.UpdatedAt = DateTimeOffset.Now;
        ds.MarkChatChanged(older);
        vm.RefreshChatList();

        firstChat = GetFirstChat(vm);
        Assert.Equal("Older", firstChat?.Title);
    }

    [Fact]
    public void ToggleChatPin_PutsChatInDedicatedFirstGroupWithinProject()
    {
        var projectId = Guid.NewGuid();
        var older = new Chat
        {
            Title = "Pinned candidate",
            ProjectId = projectId,
            UpdatedAt = DateTimeOffset.Now.AddDays(-10)
        };
        var newer = new Chat
        {
            Title = "Recent chat",
            ProjectId = projectId,
            UpdatedAt = DateTimeOffset.Now.AddMinutes(-2)
        };
        var otherProjectPinned = new Chat
        {
            Title = "Other project",
            ProjectId = Guid.NewGuid(),
            IsPinned = true,
            UpdatedAt = DateTimeOffset.Now
        };
        var ds = CreateDataStore(older, newer, otherProjectPinned);
        var vm = new MainViewModel(ds, new CopilotService(), new UpdateService())
        {
            SelectedProjectFilter = projectId
        };

        vm.ToggleChatPinCommand.Execute(older);

        Assert.True(older.IsPinned);
        Assert.Equal(Loc.ChatGroup_Pinned, vm.ChatGroups[0].Label);
        Assert.Equal(older, Assert.Single(vm.ChatGroups[0].Chats));
        Assert.DoesNotContain(otherProjectPinned, vm.ChatGroups.SelectMany(group => group.Chats));

        vm.ToggleChatPinCommand.Execute(older);

        Assert.False(older.IsPinned);
        Assert.DoesNotContain(vm.ChatGroups, group => group.Label == Loc.ChatGroup_Pinned);
        Assert.Equal(newer, GetFirstChat(vm));
    }

    [Fact]
    public void RefreshChatList_MovesOlderGroupChatToToday()
    {
        var now = DateTimeOffset.Now;
        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        var yesterday = new Chat
        {
            Title = "Yesterday Chat",
            UpdatedAt = todayStart.AddDays(-1).AddHours(12)
        };
        var today = new Chat
        {
            Title = "Today Chat",
            UpdatedAt = todayStart.AddTicks(1)
        };
        var ds = CreateDataStore(yesterday, today);
        var vm = new MainViewModel(ds, new CopilotService(), new UpdateService());

        // Yesterday chat should be in a different group initially
        Assert.True(vm.ChatGroups.Count >= 2, "Expected at least two time groups");

        // Simulate message sent → timestamp becomes "now"
        yesterday.UpdatedAt = now;
        ds.MarkChatChanged(yesterday);
        vm.RefreshChatList();

        // Now both chats should be in the "Today" group, yesterday chat first
        var todayGroup = vm.ChatGroups[0];
        Assert.Equal(2, todayGroup.Chats.Count);
        Assert.Equal("Yesterday Chat", todayGroup.Chats[0].Title);
    }

    [Fact]
    public void ChatUpdated_EventTriggersRefreshChatList()
    {
        var older = new Chat { Title = "Older", UpdatedAt = DateTimeOffset.Now.AddHours(-2) };
        var newer = new Chat { Title = "Newer", UpdatedAt = DateTimeOffset.Now.AddMinutes(-5) };
        var ds = CreateDataStore(older, newer);
        var vm = new MainViewModel(ds, new CopilotService(), new UpdateService());

        // Verify initial order
        Assert.Equal("Newer", GetFirstChat(vm)?.Title);

        // Update timestamp on older chat, then raise ChatUpdated on ChatVM
        // (simulates what happens when SendMessage fires the event)
        older.UpdatedAt = DateTimeOffset.Now;
        ds.MarkChatChanged(older);
        vm.ChatVM.RaiseChatUpdatedForTest();

        // ChatUpdated handler in MainViewModel calls RefreshChatList
        Assert.Equal("Older", GetFirstChat(vm)?.Title);
    }

    [Fact]
    public void RefreshChatList_RespectsProjectFilter()
    {
        var projectId = Guid.NewGuid();
        var projectChat = new Chat
        {
            Title = "Project Chat",
            ProjectId = projectId,
            UpdatedAt = DateTimeOffset.Now.AddHours(-3)
        };
        var otherChat = new Chat
        {
            Title = "Other Chat",
            UpdatedAt = DateTimeOffset.Now.AddMinutes(-1)
        };
        var ds = CreateDataStore(projectChat, otherChat);
        var vm = new MainViewModel(ds, new CopilotService(), new UpdateService());

        // Filter by project
        vm.SelectedProjectFilter = projectId;

        // Only the project chat should be visible
        var firstChat = GetFirstChat(vm);
        Assert.Equal("Project Chat", firstChat?.Title);
        Assert.Single(vm.ChatGroups.SelectMany(g => g.Chats));

        // Simulate message sent to project chat
        projectChat.UpdatedAt = DateTimeOffset.Now;
        ds.MarkChatChanged(projectChat);
        vm.RefreshChatList();

        // Still visible and now in Today group
        firstChat = GetFirstChat(vm);
        Assert.Equal("Project Chat", firstChat?.Title);
    }

    [Fact]
    public async Task LoadChatAsync_DoesNotBumpChatWhenOnlyMetadataChanges()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var olderUpdatedAt = DateTimeOffset.Now.AddHours(-2);
            var older = new Chat
            {
                Title = "Older",
                UpdatedAt = olderUpdatedAt
            };
            var newer = new Chat
            {
                Title = "Newer",
                UpdatedAt = DateTimeOffset.Now.AddMinutes(-5),
                PlanContent = "Persisted plan"
            };
            newer.Messages.Add(new ChatMessage { Role = "user", Content = "Hello" });

            var ds = CreateDataStore(older, newer);
            var vm = new MainViewModel(ds, new CopilotService(), new UpdateService());

            Assert.Equal("Newer", GetFirstChat(vm)?.Title);

            await vm.ChatVM.LoadChatAsync(newer);
            await vm.ChatVM.LoadChatAsync(older);
            vm.RefreshChatList();

            Assert.Equal(older.Id, vm.ChatVM.CurrentChat?.Id);
            Assert.Equal(olderUpdatedAt, older.UpdatedAt);
            Assert.Equal("Newer", GetFirstChat(vm)?.Title);
        }, CancellationToken.None);
    }

    private static Chat? GetFirstChat(MainViewModel vm)
        => vm.ChatGroups.FirstOrDefault()?.Chats.FirstOrDefault();
}
