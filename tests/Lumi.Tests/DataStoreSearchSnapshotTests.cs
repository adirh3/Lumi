using System;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public class DataStoreSearchSnapshotTests
{
    [Fact]
    public void GetChatSearchSnapshot_LoadedMessagesRefreshVersionWhenContentChanges()
    {
        var timestamp = new DateTimeOffset(2026, 4, 23, 8, 0, 0, TimeSpan.Zero);
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            UpdatedAt = timestamp,
            Messages =
            [
                new ChatMessage
                {
                    Id = Guid.NewGuid(),
                    Role = "assistant",
                    Content = "Original answer",
                    Timestamp = timestamp
                }
            ]
        };

        var store = new DataStore(new AppData { Chats = [chat] });

        var original = store.GetChatSearchSnapshot(chat);

        chat.Messages[0].Content = "Updated answer";

        var refreshed = store.GetChatSearchSnapshot(chat);

        Assert.NotEqual(original.Version, refreshed.Version);
        var message = Assert.Single(refreshed.Messages);
        Assert.Equal("Updated answer", message.Text);
    }

    [Fact]
    public void GetChatSearchSnapshot_PreservesMessageRoleForRelevanceWeighting()
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = "memory design",
                    Timestamp = DateTimeOffset.UtcNow
                }
            ]
        };
        var store = new DataStore(new AppData { Chats = [chat] });

        var snapshot = store.GetChatSearchSnapshot(chat);

        Assert.Equal("user", Assert.Single(snapshot.Messages).Role);
    }
}
