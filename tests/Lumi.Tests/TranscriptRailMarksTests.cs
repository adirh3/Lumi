using System;
using System.Globalization;
using System.Linq;
using Lumi.Localization;
using Lumi.Models;
using Lumi.ViewModels;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

public sealed class TranscriptRailMarksTests
{
    [Fact]
    public void Preview_FlowsTheMessageOnOneLineAndCutsItShort()
    {
        Assert.Equal("Fix the bug in the header", TranscriptRailMarks.Preview("  Fix the bug\n\n in \t the header  "));

        var preview = TranscriptRailMarks.Preview(string.Join(' ', Enumerable.Repeat("word", 100)));
        Assert.EndsWith("…", preview);
        Assert.InRange(preview.Length, TranscriptRailMarks.PreviewLength - 4, TranscriptRailMarks.PreviewLength + 1);

        // A cut never splits a character in two.
        var emoji = TranscriptRailMarks.Preview(new string('a', TranscriptRailMarks.PreviewLength - 1) + "🎉🎉");
        Assert.False(char.IsHighSurrogate(emoji[^2]));
    }

    [Fact]
    public void Preview_OfAMessageWithoutText_ReadsAsItsFiles()
    {
        Assert.Equal("report.pdf, chart.png", TranscriptRailMarks.Preview(" \n ", "report.pdf, chart.png"));
        Assert.Equal(Loc.TranscriptRail_Attachments, TranscriptRailMarks.Preview(null));
    }

    [Fact]
    public void FormatSentAt_IsAsBriefAsItsDistanceFromNowAllows()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        var now = Local(2026, 10, 24, 20, 0);
        string Time(DateTimeOffset at) => at.ToString("t", culture);

        var today = Local(2026, 10, 24, 9, 15);
        var yesterday = Local(2026, 10, 23, 23, 30);
        var thisWeek = Local(2026, 10, 20, 8, 5);
        var thisYear = Local(2026, 10, 2, 16, 0);
        var lastYear = Local(2025, 12, 31, 16, 0);

        Assert.Equal(Time(today), TranscriptRailMarks.FormatSentAt(today, now, culture));
        Assert.Equal($"{Loc.ChatGroup_Yesterday} {Time(yesterday)}", TranscriptRailMarks.FormatSentAt(yesterday, now, culture));
        Assert.Equal($"Tuesday {Time(thisWeek)}", TranscriptRailMarks.FormatSentAt(thisWeek, now, culture));
        Assert.Equal($"October 2, {Time(thisYear)}", TranscriptRailMarks.FormatSentAt(thisYear, now, culture));
        Assert.Equal($"12/31/2025, {Time(lastYear)}", TranscriptRailMarks.FormatSentAt(lastYear, now, culture));
        Assert.Null(TranscriptRailMarks.FormatSentAt(null, now, culture));
    }

    [Fact]
    public void StoredInputs_ReadAsTheCardsTheyAreBuiltInto()
    {
        // A question reads as what it asked and how it was answered.
        var answered = TranscriptRailMarks.Describe(Question("Completed", "User answered: Blue"))!;
        Assert.Equal("question:q-1", answered.ItemId);
        Assert.Equal(StrataTranscriptMarkKind.Card, answered.Mark.Kind);
        Assert.Equal(Loc.TranscriptRail_Question, answered.Mark.Label);
        Assert.Equal("Which color?", answered.Mark.Text);
        Assert.Equal(Loc.Get("TranscriptRail_Answered", "Blue"), answered.Mark.Detail(DateTimeOffset.Now));
        Assert.Equal(Loc.TranscriptRail_NotAnswered, TranscriptRailMarks.Describe(Question("Stopped", null))!.Mark.Status);
        Assert.Equal(Loc.TranscriptRail_Waiting, TranscriptRailMarks.Describe(Question("InProgress", null))!.Mark.Status);

        // A form, and a form's receipt, read exactly as the items the transcript builds from them.
        foreach (var form in new[] { Form(answer: null), Form(answer: """{"values":{"dest":"Paris"}}""") })
        {
            var stored = TranscriptRailMarks.Describe(form)!;
            var item = new InputRequestItem(form);
            Assert.Equal(item.StableId, stored.ItemId);
            Assert.Equal(TranscriptRailMarks.Describe(item), stored.Mark);
            Assert.Equal(Loc.TranscriptRail_Form, stored.Mark.Label);
            Assert.Equal("Trip details", stored.Mark.Text);
        }

        var receipt = new ChatMessage
        {
            Role = "user", Content = "Submitted.", Timestamp = DateTimeOffset.Now,
            InteractionReceipt = new InteractionReceipt { Title = "Form submitted", Summary = "Destination: Paris" },
        };
        var storedReceipt = TranscriptRailMarks.Describe(receipt)!;
        var receiptItem = new InteractionReceiptItem(receipt);
        Assert.Equal(receiptItem.StableId, storedReceipt.ItemId);
        Assert.Equal(TranscriptRailMarks.Describe(receiptItem), storedReceipt.Mark);
        Assert.Equal(StrataTranscriptMarkKind.Message, storedReceipt.Mark.Kind);
        Assert.Equal(("Form submitted", "Destination: Paris"), (storedReceipt.Mark.Label, storedReceipt.Mark.Text));

        // Anything else is no input.
        Assert.Null(TranscriptRailMarks.Describe(new ChatMessage { Role = "user", Content = "Hi" }));
        Assert.Null(TranscriptRailMarks.Describe(new ChatMessage { Role = "tool", ToolName = "powershell", Content = "{}" }));

        static ChatMessage Question(string status, string? output) => new()
        {
            Role = "tool", ToolName = "ask_question", ToolStatus = status, ToolOutput = output,
            QuestionId = "q-1", QuestionText = "Which color?", Content = """{"question":"Which color?"}""",
        };

        static ChatMessage Form(string? answer) => new()
        {
            Role = "tool", ToolName = "ask_input", ToolStatus = answer is null ? "InProgress" : "Completed",
            InputKind = "form", InputAnswer = answer,
            InputDefinition = """{"id":"trip","title":"Trip details","fields":[{"id":"dest","label":"Destination","type":"text"}]}""",
        };
    }

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute)
        => new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local));
}
