using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ChatLoadSynchronizationTests
{
    [Fact]
    public async Task LoadChatAsync_SameCurrentChatRefreshesDisplayedMessagesFromModel()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var dataStore = CreateDataStore();
            var chat = new Chat { Title = "sync-chat" };
            chat.Messages.Add(new ChatMessage { Role = "user", Content = "question" });
            dataStore.Data.Chats.Add(chat);
            var vm = new ChatViewModel(dataStore, TestCopilot.Shared);

            await vm.LoadChatAsync(chat);
            chat.Messages.Add(new ChatMessage { Role = "assistant", Content = "latest answer" });

            await vm.LoadChatAsync(chat);

            Assert.Equal(2, vm.Messages.Count);
            Assert.Contains(vm.Messages, message => message.Role == "assistant" && message.Content == "latest answer");
            Assert.Contains(
                vm.TranscriptTurns.SelectMany(turn => turn.Items).OfType<AssistantMessageItem>(),
                item => item.Content == "latest answer");
            vm.Dispose();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LoadChatAsync_SameCurrentChatSweepsInactiveRuntimeStateWithoutEvictingMessages()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var dataStore = CreateDataStore();
            var activeChat = new Chat { Title = "active" };
            var inactiveChat = new Chat { Title = "inactive" };
            activeChat.Messages.Add(new ChatMessage { Role = "user", Content = "question" });
            inactiveChat.Messages.Add(new ChatMessage { Role = "assistant", Content = "finished answer" });
            dataStore.Data.Chats.Add(activeChat);
            dataStore.Data.Chats.Add(inactiveChat);
            var vm = new ChatViewModel(dataStore, TestCopilot.Shared);

            await vm.LoadChatAsync(activeChat);
            var runtimeStates = GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates");
            runtimeStates[inactiveChat.Id] = new ChatRuntimeState { Chat = inactiveChat };

            await vm.LoadChatAsync(activeChat);

            Assert.False(runtimeStates.ContainsKey(inactiveChat.Id));
            Assert.Single(inactiveChat.Messages);
            Assert.Equal("finished answer", inactiveChat.Messages[0].Content);
            vm.Dispose();
        }, CancellationToken.None);
    }

    private static DataStore CreateDataStore()
        => new(new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            }
        });

    private static T GetField<T>(object instance, string name) where T : class
        => (T)(instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance)
            ?? throw new InvalidOperationException($"Field {name} was not found."));
}
