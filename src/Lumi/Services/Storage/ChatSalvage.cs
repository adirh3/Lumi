using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Lumi.Services.Storage;

/// <summary>
/// Copies what still reads in a damaged chat database into a new one: its chats, then their messages,
/// a range of rows at a time. A range that fails, with rows that do not read or read as something the
/// new file does not take, is halved until those rows are isolated, so only they are left behind; a
/// long stretch without rows is crossed in one seek, so a bound read wrong from a damaged page costs
/// little. A chat that lost messages is marked as missing some, like a damaged legacy file
/// (<see cref="ChatImportState.Failed"/>), so it is never rewound by position. Copying again, after
/// Lumi was closed part-way, adds only what is not there yet. The damaged file is only read: a
/// read-only connection never writes its log back into it.
/// </summary>
internal sealed class ChatSalvage
{
    /// <summary>What a recovery copied: chats, messages, and chats that lost messages on the way.</summary>
    internal readonly record struct Result(int Chats, int Messages, int ChatsMissingMessages);

    private const long RowsPerRange = 256;

    // Past this many ranges in a row without rows (a stretch left by deleted chats, or a bound read
    // from a damaged page), the walk seeks the next row there is rather than stepping on.
    private const int EmptyRangesBeforeSeek = 64;

    // Damage high in a table's tree fails every read below it; past this many failures the rest is
    // given up rather than probed row by row.
    private const int MaxFailedReads = 50_000;

    private const int SqliteIoError = 10;
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteFull = 13;

    private readonly SqliteConnection _source;
    private readonly SqliteConnection _target;
    private readonly Dictionary<long, long> _chatKeys = [];
    private int _failedReads;

    private ChatSalvage(SqliteConnection source, SqliteConnection target)
    {
        _source = source;
        _target = target;
    }

    public static Result Copy(string damagedPath, ChatDatabase database)
    {
        using var target = database.OpenConnection();
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = damagedPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());

        var salvage = new ChatSalvage(source, target);
        try
        {
            source.Open();
            salvage.CopyTable("chats", "chat_key", salvage.CopyChats);
            salvage.CopyTable("messages", "message_key", salvage.CopyMessages);
        }
        catch (Exception ex)
        {
            // The file does not open, or the disk is busy or full: what was copied is kept.
            Trace.TraceError("[ChatStore] Recovering chats from {0} stopped: {1}", damagedPath, ex);
        }

        return salvage.Finish();
    }

    private void CopyTable(string table, string key, Func<long, long, int> copyRange)
    {
        if (ReadBounds(table, key) is not var (from, last))
            return;

        var emptyRanges = 0;
        while (_failedReads < MaxFailedReads)
        {
            var to = from > last - (RowsPerRange - 1) ? last : from + (RowsPerRange - 1);
            emptyRanges = CopyRange(from, to, copyRange) > 0 ? 0 : emptyRanges + 1;
            if (to >= last)
                return;

            from = to + 1;
            // A long stretch without rows is crossed in one seek, so a bound too far out costs little.
            if (emptyRanges >= EmptyRangesBeforeSeek && TryReadNextKey(table, key, to, out var next))
            {
                if (next is not { } found)
                    return;
                from = Math.Max(from, found);
                emptyRanges = 0;
            }
        }
    }

    /// <summary>
    /// The smallest and largest keys either way of finding them reads, or null when neither reads or
    /// the table is empty. A seek down the table reads only its edges; the aggregate scans one of its
    /// indexes. Damage to a page either reads can make it fail or read wrong, so both are asked: the
    /// widest bounds leave no rows out, and the walk crosses what lies outside the rows in one seek.
    /// </summary>
    private (long First, long Last)? ReadBounds(string table, string key)
    {
        (long First, long Last)? bounds = null;
        foreach (var sql in new[]
                 {
                     $"SELECT (SELECT MIN({key}) FROM {table}), (SELECT MAX({key}) FROM {table});",
                     $"SELECT MIN({key}), MAX({key}) FROM {table};",
                 })
        {
            try
            {
                using var command = _source.CreateCommand();
                command.CommandText = sql;
                using var reader = command.ExecuteReader();
                if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1))
                    continue;
                var (low, high) = (reader.GetInt64(0), reader.GetInt64(1));
                var (first, last) = (Math.Min(low, high), Math.Max(low, high));
                bounds = bounds is { } known ? (Math.Min(known.First, first), Math.Max(known.Last, last)) : (first, last);
            }
            catch (SqliteException ex) when (IsReadFailure(ex))
            {
            }
        }

        return bounds;
    }

    /// <summary>Reads the first key after <paramref name="after"/> (null when there is none); false when it does not read.</summary>
    private bool TryReadNextKey(string table, string key, long after, out long? next)
    {
        next = null;
        try
        {
            using var command = _source.CreateCommand();
            command.CommandText = $"SELECT {key} FROM {table} WHERE {key} > $after ORDER BY {key} LIMIT 1;";
            command.Parameters.AddWithValue("$after", after);
            if (command.ExecuteScalar() is long found)
                next = found;
            return true;
        }
        catch (SqliteException ex) when (IsReadFailure(ex))
        {
            _failedReads++;
            return false;
        }
    }

    /// <summary>Copies a range of rows, halving it around rows that fail. Returns how many rows it read.</summary>
    private int CopyRange(long from, long to, Func<long, long, int> copyRange)
    {
        try
        {
            return copyRange(from, to);
        }
        catch (Exception ex) when (IsBadRows(ex))
        {
            if (++_failedReads >= MaxFailedReads || from == to)
                return 0;
            var middle = from + (to - from) / 2;
            return CopyRange(from, middle, copyRange) + CopyRange(middle + 1, to, copyRange);
        }
    }

    private int CopyChats(long from, long to)
    {
        using var transaction = _target.BeginTransaction();
        using var read = _source.CreateCommand();
        read.CommandText = """
            SELECT chat_key, id, updated_at, message_count, next_seq, content_version, import_state, metadata
            FROM chats WHERE chat_key BETWEEN $from AND $to;
            """;
        read.Parameters.AddWithValue("$from", from);
        read.Parameters.AddWithValue("$to", to);
        using var insert = _target.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO chats (id, updated_at, message_count, next_seq, content_version, import_state, metadata)
            VALUES ($id, $updated, $count, $next, $version, $state, $metadata)
            ON CONFLICT(id) DO UPDATE SET id = excluded.id
            RETURNING chat_key;
            """;
        var parameters = AddParameters(insert, "$id", "$updated", "$count", "$next", "$version", "$state", "$metadata");
        var keys = new List<(long, long)>();
        using (var reader = read.ExecuteReader())
        {
            while (reader.Read())
            {
                for (var column = 1; column < reader.FieldCount; column++)
                    parameters[column - 1].Value = reader.GetValue(column);
                keys.Add((reader.GetInt64(0), Convert.ToInt64(insert.ExecuteScalar())));
            }
        }

        transaction.Commit();
        foreach (var (damaged, copied) in keys)
            _chatKeys[damaged] = copied;
        return keys.Count;
    }

    private int CopyMessages(long from, long to)
    {
        using var transaction = _target.BeginTransaction();
        using var read = _source.CreateCommand();
        read.CommandText = """
            SELECT chat_key, seq, id, role, timestamp, tool_call_id, parent_tool_call_id, tool_name, kind, text_length, body
            FROM messages WHERE message_key BETWEEN $from AND $to;
            """;
        read.Parameters.AddWithValue("$from", from);
        read.Parameters.AddWithValue("$to", to);
        using var insert = _target.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR IGNORE INTO messages (chat_key, seq, id, role, timestamp, tool_call_id, parent_tool_call_id,
                                            tool_name, kind, text_length, body)
            VALUES ($chat, $seq, $id, $role, $timestamp, $toolCall, $parent, $toolName, $kind, $length, $body);
            """;
        var parameters = AddParameters(insert,
            "$chat", "$seq", "$id", "$role", "$timestamp", "$toolCall", "$parent", "$toolName", "$kind", "$length", "$body");
        var rows = 0;
        using (var reader = read.ExecuteReader())
        {
            while (reader.Read())
            {
                rows++;
                // Messages of a chat whose row did not read have nothing to belong to.
                if (!_chatKeys.TryGetValue(reader.GetInt64(0), out var chatKey))
                    continue;
                parameters[0].Value = chatKey;
                for (var column = 1; column < reader.FieldCount; column++)
                    parameters[column].Value = reader.GetValue(column);
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        return rows;
    }

    private Result Finish()
    {
        using var command = _target.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM chats
            WHERE import_state = 0 AND message_count > (SELECT COUNT(*) FROM messages m WHERE m.chat_key = chats.chat_key);
            """;
        var missing = Convert.ToInt32(command.ExecuteScalar());
        command.CommandText = """
            UPDATE chats SET
                import_state = CASE
                    WHEN import_state = 0 AND message_count > (SELECT COUNT(*) FROM messages m WHERE m.chat_key = chats.chat_key)
                    THEN 2 ELSE import_state END,
                message_count = (SELECT COUNT(*) FROM messages m WHERE m.chat_key = chats.chat_key),
                next_seq = MAX(next_seq, IFNULL((SELECT MAX(seq) + 1 FROM messages m WHERE m.chat_key = chats.chat_key), 1));
            SELECT (SELECT COUNT(*) FROM chats), (SELECT COUNT(*) FROM messages);
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() ? new Result(reader.GetInt32(0), reader.GetInt32(1), missing) : default;
    }

    private static SqliteParameter[] AddParameters(SqliteCommand command, params string[] names)
        => Array.ConvertAll(names, name => command.Parameters.Add(new SqliteParameter(name, DBNull.Value)));

    // Damage, or a disk that cannot read part of the file.
    private static bool IsReadFailure(SqliteException exception)
        => ChatDatabase.IsDamage(exception) || exception.SqliteErrorCode == SqliteIoError;

    // Rows that do not read, or read as something the new file does not take: text that is no longer
    // JSON (its index entry is made from it), a value missing from a column. A busy or full disk is
    // not the rows' doing; it ends the copy.
    private static bool IsBadRows(Exception exception) => exception switch
    {
        SqliteException sqlite => sqlite.SqliteErrorCode is not (SqliteBusy or SqliteLocked or SqliteFull),
        InvalidOperationException or InvalidCastException => true,
        _ => false,
    };
}
