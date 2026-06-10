using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class ChatSurfaceRegistryTests
{
    [Fact]
    public void Attach_TracksCurrentChatOwner()
    {
        var chat = new Chat { Title = "Tracked" };
        using var surface = CreateSurface(chat);
        using var registry = new ChatSurfaceRegistry();

        registry.Attach(surface);

        Assert.True(registry.TryGetOwner(chat.Id, out var owner));
        Assert.Same(surface, owner);
    }

    [Fact]
    public void CurrentChatChange_MovesOwnerToNewChat()
    {
        var first = new Chat { Title = "First" };
        var second = new Chat { Title = "Second" };
        using var surface = CreateSurface(first, second);
        using var registry = new ChatSurfaceRegistry();
        registry.Attach(surface);

        surface.CurrentChat = second;

        Assert.False(registry.TryGetOwner(first.Id, out _));
        Assert.True(registry.TryGetOwner(second.Id, out var owner));
        Assert.Same(surface, owner);
    }

    [Fact]
    public void TryGetLiveOwner_FindsSurfaceThatOwnsInactiveRunningChat()
    {
        var running = new Chat { Title = "Running" };
        var visible = new Chat { Title = "Visible" };
        using var surface = CreateSurface(running, visible);
        using var registry = new ChatSurfaceRegistry();
        registry.Attach(surface);
        var runtimeStates = GetField<Dictionary<Guid, ChatRuntimeState>>(surface, "_runtimeStates");
        var runtime = new ChatRuntimeState { Chat = running };
        runtime.IsBusy = true;
        runtimeStates[running.Id] = runtime;
        surface.CurrentChat = visible;

        Assert.False(registry.TryGetOwner(running.Id, out _));
        Assert.True(registry.TryGetLiveOwner(running.Id, out var owner));
        Assert.Same(surface, owner);
    }

    [Fact]
    public void TryGetLiveOwner_FindsSurfaceThatOwnsPendingQuestion()
    {
        var questionChat = new Chat { Title = "Question" };
        questionChat.Messages.Add(new ChatMessage
        {
            Role = "tool",
            ToolName = "ask_question",
            ToolStatus = "InProgress",
            QuestionId = "question-1"
        });
        var visible = new Chat { Title = "Visible" };
        using var surface = CreateSurface(questionChat, visible);
        using var registry = new ChatSurfaceRegistry();
        registry.Attach(surface);
        var pendingQuestions = GetField<ConcurrentDictionary<string, TaskCompletionSource<string>>>(surface, "_pendingQuestions");
        pendingQuestions["question-1"] = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        surface.CurrentChat = visible;

        Assert.True(registry.TryGetLiveOwner(questionChat.Id, out var owner));
        Assert.Same(surface, owner);
    }

    [Fact]
    public void IsChatBusy_IgnoresModelRunningProjectionWithoutOwnedRuntime()
    {
        var chat = new Chat { Title = "Projected only" };
        using var surface = CreateSurface(chat);

        chat.IsRunning = true;

        Assert.False(surface.IsChatBusy(chat.Id));
    }

    [Fact]
    public void Detach_RemovesTrackedOwner()
    {
        var chat = new Chat { Title = "Detached" };
        using var surface = CreateSurface(chat);
        using var registry = new ChatSurfaceRegistry();
        registry.Attach(surface);

        registry.Detach(surface);

        Assert.False(registry.TryGetOwner(chat.Id, out _));
    }

    private static ChatViewModel CreateSurface(params Chat[] chats)
    {
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            },
            Chats = [.. chats]
        };

        return new ChatViewModel(new DataStore(data), new CopilotService())
        {
            CurrentChat = chats.FirstOrDefault()
        };
    }

    private static T GetField<T>(object instance, string name) where T : class
        => (T)(instance.GetType()
            .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(instance)
            ?? throw new InvalidOperationException($"Field {name} was not found."));
}
