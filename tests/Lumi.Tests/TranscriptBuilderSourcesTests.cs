using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Regression coverage for the "UI freezes for a few seconds when a web-search chat finishes
/// writing" bug. Web sources are attached to the final assistant message on session idle; doing
/// that with a full <c>RebuildTranscript()</c> re-parses the whole mounted tail's markdown (heavy
/// for long answers). The builder must instead refresh the existing assistant item's sources in
/// place — same turn/item instances, so the realized (already-parsed) controls are reused.
/// </summary>
public sealed class TranscriptBuilderSourcesTests
{
    [Fact]
    public void RefreshAssistantSources_UpdatesExistingItemInPlace_WithoutRebuilding()
    {
        var builder = CreateBuilder();
        var assistantVm = CreateAssistantVm("Here is the deal I found.");

        var turns = builder.Rebuild([assistantVm]);

        var turn = Assert.Single(turns);
        var assistantItem = Assert.IsType<AssistantMessageItem>(Assert.Single(turn.Items));
        Assert.False(assistantItem.HasSources);

        // Sources are attached to the persisted model after the turn completes (on session idle).
        assistantVm.Message.Sources.Add(new SearchSource { Title = "Deal A", Url = "https://example.com/a" });
        assistantVm.Message.Sources.Add(new SearchSource { Title = "Deal B", Url = "https://example.com/b" });

        var refreshed = builder.RefreshAssistantSources(assistantVm.Message);

        Assert.True(refreshed);
        // Same collection and same item instances — proves no rebuild (no markdown re-parse) happened.
        Assert.Same(turn, Assert.Single(turns));
        Assert.Same(assistantItem, Assert.Single(turns[0].Items));
        Assert.True(assistantItem.HasSources);
        Assert.Equal(2, assistantItem.Sources.Count);
        Assert.NotNull(assistantItem.DisplaySourcesSection);
    }

    [Fact]
    public void RefreshAssistantSources_AfterStreamingEnds_RevealsSourcesOnLiveItem()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        var streamingAssistant = CreateAssistantVm("Searching for the best price...", isStreaming: true);
        builder.ProcessMessageToTranscript(streamingAssistant);

        // Streaming ends first (ApplyExtras runs with no sources yet), then sources are attached.
        streamingAssistant.Message.IsStreaming = false;
        streamingAssistant.NotifyStreamingEnded();

        var assistantItem = Assert.IsType<AssistantMessageItem>(Assert.Single(Assert.Single(liveTurns).Items));
        Assert.False(assistantItem.HasSources);

        streamingAssistant.Message.Sources.Add(new SearchSource { Title = "Best Buy", Url = "https://example.com/bestbuy" });

        var refreshed = builder.RefreshAssistantSources(streamingAssistant.Message);

        Assert.True(refreshed);
        Assert.Same(assistantItem, Assert.Single(Assert.Single(liveTurns).Items));
        Assert.True(assistantItem.HasSources);
        Assert.Single(assistantItem.Sources);
        Assert.False(string.IsNullOrWhiteSpace(assistantItem.SourcesLabel));
    }

    [Fact]
    public void RefreshAssistantSources_TargetsLatestAssistantMessage()
    {
        var builder = CreateBuilder();
        var firstAssistant = CreateAssistantVm("First answer.");
        var secondAssistant = CreateAssistantVm("Second answer.");

        var turns = builder.Rebuild(
        [
            firstAssistant,
            CreateUserVm("And the laundry one?"),
            secondAssistant,
        ]);

        secondAssistant.Message.Sources.Add(new SearchSource { Title = "Laundry", Url = "https://example.com/laundry" });

        Assert.True(builder.RefreshAssistantSources(secondAssistant.Message));

        var secondItem = turns
            .SelectMany(t => t.Items)
            .OfType<AssistantMessageItem>()
            .Single(item => item.MessageId == secondAssistant.Message.Id);
        var firstItem = turns
            .SelectMany(t => t.Items)
            .OfType<AssistantMessageItem>()
            .Single(item => item.MessageId == firstAssistant.Message.Id);

        Assert.True(secondItem.HasSources);
        Assert.False(firstItem.HasSources);
    }

    [Fact]
    public void RefreshAssistantSources_ReturnsFalse_WhenNoMatchingItem()
    {
        var builder = CreateBuilder();
        builder.Rebuild([CreateAssistantVm("Some answer.")]);

        var orphan = new ChatMessage { Role = "assistant", Content = "Different message." };
        orphan.Sources.Add(new SearchSource { Title = "X", Url = "https://example.com/x" });

        Assert.False(builder.RefreshAssistantSources(orphan));
    }

    private static TranscriptBuilder CreateBuilder()
        => new(CreateDataStore(), _ => { }, (_, _) => { }, (_, _) => Task.CompletedTask, () => null);

    private static ChatMessageViewModel CreateAssistantVm(string content, bool isStreaming = false)
        => new(new ChatMessage
        {
            Role = "assistant",
            Content = content,
            Author = "Lumi",
            IsStreaming = isStreaming,
            Timestamp = DateTimeOffset.Now,
        });

    private static ChatMessageViewModel CreateUserVm(string content)
        => new(new ChatMessage
        {
            Role = "user",
            Content = content,
            Timestamp = DateTimeOffset.Now,
        });

    private static DataStore CreateDataStore()
    {
#pragma warning disable SYSLIB0050
        var store = (DataStore)FormatterServices.GetUninitializedObject(typeof(DataStore));
#pragma warning restore SYSLIB0050
        var data = new AppData();
        typeof(DataStore)
            .GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(store, data);
        return store;
    }
}
