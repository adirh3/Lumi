using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Lumi.Services.Storage;

internal sealed partial class ChatStore
{
    // Free pages below this stay for new writes to reuse; above it they go back to the disk.
    private const long ReclaimThresholdPages = 1024;

    private const long FullVacuum = 1;
    private const long IncrementalVacuum = 2;

    private int _reclaimQueued;

    /// <summary>
    /// True when <see cref="StartMaintenance"/> upgrades the file once at this start: it builds the text
    /// index of a schema 1 database, or rewrites a file from before incremental vacuum (when the disk
    /// has room for it). Every write waits behind that, so Lumi lets it finish before opening a window.
    /// </summary>
    internal bool NeedsUpgrade
        => _database.NeedsTextIndex || (_database.NeedsVacuumSwitch && HasRoomToSwitch());

    /// <summary>
    /// Starts, on the writer and in this order, the upkeep a database from an earlier Lumi needs: the
    /// text index of a schema 1 database (search finds message text once it is built), the one-time
    /// switch of an older file to incremental vacuum, and giving space deletes left behind back to the
    /// disk. Each step that fails is tried again at the next start. Completes when all have run; when
    /// the first two have work to do (<see cref="NeedsUpgrade"/>), Lumi waits for that before its windows open.
    /// </summary>
    public Task StartMaintenance()
    {
        var textIndex = _database.NeedsTextIndex
            ? _writer.Enqueue(null, (connection, transaction) =>
                {
                    var watch = Stopwatch.StartNew();
                    if (ChatDatabase.BuildTextIndex(connection, transaction))
                        Trace.TraceInformation("[ChatStore] Built the text index in {0} ms.", watch.ElapsedMilliseconds);
                }).ContinueWith(
                    task =>
                    {
                        if (task.IsCompletedSuccessfully)
                            _database.NoteTextIndexBuilt();
                    },
                    TaskScheduler.Default)
            : Task.CompletedTask;
        var vacuum = _writer.EnqueueMaintenance(SwitchToIncrementalVacuum);
        return Task.WhenAll(textIndex, vacuum, ReclaimFreeSpace());
    }

    /// <summary>
    /// Gives pages deletes freed back to the disk once there are enough of them, on the writer after
    /// the writes queued before.
    /// </summary>
    internal Task ReclaimFreeSpace()
    {
        if (Interlocked.Exchange(ref _reclaimQueued, 1) == 1)
            return Task.CompletedTask;

        try
        {
            return _writer.EnqueueMaintenance(Reclaim);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down: the space is given back at the next start.
            return Task.CompletedTask;
        }
    }

    private void Reclaim(SqliteConnection connection)
    {
        Volatile.Write(ref _reclaimQueued, 0);
        if (Pragma(connection, "auto_vacuum") != IncrementalVacuum
            || Pragma(connection, "freelist_count") < ReclaimThresholdPages)
            return;

        // The commit that switches to full mode gives every free page back in one pass: about half a
        // second for the largest chat, under a tenth after Clear All. PRAGMA incremental_vacuum instead
        // looks each page it drops up in the whole freelist, which took minutes after Clear All.
        using var command = CreateCommand(connection, null, "PRAGMA auto_vacuum = FULL;");
        try
        {
            command.ExecuteNonQuery();
        }
        finally
        {
            command.CommandText = "PRAGMA auto_vacuum = INCREMENTAL;";
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Switches a file created before incremental vacuum (schema 1) by rewriting it once. The rewrite
    /// goes through a temporary copy and the write-ahead log, so it waits for a start with room for both.
    /// Readers keep reading meanwhile; writes queue behind it, which is why Lumi waits for it at startup.
    /// </summary>
    private void SwitchToIncrementalVacuum(SqliteConnection connection)
    {
        var mode = Pragma(connection, "auto_vacuum");
        if (mode == IncrementalVacuum)
            return;

        if (mode == FullVacuum)
        {
            // Left by a reclaim that stopped between its two steps: switching back needs no rewrite.
            using var reset = CreateCommand(connection, null, "PRAGMA auto_vacuum = INCREMENTAL;");
            reset.ExecuteNonQuery();
            return;
        }

        if (!HasRoomToSwitch())
        {
            Trace.TraceWarning("[ChatStore] Not enough disk space to switch the chat database to incremental vacuum yet.");
            return;
        }

        var watch = Stopwatch.StartNew();
        using var command = CreateCommand(connection, null, "PRAGMA temp_store = FILE; PRAGMA auto_vacuum = INCREMENTAL;");
        command.ExecuteNonQuery();
        try
        {
            command.CommandText = "VACUUM;";
            command.ExecuteNonQuery();
        }
        finally
        {
            command.CommandText = "PRAGMA temp_store = MEMORY;";
            command.ExecuteNonQuery();
        }

        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
        _database.NoteVacuumSwitched();
        Trace.TraceInformation("[ChatStore] Switched the chat database to incremental vacuum in {0} ms.", watch.ElapsedMilliseconds);
    }

    // The rewrite goes through a temporary copy and the write-ahead log, each the size of the file.
    private bool HasRoomToSwitch()
    {
        var size = new FileInfo(_database.Path).Length;
        return HasRoom(_database.Path, 2 * size) && HasRoom(Path.GetTempPath(), size);
    }

    /// <summary>True when the disk the database is on has <paramref name="bytes"/> free, beside a margin.</summary>
    internal bool HasRoomFor(long bytes) => HasRoom(_database.Path, bytes);

    private static bool HasRoom(string path, long bytes)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace > bytes + (64L << 20);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Unknown: try; a write that runs out of room is undone.
            return true;
        }
    }

    private static long Pragma(SqliteConnection connection, string name)
    {
        using var command = CreateCommand(connection, null, $"PRAGMA {name};");
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
