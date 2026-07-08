using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

public sealed class ChatViewModelLeakTests
{
    [Fact]
    public void Dispose_UnsubscribesFromChatModel_SoDisposedSurfaceIsNotPinned()
    {
        var dataStore = CreateDataStore();
        var chat = new Chat { Title = "leaky" };
        dataStore.Data.Chats.Add(chat);

        var vm = new ChatViewModel(dataStore, new CopilotService());
        vm.CurrentChat = chat;

        // While active the surface tracks the chat's title through PropertyChanged.
        Assert.True(ChatEventReferencesTarget(chat, vm));

        vm.Dispose();

        // The chat model outlives the surface (it stays in DataStore.Data.Chats and MainViewModel keeps
        // a running-state PropertyChanged subscription on it). If Dispose leaves this handler attached,
        // the chat's event invocation list pins the entire disposed ChatViewModel — its Messages,
        // transcript turns, and realized Avalonia controls — until app shutdown.
        Assert.False(
            ChatEventReferencesTarget(chat, vm),
            "Disposed ChatViewModel is still in the chat model's PropertyChanged invocation list.");
    }

    private static bool ChatEventReferencesTarget(Chat chat, object target)
    {
        var field = typeof(Chat).GetField("PropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = field?.GetValue(chat) as PropertyChangedEventHandler;
        return handler?.GetInvocationList().Any(d => ReferenceEquals(d.Target, target)) == true;
    }

    [Fact]
    public async Task IdleCacheEviction_DisposesSurface_AndUnsubscribesFromChatModel()
    {
        var chatA = new Chat { Title = "A" };
        var chatB = new Chat { Title = "B" };
        chatA.Messages.Add(new ChatMessage { Role = "user", Content = "a" });
        chatB.Messages.Add(new ChatMessage { Role = "user", Content = "b" });

        var dataStore = new DataStore(new AppData
        {
            Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false },
            Chats = [chatA, chatB]
        });
        using var registry = new ChatSurfaceRegistry();
        using var sessionStore = new ChatSessionStore(
            dataStore,
            new CopilotService(),
            registry,
            static (surface, chat) =>
            {
                surface.CurrentChat = chat;
                return Task.CompletedTask;
            },
            maxIdleCachedSurfaces: 1);

        var surfaceA = await sessionStore.AcquireChatAsync(chatA);
        Assert.True(ChatEventReferencesTarget(chatA, surfaceA));
        sessionStore.Release(surfaceA); // A becomes idle-cached (single slot).

        // Acquiring/releasing a second chat overflows the one idle slot, evicting and disposing A
        // through the real pool lifecycle (TrimIdleCache -> UntrackSurface -> ChatViewModel.Dispose).
        var surfaceB = await sessionStore.AcquireChatAsync(chatB);
        sessionStore.Release(surfaceB);

        Assert.NotSame(surfaceA, surfaceB);
        Assert.False(
            ChatEventReferencesTarget(chatA, surfaceA),
            "Evicted+disposed surface is still subscribed to its chat model — it leaks until app shutdown.");
    }

    [Fact]
    public void ReleaseInactiveChatState_ReleasesDetachedRuntimeResourcesWithoutEvictingMessages()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var activeChat = new Chat { Title = "active" };
        var inactiveChat = new Chat { Title = "inactive" };
        inactiveChat.Messages.Add(new ChatMessage { Role = "assistant", Content = "cached" });

        dataStore.Data.Chats.Add(activeChat);
        dataStore.Data.Chats.Add(inactiveChat);
        vm.CurrentChat = activeChat;

        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[inactiveChat.Id] = new ChatRuntimeState
        {
            Chat = inactiveChat
        };
        GetField<Dictionary<Guid, CancellationTokenSource>>(vm, "_ctsSources")[inactiveChat.Id] = new CancellationTokenSource();
        var subscription = new CountingDisposable();
        GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs")[inactiveChat.Id] = subscription;
        GetField<Dictionary<Guid, ChatMessage>>(vm, "_inProgressMessages")[inactiveChat.Id] =
            new ChatMessage { Role = "assistant", Content = "streaming" };
        GetField<HashSet<Guid>>(vm, "_suggestionGenerationInFlightChats").Add(inactiveChat.Id);
        GetField<Dictionary<Guid, Guid>>(vm, "_lastSuggestedAssistantMessageByChat")[inactiveChat.Id] = Guid.NewGuid();
        GetField<ConcurrentDictionary<Guid, BrowserService>>(vm, "_chatBrowserServices")[inactiveChat.Id] = new BrowserService();

        InvokePrivate(vm, "ReleaseInactiveChatState", inactiveChat);

        Assert.Single(inactiveChat.Messages);
        Assert.Equal("cached", inactiveChat.Messages[0].Content);
        Assert.Equal(1, subscription.DisposeCount);
        Assert.False(GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates").ContainsKey(inactiveChat.Id));
        Assert.False(GetField<Dictionary<Guid, CancellationTokenSource>>(vm, "_ctsSources").ContainsKey(inactiveChat.Id));
        Assert.False(GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs").ContainsKey(inactiveChat.Id));
        Assert.False(GetField<Dictionary<Guid, ChatMessage>>(vm, "_inProgressMessages").ContainsKey(inactiveChat.Id));
        Assert.DoesNotContain(inactiveChat.Id, GetField<HashSet<Guid>>(vm, "_suggestionGenerationInFlightChats"));
        Assert.DoesNotContain(inactiveChat.Id, GetField<Dictionary<Guid, Guid>>(vm, "_lastSuggestedAssistantMessageByChat").Keys);
        // Browser sessions belong to the chat's lifetime, not its transient runtime state, so they
        // survive an inactive-state release and are reattached when the user switches back.
        Assert.True(GetField<ConcurrentDictionary<Guid, BrowserService>>(vm, "_chatBrowserServices").ContainsKey(inactiveChat.Id));
    }

    [Fact]
    public void BrowserService_SurvivesInactiveReleaseButIsDisposedOnCleanup()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var activeChat = new Chat { Title = "active" };
        var browserChat = new Chat { Title = "browser" };
        browserChat.Messages.Add(new ChatMessage { Role = "assistant", Content = "kept" });

        dataStore.Data.Chats.Add(activeChat);
        dataStore.Data.Chats.Add(browserChat);
        vm.CurrentChat = activeChat;

        var services = GetField<ConcurrentDictionary<Guid, BrowserService>>(vm, "_chatBrowserServices");
        services[browserChat.Id] = new BrowserService();
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[browserChat.Id] =
            new ChatRuntimeState { Chat = browserChat };

        // Going inactive releases the runtime state but preserves the browser session so the user
        // can return to the chat and find the browser exactly as they left it.
        InvokePrivate(vm, "ReleaseInactiveChatState", browserChat);
        Assert.False(GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates").ContainsKey(browserChat.Id));
        Assert.True(services.ContainsKey(browserChat.Id));

        // Deleting the chat tears the browser session down for good.
        vm.CleanupSession(browserChat.Id);
        Assert.False(services.ContainsKey(browserChat.Id));

        vm.Dispose();
    }

    [Fact]
    public async Task SubscribeToSession_WhenSurfaceDisposedMidSetup_ReleasesSessionInsteadOfCaching()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "raced" };
        dataStore.Data.Chats.Add(chat);

        // Reproduce the disposal race: the pool evicted+disposed this surface while a session was
        // still being created/resumed. Dispose() already swept _sessionCache; then the in-flight
        // create resolves and calls SubscribeToSession. Before the fix this re-populated the cache of
        // a dead VM, stranding the session — nothing was left to dispose it, so its host + MCP
        // subprocesses leaked forever (GC's finalizer only removes it from the client dictionary).
        SetPrivateField(vm, "_isDisposed", true);
        var stranded = CreateDetachedSession("sid-raced");

        InvokePrivate(vm, "SubscribeToSession", stranded, chat, "C:\\work");
        await DrainSessionReleaseAsync(vm, chat.Id);

        Assert.True(SessionWasDisposed(stranded));
        Assert.False(GetField<Dictionary<Guid, CopilotSession>>(vm, "_sessionCache").ContainsKey(chat.Id));
        Assert.False(GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs").ContainsKey(chat.Id));
    }

    [Fact]
    public async Task SubscribeToSession_WhenDifferentServerSessionCached_ReleasesStaleSessionBeforeReplacing()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "overwrite" };
        dataStore.Data.Chats.Add(chat);

        var stale = CreateDetachedSession("sid-old");
        GetField<Dictionary<Guid, CopilotSession>>(vm, "_sessionCache")[chat.Id] = stale;

        // The overwrite guard runs first and unconditionally for a different server session id. We
        // also flag the surface disposed so the method returns before the full event-subscription
        // body (which needs a live session); that path is covered above.
        SetPrivateField(vm, "_isDisposed", true);
        var replacement = CreateDetachedSession("sid-new");

        InvokePrivate(vm, "SubscribeToSession", replacement, chat, "C:\\work");
        await DrainSessionReleaseAsync(vm, chat.Id);

        // The stale, different-id session must be destroyed (reaping its MCP), not silently dropped
        // when the cache entry is overwritten.
        Assert.True(SessionWasDisposed(stale));
        Assert.False(GetField<Dictionary<Guid, CopilotSession>>(vm, "_sessionCache").ContainsValue(stale));
    }

    [Fact]
    public async Task SubscribeToSession_WhenSameServerSessionCached_DoesNotDestroySharedSession()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "same-id" };
        dataStore.Data.Chats.Add(chat);

        var existing = CreateDetachedSession("sid-shared");
        GetField<Dictionary<Guid, CopilotSession>>(vm, "_sessionCache")[chat.Id] = existing;

        SetPrivateField(vm, "_isDisposed", true);
        var resumedSameId = CreateDetachedSession("sid-shared");

        InvokePrivate(vm, "SubscribeToSession", resumedSameId, chat, "C:\\work");
        await DrainSessionReleaseAsync(vm, chat.Id);

        // destroy is scoped to the SERVER session id, so destroying a handle that shares the id with
        // the incoming one would tear down the very session we are about to use. The overwrite guard
        // must skip the same-id handle rather than reap it.
        Assert.False(SessionWasDisposed(existing));
    }

    [Fact]
    public void InvalidateLocalSessionCache_EvictsLocallyWithoutDestroyingResumableSession()
    {
        var dataStore = CreateDataStore();
        var service = new CopilotService();
        var vm = new ChatViewModel(dataStore, service);
        var chat = new Chat { Title = "invalidate", CopilotSessionId = "sid-inv" };
        dataStore.Data.Chats.Add(chat);

        var evicted = CreateDetachedSession("sid-inv");
        GetField<Dictionary<Guid, CopilotSession>>(vm, "_sessionCache")[chat.Id] = evicted;

        InvokePrivate(vm, "InvalidateLocalSessionCache", chat);

        // The local handle is dropped so EnsureSessionAsync re-establishes the session next send...
        Assert.False(GetField<Dictionary<Guid, CopilotSession>>(vm, "_sessionCache").ContainsKey(chat.Id));
        // ...and the id is KEPT so that next send RESUMES the SAME server session (reusing its live MCP).
        Assert.Equal("sid-inv", chat.CopilotSessionId);
        // Crucially it must NOT destroy the evicted handle: this path fires on an unhealthy/slow CLI, so a
        // destroy would (1) reap the very MCP the resume reuses and (2) hang — and because releases are
        // keyed by server session id, that hung destroy would block the destroy-before-resume gate for the
        // whole setup budget, surfacing as "MCP server connection timed out" (the bb470e8 regression).
        Assert.False(SessionWasDisposed(evicted));
        var registry = GetField<ConcurrentDictionary<string, Task>>(service, "_pendingReleasesBySessionId");
        Assert.False(registry.ContainsKey("sid-inv"));
    }

    [Fact]
    public async Task DetachPersistedSession_ReleasesDetachedSessionToReapMcp()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "detach", CopilotSessionId = "sid-det" };
        dataStore.Data.Chats.Add(chat);

        var detached = CreateDetachedSession("sid-det");
        GetField<Dictionary<Guid, CopilotSession>>(vm, "_sessionCache")[chat.Id] = detached;

        InvokePrivate(vm, "DetachPersistedSession", chat, null);
        await DrainSessionReleaseAsync(vm, chat.Id);

        Assert.True(SessionWasDisposed(detached));
        Assert.False(GetField<Dictionary<Guid, CopilotSession>>(vm, "_sessionCache").ContainsKey(chat.Id));
        // The id is cleared so the caller creates a FRESH session (new id) — no same-id resume race.
        Assert.Null(chat.CopilotSessionId);
    }

    // --- Cross-surface (cross-ChatViewModel) destroy-before-resume sequencing ---
    // Every ChatViewModel surface shares ONE CopilotService (ChatSessionStore hands the same instance
    // to each surface it creates). A session destroy started by a disposed/evicted surface leaves the
    // server session resumable, so a *different* surface can resume the same id while that destroy is
    // still in flight — and a late destroy would reap the freshly resumed live session. The fix tracks
    // releases by server session id inside the shared CopilotService and makes ResumeSessionAsync wait
    // for a matching in-flight release. These tests pin that mechanism.

    [Fact]
    public async Task ReleaseSessionAsync_DisposesSessionAndSelfCleansRegistry()
    {
        var service = new CopilotService();
        var session = CreateDetachedSession("sid-reap");

        await service.ReleaseSessionAsync(session, deleteServerSession: false);

        // The dropped session was actually disposed (destroy → reaps its host + MCP subprocesses)...
        Assert.True(SessionWasDisposed(session));
        // ...and the id-keyed registry cleaned its own entry, so it neither leaks nor falsely blocks a
        // future resume of that id once the destroy has completed.
        var registry = GetField<ConcurrentDictionary<string, Task>>(service, "_pendingReleasesBySessionId");
        Assert.False(registry.ContainsKey("sid-reap"));
    }

    [Fact]
    public async Task ResumeGate_WaitsForInFlightReleaseOfSameSessionId()
    {
        var service = new CopilotService();
        var destroyInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Simulate a destroy of session "S" still running (started by another surface being disposed).
        var registry = GetField<ConcurrentDictionary<string, Task>>(service, "_pendingReleasesBySessionId");
        registry["S"] = destroyInFlight.Task;

        // A resume of the SAME id must block until that destroy settles.
        var gate = InvokePrivate<Task>(service, "AwaitPendingReleaseAsync", "S", CancellationToken.None);
        Assert.False(gate.IsCompleted);

        destroyInFlight.SetResult();
        await gate.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(gate.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ResumeGate_DoesNotWaitForReleaseOfDifferentSessionId()
    {
        var service = new CopilotService();
        var destroyInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = GetField<ConcurrentDictionary<string, Task>>(service, "_pendingReleasesBySessionId");
        registry["S"] = destroyInFlight.Task;

        // Resuming an unrelated id must not be held up by S's release.
        var gate = InvokePrivate<Task>(service, "AwaitPendingReleaseAsync", "OTHER", CancellationToken.None);
        await gate.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(gate.IsCompletedSuccessfully);

        destroyInFlight.SetResult();
    }

    [Fact]
    public async Task ResumeGate_HonorsCancellation()
    {
        var service = new CopilotService();
        var destroyInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = GetField<ConcurrentDictionary<string, Task>>(service, "_pendingReleasesBySessionId");
        registry["S"] = destroyInFlight.Task;
        using var cts = new CancellationTokenSource();

        var gate = InvokePrivate<Task>(service, "AwaitPendingReleaseAsync", "S", cts.Token);
        cts.Cancel();

        // A hung destroy must not pin the resume forever — cancellation (e.g. the session timeout)
        // propagates so EnsureSessionAsync can fall back instead of blocking the UI.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate);

        destroyInFlight.SetResult();
    }

    [Fact]
    public async Task ResumeGate_HungRelease_StallsResumeForTheWholeBudgetThenSurfacesTimeout()
    {
        // REPRODUCTION of the acute bb470e8 regression. A destroy that hangs — a live-but-unresponsive
        // CLI, which is exactly what a 2s health-miss on InvalidateLocalSessionCache used to dispatch —
        // pins a same-id release, so ResumeSessionAsync's destroy-before-resume gate blocks the resume for
        // the entire session-setup budget and then surfaces cancellation, which EnsureSessionAsync turns
        // into the "MCP server connection timed out" TimeoutException. A short budget stands in for the
        // real 30s MCP bound so the stall is measured deterministically.
        var service = new CopilotService();
        var hungDestroy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = GetField<ConcurrentDictionary<string, Task>>(service, "_pendingReleasesBySessionId");
        registry["S"] = hungDestroy.Task; // never completes → a hung destroy of session S

        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var gate = InvokePrivate<Task>(service, "AwaitPendingReleaseAsync", "S", budget.Token);
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate);
        sw.Stop();

        // The resume did NOT proceed early — it was stalled for ~the whole budget before failing. That is
        // the stall a slow/hung same-id destroy inflicts on the next send. The fix removes the SOURCE of
        // such same-id releases on the resume path (InvalidateLocalSessionCache no longer destroys); the
        // gate itself is intentionally retained for the genuine cross-surface destroy-then-resume case.
        Assert.True(
            sw.ElapsedMilliseconds >= 300,
            $"gate returned after only {sw.ElapsedMilliseconds}ms — the resume stall was not reproduced");
        hungDestroy.SetResult();
    }

    [Fact]
    public void AllSurfacesShareOneCopilotService_SoReleaseRegistryIsGlobal()
    {
        // The cross-surface guarantee only holds if surfaces share the CopilotService whose registry
        // sequences releases. Guard that ChatSessionStore invariant so a future refactor can't silently
        // give each surface its own service (which would reopen the cross-instance race).
        var dataStore = CreateDataStore();
        var copilotService = new CopilotService();
        var registry = new ChatSurfaceRegistry();
        var store = new ChatSessionStore(dataStore, copilotService, registry);

        var surfaceA = store.AcquireDraft(projectId: null);
        var surfaceB = store.AcquireDraft(projectId: null);

        Assert.NotSame(surfaceA, surfaceB);
        Assert.Same(
            GetField<CopilotService>(surfaceA, "_copilotService"),
            GetField<CopilotService>(surfaceB, "_copilotService"));
        Assert.Same(copilotService, GetField<CopilotService>(surfaceA, "_copilotService"));

        store.Dispose();
    }

    [Fact]
    public void AcquireDraft_DoesNotDisposeReturnedDraftSurface()
    {
        // Regression: AcquireDraft seeded the draft's project context (SetDraftProjectContext ->
        // ChatViewModel.ClearProjectId) BEFORE retaining the surface. For a brand-new draft, ClearProjectId
        // raises a CurrentChat PropertyChanged (its else branch fires even when CurrentChat is null); the
        // store listens (OnSurfacePropertyChanged -> CacheOrReleaseIfIdleAndUnhosted). With CurrentChat null,
        // CanCacheIdleSurface is false, so while the draft was still unhosted (hostCount 0) the idle-release
        // path disposed it on the spot — AcquireDraft then returned a DISPOSED surface, which threw
        // ObjectDisposedException on the first send in a new chat. Uses the same public constructor (default
        // idle-cache size) as the app, so it reproduces at production settings.
        var dataStore = CreateDataStore();
        using var registry = new ChatSurfaceRegistry();
        using var store = new ChatSessionStore(dataStore, new CopilotService(), registry);

        var surface = store.AcquireDraft(projectId: null);

        var isDisposed = (bool)surface.GetType()
            .GetField("_isDisposed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(surface)!;

        Assert.False(isDisposed, "AcquireDraft must not return a disposed surface.");
    }

    [Fact]
    public void ReleaseInactiveChatState_LeavesBusyChatAttached()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var activeChat = new Chat { Title = "active" };
        var busyChat = new Chat { Title = "busy" };
        busyChat.Messages.Add(new ChatMessage { Role = "assistant", Content = "cached" });

        dataStore.Data.Chats.Add(activeChat);
        dataStore.Data.Chats.Add(busyChat);
        vm.CurrentChat = activeChat;

        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[busyChat.Id] = new ChatRuntimeState
        {
            Chat = busyChat,
            IsBusy = true
        };
        var subscription = new CountingDisposable();
        GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs")[busyChat.Id] = subscription;

        InvokePrivate(vm, "ReleaseInactiveChatState", busyChat);

        Assert.Single(busyChat.Messages);
        Assert.Equal(0, subscription.DisposeCount);
        Assert.True(GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates").ContainsKey(busyChat.Id));
        Assert.True(GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs").ContainsKey(busyChat.Id));
    }

    [Fact]
    public void ReleaseInactiveChatState_DoesNotCreateRuntimeStateForUnknownChat()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var activeChat = new Chat { Title = "active" };
        var detachedChat = new Chat { Title = "detached" };
        detachedChat.Messages.Add(new ChatMessage { Role = "assistant", Content = "cached" });

        dataStore.Data.Chats.Add(activeChat);
        dataStore.Data.Chats.Add(detachedChat);
        vm.CurrentChat = activeChat;

        InvokePrivate(vm, "ReleaseInactiveChatState", detachedChat);

        Assert.False(GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates").ContainsKey(detachedChat.Id));
    }

    [Fact]
    public void DropCompletedTurnState_RemovesStaleLiveOwnershipMarkersAfterIdle()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "completed" };
        dataStore.Data.Chats.Add(chat);

        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = new ChatRuntimeState
        {
            Chat = chat
        };
        GetField<Dictionary<Guid, CancellationTokenSource>>(vm, "_ctsSources")[chat.Id] = new CancellationTokenSource();
        GetField<Dictionary<Guid, ChatMessage>>(vm, "_inProgressMessages")[chat.Id] =
            new ChatMessage { Role = "assistant", Content = "done" };

        Assert.True(vm.OwnsLiveChat(chat.Id));

        InvokePrivate(vm, "DropCompletedTurnState", chat.Id, false);

        Assert.True(GetField<Dictionary<Guid, CancellationTokenSource>>(vm, "_ctsSources").ContainsKey(chat.Id));
        Assert.False(GetField<Dictionary<Guid, ChatMessage>>(vm, "_inProgressMessages").ContainsKey(chat.Id));
        Assert.True(vm.OwnsLiveChat(chat.Id));

        InvokePrivate(vm, "DropCompletedTurnState", chat.Id, true);

        Assert.False(GetField<Dictionary<Guid, CancellationTokenSource>>(vm, "_ctsSources").ContainsKey(chat.Id));
        Assert.False(GetField<Dictionary<Guid, ChatMessage>>(vm, "_inProgressMessages").ContainsKey(chat.Id));
        Assert.False(vm.OwnsLiveChat(chat.Id));
    }

    [Fact]
    public void CancelPendingQuestions_RemovesTrackedQuestionTasks()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "question-chat" };
        chat.Messages.Add(new ChatMessage { Role = "tool", ToolName = "ask_question", QuestionId = "q-1" });
        chat.Messages.Add(new ChatMessage { Role = "tool", ToolName = "ask_question", QuestionId = "q-2" });

        var pendingQuestions = GetField<Dictionary<string, TaskCompletionSource<string>>>(vm, "_pendingQuestions");
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingQuestions["q-1"] = first;
        pendingQuestions["q-2"] = second;

        InvokePrivate(vm, "CancelPendingQuestions", chat);

        Assert.True(first.Task.IsCanceled);
        Assert.True(second.Task.IsCanceled);
        Assert.Empty(pendingQuestions);
    }

    [Fact]
    public void IsChatBusy_ReturnsTrueWhileTurnCleanupIsPending()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "pending-chat" };

        dataStore.Data.Chats.Add(chat);
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = new ChatRuntimeState
        {
            Chat = chat,
            PendingSessionUserMessageCount = 1
        };

        Assert.True(vm.IsChatBusy(chat.Id));
    }

    [Fact]
    public void IsChatBusy_ReturnsTrueWhileToolIsStillTracked()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "tool-chat" };

        dataStore.Data.Chats.Add(chat);
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = new ChatRuntimeState
        {
            Chat = chat,
            ActiveToolCount = 1
        };

        Assert.True(vm.IsChatBusy(chat.Id));
    }

    [Fact]
    public void MarkRuntimeActive_SetsRunningFlagForPreSendWork()
    {
        var chat = new Chat { Title = "worktree-chat" };
        var runtime = new ChatRuntimeState { Chat = chat };

        InvokePrivateStatic(typeof(ChatViewModel), "MarkRuntimeActive", runtime, "Creating worktree", true, false);

        Assert.True(runtime.IsBusy);
        Assert.True(runtime.IsStreaming);
        Assert.True(chat.IsRunning);
        Assert.Equal("Creating worktree", runtime.StatusText);
    }

    [Fact]
    public void MarkRuntimeWaitingForSessionIdle_KeepsRunningWhileTurnIsTracked()
    {
        var chat = new Chat { Title = "agent-chat" };
        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true,
            IsStreaming = true,
            PendingSessionUserMessageCount = 1,
            StatusText = "Running agent"
        };

        InvokePrivateStatic(typeof(ChatViewModel), "MarkRuntimeWaitingForSessionIdle", runtime);

        Assert.True(runtime.IsBusy);
        Assert.False(runtime.IsStreaming);
        Assert.True(chat.IsRunning);
        Assert.Equal("Running agent", runtime.StatusText);
    }

    [Fact]
    public void MarkRuntimeWaitingForSessionIdle_KeepsRunningWhileBackgroundWorkIsPending()
    {
        var chat = new Chat { Title = "background-chat" };
        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsStreaming = true,
            HasPendingBackgroundWork = true
        };

        InvokePrivateStatic(typeof(ChatViewModel), "MarkRuntimeWaitingForSessionIdle", runtime);

        Assert.True(runtime.IsBusy);
        Assert.False(runtime.IsStreaming);
        Assert.True(runtime.HasPendingBackgroundWork);
        Assert.True(chat.IsRunning);
    }

    [Fact]
    public void MarkRuntimeWaitingForSessionIdle_ClearsWhenNoWorkRemains()
    {
        var chat = new Chat { Title = "complete-chat" };
        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true,
            IsStreaming = true,
            StatusText = "Finishing"
        };

        InvokePrivateStatic(typeof(ChatViewModel), "MarkRuntimeWaitingForSessionIdle", runtime);

        Assert.False(runtime.IsBusy);
        Assert.False(runtime.IsStreaming);
        Assert.False(chat.IsRunning);
        Assert.Equal("", runtime.StatusText);
    }

    [Fact]
    public void FinalizeTerminalAssistantMessage_AddsCompletedStreamingMessage()
    {
        var chat = new Chat { Title = "complete-chat" };
        var streamingMessage = new ChatMessage
        {
            Role = "assistant",
            Content = "final answer",
            IsStreaming = true
        };

        var added = ChatViewModel.FinalizeTerminalAssistantMessage(chat, streamingMessage);

        Assert.True(added);
        Assert.False(streamingMessage.IsStreaming);
        Assert.Same(streamingMessage, Assert.Single(chat.Messages));
    }

    [Fact]
    public void FinalizeTerminalAssistantMessage_DoesNotPersistEmptyStreamingMessage()
    {
        var chat = new Chat { Title = "complete-chat" };
        var streamingMessage = new ChatMessage
        {
            Role = "assistant",
            Content = "   ",
            IsStreaming = true
        };

        var added = ChatViewModel.FinalizeTerminalAssistantMessage(chat, streamingMessage);

        Assert.False(added);
        Assert.False(streamingMessage.IsStreaming);
        Assert.Empty(chat.Messages);
    }

    [Fact]
    public void FinalizeTerminalAssistantMessage_DoesNotDuplicateExistingMessage()
    {
        var chat = new Chat { Title = "complete-chat" };
        var streamingMessage = new ChatMessage
        {
            Role = "assistant",
            Content = "final answer",
            IsStreaming = true
        };
        chat.Messages.Add(streamingMessage);

        var added = ChatViewModel.FinalizeTerminalAssistantMessage(chat, streamingMessage);

        Assert.True(added);
        Assert.False(streamingMessage.IsStreaming);
        Assert.Single(chat.Messages);
    }

    [Fact]
    public void FinalizeTerminalReasoningMessage_ClearsStreamingState()
    {
         var reasoningMessage = new ChatMessage
        {
            Role = "reasoning",
            Content = "thinking",
            IsStreaming = true
        };

        ChatViewModel.FinalizeTerminalReasoningMessage(reasoningMessage);

        Assert.False(reasoningMessage.IsStreaming);
    }

    [Fact]
    public async Task SendMessage_WhenChatRuntimeActive_QueuesPromptAndClearsComposer()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "busy-chat" };

        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;
        vm.PromptText = "queued while busy";
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true
        };

        await InvokePrivateAsync(vm, "SendMessage");

        Assert.Empty(chat.Messages);
        Assert.Equal("", vm.PromptText);
        Assert.Equal(
            "queued while busy",
            GetField<Dictionary<Guid, string>>(vm, "_queuedBusySendPrompts")[chat.Id]);
    }

    [Fact]
    public async Task SendMessageCore_WhenQueuedPromptFindsRuntimeStillActive_DoesNotOverwriteDraft()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "busy-chat" };

        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;
        vm.PromptText = "new draft";
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true
        };

        await InvokePrivateAsync(vm, "SendMessageCore", "queued prompt", false);

        Assert.Empty(chat.Messages);
        Assert.Equal("new draft", vm.PromptText);
        Assert.Equal(
            "queued prompt",
            GetField<Dictionary<Guid, string>>(vm, "_queuedBusySendPrompts")[chat.Id]);
    }

    [Fact]
    public async Task DrainQueuedBusySendAsync_WhenChatChanged_PreservesQueuedPromptAsDraft()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var queuedChat = new Chat { Title = "queued-chat" };
        var visibleChat = new Chat { Title = "visible-chat" };

        dataStore.Data.Chats.Add(queuedChat);
        dataStore.Data.Chats.Add(visibleChat);
        vm.CurrentChat = visibleChat;
        GetField<Dictionary<Guid, string>>(vm, "_queuedBusySendPrompts")[queuedChat.Id] = "send me later";

        await InvokePrivateAsync(vm, "DrainQueuedBusySendAsync", queuedChat.Id);

        Assert.False(GetField<Dictionary<Guid, string>>(vm, "_queuedBusySendPrompts").ContainsKey(queuedChat.Id));
        Assert.Equal("send me later", GetField<Dictionary<Guid, string>>(vm, "_chatDrafts")[queuedChat.Id]);
        Assert.Empty(queuedChat.Messages);
    }

    [Fact]
    public void MarkInProgressToolsStopped_StopsPersistedAndLiveToolMessages()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "tool-chat" };
        var runningTool = new ChatMessage
        {
            Role = "tool",
            ToolName = "powershell",
            ToolCallId = "tool-1",
            ToolStatus = "InProgress"
        };
        var completedTool = new ChatMessage
        {
            Role = "tool",
            ToolName = "powershell",
            ToolCallId = "tool-2",
            ToolStatus = "Completed"
        };
        chat.Messages.Add(runningTool);
        chat.Messages.Add(completedTool);

        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;
        var runningVm = new ChatMessageViewModel(runningTool);
        var completedVm = new ChatMessageViewModel(completedTool);
        vm.Messages.Add(runningVm);
        vm.Messages.Add(completedVm);

        var changed = InvokePrivate<bool>(vm, "MarkInProgressToolsStopped", chat);

        Assert.True(changed);
        Assert.Equal("Stopped", runningTool.ToolStatus);
        Assert.Equal("Stopped", runningVm.ToolStatus);
        Assert.Equal("Completed", completedTool.ToolStatus);
        Assert.Equal("Completed", completedVm.ToolStatus);
    }

    [Fact]
    public void TranscriptBuilder_RendersStoppedToolAsTerminal()
    {
        var dataStore = CreateDataStore();
        dataStore.Data.Settings.ShowToolCalls = true;
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var toolMessage = new ChatMessage
        {
            Role = "tool",
            ToolName = "powershell",
            ToolCallId = "tool-1",
            ToolStatus = "Stopped",
            Content = "{\"command\":\"Start-Sleep -Seconds 45\"}"
        };

        vm.Messages.Add(new ChatMessageViewModel(toolMessage));

        var turn = Assert.Single(vm.TranscriptTurns);
        var group = Assert.IsType<ToolGroupItem>(Assert.Single(turn.Items));
        var terminal = Assert.IsType<TerminalPreviewItem>(Assert.Single(group.ToolCalls));
        Assert.False(group.IsActive);
        Assert.Equal(StrataTheme.Controls.StrataAiToolCallStatus.Stopped, terminal.Status);
    }

    [Fact]
    public void ResetAfterCopilotReconnect_ClearsTransientRuntimeState()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "recoverable-chat" };

        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;
        vm.IsBusy = true;
        vm.IsStreaming = true;
        vm.StatusText = "busy";

        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true,
            IsStreaming = true,
            HasPendingBackgroundWork = true,
            StatusText = "busy"
        };

        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = runtime;
        GetField<Dictionary<Guid, CancellationTokenSource>>(vm, "_ctsSources")[chat.Id] = new CancellationTokenSource();
        var subscription = new CountingDisposable();
        GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs")[chat.Id] = subscription;
        GetField<Dictionary<Guid, ChatMessage>>(vm, "_inProgressMessages")[chat.Id] =
            new ChatMessage { Role = "assistant", Content = "partial" };

        InvokePrivate(vm, "ResetAfterCopilotReconnect");

        Assert.Equal(1, subscription.DisposeCount);
        Assert.False(GetField<Dictionary<Guid, CancellationTokenSource>>(vm, "_ctsSources").ContainsKey(chat.Id));
        Assert.False(GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs").ContainsKey(chat.Id));
        Assert.False(GetField<Dictionary<Guid, ChatMessage>>(vm, "_inProgressMessages").ContainsKey(chat.Id));
        Assert.False(runtime.IsBusy);
        Assert.False(runtime.IsStreaming);
        Assert.False(runtime.HasPendingBackgroundWork);
        Assert.Equal("", runtime.StatusText);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsStreaming);
        Assert.Equal("", vm.StatusText);
    }

    [Fact]
    public void ReleaseInactiveChatState_CleansUpChatAfterRemoteShutdownClearsBackgroundWork()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var activeChat = new Chat { Title = "active" };
        var detachedChat = new Chat { Title = "detached", CopilotSessionId = "session-456" };
        detachedChat.Messages.Add(new ChatMessage { Role = "assistant", Content = "cached" });

        dataStore.Data.Chats.Add(activeChat);
        dataStore.Data.Chats.Add(detachedChat);
        vm.CurrentChat = activeChat;

        var runtime = new ChatRuntimeState
        {
            Chat = detachedChat,
            HasPendingBackgroundWork = true
        };
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[detachedChat.Id] = runtime;
        var subscription = new CountingDisposable();
        GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs")[detachedChat.Id] = subscription;

        InvokePrivate(vm, "DetachSessionAfterRemoteShutdown", detachedChat, false);
        InvokePrivate(vm, "ReleaseInactiveChatState", detachedChat);

        Assert.Equal(1, subscription.DisposeCount);
        Assert.False(GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates").ContainsKey(detachedChat.Id));
        Assert.Single(detachedChat.Messages);
    }

    [Fact]
    public void DetachSessionAfterRemoteShutdown_PreservesPersistedSessionId()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat
        {
            Title = "recoverable-chat",
            CopilotSessionId = "session-123"
        };

        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;
        vm.IsBusy = true;
        vm.IsStreaming = true;
        vm.StatusText = "busy";

        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true,
            IsStreaming = true,
            HasPendingBackgroundWork = true,
            StatusText = "busy"
        };
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = runtime;
        var subscription = new CountingDisposable();
        GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs")[chat.Id] = subscription;

        InvokePrivate(vm, "DetachSessionAfterRemoteShutdown", chat, true);

        Assert.Equal("session-123", chat.CopilotSessionId);
        Assert.Equal(1, subscription.DisposeCount);
        Assert.False(GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs").ContainsKey(chat.Id));
        Assert.False(runtime.IsBusy);
        Assert.False(runtime.IsStreaming);
        Assert.Equal("", runtime.StatusText);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsStreaming);
        Assert.Equal("", vm.StatusText);
    }

    [Fact]
    public void InvalidateCurrentSession_ClearsPersistedSessionId()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat
        {
            Title = "fresh-session",
            CopilotSessionId = "session-123"
        };

        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;

        InvokePrivate(vm, "InvalidateCurrentSession");

        Assert.Null(chat.CopilotSessionId);
    }

    [Fact]
    public void HandleSendError_AddsSingleTranscriptErrorItem()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "error-chat" };
        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;
        vm.IsBusy = true;
        vm.IsStreaming = true;

        InvokePrivate(
            vm,
            "HandleSendError",
            new InvalidOperationException("Copilot request failed"),
            false,
            null!,
            chat);

        Assert.Single(chat.Messages);
        Assert.Single(vm.Messages);

        var turn = Assert.Single(vm.TranscriptTurns);
        var errorItem = Assert.IsType<ErrorMessageItem>(Assert.Single(turn.Items));
        Assert.Contains("Copilot request failed", errorItem.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void HandleSendError_UnprocessableImage_SchedulesSessionResetAndOffersRetry()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "bricked-chat", CopilotSessionId = "poisoned-session" };
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "take a screenshot" });
        chat.Messages.Add(new ChatMessage { Role = "assistant", Content = "done" });
        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;
        vm.IsBusy = true;
        vm.IsStreaming = true;

        // The verbatim rejection that permanently bricked the "Sub Agent Window Bug" chat.
        InvokePrivate(
            vm,
            "HandleSendError",
            new InvalidOperationException(
                "CAPIError: 400 invalid_request_error: The image data you provided does not represent a valid image."),
            false,
            null!,
            chat);

        // The chat is flagged so the NEXT send recreates a fresh session and replays the
        // transcript as text (dropping the rejected image) instead of resuming the poisoned one.
        Assert.Contains(chat.Id, GetField<HashSet<Guid>>(vm, "_pendingSessionInvalidations"));

        // The user gets a clear, one-click-retryable affordance — not a dead-end error.
        var turn = Assert.Single(vm.TranscriptTurns);
        var errorItem = Assert.IsType<ErrorMessageItem>(Assert.Single(turn.Items));
        Assert.True(errorItem.ShowRetryButton);
        Assert.NotNull(errorItem.RetryCommand);
        Assert.Equal(Loc.Status_ImageRejectedReset, errorItem.Content);
        // The raw CAPI wording is replaced by the friendly recovery message.
        Assert.DoesNotContain("does not represent", errorItem.Content, StringComparison.OrdinalIgnoreCase);

        // The error is PERSISTED (as an error-role message) so the affordance survives a reload:
        // reopening the chat re-derives Retry from this tail via UpdateStuckChatRetryAffordance.
        var persisted = Assert.IsType<ChatMessage>(chat.Messages[^1]);
        Assert.Equal("error", persisted.Role);
        Assert.Equal(Loc.Status_ImageRejectedReset, persisted.Content);
    }

    [Fact]
    public void HandleSendError_GenericRecoverableError_SchedulesResetAndOffersRetry()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "error-chat", CopilotSessionId = "live-session" };
        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;

        InvokePrivate(
            vm,
            "HandleSendError",
            new InvalidOperationException("Copilot request failed"),
            false,
            null!,
            chat);

        // Round 2: EVERY non-fatal terminal error is recoverable by rebuilding the session from the
        // transcript as text, so a generic failure now arms a reset and offers a one-click Retry
        // (previously it was a dead end).
        Assert.Contains(chat.Id, GetField<HashSet<Guid>>(vm, "_pendingSessionInvalidations"));

        var turn = Assert.Single(vm.TranscriptTurns);
        var errorItem = Assert.IsType<ErrorMessageItem>(Assert.Single(turn.Items));
        Assert.True(errorItem.ShowRetryButton);
        Assert.NotNull(errorItem.RetryCommand);
        // The raw message is surfaced (no friendly image copy) for a non-image error.
        Assert.Contains("Copilot request failed", errorItem.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void HandleSendError_FatalError_DoesNotScheduleResetOrOfferRetry()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "fatal-chat", CopilotSessionId = "live-session" };
        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;

        // A hard limit (context window) can't be fixed by resending the same conversation, so Retry
        // would be false hope — no reset is armed and no Retry button is shown.
        InvokePrivate(
            vm,
            "HandleSendError",
            new InvalidOperationException("The context length exceeded the model's maximum."),
            false,
            null!,
            chat);

        Assert.DoesNotContain(chat.Id, GetField<HashSet<Guid>>(vm, "_pendingSessionInvalidations"));

        var turn = Assert.Single(vm.TranscriptTurns);
        var errorItem = Assert.IsType<ErrorMessageItem>(Assert.Single(turn.Items));
        Assert.False(errorItem.ShowRetryButton);
        Assert.Null(errorItem.RetryCommand);
    }

    [Fact]
    public void HandleSendError_BareAuthLogout_DoesNotScheduleResetOrOfferRetry()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "logged-out", CopilotSessionId = "live-session" };
        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;

        // A genuine logout surfaces as a plain exception with no transient backend marker, so it reaches
        // the terminal path. Retrying the same turn can't help — the user must re-authenticate — so no
        // reset is armed and no false Retry is offered.
        InvokePrivate(
            vm,
            "HandleSendError",
            new InvalidOperationException("401 Unauthorized: Bad credentials"),
            false,
            null!,
            chat);

        Assert.DoesNotContain(chat.Id, GetField<HashSet<Guid>>(vm, "_pendingSessionInvalidations"));
        var turn = Assert.Single(vm.TranscriptTurns);
        var errorItem = Assert.IsType<ErrorMessageItem>(Assert.Single(turn.Items));
        Assert.False(errorItem.ShowRetryButton);
        Assert.Null(errorItem.RetryCommand);
    }

    [Fact]
    public void HandleSendError_TerminalOverrideMessage_DoesNotOfferRetry()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "session-gone", CopilotSessionId = "live-session" };
        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;

        // A synthetic terminal override ("Start a new chat to continue.") is unrecoverable by design.
        // Its persisted "Error: {text}" carries no fatal keyword, so the affordance must NOT re-derive
        // a Retry from that lossy string — HandleSendError passes its authoritative (false) decision.
        InvokePrivate(
            vm,
            "HandleSendError",
            new InvalidOperationException("inner transport failure"),
            false,
            Loc.Status_OriginalSessionUnavailable,
            chat);

        Assert.DoesNotContain(chat.Id, GetField<HashSet<Guid>>(vm, "_pendingSessionInvalidations"));
        var turn = Assert.Single(vm.TranscriptTurns);
        var errorItem = Assert.IsType<ErrorMessageItem>(Assert.Single(turn.Items));
        Assert.False(errorItem.ShowRetryButton);
        Assert.Null(errorItem.RetryCommand);
        Assert.Equal(string.Format(Loc.Status_Error, Loc.Status_OriginalSessionUnavailable), errorItem.Content);
    }

    [Fact]
    public void HandleSendError_FatalErrorWithImageMessage_UsesPlainErrorCopyAndNoRetry()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "policy-image", CopilotSessionId = "live-session" };
        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;

        // A content-policy block whose message ALSO matches image phrasing. It is fatal (retry can't
        // help), so the "click Retry" image copy must NOT be shown and no Retry offered — the image copy
        // is gated on `recoverable`. This is the exact overlap SessionErrorEvent now mirrors.
        const string msg = "content policy violation: could not process image";
        Assert.True(CopilotService.IsUnprocessableImageError(msg));   // image phrasing matches...
        Assert.True(CopilotService.IsFatalNonRetryableError(msg));    // ...but it is fatal

        InvokePrivate(vm, "HandleSendError", new InvalidOperationException(msg), false, null!, chat);

        Assert.DoesNotContain(chat.Id, GetField<HashSet<Guid>>(vm, "_pendingSessionInvalidations"));
        var turn = Assert.Single(vm.TranscriptTurns);
        var errorItem = Assert.IsType<ErrorMessageItem>(Assert.Single(turn.Items));
        Assert.False(errorItem.ShowRetryButton);
        Assert.Null(errorItem.RetryCommand);
        // Plain "Error: {message}" — NOT the recovery-implying image copy.
        Assert.Equal(string.Format(Loc.Status_Error, msg), errorItem.Content);
        Assert.NotEqual(Loc.Status_ImageRejectedReset, errorItem.Content);
    }

    [Fact]
    public void HandleSendError_QueuesChatSave_SoErrorCardSurvivesRestart()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "persist-me", CopilotSessionId = "live-session" };
        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;

        var before = DirtyChatVersion(dataStore, chat.Id);
        InvokePrivate(vm, "HandleSendError", new InvalidOperationException("could not process image"), false, null!, chat);
        var after = DirtyChatVersion(dataStore, chat.Id);

        // HandleSendError must queue a per-chat save (MarkChatChanged bumps the dirty version) so the
        // error card it just appended is persisted — otherwise a restart before the next send drops it
        // and, for a recoverable error, the reopen path can't re-arm recovery from the missing card.
        Assert.True(after > before, $"expected a dirty-version bump; before={before} after={after}");
        Assert.Contains(chat.Id, GetField<HashSet<Guid>>(vm, "_pendingSessionInvalidations")); // recoverable → armed
    }

    private static long DirtyChatVersion(DataStore store, Guid chatId)
    {
        var field = typeof(DataStore).GetField("_dirtyChatVersions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (System.Collections.IDictionary)field.GetValue(store)!;
        return dict.Contains(chatId) ? Convert.ToInt64(dict[chatId]) : 0L;
    }

    [Fact]
    public void UpdateStuckChatRetryAffordance_AuthoritativeFatalDecision_SuppressesRetryDespiteRecoverableLookingText()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "logout-bricked", CopilotSessionId = "poisoned" };
        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;

        // A structured session.error that is fatal purely by its ErrorType (e.g. a content-policy or
        // quota rejection) but whose backend message is opaque: once persisted as plain "Error: {message}"
        // the type is gone and the generic text carries no fatal keyword, so the string heuristic alone
        // would wrongly recover. This is exactly the case the authoritative-decision param exists for.
        var err = new ChatMessage { Role = "error", Author = "Lumi", Content = "Error: The request could not be completed." };
        chat.Messages.Add(err);
        vm.Messages.Add(new ChatMessageViewModel(err)); // renders the trailing ErrorMessageItem
        Assert.False(CopilotService.IsFatalNonRetryableError(err.Content)); // text heuristic == "recoverable"

        // The live handler passes its authoritative (structured) decision — fatal — so no false Retry
        // and no needless session reset are armed, even though the persisted text looks recoverable.
        InvokePrivate(vm, "UpdateStuckChatRetryAffordance", false);

        Assert.DoesNotContain(chat.Id, GetField<HashSet<Guid>>(vm, "_pendingSessionInvalidations"));
        var turn = Assert.Single(vm.TranscriptTurns);
        var item = Assert.IsType<ErrorMessageItem>(Assert.Single(turn.Items));
        Assert.False(item.ShowRetryButton);
        Assert.Null(item.RetryCommand);
    }

    [Fact]
    public void UpdateStuckChatRetryAffordance_RecoverableDecision_ArmsResetAndOffersRetry()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "recoverable-bricked", CopilotSessionId = "poisoned" };
        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;

        var err = new ChatMessage { Role = "error", Author = "Lumi", Content = "Error: something odd happened" };
        chat.Messages.Add(err);
        vm.Messages.Add(new ChatMessageViewModel(err));

        InvokePrivate(vm, "UpdateStuckChatRetryAffordance", true);

        Assert.Contains(chat.Id, GetField<HashSet<Guid>>(vm, "_pendingSessionInvalidations"));
        var turn = Assert.Single(vm.TranscriptTurns);
        var item = Assert.IsType<ErrorMessageItem>(Assert.Single(turn.Items));
        Assert.True(item.ShowRetryButton);
        Assert.NotNull(item.RetryCommand);
    }

    [Fact]
    public void UpdateStuckChatRetryAffordance_McpSetupTimeout_OffersRetryButKeepsSessionResumable()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "mcp-timeout", CopilotSessionId = "resumable" };
        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;

        // The MCP session-SETUP timeout is recoverable (Retry is offered) but means setup was slow, not
        // that the session is poisoned — so it must NOT arm a delete + cold-recreate, which is strictly
        // slower and cascades into further timeouts. Its persisted card carries the setup-timeout phrase.
        var err = new ChatMessage
        {
            Role = "error",
            Author = "Lumi",
            Content = $"Error: {CopilotService.McpSetupTimeoutMessage}"
        };
        chat.Messages.Add(err);
        vm.Messages.Add(new ChatMessageViewModel(err));

        InvokePrivate(vm, "UpdateStuckChatRetryAffordance", true);

        // Retry is shown so the user (or the next send) can resume the SAME session cheaply...
        var turn = Assert.Single(vm.TranscriptTurns);
        var item = Assert.IsType<ErrorMessageItem>(Assert.Single(turn.Items));
        Assert.True(item.ShowRetryButton);
        Assert.NotNull(item.RetryCommand);
        // ...but NO session reset is armed, so the retry RESUMES instead of deleting + cold-creating.
        Assert.DoesNotContain(chat.Id, GetField<HashSet<Guid>>(vm, "_pendingSessionInvalidations"));
    }

    [Fact]
    public void IsMcpSetupTimeoutError_MatchesOnlyTheSetupTimeoutAndStaysRecoverable()
    {
        Assert.True(CopilotService.IsMcpSetupTimeoutError(CopilotService.McpSetupTimeoutMessage));
        Assert.True(CopilotService.IsMcpSetupTimeoutError($"Error: {CopilotService.McpSetupTimeoutMessage}"));
        Assert.False(CopilotService.IsMcpSetupTimeoutError("Session not found"));
        Assert.False(CopilotService.IsMcpSetupTimeoutError("quota exceeded"));
        Assert.False(CopilotService.IsMcpSetupTimeoutError(null));
        Assert.False(CopilotService.IsMcpSetupTimeoutError(" "));
        // It stays RECOVERABLE (Retry is offered) — it is NOT a fatal, non-retryable error.
        Assert.False(CopilotService.IsFatalNonRetryableError(CopilotService.McpSetupTimeoutMessage));
    }

    [Fact]
    public void IsCopilotTransportError_DetectsJsonRpcDisconnect()
    {
        var ex = new Exception(
            "Communication error with Copilot CLI",
            new IOException("The JSON-RPC connection with the remote party was lost before the request could complete."));

        var result = InvokePrivateStatic<bool>(typeof(ChatViewModel), "IsCopilotTransportError", ex);

        Assert.True(result);
    }

    [Fact]
    public void IsCopilotTransportError_IgnoresUnrelatedExceptions()
    {
        var ex = new InvalidOperationException("Session not found");

        var result = InvokePrivateStatic<bool>(typeof(ChatViewModel), "IsCopilotTransportError", ex);

        Assert.False(result);
    }

    [Fact]
    public void ShouldAutoResendTransportSend_WhenServerIsMissingLatestUserTurn()
    {
        IReadOnlyList<SessionEvent> events =
        [
            CreateUserMessageEvent("first")
        ];

        var analysis = PendingTurnRecoveryAnalyzer.Analyze(events, expectedSessionUserMessageCount: 2);

        Assert.False(analysis.UserMessageObserved);
    }

    [Fact]
    public void ShouldAutoResendTransportSend_WhenServerAlreadyRecordedLatestUserTurn()
    {
        IReadOnlyList<SessionEvent> events =
        [
            CreateUserMessageEvent("first"),
            CreateUserMessageEvent("second")
        ];

        var analysis = PendingTurnRecoveryAnalyzer.Analyze(events, expectedSessionUserMessageCount: 2);

        Assert.True(analysis.UserMessageObserved);
    }

    [Fact]
    public void GetRecoveredAssistantMessages_ReturnsOnlyMissingTopLevelMessages()
    {
        IReadOnlyList<SessionEvent> events =
        [
            CreateUserMessageEvent("continue"),
            CreateAssistantMessageEvent("msg-1", "First reply"),
            CreateAssistantMessageEvent("tool-1", "Tool transcript", parentToolCallId: "call-1"),
            CreateAssistantMessageEvent("msg-2", "Second reply")
        ];

        var analysis = PendingTurnRecoveryAnalyzer.Analyze(events, expectedSessionUserMessageCount: 1);
        var result = analysis.AssistantMessages.Skip(1).ToList();

        Assert.Single(result);
        Assert.Equal("Second reply", result[0].Content);
    }

    [Fact]
    public void AttachSourcesToFinalAssistantMessage_UsesOnlyLatestAssistantAfterUserTurn()
    {
        var previousAssistant = new ChatMessage { Role = "assistant", Content = "Previous answer" };
        var firstAssistant = new ChatMessage { Role = "assistant", Content = "I will look that up." };
        var finalAssistant = new ChatMessage { Role = "assistant", Content = "Here is the final answer." };
        var chat = new Chat();
        chat.Messages.Add(previousAssistant);
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "Find current info" });
        chat.Messages.Add(firstAssistant);
        chat.Messages.Add(new ChatMessage { Role = "tool", ToolName = "web_search", Content = "{}" });
        chat.Messages.Add(finalAssistant);

        var updatedMessage = InvokePrivateStaticNullable<ChatMessage>(
            typeof(ChatViewModel),
            "AttachSourcesToFinalAssistantMessage",
            chat,
            new List<SearchSource>
            {
                new()
                {
                    Title = "Example Domain",
                    Url = "https://example.com/",
                    Snippet = "Example snippet"
                }
            });

        Assert.Same(finalAssistant, updatedMessage);
        Assert.Empty(previousAssistant.Sources);
        Assert.Empty(firstAssistant.Sources);
        var source = Assert.Single(finalAssistant.Sources);
        Assert.Equal("https://example.com/", source.Url);
    }

    [Fact]
    public void AttachSourcesToFinalAssistantMessage_DoesNotLeakToPreviousTurn()
    {
        var previousAssistant = new ChatMessage { Role = "assistant", Content = "Previous answer" };
        var chat = new Chat();
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "Earlier question" });
        chat.Messages.Add(previousAssistant);
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "New question" });

        var updatedMessage = InvokePrivateStaticNullable<ChatMessage>(
            typeof(ChatViewModel),
            "AttachSourcesToFinalAssistantMessage",
            chat,
            new List<SearchSource>
            {
                new()
                {
                    Title = "Example Domain",
                    Url = "https://example.com/",
                    Snippet = "Example snippet"
                }
            });

        Assert.Null(updatedMessage);
        Assert.Empty(previousAssistant.Sources);
    }

    [Fact]
    public void BuildSessionRecoveryReplayPrompt_IncludesRetainedTranscriptAndLatestMessage()
    {
        var retainedContext = new List<ChatMessage>
        {
            new() { Role = "system", Content = "System context" },
            new() { Role = "user", Content = "Earlier question" },
            new() { Role = "assistant", Content = "Earlier answer" },
            new() { Role = "tool", Content = "Ignored tool output" }
        };

        var prompt = InvokePrivateStatic<string>(
            typeof(ChatViewModel),
            "BuildSessionRecoveryReplayPrompt",
            retainedContext,
            "Latest question");

        Assert.Contains("The previous backend chat session is unavailable.", prompt);
        Assert.Contains("System: System context", prompt);
        Assert.Contains("User: Earlier question", prompt);
        Assert.Contains("Assistant: Earlier answer", prompt);
        Assert.DoesNotContain("Ignored tool output", prompt);
        Assert.Contains("Latest user message:", prompt);
        Assert.Contains("Latest question", prompt);
    }

    [Fact]
    public void ShouldReplayTranscriptAfterSessionReset_WhenSessionIsRecreated_ReturnsTrue()
    {
        var result = InvokePrivateStatic<bool>(
            typeof(ChatViewModel),
            "ShouldReplayTranscriptAfterSessionReset",
            false,
            "session-1",
            "session-2",
            2);

        Assert.True(result);
    }

    [Fact]
    public void ShouldReplayTranscriptAfterSessionReset_WhenSessionIsReused_ReturnsFalse()
    {
        var result = InvokePrivateStatic<bool>(
            typeof(ChatViewModel),
            "ShouldReplayTranscriptAfterSessionReset",
            false,
            "session-1",
            "session-1",
            2);

        Assert.False(result);
    }

    [Fact]
    public void BuildResendPrompt_AppendsPromptAdditions_ForEditedResend()
    {
        var retainedContext = new List<ChatMessage>
        {
            new() { Role = "user", Content = "Earlier question" }
        };
        const string promptAdditions = "\n\n[Activated skill context]";

        var prompt = InvokePrivateStatic<string>(
            typeof(ChatViewModel),
            "BuildResendPrompt",
            retainedContext,
            "Edited question",
            true,
            false,
            promptAdditions);

        Assert.Contains("Latest user message (edited):", prompt);
        Assert.Contains("Edited question", prompt);
        Assert.EndsWith(promptAdditions, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildResendPrompt_AppendsPromptAdditions_ForRecoveredResend()
    {
        var retainedContext = new List<ChatMessage>
        {
            new() { Role = "assistant", Content = "Earlier answer" }
        };
        const string promptAdditions = "\n\n[Workspace skill instructions]";

        var prompt = InvokePrivateStatic<string>(
            typeof(ChatViewModel),
            "BuildResendPrompt",
            retainedContext,
            "Retry question",
            false,
            true,
            promptAdditions);

        Assert.Contains("The previous backend chat session is unavailable.", prompt);
        Assert.Contains("Retry question", prompt);
        Assert.EndsWith(promptAdditions, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PreparePendingTurnTracking_ClearsManualStopRequested()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "tracked-chat" };

        dataStore.Data.Chats.Add(chat);
        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            ManualStopRequested = true
        };
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = runtime;

        InvokePrivate(vm, "PreparePendingTurnTracking", chat, 1, 0);

        Assert.False(runtime.ManualStopRequested);
    }

    [Fact]
    public void AdjustPendingToolCount_ReconcilesWhenLastTrackedToolCompletes()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "tracked-chat" };

        dataStore.Data.Chats.Add(chat);

        InvokePrivate(vm, "PreparePendingTurnTracking", chat, 1, 0);

        var started = InvokePrivate<bool>(vm, "AdjustPendingToolCount", chat.Id, 1);
        var completed = InvokePrivate<bool>(vm, "AdjustPendingToolCount", chat.Id, -1);
        var runtime = GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id];

        Assert.False(started);
        Assert.True(completed);
        Assert.Equal(0, runtime.ActiveToolCount);
    }

    [Fact]
    public void RefreshActiveMcpSelections_RebuildsFromRenameAndDeleteWithoutStaleEntries()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat
        {
            Title = "mcp-chat",
            ActiveMcpServerNames = ["filesystem", "filesystem", "legacy", "missing"]
        };

        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;
        vm.ActiveMcpServerNames.AddRange(chat.ActiveMcpServerNames);
        vm.AvailableMcpChips.Add(new StrataTheme.Controls.StrataComposerChip("local-filesystem", "📁"));
        vm.AvailableMcpChips.Add(new StrataTheme.Controls.StrataComposerChip("workspace", "🧰"));

        var changed = InvokePrivate<bool>(
            vm,
            "RefreshActiveMcpSelections",
            new FeatureChangeResult(
                "updated",
                DataChanged: true,
                RenamedMcpOldName: "filesystem",
                RenamedMcpNewName: "local-filesystem",
                DeletedMcpName: "legacy"));

        Assert.True(changed);
        Assert.Equal(["local-filesystem"], vm.ActiveMcpServerNames);
        var chip = Assert.Single(vm.ActiveMcpChips.OfType<StrataTheme.Controls.StrataComposerChip>());
        Assert.Equal("local-filesystem", chip.Name);
        Assert.Equal(["local-filesystem"], chat.ActiveMcpServerNames);
    }

    [Fact]
    public void ConsumeManualStopRequested_ReturnsTrueOnlyOnce()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "tracked-chat" };

        dataStore.Data.Chats.Add(chat);

        InvokePrivate(vm, "SetManualStopRequested", chat.Id, true);

        var first = InvokePrivate<bool>(vm, "ConsumeManualStopRequested", chat.Id);
        var second = InvokePrivate<bool>(vm, "ConsumeManualStopRequested", chat.Id);

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public void BuildCustomAgents_IncludesActiveAgentForSessionRegistration()
    {
        var dataStore = CreateDataStore();
        var activeAgent = new LumiAgent
        {
            Name = "Active agent",
            Description = "Selected before send",
            SystemPrompt = "You are active."
        };
        var otherAgent = new LumiAgent
        {
            Name = "Other agent",
            Description = "Available in catalog",
            SystemPrompt = "You are other."
        };

        dataStore.Data.Agents.Add(activeAgent);
        dataStore.Data.Agents.Add(otherAgent);

        var vm = new ChatViewModel(dataStore, new CopilotService());
        vm.SetActiveAgent(activeAgent);

        var configs = InvokePrivate<List<CustomAgentConfig>>(vm, "BuildCustomAgents", new object[] { null! });

        Assert.Contains(configs, cfg => cfg.Name == activeAgent.Name);
        Assert.Contains(configs, cfg => cfg.Name == otherAgent.Name);
    }

    [Fact]
    public void BuildCustomAgents_RegistersDiscoveredExternalAgentsAsDelegatableSubagents()
    {
        var dataStore = CreateDataStore();
        dataStore.Data.Agents.Add(new LumiAgent
        {
            Name = "Lumi agent",
            Description = "Built-in persona",
            SystemPrompt = "You are a Lumi agent."
        });

        var vm = new ChatViewModel(dataStore, new CopilotService());

        var catalog = new ProjectContextCatalogSnapshot(
            new[] { new CopilotSkillDefinition("SomeSkill", "desc", "content", @"C:\repo\.github\skills\some\SKILL.md") },
            new[]
            {
                new CopilotAgentDefinition("WebReviewer", "Reviews web app code", "You review TypeScript code.", @"C:\repo\.github\agents\reviewer\AGENT.md"),
                new CopilotAgentDefinition("Lumi agent", "External duplicate", "External duplicate body.", @"C:\repo\.github\agents\dup\AGENT.md"),
                new CopilotAgentDefinition("Blank", "No body", "   ", @"C:\repo\.github\agents\blank\AGENT.md"),
            },
            Array.Empty<ProjectContextMcpServerDefinition>());

        var configs = InvokePrivate<List<CustomAgentConfig>>(vm, "BuildCustomAgents", new object[] { catalog });

        // External .github/agents agent becomes a delegatable subagent using its AGENT.md body as the prompt.
        var webReviewer = configs.Single(cfg => cfg.Name == "WebReviewer");
        Assert.Equal("You review TypeScript code.", webReviewer.Prompt);
        Assert.Equal("Reviews web app code", webReviewer.Description);

        // Lumi's own agent wins a name collision; the external duplicate is not added.
        var lumiMatches = configs.Where(cfg => cfg.Name == "Lumi agent").ToList();
        Assert.Single(lumiMatches);
        Assert.Equal("You are a Lumi agent.", lumiMatches[0].Prompt);

        // External agents with blank content are skipped.
        Assert.DoesNotContain(configs, cfg => cfg.Name == "Blank");
    }

    [Fact]
    public void ApplyUnexpectedAbortState_ResetsRuntimeAndDetachesCachedSession()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "abort-chat" };

        dataStore.Data.Chats.Add(chat);
        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true,
            IsStreaming = true,
            HasPendingBackgroundWork = true,
            StatusText = "busy"
        };
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = runtime;
        var subscription = new CountingDisposable();
        GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs")[chat.Id] = subscription;

        InvokePrivate(vm, "ApplyUnexpectedAbortState", chat, "Connection to Copilot was lost.", true);

        Assert.Equal(1, subscription.DisposeCount);
        Assert.False(GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs").ContainsKey(chat.Id));
        Assert.False(runtime.IsBusy);
        Assert.False(runtime.IsStreaming);
        Assert.False(runtime.HasPendingBackgroundWork);
        Assert.Equal("Connection to Copilot was lost.", runtime.StatusText);
    }

    [Fact]
    public void ApplyUnexpectedAbortState_CanSkipDisplayedChatUiCleanup()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "abort-chat" };

        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;
        vm.IsBusy = true;
        vm.IsStreaming = true;
        vm.StatusText = "busy";

        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true,
            IsStreaming = true,
            HasPendingBackgroundWork = true,
            StatusText = "busy"
        };
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = runtime;

        InvokePrivate(vm, "ApplyUnexpectedAbortState", chat, "Connection to Copilot was lost.", false);

        Assert.False(runtime.IsBusy);
        Assert.False(runtime.IsStreaming);
        Assert.False(runtime.HasPendingBackgroundWork);
        Assert.Equal("Connection to Copilot was lost.", runtime.StatusText);
        Assert.True(vm.IsBusy);
        Assert.True(vm.IsStreaming);
        Assert.Equal("busy", vm.StatusText);
    }

    [Fact]
    public void ShouldMarkBackgroundWorkPending_ReturnsTrueWhileTurnIsStillTracked()
    {
        var runtime = new ChatRuntimeState
        {
            PendingSessionUserMessageCount = 1
        };

        var shouldMark = InvokePrivateStatic<bool>(typeof(ChatViewModel), "ShouldMarkBackgroundWorkPending", runtime);

        Assert.True(shouldMark);
    }

    [Fact]
    public void ShouldMarkBackgroundWorkPending_ReturnsFalseAfterIdleCleanup()
    {
        var runtime = new ChatRuntimeState
        {
            PendingSessionUserMessageCount = 0,
            ActiveToolCount = 0,
            IsBusy = false,
            IsStreaming = false
        };

        var shouldMark = InvokePrivateStatic<bool>(typeof(ChatViewModel), "ShouldMarkBackgroundWorkPending", runtime);

        Assert.False(shouldMark);
    }

    [Fact]
    public void ShouldMarkBackgroundWorkPending_IgnoresStaleBusyAfterTurnTrackingClears()
    {
        var runtime = new ChatRuntimeState
        {
            IsBusy = true,
            IsStreaming = true
        };

        var shouldMark = InvokePrivateStatic<bool>(typeof(ChatViewModel), "ShouldMarkBackgroundWorkPending", runtime);

        Assert.False(shouldMark);
    }

    [Fact]
    public void BackgroundTasksChangedAfterIdleCleanup_DoesNotRestickPendingBackgroundWork()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "background-chat" };

        dataStore.Data.Chats.Add(chat);
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = new ChatRuntimeState
        {
            Chat = chat,
            HasPendingBackgroundWork = true
        };

        InvokePrivate(vm, "PreparePendingTurnTracking", chat, 1, 0);

        var runtime = GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id];
        runtime.IsBusy = false;
        runtime.IsStreaming = false;

        var shouldMarkBeforeIdle = InvokePrivateStatic<bool>(typeof(ChatViewModel), "ShouldMarkBackgroundWorkPending", runtime);
        Assert.True(shouldMarkBeforeIdle);

        InvokePrivate(vm, "ClearPendingTurnTracking", chat.Id);
        InvokePrivateStatic(typeof(ChatViewModel), "MarkRuntimeTerminal", runtime, null!);

        var shouldMarkAfterIdle = InvokePrivateStatic<bool>(typeof(ChatViewModel), "ShouldMarkBackgroundWorkPending", runtime);
        if (shouldMarkAfterIdle)
            runtime.HasPendingBackgroundWork = true;

        Assert.False(shouldMarkAfterIdle);
        Assert.False(runtime.HasPendingBackgroundWork);
    }

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
            typeof(ChatViewModel),
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
            typeof(ChatViewModel),
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

        var canTreatAsIdle = InvokePrivateStatic<bool>(
            typeof(ChatViewModel),
            "CanTreatCompletedTurnAsIdle",
            analysis);

        Assert.True(canTreatAsIdle);
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

        var canTreatAsIdle = InvokePrivateStatic<bool>(
            typeof(ChatViewModel),
            "CanTreatCompletedTurnAsIdle",
            analysis);

        Assert.False(canTreatAsIdle);
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

    // Builds a CopilotSession without running its constructor (which needs a live JsonRpc transport)
    // so tests can exercise Lumi's session-teardown paths. Only SessionId is set. Calling
    // DisposeAsync() on it flips the internal _isDisposed flag (before it NREs on the null transport,
    // which DisposeReleasedSessionAsync swallows), giving a direct signal that Lumi actually invoked
    // DisposeAsync() — the reap step that was missing when sessions leaked.
    private static CopilotSession CreateDetachedSession(string sessionId)
    {
        var session = (CopilotSession)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(CopilotSession));
        typeof(CopilotSession)
            .GetField("<SessionId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, sessionId);
        // This object never ran its constructor, so its finalizer (which calls RemoveFromClient on a
        // null client) would NRE and crash the test host during GC.RunFinalizers at shutdown. We drive
        // disposal explicitly in these tests, so suppress the real finalizer.
        GC.SuppressFinalize(session);
        return session;
    }

    private static bool SessionWasDisposed(CopilotSession session)
        => (int)typeof(CopilotSession)
            .GetField("_isDisposed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(session)! != 0;

    private static void SetPrivateField(object instance, string name, object? value)
        => instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(instance, value);

    private static async Task DrainSessionReleaseAsync(ChatViewModel vm, Guid chatId)
    {
        var releaseTasks = GetField<Dictionary<Guid, Task>>(vm, "_sessionReleaseTasks");
        if (releaseTasks.TryGetValue(chatId, out var release))
            await release.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static T GetField<T>(object instance, string name) where T : class
        => (T)(instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance)
            ?? throw new InvalidOperationException($"Field {name} was not found."));

    private static void InvokePrivate(object instance, string name, params object?[] args)
    {
        instance.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(instance, args);
    }

    private static T InvokePrivate<T>(object instance, string name, params object[] args)
        => (T)(instance.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(instance, args)
            ?? throw new InvalidOperationException($"Method {name} was not found."));

    private static async Task InvokePrivateAsync(object instance, string name, params object[] args)
    {
        var task = instance.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(instance, args) as Task
            ?? throw new InvalidOperationException($"Async method {name} was not found.");

        await task;
    }

    private static T InvokePrivateStatic<T>(Type type, string name, params object[] args)
        => (T)(type
            .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?.Invoke(null, args)
            ?? throw new InvalidOperationException($"Static method {name} was not found."));

    private static T? InvokePrivateStaticNullable<T>(Type type, string name, params object[] args)
        where T : class
    {
        var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Static method {name} was not found.");

        return (T?)method.Invoke(null, args);
    }

    private static void InvokePrivateStatic(Type type, string name, params object?[] args)
    {
        type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?.Invoke(null, args);
    }

    private static UserMessageEvent CreateUserMessageEvent(string content)
        => new()
        {
            Data = new UserMessageData
            {
                Content = content
            }
        };

#pragma warning disable CS0618 // ParentToolCallId is deprecated in GitHub.Copilot.SDK 1.0.1 with no replacement; test fixture mirrors the runtime sub-agent payload.
    private static AssistantMessageEvent CreateAssistantMessageEvent(
        string messageId,
        string content,
        string? parentToolCallId = null)
        => new()
        {
            Data = new AssistantMessageData
            {
                MessageId = messageId,
                Content = content,
                ParentToolCallId = parentToolCallId
            }
        };
#pragma warning restore CS0618

    private sealed class CountingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }
}
