using System;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ChatSuggestionPersistenceTests
{
    [Fact]
    public async Task LoadChatAsync_RestoresPersistedSuggestionsPerChat()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var firstChat = CreateChatWithSuggestions("First chat", ["Run code review", "Push changes"]);
            var secondChat = CreateChatWithSuggestions("Second chat", ["Plan my day"]);
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                },
                Chats = [firstChat, secondChat]
            };

            var viewModel = new ChatViewModel(new DataStore(data), new CopilotService());

            await viewModel.LoadChatAsync(firstChat);
            Assert.Equal("Run code review", viewModel.SuggestionA);
            Assert.Equal("Push changes", viewModel.SuggestionB);
            Assert.True(viewModel.HasSuggestions);

            await viewModel.LoadChatAsync(secondChat);
            Assert.Equal("Plan my day", viewModel.SuggestionA);
            Assert.Equal("", viewModel.SuggestionB);

            await viewModel.LoadChatAsync(firstChat);
            Assert.Equal("Run code review", viewModel.SuggestionA);
            Assert.Equal("Push changes", viewModel.SuggestionB);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LoadChatAsync_DoesNotRestoreSuggestionsAfterNewerUserMessage()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var chat = CreateChatWithSuggestions("Stale suggestions", ["Run code review"]);
            chat.Messages.Add(new ChatMessage
            {
                Role = "user",
                Content = "Actually, do something else"
            });
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                },
                Chats = [chat]
            };

            var viewModel = new ChatViewModel(new DataStore(data), new CopilotService());

            await viewModel.LoadChatAsync(chat);

            Assert.Equal("", viewModel.SuggestionA);
            Assert.False(viewModel.HasSuggestions);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task QueueChatCompletionFollowUps_GeneratesSuggestionsForInactiveChat()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var activeChat = CreateCompletedChat("Active chat");
            var inactiveChat = CreateCompletedChat("Inactive chat");
            var inactiveAssistantMessage = inactiveChat.Messages.Last(static message => message.Role == "assistant");
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                },
                Chats = [activeChat, inactiveChat]
            };

            var viewModel = new ChatViewModel(new DataStore(data), new CopilotService());

            await viewModel.LoadChatAsync(activeChat);
            viewModel.QueueChatCompletionFollowUps(inactiveChat);
            await WaitForConditionAsync(() =>
                inactiveChat.FollowUpSuggestionAssistantMessageId == inactiveAssistantMessage.Id);

            Assert.Equal(activeChat.Id, viewModel.CurrentChat?.Id);
            Assert.False(viewModel.IsSuggestionsGenerating);
            Assert.Equal("", viewModel.SuggestionA);
        }, CancellationToken.None);
    }

    private static Chat CreateChatWithSuggestions(string title, List<string> suggestions)
    {
        var chat = CreateCompletedChat(title);
        var assistantMessage = chat.Messages.Last(static message => message.Role == "assistant");
        chat.FollowUpSuggestions = suggestions;
        chat.FollowUpSuggestionAssistantMessageId = assistantMessage.Id;
        return chat;
    }

    private static Chat CreateCompletedChat(string title)
    {
        var assistantMessage = new ChatMessage
        {
            Role = "assistant",
            Content = "Done."
        };
        return new Chat
        {
            Title = title,
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = "Can you help?"
                },
                assistantMessage
            ]
        };
    }

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50; i++)
        {
            if (condition())
                return;

            await Task.Delay(20);
        }

        Assert.True(condition());
    }
}
