using System.Reflection;
using System.Text.Json;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class BackgroundJobServiceSchedulingTests
{
    private const string UnavailableChatSummary = "Linked source or target chat is unavailable.";
    private static readonly DateTimeOffset PreviousUpdate = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunDueJobsNowAsync_NewOrphanIsInvalidatedOnlyOnce(bool enabled)
    {
        var job = CreateOrphan();
        job.IsEnabled = enabled;
        job.LastRunStatus = BackgroundJobRunStatuses.Idle;
        job.LastRunSummary = "";

        await AssertInvalidatedOnlyOnceAsync(new AppData { BackgroundJobs = [job] }, job);
    }

    [Theory]
    [InlineData(true, false, BackgroundJobRunStatuses.Failed, UnavailableChatSummary)]
    [InlineData(false, true, BackgroundJobRunStatuses.Failed, UnavailableChatSummary)]
    [InlineData(false, false, BackgroundJobRunStatuses.Completed, UnavailableChatSummary)]
    [InlineData(false, false, BackgroundJobRunStatuses.Failed, "Previous failure.")]
    public async Task RunDueJobsNowAsync_ChangedOrphanStateIsCorrectedOnlyOnce(
        bool enabled,
        bool staleNextRun,
        string status,
        string summary)
    {
        var job = CreateOrphan();
        job.IsEnabled = enabled;
        job.NextRunAt = staleNextRun ? PreviousUpdate.AddHours(1) : null;
        job.LastRunStatus = status;
        job.LastRunSummary = summary;

        await AssertInvalidatedOnlyOnceAsync(new AppData { BackgroundJobs = [job] }, job);
    }

    [Theory]
    [InlineData(BackgroundJobTriggerTypes.Time)]
    [InlineData(BackgroundJobTriggerTypes.Script)]
    [InlineData(BackgroundJobTriggerTypes.ChatEvent)]
    public async Task RunDueJobsNowAsync_SettledOrphanDoesNotMutateSaveOrNotify(string triggerType)
    {
        var job = CreateOrphan();
        job.TriggerType = triggerType;
        BackgroundJobSchedule.Normalize(job);
        var original = Serialize(job);
        var store = new DataStore(new AppData { BackgroundJobs = [job] });
        using var service = CreateService(store);
        var saves = 0;
        var notifications = 0;
        store.IndexSaved += () => saves++;
        service.JobsChanged += () => notifications++;

        for (var scan = 0; scan < 3; scan++)
            await service.RunDueJobsNowAsync();

        Assert.Equal(original, Serialize(job));
        Assert.Equal(PreviousUpdate, job.UpdatedAt);
        Assert.Equal(0, saves);
        Assert.Equal(0, notifications);
        Assert.Equal(0, ReadLongField(store, "_dirtyBackgroundJobsVersion"));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("source")]
    [InlineData("unspecified-source")]
    [InlineData("same-chat")]
    public async Task RunDueJobsNowAsync_InvalidChatEventLinkIsInvalidatedOnlyOnce(string invalidLink)
    {
        var source = new Chat { Title = "Synthetic source" };
        var target = new Chat { Title = "Synthetic target" };
        var job = CreateOrphan();
        job.TriggerType = BackgroundJobTriggerTypes.ChatEvent;
        job.ChatId = invalidLink == "target" ? Guid.NewGuid() : target.Id;
        job.SourceChatId = invalidLink switch
        {
            "source" => Guid.NewGuid(),
            "unspecified-source" => null,
            "same-chat" => target.Id,
            _ => source.Id
        };
        job.IsEnabled = true;
        job.LastRunStatus = BackgroundJobRunStatuses.Idle;

        await AssertInvalidatedOnlyOnceAsync(
            new AppData { Chats = [source, target], BackgroundJobs = [job] }, job);
    }

    [Fact]
    public async Task DispatchChatEventAsync_InvalidTargetRemainsSettledInLaterSchedulerScans()
    {
        var source = new Chat { Title = "Synthetic source" };
        var job = CreateOrphan();
        job.TriggerType = BackgroundJobTriggerTypes.ChatEvent;
        job.SourceChatId = source.Id;
        job.ChatEventTypes = [ChatLifecycleEventTypes.Idle];
        job.IsEnabled = true;
        var store = new DataStore(new AppData { Chats = [source], BackgroundJobs = [job] });
        using var service = CreateService(store);
        var saves = 0;
        var notifications = 0;
        store.IndexSaved += () => saves++;
        service.JobsChanged += () => notifications++;
        var chatEvent = new ChatLifecycleEvent(
            source.Id, source.Title, ChatLifecycleEventTypes.Idle, DateTimeOffset.Now);

        await service.DispatchChatEventAsync(chatEvent);
        Assert.False(job.IsEnabled);
        Assert.Equal(UnavailableChatSummary, job.LastRunSummary);
        var settled = Serialize(job);
        await service.RunDueJobsNowAsync();
        await service.DispatchChatEventAsync(chatEvent);
        await service.RunDueJobsNowAsync();

        Assert.Equal(settled, Serialize(job));
        Assert.Equal(1, saves);
        Assert.Equal(1, notifications);
        Assert.Equal(1, ReadLongField(store, "_dirtyBackgroundJobsVersion"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SchedulerScan_SettledOrphanWaitsUntilExplicitReschedule(bool newlyOrphaned)
    {
        var job = CreateOrphan();
        job.IsEnabled = newlyOrphaned;
        var store = new DataStore(new AppData { BackgroundJobs = [job] });
        using var service = CreateService(store);

        // Follow the actual loop's scan/version/wait sequence without starting an app or a worker.
        await ScanAsync(service);
        var observedVersion = ReadLongField(service, "_rescheduleVersion");
        var nextRunAt = await ScanAsync(service);
        Assert.Null(nextRunAt);
        using var waitCancellation = new CancellationTokenSource();
        var wait = (Task)InvokePrivate(service, "WaitForNextScheduleAsync",
            nextRunAt, observedVersion, waitCancellation.Token);
        try
        {
            Assert.False(wait.IsCompleted, "A settled orphan must not wake its own scheduler.");
            service.Reschedule();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => wait.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            waitCancellation.Cancel();
            try { await wait; }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task SchedulerScan_SettledOrphanPreservesLegitimateNextWake()
    {
        var chat = new Chat { Title = "Synthetic target" };
        var futureRun = DateTimeOffset.Now.AddHours(1);
        var scheduled = new BackgroundJob
        {
            ChatId = chat.Id,
            ScheduleType = BackgroundJobScheduleTypes.Once,
            RunAt = futureRun
        };
        var orphan = CreateOrphan();
        var store = new DataStore(new AppData
        {
            Chats = [chat],
            BackgroundJobs = [orphan, scheduled]
        });
        using var service = CreateService(store);
        var saves = 0;
        store.IndexSaved += () => saves++;

        Assert.Equal(futureRun, await ScanAsync(service));
        var observedVersion = ReadLongField(service, "_rescheduleVersion");
        Assert.Equal(futureRun, await ScanAsync(service));

        Assert.Equal(futureRun, scheduled.NextRunAt);
        Assert.True(scheduled.IsEnabled);
        Assert.Equal(BackgroundJobRunStatuses.Idle, scheduled.LastRunStatus);
        Assert.Equal(PreviousUpdate, orphan.UpdatedAt);
        Assert.Equal(observedVersion, ReadLongField(service, "_rescheduleVersion"));
        Assert.Equal(1, saves);
    }

    [Theory]
    [InlineData(BackgroundJobRunStatuses.Idle)]
    [InlineData(BackgroundJobRunStatuses.Running)]
    [InlineData(BackgroundJobRunStatuses.Watching)]
    [InlineData(BackgroundJobRunStatuses.Waiting)]
    public async Task SchedulerScan_DueOrInterruptedTimeJobStillExecutesOnce(string status)
    {
        var chat = new Chat { Title = "Synthetic target" };
        var job = new BackgroundJob
        {
            ChatId = chat.Id,
            ScheduleType = BackgroundJobScheduleTypes.Once,
            RunAt = status == BackgroundJobRunStatuses.Idle
                ? DateTimeOffset.Now.AddHours(-1)
                : DateTimeOffset.Now.AddHours(1),
            LastRunStatus = status,
            IsTemporary = true
        };
        var store = new DataStore(new AppData { Chats = [chat], BackgroundJobs = [job] });
        var invocations = 0;
        using var service = new BackgroundJobService(store, new ChatEventHub(), (_, _, _) =>
        {
            Interlocked.Increment(ref invocations);
            return Task.CompletedTask;
        });
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.JobsChanged += () =>
        {
            if (job.LastRunStatus == BackgroundJobRunStatuses.Completed && !job.IsRunning)
                finished.TrySetResult();
        };

        await service.RunDueJobsNowAsync();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.RunDueJobsNowAsync();

        Assert.Equal(1, invocations);
        Assert.Equal(1, job.RunCount);
        Assert.False(job.IsRunning);
        Assert.False(job.IsEnabled);
        Assert.Equal(BackgroundJobRunStatuses.Completed, job.LastRunStatus);
        Assert.Null(job.NextRunAt);
    }

    private static async Task AssertInvalidatedOnlyOnceAsync(AppData data, BackgroundJob job)
    {
        var store = new DataStore(data);
        using var service = CreateService(store);
        var savedJobs = new List<string>();
        var notifications = 0;
        store.IndexSaved += () => savedJobs.Add(Serialize(Assert.Single(store.CreateIndexSnapshot().BackgroundJobs)));
        service.JobsChanged += () => notifications++;

        await service.RunDueJobsNowAsync();

        Assert.False(job.IsEnabled);
        Assert.Null(job.NextRunAt);
        Assert.Equal(BackgroundJobRunStatuses.Failed, job.LastRunStatus);
        Assert.Equal(UnavailableChatSummary, job.LastRunSummary);
        Assert.True(job.UpdatedAt > PreviousUpdate);
        Assert.Equal(0, job.RunCount);
        Assert.Null(job.LastRunStartedAt);
        Assert.Null(job.LastRunAt);
        var settled = Serialize(job);
        Assert.Equal(settled, Assert.Single(savedJobs));
        Assert.Equal(1, notifications);
        var dirtyVersion = ReadLongField(store, "_dirtyBackgroundJobsVersion");
        Assert.True(dirtyVersion > 0);

        for (var scan = 0; scan < 3; scan++)
            await service.RunDueJobsNowAsync();

        Assert.Equal(settled, Serialize(job));
        Assert.Single(savedJobs);
        Assert.Equal(1, notifications);
        Assert.Equal(dirtyVersion, ReadLongField(store, "_dirtyBackgroundJobsVersion"));
    }

    private static BackgroundJob CreateOrphan() => new()
    {
        ChatId = Guid.NewGuid(),
        IsEnabled = false,
        LastRunStatus = BackgroundJobRunStatuses.Failed,
        LastRunSummary = UnavailableChatSummary,
        CreatedAt = PreviousUpdate,
        UpdatedAt = PreviousUpdate
    };

    private static BackgroundJobService CreateService(DataStore store)
        => new(store, new ChatEventHub(), (_, _, _) =>
            throw new InvalidOperationException("An orphan or future job must not invoke a chat."));

    private static string Serialize(BackgroundJob job)
        => JsonSerializer.Serialize(job, AppDataJsonContext.Default.BackgroundJob);

    private static Task<DateTimeOffset?> ScanAsync(BackgroundJobService service)
        => (Task<DateTimeOffset?>)InvokePrivate(service, "RunDueJobsAsync", CancellationToken.None);

    private static object InvokePrivate(object instance, string name, params object?[] arguments)
        => (instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method {name} was not found."))
            .Invoke(instance, arguments)!;

    private static long ReadLongField(object instance, string name)
        => (long)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field {name} was not found."))
            .GetValue(instance)!;
}
