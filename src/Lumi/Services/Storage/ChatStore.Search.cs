using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumi.Models;
using Microsoft.Data.Sqlite;

namespace Lumi.Services.Storage;

/// <summary>
/// Where stored messages contain some text: the chat, and its newest such message of the user's and
/// of Lumi's (their rows, or 0 when there is none).
/// </summary>
internal readonly record struct StoredTextMatch(Guid ChatId, long UserMessageKey, long AnswerMessageKey);

internal sealed partial class ChatStore
{
    /// <summary>
    /// True once the text index can be searched. A database from before it (schema 1) builds it in the
    /// background when Lumi starts (see <see cref="StartMaintenance"/>).
    /// </summary>
    public bool HasTextIndex => !_database.NeedsTextIndex;

    /// <summary>
    /// The chats whose stored messages contain <paramref name="text"/>, ignoring case and accents (see
    /// <see cref="ChatSearchText"/>). Text the index cannot look up (under three characters, or before
    /// the index exists) finds nothing. Synchronous; call from a background thread.
    /// </summary>
    public List<StoredTextMatch> FindText(string text)
    {
        if (!HasTextIndex || !ChatSearchText.CanLookUp(text))
            return [];

        return Read(connection =>
        {
            using var command = CreateCommand(connection, null, """
                SELECT c.id,
                       IFNULL(MAX(CASE WHEN t.role = 'user' THEN t.rowid END), 0),
                       IFNULL(MAX(CASE WHEN t.role <> 'user' THEN t.rowid END), 0)
                FROM messages_text t JOIN chats c ON c.chat_key = t.chat_key
                WHERE messages_text MATCH $text
                GROUP BY t.chat_key;
                """);
            command.Parameters.AddWithValue("$text", ChatSearchText.Phrase(text));
            using var reader = command.ExecuteReader();
            var matches = new List<StoredTextMatch>();
            while (reader.Read())
                matches.Add(new StoredTextMatch(ReadGuid(reader, 0), reader.GetInt64(1), reader.GetInt64(2)));
            return matches;
        });
    }

    /// <summary>What the text index holds of a stored message (to quote it), or null when it is gone.</summary>
    public string? ReadSearchText(long messageKey)
        => Read(connection =>
        {
            using var command = CreateCommand(connection, null,
                $"SELECT {ChatDatabase.SearchTextSql("m")} FROM messages m WHERE m.message_key = $key;");
            command.Parameters.AddWithValue("$key", messageKey);
            return command.ExecuteScalar() as string;
        });

    /// <summary>
    /// The messages before <paramref name="beforeSeq"/> in which in-chat search may find
    /// <paramref name="query"/>, oldest first, with just what it reads of them (see
    /// <see cref="TranscriptSearchText"/>): the role, text and author of conversation and reasoning
    /// messages, and the whole message where the transcript shows more than its text (a submitted
    /// form's receipt, an input request). The text index finds them; reasoning, which it leaves out,
    /// and queries it cannot look up are matched by reading the chat's own rows. A superset: the
    /// caller counts the hits.
    /// </summary>
    public Task<List<ChatMessage>> ReadSearchCandidatesAsync(Guid chatId, string query, long beforeSeq, bool includeReasoning)
        => ReadAsync(chatId, (connection, chatKey) =>
        {
            const string columns = """
                m.seq, m.role, json_extract(m.body, '$.content'), json_extract(m.body, '$.author'),
                CASE WHEN m.role = 'tool' OR json_type(m.body, '$.interactionReceipt') IS NOT NULL THEN m.body END
                """;
            const string reasoning = "m.role = 'reasoning' AND lumi_contains(json_extract(m.body, '$.content'), $query)";
            var sql = HasTextIndex && ChatSearchText.CanLookUp(query)
                ? $"""
                   SELECT {columns}
                   FROM messages_text t JOIN messages m ON m.message_key = t.rowid
                   WHERE messages_text MATCH $match AND t.chat_key = $chat AND m.seq < $before
                   {(includeReasoning ? $"UNION ALL SELECT {columns} FROM messages m WHERE m.chat_key = $chat AND m.seq < $before AND {reasoning}" : "")}
                   ORDER BY 1;
                   """
                : $"""
                   SELECT {columns}
                   FROM messages m
                   WHERE m.chat_key = $chat AND m.seq < $before
                     AND (({ChatDatabase.IsSearchableSql("m")} AND lumi_contains({ChatDatabase.SearchTextSql("m")}, $query))
                          {(includeReasoning ? $"OR ({reasoning})" : "")})
                   ORDER BY m.seq;
                   """;
            // Matched as in-chat search counts: ignoring case, otherwise exactly as typed.
            connection.CreateFunction(
                "lumi_contains",
                static (string? text, string needle) => text is not null && text.Contains(needle, StringComparison.OrdinalIgnoreCase),
                isDeterministic: true);
            using var command = CreateCommand(connection, null, sql);
            command.Parameters.AddWithValue("$chat", chatKey);
            command.Parameters.AddWithValue("$before", beforeSeq);
            command.Parameters.AddWithValue("$query", query);
            command.Parameters.AddWithValue("$match", ChatSearchText.Phrase(query));
            using var reader = command.ExecuteReader();
            var messages = new List<ChatMessage>();
            while (reader.Read())
            {
                var message = reader.IsDBNull(4)
                    ? new ChatMessage
                    {
                        Role = reader.GetString(1),
                        Content = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        Author = reader.IsDBNull(3) ? null : reader.GetString(3),
                    }
                    : ChatStorageJson.DeserializeMessage(reader.GetString(4));
                message.StorageSeq = reader.GetInt64(0);
                messages.Add(message);
            }

            return messages;
        }, new List<ChatMessage>());
}
