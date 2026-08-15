using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
    public async Task LoadChatAsync_NormalHistorySkipsTailPreview()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, async () =>
        {
            var dataStore = CreateDataStore();
            var chat = new Chat { Title = "normal-history" };
            chat.Messages.Add(new ChatMessage { Role = "user", Content = "question" });
            chat.Messages.Add(new ChatMessage { Role = "assistant", Content = "answer" });
            dataStore.Data.Chats.Add(chat);
            var vm = new ChatViewModel(dataStore, TestCopilot.Shared);
            var previewRaised = false;
            vm.LoadingTranscriptPreviewReady += () => previewRaised = true;

            await vm.LoadChatAsync(chat);

            Assert.False(previewRaised);
            Assert.False(vm.HasLoadingTranscriptPreview);
            Assert.Empty(vm.LoadingTranscriptPreviewTurns);
            vm.Dispose();
        });
    }

    [Fact]
    public async Task LoadChatAsync_LargeHistoryPublishesBoundedTailPreviewBeforeSingleFullReset()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, async () =>
        {
            var dataStore = CreateDataStore();
            var chat = new Chat { Title = "large-preview" };
            chat.Messages.Add(new ChatMessage { Role = "user", Content = "old user turn" });
            for (var index = 0; index < 2101; index++)
            {
                chat.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = $"assistant {index}"
                });
            }
            dataStore.Data.Chats.Add(chat);
            var vm = new ChatViewModel(dataStore, TestCopilot.Shared);
            var eventOrder = new List<string>();
            var resetCount = 0;
            var previewTurnCount = 0;
            var previewContainsTail = false;

            vm.Messages.CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Reset)
                    resetCount++;
            };
            vm.LoadingTranscriptPreviewReady += () =>
            {
                eventOrder.Add("preview");
                previewTurnCount = vm.LoadingTranscriptPreviewTurns.Count;
                previewContainsTail = vm.LoadingTranscriptPreviewTurns
                    .SelectMany(static turn => turn.Items)
                    .OfType<AssistantMessageItem>()
                    .Any(static item => item.Content == "assistant 2100");
            };
            vm.TranscriptRebuilt += () => eventOrder.Add("full");

            await vm.LoadChatAsync(chat);

            Assert.Equal(["preview", "full"], eventOrder);
            Assert.InRange(previewTurnCount, 1, 1200);
            Assert.True(previewContainsTail);
            Assert.Equal(1, resetCount);
            Assert.Equal(chat.Messages.Count, vm.Messages.Count);
            Assert.True(vm.HasLoadingTranscriptPreview);
            Assert.NotEmpty(vm.LoadingTranscriptPreviewTurns);
            Assert.True(vm.TryClaimInitialTranscriptTailPrewarm());
            Assert.False(vm.TryClaimInitialTranscriptTailPrewarm());

            vm.Messages.Add(new ChatMessageViewModel(new ChatMessage
            {
                Role = "assistant",
                Content = "new background result"
            }));

            Assert.False(vm.HasLoadingTranscriptPreview);
            Assert.Empty(vm.LoadingTranscriptPreviewTurns);
            vm.Dispose();
        });
    }

    [Fact]
    public async Task LoadingPreview_WaitsForEveryTranscriptHostBeforeClearing()
    {
        using var session = HeadlessTestSession.Start();

        await DispatchAsync(session, () =>
        {
            var vm = new ChatViewModel(CreateDataStore(), TestCopilot.Shared);
            var turn = new TranscriptTurn("turn:shared-preview");
            turn.Items.Add(new AssistantMessageItem(
                new ChatMessageViewModel(new ChatMessage
                {
                    Role = "assistant",
                    Content = "shared preview"
                }),
                showTimestamps: false));
            vm.LoadingTranscriptPreviewTurns = new ObservableCollection<TranscriptTurn> { turn };
            vm.HasLoadingTranscriptPreview = true;
            Assert.True(vm.MarkLoadingTranscriptPreviewPresented());

            vm.BeginTranscriptRealization();
            vm.BeginTranscriptRealization();
            vm.EndTranscriptRealization();

            Assert.True(vm.IsTranscriptRealizing);
            Assert.True(vm.HasLoadingTranscriptPreview);

            vm.EndTranscriptRealization();

            Assert.False(vm.IsTranscriptRealizing);
            Assert.False(vm.HasLoadingTranscriptPreview);
            Assert.Empty(vm.LoadingTranscriptPreviewTurns);
            vm.Dispose();
            return Task.CompletedTask;
        });
    }

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

    [Fact]
    public async Task LoadChatAsync_SwitchAwayAndBackResetsProgressiveAdmissionToTail()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var dataStore = CreateDataStore();
            var longChat = new Chat { Title = "long" };
            for (var index = 0; index < 80; index++)
            {
                longChat.Messages.Add(new ChatMessage { Role = "user", Content = $"question {index}" });
                longChat.Messages.Add(new ChatMessage { Role = "assistant", Content = $"answer {index}" });
            }

            var otherChat = new Chat { Title = "other" };
            otherChat.Messages.Add(new ChatMessage { Role = "user", Content = "other question" });
            dataStore.Data.Chats.Add(longChat);
            dataStore.Data.Chats.Add(otherChat);
            using var vm = new ChatViewModel(dataStore, TestCopilot.Shared);

            await vm.LoadChatAsync(longChat);
            while (vm.HasOlderTranscriptPages)
            {
                vm.UpdateTranscriptViewport(
                    offsetY: 0,
                    viewportHeight: 720,
                    extentHeight: Math.Max(720, vm.MountedTranscriptTurns.Count * 72),
                    isFollowingTail: false,
                    isPinnedToBottom: false,
                    distanceFromBottom: 1_000,
                    pagingDirection: TranscriptPagingDirection.TowardOlder);
            }

            Assert.Equal(vm.TranscriptTurns.Count, vm.MountedTranscriptTurns.Count);

            await vm.LoadChatAsync(otherChat);
            await vm.LoadChatAsync(longChat);

            Assert.True(vm.MountedTranscriptTurns.Count < vm.TranscriptTurns.Count);
            Assert.True(vm.HasOlderTranscriptPages);
            Assert.Same(vm.TranscriptTurns[^1], vm.MountedTranscriptTurns[^1]);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ClearChat_ThenReopenResetsProgressiveAdmissionToTail()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var dataStore = CreateDataStore();
            var chat = new Chat { Title = "reopen" };
            for (var index = 0; index < 80; index++)
            {
                chat.Messages.Add(new ChatMessage { Role = "user", Content = $"question {index}" });
                chat.Messages.Add(new ChatMessage { Role = "assistant", Content = $"answer {index}" });
            }

            dataStore.Data.Chats.Add(chat);
            using var vm = new ChatViewModel(dataStore, TestCopilot.Shared);
            await vm.LoadChatAsync(chat);
            while (vm.HasOlderTranscriptPages)
            {
                vm.UpdateTranscriptViewport(
                    offsetY: 0,
                    viewportHeight: 720,
                    extentHeight: Math.Max(720, vm.MountedTranscriptTurns.Count * 72),
                    isFollowingTail: false,
                    isPinnedToBottom: false,
                    distanceFromBottom: 1_000,
                    pagingDirection: TranscriptPagingDirection.TowardOlder);
            }

            Assert.Equal(vm.TranscriptTurns.Count, vm.MountedTranscriptTurns.Count);

            vm.ClearChat();
            await vm.LoadChatAsync(chat);

            Assert.True(vm.MountedTranscriptTurns.Count < vm.TranscriptTurns.Count);
            Assert.True(vm.HasOlderTranscriptPages);
            Assert.Same(vm.TranscriptTurns[^1], vm.MountedTranscriptTurns[^1]);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task InPlaceRebuildOfAdmittedHistory_DoesNotArmWholeTranscriptPrewarm()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var dataStore = CreateDataStore();
            var chat = new Chat { Title = "preserved-rebuild" };
            for (var index = 0; index < 80; index++)
            {
                chat.Messages.Add(new ChatMessage { Role = "user", Content = $"question {index}" });
                chat.Messages.Add(new ChatMessage { Role = "assistant", Content = $"answer {index}" });
            }

            dataStore.Data.Chats.Add(chat);
            using var vm = new ChatViewModel(dataStore, TestCopilot.Shared);
            await vm.LoadChatAsync(chat);
            Assert.True(vm.TryClaimInitialTranscriptTailPrewarm());
            while (vm.HasOlderTranscriptPages)
            {
                vm.UpdateTranscriptViewport(
                    offsetY: 0,
                    viewportHeight: 720,
                    extentHeight: Math.Max(720, vm.MountedTranscriptTurns.Count * 72),
                    isFollowingTail: false,
                    isPinnedToBottom: false,
                    distanceFromBottom: 1_000,
                    pagingDirection: TranscriptPagingDirection.TowardOlder);
            }

            vm.RebuildTranscript();

            Assert.Equal(vm.TranscriptTurns.Count, vm.MountedTranscriptTurns.Count);
            Assert.False(vm.TryClaimInitialTranscriptTailPrewarm());
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

    private static async Task DispatchAsync(HeadlessTestSession session, Func<Task> action)
    {
        Exception? dispatchedException = null;
        await session.Dispatch(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                dispatchedException = ex;
            }
        }, CancellationToken.None);

        if (dispatchedException is not null)
            ExceptionDispatchInfo.Capture(dispatchedException).Throw();
    }
}
