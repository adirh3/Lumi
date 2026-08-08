using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Localization;
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
            var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);

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
            var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);

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
            var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);

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
            TestCopilot.Shared)
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
            var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
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

    [Fact]
    public void ContextUsage_AloneMakesTheContextButtonVisible()
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
            TestCopilot.Shared)
        {
            ContextCurrentTokens = 12_000,
            ContextTokenLimit = 100_000
        };

        Assert.True(viewModel.HasContextUsage);
        Assert.True(viewModel.HasTokenUsage);
        Assert.Equal("88K available", viewModel.ContextRemainingDisplay);
        Assert.Equal("12K of 100K used", viewModel.ContextUsageDetailDisplay);
        Assert.Equal("Plenty of room", viewModel.ContextHealthDisplay);
        Assert.Equal(12, viewModel.ContextUsageProgress);

        viewModel.ContextCurrentTokens = 85_000;
        viewModel.ContextCompactionThreshold = 90_000;

        Assert.Equal("Compaction soon", viewModel.ContextHealthDisplay);
    }

    [Fact]
    public void CalculateContextWindowMetrics_TracksRemainingAndCompactionHeadroom()
    {
        var metrics = ChatViewModel.CalculateContextWindowMetrics(
            currentTokens: 75_000,
            tokenLimit: 100_000,
            compactionThreshold: 82_000);

        Assert.Equal(75, metrics.UsagePercent);
        Assert.Equal(75, metrics.ProgressPercent);
        Assert.Equal(25_000, metrics.RemainingTokens);
        Assert.True(metrics.HasCompactionThreshold);
        Assert.False(metrics.CompactionThresholdReached);
        Assert.Equal(7_000, metrics.TokensUntilCompaction);
    }

    [Fact]
    public void CalculateContextWindowMetrics_ClampsOverflowAndNegativeSavings()
    {
        var metrics = ChatViewModel.CalculateContextWindowMetrics(
            currentTokens: 125_000,
            tokenLimit: 100_000,
            compactionThreshold: 90_000);

        Assert.Equal(125, metrics.UsagePercent);
        Assert.Equal(100, metrics.ProgressPercent);
        Assert.Equal(0, metrics.RemainingTokens);
        Assert.True(metrics.CompactionThresholdReached);
        Assert.Equal(0, metrics.TokensUntilCompaction);
        Assert.Equal(0, ChatViewModel.NormalizeRemovedContextTokens(-250));
    }

    [Fact]
    public void CalculateContextSharePercent_ExpressesCompositionWithoutOverflow()
    {
        Assert.Equal(55, ChatViewModel.CalculateContextSharePercent(10_500, 19_134));
        Assert.Equal(0, ChatViewModel.CalculateContextSharePercent(-1, 19_134));
        Assert.Equal(0, ChatViewModel.CalculateContextSharePercent(100, 0));
        Assert.Equal(100, ChatViewModel.CalculateContextSharePercent(120, 100));
    }

    [Fact]
    public void FormatCompactionOutcome_UserStopOverridesSdkAbortError()
    {
        var display = ChatViewModel.FormatCompactionOutcome(
            success: false,
            tokensRemoved: null,
            messagesRemoved: null,
            error: "AbortError: Compaction Cancelled",
            stoppedByUser: true);

        Assert.Equal("Context compaction stopped.", display);
    }

    [Fact]
    public void FinalizeRuntimeAfterCompaction_ReturnsStandaloneCompactionToIdle()
    {
        var runtime = new ChatRuntimeState
        {
            IsBusy = true,
            IsStreaming = false,
            TurnInProgress = false,
            StatusText = "Compacting context"
        };

        var becameIdle = ChatViewModel.FinalizeRuntimeAfterCompaction(runtime);

        Assert.True(becameIdle);
        Assert.False(runtime.IsBusy);
        Assert.False(runtime.IsStreaming);
        Assert.False(runtime.TurnInProgress);
        Assert.Equal("", runtime.StatusText);
    }

    [Fact]
    public void FinalizeRuntimeAfterCompaction_KeepsAnActiveTurnBusy()
    {
        var runtime = new ChatRuntimeState
        {
            IsBusy = true,
            IsStreaming = false,
            TurnInProgress = true,
            StatusText = "Compacting context"
        };

        var becameIdle = ChatViewModel.FinalizeRuntimeAfterCompaction(runtime);

        Assert.False(becameIdle);
        Assert.True(runtime.IsBusy);
        Assert.True(runtime.TurnInProgress);
        Assert.Equal("", runtime.StatusText);
    }

    [Fact]
    public void MarkRuntimeCompacting_BlocksFreshSendsWithoutCreatingATurn()
    {
        var runtime = new ChatRuntimeState();

        ChatViewModel.MarkRuntimeCompacting(runtime);

        Assert.True(runtime.IsBusy);
        Assert.False(runtime.IsStreaming);
        Assert.False(runtime.TurnInProgress);
        Assert.Equal(Loc.Status_Compacting, runtime.StatusText);
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
