using System.Linq;
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
            var vm = new ChatViewModel(dataStore, new CopilotService());

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

    private static DataStore CreateDataStore()
        => new(new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            }
        });
}
