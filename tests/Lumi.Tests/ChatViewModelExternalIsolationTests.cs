using System.IO;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class ChatViewModelExternalIsolationTests
{
    [Fact]
    public async Task RemoteSteerWithoutAttachments_PreservesDesktopComposerAttachments()
    {
        using var harness = TestHarness.Create();
        var attachmentPath = Path.Combine(Path.GetTempPath(), "desktop-draft.txt");
        harness.ViewModel.AddAttachment(attachmentPath);

        var accepted = await harness.ViewModel.SteerExternalMessageAsync(
            harness.Chat,
            "sent from the phone",
            "Lumi Mobile");

        Assert.True(accepted);
        Assert.Equal([attachmentPath], harness.ViewModel.PendingAttachments);
        Assert.Single(harness.ViewModel.PendingAttachmentItems);

        var remoteMessage = Assert.Single(harness.Chat.Messages);
        Assert.Equal("Lumi Mobile", remoteMessage.Author);
        Assert.Empty(remoteMessage.Attachments);
        Assert.Equal(MessageSteerState.Queued, remoteMessage.SteerDelivery);
    }

    [Theory]
    [InlineData("Managed worker")]
    [InlineData("User-chosen title")]
    public void FirstExternalMessage_PreservesExplicitTitle(string explicitTitle)
    {
        using var harness = TestHarness.Create(explicitTitle);
        const string prompt = "Handle the deployment";
        harness.Chat.Messages.Add(new ChatMessage { Role = "user", Content = prompt });

        var prepared = harness.ViewModel.TryPrepareFirstExternalMessageTitle(harness.Chat, prompt);

        Assert.False(prepared);
        Assert.Equal(explicitTitle, harness.Chat.Title);
    }

    [Fact]
    public void FirstExternalMessage_NamesChatWithDefaultTitle()
    {
        using var harness = TestHarness.Create();
        const string prompt = "Investigate the flaky desktop test";
        harness.Chat.Messages.Add(new ChatMessage { Role = "user", Content = prompt });

        var prepared = harness.ViewModel.TryPrepareFirstExternalMessageTitle(harness.Chat, prompt);

        Assert.True(prepared);
        Assert.Equal(prompt, harness.Chat.Title);
    }

    [Fact]
    public void DelayedStopFailureCannotWriteStatusOntoAnotherChat()
    {
        using var harness = TestHarness.Create();
        var stoppedChatId = harness.Chat.Id;
        var nextChat = new Chat { Id = Guid.NewGuid(), Title = "Next" };
        harness.ViewModel.CurrentChat = nextChat;
        harness.ViewModel.StatusText = "Next chat status";

        harness.ViewModel.ApplyStopError(stoppedChatId, "Abort failed");

        Assert.Equal("Next chat status", harness.ViewModel.StatusText);
    }

    private sealed class TestHarness : IDisposable
    {
        private TestHarness(ChatViewModel viewModel, Chat chat)
        {
            ViewModel = viewModel;
            Chat = chat;
        }

        public ChatViewModel ViewModel { get; }

        public Chat Chat { get; }

        public static TestHarness Create(string? title = null)
        {
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    AutoGenerateTitles = false,
                    EnableMemoryAutoSave = false
                }
            };
            var chat = new Chat();
            if (title is not null)
                chat.Title = title;
            data.Chats.Add(chat);

            var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared)
            {
                CurrentChat = chat
            };
            return new TestHarness(viewModel, chat);
        }

        public void Dispose()
            => ViewModel.Dispose();
    }
}
