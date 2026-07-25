using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Tool ("command") durations are a property of the command itself, measured from the SDK event
/// timeline and persisted on the message — not transient UI state. These cover the regression that
/// made the readout unreliable: opening/closing/switching chats rebuilt the transcript, which reset
/// the UI-local stopwatch and either dropped the duration or restarted the clock mid-flight.
/// </summary>
public sealed class ToolDurationTests
{
    [Fact]
    public void MarkToolFinished_MeasuresFromStart()
    {
        var message = NewToolMessage();
        var start = DateTimeOffset.UtcNow;

        message.MarkToolStarted(start);
        Assert.True(message.MarkToolFinished(start.AddSeconds(3)));

        Assert.Equal(3000, message.ToolDurationMs!.Value, 3);
    }

    [Fact]
    public void MarkToolStarted_IsIdempotent()
    {
        var message = NewToolMessage();
        var start = DateTimeOffset.UtcNow;

        message.MarkToolStarted(start);
        message.MarkToolStarted(start.AddSeconds(10));
        message.MarkToolFinished(start.AddSeconds(12));

        Assert.Equal(start, message.ToolStartedAt);
        Assert.Equal(12000, message.ToolDurationMs!.Value, 3);
    }

    [Fact]
    public void MarkToolFinished_DuplicateTerminalEvent_KeepsFirstDuration()
    {
        var message = NewToolMessage();
        var start = DateTimeOffset.UtcNow;

        message.MarkToolStarted(start);
        message.MarkToolFinished(start.AddSeconds(2));
        Assert.False(message.MarkToolFinished(start.AddSeconds(30)));

        Assert.Equal(2000, message.ToolDurationMs!.Value, 3);
    }

    [Fact]
    public void MarkToolFinished_WithoutObservedStart_RecordsNothing()
    {
        var message = NewToolMessage();

        Assert.False(message.MarkToolFinished(DateTimeOffset.UtcNow));
        Assert.Null(message.ToolDurationMs);
    }

    [Fact]
    public void Rebuild_PreservesCompletedToolDuration()
    {
        var toolVm = CreateToolVm("tool-1", "view", "Completed", "{\"path\":\"notes.txt\"}", durationMs: 4200);

        var builder = CreateBuilder();
        var turns = builder.Rebuild([toolVm]);

        Assert.Equal(4200, FindToolCall(turns).DurationMs);
    }

    [Fact]
    public void ToolFinishingAfterChatSwitch_ReportsFullDurationOnNextRebuild()
    {
        // The tool started 30s ago. Switching away and back rebuilds the transcript while it is
        // still running — the regression restarted the clock here, so the final readout only
        // covered the time since the rebuild.
        var toolVm = CreateToolVm("tool-1", "view", "InProgress", "{\"path\":\"notes.txt\"}");
        var start = DateTimeOffset.UtcNow.AddSeconds(-30);
        toolVm.Message.MarkToolStarted(start);

        var rebuiltWhileRunning = FindToolCall(CreateBuilder().Rebuild([toolVm]));
        Assert.Equal(StrataAiToolCallStatus.InProgress, rebuiltWhileRunning.Status);
        Assert.Equal(0, rebuiltWhileRunning.DurationMs);

        toolVm.Message.MarkToolFinished(start.AddSeconds(31));
        toolVm.Message.ToolStatus = "Completed";

        Assert.Equal(31000, FindToolCall(CreateBuilder().Rebuild([toolVm])).DurationMs, 3);
    }

    [Fact]
    public void LiveCompletion_ThenRebuild_ReportsSameDuration()
    {
        var toolVm = CreateToolVm("tool-1", "view", "InProgress", "{\"path\":\"notes.txt\"}");
        var start = DateTimeOffset.UtcNow;
        toolVm.Message.MarkToolStarted(start);

        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);
        builder.ProcessMessageToTranscript(toolVm);

        toolVm.Message.MarkToolFinished(start.AddSeconds(7));
        toolVm.Message.ToolStatus = "Completed";
        toolVm.NotifyToolStatusChanged();
        var liveDuration = FindToolCall(liveTurns).DurationMs;

        var rebuiltDuration = FindToolCall(CreateBuilder().Rebuild([toolVm])).DurationMs;

        Assert.Equal(7000, liveDuration, 3);
        Assert.Equal(liveDuration, rebuiltDuration);
    }

    [Fact]
    public void Rebuild_AnchorsLiveClockToAuthoritativeStart()
    {
        // A tool that is still running when the transcript is rebuilt (chat switch / reopen) must
        // hand the control a fixed start instant. Without it the control starts a fresh local clock
        // and the visible "Running 45s" snaps back to 0s on every switch.
        var toolVm = CreateToolVm("tool-1", "view", "InProgress", "{\"path\":\"notes.txt\"}");
        var start = DateTimeOffset.UtcNow.AddSeconds(-45);
        toolVm.Message.MarkToolStarted(start);

        Assert.Equal(start, FindToolCall(CreateBuilder().Rebuild([toolVm])).RunningSince);
    }

    [Fact]
    public void Rebuild_AnchorsTerminalLiveClockToAuthoritativeStart()
    {
        var toolVm = CreateToolVm("tool-1", "powershell", "InProgress", "{\"command\":\"Start-Sleep 60\"}");
        var start = DateTimeOffset.UtcNow.AddSeconds(-45);
        toolVm.Message.MarkToolStarted(start);

        Assert.Equal(start, FindTerminalPreview(CreateBuilder().Rebuild([toolVm])).RunningSince);
    }

    [Fact]
    public void Rebuild_PreservesCompletedTerminalDuration()
    {
        var toolVm = CreateToolVm("tool-1", "powershell", "Completed", "{\"command\":\"Get-Date\"}", durationMs: 558.8);

        Assert.Equal(558.8, FindTerminalPreview(CreateBuilder().Rebuild([toolVm])).DurationMs, 3);
    }

    [Fact]
    public void ChatFileSerialization_RoundTripsTiming()
    {
        var started = new DateTimeOffset(2026, 7, 25, 15, 16, 47, 123, TimeSpan.FromHours(3));
        var saved = new List<ChatMessage>
        {
            new() { Role = "tool", ToolName = "powershell", ToolStatus = "Completed", ToolStartedAt = started, ToolDurationMs = 2680.4186 },
        };

        var reloaded = RoundTripChatFile(saved).Single();

        Assert.Equal(started, reloaded.ToolStartedAt);
        Assert.Equal(2680.4186, reloaded.ToolDurationMs!.Value, 4);
    }

    [Fact]
    public void ChatFileSerialization_UntimedToolStaysUntimed()
    {
        var reloaded = RoundTripChatFile([new ChatMessage { Role = "tool", ToolName = "view", ToolStatus = "Completed" }]).Single();

        Assert.Null(reloaded.ToolStartedAt);
        Assert.Null(reloaded.ToolDurationMs);
    }

    /// <summary>Serializes through the exact source-generated context <see cref="DataStore.SaveChatAsync"/>
    /// uses, so a timing field that is unserializable there fails here rather than silently persisting
    /// as null and losing every duration on restart.</summary>
    private static List<ChatMessage> RoundTripChatFile(List<ChatMessage> messages)
        => JsonSerializer.Deserialize(
            JsonSerializer.Serialize(messages, AppDataJsonContext.Default.ListChatMessage),
            AppDataJsonContext.Default.ListChatMessage)!;

    private static TerminalPreviewItem FindTerminalPreview(ObservableCollection<TranscriptTurn> turns)
        => turns
            .SelectMany(turn => turn.Items)
            .SelectMany(item => item switch
            {
                ToolGroupItem group => group.ToolCalls.OfType<TerminalPreviewItem>(),
                SingleToolItem single => new[] { single.Inner }.OfType<TerminalPreviewItem>(),
                _ => Enumerable.Empty<TerminalPreviewItem>(),
            })
            .Single();

    private static ToolCallItem FindToolCall(ObservableCollection<TranscriptTurn> turns)
        => turns
            .SelectMany(turn => turn.Items)
            .SelectMany(item => item switch
            {
                ToolGroupItem group => group.ToolCalls.OfType<ToolCallItem>(),
                SingleToolItem single => new[] { single.Inner }.OfType<ToolCallItem>(),
                _ => Enumerable.Empty<ToolCallItem>(),
            })
            .Single();

    private static ChatMessage NewToolMessage()
        => new() { Role = "tool", ToolName = "view", ToolStatus = "InProgress" };

    private static ChatMessageViewModel CreateToolVm(
        string toolCallId,
        string toolName,
        string toolStatus,
        string content,
        double? durationMs = null)
        => new(new ChatMessage
        {
            Role = "tool",
            ToolCallId = toolCallId,
            ToolName = toolName,
            ToolStatus = toolStatus,
            Content = content,
            ToolDurationMs = durationMs,
            Timestamp = DateTimeOffset.Now,
        });

    private static TranscriptBuilder CreateBuilder()
        => new(CreateDataStore(), _ => { }, (_, _) => { }, _ => { }, (_, _) => Task.CompletedTask, () => null);

    private static DataStore CreateDataStore()
    {
#pragma warning disable SYSLIB0050
        var store = (DataStore)FormatterServices.GetUninitializedObject(typeof(DataStore));
#pragma warning restore SYSLIB0050
        var data = new AppData();
        data.Settings.ShowToolCalls = true;
        typeof(DataStore)
            .GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(store, data);
        return store;
    }
}
