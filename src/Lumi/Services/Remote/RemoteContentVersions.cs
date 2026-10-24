using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Lumi.Models;
using Lumi.Remote.Protocol;

namespace Lumi.Services.Remote;

/// <summary>
/// Versions for what a phone caches, so unchanged data is never shipped and re-parsed again: the
/// snapshot's chat page, library and settings sections, and the latest transcript window.
/// </summary>
internal static class RemoteContentVersions
{
    /// <summary>
    /// Stamps the content version of each section the snapshot carries, then drops the sections the
    /// phone already holds at that version and names them in <see cref="RemoteSnapshot.UnchangedSections"/>.
    /// </summary>
    public static void ApplySectionVersions(
        RemoteSnapshot snapshot,
        IReadOnlyDictionary<string, string>? known = null)
    {
        snapshot.SettingsVersion = Version(
            JsonSerializer.SerializeToUtf8Bytes(snapshot.Settings, RemoteJsonContext.Default.RemoteSettings));
        if (!snapshot.IsPartial)
        {
            snapshot.ChatsVersion = Version(
                JsonSerializer.SerializeToUtf8Bytes(snapshot.Chats, RemoteJsonContext.Default.RemoteChatPage));
            snapshot.LibraryVersion = Version(
                JsonSerializer.SerializeToUtf8Bytes(snapshot.Library, RemoteJsonContext.Default.RemoteLibrary));
        }

        if (known is not { Count: > 0 })
            return;

        var unchanged = new List<string>();
        if (!snapshot.IsPartial && Matches(RemoteProtocol.SnapshotSections.Chats, snapshot.ChatsVersion))
        {
            snapshot.Chats = new RemoteChatPage();
            unchanged.Add(RemoteProtocol.SnapshotSections.Chats);
        }

        if (!snapshot.IsPartial && Matches(RemoteProtocol.SnapshotSections.Library, snapshot.LibraryVersion))
        {
            snapshot.Library = new RemoteLibrary();
            unchanged.Add(RemoteProtocol.SnapshotSections.Library);
        }

        if (Matches(RemoteProtocol.SnapshotSections.Settings, snapshot.SettingsVersion))
        {
            snapshot.Settings = new RemoteSettings();
            unchanged.Add(RemoteProtocol.SnapshotSections.Settings);
        }

        snapshot.UnchangedSections = unchanged.Count > 0 ? unchanged : null;

        bool Matches(string section, string? version) =>
            version is not null
            && known.TryGetValue(section, out var held)
            && string.Equals(held, version, StringComparison.Ordinal);
    }

    /// <summary>
    /// Identifies one latest-window transcript projection. Any change the projection can show moves
    /// the version: the hub's per-chat change counter, a fingerprint of the in-memory messages
    /// (covering edits no event announced), the request shape and the settings it depends on.
    /// </summary>
    public static string ComputeTranscriptVersion(
        string epoch,
        long chatVersion,
        Chat chat,
        IEnumerable<ChatMessage>? liveMessages,
        bool compact,
        int limit,
        bool showReasoning,
        bool showToolCalls,
        string? workingDirectory,
        IReadOnlySet<string>? runningBackgroundToolCallIds,
        RemoteChatStatus status)
    {
        var hash = new RemoteContentHash();
        hash.Add(chat.Title);
        hash.Add(compact);
        hash.Add(limit);
        hash.Add(showReasoning);
        hash.Add(showToolCalls);
        hash.Add(workingDirectory);
        hash.Add(CultureInfo.CurrentUICulture.Name);
        // The compact window treats the live tail differently while the assistant works.
        hash.Add(status.IsBusy);
        hash.Add(status.IsStreaming);
        hash.Add(status.IsSessionActive);
        hash.Add(status.IsAwaitingInput);

        if (runningBackgroundToolCallIds is { Count: > 0 })
        {
            foreach (var id in runningBackgroundToolCallIds.Order(StringComparer.Ordinal))
                hash.Add(id);
        }
        else
        {
            hash.Add(0L);
        }

        if (liveMessages is not null)
        {
            long count = 0;
            foreach (var message in liveMessages)
            {
                AddMessage(ref hash, message);
                count++;
            }

            hash.Add(count);
        }
        else
        {
            // Persisted-only chats change through saves, which already move the per-chat counter.
            hash.Add(-2L);
            hash.Add(chat.MessageCount);
            hash.Add(chat.UpdatedAt.UtcTicks);
        }

        var prefix = epoch.Length > 8 ? epoch[..8] : epoch;
        return $"{prefix}.{chatVersion.ToString("x", CultureInfo.InvariantCulture)}.{RemoteContentHash.Format(hash.Value)}";
    }

    public static string Version(ReadOnlySpan<byte> json) => RemoteContentHash.Format(RemoteContentHash.Of(json));

    /// <summary>Fields of a message that can change after it was created and that the phone renders.</summary>
    private static void AddMessage(ref RemoteContentHash hash, ChatMessage message)
    {
        hash.Add(message.Id);
        hash.Add(message.Role);
        hash.Add(message.Content?.Length ?? -1);
        hash.Add(message.IsStreaming);
        hash.Add(message.IsProgress switch { true => 1L, false => 0L, _ => -1L });
        hash.Add(message.ToolStatus);
        hash.Add(message.ToolOutput?.Length ?? -1);
        hash.Add(message.ToolStartedAt?.UtcTicks ?? -1);
        hash.Add(BitConverter.DoubleToInt64Bits(message.ToolDurationMs ?? -1));
        hash.Add((long)message.SteerDelivery);
        hash.Add(message.LinkedChatId);
        hash.Add(message.LinkedChatTitle);
        hash.Add(message.QuestionText?.Length ?? -1);
        hash.Add(message.InputAnswer?.Length ?? -1);
        hash.Add(message.InputCanceled);
        hash.Add(message.Model);
        hash.Add(message.Sources.Count);
        hash.Add(message.Attachments.Count);
        if (message.InteractionReceipt is { } receipt)
        {
            hash.Add(receipt.Status);
            hash.Add(receipt.Error);
        }
        else
        {
            hash.Add(-3L);
        }

        if (message.InteractionStates is { Count: > 0 } states)
        {
            hash.Add(states.Count);
            foreach (var pair in states.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                hash.Add(pair.Key);
                hash.Add(pair.Value);
            }
        }
        else
        {
            hash.Add(-4L);
        }
    }
}
