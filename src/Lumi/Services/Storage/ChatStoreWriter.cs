using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Lumi.Services.Storage;

/// <summary>
/// Serializes every write to the chat database through one connection on a background task. Jobs
/// that arrive together share a transaction (each in its own savepoint, so one failing job never
/// takes the others with it), which keeps bursts of small saves cheap. Readers that must observe
/// a chat's queued writes await <see cref="WhenSettled"/> first.
/// </summary>
internal sealed class ChatStoreWriter : IDisposable
{
    private const int MaxJobsPerTransaction = 128;

    private readonly ChatDatabase _database;
    private readonly Channel<WriteJob> _jobs = Channel.CreateUnbounded<WriteJob>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly ConcurrentDictionary<Guid, Task> _lastWriteByChat = new();
    private readonly Task _loop;
    private Task _lastWrite = Task.CompletedTask;
    private readonly object _lastWriteSync = new();
    private bool _disposed;

    public ChatStoreWriter(ChatDatabase database)
    {
        _database = database;
        _loop = Task.Factory.StartNew(
            RunLoop,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>Queues a write. Its task completes once the write is committed.</summary>
    public Task Enqueue(Guid? chatId, Action<SqliteConnection, SqliteTransaction> work)
        => Enqueue(chatId, new WriteJob(work, null));

    /// <summary>
    /// Queues work that manages its own transactions (<c>VACUUM</c> cannot run inside one), in order
    /// with the writes around it. Its task completes once it ran.
    /// </summary>
    public Task EnqueueMaintenance(Action<SqliteConnection> work)
        => Enqueue(null, new WriteJob(null, work));

    private Task Enqueue(Guid? chatId, WriteJob job)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lastWriteSync)
        {
            if (!_jobs.Writer.TryWrite(job))
                throw new ObjectDisposedException(nameof(ChatStoreWriter));
            _lastWrite = job.Completion.Task;
            if (chatId is { } id)
                _lastWriteByChat[id] = job.Completion.Task;
        }

        return job.Completion.Task;
    }

    /// <summary>Completes once every write queued so far for the chat has finished (successfully or not).</summary>
    public Task WhenSettled(Guid chatId)
    {
        if (!_lastWriteByChat.TryGetValue(chatId, out var last) || last.IsCompleted)
            return Task.CompletedTask;
        return last.ContinueWith(static _ => { }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Completes once every write queued so far has finished.</summary>
    public Task WhenAllSettled()
    {
        Task last;
        lock (_lastWriteSync)
            last = _lastWrite;
        return last.IsCompleted
            ? Task.CompletedTask
            : last.ContinueWith(static _ => { }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void RunLoop()
    {
        using var connection = _database.OpenConnection();
        var batch = new List<WriteJob>(MaxJobsPerTransaction);
        while (_jobs.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            batch.Clear();
            while (batch.Count < MaxJobsPerTransaction && _jobs.Reader.TryRead(out var job))
            {
                if (job.Maintenance is null)
                {
                    batch.Add(job);
                    continue;
                }

                // Writes queued before it commit first; it then runs on its own.
                if (batch.Count > 0)
                    Execute(connection, batch);
                batch.Clear();
                RunMaintenance(connection, job);
            }

            if (batch.Count > 0)
                Execute(connection, batch);
        }
    }

    private void RunMaintenance(SqliteConnection connection, WriteJob job)
    {
        try
        {
            job.Maintenance!(connection);
            job.Completion.TrySetResult();
        }
        catch (Exception ex)
        {
            Trace.TraceError("[ChatStore] Maintenance failed: {0}", ex);
            _database.NoteDamage(ex);
            job.Completion.TrySetException(ex);
        }
    }

    private void Execute(SqliteConnection connection, List<WriteJob> batch)
    {
        SqliteTransaction? transaction = null;
        try
        {
            transaction = connection.BeginTransaction(deferred: false);
            for (var i = 0; i < batch.Count; i++)
            {
                var job = batch[i];
                var savepoint = "job" + i;
                transaction.Save(savepoint);
                try
                {
                    job.Work!(connection, transaction);
                    transaction.Release(savepoint);
                }
                catch (Exception ex)
                {
                    try { transaction.Rollback(savepoint); transaction.Release(savepoint); }
                    catch (Exception rollbackError) { Trace.TraceWarning("[ChatStore] Savepoint rollback failed: {0}", rollbackError); }
                    job.Error = ex;
                }
            }

            transaction.Commit();
        }
        catch (Exception ex)
        {
            Trace.TraceError("[ChatStore] Write transaction failed: {0}", ex);
            try { transaction?.Rollback(); } catch { /* the failed commit already ended it */ }
            foreach (var job in batch)
                job.Error ??= ex;
        }
        finally
        {
            transaction?.Dispose();
        }

        foreach (var job in batch)
        {
            if (job.Error is { } error)
            {
                _database.NoteDamage(error);
                job.Completion.TrySetException(error);
            }
            else
            {
                job.Completion.TrySetResult();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        lock (_lastWriteSync)
            _jobs.Writer.TryComplete();
        try
        {
            if (!_loop.Wait(TimeSpan.FromSeconds(30)))
                Trace.TraceWarning("[ChatStore] Timed out waiting for queued chat writes to finish.");
        }
        catch (AggregateException ex)
        {
            Trace.TraceWarning("[ChatStore] Writer stopped with an error: {0}", ex);
        }
    }

    private sealed class WriteJob(Action<SqliteConnection, SqliteTransaction>? work, Action<SqliteConnection>? maintenance)
    {
        public Action<SqliteConnection, SqliteTransaction>? Work { get; } = work;
        public Action<SqliteConnection>? Maintenance { get; } = maintenance;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Error { get; set; }
    }
}
