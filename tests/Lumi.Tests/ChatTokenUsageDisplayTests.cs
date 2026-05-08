using System;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot.SDK;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ChatTokenUsageDisplayTests
{
    [Fact]
    public async Task LoadChatAsync_UsesModelContextLimitForPersistedCurrentUsage()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var chat = new Chat
            {
                Title = "Token usage",
                LastModelUsed = "gpt-test",
                TotalInputTokens = 100,
                TotalOutputTokens = 25,
                ContextCurrentTokens = 250
            };
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

            viewModel.UpdateModelCapabilities([CreateModel("gpt-test", 1_000)]);
            await viewModel.LoadChatAsync(chat);

            Assert.True(viewModel.HasContextUsage);
            Assert.Equal(250, viewModel.ContextCurrentTokens);
            Assert.Equal(1_000, viewModel.ContextTokenLimit);
            Assert.Equal(25, viewModel.ContextUsagePercent);
            Assert.Equal("25%", viewModel.TokenUsageSummary);
            Assert.Equal("context", viewModel.TokenUsageSuffixText);
        }, CancellationToken.None);
    }

    [Fact]
    public void TokenUsageSummary_FallsBackToTokenCountWhenCurrentContextIsUnknown()
    {
        var viewModel = new ChatViewModel(
            new DataStore(new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            }),
            new CopilotService())
        {
            TotalInputTokens = 100,
            TotalOutputTokens = 25,
            ContextTokenLimit = 1_000
        };

        Assert.False(viewModel.HasContextUsage);
        Assert.Equal("125", viewModel.TokenUsageSummary);
        Assert.Equal("tokens", viewModel.TokenUsageSuffixText);
    }

    private static ModelInfo CreateModel(string id, int contextTokenLimit)
        => new()
        {
            Id = id,
            Name = id,
            Capabilities = new ModelCapabilities
            {
                Limits = new ModelLimits
                {
                    MaxContextWindowTokens = contextTokenLimit
                },
                Supports = new ModelSupports()
            }
        };
}
