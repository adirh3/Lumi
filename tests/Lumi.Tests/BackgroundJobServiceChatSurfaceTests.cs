using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class BackgroundJobServiceChatSurfaceTests
{
    [Fact]
    public void ResolveChatExecutorForTest_UsesVisibleDetachedOwnerBeforeFallback()
    {
        var chat = new Chat { Title = "Detached job target" };
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            },
            Chats = [chat]
        };
        var store = new DataStore(data);
        using var registry = new ChatSurfaceRegistry();
        using var fallback = new ChatViewModel(store, new CopilotService());
        using var detached = new ChatViewModel(store, new CopilotService())
        {
            CurrentChat = chat
        };
        registry.Attach(detached);
        using var service = new BackgroundJobService(store, registry, fallback);

        var executor = service.ResolveChatExecutorForTest(chat.Id);

        Assert.Same(detached, executor);
    }

    [Fact]
    public void ResolveChatExecutorForTest_UsesFallbackWhenChatHasNoVisibleOwner()
    {
        var chat = new Chat { Title = "Hidden job target" };
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            },
            Chats = [chat]
        };
        var store = new DataStore(data);
        using var registry = new ChatSurfaceRegistry();
        using var fallback = new ChatViewModel(store, new CopilotService());
        using var service = new BackgroundJobService(store, registry, fallback);

        var executor = service.ResolveChatExecutorForTest(chat.Id);

        Assert.Same(fallback, executor);
    }

    [Fact]
    public void ResolveChatExecutorForTest_UsesLiveOwnerBeforeFallback()
    {
        var running = new Chat { Title = "Running hidden target" };
        var visible = new Chat { Title = "Visible chat" };
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            },
            Chats = [running, visible]
        };
        var store = new DataStore(data);
        using var registry = new ChatSurfaceRegistry();
        using var fallback = new ChatViewModel(store, new CopilotService());
        using var liveOwner = new ChatViewModel(store, new CopilotService())
        {
            CurrentChat = running
        };
        registry.Attach(liveOwner);
        var runtimeStates = GetField<Dictionary<Guid, ChatRuntimeState>>(liveOwner, "_runtimeStates");
        var runtime = new ChatRuntimeState { Chat = running };
        runtime.IsBusy = true;
        runtimeStates[running.Id] = runtime;
        liveOwner.CurrentChat = visible;
        using var service = new BackgroundJobService(store, registry, fallback);

        var executor = service.ResolveChatExecutorForTest(running.Id);

        Assert.Same(liveOwner, executor);
    }

    [Fact]
    public async Task ResolveChatExecutorForInvocation_RetainsCachedOwnerUntilInvocationReleasesIt()
    {
        var chat = new Chat { Title = "Cached job target" };
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "hello" });
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            },
            Chats = [chat]
        };
        var store = new DataStore(data);
        using var registry = new ChatSurfaceRegistry();
        using var sessionStore = new ChatSessionStore(
            store,
            new CopilotService(),
            registry,
            static (surface, chatToLoad) =>
            {
                surface.CurrentChat = chatToLoad;
                return Task.CompletedTask;
            });
        using var service = new BackgroundJobService(store, registry, sessionStore);

        var surface = await sessionStore.AcquireChatAsync(chat);
        sessionStore.Release(surface);
        var hostCounts = GetField<Dictionary<ChatViewModel, int>>(sessionStore, "_hostCounts");

        var (executor, releaseWhenDone) = await InvokeResolveForInvocationAsync(service, chat.Id);

        Assert.Same(surface, executor);
        Assert.True(releaseWhenDone);
        Assert.Equal(1, hostCounts[surface]);

        sessionStore.Release(executor);

        Assert.Equal(0, hostCounts[surface]);
    }

    private static T GetField<T>(object instance, string name) where T : class
        => (T)(instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance)
            ?? throw new InvalidOperationException($"Field {name} was not found."));

    private static Task<(ChatViewModel Executor, bool ReleaseWhenDone)> InvokeResolveForInvocationAsync(
        BackgroundJobService service,
        Guid chatId)
    {
        var method = typeof(BackgroundJobService).GetMethod(
            "ResolveChatExecutorForInvocationAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ResolveChatExecutorForInvocationAsync was not found.");

        return (Task<(ChatViewModel Executor, bool ReleaseWhenDone)>)method.Invoke(service, [chatId])!;
    }
}
