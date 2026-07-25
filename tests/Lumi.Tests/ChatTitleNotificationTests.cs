using System.Collections.Generic;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class ChatTitleNotificationTests
{
    [Fact]
    public void CurrentChatTitle_UpdatesWhenCurrentChatTitleChanges()
    {
        var chat = new Chat { Title = "Original" };
        var viewModel = new ChatViewModel(new DataStore(new AppData { Chats = [chat] }), TestCopilot.Shared)
        {
            CurrentChat = chat
        };
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        chat.Title = "Renamed";

        Assert.Equal("Renamed", viewModel.CurrentChatTitle);
        Assert.Contains(nameof(ChatViewModel.CurrentChatTitle), changedProperties);
    }

    [Fact]
    public void CurrentChatTitle_IgnoresDetachedChatTitleChanges()
    {
        var originalChat = new Chat { Title = "Original" };
        var activeChat = new Chat { Title = "Active" };
        var viewModel = new ChatViewModel(
            new DataStore(new AppData { Chats = [originalChat, activeChat] }),
            TestCopilot.Shared)
        {
            CurrentChat = originalChat
        };
        viewModel.CurrentChat = activeChat;

        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        originalChat.Title = "Detached rename";

        Assert.Equal("Active", viewModel.CurrentChatTitle);
        Assert.DoesNotContain(nameof(ChatViewModel.CurrentChatTitle), changedProperties);
    }

    [Fact]
    public void CommitRenameChat_UpdatesActiveChatTitleProjectionImmediately()
    {
        var chat = new Chat { Title = "Original" };
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            },
            Chats = [chat]
        };
        var viewModel = new MainViewModel(new DataStore(data), TestCopilot.Shared, new UpdateService());
        viewModel.ChatVM.CurrentChat = chat;
        var changedProperties = new List<string?>();
        viewModel.ChatVM.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.StartRenameChatCommand.Execute(chat);
        viewModel.RenamingTitle = "Renamed from history";
        viewModel.CommitRenameChatCommand.Execute(null);

        Assert.Equal("Renamed from history", viewModel.ChatVM.CurrentChatTitle);
        Assert.Contains(nameof(ChatViewModel.CurrentChatTitle), changedProperties);
    }
}
