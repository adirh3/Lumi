using Avalonia.Media;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

/// <summary>What the phone's transcript chips say, and the pieces that draw them as the desktop does.</summary>
public sealed class TranscriptGlassTests
{
    [Fact]
    public void ActivityChipsSayWhatWorkIsDoingThenHowItWent()
    {
        var activity = new ActivitySummaryItemViewModel(new RemoteTranscriptItem
        {
            Id = "w", Kind = RemoteProtocol.ItemKinds.Activity, Status = "InProgress", ActionCount = 2,
            Label = "Running device tests...", Icon = "terminal"
        });
        Assert.Equal("Running device tests...", activity.ChipLabel);
        Assert.Equal("2 actions", activity.ChipMeta);
        Assert.Equal("terminal", activity.IconKey);
        Assert.False(activity.HasLineStats);

        activity.Update(new RemoteTranscriptItem
        {
            Id = "w", Kind = RemoteProtocol.ItemKinds.Activity, Status = "Completed", ActionCount = 3,
            DurationMs = 72_000, Icon = "mixed",
            FileChanges =
            [
                new RemoteFileChange { Path = "src/a.cs", FileName = "a.cs", LinesAdded = 10, LinesRemoved = 2 },
                new RemoteFileChange { Path = "src/b.cs", FileName = "b.cs", Operation = "Created", LinesAdded = 4 }
            ]
        });
        Assert.Equal("Worked for 1m 12s", activity.ChipLabel);
        Assert.Equal("3 actions", activity.ChipMeta);
        Assert.Equal("mixed", activity.IconKey);
        Assert.True(activity.HasLineStats);
        Assert.Equal("+14", activity.LinesAddedText);
        Assert.Equal("\u22122", activity.LinesRemovedText);
        // The work sheet keeps its whole sentence.
        Assert.Equal("Worked for 1m 12s · 3 actions", activity.SummaryText);

        activity.Update(new RemoteTranscriptItem
        {
            Id = "w", Kind = RemoteProtocol.ItemKinds.Activity, Status = "Failed", ActionCount = 1, DurationMs = 3_000
        });
        Assert.True(activity.IsFailed);
        Assert.Equal("Finished with an issue for 3s", activity.ChipLabel);
        Assert.Equal("1 action", activity.ChipMeta);

        // Thinking alone has no steps, keeps the desktop's own words, and wears the lightbulb.
        var thought = new ActivitySummaryItemViewModel(new RemoteTranscriptItem
        {
            Id = "t", Kind = RemoteProtocol.ItemKinds.Activity, Status = "Completed", Label = "Thought through the response"
        });
        Assert.Equal("think", thought.IconKey);
        Assert.Equal("Thought through the response", thought.ChipLabel);
        Assert.Null(thought.ChipMeta);
    }

    [Fact]
    public void FinishedWorkFoldsBehindItsDurationStepsAndFailures()
    {
        using var turn = new TranscriptTurnViewModel("t");
        turn.Apply(new RemoteTranscriptTurn
        {
            Id = "t", FinalAnswerId = "a", WorkDurationMs = 3_200,
            Items =
            [
                new RemoteTranscriptItem { Id = "u", Kind = RemoteProtocol.ItemKinds.User, Text = "Go" },
                new RemoteTranscriptItem { Id = "w1", Kind = RemoteProtocol.ItemKinds.Activity, Status = "Completed", ActionCount = 2 },
                new RemoteTranscriptItem { Id = "w2", Kind = RemoteProtocol.ItemKinds.Activity, Status = "Failed", ActionCount = 1 },
                new RemoteTranscriptItem { Id = "a", Kind = RemoteProtocol.ItemKinds.Assistant, Text = "Done" }
            ]
        });

        var work = Assert.IsType<WorkSummaryItemViewModel>(turn.DisplayItems[1]);
        Assert.Equal("Worked for 3s · 3 actions", work.Label);
        Assert.True(work.HasFailures);
        Assert.Empty(work.Items);

        // The summary opens and closes through its own control: a two-way IsExpanded.
        work.IsExpanded = true;
        Assert.Equal(new[] { "w1", "w2" }, work.Items.Select(item => item.Id));
        work.IsExpanded = false;
        Assert.Equal(new[] { "w1", "w2" }, work.Items.Select(item => item.Id));
        Assert.Equal(new[] { "u", "work-w1", "a" }, turn.DisplayItems.Select(item => item.Id));
    }

    [Fact]
    public void AnswersNameTheirModelAndTimeAsTheDesktopDoes()
    {
        var time = new DateTimeOffset(2026, 10, 18, 7, 5, 0, TimeSpan.Zero);
        var answer = new AssistantItemViewModel(
            new RemoteTranscriptItem
            {
                Id = "a", Kind = RemoteProtocol.ItemKinds.Assistant, Text = "Done", Model = "claude-opus-5.5", Timestamp = time
            },
            displayModel: model => model == "claude-opus-5.5" ? "Claude Opus 5.5" : null);
        Assert.Equal($"Claude Opus 5.5 · {time.ToLocalTime():HH:mm}", answer.FooterText);

        var unknown = new AssistantItemViewModel(
            new RemoteTranscriptItem { Id = "b", Kind = RemoteProtocol.ItemKinds.Assistant, Text = "Done", Model = "new-model" },
            displayModel: _ => null);
        Assert.Equal("new-model", unknown.FooterText);
        Assert.Equal("", new AssistantItemViewModel(
            new RemoteTranscriptItem { Id = "c", Kind = RemoteProtocol.ItemKinds.Assistant, Text = "Done" }).FooterText);
    }

    [Fact]
    public void TheAnswerThatClosesATurnCarriesTheFilesItMadeWithoutRebuildingThem()
    {
        var fileId = Guid.NewGuid();
        RemoteTranscriptTurn Turn(string? finalAnswerId) => new()
        {
            Id = "t", FinalAnswerId = finalAnswerId,
            Items =
            [
                new RemoteTranscriptItem { Id = "u", Kind = RemoteProtocol.ItemKinds.User, Text = "Make a report" },
                new RemoteTranscriptItem
                {
                    Id = "file", Kind = RemoteProtocol.ItemKinds.File,
                    Attachments = [new RemoteAttachment { MessageId = fileId, FileName = "report.pdf", Extension = ".pdf" }]
                },
                new RemoteTranscriptItem { Id = "a", Kind = RemoteProtocol.ItemKinds.Assistant, Text = "Your report is ready." }
            ]
        };
        using var turn = new TranscriptTurnViewModel("t");

        // While the turn is under way its files follow the work.
        turn.Apply(Turn(null));
        Assert.Equal(new[] { "u", "a", "file" }, turn.DisplayItems.Select(item => item.Id));
        Assert.Empty(Assert.IsType<AssistantItemViewModel>(turn.DisplayItems[1]).Files);

        // Once an answer closes it, they go with that answer, as on the desktop.
        turn.Apply(Turn("a"));
        Assert.Equal(new[] { "u", "a" }, turn.DisplayItems.Select(item => item.Id));
        var answer = Assert.IsType<AssistantItemViewModel>(turn.DisplayItems[^1]);
        var shown = Assert.Single(answer.Files);
        Assert.Equal(fileId, shown.MessageId);
        Assert.True(answer.HasFiles);

        // A refresh carrying the same files leaves the chips alone.
        turn.Apply(Turn("a"));
        Assert.Same(shown, Assert.Single(answer.Files));
    }

    [Fact]
    public async Task TranscriptGlyphsFollowTheDesktopsIconsAndDrawFilesAsLineIcons()
    {
        Assert.Equal("PNG", FileTypeLabelConverter.Instance.Convert(".png", typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("MD", FileTypeLabelConverter.Instance.Convert("md", typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(FileTypeLabelConverter.Instance.Convert("", typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));

        // Geometry needs the render platform, so the icons are checked inside a headless app.
        using var session = HeadlessMobileSession.Start();
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        await session.Dispatch(() =>
        {
            try
            {
                // Every key the desktop sends has its own line icon; an unknown or missing one is the generic step.
                var keys = new[]
                {
                    "read", "create", "edit", "search", "web", "page", "terminal", "plan", "screenshot", "desktop", "memory",
                    "skill", "project", "agent", "connector", "chat", "chats", "test", "data", "question", "mixed", "think", "deleted"
                };
                Assert.All(keys, key => Assert.True(TranscriptIconConverter.TryResolve(key) is not null, $"'{key}' has no icon of its own."));
                Assert.Null(TranscriptIconConverter.TryResolve("tool"));
                Assert.Null(TranscriptIconConverter.TryResolve(null));
                Assert.NotNull(TranscriptIconConverter.Resolve(null));

                Assert.IsType<DrawingImage>(FileTypeIconConverter.Create("chart.png"));
                Assert.IsType<DrawingImage>(FileTypeIconConverter.Create("notes"));
            }
            catch (Exception ex)
            {
                failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
            return Task.CompletedTask;
        }, CancellationToken.None);
        failure?.Throw();
    }

    private sealed class NullSink : IRemoteCommandSink
    {
        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command) =>
            Task.FromResult(new RemoteCommandResult { Ok = true });

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse { Ok = true });
    }
}
