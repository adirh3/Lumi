using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
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
    public async Task LoadChatAsync_UsesDefaultContextTierLimitForPersistedCurrentUsage()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var chat = new Chat
            {
                Title = "Default context",
                LastModelUsed = "gpt-5.5",
                LastContextWindowTierUsed = ModelContextWindowTiers.Default,
                ContextCurrentTokens = 1_000
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

            viewModel.UpdateModelCapabilities(
                [CreateModel("gpt-5.5", 922_000)],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "gpt-5.5" },
                new Dictionary<string, ModelContextWindowLimits>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gpt-5.5"] = new(272_000, 922_000)
                });
            await viewModel.LoadChatAsync(chat);

            Assert.True(viewModel.HasContextUsage);
            Assert.Equal(272_000, viewModel.ContextTokenLimit);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LoadChatAsync_UsesLongContextTierLimitForPersistedCurrentUsage()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var chat = new Chat
            {
                Title = "Long context",
                LastModelUsed = "gpt-5.5",
                LastContextWindowTierUsed = ModelContextWindowTiers.LongContext,
                ContextCurrentTokens = 1_000
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

            viewModel.UpdateModelCapabilities(
                [CreateModel("gpt-5.5", 922_000)],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "gpt-5.5" },
                new Dictionary<string, ModelContextWindowLimits>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gpt-5.5"] = new(272_000, 922_000)
                });
            await viewModel.LoadChatAsync(chat);

            Assert.True(viewModel.HasContextUsage);
            Assert.Equal(922_000, viewModel.ContextTokenLimit);
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

    [Fact]
    public void ResolveContextTokenLimitFromSessionUsage_PrefersSessionLimitOverCatalogLimit()
    {
        var resolved = ChatViewModel.ResolveContextTokenLimitFromSessionUsage(
            sessionTokenLimit: 272_000,
            catalogTokenLimit: 922_000);

        Assert.Equal(272_000, resolved.TokenLimit);
        Assert.Equal(ContextTokenLimitSource.Session, resolved.Source);
    }

    [Fact]
    public void ResolveContextTokenLimitFromSessionUsage_FallsBackToCatalogWhenSessionLimitIsMissing()
    {
        var resolved = ChatViewModel.ResolveContextTokenLimitFromSessionUsage(
            sessionTokenLimit: 0,
            catalogTokenLimit: 922_000);

        Assert.Equal(922_000, resolved.TokenLimit);
        Assert.Equal(ContextTokenLimitSource.Catalog, resolved.Source);
    }

    [Fact]
    public async Task ResolveCatalogFallbackContextWindowSelection_PrefersActiveSessionTierOverRequestedTier()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            var chat = new Chat
            {
                Title = "Active default session",
                LastModelUsed = "gpt-5.5",
                LastContextWindowTierUsed = ModelContextWindowTiers.LongContext
            };
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false,
                    ContextWindowTier = ModelContextWindowTiers.LongContext
                },
                Chats = [chat]
            };
            var viewModel = new ChatViewModel(new DataStore(data), new CopilotService());
            viewModel.UpdateModelCapabilities(
                [CreateModel("gpt-5.5", 922_000)],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "gpt-5.5" },
                new Dictionary<string, ModelContextWindowLimits>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gpt-5.5"] = new(272_000, 922_000)
                });

            var runtime = new ChatRuntimeState
            {
                ActiveModelId = "gpt-5.5",
                ActiveContextWindowTier = ModelContextWindowTiers.Default
            };

            var selection = viewModel.ResolveCatalogFallbackContextWindowSelection(
                chat,
                runtime,
                requestedModelId: "gpt-5.5");

            Assert.Equal("gpt-5.5", selection.ModelId);
            Assert.Equal(ModelContextWindowTiers.Default, selection.ContextTier);
        }, CancellationToken.None);
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
