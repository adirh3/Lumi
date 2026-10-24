using System;
using System.Globalization;
using System.Linq;
using Lumi.Localization;
using Lumi.ViewModels;
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

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute)
        => new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local));
}
