using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;

namespace Lumi.Services;

/// <summary>Runs safe once-per-day default-branch synchronization for opted-in coding projects.</summary>
public sealed class ProjectGitSyncService : IAsyncDisposable
{
    private static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromHours(1);

    private readonly DataStore _dataStore;
    private readonly Func<string, CancellationToken, Task<GitBranchSyncResult>> _syncBranchAsync;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _checkInterval;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private Task? _loopTask;
    private int _started;
    private int _disposed;

    public ProjectGitSyncService(DataStore dataStore)
        : this(dataStore, GitService.SyncDefaultBranchAsync, static () => DateTimeOffset.Now, DefaultCheckInterval)
    {
    }

    internal ProjectGitSyncService(
        DataStore dataStore,
        Func<string, CancellationToken, Task<GitBranchSyncResult>> syncBranchAsync,
        Func<DateTimeOffset> now,
        TimeSpan checkInterval)
    {
        _dataStore = dataStore;
        _syncBranchAsync = syncBranchAsync;
        _now = now;
        _checkInterval = checkInterval;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        var cancellationToken = _shutdown.Token;
        _loopTask = Task.Run(() => RunLoopAsync(cancellationToken));
    }

    public void RequestSync()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A due-project check is already queued.
        }
    }

    internal static bool IsSyncDue(Project project, DateTimeOffset now)
    {
        return project.AutoSyncMainBranchDaily
            && project.LastMainBranchSyncAttemptAt?.ToLocalTime().Date != now.ToLocalTime().Date;
    }

    internal async Task RunDueSyncsAsync(CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dueCheckAt = _now();
            var dueProjects = _dataStore.Data.Projects
                .Where(project => IsSyncDue(project, dueCheckAt))
                .ToArray();
            if (dueProjects.Length == 0)
                return;

            foreach (var project in dueProjects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attemptAt = _now();
                if (!project.AutoSyncMainBranchDaily || !IsSyncDue(project, attemptAt))
                    continue;

                var previousAttemptAt = project.LastMainBranchSyncAttemptAt;
                project.LastMainBranchSyncAttemptAt = attemptAt;
                GitBranchSyncResult result;
                if (string.IsNullOrWhiteSpace(project.WorkingDirectory))
                {
                    result = new GitBranchSyncResult(
                        false,
                        null,
                        false,
                        "The project has no working directory.");
                }
                else
                {
                    try
                    {
                        result = await _syncBranchAsync(
                            project.WorkingDirectory,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        project.LastMainBranchSyncAttemptAt = previousAttemptAt;
                        throw;
                    }
                    catch (Exception ex)
                    {
                        result = new GitBranchSyncResult(
                            false,
                            null,
                            false,
                            $"Unexpected git sync failure: {ex.Message}");
                    }
                }

                if (result.Succeeded)
                {
                    project.LastMainBranchSyncAt = attemptAt;
                    project.LastMainBranchSyncError = null;
                }
                else
                {
                    project.LastMainBranchSyncError = result.Message;
                    Trace.TraceWarning($"[Project sync] {project.Name}: {result.Message}");
                }
            }

            await _dataStore.SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdown.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        _shutdown.Dispose();
        _wakeSignal.Dispose();
        _runGate.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        await RunDueSyncsSafelyAsync(cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delayTask = Task.Delay(_checkInterval, waitCts.Token);
            var wakeTask = _wakeSignal.WaitAsync(waitCts.Token);
            var completed = await Task.WhenAny(delayTask, wakeTask).ConfigureAwait(false);
            waitCts.Cancel();

            try
            {
                await completed.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await RunDueSyncsSafelyAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunDueSyncsSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunDueSyncsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Project sync] Daily synchronization failed: {ex}");
        }
    }
}
