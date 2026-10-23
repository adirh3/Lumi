using System;
using System.Collections.Generic;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.Tests;

/// <summary>What a chat said, for search tests: its messages' text by role.</summary>
public sealed class ChatSearchSnapshot
{
    public string Version { get; init; } = "";
    public IReadOnlyList<ChatSearchMessage> Messages { get; init; } = [];
}

public sealed class ChatSearchMessage
{
    public string Text { get; init; } = "";

    /// <summary>The message's role; empty means one of Lumi's answers.</summary>
    public string Role { get; init; } = "";

    public DateTimeOffset Timestamp { get; init; }
}

internal static class SearchTestContent
{
    /// <summary>
    /// A search over <paramref name="data"/> whose chats said what <paramref name="snapshots"/> holds,
    /// as messages in memory, found the way a store without a database finds them.
    /// </summary>
    public static GlobalSearchService CreateService(
        AppData data,
        IReadOnlyDictionary<Guid, ChatSearchSnapshot>? snapshots,
        Func<DateTimeOffset> now)
    {
        if (snapshots is not null)
        {
            foreach (var chat in data.Chats)
            {
                if (!snapshots.TryGetValue(chat.Id, out var snapshot))
                    continue;
                foreach (var message in snapshot.Messages)
                {
                    chat.Messages.Add(new ChatMessage
                    {
                        Role = string.IsNullOrEmpty(message.Role) ? "assistant" : message.Role,
                        Content = message.Text,
                        Timestamp = message.Timestamp
                    });
                }
            }
        }

        return new GlobalSearchService(() => data, new DataStore(data), now);
    }
}
