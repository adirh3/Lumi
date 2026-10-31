using System.Text;
using Lumi.Mobile.Services;
using Xunit;

namespace Lumi.Mobile.Tests;

/// <summary>
/// A long file's text comes in sections that each build on their own, so they must read as the file does:
/// together they are the file, and markdown only ends a section where its blocks already part.
/// </summary>
public sealed class FilePreviewSectionsTests
{
    [Fact]
    public void AReportIsCutWhereItsTextMeetsItsBlocks()
    {
        var report = Report(sections: 6);
        var sections = FilePreviewSections.Split(FilePreviewKind.Markdown, report);

        Assert.True(sections.Count > 6, $"{sections.Count} sections");
        AssertMarkdownRejoins(report, sections);
        AssertMarkdownSeams(sections);
        Assert.All(sections, section => Assert.False(section.JoinsNext, "a report has a block at every seam"));
        // Counting the blank line that parts each section from the next.
        Assert.All(sections.SkipLast(1), section => Assert.True(section.Text.Length + 2 >= FilePreviewSections.MarkdownCharacters));
    }

    [Fact]
    public void LongTextIsCutBetweenPlainParagraphsThatJoin()
    {
        var prose = Prose(sections: 12);
        var sections = FilePreviewSections.Split(FilePreviewKind.Markdown, prose);

        Assert.True(sections.Count > 2, $"{sections.Count} sections");
        AssertMarkdownRejoins(prose, sections);
        AssertMarkdownSeams(sections);
        Assert.All(sections.SkipLast(1), section =>
        {
            Assert.True(section.JoinsNext, "text with no block in it parts only between paragraphs");
            Assert.True(section.Text.Length + 2 >= FilePreviewSections.MarkdownTextCharacters);
        });
        Assert.False(sections[^1].JoinsNext);
    }

    [Fact]
    public void ShortMarkdownStaysWholeAndCodeIsNeverCut()
    {
        var short_ = "# Fold report\n\nAll **ten** postures pass.\n\n| Device | Result |\n| --- | --- |\n| Fold7 | Pass |\n";
        Assert.Equal([(short_.TrimEnd(), false)], FilePreviewSections.Split(FilePreviewKind.Markdown, short_));

        // Markdown opens and closes code at any line that begins with three backticks, whatever follows them.
        var code = new StringBuilder("# Generated\n\n````csharp\n");
        for (var line = 0; line < 60; line++)
            code.Append("var value").Append(line).Append(" = ").Append(line).Append(";\n\n| not | a table |\n\n# not a heading\n\n");
        code.Append("```\n\nThe code above is closed by the three backticks, as markdown reads it.\n\n");
        var text = code + Report(sections: 3);
        var sections = FilePreviewSections.Split(FilePreviewKind.Markdown, text);

        Assert.True(sections.Count > 1);
        AssertMarkdownRejoins(text, sections);
        AssertMarkdownSeams(sections);
        Assert.EndsWith("```", sections[0].Text);
    }

    [Fact]
    public void TextIsOnlyCutBetweenParagraphsThatBeginInOneDirection()
    {
        var hebrew = Hebrew(paragraphs: 40);
        var sections = FilePreviewSections.Split(FilePreviewKind.Markdown, hebrew);
        Assert.True(sections.Count > 1, "Hebrew text parts between its paragraphs too");
        AssertMarkdownRejoins(hebrew, sections);
        Assert.All(sections.SkipLast(1), section =>
        {
            Assert.True(section.JoinsNext);
            Assert.True(StrataTheme.Controls.StrataTextDirectionDetector.IsStrongRtl(section.Text.TrimEnd().Split('\n')[^1][0]));
        });

        // Markdown starts a new text where the direction changes, and reads it past a paragraph's opening
        // link, code or emphasis: none of these seams would read as the file does, so none is cut.
        var mixed = new StringBuilder();
        for (var paragraph = 0; paragraph < 40; paragraph++)
        {
            mixed.Append(paragraph % 2 == 0
                ? "The release candidate was installed on a clean profile and walked end to end.\n\n"
                : "הגרסה הותקנה על פרופיל נקי ונבדקה מקצה לקצה, וכל המסלולים עברו.\n\n");
            mixed.Append(paragraph % 2 == 0
                ? "**Startup** and the welcome tour behaved as designed on every device.\n\n"
                : "`lumi --version` printed the build that the release notes describe.\n\n");
        }

        Assert.Single(FilePreviewSections.Split(FilePreviewKind.Markdown, mixed.ToString()));
    }

    [Fact]
    public void PlainTextIsCutBetweenLines()
    {
        var log = new StringBuilder();
        for (var line = 0; log.Length < FilePreviewSections.TextCharacters * 5; line++)
            log.Append("[").Append(line.ToString("D5")).Append("] INFO Remote: transcript served to the Fold over Tailscale.\r\n");
        var text = log.ToString();

        var sections = FilePreviewSections.Split(FilePreviewKind.Text, text);
        Assert.True(sections.Count >= 4);
        AssertRejoins(text, sections, "\r\n");
        Assert.All(sections, section => Assert.False(section.JoinsNext));
        Assert.All(sections.SkipLast(1), section => Assert.True(section.Text.Length >= FilePreviewSections.TextCharacters - 100));
        Assert.All(sections.Skip(1), section => Assert.StartsWith("[", section.Text));
        Assert.Single(FilePreviewSections.Split(FilePreviewKind.Text, "Build succeeded."));
    }

    [Fact]
    public void CodeIsCutAtBlankLinesOutsideCommentsAndStrings()
    {
        var code = new List<string>();
        for (var line = 0; line < 420; line++)
        {
            switch (line)
            {
                // Comments and strings that span blank lines where a section would otherwise end.
                case 75: code.AddRange(["/*", " A comment that runs on", "", " past a blank line.", "*/"]); break;
                case 160: code.AddRange(["var query = @\"", "SELECT *", "", "FROM chats\";"]); break;
                case 240: code.AddRange(["prompt = \"\"\"", "Summarise the day", "", "in three lines", "\"\"\""]); break;
                case 330: code.AddRange(["<!--", "markup", "", "-->"]); break;
            }

            code.Add(line % 10 == 9 ? "" : $"var value{line} = {line};");
        }

        var text = string.Join("\n", code);
        var sections = FilePreviewSections.Split(FilePreviewKind.Code, text);

        Assert.True(sections.Count >= 4, $"{sections.Count} sections");
        AssertRejoins(text, sections, "\n");
        foreach (var (section, _) in sections)
        {
            Assert.Equal(Count(section, "/*"), Count(section, "*/"));
            Assert.Equal(Count(section, "<!--"), Count(section, "-->"));
            Assert.Equal(0, Count(section, "\"\"\"") % 2);
            Assert.Equal(0, Count(section, "@\"") - Count(section, "\";"));
        }

        Assert.All(sections.SkipLast(1), section =>
            Assert.True(section.Text.Split('\n').Length >= FilePreviewSections.CodeLines));
    }

    private static void AssertRejoins(string text, IReadOnlyList<(string Text, bool JoinsNext)> sections, string lineBreak) =>
        Assert.Equal(text, string.Join(lineBreak, sections.Select(section => section.Text)));

    /// <summary>Markdown sections are the file a blank line apart, each without the blank lines after it.</summary>
    private static void AssertMarkdownRejoins(string text, IReadOnlyList<(string Text, bool JoinsNext)> sections)
    {
        Assert.All(sections, section => Assert.Equal(section.Text.TrimEnd(), section.Text));
        Assert.Equal(text.TrimEnd(), string.Join("\n\n", sections.Select(section => section.Text)));
    }

    /// <summary>
    /// Every seam lies outside code, at the margin and not in a list; one that joins parts two plain
    /// paragraphs, and any other has a table, code, quote or rule on one side.
    /// </summary>
    private static void AssertMarkdownSeams(IReadOnlyList<(string Text, bool JoinsNext)> sections)
    {
        for (var index = 0; index < sections.Count; index++)
        {
            var (text, joinsNext) = sections[index];
            Assert.Equal(0, text.Split('\n').Count(line => line.TrimStart().StartsWith("```")) % 2);
            if (index == sections.Count - 1)
                break;

            var before = text.Split('\n').Last(line => line.Trim().Length > 0).TrimStart();
            var after = sections[index + 1].Text.Split('\n')[0];
            Assert.False(after.Length == 0 || char.IsWhiteSpace(after[0]), $"section {index + 1} starts indented: '{after}'");
            Assert.False(after.StartsWith("- ") || after.StartsWith("* ") || char.IsAsciiDigit(after[0]), $"section {index + 1} starts in a list: '{after}'");
            if (joinsNext)
            {
                Assert.True(IsPlain(before) && IsPlain(after), $"joined seam between '{before}' and '{after}'");
            }
            else
            {
                Assert.True(IsBlock(before) || IsBlock(after), $"seam between '{before}' and '{after}' has no block beside it");
            }
        }

        static bool IsBlock(string line) =>
            line.StartsWith('|') || line.StartsWith("```") || line.StartsWith('>') || line.Trim() == "---";

        static bool IsPlain(string line) =>
            !IsBlock(line) && !line.StartsWith('#') && !line.StartsWith("- ") && !char.IsAsciiDigit(line[0]);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    internal static string Report(int sections)
    {
        var text = new StringBuilder("# Lumi 1.0 launch readiness review\n\n");
        for (var section = 1; section <= sections; section++)
        {
            text.Append("## ").Append(section).Append(". What was checked\n\n");
            text.Append("The release candidate was installed on a clean profile and on one migrated from 0.9, and every critical journey in this area was walked end to end. **Startup** and the *welcome tour* behaved as designed.\n\n");
            text.Append("- Cold start reaches an interactive window in under a second\n- The transcript keeps its place across chat switches\n  - Including long chats\n\n");
            text.Append("| Journey | Windows | macOS |\n| --- | --- | --- |\n| Startup | Pass | Pass |\n| Welcome | Pass | Pass |\n\n");
            text.Append("```csharp\nvar journeys = await catalog.LoadAsync(version);\n\n# not a heading inside the fence\n\n| not | a table |\n```\n\n");
            text.Append("> Decision needed: whether the polish ships in 1.0 or 1.0.1.\n\n---\n\n");
        }

        return text.ToString();
    }

    internal static string Hebrew(int paragraphs)
    {
        var text = new StringBuilder("# סיכום הגרסה\n\n");
        for (var paragraph = 0; paragraph < paragraphs; paragraph++)
            text.Append("הגרסה הותקנה על פרופיל נקי ונבדקה מקצה לקצה, וכל המסלולים החשובים עברו כמתוכנן. חלק ").Append(paragraph).Append(".\n\n");
        return text.ToString();
    }

    internal static string Prose(int sections)
    {
        var text = new StringBuilder("# What changed in the mobile companion\n\n");
        for (var section = 1; section <= sections; section++)
        {
            text.Append("## ").Append(section).Append(". A part of the release\n\n");
            text.Append("Opening a file Lumi made slides its pane in at once and fills it as the bytes arrive, so the chat never waits on the network. **Startup** and the *welcome tour* behaved as designed.\n\n");
            text.Append("On a Fold the chat moves left and the file takes the right, with `the composer` still in reach of the thumb and the list a swipe away.\n\n");
            text.Append("- Cold start reaches an interactive window in under a second\n- Background jobs wake the right chat\n\n");
            text.Append("1. Install the build\n2. Open a report\n\n");
        }

        return text.ToString();
    }
}
