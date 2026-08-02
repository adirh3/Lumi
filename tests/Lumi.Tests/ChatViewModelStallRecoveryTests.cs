using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class ChatViewModelStallRecoveryTests
{
    [Fact]
    public void ShouldRecoverCompletedTurnIfIdleIsMissing_ReturnsTrueForTextOnlyTurnEnd()
    {
        var runtime = new ChatRuntimeState
        {
            IsBusy = true,
            IsStreaming = false,
            PendingSessionUserMessageCount = 1,
            ActiveToolCount = 0,
            HasPendingBackgroundWork = false
        };

        var shouldRecover = InvokePrivateStatic<bool>(
            "ShouldRecoverCompletedTurnIfIdleIsMissing",
            runtime);

        Assert.True(shouldRecover);
    }

    [Theory]
    [InlineData(true, 0, false)]
    [InlineData(false, 1, false)]
    [InlineData(false, 0, true)]
    public void ShouldRecoverCompletedTurnIfIdleIsMissing_ReturnsFalseWhileWorkRemains(
        bool isStreaming,
        int activeToolCount,
        bool hasPendingBackgroundWork)
    {
        var runtime = new ChatRuntimeState
        {
            IsBusy = true,
            IsStreaming = isStreaming,
            PendingSessionUserMessageCount = 1,
            ActiveToolCount = activeToolCount,
            HasPendingBackgroundWork = hasPendingBackgroundWork
        };

        var shouldRecover = InvokePrivateStatic<bool>(
            "ShouldRecoverCompletedTurnIfIdleIsMissing",
            runtime);

        Assert.False(shouldRecover);
    }

    [Fact]
    public void CanTreatCompletedTurnAsIdle_ReturnsTrueForTurnEndWithoutActiveTools()
    {
        var analysis = new PendingTurnRecoveryAnalysis
        {
            UserMessageObserved = true,
            AssistantTurnEnded = true,
            ActiveToolCount = 0
        };

        Assert.True(InvokePrivateStatic<bool>("CanTreatCompletedTurnAsIdle", analysis));
    }

    [Fact]
    public void CanTreatCompletedTurnAsIdle_ReturnsFalseWhenToolStillActive()
    {
        var analysis = new PendingTurnRecoveryAnalysis
        {
            UserMessageObserved = true,
            AssistantTurnEnded = true,
            ActiveToolCount = 1
        };

        Assert.False(InvokePrivateStatic<bool>("CanTreatCompletedTurnAsIdle", analysis));
    }

    [Fact]
    public async Task ApplyRecoveredTurnState_ActiveToolSnapshotKeepsRecoveryPolling()
    {
        var dataStore = CreateDataStore();
        using var vm = new ChatViewModel(dataStore, TestCopilot.Shared);
        var chat = new Chat { Title = "active recovery tool" };
        dataStore.Data.Chats.Add(chat);
        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true,
            PendingSessionUserMessageCount = 1,
            ActiveToolCount = 0
        };
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = runtime;
        var analysis = new PendingTurnRecoveryAnalysis
        {
            UserMessageObserved = true,
            ActiveToolCount = 1
        };

        var handled = await InvokePrivateAsync<bool>(
            vm,
            "ApplyRecoveredTurnStateAsync",
            chat,
            analysis,
            true);

        Assert.False(handled);
        Assert.Equal(0, runtime.ActiveToolCount);
        Assert.Equal(1, runtime.PendingSessionUserMessageCount);
    }

    [Fact]
    public void SyncRecoveredAssistantMessages_DoesNotTerminalizeActiveRuntime()
    {
        var dataStore = CreateDataStore();
        using var vm = new ChatViewModel(dataStore, TestCopilot.Shared);
        var chat = new Chat { Title = "recovered preamble" };
        dataStore.Data.Chats.Add(chat);
        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true,
            IsStreaming = true,
            PendingSessionUserMessageCount = 1
        };
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = runtime;

        InvokePrivate(
            vm,
            "SyncRecoveredAssistantMessages",
            chat,
            new[] { new RecoveredAssistantMessage("Still working") });

        Assert.Equal("Still working", Assert.Single(chat.Messages).Content);
        Assert.True(runtime.IsBusy);
        Assert.True(runtime.IsStreaming);
    }

    [Fact]
    public void ApplyRecoveredToolStatuses_StopsOrphanedToolCard()
    {
        var toolMessage = new ChatMessage
        {
            Role = "tool",
            ToolName = "powershell",
            ToolCallId = "orphaned-tool",
            ToolStatus = "InProgress",
            ToolStartedAt = DateTimeOffset.UtcNow.AddSeconds(-1)
        };
        var chat = new Chat { Title = "orphaned recovery tool" };
        chat.Messages.Add(toolMessage);

        InvokePrivateStatic(
            "ApplyRecoveredToolStatusToMessages",
            chat,
            new[] { "orphaned-tool" },
            "Stopped",
            DateTimeOffset.UtcNow);

        Assert.Equal("Stopped", toolMessage.ToolStatus);
        Assert.NotNull(toolMessage.ToolDurationMs);
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

    private static T GetField<T>(object instance, string name)
        => (T)(instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance)
            ?? throw new InvalidOperationException($"Field {name} was not found."));

    private static void InvokePrivate(object instance, string name, params object[] args)
    {
        var method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method {name} was not found.");
        method.Invoke(instance, args);
    }

    private static async Task<T> InvokePrivateAsync<T>(object instance, string name, params object[] args)
    {
        var task = instance.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(instance, args) as Task<T>
            ?? throw new InvalidOperationException($"Async method {name} was not found.");

        return await task;
    }

    private static T InvokePrivateStatic<T>(string name, params object[] args)
        => (T)(typeof(ChatViewModel)
            .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?.Invoke(null, args)
            ?? throw new InvalidOperationException($"Static method {name} was not found."));

    private static void InvokePrivateStatic(string name, params object[] args)
    {
        var method = typeof(ChatViewModel).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Static method {name} was not found.");
        method.Invoke(null, args);
    }
}
