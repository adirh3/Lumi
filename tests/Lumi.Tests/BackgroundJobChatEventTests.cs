using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
#if DEBUG
using System.Reflection;
#endif
using System.Text.Json;
using Xunit;

namespace Lumi.Tests;

public sealed class BackgroundJobChatEventTests
{
    [Fact]
    public async Task DispatchChatEventAsync_MatchingEventWakesTargetChatWithoutScheduling()
    {
        var sourceChat = new Chat { Title = "Worker" };
        var targetChat = new Chat { Title = "Lead" };
        var job = CreateChatEventJob(sourceChat.Id, targetChat.Id);
        var data = new AppData
        {
            Chats = [sourceChat, targetChat],
            BackgroundJobs = [job]
        };
        var contexts = new List<string>();
        using var service = new BackgroundJobService(
            new DataStore(data),
            new ChatEventHub(),
            (_, context, _) =>
            {
                contexts.Add(context);
                return Task.CompletedTask;
            });

        await service.DispatchChatEventAsync(new ChatLifecycleEvent(
            sourceChat.Id,
            sourceChat.Title,
            ChatLifecycleEventTypes.Idle,
            new DateTimeOffset(2026, 8, 22, 15, 0, 0, TimeSpan.FromHours(3))));

        Assert.Single(contexts);
        Assert.Contains("event 'idle'", contexts[0]);
        Assert.Contains(sourceChat.Id.ToString(), contexts[0]);
        Assert.Contains("no timer or polling", contexts[0]);
        Assert.Equal(1, job.RunCount);
        Assert.Equal(BackgroundJobRunStatuses.Completed, job.LastRunStatus);
        Assert.True(job.IsEnabled);
        Assert.Null(job.NextRunAt);
    }

    [Fact]
    public async Task DispatchChatEventAsync_NonMatchingEventDoesNothing()
    {
        var sourceChat = new Chat { Title = "Worker" };
        var otherChat = new Chat { Title = "Other worker" };
        var targetChat = new Chat { Title = "Lead" };
        var job = CreateChatEventJob(sourceChat.Id, targetChat.Id);
        var data = new AppData
        {
            Chats = [sourceChat, otherChat, targetChat],
            BackgroundJobs = [job]
        };
        var invocationCount = 0;
        using var service = new BackgroundJobService(
            new DataStore(data),
            new ChatEventHub(),
            (_, _, _) =>
            {
                invocationCount++;
                return Task.CompletedTask;
            });

        await service.DispatchChatEventAsync(new ChatLifecycleEvent(
            sourceChat.Id,
            sourceChat.Title,
            ChatLifecycleEventTypes.TurnStart,
            DateTimeOffset.Now));
        await service.DispatchChatEventAsync(new ChatLifecycleEvent(
            otherChat.Id,
            otherChat.Title,
            ChatLifecycleEventTypes.Idle,
            DateTimeOffset.Now));

        Assert.Equal(0, invocationCount);
        Assert.Equal(0, job.RunCount);
        Assert.Equal(BackgroundJobRunStatuses.Idle, job.LastRunStatus);
    }

    [Fact]
    public async Task DispatchChatEventAsync_TemporarySubscriptionPausesAfterFirstMatch()
    {
        var sourceChat = new Chat { Title = "Worker" };
        var targetChat = new Chat { Title = "Lead" };
        var job = CreateChatEventJob(sourceChat.Id, targetChat.Id);
        job.IsTemporary = true;
        var data = new AppData
        {
            Chats = [sourceChat, targetChat],
            BackgroundJobs = [job]
        };
        var invocationCount = 0;
        using var service = new BackgroundJobService(
            new DataStore(data),
            new ChatEventHub(),
            (_, _, _) =>
            {
                invocationCount++;
                return Task.CompletedTask;
            });

        var chatEvent = new ChatLifecycleEvent(
            sourceChat.Id,
            sourceChat.Title,
            ChatLifecycleEventTypes.Idle,
            DateTimeOffset.Now);
        await service.DispatchChatEventAsync(chatEvent);
        await service.DispatchChatEventAsync(chatEvent);

        Assert.Equal(1, invocationCount);
        Assert.Equal(1, job.RunCount);
        Assert.False(job.IsEnabled);
        Assert.Contains("Temporary job paused", job.LastRunSummary);
    }

    [Fact]
    public void RemoveBackgroundJobsForChat_RemovesSourceSubscriptions()
    {
        var sourceChat = new Chat { Title = "Worker" };
        var targetChat = new Chat { Title = "Lead" };
        var job = CreateChatEventJob(sourceChat.Id, targetChat.Id);
        var data = new AppData
        {
            Chats = [sourceChat, targetChat],
            BackgroundJobs = [job]
        };
        var store = new DataStore(data);

        var removed = store.RemoveBackgroundJobsForChat(sourceChat.Id);

        Assert.Equal(1, removed);
        Assert.Empty(data.BackgroundJobs);
    }

    [Fact]
    public async Task DispatchChatEventAsync_QueuesDistinctMatchingEventsWhileRunning()
    {
        var sourceChat = new Chat { Title = "Worker" };
        var targetChat = new Chat { Title = "Lead" };
        var job = CreateChatEventJob(sourceChat.Id, targetChat.Id);
        job.ChatEventTypes =
            [ChatLifecycleEventTypes.TurnEnd, ChatLifecycleEventTypes.Idle, ChatLifecycleEventTypes.Error];
        var data = new AppData
        {
            Chats = [sourceChat, targetChat],
            BackgroundJobs = [job]
        };
        var firstInvocationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstInvocation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondInvocationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdInvocationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completionSaveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCompletionSave = new ManualResetEventSlim();
        var blockNextSave = 0;
        var contexts = new List<string>();
        using var service = new BackgroundJobService(
            new DataStore(data),
            new ChatEventHub(),
            async (_, context, _) =>
            {
                int invocationNumber;
                lock (contexts)
                {
                    contexts.Add(context);
                    invocationNumber = contexts.Count;
                }

                if (invocationNumber == 1)
                {
                    firstInvocationStarted.TrySetResult();
                    await releaseFirstInvocation.Task;
                }
                else if (invocationNumber == 2)
                {
                    secondInvocationCompleted.TrySetResult();
                }
                else
                {
                    thirdInvocationCompleted.TrySetResult();
                }
            });
        service.JobsChanged += () =>
        {
            if (Interlocked.CompareExchange(ref blockNextSave, 0, 1) != 1)
                return;

            completionSaveEntered.TrySetResult();
            releaseCompletionSave.Wait(TimeSpan.FromSeconds(2));
        };

        var firstDispatch = service.DispatchChatEventAsync(new ChatLifecycleEvent(
            sourceChat.Id,
            sourceChat.Title,
            ChatLifecycleEventTypes.TurnEnd,
            DateTimeOffset.Now));
        await firstInvocationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var secondDispatch = service.DispatchChatEventAsync(new ChatLifecycleEvent(
            sourceChat.Id,
            sourceChat.Title,
            ChatLifecycleEventTypes.Idle,
            DateTimeOffset.Now.AddMilliseconds(10)));
        Volatile.Write(ref blockNextSave, 1);
        releaseFirstInvocation.TrySetResult();
        await completionSaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var thirdDispatch = service.DispatchChatEventAsync(new ChatLifecycleEvent(
            sourceChat.Id,
            sourceChat.Title,
            ChatLifecycleEventTypes.Error,
            DateTimeOffset.Now.AddMilliseconds(20)));
        releaseCompletionSave.Set();

        await Task.WhenAll(firstDispatch, secondDispatch, thirdDispatch)
            .WaitAsync(TimeSpan.FromSeconds(4));
        await secondInvocationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await thirdInvocationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => job.RunCount == 3);

        Assert.Collection(
            contexts,
            context => Assert.Contains("turn_end", context),
            context => Assert.Contains("idle", context),
            context => Assert.Contains("error", context));
        Assert.Equal(3, job.RunCount);
    }

    [Fact]
    public async Task DispatchChatEventAsync_SourceDeletedWhileTargetBusy_DoesNotInvokeTarget()
    {
        var sourceChat = new Chat { Title = "Worker" };
        var targetChat = new Chat { Title = "Lead" };
        var job = CreateChatEventJob(sourceChat.Id, targetChat.Id);
        var data = new AppData
        {
            Chats = [sourceChat, targetChat],
            BackgroundJobs = [job]
        };
        var store = new DataStore(data);
        var targetBusy = true;
        var invocationCount = 0;
        using var service = new BackgroundJobService(
            store,
            new ChatEventHub(),
            (_, _, _) =>
            {
                invocationCount++;
                return Task.CompletedTask;
            },
            _ => targetBusy);

        var dispatch = service.DispatchChatEventAsync(new ChatLifecycleEvent(
            sourceChat.Id,
            sourceChat.Title,
            ChatLifecycleEventTypes.Idle,
            DateTimeOffset.Now));
        await WaitUntilAsync(() => job.LastRunStatus == BackgroundJobRunStatuses.Waiting);

        store.RemoveBackgroundJobsForChat(sourceChat.Id);
        targetBusy = false;
        await dispatch.WaitAsync(TimeSpan.FromSeconds(4));

        Assert.Equal(0, invocationCount);
        Assert.Empty(data.BackgroundJobs);
        Assert.Equal(BackgroundJobRunStatuses.Skipped, job.LastRunStatus);
    }

    [Fact]
    public async Task DispatchChatEventAsync_PreservesFifoAcrossSubscriptionsWithSameTarget()
    {
        var firstSource = new Chat { Title = "First worker" };
        var secondSource = new Chat { Title = "Second worker" };
        var targetChat = new Chat { Title = "Lead" };
        var firstJob = CreateChatEventJob(firstSource.Id, targetChat.Id);
        firstJob.Name = "First subscription";
        firstJob.ChatEventTypes = [ChatLifecycleEventTypes.TurnEnd, ChatLifecycleEventTypes.Idle];
        var secondJob = CreateChatEventJob(secondSource.Id, targetChat.Id);
        secondJob.Name = "Second subscription";
        secondJob.ChatEventTypes = [ChatLifecycleEventTypes.Error];
        var data = new AppData
        {
            Chats = [firstSource, secondSource, targetChat],
            BackgroundJobs = [firstJob, secondJob]
        };
        var firstInvocationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstInvocation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contexts = new List<string>();
        using var service = new BackgroundJobService(
            new DataStore(data),
            new ChatEventHub(),
            async (_, context, _) =>
            {
                var isFirst = false;
                lock (contexts)
                {
                    contexts.Add(context);
                    isFirst = contexts.Count == 1;
                }

                if (isFirst)
                {
                    firstInvocationStarted.TrySetResult();
                    await releaseFirstInvocation.Task;
                }
            });

        var firstDispatch = service.DispatchChatEventAsync(new ChatLifecycleEvent(
            firstSource.Id,
            firstSource.Title,
            ChatLifecycleEventTypes.TurnEnd,
            DateTimeOffset.Now));
        await firstInvocationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var secondDispatch = service.DispatchChatEventAsync(new ChatLifecycleEvent(
            firstSource.Id,
            firstSource.Title,
            ChatLifecycleEventTypes.Idle,
            DateTimeOffset.Now.AddMilliseconds(10)));
        var thirdDispatch = service.DispatchChatEventAsync(new ChatLifecycleEvent(
            secondSource.Id,
            secondSource.Title,
            ChatLifecycleEventTypes.Error,
            DateTimeOffset.Now.AddMilliseconds(20)));
        releaseFirstInvocation.TrySetResult();

        await Task.WhenAll(firstDispatch, secondDispatch, thirdDispatch)
            .WaitAsync(TimeSpan.FromSeconds(4));

        Assert.Collection(
            contexts,
            context =>
            {
                Assert.Contains("First worker", context);
                Assert.Contains("turn_end", context);
            },
            context =>
            {
                Assert.Contains("First worker", context);
                Assert.Contains("idle", context);
            },
            context =>
            {
                Assert.Contains("Second worker", context);
                Assert.Contains("error", context);
            });
    }

    [Fact]
    public async Task DispatchChatEventAsync_ReconfiguredWaitingDelivery_DoesNotRedirect()
    {
        var sourceChat = new Chat { Title = "Worker" };
        var originalTarget = new Chat { Title = "Original lead" };
        var replacementTarget = new Chat { Title = "Replacement lead" };
        var job = CreateChatEventJob(sourceChat.Id, originalTarget.Id);
        var data = new AppData
        {
            Chats = [sourceChat, originalTarget, replacementTarget],
            BackgroundJobs = [job]
        };
        var store = new DataStore(data);
        var manager = new LumiFeatureManager(store);
        var originalTargetBusy = true;
        var invocations = new List<BackgroundJob>();
        using var service = new BackgroundJobService(
            store,
            new ChatEventHub(),
            (invocationJob, _, _) =>
            {
                invocations.Add(invocationJob);
                return Task.CompletedTask;
            },
            chatId => chatId == originalTarget.Id && originalTargetBusy);

        var staleDispatch = service.DispatchChatEventAsync(new ChatLifecycleEvent(
            sourceChat.Id,
            sourceChat.Title,
            ChatLifecycleEventTypes.Idle,
            DateTimeOffset.Now));
        await WaitUntilAsync(() => job.LastRunStatus == BackgroundJobRunStatuses.Waiting);

        var update = manager.ManageJobs(
            "update",
            identifier: job.Name,
            prompt: "Use the replacement target.",
            chatIdentifier: replacementTarget.Id.ToString());
        Assert.True(update.DataChanged);
        originalTargetBusy = false;
        await staleDispatch.WaitAsync(TimeSpan.FromSeconds(4));

        Assert.Empty(invocations);
        Assert.Equal(replacementTarget.Id, job.ChatId);
        Assert.Equal("Use the replacement target.", job.Prompt);

        await service.DispatchChatEventAsync(new ChatLifecycleEvent(
            sourceChat.Id,
            sourceChat.Title,
            ChatLifecycleEventTypes.Idle,
            DateTimeOffset.Now.AddSeconds(1)));

        var invocation = Assert.Single(invocations);
        Assert.Equal(replacementTarget.Id, invocation.ChatId);
        Assert.Equal("Use the replacement target.", invocation.Prompt);
    }

    [Fact]
    public async Task DispatchChatEventAsync_PauseResumeInvalidatesWaitingDelivery()
    {
        var sourceChat = new Chat { Title = "Worker" };
        var targetChat = new Chat { Title = "Lead" };
        var job = CreateChatEventJob(sourceChat.Id, targetChat.Id);
        var data = new AppData
        {
            Chats = [sourceChat, targetChat],
            BackgroundJobs = [job]
        };
        var store = new DataStore(data);
        var manager = new LumiFeatureManager(store);
        var targetBusy = true;
        var invocationCount = 0;
        using var service = new BackgroundJobService(
            store,
            new ChatEventHub(),
            (_, _, _) =>
            {
                invocationCount++;
                return Task.CompletedTask;
            },
            _ => targetBusy);

        var staleDispatch = service.DispatchChatEventAsync(new ChatLifecycleEvent(
            sourceChat.Id,
            sourceChat.Title,
            ChatLifecycleEventTypes.Idle,
            DateTimeOffset.Now));
        await WaitUntilAsync(() => job.LastRunStatus == BackgroundJobRunStatuses.Waiting);

        Assert.True(manager.ManageJobs("pause", identifier: job.Name).DataChanged);
        Assert.True(manager.ManageJobs("resume", identifier: job.Name).DataChanged);
        targetBusy = false;
        await staleDispatch.WaitAsync(TimeSpan.FromSeconds(4));

        Assert.Equal(0, invocationCount);

        await service.DispatchChatEventAsync(new ChatLifecycleEvent(
            sourceChat.Id,
            sourceChat.Title,
            ChatLifecycleEventTypes.Idle,
            DateTimeOffset.Now.AddSeconds(1)));
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task SendBackgroundJobMessageAsync_InvalidatedDeliveryStopsBeforeTranscriptMutation()
    {
        var targetChat = new Chat { Title = "Lead" };
        var data = new AppData { Chats = [targetChat] };
        using var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
        var job = new BackgroundJob
        {
            ChatId = targetChat.Id,
            Name = "Invalidated",
            Prompt = "Do not send this."
        };

        await Assert.ThrowsAsync<BackgroundJobDeliveryInvalidatedException>(() =>
            viewModel.SendBackgroundJobMessageAsync(
                job,
                "stale",
                validateDelivery: static () => throw new BackgroundJobDeliveryInvalidatedException()));

        Assert.Empty(targetChat.Messages);
    }

#if DEBUG
    [Fact]
    public void DebugJobSummary_IncludesChatEventSourceAndFilters()
    {
        var sourceChatId = Guid.NewGuid();
        var job = CreateChatEventJob(sourceChatId, Guid.NewGuid());
        job.ChatEventTypes = [ChatLifecycleEventTypes.TurnEnd, ChatLifecycleEventTypes.Idle];
        var method = typeof(LumiDebugBridge).GetMethod(
            "JobSummary",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("JobSummary was not found.");

        var summary = method.Invoke(null, [job])
            ?? throw new InvalidOperationException("JobSummary returned null.");
        var json = JsonSerializer.SerializeToElement(
            summary,
            summary.GetType(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(sourceChatId, json.GetProperty("sourceChatId").GetGuid());
        Assert.Equal("turn_end", json.GetProperty("chatEventTypes")[0].GetString());
        Assert.Equal("idle", json.GetProperty("chatEventTypes")[1].GetString());
    }
#endif

    private static BackgroundJob CreateChatEventJob(Guid sourceChatId, Guid targetChatId)
    {
        return new BackgroundJob
        {
            ChatId = targetChatId,
            SourceChatId = sourceChatId,
            Name = "Worker completion",
            Prompt = "Review the worker result.",
            TriggerType = BackgroundJobTriggerTypes.ChatEvent,
            ChatEventTypes = [ChatLifecycleEventTypes.Idle],
            IsEnabled = true
        };
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= timeoutAt)
                throw new TimeoutException("Condition was not met before timeout.");
            await Task.Delay(10);
        }
    }
}
