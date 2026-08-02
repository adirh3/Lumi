using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Guards the composer's model-selection catalog against regressions that silently disable the
/// reasoning-effort and context-window pickers, or downgrade an explicit long-context selection.
/// </summary>
[Collection("Headless UI")]
public sealed class ModelSelectionCatalogTests
{
    [Fact]
    public async Task UpdateModelCapabilities_ByokMergeKeepsCopilotModelCapabilities()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            var viewModel = CreateViewModel(out _);
            SeedCatalog(viewModel);
            viewModel.SelectedModel = "gpt-5.5";

            Assert.NotNull(viewModel.QualityLevels);
            Assert.NotNull(viewModel.ContextWindowTiers);

            // MainViewModel.InjectByokModels pushes BYOK picker tokens (which the SDK model list never
            // returns) into every surface right after the real catalog lands. Replacing instead of
            // merging here used to erase every model's reasoning efforts and long-context support.
            viewModel.UpdateModelCapabilities(
                [new ModelInfo { Id = "byok:endpoint:model" }],
                longContextModelIds: null,
                contextWindowLimits: null,
                merge: true);

            Assert.Equal(["Low", "Medium", "High"], viewModel.QualityLevels!);
            Assert.Equal(["Default", "Long"], viewModel.ContextWindowTiers!);
            Assert.Equal("Default", viewModel.SelectedContextWindowTier);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ApplySessionModelState_WithoutContextTierKeepsLongContextSelection()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            var viewModel = CreateViewModel(out var chat);
            chat.LastModelUsed = "gpt-5.5";
            chat.LastContextWindowTierUsed = ModelContextWindowTiers.LongContext;
            SeedCatalog(viewModel);

            var runtime = new ChatRuntimeState
            {
                ActiveModelId = "gpt-5.5",
                ActiveContextWindowTier = ModelContextWindowTiers.LongContext
            };

            // A mid-session ModelChange does not always echo the context tier back; treating that
            // silence as "Default" downgraded the session to the 272K window and persisted it.
            InvokeApplySessionModelState(viewModel, chat, runtime, "gpt-5.5", sessionContextTier: null);

            Assert.Equal(ModelContextWindowTiers.LongContext, chat.LastContextWindowTierUsed);
            Assert.Equal(ModelContextWindowTiers.LongContext, runtime.ActiveContextWindowTier);
            Assert.Equal(922_000, viewModel.ContextTokenLimit);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ApplySessionModelState_WithExplicitContextTierWins()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            var viewModel = CreateViewModel(out var chat);
            chat.LastModelUsed = "gpt-5.5";
            chat.LastContextWindowTierUsed = ModelContextWindowTiers.LongContext;
            SeedCatalog(viewModel);

            var runtime = new ChatRuntimeState
            {
                ActiveModelId = "gpt-5.5",
                ActiveContextWindowTier = ModelContextWindowTiers.LongContext
            };

            InvokeApplySessionModelState(
                viewModel,
                chat,
                runtime,
                "gpt-5.5",
                sessionContextTier: ModelContextWindowTiers.Default);

            Assert.Equal(ModelContextWindowTiers.Default, chat.LastContextWindowTierUsed);
            Assert.Equal(ModelContextWindowTiers.Default, runtime.ActiveContextWindowTier);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ApplySessionModelState_ForCapabilityLessModelKeepsStoredPreferences()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            var viewModel = CreateViewModel(out var chat);
            chat.LastModelUsed = "gpt-5.5";
            chat.LastReasoningEffortUsed = "high";
            chat.LastContextWindowTierUsed = ModelContextWindowTiers.LongContext;
            SeedCatalog(viewModel);

            var runtime = new ChatRuntimeState();

            // Switching the chat to a model with no reasoning efforts and no long-context tier used to
            // null both persisted fields, so switching back could only fall back to the global default
            // and the user's explicit High/Long choice was gone for good.
            InvokeApplySessionModelState(viewModel, chat, runtime, "plain-model", sessionContextTier: null);

            Assert.Equal("high", chat.LastReasoningEffortUsed);
            Assert.Equal(ModelContextWindowTiers.LongContext, chat.LastContextWindowTierUsed);

            // The live session state must still report the truth for the capability-less model.
            Assert.Null(runtime.ActiveContextWindowTier);
            Assert.Null(viewModel.QualityLevels);
            Assert.Null(viewModel.ContextWindowTiers);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ComposerRestoresSelectionAfterSessionRunsOnCapabilityLessModel()
    {
        using var session = HeadlessTestSession.Start();

        string? qualityWhileCapabilityLess = null;
        string? tierWhileCapabilityLess = null;
        string? restoredQuality = null;
        string? restoredTier = null;

        // Assertions must live OUTSIDE Dispatch: the Func<Task> overload swallows exceptions thrown
        // inside the dispatched body, so a failing Assert in there would not fail the test.
        await session.Dispatch(async () =>
        {
            var viewModel = CreateViewModel(out var chat);
            chat.Messages.Add(new ChatMessage { Role = "user", Content = "hi" });
            SeedCatalog(viewModel);
            await viewModel.LoadChatAsync(chat);

            viewModel.SelectedModel = "gpt-5.5";
            viewModel.SelectedQuality = "Low";
            viewModel.SelectedContextWindowTier = "Long";

            // A send on a model with no reasoning efforts and no long-context tier drives this exact
            // call from the session lifecycle. It used to null both persisted fields, so switching back
            // could only fall back to the global default and the explicit Low/Long choice was lost.
            viewModel.SelectedModel = "plain-model";
            InvokeApplySessionModelState(
                viewModel,
                chat,
                new ChatRuntimeState(),
                "plain-model",
                sessionContextTier: null);

            qualityWhileCapabilityLess = viewModel.SelectedQuality;
            tierWhileCapabilityLess = viewModel.SelectedContextWindowTier;

            viewModel.SelectedModel = "gpt-5.5";
            restoredQuality = viewModel.SelectedQuality;
            restoredTier = viewModel.SelectedContextWindowTier;
        }, CancellationToken.None);

        Assert.Null(qualityWhileCapabilityLess);
        Assert.Null(tierWhileCapabilityLess);
        Assert.Equal("Low", restoredQuality);
        Assert.Equal("Long", restoredTier);
    }

    [Fact]
    public async Task ResolveContextWindowTierForModel_KeepsLongContextWhenComposerShowsAnotherModel()
    {
        using var session = HeadlessTestSession.Start();

        string? resolvedForOverrideModel = null;
        string? resolvedViaComposer = null;
        string? resolvedForUnsupportedModel = null;

        await session.Dispatch(async () =>
        {
            var viewModel = CreateViewModel(out var chat);
            chat.Messages.Add(new ChatMessage { Role = "user", Content = "hi" });
            SeedCatalog(viewModel);
            await viewModel.LoadChatAsync(chat);

            // A manage_chats per-send model override writes the new model onto the chat but leaves the
            // composer showing the previous one, so the chat is CurrentChat while SelectedModel is stale.
            viewModel.SelectedModel = "plain-model";
            chat.LastModelUsed = "gpt-5.5";
            chat.LastContextWindowTierUsed = ModelContextWindowTiers.LongContext;

            resolvedForOverrideModel = viewModel.ResolveContextWindowTierForModel(
                chat.LastContextWindowTierUsed,
                chat.LastModelUsed);

            // The composer-sensitive resolver normalizes against the displayed capability-less model
            // first, collapsing "long_context" to null and then to Default. Forwarding that to
            // SetModelAsync silently ran the override send at the small context window.
            resolvedViaComposer = viewModel.ResolveSelectedContextWindowTierForChat(chat, chat.LastModelUsed);

            // A tier the target model does not support is still dropped rather than forwarded to the SDK.
            resolvedForUnsupportedModel = viewModel.ResolveContextWindowTierForModel(
                ModelContextWindowTiers.LongContext,
                "plain-model");
        }, CancellationToken.None);

        Assert.Equal(ModelContextWindowTiers.LongContext, resolvedForOverrideModel);
        Assert.Equal(ModelContextWindowTiers.Default, resolvedViaComposer);
        Assert.Null(resolvedForUnsupportedModel);
    }

    private static ChatViewModel CreateViewModel(out Chat chat)
    {
        chat = new Chat { Title = "Model selection" };
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            },
            Chats = [chat]
        };

        return new ChatViewModel(new DataStore(data), TestCopilot.Shared);
    }

    private static void SeedCatalog(ChatViewModel viewModel)
        => viewModel.UpdateModelCapabilities(
            [
                new ModelInfo
                {
                    Id = "gpt-5.5",
                    Name = "gpt-5.5",
                    SupportedReasoningEfforts = ["low", "medium", "high"],
                    DefaultReasoningEffort = "medium"
                },
                // Stands in for a model such as claude-sonnet-4.5: no reasoning efforts, no long context.
                new ModelInfo { Id = "plain-model", Name = "plain-model" }
            ],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "gpt-5.5" },
            new Dictionary<string, ModelContextWindowLimits>(StringComparer.OrdinalIgnoreCase)
            {
                ["gpt-5.5"] = new(272_000, 922_000),
                ["plain-model"] = new(200_000, null)
            });

    private static void InvokeApplySessionModelState(
        ChatViewModel viewModel,
        Chat chat,
        ChatRuntimeState runtime,
        string modelId,
        string? sessionContextTier)
    {
        var method = typeof(ChatViewModel).GetMethod(
            "ApplySessionModelState",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplySessionModelState was not found.");

        method.Invoke(viewModel, [chat, runtime, modelId, null, sessionContextTier, true]);
    }
}
