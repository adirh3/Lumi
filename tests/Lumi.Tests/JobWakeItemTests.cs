using System;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// A job's wake as the transcript shows it, read from the wakes the job service actually writes:
/// why it ran in words, a wake script's message and result, and what opens from it.
/// </summary>
public sealed class JobWakeItemTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 30, 9, 14, 17, TimeSpan.FromHours(2));

    public JobWakeItemTests() => Loc.Load("en");

    public void Dispose() => Loc.Load("en");

    [Fact]
    public void ScheduledRunSaysSoInsteadOfItsTriggerText()
    {
        var wake = Wake("Daily report", "Summarize my day.", $"{BackgroundJobService.ScheduledRunContextPrefix}{At:yyyy-MM-dd HH:mm:ss zzz}.");

        Assert.Equal(JobWakeKind.Scheduled, wake.Kind);
        Assert.True(wake.IsScheduled);
        Assert.Equal("Daily report", wake.JobName);
        Assert.Equal("Scheduled run", wake.ReasonText);
        Assert.False(wake.HasBody);
        Assert.False(wake.IsFailed);
        Assert.Equal("Summarize my day.", wake.Instructions);
    }

    [Fact]
    public void ChatEventNamesItsSourceChatAndOpensIt()
    {
        var sourceId = Guid.NewGuid();
        Guid? opened = null;
        var wake = Wake("Review follow-up", "Review the latest changes.",
            BackgroundJobService.BuildChatEventTriggerContext(new ChatLifecycleEvent(
                sourceId, "Lumi \"Landing\" Page", ChatLifecycleEventTypes.Idle, At, "Finished the hero.")),
            openChat: id => opened = id);

        Assert.Equal(JobWakeKind.ChatEvent, wake.Kind);
        Assert.Equal("Lumi \"Landing\" Page", wake.SourceChatTitle);
        Assert.Equal(sourceId, wake.SourceChatId);
        Assert.Equal("\u201cLumi \"Landing\" Page\u201d finished", wake.ReasonText);
        Assert.DoesNotContain(sourceId.ToString(), wake.ReasonText);
        Assert.False(wake.HasBody);
        Assert.True(wake.CanOpenSourceChat);

        wake.OpenSourceChatCommand.Execute(null);
        Assert.Equal(sourceId, opened);
    }

    [Theory]
    [InlineData(ChatLifecycleEventTypes.TurnStart, "\u201cDocs\u201d started a turn")]
    [InlineData(ChatLifecycleEventTypes.TurnEnd, "\u201cDocs\u201d finished a turn")]
    [InlineData(ChatLifecycleEventTypes.Error, "\u201cDocs\u201d hit an error")]
    [InlineData(ChatLifecycleEventTypes.Aborted, "\u201cDocs\u201d was stopped")]
    public void ChatEventSaysWhatHappenedInTheSourceChat(string eventType, string expected)
    {
        var wake = Wake("Watcher", "Watch.", BackgroundJobService.BuildChatEventTriggerContext(
            new ChatLifecycleEvent(Guid.NewGuid(), "Docs", eventType, At)));

        Assert.Equal(expected, wake.ReasonText);
    }

    [Fact]
    public void WakeScriptLeadsWithItsMessageAndShowsOnlyWhatItPrinted()
    {
        var context = BackgroundJobService.ParseScriptOutput(
            "Checked PR #412.\nLUMI_INVOKE: PR #412 has 2 new review comments.",
            "", 0, "PowerShell", At.AddMinutes(-2).AddSeconds(-14), At).Context;
        var wake = Wake("PR watcher", "Watch PR #412.", context);

        Assert.Equal(JobWakeKind.Script, wake.Kind);
        Assert.False(wake.IsFailed);
        Assert.Equal("Wake script finished", wake.ReasonText);
        Assert.Equal("PR #412 has 2 new review comments.", wake.BodyText);
        Assert.Equal("ran 2m 14s", wake.DurationText);
        Assert.StartsWith("PowerShell · ", wake.RunText);
        Assert.Equal("Checked PR #412.\nLUMI_INVOKE: PR #412 has 2 new review comments.", wake.DisplayOutput);
        // Search still finds everything the wake carries, as stored history's search does.
        Assert.Contains("Runner: PowerShell", wake.SearchText);
    }

    [Fact]
    public void FailedWakeScriptLeadsWithItsError()
    {
        var context = BackgroundJobService.ParseScriptOutput(
            "Building Lumi.sln...", "error CS0246: ChatOverview could not be found", 1,
            "PowerShell", At.AddSeconds(-38), At).Context;
        var wake = Wake("Nightly build", "Build Lumi.", context);

        Assert.True(wake.IsFailed);
        Assert.Equal("Wake script failed with exit code 1", wake.ReasonText);
        Assert.Equal("error CS0246: ChatOverview could not be found", wake.BodyText);
        Assert.Equal("ran 38s", wake.DurationText);
        Assert.Contains("stdout:", wake.DisplayOutput);
        Assert.Contains("stderr:", wake.DisplayOutput);
    }

    [Fact]
    public void FailedWakeScriptKeepsAMessageOfItsOwn()
    {
        var context = BackgroundJobService.ParseScriptOutput(
            "LUMI_INVOKE: The deploy is stuck.", "warning: retrying", 2, "PowerShell", At.AddSeconds(-5), At).Context;

        Assert.Equal("The deploy is stuck.", Wake("Deploy", "Watch.", context).BodyText);
    }

    [Fact]
    public void WakeScriptThatWroteJsonIsDescribedByItsOutput()
    {
        var context = BackgroundJobService.ParseScriptOutput(
            "{\"invoke\":true}", "", 0, "Node.js", At.AddSeconds(-3), At).Context;
        var wake = Wake("Feed", "Watch the feed.", context);

        Assert.Equal(JobWakeKind.Script, wake.Kind);
        Assert.False(wake.HasBody);
        Assert.Equal("{\"invoke\":true}", wake.DisplayOutput);
    }

    [Fact]
    public void ManualChatEventRunAndUnknownWakesStayReadable()
    {
        var manual = Wake("Watcher", "Watch.", $"{BackgroundJobService.ManualChatEventRunContextPrefix}{At:yyyy-MM-dd HH:mm:ss zzz}.");
        Assert.Equal(JobWakeKind.RunManually, manual.Kind);
        Assert.Equal("Run manually", manual.ReasonText);
        Assert.False(manual.HasBody);

        var legacy = Wake("Old job", "Do it.", "Something changed upstream.");
        Assert.Equal(JobWakeKind.Other, legacy.Kind);
        Assert.Equal(Loc.Jobs_ChatWakeEyebrow, legacy.ReasonText);
        Assert.Equal("Something changed upstream.", legacy.BodyText);
    }

    [Fact]
    public void OpenJobNamesTheJobAndIsOfferedOnlyWhenItCanOpen()
    {
        string? opened = null;
        var wake = Wake("Daily report", "Summarize.", BackgroundJobService.ScheduledRunContextPrefix + "now.", openJob: name => opened = name);

        Assert.True(wake.CanOpenJob);
        wake.OpenJobCommand.Execute(null);
        Assert.Equal("Daily report", opened);
        Assert.False(Wake("Daily report", "Summarize.", "x").CanOpenJob);
    }

    [Fact]
    public void HeaderTogglesTheDetailsAndSearchOpensThem()
    {
        var wake = Wake("Daily report", "Summarize.", BackgroundJobService.ScheduledRunContextPrefix + "now.");
        var turn = new TranscriptTurn("wake");
        turn.Items.Add(wake);

        wake.ToggleExpandedCommand.Execute(null);
        Assert.True(wake.IsExpanded);
        wake.ToggleExpandedCommand.Execute(null);
        Assert.False(wake.IsExpanded);

        Assert.True(turn.RevealFoldedItem(wake));
        Assert.True(wake.IsExpanded);
        Assert.False(turn.RevealFoldedItem(wake));
    }

    [Fact]
    public void HebrewTranslatesTheReasonButNotWhatTheJobWrote()
    {
        Loc.Load("he");
        var wake = Wake("My job", "Keep my instructions.", BackgroundJobService.BuildChatEventTriggerContext(
            new ChatLifecycleEvent(Guid.NewGuid(), "Docs", ChatLifecycleEventTypes.Idle, At)));

        Assert.Equal("״Docs״ הסתיים", wake.ReasonText);
        Assert.Equal("My job", wake.JobName);
        Assert.Equal("Keep my instructions.", wake.Instructions);
        Assert.Equal("Docs", wake.SourceChatTitle);
    }

    [Fact]
    public void OpeningTheJobFromAWakePrefersThisChatsJobOfThatName()
    {
        var chat = new Chat { Title = "Pull requests" };
        var other = new Chat { Title = "Other" };
        var elsewhere = new BackgroundJob { Name = "Watcher", ChatId = other.Id, LastRunStartedAt = At };
        var mine = new BackgroundJob { Name = "Watcher", ChatId = chat.Id, LastRunStartedAt = At.AddDays(-1) };
        var store = new DataStore(new AppData { Chats = [chat, other], BackgroundJobs = [elsewhere, mine] });
        using var viewModel = new ChatViewModel(store, TestCopilot.Shared) { CurrentChat = chat };
        var requests = new System.Collections.Generic.List<Guid?>();
        viewModel.OpenAutomationsRequested += requests.Add;

        viewModel.RequestOpenJob("Watcher");
        viewModel.RequestOpenJob("A job since deleted");

        Assert.Equal([mine.Id, null], requests);
    }

    private static JobWakeItem Wake(
        string name,
        string instructions,
        string triggerContext,
        Action<string>? openJob = null,
        Action<Guid>? openChat = null)
    {
        var message = new ChatMessage
        {
            Role = "user",
            Author = $"Lumi Job - {name}",
            Content = ChatViewModel.BuildBackgroundJobPrompt(new BackgroundJob { Name = name, Prompt = instructions }, triggerContext),
            Timestamp = At,
        };
        var source = new ChatMessageViewModel(message);
        Assert.True(JobWakeItem.IsJobWakeMessage(source));
        Assert.True(RemoteJobWake.IsJobWake(message.Author, message.Content));
        return new JobWakeItem(source, showTimestamps: true, openJob, openChat);
    }
}
