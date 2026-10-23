using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Lumi.Models;
using Microsoft.Data.Sqlite;

namespace Lumi.Services.Storage;

internal sealed partial class ChatStore
{
    /// <summary>
    /// Reads a chat's newest exchange — from its last user message — as its live tail. Sends held by
    /// Pause wait at the end of a chat as user messages of their own; the tail keeps them all, with
    /// the exchange before them, so the chat can queue them again. A tail is only read when no
    /// session in this process owns the chat, so work stored as in progress in an older exchange is
    /// left over from an earlier run and stays in history like everything else.
    /// </summary>
    public Task<ChatTailRead> ReadTailAsync(Guid chatId)
        => ReadAsync(chatId, (connection, chatKey) =>
        {
            var nextSeq = ScalarLong(connection, "SELECT next_seq FROM chats WHERE chat_key = $chat;", chatKey) ?? 1;
            var tailStart = FindTailStart(connection, chatKey)
                ?? ScalarLong(connection, "SELECT MIN(seq) FROM messages WHERE chat_key = $chat;", chatKey)
                ?? nextSeq;

            var messages = ReadMessages(connection,
                "SELECT seq, body FROM messages WHERE chat_key = $chat AND seq >= $from ORDER BY seq;",
                chatKey, ("$from", tailStart));
            var history = (int)(ScalarLong(connection,
                "SELECT COUNT(*) FROM messages WHERE chat_key = $chat AND seq < $from;",
                chatKey, ("$from", tailStart)) ?? 0);
            return new ChatTailRead(messages, tailStart, history, nextSeq, CountConversation(connection, chatKey, tailStart));
        }, new ChatTailRead([], 1, 0, 1, default));

    // The newest user message that is not a held send — or, when only held sends exist, the first of
    // them. Reads user messages newest first and stops at the first that is not held.
    private static long? FindTailStart(SqliteConnection connection, long chatKey)
    {
        using var command = CreateCommand(connection, null, """
            SELECT seq, json_extract(body, '$.isPendingPausedSend') FROM messages INDEXED BY messages_outline
            WHERE chat_key = $chat AND role IN ('user', 'assistant', 'system', 'error') AND role = 'user'
            ORDER BY seq DESC;
            """);
        command.Parameters.AddWithValue("$chat", chatKey);
        using var reader = command.ExecuteReader();
        long? start = null;
        while (reader.Read())
        {
            start = reader.GetInt64(0);
            if (reader.IsDBNull(1) || reader.GetInt64(1) == 0)
                break;
        }

        return start;
    }

    /// <summary>User messages and assistant messages with text before <paramref name="beforeSeq"/>, from the outline index.</summary>
    public Task<HistoryConversationCounts> CountConversationAsync(Guid chatId, long beforeSeq)
        => ReadAsync(chatId, (connection, chatKey) => CountConversation(connection, chatKey, beforeSeq), default(HistoryConversationCounts));

    private static HistoryConversationCounts CountConversation(SqliteConnection connection, long chatKey, long beforeSeq)
    {
        using var command = CreateCommand(connection, null, """
            SELECT
                COUNT(CASE WHEN role = 'user' THEN 1 END),
                COUNT(CASE WHEN role = 'assistant' AND text_length > 0 THEN 1 END),
                COUNT(CASE WHEN role = 'user' AND text_length > 0 THEN 1 END)
            FROM messages INDEXED BY messages_outline
            WHERE chat_key = $chat AND role IN ('user', 'assistant', 'system', 'error') AND seq < $before;
            """);
        command.Parameters.AddWithValue("$chat", chatKey);
        command.Parameters.AddWithValue("$before", beforeSeq);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new HistoryConversationCounts(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)) : default;
    }

    /// <summary>Reads the messages with positions in [<paramref name="fromSeq"/>, <paramref name="toSeqExclusive"/>).</summary>
    public Task<List<ChatMessage>> ReadRangeAsync(Guid chatId, long fromSeq, long toSeqExclusive)
        => ReadAsync(chatId, (connection, chatKey) => ReadMessages(connection,
            "SELECT seq, body FROM messages WHERE chat_key = $chat AND seq >= $from AND seq < $to ORDER BY seq;",
            chatKey, ("$from", fromSeq), ("$to", toSeqExclusive)), new List<ChatMessage>());

    /// <summary>Reads every message before <paramref name="beforeSeq"/> (or the whole transcript).</summary>
    public Task<List<ChatMessage>> ReadAllAsync(Guid chatId, long beforeSeq = long.MaxValue)
        => ReadRangeAsync(chatId, long.MinValue, beforeSeq);

    /// <summary>
    /// The conversation outline: every user, assistant, system and error message's position, id,
    /// role, time and text length, read from an index without touching message bodies — except a
    /// user message's author, the start of its text and whether it is a form's receipt (see
    /// <see cref="ChatOutlineEntry"/>), read from the few user rows.
    /// </summary>
    public Task<List<ChatOutlineEntry>> ReadOutlineAsync(Guid chatId, long beforeSeq = long.MaxValue)
        => ReadAsync(chatId, (connection, chatKey) =>
        {
            using var command = CreateCommand(connection, null, $"""
                SELECT seq, id, role, timestamp, text_length,
                    CASE WHEN role = 'user' THEN json_extract(body, '$.author') END,
                    CASE WHEN role = 'user' THEN substr(json_extract(body, '$.content'), 1, {OutlineTextStartLength}) END,
                    role = 'user' AND json_type(body, '$.interactionReceipt') IS NOT NULL
                FROM messages INDEXED BY messages_outline
                WHERE chat_key = $chat AND role IN ('user', 'assistant', 'system', 'error') AND seq < $before
                ORDER BY seq;
                """);
            command.Parameters.AddWithValue("$chat", chatKey);
            command.Parameters.AddWithValue("$before", beforeSeq);
            using var reader = command.ExecuteReader();
            var outline = new List<ChatOutlineEntry>();
            while (reader.Read())
            {
                outline.Add(new ChatOutlineEntry(
                    reader.GetInt64(0),
                    ReadGuid(reader, 1),
                    reader.GetString(2),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)).ToLocalTime(),
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetBoolean(7)));
            }

            return outline;
        }, new List<ChatOutlineEntry>());

    /// <summary>How much of a user message's text the outline reads: enough to tell how it starts.</summary>
    private const int OutlineTextStartLength = 64;

    /// <summary>
    /// Reads messages by absolute position in the ordered transcript: <paramref name="count"/>
    /// messages starting at <paramref name="startIndex"/>. Synchronous, for callers already on a
    /// background thread that page through a transcript.
    /// </summary>
    public List<ChatMessage> ReadByIndexNow(Guid chatId, int startIndex, int count)
    {
        PrepareReadAsync(chatId).GetAwaiter().GetResult();
        return ReadNow(chatId, (connection, chatKey) => count <= 0
            ? new List<ChatMessage>()
            : ReadMessages(connection,
                "SELECT seq, body FROM messages WHERE chat_key = $chat ORDER BY seq LIMIT $count OFFSET $start;",
                chatKey, ("$count", count), ("$start", Math.Max(0, startIndex))), new List<ChatMessage>());
    }

    /// <summary>
    /// The messages that can make a local image path showable: user messages (their attachments),
    /// announced files and image views — through indexes, without reading the rest of the transcript.
    /// </summary>
    public Task<List<ChatMessage>> ReadImageSourcesAsync(Guid chatId)
        => ReadAsync(chatId, (connection, chatKey) =>
        {
            var users = ReadMessages(connection, """
                SELECT seq, body FROM messages INDEXED BY messages_outline
                WHERE chat_key = $chat AND role IN ('user', 'assistant', 'system', 'error') AND role = 'user'
                ORDER BY seq;
                """, chatKey);
            var files = ReadMessages(connection, """
                SELECT seq, body FROM messages INDEXED BY messages_by_kind
                WHERE chat_key = $chat AND kind IS NOT NULL AND kind IN ('deliverable', 'image')
                ORDER BY seq;
                """, chatKey);
            users.AddRange(files);
            users.Sort(static (left, right) => left.StorageSeq.CompareTo(right.StorageSeq));
            return users;
        }, new List<ChatMessage>());

    /// <summary>
    /// Messages per role before <paramref name="beforeSeq"/>, with how many of them have text — for
    /// summaries of a whole chat that must not read its transcript.
    /// </summary>
    public Task<Dictionary<string, (int Count, int WithText)>> CountRolesAsync(Guid chatId, long beforeSeq = long.MaxValue)
        => ReadAsync(chatId, (connection, chatKey) =>
        {
            using var command = CreateCommand(connection, null,
                "SELECT role, COUNT(*), TOTAL(text_length > 0) FROM messages WHERE chat_key = $chat AND seq < $before GROUP BY role;");
            command.Parameters.AddWithValue("$chat", chatKey);
            command.Parameters.AddWithValue("$before", beforeSeq);
            using var reader = command.ExecuteReader();
            var counts = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            while (reader.Read())
                counts[reader.GetString(0)] = (reader.GetInt32(1), (int)reader.GetDouble(2));
            return counts;
        }, new Dictionary<string, (int, int)>(StringComparer.Ordinal));

    /// <summary>
    /// The newest user, assistant, system or error message with <paramref name="role"/> (optionally
    /// only one with text) before <paramref name="beforeSeq"/>, through the outline index.
    /// </summary>
    public Task<ChatMessage?> ReadLastConversationMessageAsync(Guid chatId, string role, bool withText, long beforeSeq = long.MaxValue)
        => ReadAsync(chatId, (connection, chatKey) => ReadMessages(connection, $"""
            SELECT seq, body FROM messages INDEXED BY messages_outline
            WHERE chat_key = $chat AND role IN ('user', 'assistant', 'system', 'error') AND role = $role
              AND seq < $before{(withText ? " AND text_length > 0" : "")}
            ORDER BY seq DESC LIMIT 1;
            """, chatKey, ("$role", role), ("$before", beforeSeq)).FirstOrDefault(), (ChatMessage?)null);

    /// <summary>
    /// For each run, the few steps its card summarizes without its work: the latest step that shows
    /// as a step card (a tool outside <paramref name="toolsWithoutCards"/>, and not a run of its own),
    /// the latest reported intent, and the first plan update. Oldest first, without their output,
    /// which a card does not show. Private copies: never attach them.
    /// </summary>
    public Task<List<ChatMessage>> ReadRunGlanceStepsAsync(
        Guid chatId,
        IReadOnlyCollection<string> runToolCallIds,
        IReadOnlyList<string> toolsWithoutCards)
        => ReadAsync(chatId, (connection, chatKey) =>
        {
            var excluded = string.Join(", ", toolsWithoutCards.Select(static (_, i) => $"$t{i}"));
            var queries = new[]
            {
                $"""
                SELECT seq, json_remove(body, '$.toolOutput') FROM messages INDEXED BY messages_by_parent
                WHERE chat_key = $chat AND parent_tool_call_id IS NOT NULL AND parent_tool_call_id = $parent
                  AND role = 'tool'
                  AND (tool_name IS NULL OR (tool_name NOT IN ({excluded}) AND tool_name <> 'task' AND tool_name NOT LIKE 'agent:%'))
                ORDER BY seq DESC LIMIT 1;
                """,
                """
                SELECT seq, json_remove(body, '$.toolOutput') FROM messages INDEXED BY messages_by_parent
                WHERE chat_key = $chat AND parent_tool_call_id IS NOT NULL AND parent_tool_call_id = $parent
                  AND role = 'tool' AND tool_name = 'report_intent'
                ORDER BY seq DESC LIMIT 1;
                """,
                """
                SELECT seq, json_remove(body, '$.toolOutput') FROM messages INDEXED BY messages_by_parent
                WHERE chat_key = $chat AND parent_tool_call_id IS NOT NULL AND parent_tool_call_id = $parent
                  AND role = 'tool' AND tool_name IN ('update_todo', 'manage_todo_list')
                ORDER BY seq LIMIT 1;
                """,
            };

            var steps = new List<ChatMessage>();
            var seen = new HashSet<long>();
            var names = toolsWithoutCards.Select(static (name, i) => ($"$t{i}", (object)name)).ToArray();
            foreach (var parent in runToolCallIds)
            {
                foreach (var query in queries)
                {
                    foreach (var step in ReadMessages(connection, query, chatKey, [("$parent", parent), .. names]))
                    {
                        if (seen.Add(step.StorageSeq))
                            steps.Add(step);
                    }
                }
            }

            steps.Sort(static (left, right) => left.StorageSeq.CompareTo(right.StorageSeq));
            return steps;
        }, new List<ChatMessage>());

    /// <summary>
    /// Reads an older exchange — the messages in [<paramref name="fromSeq"/>, <paramref name="toSeq"/>)
    /// — for its build: first its outline from the rows' small columns, without touching a body
    /// (each message's header, the headers of the steps its runs took at or after
    /// <paramref name="toSeq"/>, and the earlier tool calls its steps continue), then the bodies of just
    /// the messages and later steps <paramref name="pick"/> chooses from that outline.
    /// </summary>
    public Task<ExchangeRead> ReadExchangeAsync(
        Guid chatId,
        long fromSeq,
        long toSeq,
        Func<ExchangeOutline, IReadOnlySet<long>> pick)
        => ReadAsync(chatId, (connection, chatKey) =>
        {
            var messages = ReadHeaders(connection, """
                SELECT seq, role, kind, tool_name, tool_call_id, parent_tool_call_id FROM messages
                WHERE chat_key = $chat AND seq >= $from AND seq < $to
                ORDER BY seq;
                """, chatKey, ("$from", fromSeq), ("$to", toSeq));
            var later = new List<StoredMessageHeader>();
            foreach (var message in messages)
            {
                if (message.Role == "tool" && message.ToolCallId is { Length: > 0 } run && ToolDisplayHelper.IsSubagentTool(message.ToolName))
                {
                    later.AddRange(ReadHeaders(connection, """
                        SELECT seq, role, kind, tool_name, tool_call_id, parent_tool_call_id FROM messages INDEXED BY messages_by_parent
                        WHERE chat_key = $chat AND parent_tool_call_id IS NOT NULL AND parent_tool_call_id = $parent AND seq >= $to
                        ORDER BY seq;
                        """, chatKey, ("$parent", run), ("$to", toSeq)));
                }
            }

            later.Sort(static (left, right) => left.Seq.CompareTo(right.Seq));
            var earlier = ReadEarlierToolCalls(
                connection, chatKey, messages.Select(static message => (message.ToolCallId, message.ParentToolCallId)), fromSeq);
            var outline = new ExchangeOutline(messages, later, earlier);
            var picked = pick(outline);
            var read = ReadAtSeqs(connection, chatKey, picked);
            var split = read.FindIndex(message => message.StorageSeq >= toSeq);
            return split < 0
                ? new ExchangeRead(outline, read, [])
                : new ExchangeRead(outline, read.GetRange(0, split), read.GetRange(split, read.Count - split));
        }, new ExchangeRead(ExchangeOutline.Empty, [], []));

    /// <summary>
    /// Reads the messages at the given positions, oldest first. Synchronous, for callers already on a
    /// background thread.
    /// </summary>
    public List<ChatMessage> ReadAtSeqsNow(Guid chatId, IReadOnlyCollection<long> seqs)
    {
        PrepareReadAsync(chatId).GetAwaiter().GetResult();
        return ReadNow(chatId, (connection, chatKey) => ReadAtSeqs(connection, chatKey, seqs), new List<ChatMessage>());
    }

    private static List<ChatMessage> ReadAtSeqs(SqliteConnection connection, long chatKey, IReadOnlyCollection<long> seqs)
        => seqs.Count == 0
            ? []
            : ReadMessages(connection, """
                SELECT seq, body FROM messages
                WHERE chat_key = $chat AND seq IN (SELECT value FROM json_each($seqs))
                ORDER BY seq;
                """, chatKey, ("$seqs", "[" + string.Join(',', seqs.Select(static seq => seq.ToString(CultureInfo.InvariantCulture))) + "]"));

    /// <summary>
    /// The tool calls stored before <paramref name="beforeSeq"/> that the given steps continue — a run
    /// of an earlier exchange that went on working — and the calls between them and their run.
    /// </summary>
    public Task<List<StoredToolCall>> ReadEarlierToolCallsAsync(
        Guid chatId,
        IReadOnlyList<(string? ToolCallId, string? ParentToolCallId)> steps,
        long beforeSeq)
        => ReadAsync(chatId, (connection, chatKey) => ReadEarlierToolCalls(connection, chatKey, steps, beforeSeq), new List<StoredToolCall>());

    private static List<StoredToolCall> ReadEarlierToolCalls(
        SqliteConnection connection,
        long chatKey,
        IEnumerable<(string? ToolCallId, string? ParentToolCallId)> steps,
        long beforeSeq)
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        var parents = new List<string>();
        foreach (var (toolCallId, parentToolCallId) in steps)
        {
            if (toolCallId is { Length: > 0 })
                defined.Add(toolCallId);
            if (parentToolCallId is { Length: > 0 })
                parents.Add(parentToolCallId);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = parents.Where(parent => !defined.Contains(parent) && seen.Add(parent)).ToList();
        var calls = new List<StoredToolCall>();
        if (pending.Count == 0)
            return calls;

        using var command = CreateCommand(connection, null, """
            SELECT tool_call_id, parent_tool_call_id, tool_name FROM messages INDEXED BY messages_by_tool_call
            WHERE chat_key = $chat AND tool_call_id IS NOT NULL AND tool_call_id = $id AND seq < $before
            ORDER BY seq DESC LIMIT 1;
            """);
        command.Parameters.AddWithValue("$chat", chatKey);
        command.Parameters.AddWithValue("$before", beforeSeq);
        var id = command.Parameters.Add("$id", SqliteType.Text);
        // A step's parent is its run; the parents of anything else in between are followed up too.
        for (var depth = 0; depth < 8 && pending.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var toolCallId in pending)
            {
                id.Value = toolCallId;
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                    continue;

                var call = new StoredToolCall(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2));
                calls.Add(call);
                if (!ToolDisplayHelper.IsSubagentTool(call.ToolName)
                    && call.ParentToolCallId is { Length: > 0 } parent
                    && !defined.Contains(parent)
                    && seen.Add(parent))
                {
                    next.Add(parent);
                }
            }

            pending = next;
        }

        return calls;
    }

    private static List<StoredMessageHeader> ReadHeaders(
        SqliteConnection connection,
        string sql,
        long chatKey,
        params (string Name, object Value)[] parameters)
    {
        using var command = CreateCommand(connection, null, sql);
        command.Parameters.AddWithValue("$chat", chatKey);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        var headers = new List<StoredMessageHeader>();
        while (reader.Read())
        {
            headers.Add(new StoredMessageHeader(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return headers;
    }

    /// <summary>Finds a message by id and returns it with its absolute index, or null.</summary>
    public Task<(ChatMessage Message, int Index)?> FindMessageAsync(Guid chatId, Guid messageId)
        => ReadAsync<(ChatMessage, int)?>(chatId, (connection, chatKey) =>
        {
            var found = ReadMessages(connection,
                "SELECT seq, body FROM messages INDEXED BY messages_by_id WHERE chat_key = $chat AND id = $id ORDER BY seq DESC LIMIT 1;",
                chatKey, ("$id", messageId.ToByteArray()));
            if (found.Count == 0)
                return null;
            var index = (int)(ScalarLong(connection,
                "SELECT COUNT(*) FROM messages WHERE chat_key = $chat AND seq < $seq;",
                chatKey, ("$seq", found[0].StorageSeq)) ?? 0);
            return (found[0], index);
        }, null);

    /// <summary>
    /// A tool call and everything nested under it — the inner activity of a sub-agent run, at any
    /// depth — in transcript order. Each level is found through the parent index.
    /// </summary>
    public Task<List<ChatMessage>> ReadDescendantsAsync(Guid chatId, string rootToolCallId)
        => ReadAsync(chatId, (connection, chatKey) => ReadMessages(connection, """
            WITH RECURSIVE tree(id) AS (
                SELECT $root
                UNION
                SELECT m.tool_call_id FROM tree t
                CROSS JOIN messages m INDEXED BY messages_by_parent
                WHERE m.chat_key = $chat AND m.parent_tool_call_id IS NOT NULL AND m.parent_tool_call_id = t.id
                  AND m.tool_call_id IS NOT NULL
            )
            SELECT seq, body FROM messages INDEXED BY messages_by_tool_call
            WHERE chat_key = $chat AND tool_call_id IS NOT NULL AND tool_call_id = $root
            UNION ALL
            SELECT m.seq, m.body FROM tree t
            CROSS JOIN messages m INDEXED BY messages_by_parent
            WHERE m.chat_key = $chat AND m.parent_tool_call_id IS NOT NULL AND m.parent_tool_call_id = t.id
              AND m.tool_call_id IS NOT $root
            ORDER BY 1;
            """, chatKey, ("$root", rootToolCallId)), new List<ChatMessage>());

    /// <summary>
    /// The last <paramref name="count"/> messages before <paramref name="beforeSeq"/>, oldest first.
    /// With <paramref name="conversationOnly"/> only user, assistant, system and error messages count.
    /// </summary>
    public Task<List<ChatMessage>> ReadRecentAsync(
        Guid chatId,
        int count,
        bool conversationOnly = false,
        long beforeSeq = long.MaxValue)
        => ReadAsync(chatId, (connection, chatKey) =>
        {
            var messages = ReadMessages(connection, conversationOnly
                ? """
                  SELECT seq, body FROM messages INDEXED BY messages_outline
                  WHERE chat_key = $chat AND role IN ('user', 'assistant', 'system', 'error') AND seq < $before
                  ORDER BY seq DESC LIMIT $count;
                  """
                : "SELECT seq, body FROM messages WHERE chat_key = $chat AND seq < $before ORDER BY seq DESC LIMIT $count;",
                chatKey, ("$before", beforeSeq), ("$count", Math.Max(0, count)));
            messages.Reverse();
            return messages;
        }, new List<ChatMessage>());

    /// <summary>
    /// Reads the conversation (user, assistant, system and error messages) together with the other
    /// messages carrying any of the given facets, oldest first — what whole-chat summaries such as the
    /// Library need, without the transcript's tool traffic.
    /// </summary>
    public Task<List<ChatMessage>> ReadConversationAndKindsAsync(Guid chatId, IReadOnlyList<string> kinds, long beforeSeq = long.MaxValue)
        => ReadAsync(chatId, (connection, chatKey) => ReadConversationAndKinds(connection, chatKey, kinds, beforeSeq, Conversation, "body"), new List<ChatMessage>());

    /// <summary>
    /// What an overview of the chat's history before <paramref name="beforeSeq"/> lists, through
    /// indexes and with SQLite doing the reading: the conversation, except answers that cannot hold a
    /// link and cite no sources (the others come with just their text and sources); the steps carrying
    /// any of <paramref name="stepKinds"/>, runs as summaries without what only their pages show (their
    /// task, transcript, reasoning and log); and what each file edit changed (see <see cref="FileEdits"/>),
    /// whose text stays in storage. Private copies: never attach one, as saving it would store the
    /// summary over the message.
    /// </summary>
    public Task<ChatOverview> ReadOverviewAsync(Guid chatId, IReadOnlyList<string> stepKinds, long beforeSeq)
        => ReadAsync(chatId, (connection, chatKey) => new ChatOverview(
            ReadConversationAndKinds(connection, chatKey, stepKinds, beforeSeq, OverviewConversation, RunSummary),
            ReadFileEdits(connection, chatKey, beforeSeq)), ChatOverview.Empty);

    private const string Conversation = """
        SELECT seq, body FROM messages INDEXED BY messages_outline
        WHERE chat_key = $chat AND role IN ('user', 'assistant', 'system', 'error') AND seq < $before
        """;

    // An answer can only hold a markdown link to a web page if its text has "](" and "http" (any case,
    // as LIKE compares): only those answers' text leaves the database, and the markdown parser reads
    // it. An answer that cites sources comes with them.
    private const string OverviewConversation = """
        SELECT seq, body FROM messages INDEXED BY messages_outline
        WHERE chat_key = $chat AND role IN ('user', 'assistant', 'system', 'error') AND role <> 'assistant' AND seq < $before
        UNION ALL
        SELECT seq, CASE WHEN sources IS NULL
                THEN json_object('role', 'assistant', 'content', content)
                ELSE json_object('role', 'assistant', 'content', content, 'sources', json(sources)) END
        FROM (SELECT seq, sources,
                CASE WHEN text LIKE '%](%' AND text LIKE '%http%' THEN text ELSE '' END AS content
              FROM (SELECT seq, json_extract(body, '$.content') AS text, json_extract(body, '$.sources') AS sources
                    FROM messages INDEXED BY messages_outline
                    WHERE chat_key = $chat AND role IN ('user', 'assistant', 'system', 'error') AND role = 'assistant'
                      AND seq < $before))
        WHERE content <> '' OR sources IS NOT NULL
        """;

    // A run without what only its page shows. Its payload is JSON text inside the message: concatenating
    // the trimmed payload makes it plain text again, so it is set back as the string it was, not as an
    // object. Other steps are read whole: their outputs are small, and trimming them costs a parse.
    private const string RunSummary = """
        CASE WHEN kind = 'agent' AND json_valid(json_extract(body, '$.content'))
            THEN json_set(body, '$.content',
                json_remove(json_extract(body, '$.content'), '$.prompt', '$.transcript', '$.reasoning', '$.entries') || '')
            ELSE body
        END
        """;

    private static List<ChatMessage> ReadConversationAndKinds(
        SqliteConnection connection,
        long chatKey,
        IReadOnlyList<string> kinds,
        long beforeSeq,
        string conversation,
        string stepBody)
    {
        var builder = new StringBuilder(conversation);
        var parameters = new List<(string, object)> { ("$before", beforeSeq) };
        if (kinds.Count > 0)
        {
            builder.Append($"""
                 UNION ALL
                SELECT seq, {stepBody} FROM messages INDEXED BY messages_by_kind
                WHERE chat_key = $chat AND kind IS NOT NULL AND kind IN (
                """);
            for (var i = 0; i < kinds.Count; i++)
            {
                builder.Append(i == 0 ? "" : ", ").Append("$k").Append(i);
                parameters.Add(("$k" + i, kinds[i]));
            }

            builder.Append(") AND role NOT IN ('user', 'assistant', 'system', 'error') AND seq < $before");
        }

        builder.Append(" ORDER BY seq;");
        return ReadMessages(connection, builder.ToString(), chatKey, parameters.ToArray());
    }

    /// <summary>
    /// What each file edit before <paramref name="beforeSeq"/> that did not fail changed, oldest first,
    /// counted by SQLite from the stored edits so their text never leaves the database (see
    /// <see cref="FileEdits"/>).
    /// </summary>
    private static List<StoredFileEdit> ReadFileEdits(SqliteConnection connection, long chatKey, long beforeSeq)
    {
        using var command = CreateCommand(connection, null, FileEdits);
        command.Parameters.AddWithValue("$chat", chatKey);
        command.Parameters.AddWithValue("$before", beforeSeq);
        using var reader = command.ExecuteReader();
        var edits = new List<StoredFileEdit>();
        while (reader.Read())
        {
            if (reader.IsDBNull(1))
                continue;
            var tool = reader.GetString(0);
            var path = reader.GetString(1);
            // A patch names its files on header lines, trimmed as ExtractAllDiffs trims them.
            if (tool == "apply_patch" && string.IsNullOrWhiteSpace(path = path.Trim()))
                continue;

            edits.Add(new StoredFileEdit(
                reader.GetInt64(2),
                path,
                reader.GetInt32(4),
                reader.GetInt32(5),
                ToolDisplayHelper.IsFileCreateTool(tool) || reader.GetBoolean(6)));
        }

        return edits;
    }

    /// <summary>
    /// The file, lines added and lines removed of every change each stored file edit made, counted as
    /// <see cref="ToolDisplayHelper.ExtractAllDiffs"/> reads the edit and the transcript's file changes
    /// count its text (a text's lines are its line breaks plus one; no text has none). An edit's
    /// arguments name the file and its old and new text; <c>multi_replace_string_in_file</c> lists such
    /// replacements; <c>apply_patch</c> is a patch whose header lines name each file it adds, updates or
    /// deletes, and whose lines starting with + or - (not +++ or ---) are its added and removed lines.
    /// Columns: tool, path, seq, the edit's part (rows of one edit come in the order it lists its files),
    /// added, removed, and whether a patch added the file.
    /// </summary>
    // json_args holds the arguments only where they parse as JSON, so no JSON function ever reads
    // malformed text (which raises an error), whatever order SQLite evaluates conditions in.
    private const string FileEdits = """
        WITH
        edit AS MATERIALIZED (
            SELECT seq, tool,
                CASE WHEN tool = 'apply_patch' THEN args END AS patch_args,
                CASE WHEN tool <> 'apply_patch' AND json_valid(args) THEN args END AS json_args
            FROM (SELECT seq, tool_name AS tool, json_extract(body, '$.content') AS args
                  FROM messages INDEXED BY messages_by_kind
                  WHERE chat_key = $chat AND kind IS NOT NULL AND kind = 'edit' AND seq < $before
                    AND json_extract(body, '$.toolStatus') IS NOT 'Failed')
        ),
        fields AS MATERIALIZED (
            SELECT seq, tool, json_extract(json_args, path_key) AS path,
                CASE WHEN old_key IS NOT NULL THEN json_extract(json_args, old_key) END AS old,
                CASE WHEN new_key IS NOT NULL THEN json_extract(json_args, new_key) END AS new
            FROM (SELECT seq, tool, json_args,
                    CASE WHEN json_type(json_args, '$.filePath') IS NOT NULL THEN '$.filePath'
                         WHEN json_type(json_args, '$.path') IS NOT NULL THEN '$.path'
                         WHEN json_type(json_args, '$.file') IS NOT NULL THEN '$.file' END AS path_key,
                    CASE WHEN json_type(json_args, '$.oldString') IS NOT NULL THEN '$.oldString'
                         WHEN json_type(json_args, '$.old_str') IS NOT NULL THEN '$.old_str' END AS old_key,
                    CASE WHEN json_type(json_args, '$.newString') IS NOT NULL THEN '$.newString'
                         WHEN json_type(json_args, '$.new_str') IS NOT NULL THEN '$.new_str'
                         WHEN json_type(json_args, '$.content') IS NOT NULL THEN '$.content'
                         WHEN json_type(json_args, '$.file_text') IS NOT NULL THEN '$.file_text'
                         WHEN json_type(json_args, '$.insert_text') IS NOT NULL THEN '$.insert_text' END AS new_key
                  FROM edit
                  WHERE tool <> 'multi_replace_string_in_file' AND json_type(json_args) = 'object')
            WHERE path_key IS NOT NULL AND json_type(json_args, path_key) = 'text'
              AND (old_key IS NULL OR json_type(json_args, old_key) IN ('text', 'null'))
              AND (new_key IS NULL OR json_type(json_args, new_key) IN ('text', 'null'))
        ),
        replacements AS (
            SELECT seq, tool, part,
                CASE WHEN json_type(item, '$.filePath') IS NOT NULL
                    THEN CASE json_type(item, '$.filePath') WHEN 'text' THEN json_extract(item, '$.filePath') END
                    ELSE CASE json_type(item, '$.path') WHEN 'text' THEN json_extract(item, '$.path') END END AS path,
                CASE json_type(item, '$.oldString') WHEN 'text' THEN json_extract(item, '$.oldString') END AS old,
                CASE json_type(item, '$.newString') WHEN 'text' THEN json_extract(item, '$.newString') END AS new
            FROM (SELECT edit.seq, edit.tool, entry.key AS part, CASE entry.type WHEN 'object' THEN entry.value END AS item
                  FROM edit, json_each(edit.json_args, '$.replacements') AS entry
                  WHERE edit.tool = 'multi_replace_string_in_file' AND json_type(edit.json_args, '$.replacements') = 'array'
                    AND entry.type = 'object')
        ),
        patch AS (
            SELECT seq, char(10) || replace(replace(
                CASE WHEN substr(trimmed, 1, 1) IN ('{', '"') AND json_valid(trimmed) THEN
                    CASE json_type(trimmed)
                        WHEN 'text' THEN json_extract(trimmed, '$')
                        WHEN 'object' THEN coalesce(
                            CASE json_type(trimmed, '$.patch') WHEN 'text' THEN json_extract(trimmed, '$.patch') END,
                            CASE json_type(trimmed, '$.input') WHEN 'text' THEN json_extract(trimmed, '$.input') END,
                            CASE json_type(trimmed, '$.text') WHEN 'text' THEN json_extract(trimmed, '$.text') END,
                            CASE json_type(trimmed, '$.content') WHEN 'text' THEN json_extract(trimmed, '$.content') END,
                            patch_args)
                        ELSE patch_args END
                    ELSE patch_args END,
                char(13) || char(10), char(10)), char(13), char(10)) AS text
            FROM (SELECT seq, patch_args, trim(patch_args, ' ' || char(9, 10, 13)) AS trimmed FROM edit WHERE tool = 'apply_patch')
        ),
        -- Quoted, a patch's line breaks read \n, so each header that starts a line is a marker that
        -- begins an array element: the file's section, its header line then its body. Quoted
        -- backslashes are set aside first, so a backslash and an n in the patched text never read as
        -- a line break.
        sections AS MATERIALIZED (
            SELECT patch.seq, part.key AS part, part.value AS section
            FROM patch, json_each('[' || replace(replace(replace(replace(replace(replace(
                    json_quote(patch.text), '\\', char(1)),
                    '\n*** Add File: ', '","A'),
                    '\n*** Update File: ', '","U'),
                    '\n*** Delete File: ', '","D'),
                    '\n*** End Patch', '","E'),
                    char(1), '\\') || ']') AS part
            WHERE part.key > 0 AND substr(part.value, 1, 1) IN ('A', 'U', 'D')
        ),
        -- A body line starts after a line break: lines starting with + are counted, less those
        -- starting with +++ (and the same for -).
        counted AS MATERIALIZED (
            SELECT seq, part, substr(section, 1, 1) AS operation,
                CASE WHEN header_end > 0 THEN substr(section, 2, header_end - 2) ELSE substr(section, 2) END AS path,
                (size - octet_length(replace(section, char(10) || '+', ''))) / 2
                    - (size - octet_length(replace(section, char(10) || '+++', ''))) / 4 AS added,
                (size - octet_length(replace(section, char(10) || '-', ''))) / 2
                    - (size - octet_length(replace(section, char(10) || '---', ''))) / 4 AS removed,
                instr(section || char(10), char(10) || '+' || char(10)) > 0 AS empty_added,
                instr(section || char(10), char(10) || '-' || char(10)) > 0 AS empty_removed
            FROM (SELECT seq, part, section, octet_length(section) AS size, instr(section, char(10)) AS header_end FROM sections)
        )
        SELECT tool, path, seq, 0 AS part,
            coalesce(octet_length(new) - octet_length(replace(new, char(10), '')) + (octet_length(new) > 0), 0),
            coalesce(octet_length(old) - octet_length(replace(old, char(10), '')) + (octet_length(old) > 0), 0),
            0
        FROM fields
        UNION ALL
        SELECT tool, path, seq, part,
            coalesce(octet_length(new) - octet_length(replace(new, char(10), '')) + (octet_length(new) > 0), 0),
            coalesce(octet_length(old) - octet_length(replace(old, char(10), '')) + (octet_length(old) > 0), 0),
            0
        FROM replacements
        UNION ALL
        -- A patch's lines join into its old and new text; one empty line joins to no text at all.
        SELECT 'apply_patch', path, seq, part,
            CASE WHEN operation = 'D' OR (added = 1 AND empty_added) THEN 0 ELSE added END,
            CASE WHEN operation <> 'U' OR (removed = 1 AND empty_removed) THEN 0 ELSE removed END,
            operation = 'A'
        FROM counted
        ORDER BY 3, 4;
        """;

    /// <summary>Reads the messages carrying any of the given facets (with the given role, if any), oldest first.</summary>
    public Task<List<ChatMessage>> ReadByKindAsync(Guid chatId, IReadOnlyList<string> kinds, long beforeSeq = long.MaxValue, string? role = null)
        => ReadAsync(chatId, (connection, chatKey) =>
        {
            var builder = new StringBuilder("SELECT seq, body FROM messages INDEXED BY messages_by_kind WHERE chat_key = $chat AND kind IS NOT NULL AND kind IN (");
            var parameters = new List<(string, object)> { ("$before", beforeSeq) };
            for (var i = 0; i < kinds.Count; i++)
            {
                builder.Append(i == 0 ? "" : ", ").Append("$k").Append(i);
                parameters.Add(("$k" + i, kinds[i]));
            }

            builder.Append(") AND seq < $before");
            if (role is not null)
            {
                builder.Append(" AND role = $role");
                parameters.Add(("$role", role));
            }

            builder.Append(" ORDER BY seq;");
            return ReadMessages(connection, builder.ToString(), chatKey, parameters.ToArray());
        }, new List<ChatMessage>());

    /// <summary>Counts the messages before a position (or all of them).</summary>
    public Task<int> CountAsync(Guid chatId, long beforeSeq = long.MaxValue)
        => ReadAsync(chatId, (connection, chatKey) => (int)(ScalarLong(connection,
            "SELECT COUNT(*) FROM messages WHERE chat_key = $chat AND seq < $before;",
            chatKey, ("$before", beforeSeq)) ?? 0), 0);

    /// <summary>The newest user prompts across every chat, newest first.</summary>
    public Task<List<StoredUserPrompt>> ReadUserPromptsAsync(int limit)
        => Task.Run(async () =>
        {
            await _writer.WhenAllSettled().ConfigureAwait(false);
            return Read(connection =>
            {
                using var command = CreateCommand(connection, null, """
                    SELECT c.id, m.id, json_extract(m.body, '$.content'), m.timestamp
                    FROM messages m INDEXED BY messages_prompts JOIN chats c ON c.chat_key = m.chat_key
                    WHERE m.role = 'user'
                    ORDER BY m.timestamp DESC LIMIT $limit;
                    """);
                command.Parameters.AddWithValue("$limit", Math.Max(0, limit));
                using var reader = command.ExecuteReader();
                var prompts = new List<StoredUserPrompt>();
                while (reader.Read())
                {
                    if (reader.IsDBNull(2))
                        continue;
                    prompts.Add(new StoredUserPrompt(
                        ReadGuid(reader, 0),
                        ReadGuid(reader, 1),
                        reader.GetString(2),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)).ToLocalTime()));
                }

                return prompts;
            });
        });

    // ── Read plumbing ───────────────────────────────────────────────────────

    private async Task PrepareReadAsync(Guid chatId)
    {
        if (EnsureImportedAsync is { } ensureImported)
            await ensureImported(chatId).ConfigureAwait(false);
        await _writer.WhenSettled(chatId).ConfigureAwait(false);
    }

    private async Task<T> ReadAsync<T>(Guid chatId, Func<SqliteConnection, long, T> read, T missing)
    {
        await PrepareReadAsync(chatId).ConfigureAwait(false);
        return await Task.Run(() => ReadNow(chatId, read, missing)).ConfigureAwait(false);
    }

    private T ReadNow<T>(Guid chatId, Func<SqliteConnection, long, T> read, T missing)
        => Read(connection =>
        {
            var chatKey = ScalarLong(connection, "SELECT chat_key FROM chats WHERE id = $id;", null, ("$id", chatId.ToByteArray()));
            return chatKey is { } key ? read(connection, key) : missing;
        });

    /// <summary>Runs a read on a pooled connection, noting damage it runs into (see <see cref="ChatDatabase.NoteDamage"/>).</summary>
    private T Read<T>(Func<SqliteConnection, T> read)
    {
        try
        {
            using var connection = _database.OpenConnection();
            return read(connection);
        }
        catch (SqliteException ex) when (ChatDatabase.IsDamage(ex))
        {
            _database.NoteDamage(ex);
            throw;
        }
    }

    internal static List<ChatMessage> ReadMessages(
        SqliteConnection connection,
        string sql,
        long chatKey,
        params (string Name, object Value)[] parameters)
    {
        using var command = CreateCommand(connection, null, sql);
        command.Parameters.AddWithValue("$chat", chatKey);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        var messages = new List<ChatMessage>();
        while (reader.Read())
        {
            var message = ChatStorageJson.DeserializeMessage(reader.GetString(1));
            message.StorageSeq = reader.GetInt64(0);
            message.PersistedRevision = message.Revision;
            messages.Add(message);
        }

        return messages;
    }

    private static long? ScalarLong(
        SqliteConnection connection,
        string sql,
        long? chatKey,
        params (string Name, object Value)[] parameters)
    {
        using var command = CreateCommand(connection, null, sql);
        if (chatKey is { } key)
            command.Parameters.AddWithValue("$chat", key);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }
}
