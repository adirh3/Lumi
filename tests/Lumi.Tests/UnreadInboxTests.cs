using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
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
/// Coverage for the sidebar unread inbox. Chats run concurrently across projects, so a reply can
/// land in a project the sidebar is filtered away from — the aggregates here are what make that
/// visible, and revealing a chat has to move the project filter or the opened chat stays hidden
/// behind the filter.
/// </summary>
[Collection("Headless UI")]
public sealed class UnreadInboxTests
{
    [Fact]
    public async Task UnreadAggregates_CountAllUnreadChatsRegardlessOfProject()
    {
        await RunAsync(() =>
        {
            var work = new Project { Name = "Work" };
            var home = new Project { Name = "Home" };
            var a = new Chat { Title = "A", ProjectId = work.Id, HasUnreadMessages = true };
            var b = new Chat { Title = "B", ProjectId = home.Id, HasUnreadMessages = true };
            var c = new Chat { Title = "C", ProjectId = home.Id };
            using var vm = CreateViewModel([work, home], a, b, c);

            Assert.True(vm.HasUnreadChats);
            Assert.Equal(2, vm.UnreadChatCount);
            Assert.Equal("2", vm.UnreadBadgeText);
            Assert.Equal(2, vm.UnreadChats.Count);
            Assert.Equal(1, vm.GetProjectUnreadCount(work.Id));
            Assert.Equal(1, vm.GetProjectUnreadCount(home.Id));
        });
    }

    [Fact]
    public async Task MarkingAChatUnreadUpdatesAggregatesWithoutAnExplicitRefresh()
    {
        await RunAsync(() =>
        {
            var chat = new Chat { Title = "Background reply" };
            using var vm = CreateViewModel([], chat);

            Assert.False(vm.HasUnreadChats);

            chat.HasUnreadMessages = true;

            Assert.True(vm.HasUnreadChats);
            Assert.Equal(1, vm.UnreadChatCount);
            Assert.Equal(Loc.Unread_SummaryOne, vm.UnreadSummaryText);
        });
    }

    [Fact]
    public async Task ActiveProjectFilter_ReportsUnreadItHides()
    {
        await RunAsync(() =>
        {
            var work = new Project { Name = "Work" };
            var home = new Project { Name = "Home" };
            var inWork = new Chat { Title = "In work", ProjectId = work.Id, HasUnreadMessages = true };
            var inHome = new Chat { Title = "In home", ProjectId = home.Id, HasUnreadMessages = true };
            using var vm = CreateViewModel([work, home], inWork, inHome);

            Assert.False(vm.HasUnreadOutsideFilter);

            vm.SelectProjectFilterCommand.Execute(work);

            Assert.True(vm.HasUnreadOutsideFilter);
            Assert.Equal(1, vm.UnreadOutsideFilterCount);
            Assert.Equal(2, vm.UnreadChatCount);

            var hidden = vm.UnreadChats.Single(entry => entry.Chat.Id == inHome.Id);
            Assert.True(hidden.IsOutsideActiveFilter);
            Assert.Equal("Home", hidden.ProjectName);

            var visible = vm.UnreadChats.Single(entry => entry.Chat.Id == inWork.Id);
            Assert.False(visible.IsOutsideActiveFilter);
        });
    }

    [Theory]
    [InlineData(1, 0, "1 unread chat")]
    [InlineData(3, 0, "3 unread chats")]
    [InlineData(1, 1, "1 unread in another project")]
    [InlineData(3, 3, "3 unread in other projects")]
    [InlineData(3, 2, "3 unread \u00b7 2 in other projects")]
    public void Summary_NamesWhereTheUnreadChatsAreRelativeToTheFilter(int total, int outside, string expected)
    {
        Loc.Load("en");
        Assert.Equal(expected, MainViewModel.BuildUnreadSummary(total, outside));
    }

    [Fact]
    public async Task HiddenUnreadChatsExplainWhyTheyAreNotInTheList()
    {
        await RunAsync(() =>
        {
            var work = new Project { Name = "Work" };
            var home = new Project { Name = "Home" };
            var inWork = new Chat { Title = "In work", ProjectId = work.Id, HasUnreadMessages = true };
            var inHome = new Chat { Title = "In home", ProjectId = home.Id, HasUnreadMessages = true };
            using var vm = CreateViewModel([work, home], inWork, inHome);

            // Unfiltered: nothing is hidden, so neither the pill nor any row claims otherwise.
            Assert.Equal(Loc.Unread_OpenTooltip, vm.UnreadTooltipText);
            Assert.All(vm.UnreadChats, entry => Assert.Equal(entry.Title, entry.TooltipText));

            vm.SelectProjectFilterCommand.Execute(work);

            Assert.Contains("1", vm.UnreadTooltipText);
            Assert.Contains("not viewing right now", vm.UnreadTooltipText);

            var hidden = vm.UnreadChats.Single(entry => entry.Chat.Id == inHome.Id);
            Assert.Contains("In home", hidden.TooltipText);
            Assert.Contains("Home", hidden.TooltipText);
            Assert.Contains("hidden", hidden.TooltipText);

            var visible = vm.UnreadChats.Single(entry => entry.Chat.Id == inWork.Id);
            Assert.Equal(visible.Title, visible.TooltipText);
        });
    }

    [Fact]
    public void Summary_IsEmptyWhenNothingIsUnread()
    {
        Loc.Load("en");
        Assert.Equal("", MainViewModel.BuildUnreadSummary(0, 0));
    }

    [Fact]
    public async Task OpeningAnUnreadChatFromAnotherProjectMovesTheFilterToThatChat()
    {
        await RunAsync(async () =>
        {
            var work = new Project { Name = "Work" };
            var home = new Project { Name = "Home" };
            var workChat = new Chat { Title = "Work chat", ProjectId = work.Id };
            var homeChat = new Chat { Title = "Home reply", ProjectId = home.Id, HasUnreadMessages = true };
            using var vm = CreateViewModel([work, home], workChat, homeChat);

            vm.SelectProjectFilterCommand.Execute(work);
            Assert.Equal(work.Id, vm.SelectedProjectFilter);

            var entry = vm.UnreadChats.Single(candidate => candidate.Chat.Id == homeChat.Id);
            await vm.OpenUnreadChatCommand.ExecuteAsync(entry);

            // The chat is opened AND revealed — switching the filter must not auto-open some other
            // chat from the destination project instead.
            Assert.Equal(home.Id, vm.SelectedProjectFilter);
            Assert.Equal(homeChat.Id, vm.ChatVM.CurrentChat?.Id);
            Assert.False(homeChat.HasUnreadMessages);
            Assert.False(vm.IsUnreadPanelOpen);
        });
    }

    [Fact]
    public async Task RevealingAChatDoesNotOpenADifferentChatFromTheTargetProject()
    {
        await RunAsync(async () =>
        {
            var home = new Project { Name = "Home" };
            // Newer than the unread chat, so the project filter's "open most recent" path picks this.
            var recent = new Chat
            {
                Title = "Recent",
                ProjectId = home.Id,
                UpdatedAt = DateTimeOffset.Now,
                MessageCount = 4,
            };
            var stale = new Chat
            {
                Title = "Stale but unread",
                ProjectId = home.Id,
                UpdatedAt = DateTimeOffset.Now.AddHours(-3),
                MessageCount = 2,
                HasUnreadMessages = true,
            };
            // The displayed chat must hold LOADED messages, not just a persisted MessageCount:
            // OnSelectedProjectFilterChanged treats an empty in-memory Messages list as a draft and
            // returns early, which would skip the auto-open branch this guard exists to suppress.
            var elsewhere = new Chat { Title = "Elsewhere", MessageCount = 1 };
            elsewhere.Messages.Add(new ChatMessage { Role = "user", Content = "hello" });
            using var vm = CreateViewModel([home], recent, stale, elsewhere);

            Assert.True(await vm.OpenChatByIdAsync(elsewhere.Id));
            Assert.NotEmpty(vm.ChatVM.CurrentChat!.Messages);

            // Which chat "wins" a competing open is a timing race, so assert on the deterministic
            // signal instead: the reveal must never navigate through, or even realize, another chat.
            var visited = new List<Guid?>();
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.ActiveChatId))
                    visited.Add(vm.ActiveChatId);
            };

            Assert.True(await vm.RevealChatAsync(stale));

            // The competing open is fire-and-forget, so let it land before asserting.
            await DrainUiThreadAsync();

            Assert.Equal(home.Id, vm.SelectedProjectFilter);
            Assert.Equal(stale.Id, vm.ChatVM.CurrentChat?.Id);
            Assert.DoesNotContain(recent.Id, visited);
            Assert.False(vm.ChatSurfaceRegistry.TryGetOwner(recent.Id, out _));
        });
    }

    [Fact]
    public async Task MarkAllRead_ClearsEveryChatAndClosesThePanel()
    {
        await RunAsync(() =>
        {
            var a = new Chat { Title = "A", HasUnreadMessages = true };
            var b = new Chat { Title = "B", HasUnreadMessages = true };
            using var vm = CreateViewModel([], a, b);

            vm.ToggleUnreadPanelCommand.Execute(null);
            Assert.True(vm.IsUnreadPanelOpen);

            vm.MarkAllChatsReadCommand.Execute(null);

            Assert.False(a.HasUnreadMessages);
            Assert.False(b.HasUnreadMessages);
            Assert.False(vm.HasUnreadChats);
            Assert.Empty(vm.UnreadChats);
            Assert.False(vm.IsUnreadPanelOpen);
        });
    }

    [Fact]
    public async Task ThePanelCannotBeOpenedWithNothingUnreadAndClosesWhenTheLastOneIsRead()
    {
        await RunAsync(() =>
        {
            var chat = new Chat { Title = "Only", HasUnreadMessages = true };
            using var vm = CreateViewModel([], chat);

            vm.ToggleUnreadPanelCommand.Execute(null);
            Assert.True(vm.IsUnreadPanelOpen);

            chat.HasUnreadMessages = false;

            Assert.False(vm.HasUnreadChats);
            Assert.False(vm.IsUnreadPanelOpen);

            vm.ToggleUnreadPanelCommand.Execute(null);
            Assert.False(vm.IsUnreadPanelOpen);
        });
    }

    [Fact]
    public async Task TheDrawerListIsCappedAndReportsTheRemainder()
    {
        await RunAsync(() =>
        {
            var chats = Enumerable.Range(0, 11)
                .Select(index => new Chat
                {
                    Title = $"Chat {index}",
                    HasUnreadMessages = true,
                    UpdatedAt = DateTimeOffset.Now.AddMinutes(-index),
                })
                .ToArray();
            using var vm = CreateViewModel([], chats);

            Assert.Equal(11, vm.UnreadChatCount);
            Assert.Equal(8, vm.UnreadChats.Count);
            Assert.True(vm.HasUnreadOverflow);
            Assert.Equal("3 more unread", vm.UnreadOverflowText);
            // Newest first, so the cap keeps the replies most likely to still matter.
            Assert.Equal("Chat 0", vm.UnreadChats[0].Title);
        });
    }

    [Fact]
    public async Task TheBadgeClampsLargeCounts()
    {
        await RunAsync(() =>
        {
            var chats = Enumerable.Range(0, 12)
                .Select(index => new Chat { Title = $"Chat {index}", HasUnreadMessages = true })
                .ToArray();
            using var vm = CreateViewModel([], chats);

            Assert.Equal("9+", vm.UnreadBadgeText);
        });
    }

    [Fact]
    public async Task OpeningAnUnreadChatNormallyAlsoDropsItFromTheInbox()
    {
        await RunAsync(async () =>
        {
            var chat = new Chat { Title = "Reply", HasUnreadMessages = true, MessageCount = 1 };
            using var vm = CreateViewModel([], chat);

            Assert.True(vm.HasUnreadChats);

            Assert.True(await vm.OpenChatByIdAsync(chat.Id));

            Assert.False(vm.HasUnreadChats);
            Assert.Empty(vm.UnreadChats);
        });
    }

    private static async Task RunAsync(Action body)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            body();
        }, CancellationToken.None);
    }

    /// <summary>
    /// Async variant. <see cref="HeadlessTestSession.Dispatch(Func{Task}, CancellationToken)"/> awaits
    /// the body but swallows the exception it faults with, so an assertion failure inside an async
    /// body would silently leave the test green. Capture the failure and rethrow it out here.
    /// </summary>
    private static async Task RunAsync(Func<Task> body)
    {
        using var session = HeadlessTestSession.Start();
        ExceptionDispatchInfo? failure = null;

        await session.Dispatch(async () =>
        {
            try
            {
                Loc.Load("en");
                await body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        }, CancellationToken.None);

        failure?.Throw();
    }

    /// <summary>
    /// Lets already-queued UI-thread continuations finish. Needed wherever the code under test
    /// fire-and-forgets a chat open (<c>_ = OpenChat(...)</c>) — without draining, a regression that
    /// starts a competing open would land after the assertions and go unnoticed.
    /// </summary>
    private static async Task DrainUiThreadAsync()
    {
        for (var i = 0; i < 4; i++)
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }

    private static MainViewModel CreateViewModel(Project[] projects, params Chat[] chats)
    {
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            },
            Projects = [.. projects],
            Chats = [.. chats]
        };

        return new MainViewModel(
            new DataStore(data),
            TestCopilot.Shared,
            new UpdateService(),
            startBackgroundJobs: false);
    }
}
