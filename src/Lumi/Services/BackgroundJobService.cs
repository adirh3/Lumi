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

public sealed class BackgroundJobService : IDisposable
{
    private static readonly TimeSpan MaxSchedulerSleep = TimeSpan.FromHours(24);
    private static readonly Encoding ScriptEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly DataStore _dataStore;
    private readonly ChatSurfaceRegistry _chatSurfaceRegistry;
    private readonly bool _ownsChatSurfaceRegistry;
    private readonly ChatViewModel? _fallbackChatViewModel;
    private readonly ChatSessionStore? _chatSessionStore;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly object _chatInvocationLocksSync = new();
    private readonly Dictionary<Guid, SemaphoreSlim> _chatInvocationLocks = [];
    private readonly object _rescheduleSync = new();
    private CancellationTokenSource _rescheduleCts = new();
    private Task? _runnerTask;
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
    }

    public BackgroundJobService(
        DataStore dataStore,
        ChatSurfaceRegistry chatSurfaceRegistry,
        ChatViewModel fallbackChatViewModel)
    {
        _dataStore = dataStore;
        _chatSurfaceRegistry = chatSurfaceRegistry;
        _fallbackChatViewModel = fallbackChatViewModel;
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
        await RunDueJobsAsync(linkedCts.Token);
        Reschedule();
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
            previous.Cancel();
            previous.Dispose();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_disposeCts.IsCancellationRequested)
            {
                var nextRunAt = await RunDueJobsAsync(_disposeCts.Token);
                var delay = GetSchedulerDelay(nextRunAt, DateTimeOffset.Now);

                try
                {
                    await WaitForNextScheduleAsync(delay, _disposeCts.Token);
                }
                catch (OperationCanceledException) when (!IsStopping)
                {
                    // Jobs changed; loop immediately to recompute the next precise wake-up.
                }
            }
        }
        catch (OperationCanceledException) when (IsStopping)
        {
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
            var jobsToRun = new List<(BackgroundJob Job, DateTimeOffset StartedAt)>();

            foreach (var job in _dataStore.SnapshotBackgroundJobs())
            {
                ct.ThrowIfCancellationRequested();
                lock (job.SyncRoot)
                {
                    BackgroundJobSchedule.Normalize(job);

                    if (!JobHasValidChat(job))
                    {
                        job.IsEnabled = false;
                        job.NextRunAt = null;
                        job.LastRunStatus = BackgroundJobRunStatuses.Failed;
                        job.LastRunSummary = "Linked chat was deleted.";
                        job.UpdatedAt = now;
                        changed = true;
                        continue;
                    }

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
                    jobsToRun.Add((job, now));
                    changed = true;
                }
            }

            if (changed)
                await SaveAndNotifyAsync(ct);

            foreach (var (job, startedAt) in jobsToRun)
                QueueJobExecution(job, startedAt);

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
        return delay > MaxSchedulerSleep ? MaxSchedulerSleep : delay;
    }

    private async Task WaitForNextScheduleAsync(TimeSpan? delay, CancellationToken disposeToken)
    {
        if (delay == TimeSpan.Zero)
            return;

        using var waitCts = CreateSchedulerWaitToken(disposeToken);
        await Task.Delay(delay ?? Timeout.InfiniteTimeSpan, waitCts.Token);
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
        => _dataStore.Data.Chats.Any(chat => chat.Id == job.ChatId);

    private static void StartJobRun(BackgroundJob job, DateTimeOffset startedAt)
    {
        job.IsRunning = true;
        job.LastRunStartedAt = startedAt;
        job.LastRunStatus = job.TriggerType == BackgroundJobTriggerTypes.Script
            ? BackgroundJobRunStatuses.Watching
            : BackgroundJobRunStatuses.Running;
        job.LastRunSummary = job.TriggerType == BackgroundJobTriggerTypes.Script
            ? "Lumi is sleeping until this script exits."
            : "Running...";
        job.NextRunAt = null;
        job.LastScriptExitCode = null;
        if (job.TriggerType == BackgroundJobTriggerTypes.Script)
            job.LastScriptOutput = "";
        job.UpdatedAt = startedAt;
    }

    private void QueueJobExecution(BackgroundJob job, DateTimeOffset startedAt)
    {
        var disposeToken = _disposeCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteJobAsync(job, startedAt, disposeToken);
            }
            catch (OperationCanceledException) when (IsStopping)
            {
            }
        }, CancellationToken.None);
    }

    private async Task ExecuteJobAsync(BackgroundJob job, DateTimeOffset startedAt, CancellationToken ct)
    {
        try
        {
            var triggerContext = $"Scheduled background job run at {startedAt:yyyy-MM-dd HH:mm:ss zzz}.";
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

            if (!JobHasValidChat(job))
                throw new InvalidOperationException("Linked chat was deleted.");

            var chatInvocationLock = GetChatInvocationLock(job.ChatId);
            await chatInvocationLock.WaitAsync(ct);
            try
            {
                await WaitForChatAvailableAsync(job, ct);
                await InvokeChatAsync(job, triggerContext, ct);
            }
            finally
            {
                chatInvocationLock.Release();
            }

            var summary = scriptResult is null
                ? $"Invoked Lumi in chat at {DateTimeOffset.Now:t}."
                : $"Script exited with code {scriptResult.ExitCode} and woke Lumi at {DateTimeOffset.Now:t}.";
            CompleteRun(job, BackgroundJobRunStatuses.Completed, summary, DateTimeOffset.Now);
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
                job.NextRunAt = job.TriggerType == BackgroundJobTriggerTypes.Script
                    ? null
                    : BackgroundJobSchedule.ComputeNextRun(job, finishedAt, afterRun: true);
                if (job.TriggerType == BackgroundJobTriggerTypes.Script)
                    job.IsEnabled = false;
                job.UpdatedAt = finishedAt;
            }
        }
        finally
        {
            lock (job.SyncRoot)
            {
                job.IsRunning = false;
            }

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

    private void CompleteRun(BackgroundJob job, string status, string summary, DateTimeOffset finishedAt)
    {
        lock (job.SyncRoot)
        {
            job.LastRunAt = finishedAt;
            job.RunCount++;
            job.LastRunStatus = status;
            job.LastRunSummary = summary;

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

    private async Task InvokeChatAsync(BackgroundJob job, string triggerContext, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var (executor, releaseWhenDone) = await ResolveChatExecutorForInvocationAsync(job.ChatId);
                try
                {
                    await executor.SendBackgroundJobMessageAsync(job, triggerContext, ct);
                }
                finally
                {
                    if (releaseWhenDone)
                        _chatSessionStore?.Release(executor);
                }

                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        await tcs.Task;
    }

    private async Task WaitForChatAvailableAsync(BackgroundJob job, CancellationToken ct)
    {
        var savedWaitingState = false;
        while (await IsChatBusyAsync(job.ChatId, ct))
        {
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

            lock (job.SyncRoot)
            {
                job.LastRunSummary = $"Watching script process {process.Id}. Lumi will wake this chat when it exits.";
                job.UpdatedAt = DateTimeOffset.Now;
            }

            await SaveAndNotifyAsync(ct);

            string stdout;
            string stderr;
            Task<string>? stdoutTask = null;
            Task<string>? stderrTask = null;
            try
            {
                stdoutTask = process.StandardOutput.ReadToEndAsync();
                stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(ct);

                stdout = await stdoutTask;
                stderr = await stderrTask;
            }
            catch (OperationCanceledException)
            {
                KillProcess(process);
                await DrainCancelledProcessAsync(process, stdoutTask, stderrTask);
                throw;
            }

            var completedAt = DateTimeOffset.Now;
            return ParseScriptOutput(stdout, stderr, process.ExitCode, language, startedAt, completedAt);
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
                psi.FileName = "python";
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

    private static async Task DrainCancelledProcessAsync(
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
        await _dataStore.SaveAsync(ct);
        JobsChanged?.Invoke();
        Reschedule();
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
        if (_ownsChatSurfaceRegistry)
            _chatSurfaceRegistry.Dispose();
    }

    private bool IsStopping => Volatile.Read(ref _stopping) == 1;

    private sealed record ScriptTriggerResult(
        bool ShouldInvoke,
        string Summary,
        string Context,
        string OutputPreview,
        int? ExitCode);
}
