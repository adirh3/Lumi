using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
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

        var vm = new MainViewModel(ds, TestCopilot.Shared, new UpdateService());

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
        var vm = new MainViewModel(ds, TestCopilot.Shared, new UpdateService())
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
        var vm = new MainViewModel(ds, TestCopilot.Shared, new UpdateService());

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
        var vm = new MainViewModel(ds, TestCopilot.Shared, new UpdateService());

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
        var vm = new MainViewModel(ds, TestCopilot.Shared, new UpdateService());

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
            var vm = new MainViewModel(ds, TestCopilot.Shared, new UpdateService());

            Assert.Equal("Newer", GetFirstChat(vm)?.Title);

            await vm.ChatVM.LoadChatAsync(newer);
            await vm.ChatVM.LoadChatAsync(older);
            vm.RefreshChatList();

            Assert.Equal(older.Id, vm.ChatVM.CurrentChat?.Id);
            Assert.Equal(olderUpdatedAt, older.UpdatedAt);
            Assert.Equal("Newer", GetFirstChat(vm)?.Title);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RefreshChatList_PreservesGroupsControlsAndSelectionWhenNothingChanged()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var project = new Project { Name = "Work" };
            var target = new Chat
            {
                Title = "Selected chat",
                ProjectId = project.Id,
                UpdatedAt = new DateTimeOffset(DateTimeOffset.Now.Date.AddHours(12), DateTimeOffset.Now.Offset),
                Messages = [new ChatMessage { Role = "user", Content = "Existing conversation" }],
            };
            var other = new Chat { Title = "Other chat", UpdatedAt = target.UpdatedAt.AddMinutes(-1) };
            var store = CreateDataStore(target, other);
            store.Data.Projects.Add(project);
            using var vm = new MainViewModel(store, TestCopilot.Shared, new UpdateService(),
                startBackgroundJobs: false, initializeCopilotOnStartup: false);
            var window = new MainWindow { DataContext = vm };
            window.Show();
            try
            {
                await DrainSidebarAsync(window);
                vm.IsOnboarded = true;
                await DrainSidebarAsync(window);
                Assert.True(await vm.OpenChatByIdAsync(target.Id));
                await DrainSidebarAsync(window);
                var group = Assert.Single(vm.ChatGroups);
                var rows = group.Chats;
                var control = FindSidebarRow(window, target);
                var changes = 0;
                var selectionSyncs = 0;
                vm.ChatGroups.CollectionChanged += (_, _) => changes++;
                rows.CollectionChanged += (_, _) => changes++;
                vm.ChatSelectionSyncRequested += _ => selectionSyncs++;

                vm.RefreshChatList();
                vm.RefreshChatList();
                await DrainSidebarAsync(window);

                Assert.Equal(0, changes);
                Assert.Equal(0, selectionSyncs);
                Assert.Same(group, Assert.Single(vm.ChatGroups));
                Assert.Same(rows, group.Chats);
                Assert.Same(control, FindSidebarRow(window, target));
                Assert.True(control.IsSelected);

                vm.SelectedProjectFilter = project.Id;
                await DrainSidebarAsync(window);

                Assert.Same(group, Assert.Single(vm.ChatGroups));
                Assert.Same(control, FindSidebarRow(window, target));
                Assert.True(control.IsSelected);
                Assert.Equal(target.Id, vm.ActiveChatId);
                Assert.Same(target, Assert.Single(group.Chats));
                Assert.False(target.ShowProjectBadge);
                Assert.Equal(1, selectionSyncs);
            }
            finally
            {
                window.Close();
                await DrainSidebarAsync(window);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PagingAndFilteringKeepRetainedGroupsAndDoNotResetTheirRows()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var project = new Project { Name = "Work" };
            var now = DateTimeOffset.Now;
            var chats = Enumerable.Range(0, 75).Select(index => new Chat
            {
                Title = $"Chat {index}",
                ProjectId = index % 2 == 0 ? project.Id : null,
                UpdatedAt = new DateTimeOffset(now.Date.AddHours(12), now.Offset).AddSeconds(-index),
            }).ToArray();
            var store = CreateDataStore(chats);
            store.Data.Projects.Add(project);
            using var vm = new MainViewModel(store, TestCopilot.Shared, new UpdateService(),
                startBackgroundJobs: false, initializeCopilotOnStartup: false);
            var group = Assert.Single(vm.ChatGroups);
            var changes = new List<NotifyCollectionChangedAction>();
            group.Chats.CollectionChanged += (_, args) => changes.Add(args.Action);

            vm.LoadMoreChats();

            Assert.Same(group, Assert.Single(vm.ChatGroups));
            Assert.Equal(chats, group.Chats);
            Assert.False(vm.HasMoreChats);

            vm.SelectedProjectFilter = project.Id;

            Assert.Same(group, Assert.Single(vm.ChatGroups));
            Assert.Equal(chats.Where(chat => chat.ProjectId == project.Id), group.Chats);
            Assert.All(group.Chats, chat => Assert.False(chat.ShowProjectBadge));
            Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);

            vm.ClearProjectFilterCommand.Execute(null);

            Assert.Same(group, Assert.Single(vm.ChatGroups));
            Assert.Equal(chats.Take(50), group.Chats);
            Assert.True(vm.HasMoreChats);
            Assert.All(group.Chats.Where(chat => chat.ProjectId == project.Id),
                chat => Assert.Equal("Work", chat.ProjectBadgeText));
            Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LoadMoreChats_SelectsAlreadyOpenChatInRetainedGroup()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var now = new DateTimeOffset(DateTimeOffset.Now.Date.AddHours(12), DateTimeOffset.Now.Offset);
            var chats = Enumerable.Range(0, 75).Select(index => new Chat
            {
                Title = $"Chat {index}",
                UpdatedAt = now.AddSeconds(-index),
                Messages = [new ChatMessage { Role = "user", Content = "Existing conversation" }],
            }).ToArray();
            using var vm = new MainViewModel(CreateDataStore(chats), TestCopilot.Shared,
                new UpdateService(), startBackgroundJobs: false, initializeCopilotOnStartup: false);
            var window = new MainWindow { DataContext = vm };
            window.Show();
            try
            {
                await DrainSidebarAsync(window);
                vm.IsOnboarded = true;
                await DrainSidebarAsync(window);
                var target = chats[60];
                Assert.True(await vm.OpenChatByIdAsync(target.Id));
                await DrainSidebarAsync(window);
                var group = Assert.Single(vm.ChatGroups);
                var list = window.FindControl<ItemsControl>("ChatGroupsHost")!
                    .GetVisualDescendants().OfType<ListBox>().Single();
                var groupChanges = 0;
                vm.ChatGroups.CollectionChanged += (_, _) => groupChanges++;
                var rowChanges = new List<NotifyCollectionChangedAction>();
                group.Chats.CollectionChanged += (_, args) => rowChanges.Add(args.Action);
                Assert.DoesNotContain(target, group.Chats);
                Assert.Null(list.SelectedItem);

                vm.LoadMoreChats();
                await DrainSidebarAsync(window);

                Assert.Same(group, Assert.Single(vm.ChatGroups));
                Assert.Equal(0, groupChanges);
                Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, rowChanges);
                Assert.Contains(target, group.Chats);
                Assert.Equal(target.Id, vm.ActiveChatId);
                Assert.Same(target, list.SelectedItem);
            }
            finally
            {
                window.Close();
                await DrainSidebarAsync(window);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ToggleChatPin_KeepsActiveSelectionBetweenRetainedGroups()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var now = new DateTimeOffset(DateTimeOffset.Now.Date.AddHours(12), DateTimeOffset.Now.Offset);
            var target = new Chat
            {
                Title = "Selected chat",
                UpdatedAt = now,
                Messages = [new ChatMessage { Role = "user", Content = "Existing conversation" }],
            };
            var pinned = new Chat { Title = "Pinned chat", UpdatedAt = now.AddMinutes(-1), IsPinned = true };
            var other = new Chat { Title = "Other chat", UpdatedAt = now.AddMinutes(-2) };
            using var vm = new MainViewModel(CreateDataStore(target, pinned, other), TestCopilot.Shared,
                new UpdateService(), startBackgroundJobs: false, initializeCopilotOnStartup: false);
            var window = new MainWindow { DataContext = vm };
            window.Show();
            try
            {
                await DrainSidebarAsync(window);
                vm.IsOnboarded = true;
                await DrainSidebarAsync(window);
                Assert.True(await vm.OpenChatByIdAsync(target.Id));
                await DrainSidebarAsync(window);
                Assert.True(FindSidebarRow(window, target).IsSelected);
                var groups = vm.ChatGroups.ToArray();
                Assert.Equal(2, groups.Length);
                var groupChanges = 0;
                vm.ChatGroups.CollectionChanged += (_, _) => groupChanges++;
                var rowChanges = new List<NotifyCollectionChangedAction>();
                foreach (var group in groups)
                    group.Chats.CollectionChanged += (_, args) => rowChanges.Add(args.Action);

                foreach (var expectedPinned in new[] { true, false })
                {
                    vm.ToggleChatPinCommand.Execute(target);
                    await DrainSidebarAsync(window);

                    Assert.Equal(expectedPinned, target.IsPinned);
                    Assert.Equal(groups, vm.ChatGroups);
                    Assert.Equal(0, groupChanges);
                    Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, rowChanges);
                    Assert.Equal(target.Id, vm.ActiveChatId);
                    Assert.True(FindSidebarRow(window, target).IsSelected);
                }
            }
            finally
            {
                window.Close();
                await DrainSidebarAsync(window);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RefreshChatList_MovesReorderedRowsInsteadOfResettingTheGroup()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var now = new DateTimeOffset(DateTimeOffset.Now.Date.AddHours(12), DateTimeOffset.Now.Offset);
            var older = new Chat { Title = "Older", UpdatedAt = now.AddMinutes(-2) };
            var newer = new Chat { Title = "Newer", UpdatedAt = now.AddMinutes(-1) };
            using var vm = new MainViewModel(CreateDataStore(older, newer), TestCopilot.Shared,
                new UpdateService(), startBackgroundJobs: false, initializeCopilotOnStartup: false);
            var group = Assert.Single(vm.ChatGroups);
            var changes = new List<NotifyCollectionChangedAction>();
            group.Chats.CollectionChanged += (_, args) => changes.Add(args.Action);

            older.UpdatedAt = now;
            vm.RefreshChatList();

            Assert.Same(group, Assert.Single(vm.ChatGroups));
            Assert.Equal(new[] { older, newer }, group.Chats);
            Assert.Equal(new[] { NotifyCollectionChangedAction.Move }, changes);

            vm.DataStore.Data.Chats.Remove(older);
            vm.RefreshChatList();

            Assert.Same(newer, Assert.Single(group.Chats));
            Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ProjectSelectionRefreshesAnOpenPickerButDoesNotRebuildItsClosingRows()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var work = new Project { Name = "Work" };
            var home = new Project { Name = "Home" };
            var store = CreateDataStore();
            store.Data.Projects.AddRange([work, home]);
            using var vm = new MainViewModel(store, TestCopilot.Shared, new UpdateService(),
                startBackgroundJobs: false, initializeCopilotOnStartup: false);
            var window = new MainWindow { DataContext = vm };
            window.Show();
            try
            {
                // Avoid scheduling startup's delayed focus across per-test dispatcher lifetimes.
                await DrainSidebarAsync(window);
                vm.IsOnboarded = true;
                await DrainSidebarAsync(window);
                new ButtonAutomationPeer(window.FindControl<Button>("ProjectSwitchButton")!).Invoke();
                await DrainSidebarAsync(window);
                var results = window.FindControl<StackPanel>("ProjectFilterResults")!;

                vm.SelectedProjectFilter = home.Id;
                await DrainSidebarAsync(window);
                Assert.Contains(results.Children.OfType<Button>(), button =>
                    button.Classes.Contains("selected")
                    && button.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Home"));

                var workRow = results.Children.OfType<Button>().Single(button =>
                    button.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Work"));
                var rows = results.Children.ToArray();
                var changes = 0;
                results.Children.CollectionChanged += (_, _) => changes++;

                new ButtonAutomationPeer(workRow).Invoke();
                await DrainSidebarAsync(window);

                Assert.Equal(work.Id, vm.SelectedProjectFilter);
                Assert.Equal("Work", window.FindControl<TextBlock>("ProjectSwitchTitleText")!.Text);
                Assert.Equal(0, changes);
                Assert.Equal(rows, results.Children);
                Assert.DoesNotContain("open", window.FindControl<Button>("ProjectSwitchButton")!.Classes);
            }
            finally
            {
                window.Close();
                await DrainSidebarAsync(window);
            }
        }, CancellationToken.None);
    }

    private static ListBoxItem FindSidebarRow(MainWindow window, Chat chat)
        => window.FindControl<ItemsControl>("ChatGroupsHost")!.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(row => ReferenceEquals(row.DataContext, chat));

    private static async Task DrainSidebarAsync(MainWindow window)
    {
        for (var i = 0; i < 4; i++)
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        window.UpdateLayout();
    }

    private static Chat? GetFirstChat(MainViewModel vm)
        => vm.ChatGroups.FirstOrDefault()?.Chats.FirstOrDefault();
}
