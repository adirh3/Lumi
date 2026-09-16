using System;
using System.Collections.Generic;
using System.Reflection;
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
                ContextCurrentTokens = 250,
                HasExactContextUsage = true
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
                ContextCurrentTokens = 1_000,
                HasExactContextUsage = true
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
                ContextCurrentTokens = 1_000,
                HasExactContextUsage = true
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
    public async Task LoadChatAsync_DropsLegacyUntrustedPersistedContextUsage()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var chat = new Chat
            {
                Title = "Legacy billed usage",
                CopilotSessionId = "legacy-session",
                LastModelUsed = "claude-opus-5",
                ContextCurrentTokens = 220_701,
                ContextTokenLimit = 200_000,
                HasExactContextUsage = false
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

            viewModel.UpdateModelCapabilities([CreateModel("claude-opus-5", 200_000)]);
            await viewModel.LoadChatAsync(chat);

            Assert.False(viewModel.HasContextUsage);
            Assert.True(viewModel.HasTokenUsage);
            Assert.Equal("Context", viewModel.TokenUsageSummary);
            Assert.Equal(0, viewModel.ContextCurrentTokens);
            Assert.Equal(0, chat.ContextCurrentTokens);
        }, CancellationToken.None);
    }

    [Fact]
    public void TokenUsageSummary_HidesBilledTokenTotalsWhenCurrentContextIsUnknown()
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
        Assert.False(viewModel.HasTokenUsage);
        Assert.Equal("", viewModel.TokenUsageSummary);
        Assert.Equal("", viewModel.TokenUsageSuffixText);
    }

    [Fact]
    public void ResolveContextTokenLimitFromSessionUsage_RejectsLimitThatConflictsWithSelectedTier()
    {
        var resolved = ChatViewModel.ResolveContextTokenLimitFromSessionUsage(
            sessionTokenLimit: 936_000,
            catalogTokenLimit: 200_000);

        Assert.Equal(200_000, resolved.TokenLimit);
        Assert.Equal(ContextTokenLimitSource.Catalog, resolved.Source);
    }

    [Fact]
    public void ResolveContextTokenLimitFromSessionUsage_AcceptsMatchingSessionLimit()
    {
        var resolved = ChatViewModel.ResolveContextTokenLimitFromSessionUsage(
            sessionTokenLimit: 200_000,
            catalogTokenLimit: 200_000);

        Assert.Equal(200_000, resolved.TokenLimit);
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
    public async Task SelectedModelChange_ImmediatelyInvalidatesOldExactUsageWithoutAnActiveSession()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var chat = new Chat
            {
                Title = "Switch model",
                LastModelUsed = "model-a",
                ContextCurrentTokens = 80_000,
                ContextTokenLimit = 200_000,
                HasExactContextUsage = true,
                Messages = [new ChatMessage { Role = "user", Content = "hello" }]
            };
            var viewModel = new ChatViewModel(
                new DataStore(new AppData
                {
                    Settings = new UserSettings
                    {
                        AutoSaveChats = false,
                        EnableMemoryAutoSave = false
                    },
                    Chats = [chat]
                }),
                TestCopilot.Shared);
            viewModel.UpdateModelCapabilities(
                [CreateModel("model-a", 200_000), CreateModel("model-b", 100_000)]);
            await viewModel.LoadChatAsync(chat);

            viewModel.SelectedModel = "model-b";

            Assert.Equal(0, viewModel.ContextCurrentTokens);
            Assert.False(chat.HasExactContextUsage);
            Assert.Equal(100_000, viewModel.ContextTokenLimit);
            Assert.Equal("Context", viewModel.TokenUsageSummary);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SelectedContextTierChange_ImmediatelyInvalidatesOldExactUsage()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var chat = new Chat
            {
                Title = "Switch tier",
                LastModelUsed = "gpt-5.5",
                LastContextWindowTierUsed = ModelContextWindowTiers.Default,
                ContextCurrentTokens = 150_000,
                ContextTokenLimit = 272_000,
                HasExactContextUsage = true,
                Messages = [new ChatMessage { Role = "user", Content = "hello" }]
            };
            var viewModel = new ChatViewModel(
                new DataStore(new AppData
                {
                    Settings = new UserSettings
                    {
                        AutoSaveChats = false,
                        EnableMemoryAutoSave = false
                    },
                    Chats = [chat]
                }),
                TestCopilot.Shared);
            viewModel.UpdateModelCapabilities(
                [CreateModel("gpt-5.5", 922_000)],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "gpt-5.5" },
                new Dictionary<string, ModelContextWindowLimits>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gpt-5.5"] = new(272_000, 922_000)
                });
            await viewModel.LoadChatAsync(chat);

            viewModel.SelectedContextWindowTier = "Long";

            Assert.Equal(0, viewModel.ContextCurrentTokens);
            Assert.False(chat.HasExactContextUsage);
            Assert.Equal(922_000, viewModel.ContextTokenLimit);
            Assert.Equal("Context", viewModel.TokenUsageSummary);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task BackgroundSessionModelChange_DoesNotInvalidateForegroundContextDetails()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            var foreground = new Chat { Title = "Foreground" };
            var background = new Chat { Title = "Background" };
            var viewModel = new ChatViewModel(
                new DataStore(new AppData
                {
                    Settings = new UserSettings
                    {
                        AutoSaveChats = false,
                        EnableMemoryAutoSave = false
                    },
                    Chats = [foreground, background]
                }),
                TestCopilot.Shared)
            {
                CurrentChat = foreground
            };
            viewModel.ContextBreakdownItems.Add(
                new ContextTokenBreakdownItem("conversation", "Conversation", 100, "100", 100, "100%"));

            var generationField = typeof(ChatViewModel).GetField(
                "_contextDetailsGeneration",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            generationField.SetValue(viewModel, 7L);
            var invalidate = typeof(ChatViewModel).GetMethod(
                "InvalidateContextDetailsForSessionModelChange",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            invalidate.Invoke(viewModel, [background, "model-b", ModelContextWindowTiers.Default]);

            Assert.Equal(7L, generationField.GetValue(viewModel));
            Assert.Single(viewModel.ContextBreakdownItems);
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

        viewModel.ContextCurrentTokens = 125_000;
        Assert.Equal(100, viewModel.ContextUsagePercent);
        Assert.Equal("100%", viewModel.TokenUsageSummary);
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

        Assert.Equal(100, metrics.UsagePercent);
        Assert.Equal(100, metrics.ProgressPercent);
        Assert.Equal(0, metrics.RemainingTokens);
        Assert.True(metrics.CompactionThresholdReached);
        Assert.Equal(0, metrics.TokensUntilCompaction);
        Assert.Equal(0, ChatViewModel.NormalizeRemovedContextTokens(-250));
    }

    [Fact]
    public void CalculateContextSharePercents_SumToExactlyOneHundred()
    {
        Assert.Equal([55, 17, 28], ChatViewModel.CalculateContextSharePercents(10_500, 3_200, 5_434));
        Assert.Equal([34, 33, 33], ChatViewModel.CalculateContextSharePercents(1, 1, 1));
        Assert.Equal([0, 0, 0], ChatViewModel.CalculateContextSharePercents(-1, 0, 0));
    }

    [Fact]
    public void NormalizeExactContextCurrentTokens_ClampsToTheKnownLimit()
    {
        Assert.Equal(158_287, ChatViewModel.NormalizeExactContextCurrentTokens(158_287, 200_000));
        Assert.Equal(200_000, ChatViewModel.NormalizeExactContextCurrentTokens(220_701, 200_000));
        Assert.Equal(220_701, ChatViewModel.NormalizeExactContextCurrentTokens(220_701, 0));
    }

    [Fact]
    public void ResolveContextInfoPromptTokenLimit_PrefersTheKnownSessionOrCatalogLimit()
    {
        Assert.Equal(200_000, ChatViewModel.ResolveContextInfoPromptTokenLimit(200_000, 128_000));
        Assert.Equal(200_000, ChatViewModel.ResolveContextInfoPromptTokenLimit(200_000, 936_000));
        Assert.Equal(936_000, ChatViewModel.ResolveContextInfoPromptTokenLimit(936_000, 128_000));
        Assert.Equal(128_000, ChatViewModel.ResolveContextInfoPromptTokenLimit(0, 128_000));
    }

    [Fact]
    public void ApplyContextUsage_NormalizesAgainstTheLimitThatWasActuallyAccepted()
    {
        var chat = new Chat
        {
            ContextCurrentTokens = 158_000,
            ContextTokenLimit = 200_000,
            HasExactContextUsage = true
        };
        var viewModel = new ChatViewModel(
            new DataStore(new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                },
                Chats = [chat]
            }),
            TestCopilot.Shared);
        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            ContextCurrentTokens = 158_000,
            ContextTokenLimit = 200_000,
            ContextTokenLimitSource = ContextTokenLimitSource.Session,
            ContextTokenLimitModelId = "model-a",
            ContextTokenLimitTier = ModelContextWindowTiers.Default,
            HasExactContextUsage = true
        };
        var apply = typeof(ChatViewModel).GetMethod(
            "ApplyContextUsage",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        apply.Invoke(
            viewModel,
            [
                chat,
                runtime,
                (long?)158_000,
                (long?)128_000,
                ContextTokenLimitSource.Catalog,
                true,
                true,
                "model-a",
                ModelContextWindowTiers.Default
            ]);

        Assert.Equal(158_000, runtime.ContextCurrentTokens);
        Assert.Equal(200_000, runtime.ContextTokenLimit);
        Assert.Equal(158_000, viewModel.ContextCurrentTokens);
        Assert.Equal(200_000, viewModel.ContextTokenLimit);
    }

    [Fact]
    public void PendingSessionRefresh_DisablesContextActionsUntilTheMarkerIsCleared()
    {
        var chat = new Chat
        {
            CopilotSessionId = "session-a",
            ContextCurrentTokens = 50_000,
            ContextTokenLimit = 200_000,
            HasExactContextUsage = true
        };
        var viewModel = new ChatViewModel(
            new DataStore(new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                },
                Chats = [chat]
            }),
            TestCopilot.Shared)
        {
            CurrentChat = chat
        };
        viewModel.ContextCurrentTokens = chat.ContextCurrentTokens;
        viewModel.ContextTokenLimit = chat.ContextTokenLimit;
        Assert.True(viewModel.CanRefreshContextDetails);
        Assert.True(viewModel.CanCompactContext);

        var pendingReconfigurations = (HashSet<Guid>)typeof(ChatViewModel).GetField(
            "_pendingSessionReconfigurations",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewModel)!;
        pendingReconfigurations.Add(chat.Id);

        Assert.False(viewModel.CanRefreshContextDetails);
        Assert.False(viewModel.CanCompactContext);

        var clear = typeof(ChatViewModel).GetMethod(
            "ClearPendingSessionInvalidation",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        clear.Invoke(viewModel, [chat.Id]);

        Assert.True(viewModel.CanRefreshContextDetails);
        Assert.True(viewModel.CanCompactContext);
    }

    [Fact]
    public async Task InvalidateCurrentSession_ClearsStaleContextAndKeepsNeutralChip()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var chat = new Chat
            {
                CopilotSessionId = "session-a",
                LastModelUsed = "model-a",
                ContextCurrentTokens = 50_000,
                ContextTokenLimit = 200_000,
                HasExactContextUsage = true,
                Messages = [new ChatMessage { Role = "user", Content = "hello" }]
            };
            var viewModel = new ChatViewModel(
                new DataStore(new AppData
                {
                    Settings = new UserSettings
                    {
                        AutoSaveChats = false,
                        EnableMemoryAutoSave = false
                    },
                    Chats = [chat]
                }),
                TestCopilot.Shared);
            viewModel.UpdateModelCapabilities([CreateModel("model-a", 200_000)]);
            await viewModel.LoadChatAsync(chat);
            viewModel.ContextBreakdownItems.Add(
                new ContextTokenBreakdownItem("conversation", "Conversation", 50_000, "50K", 100, "100%"));

            var invalidate = typeof(ChatViewModel).GetMethod(
                "InvalidateCurrentSession",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            invalidate.Invoke(viewModel, null);

            Assert.Null(chat.CopilotSessionId);
            Assert.Equal(0, chat.ContextCurrentTokens);
            Assert.False(chat.HasExactContextUsage);
            Assert.Equal(0, viewModel.ContextCurrentTokens);
            Assert.Empty(viewModel.ContextBreakdownItems);
            Assert.Equal("Context", viewModel.TokenUsageSummary);
            Assert.False(viewModel.CanRefreshContextDetails);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task BackgroundPendingInvalidation_ClearsThatChatsExactContext()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            var foreground = new Chat
            {
                Title = "Foreground",
                Messages = [new ChatMessage { Role = "user", Content = "front" }]
            };
            var background = new Chat
            {
                Title = "Background",
                CopilotSessionId = "session-b",
                ContextCurrentTokens = 75_000,
                ContextTokenLimit = 200_000,
                HasExactContextUsage = true,
                Messages = [new ChatMessage { Role = "user", Content = "back" }]
            };
            var viewModel = new ChatViewModel(
                new DataStore(new AppData
                {
                    Settings = new UserSettings
                    {
                        AutoSaveChats = false,
                        EnableMemoryAutoSave = false
                    },
                    Chats = [foreground, background]
                }),
                TestCopilot.Shared)
            {
                CurrentChat = foreground
            };
            var getRuntime = typeof(ChatViewModel).GetMethod(
                "GetOrCreateRuntimeState",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var backgroundRuntime = (ChatRuntimeState)getRuntime.Invoke(viewModel, [background.Id])!;
            var pending = (HashSet<Guid>)typeof(ChatViewModel).GetField(
                "_pendingSessionInvalidations",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewModel)!;
            pending.Add(background.Id);
            var consume = typeof(ChatViewModel).GetMethod(
                "ConsumePendingSessionInvalidation",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            var consumed = (bool)consume.Invoke(viewModel, [background])!;

            Assert.True(consumed);
            Assert.Null(background.CopilotSessionId);
            Assert.Equal(0, background.ContextCurrentTokens);
            Assert.False(background.HasExactContextUsage);
            Assert.Equal(0, backgroundRuntime.ContextCurrentTokens);
            Assert.False(backgroundRuntime.HasExactContextUsage);
        }, CancellationToken.None);
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
