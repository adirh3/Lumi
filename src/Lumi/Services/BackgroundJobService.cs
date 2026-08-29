using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Lumi.Models;
using Lumi.ViewModels;

namespace Lumi.Services;

internal sealed class BackgroundJobDeliveryInvalidatedException : Exception
{
}

public sealed class BackgroundJobService : IDisposable
{
    private static readonly TimeSpan MaxWallClockWaitSlice = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SchedulerRetryDelay = TimeSpan.FromSeconds(15);
    private static readonly Encoding ScriptEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly DataStore _dataStore;
    private readonly ChatSurfaceRegistry _chatSurfaceRegistry;
    private readonly bool _ownsChatSurfaceRegistry;
    private readonly ChatViewModel? _fallbackChatViewModel;
    private readonly ChatSessionStore? _chatSessionStore;
    private readonly ChatEventHub _chatEvents;
    private readonly Func<BackgroundJob, string, CancellationToken, Task>? _invokeChatOverride;
    private readonly Func<Guid, bool>? _isChatBusyOverride;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly object _chatInvocationLocksSync = new();
    private readonly Dictionary<Guid, SemaphoreSlim> _chatInvocationLocks = [];
    private readonly object _chatEventQueuesSync = new();
    private readonly Dictionary<Guid, Queue<ChatEventDelivery>> _chatEventQueues = [];
    private readonly HashSet<Guid> _activeChatEventQueueTargets = [];
    private readonly object _rescheduleSync = new();
    private CancellationTokenSource _rescheduleCts = new();
    private Task? _runnerTask;
    // Detect reschedules raised before the scheduler has attached its next wait token.
    private long _rescheduleVersion;
    private int _started;
    private int _stopping;

    public event Action? JobsChanged;

    public BackgroundJobService(DataStore dataStore, ChatViewModel chatViewModel)
        : this(dataStore, CreateSingleSurfaceRegistry(chatViewModel), chatViewModel)
    {
        _ownsChatSurfaceRegistry = true;
    }

    public BackgroundJobService(
        DataStore dataStore,
        ChatSurfaceRegistry chatSurfaceRegistry,
        ChatSessionStore chatSessionStore)
    {
        _dataStore = dataStore;
        _chatSurfaceRegistry = chatSurfaceRegistry;
        _chatSessionStore = chatSessionStore;
        _chatEvents = chatSessionStore.ChatEvents;
        _chatEvents.EventPublished += OnChatEventPublished;
    }

    public BackgroundJobService(
        DataStore dataStore,
        ChatSurfaceRegistry chatSurfaceRegistry,
        ChatViewModel fallbackChatViewModel)
    {
        _dataStore = dataStore;
        _chatSurfaceRegistry = chatSurfaceRegistry;
        _fallbackChatViewModel = fallbackChatViewModel;
        _chatEvents = fallbackChatViewModel.ChatEvents;
        _chatEvents.EventPublished += OnChatEventPublished;
    }

    internal BackgroundJobService(
        DataStore dataStore,
        ChatEventHub chatEvents,
        Func<BackgroundJob, string, CancellationToken, Task> invokeChatOverride,
        Func<Guid, bool>? isChatBusyOverride = null)
    {
        _dataStore = dataStore;
        _chatSurfaceRegistry = new ChatSurfaceRegistry();
        _ownsChatSurfaceRegistry = true;
        _chatEvents = chatEvents;
        _invokeChatOverride = invokeChatOverride;
        _isChatBusyOverride = isChatBusyOverride;
        _chatEvents.EventPublished += OnChatEventPublished;
    }

    private static ChatSurfaceRegistry CreateSingleSurfaceRegistry(ChatViewModel chatViewModel)
    {
        ArgumentNullException.ThrowIfNull(chatViewModel);
        var registry = new ChatSurfaceRegistry();
        registry.Attach(chatViewModel);
        return registry;
    }

    private ChatViewModel ResolveChatExecutor(Guid chatId)
    {
        if (_chatSurfaceRegistry.TryGetLiveOwner(chatId, out var liveSurface))
            return liveSurface;

        if (_chatSurfaceRegistry.TryGetOwner(chatId, out var visibleSurface))
            return visibleSurface;

        if (_fallbackChatViewModel is not null)
            return _fallbackChatViewModel;

        throw new InvalidOperationException($"No chat executor is available for chat {chatId}.");
    }

    private async Task<(ChatViewModel Executor, bool ReleaseWhenDone)> ResolveChatExecutorForInvocationAsync(Guid chatId)
    {
        if (_chatSurfaceRegistry.TryGetLiveOwner(chatId, out var liveSurface))
            return (liveSurface, false);

        if (_chatSurfaceRegistry.TryGetOwner(chatId, out var visibleSurface))
        {
            if (_chatSessionStore is not null)
            {
                _chatSessionStore.Retain(visibleSurface);
                return (visibleSurface, true);
            }

            return (visibleSurface, false);
        }

        if (_chatSessionStore is not null)
        {
            var chat = _dataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == chatId)
                ?? throw new InvalidOperationException($"Background job chat not found: {chatId}");
            return (await _chatSessionStore.AcquireChatAsync(chat), true);
        }

        if (_fallbackChatViewModel is not null)
            return (_fallbackChatViewModel, false);

        throw new InvalidOperationException($"No chat executor is available for chat {chatId}.");
    }

    internal ChatViewModel ResolveChatExecutorForTest(Guid chatId) => ResolveChatExecutor(chatId);

    private bool IsChatBusy(Guid chatId)
    {
        if (_chatSurfaceRegistry.TryGetLiveOwner(chatId, out var liveSurface))
            return liveSurface.IsChatBusy(chatId);

        if (_chatSurfaceRegistry.TryGetOwner(chatId, out var visibleSurface))
            return visibleSurface.IsChatBusy(chatId);

        return _fallbackChatViewModel?.IsChatBusy(chatId) == true;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

#if DEBUG
        // The automatic scheduler is intentionally disabled in Debug builds. When Lumi is
        // debugged from multiple git worktrees, every open debug window would otherwise fire
        // each scheduled job, running it many times over. Manual "Run now" (RunDueJobsNowAsync)
        // still works for testing jobs while debugging.
        return;
#else
        _runnerTask = Task.Run(RunAsync);
#endif
    }

    public async Task RunDueJobsNowAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token, cancellationToken);
        try
        {
            await RunDueJobsAsync(linkedCts.Token);
        }
        finally
        {
            Reschedule();
        }
    }

    private Task EnqueueChatEventDelivery(ChatEventDelivery delivery)
    {
        var startDrain = false;
        lock (_chatEventQueuesSync)
        {
            if (IsStopping)
            {
                delivery.Completion.TrySetCanceled();
                return delivery.Completion.Task;
            }

            if (!_chatEventQueues.TryGetValue(delivery.TargetChatId, out var queue))
            {
                queue = new Queue<ChatEventDelivery>();
                _chatEventQueues[delivery.TargetChatId] = queue;
            }

            queue.Enqueue(delivery);
            startDrain = _activeChatEventQueueTargets.Add(delivery.TargetChatId);
        }

        if (startDrain)
            _ = Task.Run(() => DrainChatEventQueueAsync(delivery.TargetChatId), CancellationToken.None);

        return delivery.Completion.Task;
    }

    private async Task DrainChatEventQueueAsync(Guid targetChatId)
    {
        while (true)
        {
            ChatEventDelivery? delivery;
            lock (_chatEventQueuesSync)
            {
                if (!_chatEventQueues.TryGetValue(targetChatId, out var queue)
                    || queue.Count == 0)
                {
                    _chatEventQueues.Remove(targetChatId);
                    _activeChatEventQueueTargets.Remove(targetChatId);
                    return;
                }

                delivery = queue.Dequeue();
            }

            try
            {
                await ExecuteChatEventDeliveryAsync(delivery, _disposeCts.Token);
                delivery.Completion.TrySetResult();
            }
            catch (OperationCanceledException) when (IsStopping)
            {
                delivery.Completion.TrySetCanceled();
            }
            catch (Exception ex)
            {
                delivery.Completion.TrySetException(ex);
            }
        }
    }

    public void Reschedule()
    {
        if (IsStopping)
            return;

        lock (_rescheduleSync)
        {
            if (IsStopping)
                return;

            var previous = _rescheduleCts;
            _rescheduleCts = new CancellationTokenSource();
            Interlocked.Increment(ref _rescheduleVersion);
            previous.Cancel();
            previous.Dispose();
        }
    }

    private void OnChatEventPublished(ChatLifecycleEvent chatEvent)
        => _ = DispatchChatEventSafelyAsync(chatEvent);

    private async Task DispatchChatEventSafelyAsync(ChatLifecycleEvent chatEvent)
    {
        try
        {
            await DispatchChatEventAsync(chatEvent, _disposeCts.Token);
        }
        catch (OperationCanceledException) when (IsStopping)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError(
                $"[BackgroundJobs] Chat event dispatch failed for {chatEvent.ChatId}/{chatEvent.EventType}: {FlattenException(ex)}");
        }
    }

    internal async Task DispatchChatEventAsync(
        ChatLifecycleEvent chatEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatEvent);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token, cancellationToken);
        var ct = linkedCts.Token;
        var deliveries = new List<Task>();
        var changed = false;

        await _scanLock.WaitAsync(ct);
        try
        {
            var jobs = _dataStore.SnapshotBackgroundJobs();
            foreach (var job in jobs)
            {
                ct.ThrowIfCancellationRequested();
                ChatEventDelivery? delivery = null;
                lock (job.SyncRoot)
                {
                    BackgroundJobSchedule.Normalize(job);
                    if (job.TriggerType != BackgroundJobTriggerTypes.ChatEvent
                        || !job.IsEnabled
                        || job.SourceChatId != chatEvent.ChatId
                        || !ChatLifecycleEventTypes.Matches(job.ChatEventTypes, chatEvent.EventType))
                    {
                        continue;
                    }

                    if (!JobHasValidChat(job))
                    {
                        job.IsEnabled = false;
                        job.NextRunAt = null;
                        job.LastRunStatus = BackgroundJobRunStatuses.Failed;
                        job.LastRunSummary = "Linked source or target chat is unavailable.";
                        job.UpdatedAt = DateTimeOffset.Now;
                        changed = true;
                        continue;
                    }

                    if (BackgroundJobSchedule.WouldCreateChatEventCycle(
                            jobs.Where(candidate => candidate.IsEnabled),
                            chatEvent.ChatId,
                            job.ChatId,
                            excludedJobId: job.Id))
                    {
                        job.IsEnabled = false;
                        job.NextRunAt = null;
                        job.LastRunStatus = BackgroundJobRunStatuses.Failed;
                        job.LastRunSummary = "Chat-event subscription was paused because it forms a trigger cycle.";
                        job.UpdatedAt = DateTimeOffset.Now;
                        changed = true;
                        continue;
                    }

                    delivery = CreateChatEventDelivery(job, chatEvent);
                }

                if (delivery is not null)
                    deliveries.Add(EnqueueChatEventDelivery(delivery));
            }
        }
        finally
        {
            _scanLock.Release();
        }

        if (changed)
            await SaveAndNotifyAsync(ct);

        if (deliveries.Count > 0)
            await Task.WhenAll(deliveries);
    }

    private static ChatEventDelivery CreateChatEventDelivery(
        BackgroundJob job,
        ChatLifecycleEvent sourceEvent)
    {
        var invocationJob = new BackgroundJob
        {
            Id = job.Id,
            ChatId = job.ChatId,
            Name = job.Name,
            Description = job.Description,
            Prompt = job.Prompt,
            TriggerType = BackgroundJobTriggerTypes.ChatEvent,
            IsEnabled = job.IsEnabled,
            IsTemporary = job.IsTemporary
        };

        return new ChatEventDelivery(
            job,
            job.ConfigurationVersion,
            job.ChatId,
            invocationJob,
            sourceEvent,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private async Task ExecuteChatEventDeliveryAsync(
        ChatEventDelivery delivery,
        CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsChatEventDeliveryStillRunnable(delivery))
                return;

            var startedAt = default(DateTimeOffset);
            lock (delivery.Job.SyncRoot)
            {
                if (!IsChatEventDeliveryConfigurationCurrent(delivery))
                    return;

                if (!delivery.Job.IsRunning)
                {
                    startedAt = DateTimeOffset.Now;
                    StartJobRun(delivery.Job, startedAt);
                }
            }

            if (startedAt != default)
            {
                await ExecuteJobAsync(
                    delivery.Job,
                    startedAt,
                    ct,
                    BuildChatEventTriggerContext(delivery.SourceEvent),
                    delivery);
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }
    }

    private async Task RunAsync()
    {
        while (!_disposeCts.IsCancellationRequested)
        {
            try
            {
                var rescheduleVersion = Volatile.Read(ref _rescheduleVersion);
                var nextRunAt = await RunDueJobsAsync(_disposeCts.Token);
                await WaitForNextScheduleAsync(nextRunAt, rescheduleVersion, _disposeCts.Token);
            }
            catch (OperationCanceledException) when (IsStopping)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Jobs changed; loop immediately to recompute the next precise wake-up.
            }
            catch (Exception ex)
            {
                Trace.TraceError($"[BackgroundJobs] Scheduler scan failed: {FlattenException(ex)}");
                try
                {
                    await Task.Delay(SchedulerRetryDelay, _disposeCts.Token);
                }
                catch (OperationCanceledException) when (IsStopping)
                {
                    return;
                }
            }
        }
    }

    private async Task<DateTimeOffset?> RunDueJobsAsync(CancellationToken ct)
    {
        await _scanLock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.Now;
            var changed = false;
            DateTimeOffset? nextRunAt = null;

            foreach (var job in _dataStore.SnapshotBackgroundJobs())
            {
                ct.ThrowIfCancellationRequested();
                var shouldQueue = false;
                lock (job.SyncRoot)
                {
                    BackgroundJobSchedule.Normalize(job);

                    if (!JobHasValidChat(job))
                    {
                        job.IsEnabled = false;
                        job.NextRunAt = null;
                        job.LastRunStatus = BackgroundJobRunStatuses.Failed;
                        job.LastRunSummary = "Linked source or target chat is unavailable.";
                        job.UpdatedAt = now;
                        changed = true;
                        continue;
                    }

                    if (TryRearmInterruptedRun(job, now))
                        changed = true;

                    if (!job.IsEnabled || job.IsRunning)
                        continue;

                    var previousNextRun = job.NextRunAt;
                    var nextRun = BackgroundJobSchedule.EnsureNextRun(job, now);
                    if (previousNextRun != job.NextRunAt)
                        changed = true;

                    if (nextRun is null)
                        continue;

                    if (nextRun > now)
                    {
                        nextRunAt = Earlier(nextRunAt, nextRun.Value);
                        continue;
                    }

                    StartJobRun(job, now);
                    shouldQueue = true;
                    changed = true;
                }

                if (shouldQueue)
                    _ = QueueJobExecution(job, now);
            }

            if (changed)
                await SaveAndNotifyAsync(ct);

            return nextRunAt;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    internal static TimeSpan? GetSchedulerDelay(DateTimeOffset? nextRunAt, DateTimeOffset now)
    {
        if (nextRunAt is null)
            return null;

        if (nextRunAt.Value <= now)
            return TimeSpan.Zero;

        var delay = nextRunAt.Value - now;
        return delay > MaxWallClockWaitSlice ? MaxWallClockWaitSlice : delay;
    }

    private async Task WaitForNextScheduleAsync(
        DateTimeOffset? nextRunAt,
        long observedRescheduleVersion,
        CancellationToken disposeToken)
    {
        while (true)
        {
            if (Volatile.Read(ref _rescheduleVersion) != observedRescheduleVersion)
                return;

            var delay = GetSchedulerDelay(nextRunAt, DateTimeOffset.Now);
            if (delay == TimeSpan.Zero)
                return;

            // .NET timers measure awake time, so long delays pause while a laptop sleeps.
            // Rechecking the wall clock in short slices catches overdue jobs promptly after resume.
            using var waitCts = CreateSchedulerWaitToken(disposeToken);
            if (Volatile.Read(ref _rescheduleVersion) != observedRescheduleVersion)
                return;
            await Task.Delay(delay ?? Timeout.InfiniteTimeSpan, waitCts.Token);
        }
    }

    private CancellationTokenSource CreateSchedulerWaitToken(CancellationToken disposeToken)
    {
        lock (_rescheduleSync)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(disposeToken, _rescheduleCts.Token);
        }
    }

    private static DateTimeOffset Earlier(DateTimeOffset? current, DateTimeOffset candidate)
        => current is null || candidate < current.Value ? candidate : current.Value;

    private bool JobHasValidChat(BackgroundJob job)
    {
        if (!_dataStore.Data.Chats.Any(chat => chat.Id == job.ChatId))
            return false;

        return job.TriggerType != BackgroundJobTriggerTypes.ChatEvent
            || job.SourceChatId is { } sourceChatId
            && sourceChatId != job.ChatId
            && _dataStore.Data.Chats.Any(chat => chat.Id == sourceChatId);
    }

    private bool IsJobStillRunnable(BackgroundJob job)
    {
        if (!_dataStore.SnapshotBackgroundJobs().Any(candidate => ReferenceEquals(candidate, job)))
            return false;

        lock (job.SyncRoot)
            return job.IsEnabled && JobHasValidChat(job);
    }

    private bool IsChatEventDeliveryStillRunnable(ChatEventDelivery delivery)
    {
        if (!_dataStore.SnapshotBackgroundJobs().Any(candidate => ReferenceEquals(candidate, delivery.Job)))
            return false;

        lock (delivery.Job.SyncRoot)
            return IsChatEventDeliveryConfigurationCurrent(delivery);
    }

    private bool IsChatEventDeliveryConfigurationCurrent(ChatEventDelivery delivery)
    {
        var job = delivery.Job;
        return job.ConfigurationVersion == delivery.ConfigurationVersion
            && job.IsEnabled
            && JobHasValidChat(job)
            && BackgroundJobSchedule.NormalizeTriggerType(job.TriggerType) == BackgroundJobTriggerTypes.ChatEvent
            && job.ChatId == delivery.TargetChatId
            && job.SourceChatId == delivery.SourceEvent.ChatId
            && ChatLifecycleEventTypes.Matches(job.ChatEventTypes, delivery.SourceEvent.EventType);
    }

    internal static bool TryRearmInterruptedRun(BackgroundJob job, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(job);

        // IsRunning is runtime-only, so an active persisted status means the prior process
        // stopped before it could finish this run.
        if (!job.IsEnabled
            || job.IsRunning
            || job.LastRunStatus is not (BackgroundJobRunStatuses.Running
                or BackgroundJobRunStatuses.Watching
                or BackgroundJobRunStatuses.Waiting))
        {
            return false;
        }

        job.NextRunAt = now;
        job.UpdatedAt = now;
        return true;
    }

    private static void StartJobRun(BackgroundJob job, DateTimeOffset startedAt)
    {
        job.IsRunning = true;
        job.LastRunStartedAt = startedAt;
        job.LastRunStatus = job.TriggerType == BackgroundJobTriggerTypes.Script
            ? BackgroundJobRunStatuses.Watching
            : BackgroundJobRunStatuses.Running;
        job.LastRunSummary = job.TriggerType switch
        {
            BackgroundJobTriggerTypes.Script => "Lumi is sleeping until this script exits.",
            BackgroundJobTriggerTypes.ChatEvent => "Matched chat event; waking the linked chat.",
            _ => "Running..."
        };
        job.NextRunAt = null;
        job.LastScriptExitCode = null;
        if (job.TriggerType == BackgroundJobTriggerTypes.Script)
            job.LastScriptOutput = "";
        job.UpdatedAt = startedAt;
    }

    private Task QueueJobExecution(
        BackgroundJob job,
        DateTimeOffset startedAt,
        string? triggerContext = null,
        ChatEventDelivery? chatEventDelivery = null)
    {
        var disposeToken = _disposeCts.Token;
        return Task.Run(async () =>
        {
            try
            {
                await ExecuteJobAsync(job, startedAt, disposeToken, triggerContext, chatEventDelivery);
            }
            catch (OperationCanceledException) when (IsStopping)
            {
            }
            catch (Exception ex)
            {
                Trace.TraceError(
                    $"[BackgroundJobs] Unhandled execution failure for '{job.Name}' ({job.Id}): {FlattenException(ex)}");
            }
        }, CancellationToken.None);
    }

    private async Task ExecuteJobAsync(
        BackgroundJob job,
        DateTimeOffset startedAt,
        CancellationToken ct,
        string? triggerContext = null,
        ChatEventDelivery? chatEventDelivery = null)
    {
        try
        {
            if (chatEventDelivery is not null)
                await SaveAndNotifyAsync(ct);

            triggerContext ??= job.TriggerType == BackgroundJobTriggerTypes.ChatEvent
                ? $"Chat-event background job was run manually at {startedAt:yyyy-MM-dd HH:mm:ss zzz}."
                : $"Scheduled background job run at {startedAt:yyyy-MM-dd HH:mm:ss zzz}.";
            ScriptTriggerResult? scriptResult = null;

            if (job.TriggerType == BackgroundJobTriggerTypes.Script)
            {
                scriptResult = await RunScriptTriggerAsync(job, startedAt, ct);
                lock (job.SyncRoot)
                {
                    job.LastScriptOutput = scriptResult.OutputPreview;
                    job.LastScriptExitCode = scriptResult.ExitCode;
                }

                if (!scriptResult.ShouldInvoke)
                {
                    CompleteRun(job, BackgroundJobRunStatuses.Skipped, scriptResult.Summary, DateTimeOffset.Now);
                    return;
                }

                triggerContext = scriptResult.Context;
            }

            if (chatEventDelivery is not null
                && !IsChatEventDeliveryStillRunnable(chatEventDelivery))
            {
                CompleteRun(
                    job,
                    BackgroundJobRunStatuses.Skipped,
                    "Job was paused, changed, or deleted before invocation.",
                    DateTimeOffset.Now,
                    chatEventDelivery);
                return;
            }

            if (chatEventDelivery is null && !JobHasValidChat(job))
                throw new InvalidOperationException("Linked chat was deleted.");

            var targetChatId = chatEventDelivery?.TargetChatId ?? job.ChatId;
            var chatInvocationLock = GetChatInvocationLock(targetChatId);
            await chatInvocationLock.WaitAsync(ct);
            try
            {
                if (!await WaitForChatAvailableAsync(job, chatEventDelivery, targetChatId, ct))
                {
                    CompleteRun(
                        job,
                        BackgroundJobRunStatuses.Skipped,
                        "Job was paused, changed, or deleted before invocation.",
                        DateTimeOffset.Now,
                        chatEventDelivery);
                    return;
                }
                if (!await InvokeChatAsync(job, triggerContext, chatEventDelivery, targetChatId, ct))
                {
                    CompleteRun(
                        job,
                        BackgroundJobRunStatuses.Skipped,
                        "Job was paused, changed, or deleted before invocation.",
                        DateTimeOffset.Now,
                        chatEventDelivery);
                    return;
                }
            }
            finally
            {
                chatInvocationLock.Release();
            }

            var summary = scriptResult is null
                ? $"Invoked Lumi in chat at {DateTimeOffset.Now:t}."
                : $"Script exited with code {scriptResult.ExitCode} and woke Lumi at {DateTimeOffset.Now:t}.";
            CompleteRun(
                job,
                BackgroundJobRunStatuses.Completed,
                summary,
                DateTimeOffset.Now,
                chatEventDelivery);
        }
        catch (OperationCanceledException) when (IsStopping)
        {
            throw;
        }
        catch (Exception ex)
        {
            var finishedAt = DateTimeOffset.Now;
            lock (job.SyncRoot)
            {
                job.LastRunAt = finishedAt;
                job.RunCount++;
                job.LastRunStatus = BackgroundJobRunStatuses.Failed;
                job.LastRunSummary = Preview(FlattenException(ex), 220);
                if (chatEventDelivery is null
                    || job.ConfigurationVersion == chatEventDelivery.ConfigurationVersion)
                {
                    job.NextRunAt = job.TriggerType == BackgroundJobTriggerTypes.Script
                        ? null
                        : BackgroundJobSchedule.ComputeNextRun(job, finishedAt, afterRun: true);
                    if (job.TriggerType == BackgroundJobTriggerTypes.Script)
                        job.IsEnabled = false;
                }
                job.UpdatedAt = finishedAt;
            }
        }
        finally
        {
            lock (job.SyncRoot)
                job.IsRunning = false;

            await SaveAndNotifyAsync(CancellationToken.None);
        }
    }

    private SemaphoreSlim GetChatInvocationLock(Guid chatId)
    {
        lock (_chatInvocationLocksSync)
        {
            if (!_chatInvocationLocks.TryGetValue(chatId, out var chatLock))
            {
                chatLock = new SemaphoreSlim(1, 1);
                _chatInvocationLocks[chatId] = chatLock;
            }

            return chatLock;
        }
    }

    private void CompleteRun(
        BackgroundJob job,
        string status,
        string summary,
        DateTimeOffset finishedAt,
        ChatEventDelivery? chatEventDelivery = null)
    {
        lock (job.SyncRoot)
        {
            job.LastRunAt = finishedAt;
            job.RunCount++;
            job.LastRunStatus = status;
            job.LastRunSummary = summary;

            var canApplyConfigurationOutcome = chatEventDelivery is null
                || job.ConfigurationVersion == chatEventDelivery.ConfigurationVersion;
            if (!canApplyConfigurationOutcome)
            {
                job.UpdatedAt = finishedAt;
                return;
            }

            if (job.TriggerType == BackgroundJobTriggerTypes.Script)
            {
                job.IsEnabled = false;
                job.NextRunAt = null;
                job.LastRunSummary = $"{summary} Wake script is complete; create or run another script job to keep watching.";
            }
            else if (job.IsTemporary && status is BackgroundJobRunStatuses.Completed)
            {
                job.IsEnabled = false;
                job.NextRunAt = null;
                job.LastRunSummary = $"{summary} Temporary job paused after this run.";
            }
            else
            {
                job.NextRunAt = BackgroundJobSchedule.ComputeNextRun(job, finishedAt, afterRun: true);
            }

            job.UpdatedAt = finishedAt;
        }
    }

    private async Task<bool> InvokeChatAsync(
        BackgroundJob job,
        string triggerContext,
        ChatEventDelivery? chatEventDelivery,
        Guid targetChatId,
        CancellationToken ct)
    {
        if (chatEventDelivery is null
            ? !IsJobStillRunnable(job)
            : !IsChatEventDeliveryStillRunnable(chatEventDelivery))
            return false;

        if (_invokeChatOverride is not null)
        {
            await _invokeChatOverride(chatEventDelivery?.InvocationJob ?? job, triggerContext, ct);
            return true;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var (executor, releaseWhenDone) = await ResolveChatExecutorForInvocationAsync(targetChatId);
                try
                {
                    if (chatEventDelivery is null
                        ? !IsJobStillRunnable(job)
                        : !IsChatEventDeliveryStillRunnable(chatEventDelivery))
                    {
                        tcs.TrySetResult(false);
                        return;
                    }

                    Action? validateDelivery = chatEventDelivery is null
                        ? null
                        : () =>
                        {
                            if (!IsChatEventDeliveryStillRunnable(chatEventDelivery))
                                throw new BackgroundJobDeliveryInvalidatedException();
                        };
                    await executor.SendBackgroundJobMessageAsync(
                        chatEventDelivery?.InvocationJob ?? job,
                        triggerContext,
                        ct,
                        validateDelivery);
                }
                finally
                {
                    if (releaseWhenDone)
                        _chatSessionStore?.Release(executor);
                }

                tcs.TrySetResult(true);
            }
            catch (BackgroundJobDeliveryInvalidatedException)
            {
                tcs.TrySetResult(false);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return await tcs.Task;
    }

    private async Task<bool> WaitForChatAvailableAsync(
        BackgroundJob job,
        ChatEventDelivery? chatEventDelivery,
        Guid targetChatId,
        CancellationToken ct)
    {
        var savedWaitingState = false;
        while (true)
        {
            if (chatEventDelivery is null
                ? !IsJobStillRunnable(job)
                : !IsChatEventDeliveryStillRunnable(chatEventDelivery))
                return false;
            if (!await IsChatBusyAsync(targetChatId, ct))
            {
                return chatEventDelivery is null
                    ? IsJobStillRunnable(job)
                    : IsChatEventDeliveryStillRunnable(chatEventDelivery);
            }

            if (!savedWaitingState)
            {
                lock (job.SyncRoot)
                {
                    job.LastRunStatus = BackgroundJobRunStatuses.Waiting;
                    job.LastRunSummary = "Linked chat is busy; waiting to wake it.";
                    job.UpdatedAt = DateTimeOffset.Now;
                }

                await SaveAndNotifyAsync(ct);
                savedWaitingState = true;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private async Task<bool> IsChatBusyAsync(Guid chatId, CancellationToken ct)
    {
        if (_isChatBusyOverride is not null)
            return _isChatBusyOverride(chatId);
        if (_invokeChatOverride is not null)
            return false;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                tcs.TrySetResult(IsChatBusy(chatId));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return await tcs.Task.WaitAsync(ct);
    }

    private async Task<ScriptTriggerResult> RunScriptTriggerAsync(BackgroundJob job, DateTimeOffset startedAt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.ScriptContent))
            return new ScriptTriggerResult(false, "Script is empty.", "", "", null);

        var language = BackgroundJobSchedule.NormalizeScriptLanguage(job.ScriptLanguage);
        var scriptPath = Path.Combine(Path.GetTempPath(), $"lumi-job-{job.Id:N}{GetScriptExtension(language)}");
        await File.WriteAllTextAsync(scriptPath, job.ScriptContent, ScriptEncoding, ct);

        try
        {
            var psi = BuildScriptProcessStartInfo(language, scriptPath);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start {language} background job script.");

            Task<string>? stdoutTask = null;
            Task<string>? stderrTask = null;
            try
            {
                stdoutTask = process.StandardOutput.ReadToEndAsync();
                stderrTask = process.StandardError.ReadToEndAsync();

                lock (job.SyncRoot)
                {
                    job.LastRunSummary = $"Watching script process {process.Id}. Lumi will wake this chat when it exits.";
                    job.UpdatedAt = DateTimeOffset.Now;
                }

                await SaveAndNotifyAsync(ct);
                await process.WaitForExitAsync(ct);

                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                var completedAt = DateTimeOffset.Now;
                return ParseScriptOutput(stdout, stderr, process.ExitCode, language, startedAt, completedAt);
            }
            catch
            {
                KillProcess(process);
                await DrainTerminatedProcessAsync(process, stdoutTask, stderrTask);
                throw;
            }
        }
        finally
        {
            try { File.Delete(scriptPath); }
            catch { /* best effort cleanup */ }
        }
    }

    private static string GetScriptExtension(string language)
    {
        return language switch
        {
            BackgroundJobScriptLanguages.Python => ".py",
            BackgroundJobScriptLanguages.Node => ".js",
            BackgroundJobScriptLanguages.Command => OperatingSystem.IsWindows() ? ".cmd" : ".sh",
            _ => ".ps1"
        };
    }

    private static ProcessStartInfo BuildScriptProcessStartInfo(string language, string scriptPath)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        switch (language)
        {
            case BackgroundJobScriptLanguages.Python:
                // macOS ships only "python3"; "python" is frequently absent on Linux too.
                psi.FileName = OperatingSystem.IsWindows() ? "python" : "python3";
                psi.ArgumentList.Add(scriptPath);
                break;
            case BackgroundJobScriptLanguages.Node:
                psi.FileName = "node";
                psi.ArgumentList.Add(scriptPath);
                break;
            case BackgroundJobScriptLanguages.Command:
                if (OperatingSystem.IsWindows())
                {
                    psi.FileName = "cmd.exe";
                    psi.ArgumentList.Add("/d");
                    psi.ArgumentList.Add("/c");
                    psi.ArgumentList.Add(scriptPath);
                }
                else
                {
                    psi.FileName = "/bin/sh";
                    psi.ArgumentList.Add(scriptPath);
                }
                break;
            default:
                psi.FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh";
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(scriptPath);
                break;
        }

        // GUI-launched Lumi has a truncated PATH on macOS/Linux; ensure job interpreters
        // (python3/node/pwsh) resolve the same way they would in the user's terminal. No-op on Windows.
        UnixShellPath.ApplyTo(psi);

        return psi;
    }

    private static void KillProcess(Process process)
    {
        if (process.HasExited)
            return;

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the HasExited check and Kill call.
        }
    }

    private static async Task DrainTerminatedProcessAsync(
        Process process,
        Task<string>? stdoutTask,
        Task<string>? stderrTask)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            if (stdoutTask is not null && stderrTask is not null)
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static ScriptTriggerResult ParseScriptOutput(
        string stdout,
        string stderr,
        int exitCode,
        string language,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        var formattedOutput = FormatScriptWakeOutput(stdout, stderr, exitCode, language, startedAt, completedAt);
        var outputPreview = Preview(formattedOutput, 1200);
        var rawOutput = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";
        var trimmed = rawOutput.Trim();
        var defaultContext = BuildScriptWakeContext(trimmed, formattedOutput, exitCode, startedAt, completedAt);

        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;
                var shouldInvoke = root.TryGetProperty("invoke", out var invokeProperty)
                                   && invokeProperty.ValueKind == JsonValueKind.True;
                var context = TryGetJsonString(root, "context")
                               ?? TryGetJsonString(root, "message")
                               ?? defaultContext;
                var reason = TryGetJsonString(root, "reason") ?? "Script did not request invocation.";
                return shouldInvoke
                    ? new ScriptTriggerResult(true, "Script requested invocation.", BuildScriptWakeContext(context, formattedOutput, exitCode, startedAt, completedAt), outputPreview, exitCode)
                    : new ScriptTriggerResult(false, reason, context, outputPreview, exitCode);
            }
            catch (JsonException)
            {
            }
        }

        var lines = trimmed
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var invokeLine = lines.FirstOrDefault(line => line.StartsWith("LUMI_INVOKE:", StringComparison.OrdinalIgnoreCase));
        if (invokeLine is not null)
        {
            var context = invokeLine["LUMI_INVOKE:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(context))
                context = defaultContext;
            return new ScriptTriggerResult(true, "Script requested invocation.", BuildScriptWakeContext(context, formattedOutput, exitCode, startedAt, completedAt), outputPreview, exitCode);
        }

        var skipLine = lines.FirstOrDefault(line => line.StartsWith("LUMI_SKIP:", StringComparison.OrdinalIgnoreCase));
        if (skipLine is not null)
        {
            var reason = skipLine["LUMI_SKIP:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(reason))
                reason = "Script exited without waking Lumi.";
            return new ScriptTriggerResult(false, reason, "", outputPreview, exitCode);
        }

        return new ScriptTriggerResult(true, $"Script exited with code {exitCode}.", defaultContext, outputPreview, exitCode);
    }

    private static string BuildScriptWakeContext(
        string context,
        string formattedOutput,
        int exitCode,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Wake script exited with code {exitCode}.");
        builder.AppendLine($"Started: {startedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Completed: {completedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine();

        if (string.IsNullOrWhiteSpace(context))
            builder.AppendLine("The script did not write output.");
        else
            builder.AppendLine(context.Trim());

        if (!string.Equals(context.Trim(), formattedOutput.Trim(), StringComparison.Ordinal))
        {
            builder.AppendLine();
            builder.AppendLine("Full script output:");
            builder.AppendLine(formattedOutput.Trim());
        }

        return builder.ToString().Trim();
    }

    private static string FormatScriptWakeOutput(
        string stdout,
        string stderr,
        int exitCode,
        string language,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Runner: {language}");
        builder.AppendLine($"Exit code: {exitCode}");
        builder.AppendLine($"Started: {startedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Completed: {completedAt:yyyy-MM-dd HH:mm:ss zzz}");

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            builder.AppendLine();
            builder.AppendLine("stdout:");
            builder.AppendLine(stdout.Trim());
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            builder.AppendLine();
            builder.AppendLine("stderr:");
            builder.AppendLine(stderr.Trim());
        }

        if (string.IsNullOrWhiteSpace(stdout) && string.IsNullOrWhiteSpace(stderr))
        {
            builder.AppendLine();
            builder.AppendLine("(no output)");
        }

        return builder.ToString().Trim();
    }

    private async Task SaveAndNotifyAsync(CancellationToken ct)
    {
        _dataStore.MarkBackgroundJobsChanged();
        try
        {
            await _dataStore.SaveAsync(ct);
        }
        finally
        {
            Reschedule();
            JobsChanged?.Invoke();
        }
    }

    private static string? TryGetJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string FlattenException(Exception ex)
    {
        var builder = new StringBuilder(ex.Message);
        var inner = ex.InnerException;
        while (inner is not null)
        {
            builder.Append(" -> ").Append(inner.Message);
            inner = inner.InnerException;
        }
        return builder.ToString();
    }

    private static string BuildChatEventTriggerContext(ChatLifecycleEvent chatEvent)
    {
        var builder = new StringBuilder()
            .Append("Chat event '")
            .Append(chatEvent.EventType)
            .Append("' occurred in source chat \"")
            .Append(chatEvent.ChatTitle)
            .Append("\" (")
            .Append(chatEvent.ChatId)
            .Append(") at ")
            .Append(chatEvent.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss zzz"))
            .Append('.');

        if (!string.IsNullOrWhiteSpace(chatEvent.Detail))
            builder.AppendLine().Append(chatEvent.Detail.Trim());

        builder.AppendLine().Append("This trigger was delivered directly from the chat event stream; no timer or polling was used.");
        return builder.ToString();
    }

    private static string Preview(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var normalized = text.Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..Math.Max(0, maxLength - 1)].TrimEnd() + "...";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 1)
            return;

        _chatEvents.EventPublished -= OnChatEventPublished;
        _disposeCts.Cancel();
        try
        {
            _runnerTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (IsStopping)
        {
        }

        _disposeCts.Dispose();
        lock (_rescheduleSync)
        {
            _rescheduleCts.Dispose();
        }

        _scanLock.Dispose();
        lock (_chatInvocationLocksSync)
        {
            foreach (var chatLock in _chatInvocationLocks.Values)
                chatLock.Dispose();
            _chatInvocationLocks.Clear();
        }
        List<ChatEventDelivery> pendingDeliveries;
        lock (_chatEventQueuesSync)
        {
            pendingDeliveries = _chatEventQueues.Values.SelectMany(static queue => queue).ToList();
            _chatEventQueues.Clear();
            _activeChatEventQueueTargets.Clear();
        }
        foreach (var delivery in pendingDeliveries)
            delivery.Completion.TrySetCanceled();
        if (_ownsChatSurfaceRegistry)
            _chatSurfaceRegistry.Dispose();
    }

    private bool IsStopping => Volatile.Read(ref _stopping) == 1;

    private sealed record ChatEventDelivery(
        BackgroundJob Job,
        long ConfigurationVersion,
        Guid TargetChatId,
        BackgroundJob InvocationJob,
        ChatLifecycleEvent SourceEvent,
        TaskCompletionSource Completion);

    private sealed record ScriptTriggerResult(
        bool ShouldInvoke,
        string Summary,
        string Context,
        string OutputPreview,
        int? ExitCode);
}
