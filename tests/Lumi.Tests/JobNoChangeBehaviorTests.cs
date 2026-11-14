using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.Services;
using Lumi.Services.Remote;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Change-only jobs: a run that finds nothing new does not show as a reply. It folds into its job's
/// single status line, or leaves no trace, while runs that found something reply as before.
/// </summary>
public sealed class JobNoChangeBehaviorTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 10, 0, 0, TimeSpan.FromHours(2));

    public JobNoChangeBehaviorTests() => Loc.Load("en");

    public void Dispose() => Loc.Load("en");

    [Theory]
    [InlineData(null, BackgroundJobNoChangeBehaviors.Reply)]
    [InlineData("", BackgroundJobNoChangeBehaviors.Reply)]
    [InlineData("Reply", BackgroundJobNoChangeBehaviors.Reply)]
    [InlineData("status_line", BackgroundJobNoChangeBehaviors.StatusLine)]
    [InlineData("Status Line", BackgroundJobNoChangeBehaviors.StatusLine)]
    [InlineData("status-line", BackgroundJobNoChangeBehaviors.StatusLine)]
    [InlineData("silent", BackgroundJobNoChangeBehaviors.Silent)]
    [InlineData("quiet", BackgroundJobNoChangeBehaviors.Silent)]
    public void NoChangeBehaviorAcceptsItsNamesAndCommonSpellings(string? value, string expected)
    {
        Assert.True(BackgroundJobNoChangeBehaviors.TryNormalize(value, out var normalized));
        Assert.Equal(expected, normalized);
        Assert.Equal(expected != BackgroundJobNoChangeBehaviors.Reply, BackgroundJobNoChangeBehaviors.ReportsOnlyChanges(value));
    }

    [Fact]
    public void UnknownNoChangeBehaviorIsRejectedButReadsAsReply()
    {
        Assert.False(BackgroundJobNoChangeBehaviors.TryNormalize("sometimes", out _));
        Assert.Equal(BackgroundJobNoChangeBehaviors.Reply, BackgroundJobNoChangeBehaviors.Normalize("sometimes"));
        Assert.False(new BackgroundJob { NoChangeBehavior = "sometimes" }.ReportsOnlyChanges);
    }

    [Fact]
    public void StatusIsOneTrimmedLineOfBoundedLength()
    {
        Assert.Equal("Still pending approval", BackgroundJobRunPresentation.NormalizeStatus("  Still\n pending\tapproval "));
        Assert.Equal("", BackgroundJobRunPresentation.NormalizeStatus("   "));
        var longStatus = BackgroundJobRunPresentation.NormalizeStatus(new string('x', 500));
        Assert.Equal(BackgroundJobRunPresentation.MaxStatusLength, longStatus.Length);
        Assert.EndsWith("…", longStatus);
    }

    [Fact]
    public void OnlyStatusLineJobsWithAQuietRunHaveAStatusLine()
    {
        var statusRun = Guid.NewGuid();
        var silentRun = Guid.NewGuid();
        var statusJob = new BackgroundJob { NoChangeBehavior = BackgroundJobNoChangeBehaviors.StatusLine, LastQuietRunMessageId = statusRun };
        var rows = BackgroundJobRunPresentation.StatusLineRuns(
        [
            statusJob,
            new BackgroundJob { NoChangeBehavior = BackgroundJobNoChangeBehaviors.Silent, LastQuietRunMessageId = silentRun },
            new BackgroundJob { NoChangeBehavior = BackgroundJobNoChangeBehaviors.StatusLine },
        ]);

        Assert.Same(statusJob, Assert.Single(rows).Value);
        Assert.Equal(statusRun, rows.Keys.Single());
    }

    [Fact]
    public void QuietRunMaskCoversTheWakeAndEverythingUpToTheNextUserMessage()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = "Hi" },
            new() { Role = "assistant", Content = "Hello" },
            new() { Role = "user", Content = "wake", JobRunQuiet = true },
            new() { Role = "tool", ToolName = "web_search" },
            new() { Role = "assistant", Content = "Nothing new." },
            new() { Role = "user", Content = "Thanks" },
        };

        Assert.Equal([false, false, true, true, true, false], BackgroundJobRunPresentation.QuietRunMask(messages));
        Assert.True(BackgroundJobRunPresentation.HasQuietRun(messages));
    }

    [Fact]
    public void WakePromptAsksChangeOnlyJobsToReportTheirOutcome()
    {
        var reply = ChatViewModel.BuildBackgroundJobPrompt(
            new BackgroundJob { Name = "Digest", Prompt = "Summarize." }, "Scheduled run.");
        Assert.DoesNotContain("report_job_outcome", reply);

        var watcher = ChatViewModel.BuildBackgroundJobPrompt(new BackgroundJob
        {
            Name = "Approval",
            Prompt = "Check the request.",
            NoChangeBehavior = BackgroundJobNoChangeBehaviors.StatusLine,
            LastReportedStatus = "Pending with 2 approvers",
        }, "Scheduled run.");
        Assert.Contains("report_job_outcome", watcher);
        Assert.Contains("Last reported status: Pending with 2 approvers", watcher);
        Assert.True(RemoteJobWake.IsJobWake("Lumi Job - Approval", watcher));
    }

    [Fact]
    public void ManageJobsCreatesUpdatesAndDescribesTheSetting()
    {
        var chat = new Chat { Title = "Approvals" };
        var data = new AppData { Chats = [chat] };
        var manager = new LumiFeatureManager(new DataStore(data));

        var created = manager.ManageJobs(
            "create",
            name: "Approval watch",
            prompt: "Check the request.",
            triggerType: BackgroundJobTriggerTypes.Time,
            scheduleType: BackgroundJobScheduleTypes.Interval,
            intervalMinutes: 120,
            defaultChatId: chat.Id,
            noChangeBehavior: "status line");
        Assert.True(created.DataChanged);
        var job = Assert.Single(data.BackgroundJobs);
        Assert.Equal(BackgroundJobNoChangeBehaviors.StatusLine, job.NoChangeBehavior);

        Assert.True(manager.ManageJobs("update", identifier: job.Id.ToString(), noChangeBehavior: "silent").DataChanged);
        Assert.Equal(BackgroundJobNoChangeBehaviors.Silent, job.NoChangeBehavior);

        var rejected = manager.ManageJobs("update", identifier: job.Id.ToString(), noChangeBehavior: "sometimes");
        Assert.False(rejected.DataChanged);
        Assert.Equal(BackgroundJobNoChangeBehaviors.Silent, job.NoChangeBehavior);

        Assert.Contains(BackgroundJobNoChangeBehaviors.Silent, manager.ManageJobs("list").Message);
    }

    [Fact]
    public void ManageJobsDefaultsToReplyingEveryRun()
    {
        var chat = new Chat { Title = "Plans" };
        var data = new AppData { Chats = [chat] };
        new LumiFeatureManager(new DataStore(data)).ManageJobs(
            "create",
            name: "Daily plan",
            prompt: "Plan my day.",
            triggerType: BackgroundJobTriggerTypes.Time,
            scheduleType: BackgroundJobScheduleTypes.Interval,
            intervalMinutes: 60,
            defaultChatId: chat.Id);

        Assert.Equal(BackgroundJobNoChangeBehaviors.Reply, Assert.Single(data.BackgroundJobs).NoChangeBehavior);
    }

    [Fact]
    public void JobsContextSaysWhichJobsPostOnlyChanges()
    {
        var context = SystemPromptBuilder.BuildBackgroundJobsContext(
        [
            new BackgroundJob { Name = "Digest", IsEnabled = true },
            new BackgroundJob { Name = "Approval watch", IsEnabled = true, NoChangeBehavior = BackgroundJobNoChangeBehaviors.StatusLine },
        ]);

        var lines = context.Split('\n');
        Assert.DoesNotContain("changes only", lines.Single(line => line.Contains("Digest")));
        Assert.Contains("posts: changes only (status_line)", lines.Single(line => line.Contains("Approval watch")));
    }

    [Fact]
    public void ChatPreviewSkipsQuietRuns()
    {
        var job = new BackgroundJob { Name = "Approval", Prompt = "Check." };
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = "Watch my request." },
            new() { Role = "assistant", Content = "I'll keep an eye on it." },
        };
        messages.AddRange(QuietRun(job, "Still pending."));

        Assert.Equal("I'll keep an eye on it.", ChatPreviewHelper.FromMessages(messages));
    }

    [Fact]
    public void TranscriptHidesQuietRunsAndKeepsOneStatusLinePerJob()
    {
        var bami = new BackgroundJob { Name = "BAMI", Prompt = "Check BAMI.", NoChangeBehavior = BackgroundJobNoChangeBehaviors.StatusLine };
        var lionrock = new BackgroundJob { Name = "Lionrock", Prompt = "Check the thread.", NoChangeBehavior = BackgroundJobNoChangeBehaviors.StatusLine };
        var askIam = new BackgroundJob { Name = "AskIAM", Prompt = "Check the exception.", NoChangeBehavior = BackgroundJobNoChangeBehaviors.Silent };

        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = "Watch these for me.", Timestamp = At },
            new() { Role = "assistant", Content = "Watching all three.", Timestamp = At },
        };
        var bamiOld = QuietRun(bami, "Pending, 0 of 2 approvals.");
        var lionrockLatest = QuietRun(lionrock, "No new replies.");
        var bamiLatest = QuietRun(bami, "Pending, 1 of 2 approvals.");
        var askIamLatest = QuietRun(askIam, "Still waiting.");
        messages.AddRange(bamiOld);
        messages.AddRange(lionrockLatest);
        messages.AddRange(bamiLatest);
        messages.AddRange(askIamLatest);

        bami.LastQuietRunMessageId = bamiLatest[0].Id;
        bami.LastQuietRunAt = At.AddHours(4);
        bami.QuietRunCount = 2;
        lionrock.LastQuietRunMessageId = lionrockLatest[0].Id;
        lionrock.LastQuietRunAt = At.AddHours(2);
        lionrock.QuietRunCount = 1;
        askIam.LastQuietRunMessageId = askIamLatest[0].Id;

        var store = new DataStore(new AppData { BackgroundJobs = [bami, lionrock, askIam] });
        var turns = CreateBuilder(store).Rebuild(messages.Select(message => new ChatMessageViewModel(message)));

        var items = turns.SelectMany(turn => turn.Items).ToList();
        Assert.DoesNotContain(items, item => item is AssistantMessageItem { } assistant
            && assistant.Content.Contains("approvals", StringComparison.Ordinal));
        var statusLines = items.OfType<JobWakeItem>().ToList();
        Assert.All(statusLines, line => Assert.True(line.IsStatusLine));
        // In chat order: Lionrock's latest run, then BAMI's latest; BAMI's older run and the silent job show nothing.
        Assert.Equal(["Lionrock", "BAMI"], statusLines.Select(line => line.JobName));
        Assert.Equal("No new replies.", statusLines[0].BodyText);
        Assert.Equal("Pending, 1 of 2 approvals.", statusLines[1].BodyText);
        Assert.False(statusLines[0].HasDuration);
        Assert.True(statusLines[1].HasDuration);
        Assert.Contains("Watching all three.", items.OfType<AssistantMessageItem>().Select(item => item.Content));
    }

    [Fact]
    public void StatusLineEndsTheTranscriptWithoutAModelLabel()
    {
        var job = new BackgroundJob { Name = "BAMI", Prompt = "Check BAMI.", NoChangeBehavior = BackgroundJobNoChangeBehaviors.StatusLine };
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = "Watch BAMI.", Timestamp = At },
            new() { Role = "assistant", Content = "Watching.", Model = "gpt-6-luna", Timestamp = At },
        };
        var run = QuietRun(job, "Pending, 0 of 2.");
        foreach (var message in run.Where(message => message.Role == "assistant"))
            message.Model = "gpt-6-luna";
        messages.AddRange(run);
        job.LastQuietRunMessageId = run[0].Id;
        job.QuietRunCount = 1;
        var store = new DataStore(new AppData { BackgroundJobs = [job] });

        var turns = CreateBuilder(store).Rebuild(messages.Select(message => new ChatMessageViewModel(message)));

        var statusTurn = Assert.Single(turns, turn => turn.Items.OfType<JobWakeItem>().Any());
        Assert.DoesNotContain(statusTurn.Items, item => item is TurnModelItem { IsItemVisible: true });
        Assert.DoesNotContain(turns.SelectMany(turn => turn.Items), item => item is TurnModelItem { IsItemVisible: true });
    }

    [Fact]
    public void QuietRunsCompletionAddsNoModelLabelUnderItsStatusLine()
    {
        var job = new BackgroundJob { Name = "BAMI", Prompt = "Check BAMI.", NoChangeBehavior = BackgroundJobNoChangeBehaviors.StatusLine };
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = "Watch BAMI.", Timestamp = At },
            new() { Role = "assistant", Content = "Watching.", Model = "gpt-6-luna", Timestamp = At },
        };
        var run = QuietRun(job, "Pending, 0 of 2.");
        messages.AddRange(run);
        job.LastQuietRunMessageId = run[0].Id;
        var store = new DataStore(new AppData { BackgroundJobs = [job] });
        var builder = CreateBuilder(store);
        var turns = builder.Rebuild(messages.Select(message => new ChatMessageViewModel(message)));

        // The run's turn end arrives after its transcript already folded it into the status line.
        builder.AppendModelLabel("gpt-6-luna");

        Assert.DoesNotContain(turns.SelectMany(turn => turn.Items), item => item is TurnModelItem { IsItemVisible: true });
    }

    [Fact]
    public void RunThatFoundSomethingStillReplies()
    {
        var job = new BackgroundJob { Name = "BAMI", Prompt = "Check BAMI.", NoChangeBehavior = BackgroundJobNoChangeBehaviors.StatusLine };
        var wake = WakeMessage(job);
        var answer = new ChatMessage { Role = "assistant", Content = "Approved! 🎉", Timestamp = At };
        var store = new DataStore(new AppData { BackgroundJobs = [job] });

        var items = CreateBuilder(store).Rebuild([new ChatMessageViewModel(wake), new ChatMessageViewModel(answer)])
            .SelectMany(turn => turn.Items)
            .ToList();

        Assert.False(Assert.Single(items.OfType<JobWakeItem>()).IsStatusLine);
        Assert.Contains("Approved! 🎉", items.OfType<AssistantMessageItem>().Select(item => item.Content));
    }

    [Fact]
    public void StatusLineWakeShowsWhenItLastCheckedAndHowOften()
    {
        var job = new BackgroundJob { Name = "BAMI", Prompt = "Check BAMI." };
        var source = new ChatMessageViewModel(WakeMessage(job));
        var line = new JobWakeItem(source, showTimestamps: true,
            statusLine: new JobWakeStatusLine("Pending, 1 of 2 approvals.", DateTimeOffset.Now, 3));

        Assert.True(line.IsStatusLine);
        Assert.False(line.IsFailed);
        Assert.Equal("BAMI", line.JobName);
        Assert.Equal("Pending, 1 of 2 approvals.", line.BodyText);
        Assert.StartsWith("Checked", line.ReasonText);
        Assert.Contains("3", line.DurationText);
    }

    [Fact]
    public void StatusLineWithoutAReportedStatusSaysNothingChanged()
    {
        var job = new BackgroundJob { Name = "BAMI", Prompt = "Check BAMI." };
        var source = new ChatMessageViewModel(WakeMessage(job));
        var line = new JobWakeItem(source, showTimestamps: true,
            statusLine: new JobWakeStatusLine("", DateTimeOffset.Now, 1));

        Assert.True(line.IsStatusLine);
        Assert.Equal("No change since the last update.", line.BodyText);
        Assert.Equal("", line.DurationText);
    }

    [Fact]
    public void RemoteTranscriptHidesQuietRunsAndCarriesTheStatusLine()
    {
        var job = new BackgroundJob { Name = "BAMI", Prompt = "Check BAMI.", NoChangeBehavior = BackgroundJobNoChangeBehaviors.StatusLine };
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = "Watch BAMI.", Timestamp = At },
            new() { Role = "assistant", Content = "Watching.", Timestamp = At },
        };
        var older = QuietRun(job, "Pending, 0 of 2.");
        var latest = QuietRun(job, "Pending, 1 of 2.");
        messages.AddRange(older);
        messages.AddRange(latest);
        job.LastQuietRunMessageId = latest[0].Id;
        job.LastQuietRunAt = At.AddHours(4);

        var transcript = RemoteProjector.BuildTranscript(
            new Chat(),
            new TranscriptMessageWindow(messages, 0, messages.Count, messages.Count),
            new RemoteChatStatus(),
            showReasoning: true,
            showToolCalls: true,
            revision: 1,
            jobStatusLines: BackgroundJobRunPresentation.StatusLineRuns([job]));

        var items = transcript.Turns.SelectMany(turn => turn.Items).ToList();
        Assert.DoesNotContain(items, item => item.Text?.Contains("Pending, 0 of 2.") == true);
        var row = Assert.Single(items, item => item.JobRunStatus is not null);
        Assert.Equal("Pending, 1 of 2.", row.JobRunStatus);
        Assert.Equal(At.AddHours(4), row.Timestamp);
        Assert.Equal(2, transcript.Turns.Count);

        var withoutLines = RemoteProjector.BuildTranscript(new Chat(), messages, new RemoteChatStatus(),
            showReasoning: true, showToolCalls: true, revision: 1);
        Assert.Single(withoutLines.Turns);
    }

    private static ChatMessage WakeMessage(BackgroundJob job) => new()
    {
        Role = "user",
        Author = $"Lumi Job - {job.Name}",
        BackgroundJobId = job.Id,
        Content = ChatViewModel.BuildBackgroundJobPrompt(job, BackgroundJobService.ScheduledRunContextPrefix + "now."),
        Timestamp = At,
    };

    // A run that found nothing new, as its completion leaves it: the wake marked quiet, then its work.
    private static List<ChatMessage> QuietRun(BackgroundJob job, string status)
    {
        var wake = WakeMessage(job);
        wake.JobRunQuiet = true;
        wake.JobRunStatus = status;
        return
        [
            wake,
            new ChatMessage { Role = "tool", ToolName = "report_job_outcome", ToolStatus = "Completed", Content = "{}", Timestamp = At },
            new ChatMessage { Role = "assistant", Content = status, Timestamp = At },
        ];
    }

    private static TranscriptBuilder CreateBuilder(DataStore store)
        => new(store, _ => { }, (_, _) => { }, _ => { }, (_, _) => Task.CompletedTask, () => null);
}
