using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class ChatTagsViewModelTests
{
    [Fact]
    public async Task SaveTag_CreatesCatalogEntryWithoutAssigningAChat()
    {
        var chat = new Chat { Title = "Unassigned" };
        var store = new DataStore(new AppData { Chats = [chat] });
        var viewModel = new ChatTagsViewModel(store);

        viewModel.NewTagCommand.Execute(null);
        viewModel.EditName = "Research";
        viewModel.SelectedColorOption = viewModel.ColorOptions[2];
        await viewModel.SaveTagCommand.ExecuteAsync(null);

        var tag = Assert.Single(store.Data.ChatTags);
        Assert.Equal("Research", tag.Name);
        Assert.Equal("#35C2A8", tag.Color);
        Assert.Null(chat.TagId);
        Assert.Null(chat.Tag);
    }

    [Fact]
    public async Task SaveTag_RejectsDuplicateNamesCaseInsensitively()
    {
        var existing = new ChatTag { Name = "Work" };
        var store = new DataStore(new AppData { ChatTags = [existing] });
        var viewModel = new ChatTagsViewModel(store);

        viewModel.NewTagCommand.Execute(null);
        viewModel.EditName = "work";
        await viewModel.SaveTagCommand.ExecuteAsync(null);

        Assert.Single(store.Data.ChatTags);
        Assert.NotEmpty(viewModel.ValidationMessage);
    }

    [Fact]
    public async Task SaveTag_UpdatesExistingNameAndColor()
    {
        var tag = new ChatTag { Name = "Old", Color = "#6E8BFF" };
        var chat = new Chat { Title = "Linked", TagId = tag.Id, Tag = tag };
        var store = new DataStore(new AppData
        {
            ChatTags = [tag],
            Chats = [chat]
        });
        var viewModel = new ChatTagsViewModel(store)
        {
            SelectedTag = tag,
            EditName = "Updated",
            SelectedColorOption = null
        };
        List<string?> propertyChanges = [];
        chat.PropertyChanged += (_, args) => propertyChanges.Add(args.PropertyName);
        viewModel.SelectedColorOption = viewModel.ColorOptions[6];

        await viewModel.SaveTagCommand.ExecuteAsync(null);

        Assert.Equal("Updated", tag.Name);
        Assert.Equal("#FB7185", tag.Color);
        Assert.Same(tag, chat.Tag);
        Assert.Equal(tag.Id, chat.TagId);
        Assert.Equal("Updated", chat.TagName);
        Assert.Equal("#FB7185", chat.TagColor);
        Assert.Contains(nameof(Chat.TagName), propertyChanges);
        Assert.Contains(nameof(Chat.TagColor), propertyChanges);
    }

    [Fact]
    public void Tag_DoesNotRetainDiscardedChat()
    {
        var tag = new ChatTag { Name = "Work" };
        var weakChat = CreateDiscardedTaggedChat(tag);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(weakChat.TryGetTarget(out _));
        GC.KeepAlive(tag);
    }

    [Fact]
    public async Task AssignTag_UsesCatalogEntryAndCanClearIt()
    {
        var tag = new ChatTag { Name = "Work" };
        var chat = new Chat { Title = "Target" };
        var store = new DataStore(new AppData
        {
            ChatTags = [tag],
            Chats = [chat]
        });
        var viewModel = new ChatTagsViewModel(store);

        await viewModel.AssignTagCommand.ExecuteAsync(new ChatTagAssignment(chat, tag));
        Assert.Equal(tag.Id, chat.TagId);
        Assert.Same(tag, chat.Tag);

        await viewModel.AssignTagCommand.ExecuteAsync(new ChatTagAssignment(chat, null));
        Assert.Null(chat.TagId);
        Assert.Null(chat.Tag);
    }

    [Fact]
    public async Task DeleteTag_UnassignsEveryLinkedChat()
    {
        var tag = new ChatTag { Name = "Temporary" };
        var first = new Chat { Title = "First", TagId = tag.Id, Tag = tag };
        var second = new Chat { Title = "Second", TagId = tag.Id, Tag = tag };
        var store = new DataStore(new AppData
        {
            ChatTags = [tag],
            Chats = [first, second]
        });
        var viewModel = new ChatTagsViewModel(store)
        {
            SelectedTag = tag
        };

        await viewModel.DeleteTagCommand.ExecuteAsync(null);

        Assert.Empty(store.Data.ChatTags);
        Assert.All(store.Data.Chats, chat =>
        {
            Assert.Null(chat.TagId);
            Assert.Null(chat.Tag);
        });
    }

    [Fact]
    public async Task CatalogEdit_RefreshesAnotherOpenManager()
    {
        var tag = new ChatTag { Name = "Old", Color = "#6E8BFF" };
        var store = new DataStore(new AppData { ChatTags = [tag] });
        using var editor = new ChatTagsViewModel(store);
        using var observer = new ChatTagsViewModel(store);
        observer.OpenManagerCommand.Execute(null);
        editor.SelectedTag = tag;
        editor.EditName = "Updated";
        editor.SelectedColorOption = editor.ColorOptions[2];

        await editor.SaveTagCommand.ExecuteAsync(null);

        Assert.Same(tag, observer.SelectedTag);
        Assert.Equal("Updated", observer.EditName);
        Assert.Equal("#35C2A8", observer.SelectedColorOption?.Hex);
        Assert.Equal("Updated", Assert.Single(observer.Tags).Name);
    }

    [Fact]
    public async Task CatalogDelete_InvalidatesAnotherOpenEditorAndPreventsOrphanSave()
    {
        var tag = new ChatTag { Name = "Temporary" };
        var store = new DataStore(new AppData { ChatTags = [tag] });
        using var editor = new ChatTagsViewModel(store);
        using var observer = new ChatTagsViewModel(store);
        observer.OpenManagerCommand.Execute(null);
        editor.SelectedTag = tag;

        await editor.DeleteTagCommand.ExecuteAsync(null);

        Assert.Null(observer.SelectedTag);
        Assert.False(observer.IsCreating);
        Assert.NotEmpty(observer.ValidationMessage);

        observer.EditName = "Ghost";
        await observer.SaveTagCommand.ExecuteAsync(null);

        Assert.Empty(store.Data.ChatTags);
        Assert.NotEmpty(observer.ValidationMessage);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Chat> CreateDiscardedTaggedChat(ChatTag tag)
    {
        var chat = new Chat { TagId = tag.Id, Tag = tag };
        return new WeakReference<Chat>(chat);
    }
}

[Collection("Headless UI")]
public sealed class ChatTagsPaginationTests
{
    [Fact]
    public async Task AssignTag_PreservesLoadedChatPage()
    {
        using var session = HeadlessTestSession.Start();
        var now = DateTimeOffset.Now;
        var chats = Enumerable.Range(0, 75)
            .Select(index => new Chat
            {
                Title = $"Chat {index:00}",
                UpdatedAt = now.AddMinutes(-index)
            })
            .ToArray();
        var tag = new ChatTag { Name = "Priority" };
        var store = new DataStore(new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            },
            Chats = [.. chats],
            ChatTags = [tag]
        });

        await session.Dispatch(async () =>
        {
            using var main = new MainViewModel(
                store,
                TestCopilot.Shared,
                new UpdateService(),
                startBackgroundJobs: false,
                initializeCopilotOnStartup: false);

            Assert.Equal(50, main.ChatGroups.Sum(group => group.Chats.Count));
            main.LoadMoreChats();
            Assert.Equal(75, main.ChatGroups.Sum(group => group.Chats.Count));
            Assert.False(main.HasMoreChats);

            await main.ChatTagsVM.AssignTagCommand.ExecuteAsync(
                new ChatTagAssignment(chats[^1], tag));

            Assert.Equal(75, main.ChatGroups.Sum(group => group.Chats.Count));
            Assert.False(main.HasMoreChats);
            Assert.Equal(tag.Id, chats[^1].TagId);
        }, default);
    }

    [Fact]
    public async Task ManageChatsEdit_PreservesLoadedChatPage()
    {
        using var session = HeadlessTestSession.Start();
        var now = DateTimeOffset.Now;
        var chats = Enumerable.Range(0, 75)
            .Select(index => new Chat
            {
                Title = $"Chat {index:00}",
                UpdatedAt = now.AddMinutes(-index)
            })
            .ToArray();
        var tag = new ChatTag { Name = "Priority" };
        var store = new DataStore(new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            },
            Chats = [.. chats],
            ChatTags = [tag]
        });

        await session.Dispatch(async () =>
        {
            using var registry = new ChatSurfaceRegistry();
            using var sessionStore = new ChatSessionStore(store, TestCopilot.Shared, registry);
            using var orchestration = new ChatOrchestrationService(store, registry, sessionStore);
            sessionStore.OrchestrationService = orchestration;
            using var main = new MainViewModel(
                store,
                TestCopilot.Shared,
                new UpdateService(),
                startBackgroundJobs: false,
                chatSurfaceRegistry: registry,
                chatSessionStore: sessionStore,
                initializeCopilotOnStartup: false);

            main.LoadMoreChats();
            Assert.Equal(75, main.ChatGroups.Sum(group => group.Chats.Count));

            var result = await orchestration.ManageChatsAsync(
                "edit",
                identifier: chats[^1].Id.ToString(),
                tag: tag.Name);
            Dispatcher.UIThread.RunJobs();

            Assert.Contains("tag: \"Priority\"", result);
            Assert.Equal(75, main.ChatGroups.Sum(group => group.Chats.Count));
            Assert.False(main.HasMoreChats);
            Assert.Equal(tag.Id, chats[^1].TagId);
        }, default);
    }
}
